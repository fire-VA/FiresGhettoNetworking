using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Admin console test for the ZSTD compression round-trip (the magic-driven receive fix in
    /// CompressionGroup). 'fgn_comptest [count] [sizeKB]':
    ///   1. Local sanity — Compress -> Decompress a payload in-process and confirm it's byte-identical
    ///      (and that it actually compressed, i.e. the FGZ7 magic was added).
    ///   2. Wire burst — ask the server to fire N large, compressible packets at you (server -> client,
    ///      the direction that desynced joiners). Each carries a nonce, sequence, deterministic payload
    ///      and FNV checksum; the client verifies every one decompressed to exactly the right bytes.
    ///
    /// If the old start-boundary bug were present, the burst's compressed packets would be read as raw:
    /// the RPC param read mis-parses (counted missing / checksum fail) or throws EndOfStream — which IS
    /// the real-world disconnect. With the fix every packet is self-describing via the magic, so they
    /// all decompress and the test passes. Admin only.
    /// </summary>
    [HarmonyPatch]
    public static class CompressionRoundTripTest
    {
        private const string RpcStart  = "FGN_CompTestStart";   // client -> server
        private const string RpcData   = "FGN_CompTestData";    // server -> client
        private const string RpcStatus = "FGN_CompTestStatus";  // server -> client (text)
        private const int DefaultCount = 20;
        private const int DefaultSizeKB = 32;
        private const int MaxCount = 100;
        private const int MaxSizeKB = 128;

        private static bool s_commandRegistered;
        private static long s_nonceSeq;

        // client-side receive tally for the in-flight test
        private static long s_expectNonce;
        private static int s_expectCount;
        private static int s_received, s_corrupt, s_outOfOrder, s_lastSeq;

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
                "[count] [sizeKB] — FGN diagnostic: round-trip-test ZSTD compression. Does a local "
                + "Compress/Decompress check, then has the server burst N compressible packets "
                + "(default 20 x 32KB) at you and verifies every one decompressed intact.",
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

            // 1. Local sanity round-trip.
            byte[] orig = MakePayload(sizeKB * 1024, 0);
            byte[] compressed = CompressionGroup.Compress(orig);
            bool didCompress = compressed.Length < orig.Length;
            byte[] back = CompressionGroup.Decompress(compressed);
            bool localOk = back != null && back.Length == orig.Length && Checksum(back) == Checksum(orig);
            args.Context?.AddString($"FGN comptest local {sizeKB}KB: {(localOk ? "OK" : "MISMATCH")} "
                + $"(compressed {orig.Length}->{compressed.Length} bytes, magic={(didCompress ? "yes" : "no/uncompressible")}).");

            // 2. Wire burst (server -> client).
            s_expectNonce = ++s_nonceSeq;
            s_expectCount = count; s_received = 0; s_corrupt = 0; s_outOfOrder = 0; s_lastSeq = -1;
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcStart, count, sizeKB, s_expectNonce);
            float delay = ReportDelay(count, sizeKB);
            args.Context?.AddString($"FGN comptest wire: requested {count} x {sizeKB}KB compressed packets from the server. Result in ~{delay:F0}s (if you DISCONNECT mid-test, that's the bug).");
            if (FiresGhettoNetworkMod.Instance != null)
                FiresGhettoNetworkMod.Instance.StartCoroutine(ReportAfter(delay, args.Context));
        }

        private static float ReportDelay(int count, int sizeKB) => Mathf.Clamp(5f + (count * sizeKB) / 200f, 5f, 60f);

        private static IEnumerator ReportAfter(float seconds, Terminal ctx)
        {
            yield return new WaitForSeconds(seconds);
            bool pass = s_received == s_expectCount && s_corrupt == 0 && s_outOfOrder == 0;
            string tail = pass
                ? "Every compressed packet round-tripped intact — the fix holds."
                : (s_received < s_expectCount
                    ? "Missing packets = compressed data read as raw (mis-parse) — the start-boundary corruption."
                    : "Corruption = the compression round-trip is broken.");
            string msg = $"[CompTest] {(pass ? "PASS" : "FAIL")} — received {s_received}/{s_expectCount}, "
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
            bool active = IsCompressionActive(sender);
            Status(sender, $"FGN comptest: server bursting {count} x {sizeKB}KB to you (compression to you: "
                + $"{(active ? "ACTIVE" : "INACTIVE — packets go raw, so a PASS proves nothing; enable compression both sides")}).");
            if (FiresGhettoNetworkMod.Instance != null)
                FiresGhettoNetworkMod.Instance.StartCoroutine(SendBurst(sender, count, sizeKB, nonce));
        }

        private static bool IsCompressionActive(long uid)
        {
            try
            {
                ZNetPeer peer = ZNet.instance.GetPeer(uid);
                return peer?.m_socket != null && CompressionGroup.CompressionStatus.GetSendCompressionStarted(peer.m_socket);
            }
            catch { return false; }
        }

        private static IEnumerator SendBurst(long target, int count, int sizeKB, long nonce)
        {
            int size = sizeKB * 1024;
            for (int seq = 0; seq < count; seq++)
            {
                ZPackage pkg = new ZPackage();
                pkg.Write(nonce);
                pkg.Write(seq);
                byte[] payload = MakePayload(size, seq);
                pkg.Write(payload);
                pkg.Write(Checksum(payload));
                ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcData, pkg);
                if ((seq & 15) == 15) yield return null; // breather every 16 so a big burst doesn't hard-stall a frame
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

        // Compressible (256-byte repeat) and sequence-varying, so ZSTD shrinks it (magic gets added)
        // yet each packet differs.
        private static byte[] MakePayload(int size, int seq)
        {
            byte[] p = new byte[size];
            for (int i = 0; i < size; i++) p[i] = (byte)((i + seq) & 0xFF);
            return p;
        }

        private static bool PayloadMatches(byte[] p, int seq)
        {
            if (p == null || p.Length == 0) return false;
            int n = p.Length;
            int[] idx = { 0, n / 3, n / 2, (2 * n) / 3, n - 1 };
            foreach (int i in idx) if (p[i] != (byte)((i + seq) & 0xFF)) return false;
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
