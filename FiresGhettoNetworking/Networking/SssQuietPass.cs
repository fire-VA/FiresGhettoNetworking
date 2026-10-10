using System.Collections.Generic;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.5.47, a DRY RUN (it changes nothing): how many of the SSS create/destroy passes (~490 a minute with two players) found the
    /// world exactly as the pass before left it, so they could have been skipped. A pass counts as quiet when, since the previous
    /// pass ended:
    ///   - no ZDO was added to or removed from any sector (ZDOMan.AddToSector / RemoveFromSector: every create, destroy, sector
    ///     crossing, and ZDOs arriving from clients),
    ///   - the loaded-zone count is the same (a zone loading lets waiting near objects through the filter),
    ///   - every source (ready peer, SSS test area) is in the same zone, in the same order,
    ///   - and the previous pass created and removed no instance (a creation backlog keeps passes busy).
    /// The count goes into the per-minute "[SSS] server pass" Debug line. If it is high while players stand still, a later build
    /// can skip those passes; until then nothing is skipped. The hooks sit in the SSS patch set, so they exist only with SSS on.
    /// </summary>
    [HarmonyPatch]
    internal static class SssQuietPass
    {
        private static bool s_touched = true;
        private static int s_lastZoneCount = -1;
        private static int s_lastCreated = -1, s_lastRemoved = -1;
        private static readonly List<Vector2s> s_lastSources = new List<Vector2s>(8);
        private static int s_quiet, s_passes;

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.AddToSector)), HarmonyPostfix]
        private static void Added() => s_touched = true;

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.RemoveFromSector)), HarmonyPostfix]
        private static void Removed() => s_touched = true;

        /// <summary>Before the pass changes anything: was everything as the previous pass left it?</summary>
        internal static bool WasQuiet(List<Vector2s> sources)
        {
            int zones = ZoneSystem.instance != null ? ZoneSystem.instance.m_zones.Count : -1;
            bool same = !s_touched && zones == s_lastZoneCount && s_lastCreated == 0 && s_lastRemoved == 0 && sources.Count == s_lastSources.Count;
            for (int i = 0; same && i < sources.Count; i++)
                same = sources[i].x == s_lastSources[i].x && sources[i].y == s_lastSources[i].y;
            s_lastSources.Clear();
            s_lastSources.AddRange(sources);
            s_lastZoneCount = zones;
            return same;
        }

        /// <summary>After create and remove: what this pass did, for the next pass's check. The flag is cleared here, so anything this
        /// pass caused (a spawner creating ZDOs while its object was instantiated) counts against the next one.</summary>
        internal static void PassDone(bool quiet, int created, int removed)
        {
            s_passes++;
            if (quiet) s_quiet++;
            s_lastCreated = created;
            s_lastRemoved = removed;
            s_touched = false;
        }

        /// <summary>For the per-minute line: "Q of P passes were quiet"; the counters restart.</summary>
        internal static string TakeSummary()
        {
            string line = s_passes > 0 ? $"{s_quiet} of {s_passes} passes found nothing changed since the one before (dry run: skippable)" : "no passes";
            s_quiet = s_passes = 0;
            return line;
        }
    }
}
