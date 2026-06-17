using System.Collections;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Admin console test for the Deflate compression round-trip (the magic-driven receive fix in
    /// CompressionGroup). 'fgn_comptest [count] [sizeKB]':
    ///   1. Local sanity — Compress -> Decompress BOTH a compressible payload (gets the FGD1 magic +
    ///      shrinks) and an incompressible one (passes through raw, no magic), confirming each is
    ///      byte-identical after the round-trip.
    ///   2. Wire burst — the server fires N packets at you (server -> client, the direction that
    ///      desynced joiners), ALTERNATING compressible (even seq) and incompressible (odd seq) every
    ///      packet. Each carries a nonce, sequence, payload and FNV checksum; the client verifies every
    ///      one arrived intact and counts how many of each kind landed.
    ///
    /// Alternating is the strong test: it forces the receiver to flip between magic (compressed) and
    /// no-magic (raw) on EVERY packet, exercising every compressed->raw and raw->compressed boundary —
    /// exactly where the old start-boundary bug corrupted the stream (a compressed packet read as raw
    /// mis-parses / throws EndOfStream, which IS the real-world disconnect). With the fix every packet
    /// is self-describing via the magic, so all of them round-trip. Admin only.
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

        private static bool s_commandRegistered;
        private static long s_nonceSeq;

        // client-side receive tally for the in-flight test
        private static long s_expectNonce;
        private static int s_expectCount;
        private static int s_received, s_corrupt, s_outOfOrder, s_lastSeq;
        private static int s_recvComp, s_recvRaw;

        // Incompressible block (fixed seed so the server's generate and the client's verify match).
        // Odd-seq packets are filled from this — Deflate can't shrink it, so it crosses the wire raw
        // (no FGD1 magic), while even-seq packets compress and DO carry the magic.
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

        private static bool IsRawSeq(int seq) => (seq & 1) == 1;   // odd = incompressible, even = compressible

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
                "[count] [sizeKB] — FGN diagnostic: round-trip-test Deflate compression. Local "
                + "Compress/Decompress check (both compressible and incompressible), then the server "
                + "bursts N packets ALTERNATING compressible/incompressible (default 100 x 64KB, up to "
                + "10000 x 128KB) and verifies every one survived the magic boundary intact.",
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

            // 1. Local sanity — round-trip BOTH a compressible (even seq, gets the magic + shrinks) and
            //    an incompressible (odd seq, passes through raw) payload, so both receive paths are proven
            //    before the wire test.
            byte[] origC = MakePayload(sizeKB * 1024, 0);
            byte[] cC = CompressionGroup.Compress(origC);
            byte[] backC = CompressionGroup.Decompress(cC);
            bool okC = backC != null && Checksum(backC) == Checksum(origC);

            byte[] origR = MakePayload(sizeKB * 1024, 1);
            byte[] cR = CompressionGroup.Compress(origR);
            byte[] backR = CompressionGroup.Decompress(cR);
            bool okR = backR != null && Checksum(backR) == Checksum(origR);

            args.Context?.AddString($"FGN comptest local {sizeKB}KB: compressible {(okC ? "OK" : "MISMATCH")} "
                + $"({origC.Length}->{cC.Length}, magic={(cC.Length < origC.Length ? "yes" : "no")}); "
                + $"incompressible {(okR ? "OK" : "MISMATCH")} ({origR.Length}->{cR.Length}, magic={(cR.Length < origR.Length ? "yes" : "no")}).");

            // 2. Wire burst (server -> client), alternating compressible / incompressible every packet.
            s_expectNonce = ++s_nonceSeq;
            s_expectCount = count; s_received = 0; s_corrupt = 0; s_outOfOrder = 0; s_lastSeq = -1;
            s_recvComp = 0; s_recvRaw = 0;
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcStart, count, sizeKB, s_expectNonce);
            float cap = ReportDelay(count, sizeKB);
            args.Context?.AddString($"FGN comptest wire: requested {count} x {sizeKB}KB packets (alternating compressible/incompressible). Reports as soon as all arrive (up to ~{cap:F0}s — the pipe is send-rate limited, so a big incompressible burst takes a while). DISCONNECT mid-test = the bug.");
            if (FiresGhettoNetworkMod.Instance != null)
                FiresGhettoNetworkMod.Instance.StartCoroutine(ReportAfter(cap, args.Context));
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
                tail = "Both compressed and raw packets round-tripped across every magic boundary — the fix holds.";
            else if (!completed)
                tail = $"Only {s_received}/{s_expectCount} arrived by the {maxSeconds:F0}s cap — the pipe is send-rate limited, NOT lossy (ZSteamSocket re-queues, never drops; what arrived is intact). Give it longer or lower count/size.";
            else if (s_corrupt > 0)
                tail = "Corruption = the round-trip is broken.";
            else
                tail = "Out-of-order delivery on a reliable channel — investigate.";
            string msg = $"[CompTest] {verdict} — received {s_received}/{s_expectCount} "
                + $"(compressible {s_recvComp}, incompressible {s_recvRaw}), corrupt={s_corrupt}, out-of-order={s_outOfOrder}. {tail}";
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
                if (IsRawSeq(seq)) s_recvRaw++; else s_recvComp++;
            }
            catch
            {
                s_corrupt++; // a read past the end on a test packet = exactly the corruption we're hunting
            }
        }

        private static void RPC_Status(long sender, string msg)
        {
            if (Console.instance != null) Console.instance.AddString(msg);
            else LoggerOptions.LogMessage(msg);
        }

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

        // Even seq → compressible (256-byte ramp; Deflate shrinks it, FGD1 magic gets added).
        // Odd seq  → incompressible (slice of the high-entropy block; passes through raw, no magic).
        // Each packet still varies by seq, and the alternation exercises both receive paths plus every
        // compressed->raw and raw->compressed boundary in the stream.
        private static byte[] MakePayload(int size, int seq)
        {
            byte[] p = new byte[size];
            if (IsRawSeq(seq))
            {
                byte[] block = RawBlock;
                int off = (seq * 7) % block.Length;
                for (int i = 0; i < size; i++) p[i] = block[(off + i) % block.Length];
            }
            else
            {
                for (int i = 0; i < size; i++) p[i] = (byte)((i + seq) & 0xFF);
            }
            return p;
        }

        private static bool PayloadMatches(byte[] p, int seq)
        {
            if (p == null || p.Length == 0) return false;
            int n = p.Length;
            int[] idx = { 0, n / 3, n / 2, (2 * n) / 3, n - 1 };
            if (IsRawSeq(seq))
            {
                byte[] block = RawBlock;
                int off = (seq * 7) % block.Length;
                foreach (int i in idx) if (p[i] != block[(off + i) % block.Length]) return false;
            }
            else
            {
                foreach (int i in idx) if (p[i] != (byte)((i + seq) & 0xFF)) return false;
            }
            return true;
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
