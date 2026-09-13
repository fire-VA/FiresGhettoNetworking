using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// A terrain compiler that wakes before its zone's heightmap exists logs "Terrain compiler could not find hmap" and
    /// skips its whole setup: it is never registered, never listens for terrain operations and never initialises, so
    /// terrain edits in that zone go nowhere until it reloads. Once the heightmap exists this finishes what Awake skipped,
    /// in Awake's order. Stands down while ValheimCommunityPatch's own recovery is active.
    /// </summary>
    [HarmonyPatch(typeof(TerrainComp), "Update")]
    public static class TerrainCompInitRace
    {
        private static AccessTools.FieldRef<TerrainComp, Heightmap> s_hmap;
        private static AccessTools.FieldRef<TerrainComp, ZNetView> s_nview;
        private static List<TerrainComp> s_instances;
        private static MethodInfo s_initialize;
        private static MethodInfo s_rpcApplyOperation;

        private static bool Prepare()
        {
            s_hmap = AccessTools.FieldRefAccess<TerrainComp, Heightmap>("m_hmap");
            s_nview = AccessTools.FieldRefAccess<TerrainComp, ZNetView>("m_nview");
            s_instances = AccessTools.Field(typeof(TerrainComp), "s_instances")?.GetValue(null) as List<TerrainComp>;
            s_initialize = AccessTools.DeclaredMethod(typeof(TerrainComp), "Initialize", Type.EmptyTypes);
            s_rpcApplyOperation = AccessTools.DeclaredMethod(typeof(TerrainComp), "RPC_ApplyOperation", new[] { typeof(long), typeof(ZPackage) });
            if (s_instances != null && s_initialize != null && s_rpcApplyOperation != null) return true;
            LoggerOptions.LogWarning("[TerrainComp] TerrainComp's setup members were not found on this game version; late terrain compilers are left to vanilla.");
            return false;
        }

        [HarmonyPrefix]
        private static bool Prefix(TerrainComp __instance)
        {
            if (s_hmap(__instance) != null) return true;
            if (ValheimCommunityPatchCompat.RecoversTerrainCompilers) return true;

            ZNetView nview = s_nview(__instance);
            if (nview == null || !nview.IsValid()) return false;
            Vector3 position = __instance.transform.position;
            Heightmap hmap = Heightmap.FindHeightmap(position);
            if (hmap == null) return false;

            s_hmap(__instance) = hmap;
            TerrainComp other = TerrainComp.FindTerrainCompiler(position);
            if (other != null && other != __instance && ZNetScene.instance != null)
            {
                LoggerOptions.LogWarning($"[TerrainComp] Found another terrain compiler in the zone at {position}; removing it, as vanilla does when a compiler starts.");
                ZNetScene.instance.Destroy(other.gameObject);
            }
            if (!s_instances.Contains(__instance)) s_instances.Add(__instance);
            nview.Register("RPC_ApplyOperation",
                (Action<long, ZPackage>)Delegate.CreateDelegate(typeof(Action<long, ZPackage>), __instance, s_rpcApplyOperation));
            s_initialize.Invoke(__instance, null);
            LoggerOptions.LogInfo($"[TerrainComp] The terrain compiler at {position} started before its zone's heightmap existed; finished its setup now that the heightmap has loaded.");
            return true;
        }
    }
}
