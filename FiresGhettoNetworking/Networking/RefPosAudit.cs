using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.5.23 ([generator], HomesteadRun HR1): the bot's companions were handed to the observer's client every 2-6 s although they
    /// stood 6 m from the bot. Ownership hand-offs (vanilla ReleaseZDOS and FGN's ServerOwnershipPatchesV3) judge "does the owner
    /// still cover it" by the owner peer's REPORTED reference position (ZNetPeer.GetRefPos), which a client sends every 2 s and
    /// which vanilla also points at any owned Tracker (cart, ship) every FixedUpdate. This logs, every 30 s on a dedicated server,
    /// each peer's reported position against its character's position, so a reported position that has drifted away shows.
    /// </summary>
    internal static class RefPosAudit
    {
        private const float ReportSeconds = 30f;
        private const float DriftWarnMeters = 32f;
        private static float s_next = -1f;
        // 1.5.48 (FGN user, 1.5.41: after a portal the reported position stayed 12 km from the character; Fire: "we should be using the
        // ACTUAL position for ownership"): twice a second, a peer whose reported position is farther than SnapMeters from its character
        // gets the character's position instead. Vanilla points the reported position at an owned ship or cart, which stays within a
        // few metres, so only a stale report (a portal, a loading screen) is replaced; the client's next report (every 2 s) wins again
        // once it is near. Every ownership and zone decision that reads ZNetPeer.GetRefPos then follows the body.
        private const float SnapMeters = 64f;
        private const float SnapSeconds = 0.5f;
        private const float SnapLogSeconds = 30f;
        private static float s_nextSnap, s_nextSnapLog;

        private static void SnapStale(ZNet net, float now)
        {
            if (now < s_nextSnap || ZDOMan.instance == null) return;
            s_nextSnap = now + SnapSeconds;
            foreach (ZNetPeer peer in net.GetConnectedPeers())
            {
                if (peer == null || peer.m_characterID.IsNone()) continue;
                ZDO character = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (character == null) continue;
                Vector3 at = character.GetPosition();
                float drift = Utils.DistanceXZ(peer.m_refPos, at);
                if (drift <= SnapMeters) continue;
                if (LoggerOptions.DebugEnabled && now >= s_nextSnapLog)
                {
                    s_nextSnapLog = now + SnapLogSeconds;
                    LoggerOptions.LogDebug($"[RefPos] {peer.m_playerName} ({peer.m_uid}): reported ({peer.m_refPos.x:F0}, {peer.m_refPos.z:F0}) is {drift:F0} m from the character; using the character's ({at.x:F0}, {at.z:F0})");
                }
                peer.m_refPos = at;
            }
        }

        public static void Tick(ZNet net)
        {
            if (net == null || !net.IsDedicated()) return;
            float now = Time.realtimeSinceStartup;
            SnapStale(net, now);
            if (s_next < 0f) { s_next = now + ReportSeconds; return; }
            if (now < s_next) return;
            s_next = now + ReportSeconds;
            foreach (ZNetPeer peer in net.GetConnectedPeers())
            {
                if (peer == null) continue;
                Vector3 reported = peer.GetRefPos();
                ZDO character = peer.m_characterID.IsNone() || ZDOMan.instance == null ? null : ZDOMan.instance.GetZDO(peer.m_characterID);
                if (character == null)
                {
                    if (LoggerOptions.DebugEnabled)
                        LoggerOptions.LogDebug($"[RefPos] {peer.m_playerName} ({peer.m_uid}): reported ({reported.x:F0}, {reported.z:F0}); no character ZDO");
                    continue;
                }
                Vector3 at = character.GetPosition();
                float drift = Utils.DistanceXZ(reported, at);
                bool warn = drift > DriftWarnMeters;
                if (!warn && !LoggerOptions.DebugEnabled) continue;
                string line = $"[RefPos] {peer.m_playerName} ({peer.m_uid}): reported ({reported.x:F0}, {reported.z:F0}), character ({at.x:F0}, {at.z:F0}), "
                              + $"{drift:F0} m apart";
                if (warn) LoggerOptions.LogWarning(line + " (left as reported: within the snap distance or no character yet)");
                else LoggerOptions.LogDebug(line);
            }
        }
    }
}
