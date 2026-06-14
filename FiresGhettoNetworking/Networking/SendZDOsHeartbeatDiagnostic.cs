using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Per-peer ZDOMan.SendZDOs heartbeat — once every 10 s per connected
    /// peer, on the dedi only, logs the peer's current Steam send-queue
    /// size, our cap, the remaining budget, and how many SendZDOs ticks
    /// fired vs how many bailed early due to queue saturation in that
    /// 10 s window.
    ///
    /// This is the right diagnostic to attribute frozen-mob / stale-voxel
    /// / shaking-cart symptoms to send-side queue saturation. If under
    /// load the budget pins near zero and bail-rate climbs, the server
    /// IS simulating state changes but ZDOMan is throttling its send
    /// attempts before they reach the peer — which presents to the
    /// client as "the server-owned thing never updates."
    ///
    /// Always-on (server only). Cost is one dict lookup + 2 ints
    /// incremented per SendZDOs call. No allocation in the hot path.
    /// </summary>
    [HarmonyPatch]
    public static class SendZDOsHeartbeatDiagnostic
    {
        private struct PeerStats
        {
            public int TicksAttempted;
            public int TicksBailedQueueFull;
            public float NextLogTime;
            public int LastQueueSize;
        }

        private static readonly Dictionary<long, PeerStats> s_stats = new Dictionary<long, PeerStats>();

        // Effective send-queue cap — single source of truth in SendCongestion so the
        // heartbeat and the adaptive throttling gate can never drift apart.
        private static int EffectiveCapBytes() => SendCongestion.EffectiveCapBytes();

        // Vanilla SendZDOs uses 2048 as the "minimum budget before bailing"
        // (line 571 of vanilla ZDOMan.cs). Our transpiler doesn't rewrite
        // this — it stays at 2048. We use it to classify bail reasons.
        private const int VanillaMinBudget = 2048;

        [HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        public static void SendZDOs_Heartbeat_Prefix(ZDOMan.ZDOPeer peer, bool flush)
        {
            // Default OFF. This writes ~1 line per peer per 10s; on a busy server that
            // is tens of thousands of synchronous log lines, so it is opt-in only.
            if (FiresGhettoNetworkMod.ConfigEnableSendHeartbeatLog == null
                || !FiresGhettoNetworkMod.ConfigEnableSendHeartbeatLog.Value) return;
            if (peer == null || peer.m_peer == null || peer.m_peer.m_socket == null) return;
            if (ZNet.instance == null || !ZNet.instance.IsDedicated()) return;

            long uid = peer.m_peer.m_uid;
            int queueSize;
            try { queueSize = peer.m_peer.m_socket.GetSendQueueSize(); }
            catch { return; }

            int cap = EffectiveCapBytes();
            int budget = cap - queueSize;
            bool wouldBail = !flush && queueSize > cap;
            bool budgetTooSmall = budget < VanillaMinBudget;

            // Update per-peer stats.
            PeerStats st;
            if (!s_stats.TryGetValue(uid, out st))
                st = new PeerStats { NextLogTime = Time.realtimeSinceStartup + 10f };

            st.TicksAttempted++;
            if (wouldBail || budgetTooSmall) st.TicksBailedQueueFull++;
            st.LastQueueSize = queueSize;
            s_stats[uid] = st;

            // 10 s per-peer rollup.
            float now = Time.realtimeSinceStartup;
            if (now < st.NextLogTime) return;

            string peerName = "<unknown>";
            try { peerName = peer.m_peer.m_socket.GetEndPointString(); } catch { /* socket may be torn down mid-frame */ }

            float bailPct = st.TicksAttempted > 0
                ? (100f * st.TicksBailedQueueFull / st.TicksAttempted)
                : 0f;

            LoggerOptions.LogMessage(
                $"[SendZDOsHB] peer={peerName} uid={uid} queue={queueSize}B cap={cap}B budget={budget}B "
                + $"ticks(attempted={st.TicksAttempted},bailedQueueFull={st.TicksBailedQueueFull},bailPct={bailPct:F1}%) "
                + $"flush={flush}.");

            st.TicksAttempted = 0;
            st.TicksBailedQueueFull = 0;
            st.NextLogTime = now + 10f;
            s_stats[uid] = st;
        }

        // Clean up state when a peer disconnects so the dict doesn't accumulate
        // stale entries over long-running sessions.
        [HarmonyPatch(typeof(ZDOMan), "RemovePeer")]
        [HarmonyPostfix]
        public static void RemovePeer_ClearStats_Postfix(ZNetPeer netPeer)
        {
            if (netPeer == null) return;
            s_stats.Remove(netPeer.m_uid);
        }
    }
}
