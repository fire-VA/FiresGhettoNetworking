using System.Collections.Generic;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.56 ([fgn], lead 11-08 relaying Fire, log-proven on the IsoDedi in the BlumpkinPre town): with Server-Side Simulation on, the
    /// server pass instantiated EVERY object near a player, static building pieces included. 131,045 near objects; the instance count rose
    /// ~12.5k a minute (75,666 -> 94,371 in 90 s; top creates stone_wall_2x1 / wood_beam / grausten pillars). The server ran ~15 fps:
    /// filter 8.3 + create 15-16 + remove 6.3 ms a pass, ~6 passes a second. Working set 5.7 GB.
    ///
    /// The server never runs a building piece: only V3's simulated set (non-player Characters, ships with ship simulation on) is ever
    /// server-owned, in both ownership modes, so a piece's owner is always a player's game, exactly as on a vanilla dedicated server (which
    /// instantiates nothing). The one thing a piece instance gives the server is SOLIDITY for what it does simulate: floors, walls and docks
    /// for its creatures and ships (ground, pathing, targets).
    ///
    /// So a static piece (a prefab with WearNTear that is not a ship, cart or character) is instantiated on the server only NEAR something it
    /// simulates. Everything else (terrain, drops, spawners, locations, vegetation, creatures, ships, carts) is unchanged.
    /// 1.6.59 (cell 0 on 1.6.56, log-proven): 1.6.56's "near" was the 3x3 of 64 m zones around each simulated object; in the town 12 creatures
    /// made 39 zones, almost the whole town ("left as data 5440 a pass" yet instances 11k -> 59k). Now "near" is a 40 m radius around each
    /// simulated object and each kept / SSS test point (about 4 s of the fastest creature at the 10 Hz pass), kept as a set of 16 m cells:
    /// building the set is ~49 cells per object, a piece check is one hash lookup of its position's cell.
    /// Pieces built before (a live switch, or a creature walking away) are released gradually: at most 100 a pass are left out of the
    /// lists, so vanilla's RemoveObjects destroys their GameObjects only (pieces are persistent ZDOs; nothing is deleted).
    /// Key "Server Builds Pieces Only Where It Simulates" (a fix, ON). Stands down until the simulated set is built.
    /// ServerStream (the queue pass) uses the same cells through BeginCells / AddSimulated / InSimulatedArea / CellOf.
    /// </summary>
    internal static class ServerStaticPieces
    {
        private const int ReleasePerPass = 100;
        private const float ReportSeconds = 60f;
        internal const float RadiusMeters = 40f;
        private const float CellMeters = 16f;
        private static readonly int CellReach = Mathf.CeilToInt(RadiusMeters / CellMeters);
        // a cell is in when its centre is within the radius plus half a cell's diagonal, so every point within the radius is covered
        private static readonly float CellInSqr = (RadiusMeters + CellMeters * 0.7072f) * (RadiusMeters + CellMeters * 0.7072f);

        private static readonly Dictionary<int, bool> s_isStatic = new Dictionary<int, bool>();   // prefab hash -> static piece (bounded by the prefab count)
        private static readonly HashSet<long> s_simCells = new HashSet<long>();
        private static bool s_on, s_saidOn;
        private static int s_releasedThisPass;

        private static int p_passes, p_simObjects, p_simCellsMax, p_skipped, p_kept, p_released, p_inside;
        private static double p_buildMs;
        private static float p_next;

        internal static bool Enabled => FiresGhettoNetworkMod.ConfigServerPiecesOnlyWhereSimulated != null && FiresGhettoNetworkMod.ConfigServerPiecesOnlyWhereSimulated.Value
                                        && ServerOwnershipPatchesV3.IsBuilt;

        internal static long CellOf(Vector3 p) => ((long)Mathf.FloorToInt(p.x / CellMeters) << 32) ^ (uint)Mathf.FloorToInt(p.z / CellMeters);
        internal static bool InSimulatedArea(Vector3 p) => s_simCells.Contains(CellOf(p));
        internal static bool InSimulatedCell(long cell) => s_simCells.Contains(cell);

        /// <summary>Start a pass's simulated area (kept / test points included). False = the rule is off this pass.</summary>
        internal static bool BeginCells(List<Vector3> keptPoints, List<Vector3> testPoints)
        {
            s_on = Enabled;
            if (!s_on) return false;
            if (!s_saidOn) { s_saidOn = true; LoggerOptions.LogInfo($"[SSS] the server builds pieces only within {RadiusMeters:0} m of what it simulates (creatures, ships, kept areas); elsewhere pieces stay data only, as on a vanilla server (Server Builds Pieces Only Where It Simulates)"); }
            s_simCells.Clear(); s_releasedThisPass = 0;
            foreach (Vector3 p in keptPoints) AddSimulated(p);
            foreach (Vector3 p in testPoints) AddSimulated(p);
            return true;
        }

        internal static void AddSimulated(Vector3 p)
        {
            int cx = Mathf.FloorToInt(p.x / CellMeters), cz = Mathf.FloorToInt(p.z / CellMeters);
            for (int dx = -CellReach; dx <= CellReach; dx++)
                for (int dz = -CellReach; dz <= CellReach; dz++)
                {
                    float x = (cx + dx + 0.5f) * CellMeters - p.x, z = (cz + dz + 0.5f) * CellMeters - p.z;
                    if (x * x + z * z <= CellInSqr) s_simCells.Add(((long)(cx + dx) << 32) ^ (uint)(cz + dz));
                }
        }

        /// <summary>The full pass (1.6.56 path): the simulated area from this pass's near list and the kept / test points.</summary>
        internal static void BeginPass(List<ZDO> near, List<Vector3> keptPoints, List<Vector3> testPoints)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!BeginCells(keptPoints, testPoints)) return;
            int sim = 0;
            for (int i = 0; i < near.Count; i++)
            {
                ZDO zdo = near[i];
                if (zdo == null || !ServerOwnershipPatchesV3.IsSimulatedPrefab(zdo.m_prefab)) continue;
                sim++;
                AddSimulated(zdo.GetPosition());
            }
            EndCells(sim, t0);
        }

        /// <summary>Proof counters for the area built this pass.</summary>
        internal static void EndCells(int simObjects, long startTicks)
        {
            p_passes++; p_simObjects += simObjects; if (s_simCells.Count > p_simCellsMax) p_simCellsMax = s_simCells.Count;
            p_buildMs += (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>The full pass: true when this near ZDO stays out of the server's lists this pass (not created; or, with an instance, released).</summary>
        internal static bool LeaveOut(ZDO zdo, ZNetScene scene)
        {
            if (!s_on) return false;
            if (!IsStaticPiece(zdo.m_prefab, scene)) return false;
            if (InSimulatedArea(zdo.GetPosition())) { p_inside++; return false; }
            if (scene != null && scene.HaveInstance(zdo)) return Release();
            p_skipped++;
            return true;
        }

        /// <summary>One more built piece outside every simulated area may be released this pass (at most 100, never all at once).</summary>
        internal static bool Release()
        {
            if (s_releasedThisPass >= ReleasePerPass) { p_kept++; return false; }
            s_releasedThisPass++; p_released++;
            return true;
        }

        internal static void ResetReleases() { s_releasedThisPass = 0; }   // 1.6.60: the queue pass's removal runs on its own clock

        /// <summary>The queue pass counts the pieces it parks and builds here, for the same proof line.</summary>
        internal static void CountParked() => p_skipped++;
        internal static void CountInside() => p_inside++;

        internal static bool IsStaticPiece(int prefabHash, ZNetScene scene)
        {
            if (s_isStatic.TryGetValue(prefabHash, out bool v)) return v;
            v = false;
            GameObject prefab = scene != null ? scene.GetPrefab(prefabHash) : null;
            if (prefab != null && prefab.GetComponent<WearNTear>() != null && !ServerOwnershipPatchesV3.IsSimulatedPrefab(prefabHash)
                && prefab.GetComponent<Ship>() == null && prefab.GetComponent<Vagon>() == null && prefab.GetComponent<Character>() == null)
                v = true;
            s_isStatic[prefabHash] = v;
            return v;
        }

        /// <summary>Debug, once a minute.</summary>
        internal static void Report(int instances)
        {
            if (!s_on || !LoggerOptions.DebugEnabled) return;
            float now = Time.unscaledTime; if (now < p_next) return;
            if (p_next > 0f && p_passes > 0)
                LoggerOptions.LogDebug($"[SSS] pieces where simulated, last {ReportSeconds:0} s over {p_passes} passes: simulated area up to {p_simCellsMax} cell(s) of {CellMeters:0} m ({RadiusMeters:0} m around {p_simObjects / p_passes} simulated object(s) a pass), " +
                    $"building pieces left as data {p_skipped} (counted per pass looked at), inside the area {p_inside}, released {p_released} (at most {ReleasePerPass} a pass; {p_kept} waits), " +
                    $"area build {p_buildMs / p_passes:0.00} ms a pass; server instances now {instances}");
            p_next = now + ReportSeconds;
            p_passes = p_simObjects = p_simCellsMax = p_skipped = p_kept = p_released = p_inside = 0; p_buildMs = 0;
        }
    }
}
