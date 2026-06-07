using System.Collections.Generic;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// V3 ServerOwnership — selective include-list. SSS-exact release/transfer
    /// logic from <see cref="ServerOwnershipPatches"/> with one critical
    /// difference: only prefabs whose root has a <see cref="Character"/>
    /// (excluding <see cref="Player"/>) or <see cref="Ship"/> component get
    /// ownership transferred to the server. Everything else — drops,
    /// containers, doors, signs, workstations, pickables, beds, traders,
    /// wards, heightmap pieces, built structures, carts — stays under
    /// vanilla peer ownership.
    ///
    /// Why this exists: V2 (broad SSS-exact) reproduces the original SSS
    /// behaviour faithfully but inherits SSS's edge cases:
    ///   - Drops dropped by mob actions sometimes can't be `removedrops`'d
    ///     (Gand 2026-05-23) — ownership transfer races vs interaction RPC.
    ///   - Voxel mining/flattening intermittently fails on busy servers
    ///     (KanKub 2026-05-23) — heightmap RPC routes to server, server
    ///     applies, but the delta back to clients gets starved.
    ///   - Carts shake/sink/fly when parked — Rigidbody+ZSyncTransform
    ///     under server ownership without a ShipFixesGroup equivalent.
    ///
    /// V3's bet: 90% of the simulation-offload benefit (mob AI, ship
    /// physics) lives in the include set. Excluding interactables avoids
    /// every interaction-RPC race condition. Carts stay peer-owned until
    /// a `VagonFixesGroup` equivalent of ShipFixesGroup is written.
    ///
    /// MUTUAL EXCLUSION with V2: Ascend.cs picks ONE of V2 or V3 based on
    /// config. They both prefix <see cref="ZDOMan.ReleaseNearbyZDOS"/> so
    /// they cannot coexist — V3 takes precedence when both flags are on,
    /// since selective is the safer default.
    ///
    /// HISTORY: V1 (deleted 2026-05-22) had selective scope BUT also two
    /// extra deviations that froze mobs — CHANGE #1 (server-as-owner
    /// always-covering) and CHANGE #3 (sticky ownership never releases
    /// simulated prefabs). V3 inherits ONLY the selective scope idea from
    /// V1; the release/transfer mechanics are SSS-exact like V2.
    /// </summary>
    [HarmonyPatch]
    public static class ServerOwnershipPatchesV3
    {
        // Hashes of every ZNetScene prefab classified as server-simulated.
        // Built once after ZNetScene.Awake; dropped on Shutdown.
        // Identical population logic to the deleted V1 — Character (not
        // Player) + Ship. The freeze in V1 was the release/transfer
        // deviations, not this scope.
        private static readonly HashSet<int> s_simulatedPrefabs = new HashSet<int>();
        private static bool s_built;

        // Reusable list — matches vanilla m_tempNearObjects, no per-tick alloc.
        private static readonly List<ZDO> s_tempNearObjects = new List<ZDO>();

        // Diagnostic counters — parallel to V2's so A/B comparison in the log
        // is direct (line shape is the same, just tagged V3).
        private static bool s_firstFireLogged;
        private static int s_passCount;
        private static int s_zdosProcessed;
        private static int s_transfersToServer;
        private static int s_releases;
        private static int s_simulatedSeen;
        private static int s_nonSimulatedTransfersToPeer;
        private static float s_nextStatLogTime;

        // ====================================================================
        // BUILD THE SIMULATED-PREFAB SET — Character (not Player) + Ship
        // ====================================================================
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        [HarmonyPostfix]
        public static void ZNetScene_Awake_BuildSet(ZNetScene __instance)
        {
            s_simulatedPrefabs.Clear();
            s_built = false;
            if (__instance == null || __instance.m_prefabs == null) return;

            foreach (var prefab in __instance.m_prefabs)
            {
                if (prefab == null) continue;
                if (prefab.GetComponent<Player>() != null) continue; // never server-own players
                if (prefab.GetComponent<Ship>() != null
                    || prefab.GetComponent<Character>() != null)
                {
                    s_simulatedPrefabs.Add(prefab.name.GetStableHashCode());
                }
            }
            s_built = true;
            LoggerOptions.LogMessage(
                $"[ServerOwnership-V3] simulated-prefab set built ({s_simulatedPrefabs.Count} entries — Character/Ship only).");
        }

        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        [HarmonyPostfix]
        public static void ZNetScene_Shutdown_DropSet()
        {
            s_simulatedPrefabs.Clear();
            s_built = false;
        }

        // ====================================================================
        // SELECTIVE RELEASE-NEARBY-ZDOS
        //
        // Vanilla source (REFERENCE_ZDOMan.md:513-533) reproduced here with
        // ONE divergence vs V2: the transfer-to-server branch checks whether
        // the prefab is in our simulated set. If yes → SetOwner(serverUid)
        // (V2 behaviour, claim for server). If no → SetOwner(uid) (vanilla
        // behaviour, peer takes ownership).
        //
        // Release-on-no-coverage (branch 1) is unchanged from V2 / SSS-exact.
        // ====================================================================
        [HarmonyPatch(typeof(ZDOMan), "ReleaseNearbyZDOS")]
        [HarmonyPrefix]
        public static bool ReleaseNearbyZDOS_Prefix(
            ZDOMan __instance,
            UnityEngine.Vector3 refPosition,
            long uid)
        {
            if (ZNet.instance == null || !ZNet.instance.IsDedicated()) return true;
            if (!s_built || s_simulatedPrefabs.Count == 0) return true;
            if (ZoneSystem.instance == null) return true;

            // Runtime config guard — belt-and-suspenders on top of the
            // startup-time Harmony.PatchAll gate in Ascend.cs. If the
            // operator disables either the umbrella authority toggle or
            // the selective ownership toggle mid-session, fall through to
            // vanilla peer ownership immediately.
            if (!(FiresGhettoNetworkMod.ConfigEnableServerAuthority?.Value ?? false)) return true;
            if (!(FiresGhettoNetworkMod.ConfigEnableServerOwnershipSelective?.Value ?? false)) return true;

            long serverUid = ZDOMan.GetSessionID();

#if PUBLIC_TEST
            Vector2s zone = ZoneSystem.GetZone(refPosition);
#else
            Vector2i zone = ZoneSystem.GetZone(refPosition);
#endif

            s_tempNearObjects.Clear();
            __instance.FindSectorObjects(
                zone,
                ZoneSystem.instance.m_activeArea,
                0,
                s_tempNearObjects);
            int activatedArea = ZoneSystem.instance.m_activeArea - 1;

            s_passCount++;
            int zdosThisPass = 0;
            int simulatedThisPass = 0;

            foreach (var zdo in s_tempNearObjects)
            {
                if (zdo == null || !zdo.Persistent) continue;
                zdosThisPass++;

#if PUBLIC_TEST
                Vector2s sector = zdo.GetSector();
#else
                Vector2i sector = zdo.GetSector();
#endif
                long owner = zdo.GetOwner();
                bool simulated = s_simulatedPrefabs.Contains(zdo.m_prefab);
                if (simulated) simulatedThisPass++;

                if (owner == uid || owner == serverUid)
                {
                    // Caller or server owns. Release if no peer covers.
                    // Vanilla / SSS-exact behaviour, same as V2.
                    if (!ZNetScene.InActiveArea(sector, zone, activatedArea))
                    {
                        bool anyPeerCovers = false;
                        foreach (var peer in ZNet.instance.GetPeers())
                        {
                            if (peer == null) continue;
                            if (ZNetScene.InActiveArea(sector, ZoneSystem.GetZone(peer.GetRefPos())))
                            {
                                anyPeerCovers = true;
                                break;
                            }
                        }
                        if (!anyPeerCovers)
                        {
                            zdo.SetOwner(0L);
                            s_releases++;
                        }
                    }
                    continue;
                }

                // Someone else owns. Vanilla / SSS-exact transfer condition:
                // owner has stale coverage AND a peer covers this sector.
                bool currentOwnerCovers = (owner != 0L)
                    && __instance.IsInPeerActiveArea(sector, owner);
                bool ownerHasStaleCoverage = !currentOwnerCovers;

                bool sectorCoveredByPeer = false;
                foreach (var peer in ZNet.instance.GetPeers())
                {
                    if (peer == null) continue;
                    if (ZNetScene.InActiveArea(sector, ZoneSystem.GetZone(peer.GetRefPos())))
                    {
                        sectorCoveredByPeer = true;
                        break;
                    }
                }

                if (ownerHasStaleCoverage && sectorCoveredByPeer)
                {
                    // *** V3 DIVERGENCE FROM V2 / SSS ***
                    // Only route to server if the prefab is one we want the
                    // server to simulate. Otherwise behave like vanilla and
                    // hand ownership to the asking peer (uid).
                    if (simulated)
                    {
                        zdo.SetOwner(serverUid);
                        s_transfersToServer++;
                    }
                    else
                    {
                        // Vanilla path — peer takes ownership of interactable.
                        // Only do this on a per-peer pass (uid != serverUid);
                        // the server's own pass shouldn't claim non-simulated
                        // ZDOs for itself (that would re-introduce the V2
                        // problems we're avoiding).
                        if (uid != serverUid)
                        {
                            zdo.SetOwner(uid);
                            s_nonSimulatedTransfersToPeer++;
                        }
                    }
                }
            }

            s_zdosProcessed += zdosThisPass;
            s_simulatedSeen += simulatedThisPass;

            // First-fire confirmation — proves the patch attached.
            if (!s_firstFireLogged)
            {
                s_firstFireLogged = true;
                LoggerOptions.LogMessage(
                    $"[ServerOwnership-V3] ReleaseNearbyZDOS_Prefix fired for the first time. "
                    + $"serverUid={serverUid}, caller uid={uid}, refPos=({refPosition.x:F2}, {refPosition.y:F2}, {refPosition.z:F2}), "
                    + $"connectedPeers={ZNet.instance.GetConnectedPeers().Count}, "
                    + $"simulatedPrefabs={s_simulatedPrefabs.Count}. Patch is wired correctly.");
            }

            // ~10 s rollup. simulatedSeen vs zdosProcessed tells you what
            // fraction of nearby ZDOs are even candidates for server-side
            // simulation. nonSimulatedTransfersToPeer shows interactables
            // flowing to peers via vanilla path.
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now >= s_nextStatLogTime)
            {
                LoggerOptions.LogMessage(
                    $"[ServerOwnership-V3] Last 10s: {s_passCount} passes, {s_zdosProcessed} ZDOs processed "
                    + $"({s_simulatedSeen} simulated-class), "
                    + $"{s_transfersToServer} transfers-to-server, "
                    + $"{s_nonSimulatedTransfersToPeer} transfers-to-peer (non-simulated), "
                    + $"{s_releases} releases-to-unowned.");
                s_passCount = 0;
                s_zdosProcessed = 0;
                s_simulatedSeen = 0;
                s_transfersToServer = 0;
                s_nonSimulatedTransfersToPeer = 0;
                s_releases = 0;
                s_nextStatLogTime = now + 10f;
            }

            return false; // we handled it; skip vanilla
        }
    }
}
