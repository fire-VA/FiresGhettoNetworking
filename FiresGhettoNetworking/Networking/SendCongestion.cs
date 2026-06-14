using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Shared send-queue congestion signal.
    ///
    /// FGN's send-side reordering (distant-ZDO penalty, player-position boost) and
    /// AI-LOD throttling only earn their keep when a peer's Steam send queue is
    /// actually backing up. On a server with bandwidth to spare the queues sit
    /// near-empty, every queued ZDO ships the same tick, and any reordering only
    /// burns CPU and can ADD latency by deferring what would otherwise have gone
    /// out immediately. This gate lets those systems engage under real congestion
    /// and otherwise stay completely out of the way — the lean behaviour that made
    /// older builds feel smoother on healthy, well-provisioned servers.
    /// </summary>
    public static class SendCongestion
    {
        /// <summary>
        /// Effective per-peer send-queue cap, mirrored from the same source the
        /// NetworkingRatesGroup transpiler used (it can't be read back from the IL).
        /// </summary>
        public static int EffectiveCapBytes()
        {
            switch (AutoTune.EffectiveConfig.QueueSize())
            {
                case QueueSizeOptions._80KB: return 80 * 1024;
                case QueueSizeOptions._64KB: return 64 * 1024;
                case QueueSizeOptions._48KB: return 48 * 1024;
                case QueueSizeOptions._32KB: return 32 * 1024;
                default:                     return 102400; // _vanilla (preloader-raised)
            }
        }

        private static float ThresholdFraction()
        {
            int pct = FiresGhettoNetworkMod.ConfigSendCongestionThresholdPct != null
                ? FiresGhettoNetworkMod.ConfigSendCongestionThresholdPct.Value
                : 50;
            return Mathf.Clamp(pct, 10, 100) / 100f;
        }

        /// <summary>
        /// Absolute send-queue byte level at which a peer counts as congested — the
        /// same cap × threshold the throttling gate uses. Exposed so external readers
        /// (e.g. NetworkStats) classify congestion identically to the gate itself.
        /// </summary>
        public static float CongestionThresholdBytes() => EffectiveCapBytes() * ThresholdFraction();

        public static int GetQueueSize(ZDOMan.ZDOPeer peer)
        {
            if (peer == null || peer.m_peer == null || peer.m_peer.m_socket == null) return -1;
            try { return peer.m_peer.m_socket.GetSendQueueSize(); }
            catch { return -1; }
        }

        /// <summary>True when this peer's send queue has backed up past the threshold.</summary>
        public static bool IsPeerCongested(ZDOMan.ZDOPeer peer)
        {
            int q = GetQueueSize(peer);
            if (q < 0) return false;
            return q >= EffectiveCapBytes() * ThresholdFraction();
        }

        // Global "is ANY peer congested" signal for subsystems that don't hold a peer
        // handle (AI LOD runs per-mob in CustomFixedUpdate). Cached for a short window
        // so a per-mob-per-tick caller doesn't rescan every connected peer thousands of
        // times a second.
        private static bool  s_cached;
        private static float s_nextCheck;
        private const float  RecheckSeconds = 1f;

        public static bool AnyPeerCongested()
        {
            float now = Time.realtimeSinceStartup;
            if (now < s_nextCheck) return s_cached;
            s_nextCheck = now + RecheckSeconds;

            s_cached = false;
            if (ZNet.instance == null) return false;

            float thresholdBytes = EffectiveCapBytes() * ThresholdFraction();
            foreach (ZNetPeer p in ZNet.instance.GetPeers())
            {
                if (p == null || p.m_socket == null) continue;
                int q;
                try { q = p.m_socket.GetSendQueueSize(); }
                catch { continue; }
                if (q >= thresholdBytes) { s_cached = true; break; }
            }
            return s_cached;
        }
    }
}
