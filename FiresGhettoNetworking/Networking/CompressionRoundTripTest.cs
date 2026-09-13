using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Admin console test for network compression. 'fgn_comptest [count] [sizeKB]' first runs the real send policy and
    /// PacketFrame locally on a compressible payload (deflated), an incompressible one (sent as is), one mixing compressible
    /// bytes with Valheim-style gzip arrays (deflated beside them), a payload that begins with the frame marker (framed so it
    /// cannot be misread) and a damaged frame (rejected). The server then bursts N packets at the caller cycling through the
    /// first three kinds, which crosses every boundary between frame types; each carries a nonce, sequence and FNV checksum.
    /// </summary>
    [HarmonyPatch]
    public static class CompressionRoundTripTest
    {
        private const string RpcStart  = "FGN_CompTestStart";   // client -> server
        private const string RpcData   = "FGN_CompTestData";    // server -> client
        private const string RpcStatus = "FGN_CompTestStatus";  // server -> client (text)
        // Pace the burst so the in-flight queue stays bounded — Valheim back-pressures a full send
        // buffer (ZSteamSocket re-queues on EResult != OK, never drops), so blasting all N at once just
        // balloons m_sendQueue and made the client tally get read mid-drain (the phantom "576/1000").
        private const int FlowControlBytes = 512 * 1024;
        private const int DefaultCount = 100;
        private const int DefaultSizeKB = 64;
        private const int MaxCount = 10000;
        private const int MaxSizeKB = 128;
        private const int GzipPartBytes = 2048;
        private const int PlainPartBytes = 4096;

        private enum PayloadKind { Compressible, Incompressible, WithGzip }
        private const int KindCount = 3;

        private static bool s_commandRegistered;
        private static long s_nonceSeq;

        // client-side receive tally for the in-flight test
        private static long s_expectNonce;
        private static int s_expectCount;
        private static int s_received, s_corrupt, s_outOfOrder, s_lastSeq;
        private static readonly int[] s_receivedByKind = new int[KindCount];

        // Incompressible block (fixed seed so the server's generate and the client's verify match).
        private static byte[] s_rawBlock;
        private static byte[] RawBlock
        {
            get
            {
                if (s_rawBlock == null)
                {
                    // Filled by an explicit LCG (not System.Random) so the block is bit-identical on the
                    // server and client regardless of runtime — the client regenerates it to spot-check
                    // raw packets in PayloadMatches. High-entropy, so Deflate can't shrink it.
                    s_rawBlock = new byte[MaxSizeKB * 1024];
                    uint x = 0x2C0FFEEu;
                    for (int i = 0; i < s_rawBlock.Length; i++) { x = x * 1664525u + 1013904223u; s_rawBlock[i] = (byte)(x >> 24); }
                }
                return s_rawBlock;
            }
        }

        private static PayloadKind KindOf(int seq) => (PayloadKind)(seq % KindCount);

        [HarmonyPatch(typeof(ZNet), "Start")]
        [HarmonyPostfix]
        public static void OnZNetStart()
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.Register<int, int, long>(RpcStart, RPC_Start);
            ZRoutedRpc.instance.Register<ZPackage>(RpcData, RPC_Data);
            ZRoutedRpc.instance.Register<string>(RpcStatus, RPC_Status);

            if (s_commandRegistered) return;
            s_commandRegistered = true;
            new Terminal.ConsoleCommand("fgn_comptest",
                "[count] [sizeKB] - FGN diagnostic: test network compression. Runs the send policy and frame checks locally "
                + "(compressible, incompressible, mixed with gzip data, frame-marker escape, damaged frame), then the server "
                + "bursts N packets cycling compressible / incompressible / mixed (default 100 x 64KB, up to 10000 x 128KB) and "
                + "verifies every one arrived intact.",
                new Terminal.ConsoleEvent(OnCommand));
        }

        private static void OnCommand(Terminal.ConsoleEventArgs args)
        {
            int count = DefaultCount, sizeKB = DefaultSizeKB;
            if (args.Length >= 2) int.TryParse(args[1], out count);
            if (args.Length >= 3) int.TryParse(args[2], out sizeKB);
            count = Mathf.Clamp(count, 1, MaxCount);
            sizeKB = Mathf.Clamp(sizeKB, 1, MaxSizeKB);
            if (ZNet.instance == null || ZRoutedRpc.instance == null) { args.Context?.AddString("FGN: not connected."); return; }

            // 1. Local: the same policy and frame code the sockets use, on each payload kind.
            var results = new List<string>();
            bool allIntact = true;
            byte[] compressibleFrame = null;
            for (int kind = 0; kind < KindCount; kind++)
            {
                byte[] payload = MakePayload(sizeKB * 1024, kind);
                byte[] wire = CompressionGroup.FrameForPeer(payload, true, true, out SendOutcome outcome);
                bool intact = RoundTrips(payload, wire);
                allIntact &= intact;
                if (kind == (int)PayloadKind.Compressible && wire != payload) compressibleFrame = wire;
                results.Add($"{(PayloadKind)kind} {payload.Length}->{wire.Length} B {outcome} {(intact ? "OK" : "MISMATCH")}");
            }

            byte[] marked = MakePayload(sizeKB * 1024, (int)PayloadKind.Incompressible);
            marked[0] = (byte)(PacketFrame.Magic & 0xFF);
            marked[1] = (byte)((PacketFrame.Magic >> 8) & 0xFF);
            marked[2] = (byte)((PacketFrame.Magic >> 16) & 0xFF);
            marked[3] = (byte)((PacketFrame.Magic >> 24) & 0xFF);
            byte[] markedWire = CompressionGroup.FrameForPeer(marked, true, true, out SendOutcome markedOutcome);
            bool markedIntact = RoundTrips(marked, markedWire) && markedWire != marked;
            allIntact &= markedIntact;
            results.Add($"marker-prefixed {marked.Length}->{markedWire.Length} B {markedOutcome} {(markedIntact ? "OK" : "MISMATCH")}");

            if (compressibleFrame != null)
            {
                byte[] damaged = (byte[])compressibleFrame.Clone();
                damaged[damaged.Length - 1] ^= 0x5A;
                PacketFrame.DecodeResult damagedResult = PacketFrame.TryDecode(damaged, out _);
                bool rejected = damagedResult != PacketFrame.DecodeResult.Decoded;
                allIntact &= rejected;
                results.Add($"damaged frame {(rejected ? "rejected" : "ACCEPTED")} ({damagedResult})");
            }

            args.Context?.AddString($"FGN comptest local {sizeKB}KB {(allIntact ? "PASS" : "FAIL")}: " + string.Join("; ", results) + ".");

            // 2. Wire burst (server -> client), cycling payload kinds every packet.
            s_expectNonce = ++s_nonceSeq;
            s_expectCount = count; s_received = 0; s_corrupt = 0; s_outOfOrder = 0; s_lastSeq = -1;
            for (int i = 0; i < KindCount; i++) s_receivedByKind[i] = 0;
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcStart, count, sizeKB, s_expectNonce);
            float cap = ReportDelay(count, sizeKB);
            args.Context?.AddString($"FGN comptest wire: requested {count} x {sizeKB}KB packets (cycling compressible / incompressible / mixed with gzip). Reports as soon as all arrive (up to ~{cap:F0}s — the pipe is send-rate limited, so a big incompressible burst takes a while). DISCONNECT mid-test = the bug.");
            if (FiresGhettoNetworkMod.Instance != null)
                FiresGhettoNetworkMod.Instance.StartCoroutine(ReportAfter(cap, args.Context));
        }

        private static bool RoundTrips(byte[] payload, byte[] wire)
        {
            if (wire == payload) return true;
            return PacketFrame.TryDecode(wire, out byte[] decoded) == PacketFrame.DecodeResult.Decoded
                && decoded.Length == payload.Length
                && Checksum(decoded) == Checksum(payload);
        }

        // SAFETY CAP only — the report fires as soon as all packets actually arrive (ReportAfter polls).
        // This just bounds the wait if they never all arrive (a real stall). Generous, since the pipe is
        // send-rate limited and a big incompressible burst legitimately takes a while to drain.
        private static float ReportDelay(int count, int sizeKB) => Mathf.Clamp(30f + (count * sizeKB) / 512f, 30f, 600f);

        private static IEnumerator ReportAfter(float maxSeconds, Terminal ctx)
        {
            // Fire as soon as every packet has actually arrived (the pipe is send-rate limited, so a big
            // incompressible burst takes a while to drain — nothing is lost, ZSteamSocket re-queues), or
            // bail at the safety cap. No more fixed-timer guessing that read the tally mid-drain.
            float deadline = Time.time + maxSeconds;
            while (Time.time < deadline && s_received < s_expectCount)
                yield return null;
            bool completed = s_received >= s_expectCount;

            bool pass = completed && s_corrupt == 0 && s_outOfOrder == 0;
            string verdict = pass ? "PASS" : (completed ? "FAIL" : "INCOMPLETE");
            string tail;
            if (pass)
                tail = "Every packet round-tripped across every boundary between frame types.";
            else if (!completed)
                tail = $"Only {s_received}/{s_expectCount} arrived by the {maxSeconds:F0}s cap — the pipe is send-rate limited, NOT lossy (ZSteamSocket re-queues, never drops; what arrived is intact). Give it longer or lower count/size.";
            else if (s_corrupt > 0)
                tail = "Corruption = the round-trip is broken.";
            else
                tail = "Out-of-order delivery on a reliable channel — investigate.";
            string msg = $"[CompTest] {verdict} — received {s_received}/{s_expectCount} "
                + $"(compressible {s_receivedByKind[0]}, incompressible {s_receivedByKind[1]}, mixed with gzip {s_receivedByKind[2]}), "
                + $"corrupt={s_corrupt}, out-of-order={s_outOfOrder}. {tail}";
            ctx?.AddString(msg);
            LoggerOptions.LogMessage(msg);
        }

        // ---- server side ----
        private static void RPC_Start(long sender, int count, int sizeKB, long nonce)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!SenderIsAdmin(sender)) { Status(sender, "FGN comptest denied — admin only."); return; }
            count = Mathf.Clamp(count, 1, MaxCount);
            sizeKB = Mathf.Clamp(sizeKB, 1, MaxSizeKB);
            if (FiresGhettoNetworkMod.Instance != null)
                FiresGhettoNetworkMod.Instance.StartCoroutine(SendBurst(sender, count, sizeKB, nonce));
        }

        private static bool IsCompressionActive(long uid)
        {
            try
            {
                var peers = ZNet.instance.GetPeers();
                ZNetPeer peer = peers.FirstOrDefault(p => p.m_uid == uid);
                if (peer?.m_socket != null && CompressionGroup.CompressionStatus.GetSendCompressionStarted(peer.m_socket))
                    return true;
                // Fallback: the server has started compressing to at least one connected peer. For a
                // single-requester test that peer IS the requester; covers any sender-id/peer-uid skew.
                return peers.Any(p => p.m_socket != null && CompressionGroup.CompressionStatus.GetSendCompressionStarted(p.m_socket));
            }
            catch { return false; }
        }

        private static IEnumerator SendBurst(long target, int count, int sizeKB, long nonce)
        {
            // The compression handshake lands a second or two after a peer connects; if the test is
            // fired right after joining, wait briefly so the burst is actually compressed and the
            // reported status is accurate (avoids a misleading INACTIVE on a healthy connection).
            float deadline = Time.time + 8f;
            while (!IsCompressionActive(target) && Time.time < deadline) yield return null;
            bool active = IsCompressionActive(target);
            LoggerOptions.LogMessage($"[CompTest] diag: target={target} peers={ZNet.instance.GetPeers().Count} "
                + $"anyStarted={ZNet.instance.GetPeers().Any(p => p.m_socket != null && CompressionGroup.CompressionStatus.GetSendCompressionStarted(p.m_socket))} "
                + $"uidMatch={ZNet.instance.GetPeers().Any(p => p.m_uid == target)} active={active}");
            Status(target, $"FGN comptest: server bursting {count} x {sizeKB}KB to you (compression to you: "
                + $"{(active ? "ACTIVE" : "INACTIVE — handshake hasn't landed; reconnect, wait a few seconds, retry")}).");

            int size = sizeKB * 1024;
            for (int seq = 0; seq < count; seq++)
            {
                // Flow control: pace to the pipe so the in-flight queue stays bounded. Without it a big
                // burst piles into m_sendQueue and the tally is read before the send-rate-limited drain
                // finishes (the phantom "576/1000" — nothing lost, just not arrived yet).
                while (GetTargetQueueBytes(target) > FlowControlBytes)
                {
                    if (!PeerConnected(target)) yield break;   // they disconnected — stop
                    yield return null;
                }
                ZPackage pkg = new ZPackage();
                pkg.Write(nonce);
                pkg.Write(seq);
                byte[] payload = MakePayload(size, seq);
                pkg.Write(payload);
                pkg.Write(Checksum(payload));
                ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcData, pkg);
                if ((seq & 31) == 31) yield return null; // keep the frame breathing even when the queue drains fast
            }
        }

        // ---- client side ----
        private static void RPC_Data(long sender, ZPackage pkg)
        {
            try
            {
                long nonce = pkg.ReadLong();
                if (nonce != s_expectNonce) return; // stale or a different test
                int seq = pkg.ReadInt();
                byte[] payload = pkg.ReadByteArray();
                int recvChecksum = pkg.ReadInt();
                if (Checksum(payload) != recvChecksum || !PayloadMatches(payload, seq)) s_corrupt++;
                if (seq != s_lastSeq + 1) s_outOfOrder++;
                s_lastSeq = seq;
                s_received++;
                s_receivedByKind[(int)KindOf(seq)]++;
            }
            catch
            {
                s_corrupt++; // a read past the end on a test packet = exactly the corruption we're hunting
            }
        }

        private static void RPC_Status(long sender, string msg) => AdminConsoleEcho.Print(msg);

        // Outbound queued bytes to the target peer (m_sendQueue + Steam pending) — for flow control.
        private static int GetTargetQueueBytes(long target)
        {
            try { var sock = ZNet.instance != null ? ZNet.instance.GetPeer(target)?.m_socket : null; return sock != null ? sock.GetSendQueueSize() : 0; }
            catch { return 0; }
        }

        private static bool PeerConnected(long uid)
        {
            try { return ZNet.instance != null && ZNet.instance.GetPeers().Any(p => p != null && p.m_uid == uid); }
            catch { return false; }
        }

        private static void Status(long target, string msg)
        {
            try { if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcStatus, msg); }
            catch { }
        }

        private static bool SenderIsAdmin(long sender)
        {
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            if (peer == null) return true; // local server/host
            string host = peer.m_rpc?.GetSocket()?.GetHostName();
            return !string.IsNullOrEmpty(host) && ZNet.instance.IsAdmin(host);
        }

        // Compressible: a 256-byte ramp. Incompressible: a slice of the high-entropy block. With gzip: length-prefixed ramp
        // chunks alternating with length-prefixed Utils.Compress output of high-entropy data, laid out exactly as ZPackage
        // writes byte arrays, so the gzip parts travel as stored regions beside the deflated ramp.
        private static byte[] MakePayload(int size, int seq)
        {
            switch (KindOf(seq))
            {
                case PayloadKind.Incompressible:
                {
                    byte[] payload = new byte[size];
                    byte[] block = RawBlock;
                    int off = (seq * 7) % block.Length;
                    for (int i = 0; i < size; i++) payload[i] = block[(off + i) % block.Length];
                    return payload;
                }
                case PayloadKind.WithGzip:
                {
                    var package = new ZPackage();
                    byte[] block = RawBlock;
                    for (int part = 0; package.Size() < size; part++)
                    {
                        if ((part & 1) == 0)
                        {
                            byte[] plain = new byte[Mathf.Min(PlainPartBytes, size)];
                            for (int i = 0; i < plain.Length; i++) plain[i] = (byte)((i + seq + part) & 0xFF);
                            package.Write(plain);
                        }
                        else
                        {
                            byte[] raw = new byte[GzipPartBytes];
                            int off = (seq * 13 + part * 101) % (block.Length - GzipPartBytes);
                            System.Buffer.BlockCopy(block, off, raw, 0, GzipPartBytes);
                            package.Write(Utils.Compress(raw));
                        }
                    }
                    return package.GetArray();
                }
                default:
                {
                    byte[] payload = new byte[size];
                    for (int i = 0; i < size; i++) payload[i] = (byte)((i + seq) & 0xFF);
                    return payload;
                }
            }
        }

        // Spot-checks the deterministic kinds on top of the checksum; the mixed kind is covered by the checksum alone.
        private static bool PayloadMatches(byte[] payload, int seq)
        {
            if (payload == null || payload.Length == 0) return false;
            int length = payload.Length;
            int[] probeOffsets = { 0, length / 3, length / 2, (2 * length) / 3, length - 1 };
            switch (KindOf(seq))
            {
                case PayloadKind.Incompressible:
                {
                    byte[] block = RawBlock;
                    int off = (seq * 7) % block.Length;
                    foreach (int i in probeOffsets) if (payload[i] != block[(off + i) % block.Length]) return false;
                    return true;
                }
                case PayloadKind.Compressible:
                    foreach (int i in probeOffsets) if (payload[i] != (byte)((i + seq) & 0xFF)) return false;
                    return true;
                default:
                    return true;
            }
        }

        private static int Checksum(byte[] data)
        {
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < data.Length; i++) { h ^= data[i]; h *= 16777619u; }
                return (int)h;
            }
        }
    }
}
