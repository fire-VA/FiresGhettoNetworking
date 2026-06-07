using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Server-side intercept for routed RPCs. The prefix replaces vanilla's
    /// RPC_RoutedRPC body on the server with RoutedRpcManager.ProcessRoutedRPC,
    /// which mirrors vanilla's local-handle/forward gating, runs the registered
    /// handler chain, and performs AoI-filtered forwarding when configured.
    /// On clients the prefix is a no-op (returns true to let vanilla run).
    /// </summary>
    [HarmonyPatch]
    public static class RpcRouterPatches
    {
        [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
        [HarmonyPrefix]
        public static bool RPC_RoutedRPC_Prefix(ZRoutedRpc __instance, ZRpc rpc, ZPackage pkg)
        {
            if (!__instance.m_server) return true;
            RoutedRpcManager.ProcessRoutedRPC(__instance, rpc, pkg);
            return false;
        }
    }
}
