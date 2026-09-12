using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Traces ItemDrop and TombStone spawns for the "falls through a structure on zone reload" report. Awake
    /// is too early to sample - position and ownership are not set yet - so a coroutine waits for the object
    /// to settle, records the real spawn state, then re-checks a few seconds later. SPAWN lines with
    /// beneath=NONE mean nothing was under it; a following FELL line means it actually dropped.
    /// </summary>
    [HarmonyPatch]
    public static class FallThroughProbe
    {
        private static int s_groundMask;
        private static bool s_maskReady;

        private static int GroundMask()
        {
            if (!s_maskReady)
            {
                s_groundMask = SolidSurface.Mask();
                s_maskReady = true;
            }
            return s_groundMask;
        }

        [HarmonyPatch(typeof(ItemDrop), "Awake")]
        [HarmonyPostfix]
        public static void ItemDrop_Awake_Probe(ItemDrop __instance)
        {
            Begin(__instance != null ? __instance.gameObject : null, "ItemDrop");
        }

        [HarmonyPatch(typeof(TombStone), "Awake")]
        [HarmonyPostfix]
        public static void TombStone_Awake_Probe(TombStone __instance)
        {
            Begin(__instance != null ? __instance.gameObject : null, "TombStone");
        }

        private static void Begin(GameObject go, string kind)
        {
            if (go == null || FiresGhettoNetworkMod.Instance == null) return;
            FiresGhettoNetworkMod.Instance.StartCoroutine(Track(go, kind));
        }

        private static IEnumerator Track(GameObject go, string kind)
        {
            // Let the ZNetView bind, the ZDO position apply, and any same-frame
            // neighbours (the structure it rests on) instantiate before we look.
            yield return new WaitForSeconds(0.25f);
            if (go == null) yield break;

            var nview = go.GetComponent<ZNetView>();
            Vector3 spawnPos = go.transform.position;

            // Skip the origin-noise: loot/inventory drops created at a default
            // position before being placed. Only world-relevant spawns have a
            // real position by now.
            if (spawnPos.sqrMagnitude < 1f) yield break;

            bool owner = nview != null && nview.IsValid() && nview.IsOwner();
            bool hit = Physics.Raycast(
                spawnPos + Vector3.up * 0.3f, Vector3.down, out RaycastHit info, 8f,
                GroundMask(), QueryTriggerInteraction.Ignore);
            string beneath = hit
                ? $"{info.collider.gameObject.name}@{info.distance:F2}m"
                : "NONE";

            // Only the at-risk spawns are worth a line (nothing under them).
            if (!hit)
            {
                LoggerOptions.LogMessage(
                    $"[FallProbe] SPAWN {kind} pos=({spawnPos.x:F1},{spawnPos.y:F1},{spawnPos.z:F1}) "
                    + $"owner={owner} beneath=NONE");
            }

            // Re-check whether it dropped, regardless of who owns it — a non-owner
            // still follows the owner's synced position, so a real fall shows here too.
            yield return new WaitForSeconds(3f);
            if (go == null) yield break;

            Vector3 nowPos = go.transform.position;
            float drop = spawnPos.y - nowPos.y;
            if (drop > 0.5f)
            {
                bool hitNow = Physics.Raycast(
                    nowPos + Vector3.up * 0.3f, Vector3.down, out RaycastHit info2, 8f,
                    GroundMask(), QueryTriggerInteraction.Ignore);
                string under = hitNow ? $"{info2.collider.gameObject.name}@{info2.distance:F2}m" : "NONE";
                LoggerOptions.LogWarning(
                    $"[FallProbe] FELL {kind} drop={drop:F2}m spawn=({spawnPos.x:F1},{spawnPos.y:F1},{spawnPos.z:F1}) "
                    + $"now=({nowPos.x:F1},{nowPos.y:F1},{nowPos.z:F1}) spawnBeneath={beneath} nowBeneath={under}");
            }
        }
    }
}
