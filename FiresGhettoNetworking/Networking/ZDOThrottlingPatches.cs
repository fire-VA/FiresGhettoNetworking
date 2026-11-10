using HarmonyLib;
using System.Collections.Generic;
using FiresGhettoNetworkMod.AutoTune;
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
                // Through EffectiveConfig so Server Auto-Tune's tier distance is the one used while it is on.
                float throttleDistance = EffectiveConfig.ZDOThrottleDistance();
                // 0 means off. Read as a distance it marked every loose object as distant.
                if (throttleDistance <= 0f) throttleEnabled = false;
                else throttleDistanceSqr = throttleDistance * throttleDistance;
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

            bool ringOn = FgnRing.On;   // 1.5.79: once per call, not per ZDO
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
                    // 1.5.78 halo ring: distance along the ring surface (FatApi.HaloSurfaceDistance); x,z as before elsewhere.
                    if (FgnRing.PriorityDistanceSqr(zdo.GetPosition(), refPos, ringOn) > throttleDistanceSqr)
                    {
                        zdo.m_tempSortValue += DistantSortPenalty;
                        modified = true;
                    }
                }
            }

            // Sort by the adjusted value only. Reordering by ObjectType here cannot change client
            // instantiation order (ZNetScene.ZDOCompare re-sorts on arrival) and caused a regression.
            if (modified)
            {
                if (ConfigKeepPrioritizedFirst != null && ConfigKeepPrioritizedFirst.Value && peer != null) { s_receiver = peer.m_peer.m_uid; objects.Sort(s_prioritizedFirst); }   // 1.6.84
                else objects.Sort((x, y) => x.m_tempSortValue.CompareTo(y.m_tempSortValue));
            }
        }

        // 1.6.84 ([fgn], [wishbone] round 2, read against vanilla ZDOMan.ServerSendCompare): vanilla sends Prioritized objects another player owns (creatures and players, to the receiver) before everything else.
        // The value-only re-sort above dropped that while a peer was congested, exactly when order matters. Their rule first; then the adjusted value, as before (type order stays out: see the note above).
        internal static BepInEx.Configuration.ConfigEntry<bool> ConfigKeepPrioritizedFirst;
        private static long s_receiver;
        private static readonly System.Comparison<ZDO> s_prioritizedFirst = PrioritizedFirst;

        internal static void InitConfig(BepInEx.Configuration.ConfigFile config)
        {
            ConfigKeepPrioritizedFirst = config.Bind("09 - Dedicated Server", "Keep Other Players Creatures First When Congested", true,
                Cfg.Adv("When a player's connection is backed up, creatures and players run by someone else are still sent to them first, as the game intends."));
        }

        private static bool OthersPrioritized(ZDO z) => z.Type == ZDO.ObjectType.Prioritized && z.HasOwner() && z.GetOwner() != s_receiver;

        private static int PrioritizedFirst(ZDO x, ZDO y)
        {
            bool a = OthersPrioritized(x), b = OthersPrioritized(y);
            if (a != b) return a ? -1 : 1;
            return x.m_tempSortValue.CompareTo(y.m_tempSortValue);
        }

        private static bool IsPlayerZDO(ZDO zdo)
        {
            return zdo != null && zdo.GetPrefab() == PlayerPrefabHash;   // 1.6.84 ([wishbone] spotted): the public getter, not the private field
        }
    }
}
