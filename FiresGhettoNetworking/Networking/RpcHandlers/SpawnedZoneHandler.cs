using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Narrows the server's SpawnedZone rebroadcast to peers near the zone. Vanilla re-sends every zone load
    /// to everybody, hundreds of RPCs per login wave; the zone id in the payload gives a world position for
    /// the AoI filter, and SetZoneGenerated has already run on the server before the filter applies.
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
            if (parameters == null || parameters.Size() < 8) // Vector2i = 2 x int32 = 8 bytes
                return true; // Malformed - allow through

            int savedPos = parameters.GetPos();
            parameters.SetPos(0);

            // SpawnedZone(Vector2i zoneID) - read the zone coords
            int zoneX = parameters.ReadInt();
            int zoneY = parameters.ReadInt();

            parameters.SetPos(savedPos);

            // Hand the zone's world position to RoutedRpcManager as the AoI centre for this broadcast.
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
    /// Single-threaded server - safe as static fields.
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
