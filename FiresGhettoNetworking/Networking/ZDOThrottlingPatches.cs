using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Single postfix on ServerSortSendZDOS that applies both send-priority adjustments in one pass and
    /// re-sorts once: player ZDOs are moved toward the front, distant loose objects toward the back.
    /// Lower m_tempSortValue means sent sooner. Only runs while the peer is actually congested.
    /// </summary>
    [HarmonyPatch]
    public static class ZDOThrottlingPatches
    {
        private const float DistantSortPenalty = 500f;

        private static readonly int PlayerPrefabHash = "Player".GetStableHashCode();

        [HarmonyPatch(typeof(ZDOMan), "ServerSortSendZDOS")]
        [HarmonyPostfix]
        public static void ServerSortSendZDOS_Postfix(List<ZDO> objects, Vector3 refPos, ZDOMan.ZDOPeer peer)
        {
            if (ZNet.instance == null || !ZNet.instance.IsDedicated()) return;
            if (FiresGhettoNetworkMod.ConfigEnableZDOThrottling == null) return;

            bool throttleEnabled = FiresGhettoNetworkMod.ConfigEnableZDOThrottling.Value;
            float throttleDistanceSqr = 0f;
            if (throttleEnabled)
            {
                float throttleDistance = FiresGhettoNetworkMod.ConfigZDOThrottleDistance.Value;
                throttleDistanceSqr = throttleDistance * throttleDistance;
            }

            bool playerBoostEnabled = PlayerPositionSyncPatches.ConfigEnablePlayerPositionBoost != null
                && PlayerPositionSyncPatches.ConfigEnablePlayerPositionBoost.Value;

            // Divisor rather than subtrahend: vanilla already keeps player ZDOs near zero, so subtracting a
            // constant saturated the floor and made the multiplier slider a no-op above 1.
            float playerBoostDivisor = playerBoostEnabled
                ? Mathf.Max(1f, PlayerPositionSyncPatches.ConfigPlayerPositionUpdateMultiplier.Value)
                : 1f;

            if (!throttleEnabled && !playerBoostEnabled) return;

            // With bandwidth to spare every queued ZDO ships the same tick, so reordering would only add
            // latency. Leave vanilla's order alone until the peer backs up.
            if (FiresGhettoNetworkMod.ConfigEnableAdaptiveThrottling == null
                || FiresGhettoNetworkMod.ConfigEnableAdaptiveThrottling.Value)
            {
                if (!SendCongestion.IsPeerCongested(peer)) return;
            }

            bool modified = false;

            foreach (ZDO zdo in objects)
            {
                if (playerBoostEnabled && IsPlayerZDO(zdo))
                {
                    // Only boost ongoing updates. A peer's first copy of a player ZDO keeps vanilla priority
                    // so it streams in alongside the ground that player is standing on.
                    bool peerHasSeenZdo = peer != null && peer.m_zdos.ContainsKey(zdo.m_uid);
                    if (peerHasSeenZdo)
                    {
                        // A negative m_tempSortValue is ZDO's SaveClone flag, which would stop
                        // ZDOExtraData.Release running on Reset.
                        zdo.m_tempSortValue = Mathf.Max(0f, zdo.m_tempSortValue / playerBoostDivisor);
                        modified = true;
                    }
                    continue;
                }

                // Loose objects only. Structures and terrain stay put so a distant building never falls behind
                // the things resting on it.
                if (throttleEnabled && zdo.Type == ZDO.ObjectType.Default)
                {
                    Vector3 zdoPos = zdo.GetPosition();
                    float dx = zdoPos.x - refPos.x;
                    float dz = zdoPos.z - refPos.z;
                    if (dx * dx + dz * dz > throttleDistanceSqr)
                    {
                        zdo.m_tempSortValue += DistantSortPenalty;
                        modified = true;
                    }
                }
            }

            // Sort by the adjusted value only. Reordering by ObjectType here cannot change client
            // instantiation order (ZNetScene.ZDOCompare re-sorts on arrival) and caused the 1.3.6 regression.
            if (modified)
            {
                objects.Sort((x, y) => x.m_tempSortValue.CompareTo(y.m_tempSortValue));
            }
        }

        private static bool IsPlayerZDO(ZDO zdo)
        {
            return zdo != null && zdo.m_prefab == PlayerPrefabHash;
        }
    }
}
