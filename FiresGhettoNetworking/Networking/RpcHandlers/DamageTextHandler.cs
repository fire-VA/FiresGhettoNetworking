namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Drops DamageText RPCs from being routed to other clients.
    /// 
    /// DamageText is purely visual - each client generates their own damage numbers
    /// from the damage event. Routing these RPCs to all clients is wasted bandwidth.
    /// 
    /// Based on BetterZeeRouter.DamageTextHandler.
    /// </summary>
    public sealed class DamageTextHandler : RpcMethodHandler
    {
        private static readonly DamageTextHandler _instance = new DamageTextHandler();

        private DamageTextHandler() { }

        public static void Register()
        {
            RoutedRpcManager.AddHandler("DamageText", _instance);
        }

        public override bool Process(ZRoutedRpc.RoutedRPCData routedRpcData)
        {
            // Block - DamageText is client-only visual, no need to route
            return false;
        }
    }
}
