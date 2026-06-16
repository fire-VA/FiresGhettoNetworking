using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Admin console test + control for the ServerSync / ServerCharacters 30-second
    /// self-disconnect disarm (see BulkTransferGatePatches).
    ///
    ///   fgn_overload [seconds]       — disarmed run: force every send queue above the 20 KB
    ///                                  gate for N seconds (default 60). Reconnect a client; it
    ///                                  should ride out the window (the send waits). Expect PASS.
    ///   fgn_overload [seconds] arm   — CONTROL: momentarily restores ServerSync's real 30 s
    ///                                  timeout, so a client reconnecting during the window SHOULD
    ///                                  be dropped at ~30 s. A drop here proves the test path is
    ///                                  live (the force reaches waitForQueue and the disconnect
    ///                                  fires); the timeout is restored afterward.
    ///
    /// Each run also reports how many GetSendQueueSize reads it forced high — proof the force is
    /// actually active rather than the test quietly doing nothing. Admin only; relays to the
    /// server over a routed RPC and echoes the result back to the caller's console.
    /// </summary>
    [HarmonyPatch]
    public static class DisarmOverloadTest
    {
        private const int ForcedQueueBytes = 50000;       // comfortably above the 20 KB ServerSync gate
        private const int DefaultSeconds = 60;
        private const int MaxSeconds = 300;
        private const float ArmedTimeoutSeconds = 30f;    // ServerSync's real timeout, for the control
        private const string RpcStart = "FGN_OverloadStart";
        private const string RpcResult = "FGN_OverloadResult";

        private static bool s_active;
        private static long s_forceCount;
        private static bool s_commandRegistered;

        [HarmonyPatch(typeof(ZSteamSocket), "GetSendQueueSize")]
        [HarmonyPostfix]
        public static void ForceQueueHigh(ref int __result)
        {
            if (s_active && __result < ForcedQueueBytes)
            {
                __result = ForcedQueueBytes;
                s_forceCount++;
            }
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        [HarmonyPostfix]
        public static void OnZNetStart()
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.Register<int, int>(RpcStart, RPC_Start);
            ZRoutedRpc.instance.Register<string>(RpcResult, RPC_Result);

            if (s_commandRegistered) return;
            s_commandRegistered = true;
            new Terminal.ConsoleCommand("fgn_overload",
                "[seconds] [arm] — FGN diagnostic (admin): force the send queue above the ServerSync "
                + "20 KB gate for N seconds to test the 30 s self-disconnect disarm. Add 'arm' to "
                + "momentarily restore the real 30 s timeout as a control — a client reconnecting then "
                + "SHOULD drop at 30 s, proving the test path is live.",
                new Terminal.ConsoleEvent(OnCommand));
        }

        // Client side: relay the request to the server.
        private static void OnCommand(Terminal.ConsoleEventArgs args)
        {
            int secs = DefaultSeconds;
            if (args.Length >= 2) int.TryParse(args[1], out secs);
            secs = Mathf.Clamp(secs, 5, MaxSeconds);
            int arm = (args.Length >= 3 && args[2] != null && args[2].ToLower().StartsWith("arm")) ? 1 : 0;

            if (ZNet.instance == null || ZRoutedRpc.instance == null)
            {
                args.Context?.AddString("FGN: not connected to a server.");
                return;
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcStart, secs, arm);
            args.Context?.AddString($"FGN: overload test ({secs}s{(arm == 1 ? ", ARMED control" : "")}) requested. "
                + "Reconnect a client now; watch here and the server console for [OverloadTest].");
        }

        // Server side: verify admin, run the test.
        private static void RPC_Start(long sender, int seconds, int arm)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!SenderIsAdmin(sender)) { Reply(sender, "FGN overload test denied — admin only."); return; }
            if (s_active) { Reply(sender, "FGN overload test already running."); return; }
            seconds = Mathf.Clamp(seconds, 5, MaxSeconds);
            if (FiresGhettoNetworkMod.Instance != null)
                FiresGhettoNetworkMod.Instance.StartCoroutine(Run(seconds, sender, arm == 1));
        }

        private static bool SenderIsAdmin(long sender)
        {
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            if (peer == null) return true; // originated locally on the server/host — the host is admin
            string host = peer.m_rpc?.GetSocket()?.GetHostName();
            return !string.IsNullOrEmpty(host) && ZNet.instance.IsAdmin(host);
        }

        private static IEnumerator Run(int secs, long requester, bool arm)
        {
            float savedTimeout = BulkTransferGatePatches.DisconnectTimeoutSeconds;
            BulkTransferGatePatches.DisconnectTimeoutSeconds = arm ? ArmedTimeoutSeconds : savedTimeout;
            BulkTransferGatePatches.ApplyJotunnTimeout();

            int startPeers = PeerCount();
            s_forceCount = 0;
            s_active = true;
            string head = arm
                ? $"[OverloadTest] OVERLOAD ON (ARMED CONTROL) — real 30s timeout restored, queue forced "
                  + $">= {ForcedQueueBytes} B for {secs}s. Reconnect a client now: it SHOULD drop at ~30s, "
                  + $"which proves the test path is live. Peers at start: {startPeers}."
                : $"[OverloadTest] OVERLOAD ON — forcing every send queue >= {ForcedQueueBytes} B (gate 20000) "
                  + $"for {secs}s. Reconnect a client now; with the disarm it stays. Peers at start: {startPeers}.";
            LoggerOptions.LogMessage(head);
            Reply(requester, head);

            float elapsed = 0f;
            bool dropSeen = false;
            int minPeers = startPeers;
            while (elapsed < secs)
            {
                yield return new WaitForSeconds(5f);
                elapsed += 5f;
                int now = PeerCount();
                if (now < minPeers) minPeers = now;
                if (now < startPeers && !dropSeen)
                {
                    dropSeen = true;
                    string note = $"[OverloadTest] Peer count dropped at ~{elapsed:F0}s ({startPeers} -> {now}).";
                    LoggerOptions.LogWarning(note);
                    Reply(requester, note);
                }
            }

            s_active = false;
            BulkTransferGatePatches.DisconnectTimeoutSeconds = savedTimeout;
            BulkTransferGatePatches.ApplyJotunnTimeout();
            int endPeers = PeerCount();

            string forced = $"forced GetSendQueueSize high {s_forceCount} time(s)";
            string result;
            if (arm)
                result = dropSeen
                    ? $"[OverloadTest] CONTROL PASS — the armed run dropped a peer at ~30s, so the test path is "
                      + $"LIVE ({forced}). Now run 'fgn_overload {secs}' (no arm) and reconnect: it should NOT drop."
                    : $"[OverloadTest] CONTROL INCONCLUSIVE — armed run dropped nobody ({forced}). "
                      + (s_forceCount == 0
                            ? "Force count is ZERO — GetSendQueueSize was never forced; the peer socket type may differ from ZSteamSocket."
                            : "The queue WAS forced, so reconnect a client DURING the window and retry — the connect-time sync is what runs waitForQueue.");
            else
                result = !dropSeen
                    ? $"[OverloadTest] PASS — no peer dropped through {secs}s of forced saturation "
                      + $"(peers {startPeers}->{endPeers}, {forced}). The 30s self-disconnect is disarmed. "
                      + $"To prove the test bites, run 'fgn_overload {secs} arm' and reconnect — that should drop at ~30s."
                    : $"[OverloadTest] FAIL — a peer dropped during a DISARMED run (start={startPeers} min={minPeers} "
                      + $"end={endPeers}, {forced}). Check the scan log for 'disarmed at 0 site(s)' on ServerCharacters.Shared.";
            if (arm ? dropSeen : !dropSeen) LoggerOptions.LogMessage(result); else LoggerOptions.LogWarning(result);
            Reply(requester, result);
        }

        // Client side: echo a server message into the local console.
        private static void RPC_Result(long sender, string msg)
        {
            if (Console.instance != null) Console.instance.AddString(msg);
            else LoggerOptions.LogMessage(msg);
        }

        private static void Reply(long target, string msg)
        {
            try { if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcResult, msg); }
            catch { /* requester may have disconnected */ }
        }

        private static int PeerCount()
        {
            try { return ZNet.instance != null ? ZNet.instance.GetPeers().Count : 0; }
            catch { return 0; }
        }
    }
}
