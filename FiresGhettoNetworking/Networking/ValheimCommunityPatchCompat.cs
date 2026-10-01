using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// ValheimCommunityPatch replaces ZNetScene's object scheduling on every side: SpawnEventQueuePatch creates objects
    /// from its own queue and ZoneDiffRemovalPatch removes them from its own index, both around ZNet's reference position.
    /// Where FGN does the same job it stands down. FGN soft-depends on VCP, so VCP's patches are attached before Detect runs.
    /// </summary>
    public static class ValheimCommunityPatchCompat
    {
        public const string PluginGuid = "MidnightsFX.ValheimCommunityPatch";

        private const string TerrainRecoveryPatchType = "ValheimCommunityPatch.Patches.Terrain.TerrainCompNullHmapPatch";

        private static bool s_terrainRecoveryAttached;
        private static FieldInfo s_terrainRecoveryEnabled;

        public static bool SchedulesObjectCreation { get; private set; }
        public static bool SchedulesObjectRemoval { get; private set; }
        public static bool SchedulesSceneObjects => SchedulesObjectCreation || SchedulesObjectRemoval;

        /// <summary>True while VCP's terrain compiler recovery is attached and its 'Fix Terrain Compiler Init Race' is on.</summary>
        public static bool RecoversTerrainCompilers =>
            s_terrainRecoveryAttached
            && !(s_terrainRecoveryEnabled?.GetValue(null) is BepInEx.Configuration.ConfigEntry<bool> enabled && !enabled.Value);

        public static void Detect()
        {
            // Any VCP prefix on these methods counts, whatever its class is called: VCP 0.32 moved creation from
            // SpawnEventQueuePatch (CreateDestroyObjects) to SpawnQueueCachePatch (CreateObjectsSorted), and the old
            // class-name check missed it, so FGN's client time-slicing bypassed VCP's queue.
            SchedulesObjectCreation =
                HasPrefixFrom(AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateDestroyObjects", Type.EmptyTypes), null)
                || HasPrefixFrom(AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObjectsSorted"), null);
            SchedulesObjectRemoval = HasPrefixFrom(
                AccessTools.DeclaredMethod(typeof(ZNetScene), "RemoveObjects", new[] { typeof(List<ZDO>), typeof(List<ZDO>) }), null);
            s_terrainRecoveryAttached = HasPrefixFrom(
                AccessTools.DeclaredMethod(typeof(TerrainComp), "Update", Type.EmptyTypes), TerrainRecoveryPatchType);
            if (s_terrainRecoveryAttached)
            {
                Type recovery = AccessTools.TypeByName(TerrainRecoveryPatchType);
                s_terrainRecoveryEnabled = recovery != null ? AccessTools.Field(recovery, "Enabled") : null;
            }

            if (SchedulesSceneObjects)
                LoggerOptions.LogMessage(
                    "[VCP] ValheimCommunityPatch schedules object creation and removal; FGN leaves that to it. Instantiation "
                    + "time-slicing and the client destroy throttle stand down, and Server-Side Simulation stays off on a "
                    + "dedicated server.");
        }

        /// <summary>A VCP prefix on the method; with a type name, only one declared in that class.</summary>
        private static bool HasPrefixFrom(MethodBase target, string patchTypeName)
        {
            if (target == null) return false;
            HarmonyLib.Patches info = Harmony.GetPatchInfo(target);
            return info?.Prefixes != null
                && info.Prefixes.Any(prefix => prefix.owner == PluginGuid
                    && (patchTypeName == null || prefix.PatchMethod?.DeclaringType?.FullName == patchTypeName));
        }
    }
}
