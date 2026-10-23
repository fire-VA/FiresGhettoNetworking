using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Stops NullReferenceExceptions in SpawnSystem.UpdateSpawning during teleports and zone loads. Vanilla's UpdateSpawning, on the game that owns the zone and has a local player (a client, or a host), reads
    /// m_heightmap.m_cornerAltBiomes with no null check. Awake leaves m_heightmap null when the zone has no vanilla heightmap yet (FAT
    /// voxel terrain, a zone still loading). This repairs it the way FGN's dedicated path does, and skips only a tick that would throw.
    /// ALWAYS attached (not in MonsterAIPatches, which only a dedicated server with Server-Side Simulation attaches, so it
    /// would never run on a client). One Info line when it attaches; Debug once a minute while ticks are skipped; a Finalizer names the
    /// culprit if UpdateSpawning still throws (the exception is passed on, not swallowed).
    /// </summary>
    [HarmonyPatch(typeof(SpawnSystem), "UpdateSpawning")]
    internal static class SpawnHeightmapGuard
    {
        private const float LogIntervalSeconds = 60f;

        private static readonly AccessTools.FieldRef<SpawnSystem, Heightmap> s_hmap = AccessTools.FieldRefAccess<SpawnSystem, Heightmap>("m_heightmap");
        private static readonly AccessTools.FieldRef<SpawnSystem, ZNetView> s_nview = AccessTools.FieldRefAccess<SpawnSystem, ZNetView>("m_nview");
        private static float s_nextSkipLog, s_nextThrowLog;
        private static int s_skipped;

        private static bool Prepare(MethodBase original)
        {
            if (original == null) LoggerOptions.LogInfo("[Spawn] heightmap guard on SpawnSystem.UpdateSpawning attached (client, host and server)");
            return true;
        }

        // Vanilla only runs its body with a local player that owns the zone; anything else is left to vanilla untouched.
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool Prefix(SpawnSystem __instance)
        {
            if (Player.m_localPlayer == null || __instance == null || s_hmap(__instance) != null) return true;
            ZNetView nview = s_nview(__instance);
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return true;
            Heightmap hmap = Heightmap.FindHeightmap(__instance.transform.position);
            if (hmap != null) { s_hmap(__instance) = hmap; return true; }
            s_skipped++;
            if (Time.time >= s_nextSkipLog)
            {
                s_nextSkipLog = Time.time + LogIntervalSeconds;
                if (LoggerOptions.DebugEnabled)
                    LoggerOptions.LogDebug($"[Spawn] {s_skipped} spawn tick(s) skipped: the zone at {__instance.transform.position:F0} has no vanilla heightmap yet (vanilla would throw)");
                s_skipped = 0;
            }
            return false;
        }

        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception, SpawnSystem __instance)
        {
            if (__exception == null || Time.time < s_nextThrowLog) return __exception;
            s_nextThrowLog = Time.time + LogIntervalSeconds;
            try
            {
                var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(SpawnSystem), "UpdateSpawning"));
                string owners = info == null ? "none" : string.Join(", ", info.Owners);
                ZNetView nview = __instance != null ? s_nview(__instance) : null;
                Vector3 pos = __instance != null ? __instance.transform.position : Vector3.zero;
                LoggerOptions.LogWarning($"[Spawn] UpdateSpawning threw {__exception.GetType().Name} at {pos:F0} (zone {ZoneSystem.GetZone(pos)}); " +
                    $"m_nview valid {(nview != null && nview.IsValid())}, m_heightmap {(__instance != null && s_hmap(__instance) != null ? "set" : "NULL")}; " +
                    $"patch owners: {owners}; stack: {__exception.StackTrace}");
            }
            catch { }
            return __exception;
        }
    }
}
