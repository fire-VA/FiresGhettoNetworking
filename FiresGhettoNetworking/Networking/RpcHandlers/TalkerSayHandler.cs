namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// AoI-filters RPC_Say, the ~70m proximity chat vanilla broadcasts to every peer. Shout and Whisper are
    /// deliberately left alone: one is meant to be world-wide, the other is already targeted.
    /// </summary>
    public sealed class TalkerSayHandler : RpcMethodHandler
    {
        private static readonly TalkerSayHandler _instance = new TalkerSayHandler();

        private TalkerSayHandler() { }

        public static void Register()
        {
            RoutedRpcManager.AddHandler("Say", _instance);
        }

        public override bool Process(ZRoutedRpc.RoutedRPCData routedRpcData)
        {
            // Allow - AoI routing in RoutedRpcManager will filter by distance.
            // Vanilla Say range is ~70m. We use ConfigRpcAoIRadius (default 256m)
            // which is intentionally generous so nearby players don't miss chat.
            return true;
        }
    }
}
