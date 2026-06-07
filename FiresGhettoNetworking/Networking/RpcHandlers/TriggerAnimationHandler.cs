namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// AoI-filters RPC_TriggerAnimation to nearby peers only.
    ///
    /// RPC_TriggerAnimation is sent every time a character plays an animation trigger
    /// (attacks, rolls, jumps, emotes, stagger, etc.). Vanilla broadcasts every one
    /// of these to ALL connected clients.
    ///
    /// On a 100-player server in combat, this generates hundreds of animation RPCs
    /// per second being sent to players who are nowhere near the action and whose
    /// clients will never render the animation (character not in view distance).
    ///
    /// We return true to allow the RPC — the RoutedRpcManager AoI system handles
    /// the distance filtering using ConfigRpcAoIRadius. Animations beyond that
    /// radius are never seen anyway (character models aren't rendered that far).
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
            // Allow — AoI routing in RoutedRpcManager will filter by distance
            return true;
        }
    }
}
