namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Base class for server-side RPC handlers.
    /// Handlers are registered with RoutedRpcManager and called for matching RPCs
    /// before they are routed to other clients.
    /// 
    /// Return true from Process() to allow the RPC to be forwarded.
    /// Return false to block/drop the RPC.
    /// 
    /// Based on BetterZeeRouter pattern from Comfy Valheim.
    /// </summary>
    public abstract class RpcMethodHandler
    {
        public abstract bool Process(ZRoutedRpc.RoutedRPCData routedRpcData);
    }
}
