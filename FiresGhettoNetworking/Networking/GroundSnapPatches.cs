using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>Layer mask for "something you can stand on", shared by the ground-snap patches and the dedicated-server fell-out rescue.</summary>
    public static class SolidSurface
    {
        public const string DefaultLayerCsv = "Default,static_solid,Default_small,piece,terrain,vehicle";

        private static int _cachedMask;
        private static string _cachedLayerCsv;

        public static int Mask()
        {
            string layerCsv = FiresGhettoNetworkMod.ConfigDediFellOutRescueLayers?.Value ?? DefaultLayerCsv;
            if (_cachedLayerCsv == layerCsv && _cachedMask != 0) return _cachedMask;

            int mask = ParseLayerMask(layerCsv);
            if (mask == 0)
            {
                LoggerOptions.LogWarning($"[SolidSurface] '{layerCsv}' matched no layers in this build; falling back to terrain only.");
                mask = LayerMask.GetMask("terrain");
            }

            _cachedMask = mask;
            _cachedLayerCsv = layerCsv;
            return mask;
        }

        private static int ParseLayerMask(string layerCsv)
        {
            var layerNames = new List<string>();
            foreach (var entry in layerCsv.Split(','))
            {
                string layerName = entry?.Trim();
                if (!string.IsNullOrEmpty(layerName)) layerNames.Add(layerName);
            }
            return layerNames.Count > 0 ? LayerMask.GetMask(layerNames.ToArray()) : 0;
        }
    }

    /// <summary>
    /// Vanilla's "fallen out of the world" rescues measure against the heightmap, which cannot see build
    /// pieces, so anything resting on a piece below heightmap level is teleported to the dirt. Each rescue
    /// asks for the ground height once; these transpilers redirect that one call to a physics probe.
    /// </summary>
    [HarmonyPatch]
    public static class GroundSnapPatches
    {
        private const float ProbeStartHeightAboveObject = 2f;
        private const float ProbeMaxDistance = 8f;

        [HarmonyPatch(typeof(Character), "UnderWorldCheck")]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Character_UnderWorldCheck_UseSupportProbe(
            IEnumerable<CodeInstruction> instructions)
        {
            return RedirectGroundHeightToSupportProbe(instructions, "Character.UnderWorldCheck");
        }

        [HarmonyPatch(typeof(TombStone), "PositionCheck")]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> TombStone_PositionCheck_UseSupportProbe(
            IEnumerable<CodeInstruction> instructions)
        {
            return RedirectGroundHeightToSupportProbe(instructions, "TombStone.PositionCheck");
        }

        [HarmonyPatch(typeof(ItemDrop), "TerrainCheck")]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> ItemDrop_TerrainCheck_UseSupportProbe(
            IEnumerable<CodeInstruction> instructions)
        {
            return RedirectGroundHeightToSupportProbe(instructions, "ItemDrop.TerrainCheck");
        }

        [HarmonyPatch(typeof(Floating), "TerrainCheck")]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Floating_TerrainCheck_UseSupportProbe(
            IEnumerable<CodeInstruction> instructions)
        {
            return RedirectGroundHeightToSupportProbe(instructions, "Floating.TerrainCheck");
        }

        private static IEnumerable<CodeInstruction> RedirectGroundHeightToSupportProbe(
            IEnumerable<CodeInstruction> instructions, string patchedMethod)
        {
            var code = new List<CodeInstruction>(instructions);
            var supportProbe = AccessTools.Method(typeof(GroundSnapPatches), nameof(SupportOrGroundHeight));
            int redirected = 0;

            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Call && code[i].opcode != OpCodes.Callvirt) continue;

                var called = code[i].operand as MethodInfo;
                if (called == null || called.Name != "GetGroundHeight" || called.GetParameters().Length != 1) continue;

                code[i] = new CodeInstruction(OpCodes.Call, supportProbe);
                redirected++;
            }

            if (redirected == 0)
                LoggerOptions.LogWarning(
                    $"[GroundSnap] {patchedMethod} has no GetGroundHeight call to redirect; vanilla behaviour stands there.");

            return code;
        }

        /// <summary>Stands in for ZoneSystem.GetGroundHeight(Vector3) at the redirected call sites, so the signature must match it.</summary>
        public static float SupportOrGroundHeight(ZoneSystem zoneSystem, Vector3 point)
        {
            float heightmapHeight = zoneSystem.GetGroundHeight(point);

            if (!(FiresGhettoNetworkMod.ConfigFixGroundSnapThroughFloors?.Value ?? true)) return heightmapHeight;
            if (point.y >= heightmapHeight) return heightmapHeight;

            RaycastHit hit;
            if (Physics.Raycast(point + Vector3.up * ProbeStartHeightAboveObject, Vector3.down, out hit,
                                ProbeMaxDistance, SolidSurface.Mask(), QueryTriggerInteraction.Ignore))
                return hit.point.y;

            return heightmapHeight;
        }
    }
}
