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
        private static bool s_classicLogged;

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
            // 1.6.31: "true" was only right inside a square sweep. Keep-alive, ownership and active-area callers ask with no sweep, so
            // with vanilla's default classic distance every zone in the world counted as covered. Now the square itself.
            if (Synced().IsClassic)
            {
                if (!s_classicLogged)
                {
                    s_classicLogged = true;
                    LoggerOptions.LogDebug("[SimDistance] classic simulation distance: zone tests use the square area (x and y each within the radius)");
                }
                Vector2s c = FgnRing.On ? FgnRing.Delta(centre, zone) : new Vector2s(zone.x - centre.x, zone.y - centre.y);
                return Mathf.Abs(c.x) <= radius && Mathf.Abs(c.y) <= radius;
            }
            // 1.5.78 halo ring: object sectors around the ring wrap at the seam; vanilla's ZonesWithinRadius (id * 64 distance)
            // would call two neighbours either side of it 469 zones apart. Same radius rule on the wrapped delta.
            if (FgnRing.On)
            {
                Vector2s d = FgnRing.Delta(centre, zone);
                float size = ZoneSystem.instance.m_zoneSize;
                float limit = radius * size + (ghostZone ? size * 0.8f : size * 0.5f);
                return (float)(d.x * d.x + d.y * d.y) * size * size < limit * limit;
            }
            return ZoneSystem.instance.ZonesWithinRadius(centre, zone, radius, ghostZone);
        }

        /// <summary>Is a world point within <paramref name="radius"/> zones of <paramref name="centre"/>?</summary>
        internal static bool PointInRadius(Vector3 point, Vector2s centre, int radius)
            => ZoneInRadius(centre, FgnRing.Sector(point), radius);
    }
}
