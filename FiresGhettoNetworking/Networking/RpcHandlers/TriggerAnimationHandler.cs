namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Lets RPC_TriggerAnimation through to the AoI filter. Attacks, rolls, jumps and emotes are broadcast to
    /// every peer by vanilla, including those far enough away that the character model is not rendered.
    /// </summary>
    public sealed class TriggerAnimationHandler : RpcMethodHandler
    {
        private static readonly TriggerAnimationHandler _instance = new TriggerAnimationHandler();

        private TriggerAnimationHandler() { }

        public static void Register()
        {
            RoutedRpcManager.AddHandler("TriggerAnimation", _instance);
        }

        public override bool Process(ZRoutedRpc.RoutedRPCData routedRpcData)
        {
            // Allow - AoI routing in RoutedRpcManager will filter by distance
            return true;
        }
    }
}
