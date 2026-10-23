using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    // Port of SSS Core.cs ZDOMan_ReleaseNearbyZDOS_Patch. Sticky server ownership and a server-always-covering
    // shortcut both froze mobs, so this keeps the SSS shape.
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
        private static readonly HashSet<int> _cartPrefabs = new HashSet<int>();

        public static void ZNetScene_Awake_BuildExclusionSets(ZNetScene __instance)
        {
            _teleportWorldPrefabs.Clear();
            _portalExclusionActive = false;
            if (__instance == null || __instance.m_prefabs == null) return;

            // V3's simulated-prefab set (Character not Player, + Ship when ship sim is on); the broad mode only ever gives
            // those to the server, so pickups (ItemDrop.RequestOwn) and terrain edits (TerrainComp) stay on a player's peer.
            ServerOwnershipPatchesV3.ZNetScene_Awake_BuildSet(__instance);

            _cartPrefabs.Clear();
            foreach (var prefab in __instance.m_prefabs)
                if (prefab != null && prefab.GetComponent<Vagon>() != null)
                    _cartPrefabs.Add(prefab.name.GetStableHashCode());

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

        public static bool ReleaseNearbyZDOS_Prefix(ZDOMan __instance, Vector3 refPosition, long uid)
        {
            if (ZNet.instance == null || !ZNet.instance.IsDedicated()) return true;
            if (ZoneSystem.instance == null) return true;

            // Runtime guard on top of the startup patch gate, so the toggle can be flipped off mid-session.
            // Also keeps V3 exclusive: with the selective toggle on, V3 takes precedence.
            if (!(FiresGhettoNetworkMod.ConfigEnableServerAuthority?.Value ?? false)) return true;
            if (!(FiresGhettoNetworkMod.ConfigEnableServerOwnership?.Value ?? false)) return true;
            if (FiresGhettoNetworkMod.ConfigEnableServerOwnershipSelective?.Value ?? false) return true;

            long serverUid = ZDOMan.GetSessionID();
            LogFirstFireOnce(serverUid);
            ServerStatusDiagnostics.s_ownership_passes++;

            Vector2s zone = ZoneSystem.GetZone(refPosition);

            __instance.m_tempNearObjects.Clear();
            // Near band only (far distance 0).
            __instance.FindSectorObjects(zone, new SimulationDistance(SimDistance.Near(), 0), __instance.m_tempNearObjects);

            foreach (var zdo in __instance.m_tempNearObjects)
            {
                if (zdo == null || !zdo.Persistent) continue;
                if (_portalExclusionActive && _teleportWorldPrefabs.Contains(zdo.m_prefab)) continue;
                if (_cartPrefabs.Contains(zdo.m_prefab)) continue;
                ServerStatusDiagnostics.s_ownership_zdosProcessed++;
                ApplySssOwnershipRule(__instance, zdo, uid, serverUid, zone);
            }

            float now = Time.realtimeSinceStartup;
            if (now >= _nextProofLogTime)
            {
                _nextProofLogTime = now + ProofLogIntervalSeconds;
                if (LoggerOptions.DebugEnabled) LoggerOptions.LogDebug(
                    $"[ServerOwnership] Last 10s: {_nonCharacterSeen} non-Character ZDO(s) (ItemDrop/terrain/piece/interactable) kept off the "
                    + $"server, {_nonCharacterToPeer} handed to the covering player on vanilla's rule, "
                    + $"{_interactablesReturned} server-owned one(s) returned to the covering player.");
                _nonCharacterSeen = 0;
                _nonCharacterToPeer = 0;
                _interactablesReturned = 0;
            }

            return false;
        }

        private static int _nonCharacterSeen;
        private static int _nonCharacterToPeer;
        private static int _interactablesReturned;
        private static float _nextProofLogTime;
        private const float ProofLogIntervalSeconds = 10f;

        private static void ApplySssOwnershipRule(ZDOMan zdoMan, ZDO zdo, long callerUid, long serverUid, Vector2s callerZone)
        {
            Vector2s sector = zdo.GetSector();
            bool coveredByAnyPeer = SectorCoveredByAnyConnectedPeer(sector);
            long owner = zdo.GetOwner();

            // ItemDrops (pickup runs on the owner), TerrainComp (edits apply on its owner) and pieces/interactables must not be
            // server-owned, or pickups never complete and terrain edits are lost. Only V3's simulated set (Characters, ships when
            // ship sim is on) goes to the server; everything else follows vanilla's rule, and one the server already holds goes
            // back to the covering player.
            if (ServerOwnershipPatchesV3.IsBuilt && !ServerOwnershipPatchesV3.IsSimulatedPrefab(zdo.m_prefab))
            {
                _nonCharacterSeen++;
                bool inCallerArea = callerUid != serverUid && ZNetScene.InActiveArea(zdo.GetPosition(), callerZone);
                if (owner == serverUid && inCallerArea)
                {
                    zdo.SetOwner(callerUid);
                    _interactablesReturned++;
                    return;
                }
                if (owner == callerUid || owner == serverUid)
                {
                    if (!coveredByAnyPeer) { zdo.SetOwner(0L); ServerStatusDiagnostics.s_ownership_releases++; }
                    return;
                }
                bool ownerCovers = owner != 0L && zdoMan.IsInPeerActiveArea(zdo.GetPosition(), owner);
                if (!ownerCovers && inCallerArea)
                {
                    zdo.SetOwner(callerUid);
                    _nonCharacterToPeer++;
                }
                return;
            }

            if (ValcastPossession.Handle(zdo)) return;   // a possessed creature stays with its possessor's peer
            owner = zdo.GetOwner();

            if (owner == callerUid || owner == serverUid)
            {
                if (!coveredByAnyPeer)
                {
                    zdo.SetOwner(0L);
                    ServerStatusDiagnostics.s_ownership_releases++;
                }
                return;
            }

            // IsInPeerActiveArea takes a world point; vanilla passes the ZDO's own position here.
            bool currentOwnerCovers = owner != 0L && zdoMan.IsInPeerActiveArea(zdo.GetPosition(), owner);
            if (!currentOwnerCovers && coveredByAnyPeer)
            {
                // A tamed ZDO (Core companions) never goes to the server: vanilla's rule, the covering peer takes it.
                if (zdo.GetBool(ZDOVars.s_tamed))
                {
                    if (callerUid != serverUid) zdo.SetOwner(callerUid);
                    return;
                }
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
                // ZNetScene.InActiveArea takes a world point, not a sector, so compare zones directly.
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
