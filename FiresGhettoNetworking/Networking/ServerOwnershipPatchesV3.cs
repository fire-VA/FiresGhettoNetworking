using System.Collections.Generic;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Selective server ownership: only Character (excluding Player) and Ship prefabs transfer, so
    /// interactables stay peer-owned and avoid the interaction-RPC races broad ownership exposes. Mutually
    /// exclusive with ServerOwnershipPatches, and this one wins when both are enabled.
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
        [HarmonyPriority(Priority.Last)]
        public static void ZNetScene_Awake_BuildSet(ZNetScene __instance)
        {
            s_simulatedPrefabs.Clear();
            s_built = false;
            if (__instance == null || __instance.m_prefabs == null) return;

            // Ships are only claimed when the operator has explicitly opted into server-driven
            // ship physics. Claiming a Ship IS simulating it: ownership is what decides who runs
            // the Rigidbody, and ImpactEffect.OnCollisionEnter only fires for the owner — so a
            // server that owns an empty hull generates its own collisions and damages the boat
            // on flat water. Before this gate the toggle below was bound, documented as
            // "disabled by default", and then never read, so enabling selective ownership
            // silently handed ship physics to the server anyway. Left off, ships stay
            // peer-owned exactly like vanilla and the sailing client simulates them.
            bool ownShips = FiresGhettoNetworkMod.ConfigEnableServerSideShipSimulation?.Value ?? false;

            foreach (var prefab in __instance.m_prefabs)
            {
                if (prefab == null) continue;
                if (prefab.GetComponent<Player>() != null) continue; // never server-own players
                bool isShip = prefab.GetComponent<Ship>() != null;
                if (isShip && !ownShips) continue;
                if (isShip || prefab.GetComponent<Character>() != null)
                {
                    s_simulatedPrefabs.Add(prefab.name.GetStableHashCode());
                }
            }
            s_built = true;
            LoggerOptions.LogMessage(
                $"[ServerOwnership-V3] simulated-prefab set built ({s_simulatedPrefabs.Count} entries — "
                + (ownShips
                    ? "Character + Ship; server-side ship physics is ON)."
                    : "Character only; ships stay peer-owned because 'Server-Side Ship Simulation' is off)."));
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

            Vector2s zone = ZoneSystem.GetZone(refPosition);

            s_tempNearObjects.Clear();
            int nearArea = SimDistance.Near();
            __instance.FindSectorObjects(zone, new SimulationDistance(nearArea, 0), s_tempNearObjects);
            int activatedArea = nearArea - 1;

            s_passCount++;
            int zdosThisPass = 0;
            int simulatedThisPass = 0;

            foreach (var zdo in s_tempNearObjects)
            {
                if (zdo == null || !zdo.Persistent) continue;
                zdosThisPass++;

                Vector2s sector = zdo.GetSector();
                long owner = zdo.GetOwner();
                bool simulated = s_simulatedPrefabs.Contains(zdo.m_prefab);
                if (simulated) simulatedThisPass++;

                if (owner == uid || owner == serverUid)
                {
                    // Caller or server owns. Release if no peer covers.
                    // Vanilla / SSS-exact behaviour, same as V2.
                    if (!SimDistance.ZoneInRadius(zone, sector, activatedArea))
                    {
                        bool anyPeerCovers = false;
                        foreach (var peer in ZNet.instance.GetPeers())
                        {
                            if (peer == null) continue;
                            if (SimDistance.ZoneInRadius(ZoneSystem.GetZone(peer.GetRefPos()), sector, nearArea))
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
                // IsInPeerActiveArea takes a world point now, not a sector.
                bool currentOwnerCovers = (owner != 0L)
                    && __instance.IsInPeerActiveArea(zdo.GetPosition(), owner);
                bool ownerHasStaleCoverage = !currentOwnerCovers;

                bool sectorCoveredByPeer = false;
                foreach (var peer in ZNet.instance.GetPeers())
                {
                    if (peer == null) continue;
                    if (SimDistance.ZoneInRadius(ZoneSystem.GetZone(peer.GetRefPos()), sector, nearArea))
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
