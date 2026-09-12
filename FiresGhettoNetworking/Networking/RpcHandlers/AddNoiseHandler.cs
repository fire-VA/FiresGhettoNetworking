namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Narrows RPC_AddNoise to the distance the noise actually carries. Vanilla broadcasts every footstep and
    /// swing to every peer; the RPC's own range argument is a far tighter bound than the global AoI radius,
    /// so it is read off the package and handed to the AoI filter as the radius for this call.
    /// </summary>
    public sealed class AddNoiseHandler : RpcMethodHandler
    {
        private const float AudibleRadiusMultiplier = 2f;
        private const int RangeArgumentBytes = 4;

        private static readonly AddNoiseHandler _instance = new AddNoiseHandler();

        private AddNoiseHandler() { }

        public static void Register()
        {
            RoutedRpcManager.AddHandler("AddNoise", _instance);
        }

        public override bool Process(ZRoutedRpc.RoutedRPCData routedRpcData)
        {
            ZPackage parameters = routedRpcData.m_parameters;
            if (parameters == null || parameters.Size() < RangeArgumentBytes)
                return true;

            int savedPos = parameters.GetPos();
            parameters.SetPos(0);
            float noiseRange = parameters.ReadSingle();
            parameters.SetPos(savedPos);

            RoutedRpcManager.SetAoIRadiusOverride(noiseRange * AudibleRadiusMultiplier);
            return true;
        }
    }
}
