using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// TEST-BUILD DIAGNOSTIC (not for release). Tracks ItemDrop / TombStone spawns to
    /// catch the "falls through a structure on zone reload" bug. v2: the v1 probe sampled
    /// at Awake — before position/ownership were set — so it logged garbage ((0,0,0),
    /// owner=False) and its danger-case detection never fired. This version starts a
    /// coroutine, waits for the object to settle, captures the real spawn state, then
    /// re-checks after a few seconds to see if it actually dropped.
    ///
    /// What to look for in the log:
    ///   [FallProbe] SPAWN ... beneath=NONE   = spawned with no collider under it (at risk)
    ///   [FallProbe] FELL  ... drop=X.XXm     = it actually dropped after spawning (the bug)
    /// If a deliberate repro produces SPAWN-beneath=NONE lines but no FELL lines, the items
    /// are landing fine and the issue isn't reproducing here.
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
                s_groundMask = LayerMask.GetMask(
                    "Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
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
