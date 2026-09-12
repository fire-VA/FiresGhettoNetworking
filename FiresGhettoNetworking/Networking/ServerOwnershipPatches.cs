using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    // Verbatim port of SSS Core.cs ZDOMan_ReleaseNearbyZDOS_Patch. Selective Character+Ship transfer, sticky
    // server ownership and a server-always-covering shortcut were each tried and each froze mobs; keep the
    // SSS shape (memory/reference_bulk_transfer_guard.md has the analysis).
    //
    // ApplySssOwnershipRule carries one exception: with TargetPortalProtection loaded, TeleportWorld prefabs
    // stay peer-owned. TPP's WearNTear.RPC_Remove check reads Player.m_localPlayer, which is null on a dedi,
    // so server-owned portals can never be removed and the m_instances desync NREs CreateDestroyObjects.
    [HarmonyPatch]
    public static class ServerOwnershipPatches
    {
        private static bool _firstFireLogged;

        private static readonly HashSet<int> _teleportWorldPrefabs = new HashSet<int>();
        private static bool _portalExclusionActive;

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        public static void ZNetScene_Awake_BuildPortalExclusionSet(ZNetScene __instance)
        {
            _teleportWorldPrefabs.Clear();
            _portalExclusionActive = false;
            if (__instance == null || __instance.m_prefabs == null) return;

            if (!IsTargetPortalProtectionLoaded()) return;

            foreach (var prefab in __instance.m_prefabs)
            {
                if (prefab == null) continue;
                if (prefab.GetComponent<TeleportWorld>() != null)
                    _teleportWorldPrefabs.Add(prefab.name.GetStableHashCode());
            }

            if (_teleportWorldPrefabs.Count > 0)
            {
                _portalExclusionActive = true;
                LoggerOptions.LogMessage(
                    $"[ServerOwnership] TargetPortalProtection detected. Excluding "
                    + $"{_teleportWorldPrefabs.Count} TeleportWorld prefab(s) from V2 broad-ownership "
                    + "claim so portal destroy flows route through a peer client where TPP's "
                    + "Player.m_localPlayer-based permission check actually works.");
            }
        }

        private static bool IsTargetPortalProtectionLoaded()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.GetType("TargetPortalProtection.TargetPortalProtection", false, false) != null)
                        return true;
                }
                catch { /* assembly-load failures are not informative here */ }
            }
            return false;
        }

        [HarmonyPatch(typeof(ZDOMan), "ReleaseNearbyZDOS")]
        [HarmonyPrefix]
        public static bool ReleaseNearbyZDOS_Prefix(ZDOMan __instance, Vector3 refPosition, long uid)
        {
            if (ZNet.instance == null || !ZNet.instance.IsDedicated()) return true;
            if (ZoneSystem.instance == null) return true;

            // Runtime config guard — belt-and-suspenders on top of the
            // startup-time Harmony.PatchAll gate in Ascend.cs. Allows the
            // operator to flip the toggle off mid-session and immediately
            // fall back to vanilla peer ownership without restarting.
            // Also enforces mutual exclusion with V3 at runtime: if the
            // selective toggle is on, V3 takes precedence and V2 bails.
            if (!(FiresGhettoNetworkMod.ConfigEnableServerAuthority?.Value ?? false)) return true;
            if (!(FiresGhettoNetworkMod.ConfigEnableServerOwnership?.Value ?? false)) return true;
            if (FiresGhettoNetworkMod.ConfigEnableServerOwnershipSelective?.Value ?? false) return true;

            long serverUid = ZDOMan.GetSessionID();
            LogFirstFireOnce(serverUid);
            ServerStatusDiagnostics.s_ownership_passes++;

            Vector2s zone = ZoneSystem.GetZone(refPosition);

            __instance.m_tempNearObjects.Clear();
            // Near band only — the old call passed distantArea 0, which is a far distance of 0 now.
            __instance.FindSectorObjects(zone, new SimulationDistance(SimDistance.Near(), 0), __instance.m_tempNearObjects);

            foreach (var zdo in __instance.m_tempNearObjects)
            {
                if (zdo == null || !zdo.Persistent) continue;
                if (_portalExclusionActive && _teleportWorldPrefabs.Contains(zdo.m_prefab)) continue;
                ServerStatusDiagnostics.s_ownership_zdosProcessed++;
                ApplySssOwnershipRule(__instance, zdo, uid, serverUid);
            }

            return false;
        }

        private static void ApplySssOwnershipRule(ZDOMan zdoMan, ZDO zdo, long callerUid, long serverUid)
        {
            Vector2s sector = zdo.GetSector();
            bool coveredByAnyPeer = SectorCoveredByAnyConnectedPeer(sector);
            long owner = zdo.GetOwner();

            if (owner == callerUid || owner == serverUid)
            {
                if (!coveredByAnyPeer)
                {
                    zdo.SetOwner(0L);
                    ServerStatusDiagnostics.s_ownership_releases++;
                }
                return;
            }

            // IsInPeerActiveArea takes a world point now, not a sector — vanilla passes the ZDO's
            // own position here.
            bool currentOwnerCovers = owner != 0L && zdoMan.IsInPeerActiveArea(zdo.GetPosition(), owner);
            if (!currentOwnerCovers && coveredByAnyPeer)
            {
                zdo.SetOwner(serverUid);
                ServerStatusDiagnostics.s_ownership_transfersToServer++;
            }
        }

        private static bool SectorCoveredByAnyConnectedPeer(Vector2s sector)
        {
            int near = SimDistance.Near();
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer == null) continue;
                // ZNetScene.InActiveArea now takes a world point, not a sector, so compare zones directly.
                if (SimDistance.ZoneInRadius(ZoneSystem.GetZone(peer.GetRefPos()), sector, near))
                    return true;
            }
            return false;
        }

        private static void LogFirstFireOnce(long serverUid)
        {
            if (_firstFireLogged) return;
            _firstFireLogged = true;
            LoggerOptions.LogMessage(
                $"[ServerOwnership] Server-side ZDO ownership transfer engaged. "
                + $"serverUid={serverUid}, peers={ZNet.instance.GetPeers().Count}. "
                + $"(First ZDOMan.ReleaseNearbyZDOS pass on this session; further passes aggregate into [ServerStatus].)");
        }
    }
}
