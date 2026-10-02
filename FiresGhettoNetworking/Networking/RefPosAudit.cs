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

        public static void Tick(ZNet net)
        {
            if (net == null || !net.IsDedicated()) return;
            float now = Time.realtimeSinceStartup;
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
                if (warn) LoggerOptions.LogWarning(line + " (ownership checks use the reported one)");
                else LoggerOptions.LogDebug(line);
            }
        }
    }
}
