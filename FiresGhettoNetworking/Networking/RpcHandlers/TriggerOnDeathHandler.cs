namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Lets RPC_OnDeath through to the AoI filter. Death effects are ragdolls and particles on a character
    /// the distant peer is not rendering; the minimap death pin is added locally in Player.OnDeath and does
    /// not travel on this RPC.
    /// </summary>
    public sealed class TriggerOnDeathHandler : RpcMethodHandler
    {
        private static readonly TriggerOnDeathHandler _instance = new TriggerOnDeathHandler();

        private TriggerOnDeathHandler() { }

        public static void Register()
        {
            RoutedRpcManager.AddHandler("OnDeath", _instance);
        }

        public override bool Process(ZRoutedRpc.RoutedRPCData routedRpcData)
        {
            return true;
        }
    }
}
