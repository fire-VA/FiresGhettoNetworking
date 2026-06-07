namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Drops RPC_HealthChanged RPCs from being routed to other clients.
    /// 
    /// Health changes are synced via ZDO data — the RPC is redundant for other clients.
    /// Each client reads health from the ZDO when they need it.
    /// 
    /// Based on BetterZeeRouter.HealthChangedHandler.
    /// </summary>
    public sealed class HealthChangedHandler : RpcMethodHandler
    {
        private static readonly HealthChangedHandler _instance = new HealthChangedHandler();

        private HealthChangedHandler() { }

        public static void Register()
        {
            RoutedRpcManager.AddHandler("RPC_HealthChanged", _instance);
        }

        public override bool Process(ZRoutedRpc.RoutedRPCData routedRpcData)
        {
            // Block — health state is synced via ZDO, RPC is redundant for routing
            return false;
        }
    }
}
