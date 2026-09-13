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

        private const string SpawnQueuePatchType = "ValheimCommunityPatch.Patches.Performance.SpawnEventQueuePatch";
        private const string UnloadPatchType = "ValheimCommunityPatch.Patches.Performance.ZoneDiffRemovalPatch";

        public static bool SchedulesObjectCreation { get; private set; }
        public static bool SchedulesObjectRemoval { get; private set; }
        public static bool SchedulesSceneObjects => SchedulesObjectCreation || SchedulesObjectRemoval;

        public static void Detect()
        {
            SchedulesObjectCreation = HasPrefixFrom(
                AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateDestroyObjects", Type.EmptyTypes), SpawnQueuePatchType);
            SchedulesObjectRemoval = HasPrefixFrom(
                AccessTools.DeclaredMethod(typeof(ZNetScene), "RemoveObjects", new[] { typeof(List<ZDO>), typeof(List<ZDO>) }), UnloadPatchType);

            if (SchedulesSceneObjects)
                LoggerOptions.LogMessage(
                    "[VCP] ValheimCommunityPatch schedules object creation and removal; FGN leaves that to it. Instantiation "
                    + "time-slicing and the client destroy throttle stand down, and Server-Side Simulation stays off on a "
                    + "dedicated server.");
        }

        private static bool HasPrefixFrom(MethodBase target, string patchTypeName)
        {
            if (target == null) return false;
            HarmonyLib.Patches info = Harmony.GetPatchInfo(target);
            return info?.Prefixes != null
                && info.Prefixes.Any(prefix => prefix.owner == PluginGuid
                    && prefix.PatchMethod?.DeclaringType?.FullName == patchTypeName);
        }
    }
}
