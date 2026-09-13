using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Shared "is this peer's send queue backing up" signal. The send-side reordering and AI-LOD throttling
    /// only pay for themselves under real congestion; with bandwidth to spare every queued ZDO ships the same
    /// tick anyway, so reordering just burns CPU and defers updates that would already have gone out.
    /// </summary>
    public static class SendCongestion
    {
        /// <summary>The per-peer send-queue cap ZDOMan.SendZDOs is enforcing right now.</summary>
        public static int EffectiveCapBytes() => NetworkingRatesGroup.ZdoSendQueueCapInForceBytes();

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
            int queueBytes = GetQueueSize(peer);
            if (queueBytes < 0) return false;
            return queueBytes >= EffectiveCapBytes() * ThresholdFraction();
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
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer == null || peer.m_socket == null) continue;
                int queueBytes;
                try { queueBytes = peer.m_socket.GetSendQueueSize(); }
                catch { continue; }
                if (queueBytes >= thresholdBytes) { s_cached = true; break; }
            }
            return s_cached;
        }
    }
}
