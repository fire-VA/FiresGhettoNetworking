using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Names build pieces whose ZNetView.m_type is not Solid. Clients instantiate Type-descending
    /// (ZNetScene.ZDOCompare), so a Solid floor always exists before the loose items resting on it; a modded
    /// piece left at the Unity default lands in the same tier as those items and can lose the race. Reads
    /// ZNetScene.m_prefabs once the count stops growing, since Jotunn registers after ZNetScene.Awake.
    /// </summary>
    [HarmonyPatch]
    public static class PieceTypeAudit
    {
        private const int MaxSettleChecks = 60;
        private const int StableChecksRequired = 3;
        private const float SettleCheckSeconds = 1f;

        private static bool s_started;

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        [HarmonyPostfix]
        public static void ZNetScene_Awake_Postfix()
        {
            if (s_started || FiresGhettoNetworkMod.Instance == null) return;
            s_started = true;
            FiresGhettoNetworkMod.Instance.StartCoroutine(RunWhenPrefabsSettle());
        }

        private static IEnumerator RunWhenPrefabsSettle()
        {
            int lastCount = -1;
            int stableChecks = 0;
            for (int check = 0; check < MaxSettleChecks; check++)
            {
                ZNetScene scene = ZNetScene.instance;
                int count = scene != null ? scene.m_prefabs.Count : 0;
                if (count > 0 && count == lastCount)
                {
                    if (++stableChecks >= StableChecksRequired) break;
                }
                else
                {
                    stableChecks = 0;
                }
                lastCount = count;
                yield return new WaitForSeconds(SettleCheckSeconds);
            }
            Audit();
        }

        private static void Audit()
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene == null)
            {
                LoggerOptions.LogWarning("[PieceAudit] ZNetScene null at audit time - aborting.");
                return;
            }

            int totalPieces = 0;
            int nonSolid = 0;
            var suspects = new List<string>();

            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (prefab == null) continue;
                if (prefab.GetComponent<Piece>() == null) continue;
                ZNetView nview = prefab.GetComponent<ZNetView>();
                if (nview == null) continue;

                totalPieces++;
                if (nview.m_type != ZDO.ObjectType.Solid)
                {
                    nonSolid++;
                    bool hasCollider = prefab.GetComponentInChildren<Collider>() != null;
                    bool hasWearNTear = prefab.GetComponent<WearNTear>() != null;
                    suspects.Add(
                        $"  {prefab.name}  type={nview.m_type}  collider={hasCollider}  wearNTear={hasWearNTear}");
                }
            }

            LoggerOptions.LogMessage(
                $"[PieceAudit] Scanned {totalPieces} build-piece prefabs; {nonSolid} are NOT Solid.");
            if (nonSolid > 0)
            {
                LoggerOptions.LogWarning(
                    "[PieceAudit] NON-SOLID pieces below. collider=True + wearNTear=True = a structural "
                    + "piece you can stand on that instantiates in the item tier -> prime fall-through suspect:");
                foreach (string line in suspects)
                    LoggerOptions.LogWarning(line);
            }
        }
    }
}
