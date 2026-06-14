using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Combined ZDO sort priority postfix.
    ///
    /// Runs once after vanilla ServerSortSendZDOS and applies ALL priority
    /// modifications in a single pass, then does ONE final sort:
    ///
    ///   1. Player ZDOs                   → subtract boost  (sync first)
    ///   2. Distant non-prioritized ZDOs  → +500 penalty    (sync less often)
    ///
    /// Previously these were two separate postfixes each re-sorting the list
    /// (ZDOThrottlingPatches + PlayerPositionSyncPatches), causing up to 3 full
    /// sorts per peer per tick. Now it is always exactly 1.
    ///
    /// Lower m_tempSortValue = higher priority = sent first.
    /// </summary>
    [HarmonyPatch]
    public static class ZDOThrottlingPatches
    {
        private const float DISTANT_PENALTY = 500f;

        [HarmonyPatch(typeof(ZDOMan), "ServerSortSendZDOS")]
        [HarmonyPostfix]
        public static void ServerSortSendZDOS_Postfix(List<ZDO> objects, Vector3 refPos, ZDOMan.ZDOPeer peer)
        {
            // SERVER-ONLY: Only run on dedicated servers
            if (ZNet.instance == null || !ZNet.instance.IsDedicated())
                return;

            // Safety: ensure configs are initialized
            if (FiresGhettoNetworkMod.ConfigEnableZDOThrottling == null)
                return;

            bool throttleEnabled = FiresGhettoNetworkMod.ConfigEnableZDOThrottling.Value;
            float throttleDistanceSqr = 0f;
            if (throttleEnabled)
            {
                float d = FiresGhettoNetworkMod.ConfigZDOThrottleDistance.Value;
                throttleDistanceSqr = d * d;
            }

            bool playerBoostEnabled = PlayerPositionSyncPatches.ConfigEnablePlayerPositionBoost != null
                && PlayerPositionSyncPatches.ConfigEnablePlayerPositionBoost.Value;
            // Divisor, not subtrahend. Earlier the boost was `150f * multiplier`
            // and the player ZDO sort value was decremented by that constant. Vanilla
            // already keeps player ZDOs near zero so any boost ≥ ~10 already saturated
            // the Mathf.Max(0,…) floor, which made the multiplier slider a no-op for
            // values >= 1.0. Dividing the sort value instead scales the player ZDO
            // DOWN relative to other ZDOs (whose values are untouched), so a 5×
            // multiplier really does push players five times further toward the
            // front of the send queue than a 1× multiplier.
            float playerBoostDivisor = playerBoostEnabled
                ? Mathf.Max(1f, PlayerPositionSyncPatches.ConfigPlayerPositionUpdateMultiplier.Value)
                : 1f;

            if (!throttleEnabled && !playerBoostEnabled)
                return;

            // ADAPTIVE: only reorder when this peer's send queue is actually backing
            // up. With bandwidth to spare every queued ZDO ships the same tick anyway,
            // so the penalty/boost/re-sort below would only cost CPU and can ADD latency
            // by deferring updates that would have gone out immediately. Leave the
            // vanilla order (already applied by the method we postfix) untouched.
            if (FiresGhettoNetworkMod.ConfigEnableAdaptiveThrottling == null
                || FiresGhettoNetworkMod.ConfigEnableAdaptiveThrottling.Value)
            {
                if (!SendCongestion.IsPeerCongested(peer))
                    return;
            }

            bool modified = false;

            foreach (ZDO zdo in objects)
            {
                // Player boost — players are never throttled.
                // GATED on "peer has already received this ZDO once": the boost
                // exists to keep ONGOING player position updates ahead of the
                // queue for smooth combat/PvP. On a peer's FIRST send of a player
                // ZDO we leave it at vanilla priority so it streams in alongside
                // the terrain/structures it stands on, rather than arriving before
                // its context. (peer.m_zdos holds every ZDO already sent to this
                // peer — see ZDOMan.SendZDOs.)
                if (playerBoostEnabled && IsPlayerZDO(zdo))
                {
                    bool peerHasSeenZdo = peer != null && peer.m_zdos.ContainsKey(zdo.m_uid);
                    if (peerHasSeenZdo)
                    {
                        // Floor at 0 still required: m_tempSortValue doubles as the
                        // SaveClone flag when negative (SaveClone = m_tempSortValue < 0).
                        // Going negative would incorrectly mark this ZDO as a save clone
                        // and prevent ZDOExtraData.Release from running on ZDO.Reset().
                        zdo.m_tempSortValue = Mathf.Max(0f, zdo.m_tempSortValue / playerBoostDivisor);
                        modified = true;
                    }
                    continue;
                }

                // Distant penalty — only penalize loose objects (ObjectType.Default).
                // Solid (structures/build pieces) and Terrain (ground/heightmap) are
                // EXEMPT so a far-out structure isn't shoved behind the things resting
                // on it. Steady-state cost is negligible — static structure rarely
                // re-sends once a peer has it.
                if (throttleEnabled && zdo.Type == ZDO.ObjectType.Default)
                {
                    Vector3 zdoPos = zdo.GetPosition();
                    float dx = zdoPos.x - refPos.x;
                    float dz = zdoPos.z - refPos.z;
                    if (dx * dx + dz * dz > throttleDistanceSqr)
                    {
                        zdo.m_tempSortValue += DISTANT_PENALTY;
                        modified = true;
                    }
                }
            }

            // Re-sort by the (now adjusted) vanilla sort value. We deliberately do NOT
            // reorder by ObjectType: the client re-sorts everything it receives
            // Type-descending on its own (ZNetScene.ZDOCompare), so a server-side Type
            // sort cannot change client instantiation order for correctly-typed objects
            // — it only shuffles which ZDOs land in a given budgeted tick. That was the
            // 1.3.6 regression; the real fall-through cause is mis-typed custom pieces,
            // fixed at the prefab, not here.
            if (modified)
            {
                objects.Sort((x, y) => x.m_tempSortValue.CompareTo(y.m_tempSortValue));
            }
        }

        private static readonly int PlayerPrefabHash = "Player".GetStableHashCode();

        private static bool IsPlayerZDO(ZDO zdo)
        {
            return zdo != null && zdo.m_prefab == PlayerPrefabHash;
        }
    }
}