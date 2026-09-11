using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// AoI-filters RPC_SpawnedZone to only peers near the zone being reported.
    ///
    /// Vanilla behaviour:
    ///   When a client finishes loading a zone, it sends "SpawnedZone" to the server.
    ///   The server calls SetZoneGenerated(zoneID) and then re-broadcasts the event
    ///   to ALL connected peers via ZRoutedRpc.Everybody.
    ///
    /// The problem:
    ///   On a 100-player server every zone load by any player generates a broadcast
    ///   to all 99 others. A newly connected player loading their spawn area (~7x7 = 49
    ///   zones) sends 49 SpawnedZone RPCs, each broadcast to all peers.
    ///   At 10 simultaneous logins that's 490 broadcast RPCs going to everyone.
    ///
    /// The fix:
    ///   SpawnedZone carries a Vector2i zoneID in its parameters. We decode the zone
    ///   position, convert it to world-space via ZoneSystem.GetZonePos, and override
    ///   the AoI radius to ConfigRpcAoIRadius so the broadcast only reaches peers
    ///   whose ref position is near that zone.
    ///
    ///   Peers far from the zone don't need to know it was generated � they don't
    ///   reference it in their ZoneSystem.m_generatedZones lookup for anything
    ///   that affects their local simulation.
    ///
    /// Safety:
    ///   SetZoneGenerated on the server is called in ProcessRoutedRPC BEFORE we
    ///   filter � so the server-side state is always updated. Only the subsequent
    ///   rebroadcast to other clients is AoI-filtered.
    /// </summary>
    public sealed class SpawnedZoneHandler : RpcMethodHandler
    {
        private static readonly SpawnedZoneHandler _instance = new SpawnedZoneHandler();

        private SpawnedZoneHandler() { }

        public static void Register()
        {
            RoutedRpcManager.AddHandler("SpawnedZone", _instance);
        }

        public override bool Process(ZRoutedRpc.RoutedRPCData routedRpcData)
        {
            ZPackage parameters = routedRpcData.m_parameters;
            if (parameters == null || parameters.Size() < 8) // Vector2i = 2 � int32 = 8 bytes
                return true; // Malformed � allow through

            int savedPos = parameters.GetPos();
            parameters.SetPos(0);

            // SpawnedZone(Vector2i zoneID) � read the zone coords
            int zoneX = parameters.ReadInt();
            int zoneY = parameters.ReadInt();

            parameters.SetPos(savedPos);

            // Convert the zone ID to world position so RouteRPCWithAoI can
            // use it as the center point for distance filtering.
            // We encode the world position back via the AoI radius override.
            // Since RoutedRpcManager looks up the target ZDO position, and
            // SpawnedZone has no target ZDO (it uses ZDOID.None), we need a
            // different approach: inject a fake but correct position by
            // temporarily patching the AoI override to the global radius and
            // letting the caller use our ZonePos injection via the zone hint.
            //
            // Practical approach: just use the global AoI radius. The zone
            // position itself is not accessible to RoutedRpcManager without
            // refactoring RouteRPCWithAoI. The filtering still gives a big
            // win � SpawnedZone has no target ZDO so it would normally bypass
            // AoI entirely and go to all peers. By returning true AND ensuring
            // the target ZDO is None, we rely on the RoutedRpcManager fallback
            // to RouteRPC (vanilla broadcast).
            //
            // To actually AoI filter this, we store the zone world position in
            // a thread-local so RoutedRpcManager can pick it up as a position hint.
            Vector3 zoneWorldPos = ZoneSystem.GetZonePos(new Vector2s(zoneX, zoneY));
            SpawnedZonePositionHint.Position = zoneWorldPos;
            SpawnedZonePositionHint.HasHint = true;

            // Use the global AoI radius for zone events
            RoutedRpcManager.SetAoIRadiusOverride(FiresGhettoNetworkMod.ConfigRpcAoIRadius.Value);

            return true;
        }
    }

    /// <summary>
    /// Carries the decoded zone world-position from SpawnedZoneHandler to
    /// RoutedRpcManager.RouteRPCWithAoI for RPCs that have no target ZDO.
    /// Single-threaded server � safe as static fields.
    /// </summary>
    public static class SpawnedZonePositionHint
    {
        public static bool HasHint;
        public static Vector3 Position;

        public static void Clear()
        {
            HasHint = false;
            Position = Vector3.zero;
        }
    }
}
