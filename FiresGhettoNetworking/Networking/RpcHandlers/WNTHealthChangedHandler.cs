namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Drops RPC_WNTHealthChanged RPCs from being routed to other clients.
    ///
    /// WearNTear (building piece) health changes are synced via ZDO s_health.
    /// The RPC is a redundant visual notification — on a busy server with players
    /// fighting or raiding, every hit on every structure broadcasts this to ALL
    /// clients. During a raid with 20 players attacking a base, this can generate
    /// hundreds of these RPCs per second.
    ///
    /// Clients read piece health from the ZDO when they need it (on hover, on damage
    /// visual). Dropping this RPC has no gameplay impact.
    /// </summary>
    public sealed class WNTHealthChangedHandler : RpcMethodHandler
    {
        private static readonly WNTHealthChangedHandler _instance = new WNTHealthChangedHandler();

        private WNTHealthChangedHandler() { }

        public static void Register()
        {
            RoutedRpcManager.AddHandler("RPC_WNTHealthChanged", _instance);
        }

        public override bool Process(ZRoutedRpc.RoutedRPCData routedRpcData)
        {
            // Drop — WearNTear health is synced via ZDO, RPC is redundant
            return false;
        }
    }
}
