namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// AoI-filters RPC_AddNoise to nearby peers only.
    ///
    /// RPC_AddNoise is fired every time a player or creature makes noise (footsteps,
    /// attacks, combat sounds). Vanilla broadcasts it to ALL peers. On a 100-player
    /// server this creates a constant wall of noise RPCs from every moving entity.
    ///
    /// Noise only matters to:
    ///   - The AI of nearby creatures (who then react to it)
    ///   - Clients close enough to actually care about the sound
    ///
    /// We let the AoI system handle distance filtering — this handler simply
    /// returns true to allow the RPC through (the RoutedRpcManager will then
    /// apply RouteRPCWithAoI using ConfigRpcAoIRadius).
    ///
    /// The real work is that noise has a TIGHT natural radius — vanilla's AddNoise
    /// takes a float `range` parameter. We enforce a hard cap so a noise event
    /// with range=1 doesn't get broadcast 256m away. We read the noise range from
    /// the RPC parameters and clamp the effective AoI to it.
    /// </summary>
    public sealed class AddNoiseHandler : RpcMethodHandler
    {
        private static readonly AddNoiseHandler _instance = new AddNoiseHandler();

        private AddNoiseHandler() { }

        public static void Register()
        {
            RoutedRpcManager.AddHandler("AddNoise", _instance);
        }

        public override bool Process(ZRoutedRpc.RoutedRPCData routedRpcData)
        {
            // Read the noise range from the RPC parameters (first float argument)
            // AddNoise(float range) — range is how far the noise propagates
            ZPackage parameters = routedRpcData.m_parameters;
            if (parameters == null || parameters.Size() < 4)
                return true; // Malformed — allow through, let vanilla handle

            int savedPos = parameters.GetPos();
            parameters.SetPos(0);
            float noiseRange = parameters.ReadSingle();
            parameters.SetPos(savedPos);

            // Store the effective AoI radius for this RPC in the RoutedRpcManager
            // by temporarily overriding it. Since we process one RPC at a time
            // (single-threaded), we can use a static override field.
            RoutedRpcManager.SetAoIRadiusOverride(noiseRange * 2f); // 2x range for safety margin
            return true; // Allow — AoI will filter at the radius we just set
        }
    }
}
