using System;
using System.Collections;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using FiresGhettoNetworkMod.AutoTune;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Passive, opt-in headroom telemetry: how close real outbound traffic gets to the socket cliff
    /// that fgn_socketramp finds. The server samples each peer's send-queue (ISocket.GetSendQueueSize —
    /// the same value vanilla reads in ZDOMan) on a low-frequency timer and logs the peak each minute.
    ///
    /// ZERO standing cost by design:
    ///   * OFF by default — the sampler coroutine is never even started, so it costs literally nothing.
    ///   * No Harmony hook on any hot method (it POLLS; it does not patch GetSendQueueSize like the
    ///     overload test does), and no per-frame Update.
    ///   * When ON, it wakes once every SampleSeconds (default 5s), reads ints, and allocates nothing
    ///     per sample (one reused WaitForSeconds). A string is built only on the once-a-minute report.
    ///   * fgn_headroom prints an on-demand snapshot and costs nothing until it's actually run.
    /// </summary>
    [HarmonyPatch]
    public static class SendQueueHeadroomMonitor
    {
        public static ConfigEntry<bool> ConfigEnabled;

        private const float SampleSeconds = 5f;
        private const float ReportSeconds = 60f;

        private static bool s_commandRegistered;
        private static bool s_sampling;
        private static int s_sessionPeakBytes;
        private static int s_windowPeakBytes;

        private const string RpcReq  = "FGN_HeadroomReq";    // client -> server
        private const string RpcResp = "FGN_HeadroomResp";   // server -> client (text)

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("10 - Diagnostics", "Send Queue Headroom Monitor", false,
                "Server-only, opt-in. When ON, samples each peer's outbound send-queue every 5s and logs the "
                + "peak each minute, so you can see how close live traffic gets to the cliff fgn_socketramp finds. "
                + "OFF (default) = the sampler never runs = zero performance cost. fgn_headroom prints a snapshot on demand.");
            ConfigEnabled.SettingChanged += (_, __) => MaybeStartSampling();
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        [HarmonyPostfix]
        static void OnZNetStart()
        {
            if (ZRoutedRpc.instance != null)
            {
                ZRoutedRpc.instance.Register(RpcReq, new Action<long>(RPC_Req));
                ZRoutedRpc.instance.Register<string>(RpcResp, RPC_Resp);
            }
            if (!s_commandRegistered)
            {
                s_commandRegistered = true;
                new Terminal.ConsoleCommand("fgn_headroom",
                    "FGN diagnostic (admin): print a snapshot of every peer's current outbound send-queue vs the "
                    + "buffer ceiling — how much socket headroom is left right now. Enable the 'Send Queue Headroom "
                    + "Monitor' config to also track the per-minute peak during play.",
                    new Terminal.ConsoleEvent(OnCommand));
            }
            MaybeStartSampling();
        }

        // Starts the sampler only when it's enabled, we're the server, and it isn't already running.
        // Called at world load and whenever the config toggle flips on.
        private static void MaybeStartSampling()
        {
            if (s_sampling) return;
            if (ConfigEnabled == null || !ConfigEnabled.Value) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (FiresGhettoNetworkMod.Instance == null) return;
            FiresGhettoNetworkMod.Instance.StartCoroutine(SampleLoop());
        }

        private static IEnumerator SampleLoop()
        {
            s_sampling = true;
            s_sessionPeakBytes = 0; s_windowPeakBytes = 0;
            var wait = new WaitForSeconds(SampleSeconds);   // allocate once, reuse — no per-sample GC
            float lastReport = Time.time;
            LoggerOptions.LogMessage($"[Headroom] sampler ON (sample every {SampleSeconds:F0}s, report every {ReportSeconds:F0}s).");

            while (ZNet.instance != null && ZNet.instance.IsServer() && ConfigEnabled != null && ConfigEnabled.Value)
            {
                int maxNow = SampleMaxQueueBytes();
                if (maxNow > s_windowPeakBytes) s_windowPeakBytes = maxNow;
                if (maxNow > s_sessionPeakBytes) s_sessionPeakBytes = maxNow;

                if (Time.time - lastReport >= ReportSeconds)
                {
                    lastReport = Time.time;
                    int bufKB = EffectiveConfig.SteamSendBufferBytes() / 1024;
                    int pct = bufKB > 0 ? (s_windowPeakBytes / 1024) * 100 / bufKB : 0;
                    LoggerOptions.LogMessage($"[Headroom] last {ReportSeconds:F0}s peak send-queue = {s_windowPeakBytes / 1024} KB "
                        + $"({pct}% of the {bufKB} KB buffer); session peak {s_sessionPeakBytes / 1024} KB.");
                    s_windowPeakBytes = 0;
                }
                yield return wait;
            }

            s_sampling = false;
            LoggerOptions.LogMessage("[Headroom] sampler OFF.");
        }

        // Reads each peer's queued outbound bytes via the ISocket interface — works for Steam AND PlayFab
        // crossplay sockets (vanilla reads the same value in ZDOMan). No allocations.
        private static int SampleMaxQueueBytes()
        {
            int max = 0;
            var peers = ZNet.instance.GetPeers();
            for (int i = 0; i < peers.Count; i++)
            {
                var sock = peers[i] != null ? peers[i].m_socket : null;
                if (sock == null) continue;
                int q = sock.GetSendQueueSize();
                if (q > max) max = q;
            }
            return max;
        }

        // ---- on-demand snapshot (client -> server -> text reply) ----
        private static void OnCommand(Terminal.ConsoleEventArgs args)
        {
            if (ZNet.instance == null || ZRoutedRpc.instance == null) { args.Context?.AddString("FGN: not connected."); return; }
            if (ZNet.instance.IsServer()) { args.Context?.AddString(BuildSnapshot()); return; }   // listen host: read locally
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcReq);
            args.Context?.AddString("FGN: headroom snapshot requested from the server...");
        }

        private static void RPC_Req(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!IsAdmin(sender)) { Reply(sender, "FGN headroom denied — admin only."); return; }
            Reply(sender, BuildSnapshot());
        }

        private static void RPC_Resp(long sender, string msg)
        {
            if (Console.instance != null) Console.instance.AddString(msg);
            else LoggerOptions.LogMessage(msg);
        }

        private static string BuildSnapshot()
        {
            var peers = ZNet.instance.GetPeers();
            int n = peers.Count, max = 0;
            long sum = 0;
            for (int i = 0; i < peers.Count; i++)
            {
                var sock = peers[i] != null ? peers[i].m_socket : null;
                if (sock == null) continue;
                int q = sock.GetSendQueueSize();
                sum += q;
                if (q > max) max = q;
            }
            int bufKB = EffectiveConfig.SteamSendBufferBytes() / 1024;
            int avgKB = n > 0 ? (int)(sum / n / 1024) : 0;
            int pct = bufKB > 0 ? (max / 1024) * 100 / bufKB : 0;
            string peak = s_sampling
                ? $"session peak {s_sessionPeakBytes / 1024} KB"
                : "session peak n/a (enable the Headroom Monitor config to track it during play)";
            return $"[Headroom] peers={n}, current max queue={max / 1024} KB ({pct}% of {bufKB} KB buffer), "
                + $"avg={avgKB} KB; {peak}.";
        }

        private static void Reply(long target, string msg)
        {
            try { if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcResp, msg); } catch { }
        }

        private static bool IsAdmin(long sender)
        {
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            if (peer == null) return true;   // originated locally on the server/host — the host is admin
            string host = peer.m_rpc?.GetSocket()?.GetHostName();
            return !string.IsNullOrEmpty(host) && ZNet.instance.IsAdmin(host);
        }
    }
}
