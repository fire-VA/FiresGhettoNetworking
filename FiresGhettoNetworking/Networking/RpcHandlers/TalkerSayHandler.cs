namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// AoI-filters RPC_Say (proximity chat) to nearby peers only.
    ///
    /// Vanilla broadcasts ALL chat types to ALL connected clients.
    /// On a 100-player server this means every "say" message goes to
    /// every player regardless of distance — players on opposite sides
    /// of the map get chat from players they will never see or interact with.
    ///
    /// RPC_Say   = proximity chat (character speaks above head, short range ~70m)
    /// RPC_Shout = global shout — intentionally world-wide, do NOT filter
    /// RPC_Whisper = private/whisper — intentionally short range, but targeted
    ///
    /// We only AoI-filter RPC_Say using the standard AoI radius.
    /// Shout and Whisper are not registered here and remain vanilla.
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
            // Allow — AoI routing in RoutedRpcManager will filter by distance.
            // Vanilla Say range is ~70m. We use ConfigRpcAoIRadius (default 256m)
            // which is intentionally generous so nearby players don't miss chat.
            return true;
        }
    }
}
