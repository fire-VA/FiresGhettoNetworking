using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Single answer to "how far does the world simulate, and is this zone inside it". Valheim 1.0 replaced
    /// ZoneSystem.m_activeArea / m_activeDistantArea with the server-synced SimulationDistance struct
    /// (Near / Far / Total) and made the active area radial unless IsClassic, so a square sweep over-counts
    /// the corners and every candidate zone goes through ZoneSystem.ZonesWithinRadius. FGN's extended zone
    /// radius widens the near band, which widens Total with it.
    /// </summary>
    internal static class SimDistance
    {
        // Only reached before ZNet exists (very early boot). Vanilla's own baseline is
        // SimulationDistance.OriginalDistance = (2, 2, classic).
        private const int FallbackNear = 2;
        private const int FallbackFar = 2;

        internal static SimulationDistance Synced()
        {
            return ZNet.instance != null
                ? ZNet.instance.GetSyncedSimulationDistance()
                : new SimulationDistance(FallbackNear, FallbackFar, true);
        }

        /// <summary>Near band — the old m_activeArea.</summary>
        internal static int Near() => Synced().NearSimulationDistance;

        /// <summary>Near + far — the old m_activeDistantArea.</summary>
        internal static int Total() => Synced().TotalSimulationDistance;

        /// <summary>
        /// The synced distance widened by FGN's extended-zone-radius. Widening NEAR also widens
        /// Total, so both the simulated and distant bands grow — the same shape as the old code
        /// adding the extra radius to m_activeArea and m_activeDistantArea alike.
        /// </summary>
        internal static SimulationDistance Widened(int extraRadius)
        {
            SimulationDistance d = Synced();
            if (extraRadius <= 0) return d;
            return new SimulationDistance(
                d.NearSimulationDistance + extraRadius,
                d.FarSimulationDistance,
                d.IsClassic);
        }

        /// <summary>
        /// Is <paramref name="zone"/> inside <paramref name="radius"/> zones of <paramref name="centre"/>,
        /// honouring vanilla's radial rule? Classic mode keeps the square area.
        /// </summary>
        internal static bool ZoneInRadius(Vector2s centre, Vector2s zone, int radius, bool ghostZone = false)
        {
            if (ZoneSystem.instance == null) return true;
            if (Synced().IsClassic) return true;
            return ZoneSystem.instance.ZonesWithinRadius(centre, zone, radius, ghostZone);
        }

        /// <summary>Is a world point within <paramref name="radius"/> zones of <paramref name="centre"/>?</summary>
        internal static bool PointInRadius(Vector3 point, Vector2s centre, int radius)
            => ZoneInRadius(centre, ZoneSystem.GetZone(point), radius);
    }
}
