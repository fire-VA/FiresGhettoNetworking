using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// TEST-BUILD DIAGNOSTIC (not for release). One-shot audit of build-piece ObjectType.
    ///
    /// Client instantiation order is Type-descending (ZNetScene.ZDOCompare): Solid (2) is always
    /// instantiated before Default (0). A ZDO's type comes straight from the prefab's serialized
    /// ZNetView.m_type (ZNetView.cs: this.m_zdo.Type = this.m_type). Vanilla build pieces are Solid,
    /// so a vanilla floor/bridge always exists before the loose item resting on it.
    ///
    /// If a custom build-piece mod ships a piece with m_type left at the Unity default (Default=0),
    /// that piece is in the SAME instantiation tier as the items on top of it — pure load-race — so
    /// a tombstone/item can spawn and fall before its support exists. This audit names any such piece.
    ///
    /// Reads ZNetScene.m_prefabs after the count stops growing (Jotunn registers modded prefabs after
    /// ZNetScene.Awake). Runs on both server and client; the dedi log is the one that matters.
    /// </summary>
    [HarmonyPatch]
    public static class PieceTypeAudit
    {
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
            int last = -1;
            int stableTicks = 0;
            for (int i = 0; i < 60; i++)
            {
                ZNetScene zs = ZNetScene.instance;
                int count = zs != null ? zs.m_prefabs.Count : 0;
                if (count > 0 && count == last)
                {
                    if (++stableTicks >= 3) break;
                }
                else
                {
                    stableTicks = 0;
                }
                last = count;
                yield return new WaitForSeconds(1f);
            }
            Audit();
        }

        private static void Audit()
        {
            ZNetScene zs = ZNetScene.instance;
            if (zs == null)
            {
                LoggerOptions.LogWarning("[PieceAudit] ZNetScene null at audit time - aborting.");
                return;
            }

            int totalPieces = 0;
            int nonSolid = 0;
            var suspects = new List<string>();

            foreach (GameObject go in zs.m_prefabs)
            {
                if (go == null) continue;
                if (go.GetComponent<Piece>() == null) continue;
                ZNetView nview = go.GetComponent<ZNetView>();
                if (nview == null) continue;

                totalPieces++;
                if (nview.m_type != ZDO.ObjectType.Solid)
                {
                    nonSolid++;
                    bool hasCollider = go.GetComponentInChildren<Collider>() != null;
                    bool hasWearNTear = go.GetComponent<WearNTear>() != null;
                    suspects.Add(
                        $"  {go.name}  type={nview.m_type}  collider={hasCollider}  wearNTear={hasWearNTear}");
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
