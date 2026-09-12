namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Drops RPC_WNTHealthChanged. Piece health already travels in the ZDO, and clients read it when they
    /// need it; the RPC is a redundant visual notice that a raid turns into hundreds of broadcasts a second.
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
            // Drop - WearNTear health is synced via ZDO, RPC is redundant
            return false;
        }
    }
}
