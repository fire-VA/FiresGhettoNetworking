using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Server-side hooks for RoutedRpcManager: routed RPCs arriving from players, broadcasts the server raises itself, and
    /// the server's own destroy batches, whose holders must be read before vanilla handles the destroy and forgets them.
    /// </summary>
    [HarmonyPatch]
    public static class RpcRouterPatches
    {
        [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
        [HarmonyPrefix]
        public static bool RPC_RoutedRPC_Prefix(ZRoutedRpc __instance, ZRpc rpc, ZPackage pkg)
        {
            if (!VanillaAccess.RoutedRpcIsServer(__instance)) return true;
            RoutedRpcManager.ProcessRoutedRPC(__instance, rpc, pkg);
            return false;
        }

        [HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC")]
        [HarmonyPrefix]
        public static bool RouteRPC_Prefix(ZRoutedRpc __instance, ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (rpcData == null || rpcData.m_targetPeerID != 0L) return true;
            if (!VanillaAccess.RoutedRpcIsServer(__instance) || rpcData.m_senderPeerID != VanillaAccess.RoutedRpcId(__instance)) return true;
            if (!RoutedRpcManager.FilteringEnabled()) return true;
            return !RoutedRpcManager.TryRelayServerBroadcast(__instance, rpcData);
        }

        [HarmonyPatch(typeof(ZDOMan), "SendDestroyed")]
        [HarmonyPrefix]
        public static void SendDestroyed_Prefix()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || !RoutedRpcManager.FilteringEnabled()) return;
            RoutedRpcManager.CaptureServerDestroyHolders();
        }

        [HarmonyPatch(typeof(ZDOMan), "SendDestroyed")]
        [HarmonyPostfix]
        public static void SendDestroyed_Postfix() => RoutedRpcManager.ClearServerDestroyHolders();
    }
}
