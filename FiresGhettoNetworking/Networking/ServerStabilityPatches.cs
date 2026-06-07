using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// SSS-parity defensive patches. Three small fixes the original
    /// Serverside Simulations mod ships alongside its authority work,
    /// each addressing a real failure mode that can show up on a busy
    /// dedicated server regardless of whether ServerOwnershipPatches is
    /// in play:
    ///
    ///   - <see cref="Humanoid_UpdateAttack_NullGuard_Prefix"/> — clears
    ///     a stale Attack whose owning Character has been destroyed.
    ///     Without this, a creature whose target's Character vanishes
    ///     mid-swing throws NRE in UpdateAttack and the AI tick aborts
    ///     for the rest of that frame.
    ///
    ///   - <see cref="WearNTear_UpdateSupport_ReinitColliders_Prefix"/> —
    ///     re-runs SetupColliders when m_colliders is populated but
    ///     m_bounds is null. SSS hit this when WNT pieces appear in a
    ///     half-initialised state after zone-stream changes load them
    ///     earlier than vanilla expects.
    ///
    ///   - <see cref="ZRoutedRpc_RouteRPC_ShipRequestRespons_Prefix"/> —
    ///     the "RequestRespons" RPC is how a client asks the server to
    ///     hand them ship ownership when they board. We process it on
    ///     the server side directly so the transfer happens immediately
    ///     instead of bouncing through the current ZDO owner first.
    ///     Compatible with vanilla peer-owned ships (just executes the
    ///     same end-state transfer one hop earlier).
    ///
    /// All three are gated by <see cref="FiresGhettoNetworkMod.ConfigEnableServerAuthority"/>
    /// via the PatchAll call site in Ascend.Awake, matching SSS's
    /// dedi-only deployment model. None of these take ZDO ownership —
    /// they're pure defensive/optimisation patches and remain in place
    /// even after the 2026-05-22 ServerOwnershipPatches revert.
    /// </summary>
    [HarmonyPatch]
    public static class ServerStabilityPatches
    {
        // ====================================================================
        // Humanoid.UpdateAttack — clear stale m_currentAttack
        //
        // From SSS Core.cs (Humanoid_UpdateAttack_Patch). If
        // m_currentAttack.m_character has been destroyed (target vanished
        // mid-swing, GC reaped the wrapper, etc.), vanilla UpdateAttack
        // dereferences it without a null check and NREs. The fix is to
        // null it out so vanilla treats the Humanoid as not currently
        // attacking and picks a new attack on the next tick.
        // ====================================================================
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

        // ====================================================================
        // WearNTear.UpdateSupport — re-init colliders if half-set
        //
        // From SSS Core.cs (WearNTear_UpdateSupport_Patch). UpdateSupport
        // relies on m_bounds being populated. If a WNT piece reaches the
        // first UpdateSupport call with m_colliders set but m_bounds null
        // (half-initialised — happens when a zone is created before WNT
        // Awake fully completes, which heavy zone-stream activity can
        // provoke), the support calculation NREs. Re-running SetupColliders
        // fills m_bounds idempotently.
        // ====================================================================
        [HarmonyPatch(typeof(WearNTear), "UpdateSupport")]
        [HarmonyPrefix]
        public static void WearNTear_UpdateSupport_ReinitColliders_Prefix(WearNTear __instance)
        {
            if (__instance == null) return;
            if (__instance.m_colliders == null) return;
            if (__instance.m_bounds != null) return;
            __instance.SetupColliders();
        }

        // ====================================================================
        // ZRoutedRpc.RouteRPC — RequestRespons → server-side ship handoff
        //
        // From SSS Core.cs (ZRoutedRpc_RouteRPC_Patch). When a client boards
        // a ship and Ship.Interact fires the "RequestRespons" routed RPC
        // asking for control, vanilla routes the RPC to the current ZDO
        // owner who then transfers ownership. This prefix performs the
        // ownership transfer directly on the server, saving one routing
        // hop. Compatible with vanilla peer-owned ships (the end state is
        // identical — the requesting peer becomes the ship's ZDO owner)
        // and with server-owned ships if a future authority patch returns.
        //
        // The cached hash avoids per-call GetStableHashCode allocation —
        // RouteRPC is hot.
        // ====================================================================
        private static readonly int s_requestResponsHash = "RequestRespons".GetStableHashCode();

        [HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC")]
        [HarmonyPrefix]
        public static void ZRoutedRpc_RouteRPC_ShipRequestRespons_Prefix(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (rpcData == null) return;
            if (rpcData.m_methodHash != s_requestResponsHash) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (rpcData.m_parameters == null) return;

            // Peek the bool flag without permanently advancing the read
            // cursor — vanilla RouteRPC doesn't read m_parameters itself,
            // but a downstream handler on the receiving end might re-read
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
