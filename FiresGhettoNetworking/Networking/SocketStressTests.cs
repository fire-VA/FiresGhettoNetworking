using System;
using System.Collections;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Admin-only throughput stress tests, the incompressible counterpart to fgn_comptest's correctness run.
    ///
    ///   fgn_flood [count] [sizeKB] [raw|comp]   bursts packets at the caller and reports throughput and loss.
    ///   fgn_socketramp [startGB] [stepGB] [maxGB]   escalates until the link drops or the data arrives wrong,
    ///       writing the last-good and failing levels to FiresGhetto_StressResults.txt on the server ahead of
    ///       each send so a hard crash still leaves the breaking point on disk.
    ///
    /// Transport matches fgn_comptest: command-fired routed RPC up, peer uid back down.
    /// </summary>
    [HarmonyPatch]
    public static class SocketStressTests
    {
        private const int BytesPerKB = 1024;
        private const int BytesPerMB = 1024 * 1024;
        private const long BytesPerGB = 1024L * 1024 * 1024;
        private const long BytesPerTB = 1024L * 1024 * 1024 * 1024;

        // Steam's per-message ceiling is 512 KB; 400 KB matches SafeRoutedRpc's chunk size + envelope headroom.
        private const int ChunkKB = 400;
        private const int ChunkBytes = ChunkKB * BytesPerKB;
        // Drain-driven send: each frame, top the peer's send queue up to QueueHighWaterBytes (feed as fast as
        // the socket drains) instead of a fixed chunks-per-frame pace, capped at MaxBurstChunksPerFrame/frame
        // so the initial fill can't monopolise one frame. This removes the self-imposed throttle that pinned
        // the old loop to ~1.6 MB/frame regardless of the unlocked send rate.
        private const int QueueHighWaterBytes = 128 * BytesPerMB;
        private const int MaxBurstChunksPerFrame = 32;

        private const long HardMaxBytes = 4L * BytesPerTB;
        private const int MaxCount = 10000;
        private const float AckTimeoutSecs = 12f;
        private const float AckTimeoutCapSecs = 4 * 60 * 60;            // 4 hours
        private const float SendStallSecs = 60f;                       // bail a level if the socket stops draining entirely
        private const float RatePriorDataFallbackBytesPerSec = 512 * BytesPerKB;
        private const int RawBlockSeed = 0x5713;

        // The ramp lifts a connection to the SAME ceiling the HYPERBOOST config applies (single source of
        // truth in EffectiveConfig), so the ramp measures exactly what HyperBoost unlocks whether or not
        // HyperBoost is toggled on. Restored to the configured tier when the test ends.
        private const int RampRateOverrideBytes    = AutoTune.EffectiveConfig.HyperBoostSendRateMaxBytes;
        private const int RampRateMinOverrideBytes = AutoTune.EffectiveConfig.HyperBoostSendRateMinBytes;
        private const int RampBufferOverrideBytes  = AutoTune.EffectiveConfig.HyperBoostSendBufferBytes;

        // Receiver-side lifts requested via RPC from the server. Steam's per-connection config is one-sided —
        // the client must apply its own recv lift. Both enums need FiresSteamworksPatcher on the receiving side.
        private const int RampRecvBufferOverrideBytes     = AutoTune.EffectiveConfig.HyperBoostRecvBufferBytes;
        private const int RampRecvMaxMessageOverrideBytes = AutoTune.EffectiveConfig.HyperBoostRecvMaxMessageBytes;
        private const float ClientRecvPrepSettleSeconds   = 0.5f;
        private const float LevelGapSeconds = 1f;

        private const string RpcFloodStart = "FGN_StressFloodStart";
        private const string RpcRampStart  = "FGN_StressRampStart";
        private const string RpcAck        = "FGN_StressAck";
        private const string RpcData       = "FGN_StressData";
        private const string RpcFlush      = "FGN_StressFlush";
        private const string RpcMsg        = "FGN_StressMsg";
        private const string RpcClientPrep   = "FGN_StressClientPrep";
        private const string RpcClientUnprep = "FGN_StressClientUnprep";

        private static bool s_registered;
        private static bool s_busy;
        private static long s_nonce;

        // Server: pending-ack state for the level currently in flight.
        private static long s_waitNonce = -1;
        private static bool s_ackGot;
        private static long s_ackBytes;
        private static int  s_ackCorrupt;

        // Server: result of the last SendLevel.
        private enum LevelStatus { Ok, Disconnected, Timeout, Incomplete }
        private static LevelStatus s_resStatus;
        private static long s_resSent;
        private static long s_resAckBytes;
        private static int  s_resCorrupt;
        private static float s_resSecs;
        private static float s_resTimeoutSecs;          // ack timeout actually used for the last level
        private static float s_lastRateBytesPerSec;     // observed end-to-end rate from the last OK level

        // Instrumentation for the last SendLevel — answers "what's the limiter?": offered (into the socket)
        // vs delivered (acked) rate, plus how hard we leaned on the socket queue and CPU per chunk.
        private static float s_resSendWallSecs;          // wall time to offer every byte into the socket
        private static long  s_resPeakQueueBytes;        // deepest send-queue level observed (socket saturation)
        private static float s_resGenCpuMs;              // cumulative payload-gen + enqueue CPU
        private static int   s_resBackpressureFrames;    // frames we hit the queue high-water and waited to drain

        // Client: tally for the level currently being received.
        private static long s_rxNonce = -1;
        private static long s_rxBytes;
        private static int  s_rxCorrupt;

        // Precomputed high-entropy block so Compress() can't shrink it — the wire really moves the bytes.
        private static byte[] s_rawBlock;
        private static byte[] RawBlock
        {
            get
            {
                if (s_rawBlock == null)
                {
                    s_rawBlock = new byte[ChunkBytes];
                    new System.Random(RawBlockSeed).NextBytes(s_rawBlock);
                }
                return s_rawBlock;
            }
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        [HarmonyPostfix]
        public static void OnZNetStart()
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.Register<int, int, int>(RpcFloodStart, RPC_FloodStart);
            ZRoutedRpc.instance.Register<int, int, int>(RpcRampStart, RPC_RampStart);
            ZRoutedRpc.instance.Register<long, long, int>(RpcAck, RPC_Ack);
            ZRoutedRpc.instance.Register<ZPackage>(RpcData, RPC_Data);
            ZRoutedRpc.instance.Register<long, int>(RpcFlush, RPC_Flush);
            ZRoutedRpc.instance.Register<string>(RpcMsg, RPC_Msg);
            ZRoutedRpc.instance.Register<int, int>(RpcClientPrep, RPC_ClientPrep);
            ZRoutedRpc.instance.Register(RpcClientUnprep, RPC_ClientUnprep);

            if (s_registered) return;
            s_registered = true;
            new Terminal.ConsoleCommand("fgn_flood",
                "[count] [sizeKB] [raw|comp] — FGN stress (admin): server bursts count x sizeKB packets "
                + "at you (default 1000 x 64, incompressible) and reports throughput + any loss. Pushes "
                + "far past normal traffic to watch for drops.",
                new Terminal.ConsoleEvent(OnFlood));
            new Terminal.ConsoleCommand("fgn_socketramp",
                "[startGB] [stepGB] [maxGB] — FGN stress (admin): temporarily lifts the send rate, then "
                + "escalates an incompressible burst until you disconnect / data corrupts / you stop acking, "
                + "and records the breaking point to FiresGhetto_StressResults.txt. Default 10/5/1024 (GB).",
                new Terminal.ConsoleEvent(OnRamp));
        }

        private static void OnFlood(Terminal.ConsoleEventArgs args)
        {
            int count = 1000, sizeKB = 64, raw = 1;
            if (args.Length >= 2) int.TryParse(args[1], out count);
            if (args.Length >= 3) int.TryParse(args[2], out sizeKB);
            if (args.Length >= 4 && args[3] != null && args[3].ToLower().StartsWith("comp")) raw = 0;
            if (ZNet.instance == null || ZRoutedRpc.instance == null) { args.Context?.AddString("FGN: not connected."); return; }
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcFloodStart, count, sizeKB, raw);
            args.Context?.AddString($"FGN flood requested: {count} x {sizeKB}KB "
                + $"({(raw == 1 ? "incompressible" : "compressible")}). Watch here for the report — a DISCONNECT here IS the finding.");
        }

        private static void RPC_FloodStart(long sender, int count, int sizeKB, int raw)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!ServerClientUtils.IsAdmin(sender)) { Msg(sender, "FGN flood denied — admin only."); return; }
            if (s_busy) { Msg(sender, "FGN stress already running."); return; }
            count = Mathf.Clamp(count, 1, MaxCount);
            sizeKB = Mathf.Clamp(sizeKB, 1, ChunkKB);
            long total = Math.Min((long)count * sizeKB * 1024, HardMaxBytes);
            if (FiresGhettoNetworkMod.Instance != null)
                FiresGhettoNetworkMod.Instance.StartCoroutine(FloodRun(sender, total, sizeKB * 1024, raw == 1));
        }

        private static IEnumerator FloodRun(long target, long totalBytes, int pktBytes, bool raw)
        {
            s_busy = true;
            Msg(target, $"[Flood] bursting {totalBytes / 1024} KB total in {pktBytes / 1024} KB packets "
                + $"({(raw ? "incompressible" : "compressible")})...");
            yield return FiresGhettoNetworkMod.Instance.StartCoroutine(SendLevel(target, ++s_nonce, totalBytes, pktBytes, raw));

            string verdict;
            if (s_resStatus == LevelStatus.Ok)
            {
                float mbps = (s_resAckBytes / 1024f / 1024f) / Math.Max(0.001f, s_resSecs);
                verdict = $"[Flood] PASS — {s_resAckBytes}/{s_resSent} bytes received, corrupt={s_resCorrupt}, "
                    + $"delivered {mbps:F1} MB/s over {s_resSecs:F1}s. No loss. {DiagLine()}";
            }
            else if (s_resStatus == LevelStatus.Disconnected)
                verdict = $"[Flood] FAIL — you DISCONNECTED after ~{s_resSent} bytes. That burst exceeded what the socket could take.";
            else if (s_resStatus == LevelStatus.Timeout)
                verdict = $"[Flood] FAIL — no ack in {s_resTimeoutSecs:F0}s after sending {s_resSent} bytes (stalled pipe).";
            else
                verdict = $"[Flood] FAIL — incomplete/corrupt: {s_resAckBytes}/{s_resSent} bytes, corrupt={s_resCorrupt}.";

            LoggerOptions.LogMessage(verdict);
            Msg(target, verdict);
            s_busy = false;
        }

        private static void OnRamp(Terminal.ConsoleEventArgs args)
        {
            int startGB = 10, stepGB = 5, maxGB = 1024;
            if (args.Length >= 2) int.TryParse(args[1], out startGB);
            if (args.Length >= 3) int.TryParse(args[2], out stepGB);
            if (args.Length >= 4) int.TryParse(args[3], out maxGB);
            if (ZNet.instance == null || ZRoutedRpc.instance == null) { args.Context?.AddString("FGN: not connected."); return; }
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcRampStart, startGB, stepGB, maxGB);
            args.Context?.AddString($"FGN socket ramp requested: {startGB}GB +{stepGB}GB up to {maxGB}GB. "
                + $"The server records the breaking point to {StressResults.FileName}. A DISCONNECT marks the cliff.");
        }

        private static void RPC_RampStart(long sender, int startGB, int stepGB, int maxGB)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!ServerClientUtils.IsAdmin(sender)) { Msg(sender, "FGN ramp denied — admin only."); return; }
            if (s_busy) { Msg(sender, "FGN stress already running."); return; }
            int maxGbCeil = (int)(HardMaxBytes / (1024L * 1024 * 1024));
            startGB = Mathf.Clamp(startGB, 1, maxGbCeil);
            stepGB = Mathf.Clamp(stepGB, 1, maxGbCeil);
            maxGB = Mathf.Clamp(maxGB, startGB, maxGbCeil);
            if (FiresGhettoNetworkMod.Instance != null)
                FiresGhettoNetworkMod.Instance.StartCoroutine(RampRun(sender, startGB, stepGB, maxGB));
        }

        private static IEnumerator RampRun(long target, int startGB, int stepGB, int maxGB)
        {
            s_busy = true;
            bool isSteam = false;
            // try/finally so the rate override is ALWAYS restored and AdaptiveSendRate.Suspend is ALWAYS
            // cleared even if a level throws — otherwise a mid-ramp exception strands the adaptive controller
            // suspended for the rest of the server's life. (yield + try/finally is legal; yield + catch is not.)
            try
            {
                StressResults.Write($"===== socketramp START -> peer {target} (start={startGB}GB step={stepGB}GB max={maxGB}GB) =====");
                // PlayFab/crossplay runs its own windowed protocol — no Steam knob applies, so report honestly.
                ZNetPeer peer = ZNet.instance.GetPeer(target);
                isSteam = NetworkingRatesGroup.IsSteamSocket(peer);
                StressResults.Write($"peer socket = {NetworkingRatesGroup.UnwrappedSocketName(peer)}");
                if (isSteam)
                {
                    NetworkingRatesGroup.OverrideForStressTest(RampRateOverrideBytes, RampBufferOverrideBytes);
                    NetworkingRatesGroup.OverrideConnectionForStressTest(peer, RampRateMinOverrideBytes, RampRateOverrideBytes, RampBufferOverrideBytes);
                    try { ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcClientPrep, RampRecvBufferOverrideBytes, RampRecvMaxMessageOverrideBytes); } catch { }
                    StressResults.Write($"Steam connection — send rate lifted to {RampRateOverrideBytes / BytesPerMB}MB/s "
                        + $"(min {RampRateMinOverrideBytes / BytesPerMB}MB/s) + send buffer {RampBufferOverrideBytes / BytesPerMB}MB. "
                        + $"Asked client to lift recv buffer to {RampRecvBufferOverrideBytes / BytesPerMB}MB + recv max-message {RampRecvMaxMessageOverrideBytes / BytesPerMB}MB.");
                    Msg(target, $"[Ramp] starting {startGB}GB +{stepGB}GB -> {maxGB}GB on a STEAM connection "
                        + $"(rate {RampRateOverrideBytes / BytesPerMB}MB/s, send buffer {RampBufferOverrideBytes / BytesPerMB}MB, client recv {RampRecvBufferOverrideBytes / BytesPerMB}MB). Recording to {StressResults.FileName}.");
                    yield return new WaitForSeconds(ClientRecvPrepSettleSeconds);
                }
                else
                {
                    StressResults.Write("PlayFab/crossplay connection — rate is PlayFab-governed (windowed protocol), NOT liftable via config; measuring the crossplay cliff at native rate");
                    Msg(target, $"[Ramp] starting {startGB}GB +{stepGB}GB -> {maxGB}GB on a PLAYFAB/CROSSPLAY connection. Its ~1 MB/s rate is governed by PlayFab's own protocol — NOT liftable via config — so this measures the crossplay cliff at native rate. Recording to {StressResults.FileName}.");
                }

                int lastGoodGB = 0;
                int round = 0;
                for (int levelGB = startGB; levelGB <= maxGB; levelGB += stepGB)
                {
                    round++;
                    StressResults.Write($"ATTEMPTING level {levelGB}GB (round {round})");
                    yield return FiresGhettoNetworkMod.Instance.StartCoroutine(SendLevel(target, ++s_nonce, (long)levelGB * BytesPerGB, ChunkBytes, true));

                    if (s_resStatus == LevelStatus.Ok)
                    {
                        float mbps = (s_resAckBytes / (float)BytesPerMB) / Math.Max(0.001f, s_resSecs);
                        string diag = DiagLine();
                        StressResults.Write($"level {levelGB}GB OK (delivered {mbps:F1} MB/s, {s_resAckBytes} bytes) — {diag}");
                        Msg(target, $"[Ramp] {levelGB}GB OK — delivered {mbps:F1} MB/s; {diag} — escalating...");
                        lastGoodGB = levelGB;
                        yield return new WaitForSeconds(LevelGapSeconds);
                        continue;
                    }

                    string why = s_resStatus == LevelStatus.Disconnected ? "client DISCONNECTED"
                               : s_resStatus == LevelStatus.Timeout ? $"no ack in {s_resTimeoutSecs:F0}s (stalled)"
                               : $"incomplete/corrupt ({s_resAckBytes}/{s_resSent} bytes, corrupt={s_resCorrupt})";
                    StressResults.Write($"level {levelGB}GB FAILED — {why}.");
                    StressResults.Write($"===== CLIFF: highest sustained burst = {lastGoodGB}GB; failed at {levelGB}GB =====");
                    Msg(target, $"[Ramp] STOP at {levelGB}GB — {why}. Cliff = {lastGoodGB}GB. Recorded to {StressResults.FileName}.");
                    yield break;
                }

                StressResults.Write($"===== RAMP TOPPED OUT: reached {maxGB}GB with no failure (raise maxGB to find the cliff) =====");
                Msg(target, $"[Ramp] reached max {maxGB}GB with no failure — raise maxGB to keep climbing. Highest good = {lastGoodGB}GB.");
            }
            finally
            {
                if (isSteam)
                {
                    NetworkingRatesGroup.RestoreSendRates();
                    NetworkingRatesGroup.RestoreConnection(ZNet.instance.GetPeer(target));
                    try { ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcClientUnprep); } catch { }
                }
                AdaptiveSendRate.Suspend = false;
                s_busy = false;
            }
        }

        /// Sends totalBytes in pktBytes chunks, flushes, then waits for the client's ack. Result lands in s_res*.
        /// Drain-driven: each frame it tops the peer's send queue up to QueueHighWaterBytes (capped at
        /// MaxBurstChunksPerFrame) then yields, so the socket is offered as much as it can drain rather than a
        /// fixed pace. The s_res* instrumentation records offered-vs-delivered so the real limiter is visible.
        private static IEnumerator SendLevel(long target, long nonce, long totalBytes, int pktBytes, bool raw)
        {
            s_waitNonce = nonce; s_ackGot = false; s_ackBytes = 0; s_ackCorrupt = 0;
            s_resSent = 0; s_resAckBytes = 0; s_resCorrupt = 0; s_resSecs = 0; s_resStatus = LevelStatus.Ok;
            s_resSendWallSecs = 0f; s_resPeakQueueBytes = 0; s_resGenCpuMs = 0f; s_resBackpressureFrames = 0;

            pktBytes = Mathf.Clamp(pktBytes, 1024, ChunkBytes);
            ZNetPeer peer = ZNet.instance != null ? ZNet.instance.GetPeer(target) : null;
            ISocket sock = peer != null ? peer.m_socket : null;   // ISocket.GetSendQueueSize — same value ZDOMan reads
            var genSw = new System.Diagnostics.Stopwatch();

            float t0 = Time.time;
            float lastProgress = Time.time;
            long lastSent = 0;
            int seq = 0;
            long sent = 0;
            while (sent < totalBytes)
            {
                if (!PeerConnected(target)) { s_resStatus = LevelStatus.Disconnected; s_resSent = sent; yield break; }

                int burst = 0;
                bool hitHighWater = false;
                while (sent < totalBytes && burst < MaxBurstChunksPerFrame)
                {
                    int queued = sock != null ? sock.GetSendQueueSize() : 0;
                    if (queued > s_resPeakQueueBytes) s_resPeakQueueBytes = queued;
                    if (queued >= QueueHighWaterBytes) { hitHighWater = true; break; }   // socket full — let it drain

                    int thisChunk = (int)Math.Min(pktBytes, totalBytes - sent);
                    genSw.Start();
                    ZPackage pkg = new ZPackage();
                    pkg.Write(nonce);
                    pkg.Write(seq);
                    byte[] payload = MakePayload(thisChunk, seq, raw);
                    pkg.Write(payload);
                    pkg.Write(Checksum(payload));
                    bool sendFailed = false;
                    try { ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcData, pkg); }
                    catch { sendFailed = true; }
                    genSw.Stop();
                    if (sendFailed) { s_resStatus = LevelStatus.Disconnected; s_resSent = sent; yield break; }

                    sent += thisChunk; seq++; burst++;
                }
                if (hitHighWater) s_resBackpressureFrames++;

                if (sent > lastSent) { lastSent = sent; lastProgress = Time.time; }
                else if (Time.time - lastProgress > SendStallSecs)
                {
                    s_resStatus = LevelStatus.Timeout; s_resSent = sent; yield break;   // socket stopped draining entirely
                }
                yield return null;
            }
            s_resSent = sent;
            s_resSendWallSecs = Time.time - t0;
            s_resGenCpuMs = (float)genSw.Elapsed.TotalMilliseconds;

            try { ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcFlush, nonce, seq); } catch { }

            // Adaptive timeout scales with the observed rate so a slow-pipe level isn't false-positive'd as the cliff.
            float rate = s_lastRateBytesPerSec > 0f ? s_lastRateBytesPerSec : RatePriorDataFallbackBytesPerSec;
            s_resTimeoutSecs = Mathf.Clamp(AckTimeoutSecs + (totalBytes / rate) * 2f, AckTimeoutSecs, AckTimeoutCapSecs);
            float deadline = Time.time + s_resTimeoutSecs;
            while (Time.time < deadline)
            {
                if (!PeerConnected(target)) { s_resStatus = LevelStatus.Disconnected; yield break; }
                if (s_ackGot && s_waitNonce == nonce)
                {
                    s_resAckBytes = s_ackBytes; s_resCorrupt = s_ackCorrupt; s_resSecs = Time.time - t0;
                    if (s_resSecs > 0.01f) s_lastRateBytesPerSec = s_resAckBytes / s_resSecs;
                    s_resStatus = (s_ackCorrupt == 0 && s_ackBytes >= sent) ? LevelStatus.Ok : LevelStatus.Incomplete;
                    yield break;
                }
                yield return null;
            }
            s_resStatus = LevelStatus.Timeout;
        }

        // One-line "what was the limiter?" summary for a completed level: offered (how fast we could shove
        // bytes into the socket) vs the delivered rate the caller prints. offered >> delivered or high
        // backpressure => the socket/wire is the cap; offered ~= delivered and low => gen/CPU is the cap.
        private static string DiagLine()
        {
            float offeredMbps = s_resSendWallSecs > 0.001f ? (s_resSent / (float)BytesPerMB) / s_resSendWallSecs : 0f;
            float chunks = Math.Max(1f, s_resSent / (float)ChunkBytes);
            float perChunkMs = s_resGenCpuMs / chunks;
            return $"offered {offeredMbps:F1} MB/s into socket, peak queue {s_resPeakQueueBytes / BytesPerMB} MB, "
                + $"gen-CPU {s_resGenCpuMs:F0} ms ({perChunkMs:F2} ms/chunk), backpressure {s_resBackpressureFrames} frame(s)";
        }

        private static void RPC_Data(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            try
            {
                long nonce = pkg.ReadLong();
                pkg.ReadInt();                       // seq (reserved — reliable channel is in-order)
                byte[] payload = pkg.ReadByteArray();
                int chk = pkg.ReadInt();
                if (nonce != s_rxNonce) { s_rxNonce = nonce; s_rxBytes = 0; s_rxCorrupt = 0; }   // new level
                if (Checksum(payload) != chk) s_rxCorrupt++;
                s_rxBytes += payload.Length;
            }
            catch { s_rxCorrupt++; }
        }

        private static void RPC_Flush(long sender, long nonce, int totalChunks)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;   // client only
            long bytes = (nonce == s_rxNonce) ? s_rxBytes : 0;
            int corrupt = (nonce == s_rxNonce) ? s_rxCorrupt : 0;
            try { if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(RpcAck, nonce, bytes, corrupt); } catch { }
        }

        private static void RPC_Ack(long sender, long nonce, long bytes, int corrupt)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (nonce != s_waitNonce) return;
            s_ackBytes = bytes; s_ackCorrupt = corrupt; s_ackGot = true;
        }

        /// Client-only: server-side socket sets push the SERVER's view of recv. Only the client can lift
        /// its OWN inbound buffer, so the server asks via RPC and we apply it here.
        private static void RPC_ClientPrep(long sender, int recvBufferBytes, int recvMaxMessageBytes)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            if (peer == null) return;
            NetworkingRatesGroup.OverrideConnectionRecvForStressTest(peer, recvBufferBytes, recvMaxMessageBytes);
        }

        private static void RPC_ClientUnprep(long sender)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            if (peer == null) return;
            NetworkingRatesGroup.RestoreConnection(peer);
        }

        private static void RPC_Msg(long sender, string msg) => AdminConsoleEcho.Print(msg);

        private static byte[] MakePayload(int size, int seq, bool raw)
        {
            byte[] p = new byte[size];
            if (raw)
            {
                // Incompressible: copy from the high-entropy block (Deflate can't shrink it, so the wire
                // carries the full size). seq shifts the start so consecutive packets aren't identical.
                byte[] block = RawBlock;
                int off = (seq * 7) % block.Length;
                for (int i = 0; i < size; i++) p[i] = block[(off + i) % block.Length];
            }
            else
            {
                // Compressible (256-byte ramp) — shrinks to almost nothing; exercises the compressor.
                for (int i = 0; i < size; i++) p[i] = (byte)((i + seq) & 0xFF);
            }
            return p;
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

        private static bool PeerConnected(long uid)
        {
            try { return ZNet.instance != null && ZNet.instance.GetPeers().Any(p => p != null && p.m_uid == uid); }
            catch { return false; }
        }


        private static void Msg(long target, string msg) => AdminConsoleEcho.Send(RpcMsg, target, msg);
    }

    // Persistent, write-ahead results log on the server. Each line is flushed immediately (AppendAllText
    // opens+closes the handle), so the breaking point survives even a hard crash that follows.
    internal static class StressResults
    {
        public const string FileName = "FiresGhetto_StressResults.txt";
        private static string _path;
        private static string Path => _path ?? (_path = System.IO.Path.Combine(Paths.ConfigPath, FileName));

        public static void Write(string line)
        {
            try { File.AppendAllText(Path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}"); }
            catch { }
        }
    }
}
