namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.5.37 ([ghost], FiresValcast 0.6.54 creature possession): a player drives a creature with their own input while its BaseAI
    /// is off, so the possessor's game must OWN it (only the owner simulates a Character). Valcast writes the ZDO long
    /// "valcast_possessed" = the possessor's playerID on take (and ClaimOwnership), 0 on release. Server ownership must not take
    /// such a creature back: while the flag is set, the owner is the connected peer whose player has that playerID; when no
    /// connected peer has it (the possessor left), the server clears the flag once and normal ownership resumes.
    /// No Valcast reference: the key is read by name, so any mod may set it.
    /// </summary>
    internal static class ValcastPossession
    {
        internal static readonly int s_possessed = "valcast_possessed".GetStableHashCode();

        /// <summary>Server ownership passes: true when the ZDO is possessed and was handled here (the caller skips its own rule).</summary>
        internal static bool Handle(ZDO zdo)
        {
            long possessor = zdo.GetLong(s_possessed, 0L);
            if (possessor == 0L) return false;
            long peerUid = PeerOf(possessor);
            if (peerUid != 0L)
            {
                if (zdo.GetOwner() != peerUid) zdo.SetOwner(peerUid);
                return true;
            }
            zdo.Set(s_possessed, 0L);   // the possessor is not connected: release the claim, normal rules from the next pass
            LoggerOptions.LogDebug($"[Ownership] {zdo.m_uid}: possessor {possessor} is not connected; 'valcast_possessed' cleared");
            return false;
        }

        internal static bool IsPossessed(ZDO zdo) => zdo != null && zdo.GetLong(s_possessed, 0L) != 0L;

        private static long PeerOf(long playerId)
        {
            if (ZNet.instance == null || ZDOMan.instance == null) return 0L;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer == null || peer.m_characterID.IsNone()) continue;
                ZDO character = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (character != null && character.GetLong(ZDOVars.s_playerID, 0L) == playerId) return peer.m_uid;
            }
            return 0L;
        }
    }
}
