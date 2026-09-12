using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Defensive fixes carried over from Serverside Simulations: a null guard for a stale Attack whose
    /// Character was destroyed, collider re-initialisation for half-loaded WearNTear pieces, and server-side
    /// handling of the ship "RequestRespons" ownership handoff. Registered only with server authority on.
    /// </summary>
    [HarmonyPatch]
    public static class ServerStabilityPatches
    {
        // A stale Attack whose Character was destroyed mid-swing NREs inside vanilla UpdateAttack; clearing
        // it makes the Humanoid pick a new attack next tick.
        [HarmonyPatch(typeof(Humanoid), "UpdateAttack")]
        [HarmonyPrefix]
        public static void Humanoid_UpdateAttack_NullGuard_Prefix(Humanoid __instance)
        {
            if (__instance == null) return;
            if (__instance.m_currentAttack == null) return;
            // Live target — let vanilla proceed.
            if (__instance.m_currentAttack.m_character != null) return;
            // Stale Attack pointing at a destroyed Character — drop it.
            __instance.m_currentAttack = null;
        }

        // UpdateSupport needs m_bounds. A piece can reach it with m_colliders set and m_bounds still null when
        // heavy zone streaming creates the zone before WearNTear.Awake finishes; SetupColliders is idempotent.
        [HarmonyPatch(typeof(WearNTear), "UpdateSupport")]
        [HarmonyPrefix]
        public static void WearNTear_UpdateSupport_ReinitColliders_Prefix(WearNTear __instance)
        {
            if (__instance == null) return;
            if (__instance.m_colliders == null) return;
            if (__instance.m_bounds != null) return;
            __instance.SetupColliders();
        }

        // Ship.Interact fires the routed "RequestRespons" RPC to ask for control; vanilla routes it to the
        // current ZDO owner, who then hands ownership over. Doing the transfer on the server saves that hop
        // and ends in the same state. Hash cached because RouteRPC is hot.
        private static readonly int s_requestResponsHash = "RequestRespons".GetStableHashCode();

        [HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC")]
        [HarmonyPrefix]
        public static void ZRoutedRpc_RouteRPC_ShipRequestRespons_Prefix(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (rpcData == null) return;
            if (rpcData.m_methodHash != s_requestResponsHash) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (rpcData.m_parameters == null) return;
            // Peek the flag and restore the cursor; a downstream handler reads the same package.
            // from the same package, so we restore position to be safe.
            int savedPos = rpcData.m_parameters.GetPos();
            bool wantsControl;
            try { wantsControl = rpcData.m_parameters.ReadBool(); }
            catch { return; }
            rpcData.m_parameters.SetPos(savedPos);

            if (!wantsControl) return;

            ZDO zdo = ZDOMan.instance?.GetZDO(rpcData.m_targetZDO);
            if (zdo == null) return;

            zdo.SetOwner(rpcData.m_targetPeerID);
            LoggerOptions.LogInfo(
                $"RequestRespons: transferred ship ZDO {rpcData.m_targetZDO} to peer {rpcData.m_targetPeerID}.");
        }
    }
}
