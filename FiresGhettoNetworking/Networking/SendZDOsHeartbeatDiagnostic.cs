using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Opt-in per-peer rollup of ZDOMan.SendZDOs on the dedicated server: send-queue size, our cap, the
    /// remaining budget and how many ticks bailed on a saturated queue. Attributes frozen-mob and stale-voxel
    /// reports to send-side throttling rather than to simulation. Off by default because it writes a line per
    /// peer per window.
    /// </summary>
    [HarmonyPatch]
    public static class SendZDOsHeartbeatDiagnostic
    {
        private struct PeerStats
        {
            public int TicksAttempted;
            public int TicksBailedQueueFull;
            public float NextLogTime;
        }

        private static readonly Dictionary<long, PeerStats> s_stats = new Dictionary<long, PeerStats>();

        // Vanilla SendZDOs bails when the remaining budget drops under this; the transpiler leaves it alone,
        // so the same figure classifies the bail reason here.
        private const int VanillaMinBudgetBytes = 2048;
        private const float RollupSeconds = 10f;

        [HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        public static void SendZDOs_Heartbeat_Prefix(ZDOMan.ZDOPeer peer, bool flush)
        {
            if (FiresGhettoNetworkMod.ConfigEnableSendHeartbeatLog == null
                || !FiresGhettoNetworkMod.ConfigEnableSendHeartbeatLog.Value) return;
            if (peer == null || peer.m_peer == null || peer.m_peer.m_socket == null) return;
            if (ZNet.instance == null || !ZNet.instance.IsDedicated()) return;

            long uid = peer.m_peer.m_uid;
            int queueSize;
            try { queueSize = peer.m_peer.m_socket.GetSendQueueSize(); }
            catch { return; }

            int cap = LinkController.WindowBytes(peer);
            int budget = cap - queueSize;
            bool wouldBail = !flush && queueSize > cap;
            bool budgetTooSmall = budget < VanillaMinBudgetBytes;

            // Update per-peer stats.
            PeerStats st;
            if (!s_stats.TryGetValue(uid, out st))
                st = new PeerStats { NextLogTime = Time.realtimeSinceStartup + RollupSeconds };

            st.TicksAttempted++;
            if (wouldBail || budgetTooSmall) st.TicksBailedQueueFull++;
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
            st.NextLogTime = now + RollupSeconds;
            s_stats[uid] = st;
        }

        [HarmonyPatch(typeof(ZDOMan), "RemovePeer")]
        [HarmonyPostfix]
        public static void RemovePeer_ClearStats_Postfix(ZNetPeer netPeer)
        {
            if (netPeer == null) return;
            s_stats.Remove(netPeer.m_uid);
        }
    }
}
