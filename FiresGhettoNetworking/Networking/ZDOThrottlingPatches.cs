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

                // Distant penalty — only penalize loose, non-load-bearing objects
                // (ObjectType.Default). Solid (structures/build pieces) and Terrain
                // (ground/heightmap) are EXEMPT so they're never pushed behind the
                // dynamic objects that physically rest on them. Works together with
                // CompareSendOrder below (which keeps supports ahead by Type): the
                // exemption stops the distance penalty from undoing that ordering for
                // far-out structures. Steady-state cost is negligible — static
                // structure rarely re-sends once a peer has it.
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

            // Single sort pass — only when values were changed. The comparator is
            // Type-aware (see CompareSendOrder): it must NOT collapse to pure sort
            // value, or supports (Solid/Terrain) lose their ordering ahead of the
            // dynamic objects that physically rest on them.
            if (modified)
            {
                objects.Sort((x, y) => CompareSendOrder(x, y, peer));
            }
        }

        // Send-order comparator that keeps physics objects from outracing their support.
        //
        //   1. A Prioritized ZDO the peer has ALREADY received jumps to the front —
        //      these are ongoing movement/combat updates for players & creatures already
        //      in the peer's world and need to stay responsive. On the peer's FIRST receipt
        //      it does NOT jump, so it falls into the Type ordering below.
        //   2. Type descending: Terrain(3) > Solid(2) > Prioritized(1) > Default(0). The
        //      structure/ground a creature or tombstone rests on is sent first, so its
        //      collider exists client-side before the dynamic object spawns and runs
        //      physics — no more tames walking out of half-loaded pens or graves dropping
        //      through bridges/rocks. Covers both since Solid outranks Prioritized AND
        //      Default.
        //   3. Within a class, our adjusted value (player boost / distant penalty) decides.
        //
        // This replaces the old pure-sort-value collapse, which discarded vanilla's
        // support-first Type order; it also gates vanilla's owned-Prioritized front-jump
        // on "peer has seen it" so a first-load creature no longer beats its own pen.
        private static int CompareSendOrder(ZDO x, ZDO y, ZDOMan.ZDOPeer peer)
        {
            bool xFront = IsSeenPrioritized(x, peer);
            bool yFront = IsSeenPrioritized(y, peer);
            if (xFront != yFront) return xFront ? -1 : 1;

            if (x.Type != y.Type) return ((int)y.Type).CompareTo((int)x.Type);

            return x.m_tempSortValue.CompareTo(y.m_tempSortValue);
        }

        private static bool IsSeenPrioritized(ZDO zdo, ZDOMan.ZDOPeer peer)
        {
            return zdo != null
                && zdo.Type == ZDO.ObjectType.Prioritized
                && peer != null
                && peer.m_zdos.ContainsKey(zdo.m_uid);
        }

        private static readonly int PlayerPrefabHash = "Player".GetStableHashCode();

        private static bool IsPlayerZDO(ZDO zdo)
        {
            return zdo != null && zdo.m_prefab == PlayerPrefabHash;
        }
    }
}