using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    // TOMBSTONE: this is a verbatim port of SSS Core.cs ZDOMan_ReleaseNearbyZDOS_Patch.
    // Three "optimisations" were tried in 2026-05-22 and ALL three froze mobs:
    //   - selective Character+Ship transfer (instead of broad)
    //   - sticky server ownership (instead of release-on-no-coverage)
    //   - server-as-always-covering shortcut in IsInPeerActiveArea
    // Keep this in SSS-exact shape. See memory/reference_bulk_transfer_guard.md for the
    // full failure-mode analysis if tempted to deviate.
    [HarmonyPatch]
    public static class ServerOwnershipPatches
    {
        private static bool _firstFireLogged;

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
            ServerStatusDiagnostics.s_so_passes++;

#if PUBLIC_TEST
            Vector2s zone = ZoneSystem.GetZone(refPosition);
#else
            Vector2i zone = ZoneSystem.GetZone(refPosition);
#endif

            __instance.m_tempNearObjects.Clear();
            __instance.FindSectorObjects(zone, ZoneSystem.instance.m_activeArea, 0, __instance.m_tempNearObjects);

            foreach (var zdo in __instance.m_tempNearObjects)
            {
                if (zdo == null || !zdo.Persistent) continue;
                ServerStatusDiagnostics.s_so_zdosProcessed++;
                ApplySssOwnershipRule(__instance, zdo, uid, serverUid);
            }

            return false;
        }

        private static void ApplySssOwnershipRule(ZDOMan zdoMan, ZDO zdo, long callerUid, long serverUid)
        {
#if PUBLIC_TEST
            Vector2s sector = zdo.GetSector();
#else
            Vector2i sector = zdo.GetSector();
#endif
            bool coveredByAnyPeer = SectorCoveredByAnyConnectedPeer(sector);
            long owner = zdo.GetOwner();

            if (owner == callerUid || owner == serverUid)
            {
                if (!coveredByAnyPeer)
                {
                    zdo.SetOwner(0L);
                    ServerStatusDiagnostics.s_so_releases++;
                }
                return;
            }

            bool currentOwnerCovers = owner != 0L && zdoMan.IsInPeerActiveArea(sector, owner);
            if (!currentOwnerCovers && coveredByAnyPeer)
            {
                zdo.SetOwner(serverUid);
                ServerStatusDiagnostics.s_so_transfersToServer++;
            }
        }

#if PUBLIC_TEST
        private static bool SectorCoveredByAnyConnectedPeer(Vector2s sector)
#else
        private static bool SectorCoveredByAnyConnectedPeer(Vector2i sector)
#endif
        {
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer == null) continue;
                if (ZNetScene.InActiveArea(sector, ZoneSystem.GetZone(peer.GetRefPos())))
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
