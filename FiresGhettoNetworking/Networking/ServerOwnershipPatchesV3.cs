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

        // Creatures whose AI, animation and physics run on the CLIENT by their mod's design are never server-owned: a
        // server-owned wild companion T-posed and floated because nothing animated it. Built-in: the Fires companions; other mods add theirs with FgnApi.ExcludeFromServerSimulation. Survives a set rebuild (not cleared on shutdown).
        private static readonly HashSet<int> s_excluded = new HashSet<int> { "CompanionNpc".GetStableHashCode(), "CompanionNpc_Wild".GetStableHashCode() };
        private static readonly List<string> s_excludedNames = new List<string> { "CompanionNpc", "CompanionNpc_Wild" };
        internal static bool Exclude(string prefabName)
        {
            int h = prefabName.GetStableHashCode();
            if (!s_excluded.Add(h)) return false;
            s_excludedNames.Add(prefabName);
            s_simulatedPrefabs.Remove(h);   // registered after the set was built: out from now on
            return true;
        }
        internal static bool IsExcluded(int prefabHash) => s_excluded.Contains(prefabHash);
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
        private static int s_takenOver;   // creatures taken from their present peer owner
        private static int s_interactablesReturned;   // server-owned interactables handed to the covering player
        private static float s_nextStatLogTime;

        // ====================================================================
        // BUILD THE SIMULATED-PREFAB SET — Character (not Player) + Ship
        // ====================================================================
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
                    int h = prefab.name.GetStableHashCode();
                    if (!s_excluded.Contains(h)) s_simulatedPrefabs.Add(h);
                }
            }
            s_built = true;
            LoggerOptions.LogDebug(
                $"[ServerOwnership-V3] simulated-prefab set built ({s_simulatedPrefabs.Count} entries — "
                + (ownShips
                    ? "Character + Ship; server-side ship physics is ON"
                    : "Character only; ships stay peer-owned because 'Server-Side Ship Simulation' is off")
                + $"; left to the clients: {string.Join(", ", s_excludedNames)}).");
        }

        // The broad mode (ServerOwnershipPatches) reuses this set so it never claims ItemDrop / TerrainComp / pieces.
        internal static bool IsBuilt => s_built;
        internal static bool IsSimulatedPrefab(int prefabHash) => s_simulatedPrefabs.Contains(prefabHash);

        public static void ZNetScene_Shutdown_DropSet()
        {
            s_simulatedPrefabs.Clear();
            s_built = false;
            s_handOffs.Clear();
            s_flipBacks = 0;
        }

        // ====================================================================
        // SELECTIVE RELEASE-NEARBY-ZDOS
        //
        // Vanilla's ZDOMan.ReleaseNearbyZDOS logic, reproduced here with
        // ONE divergence vs V2: the transfer-to-server branch checks whether
        // the prefab is in our simulated set. If yes → SetOwner(serverUid)
        // (V2 behaviour, claim for server). If no → SetOwner(uid) (vanilla
        // behaviour, peer takes ownership).
        //
        // Release-on-no-coverage (branch 1) is unchanged from V2 / SSS-exact.
        // ====================================================================
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

            Vector2s zone = FgnRing.Sector(refPosition);

            s_tempNearObjects.Clear();
            int nearArea = SimDistance.Near();
            __instance.FindSectorObjects(zone, new SimulationDistance(nearArea, 0, SimDistance.Synced().IsClassic), s_tempNearObjects);   // 1.6.31: classic as vanilla's ReleaseNearbyZDOS passes it; without it the square's corners got no pass
            int activatedArea = nearArea - 1;

            s_passCount++;
            int zdosThisPass = 0;
            int simulatedThisPass = 0;
            float now = UnityEngine.Time.realtimeSinceStartup;

            foreach (var zdo in s_tempNearObjects)
            {
                if (zdo == null || !zdo.Persistent) continue;
                zdosThisPass++;

                Vector2s sector = zdo.GetSector();
                long owner = zdo.GetOwner();
                bool simulated = s_simulatedPrefabs.Contains(zdo.m_prefab);
                if (simulated) simulatedThisPass++;
                // A creature a player possesses (FiresValcast 'valcast_possessed') stays with that player's peer
                if (simulated && ValcastPossession.Handle(zdo)) continue;
                owner = zdo.GetOwner();   // Handle may have cleared a stale claim; the owner itself is unchanged then

                // 1.6.24: same generic opt-in as V2 - a "fgn_server_keeps" ZDO stays the server's, never handed to a peer
                if (zdo.GetBool(ServerOwnershipPatches.ServerKeepsKey))
                {
                    if (owner != serverUid) zdo.SetOwner(serverUid);
                    continue;
                }

                // Without this the server only ever gets the monsters it spawned itself. The branches below are vanilla's stale-owner rule, which never
                // moves a creature away from a player who still covers it, and the player's own SpawnSystem makes it that
                // player's. Selective means "the server owns monsters": a simulated (non-tamed) creature owned by the peer whose
                // pass this is goes to the server, once, when the server has it instantiated (so it really simulates it).
                if (simulated && uid != serverUid && owner == uid && !zdo.GetBool(ZDOVars.s_tamed) && !HelmOwnership.HelmsmanHolds(zdo)   // 1.6.31: never from the helmsman
                    && ZNetScene.instance != null && ZNetScene.instance.FindInstance(zdo) != null)
                {
                    zdo.SetOwner(serverUid);
                    s_takenOver++;
                    continue;
                }

                // An interactable (not in the simulated set) the SERVER owns, e.g. one it spawned while simulating a zone, would
                // stay the server's for as long as a player covered it, so the players' pick / open / use RPCs would run on the
                // headless server, where vanilla's code reaches for the local player (NRE in Pickable.RPC_Pick).
                // Selective only ever wants creatures (and opted-in ships); the covering player takes it, as on vanilla.
                if (!simulated && owner == serverUid && uid != serverUid && ZNetScene.InActiveArea(zdo.GetPosition(), zone))
                {
                    zdo.SetOwner(uid);
                    s_interactablesReturned++;
                    continue;
                }

                // 1.6.31: a creature tamed while the server owned it stayed on the server, against the key's text. Selective never takes tames, so this only hands back.
                if (simulated && owner == serverUid && uid != serverUid && !ServerOwnershipPatches.ServerTakesTames && zdo.GetBool(ZDOVars.s_tamed)
                    && ZNetScene.InActiveArea(zdo.GetPosition(), zone))
                {
                    zdo.SetOwner(uid);
                    ServerOwnershipPatches.NoteTameReturned(zdo.m_prefab);
                    continue;
                }

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
                            if (SimDistance.ZoneInRadius(FgnRing.Sector(peer.GetRefPos()), sector, nearArea))
                            {
                                anyPeerCovers = true;
                                break;
                            }
                        }
                        if (!anyPeerCovers && CompanionKeepAlive.Covers(sector, nearArea)) anyPeerCovers = true;   // 1.5.75: a kept companion area holds it
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
                    if (SimDistance.ZoneInRadius(FgnRing.Sector(peer.GetRefPos()), sector, nearArea))
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
                    // Never a tamed one: a Core companion simulates only on its owner player's peer (its
                    // AI, commands and OwnerAssist gate on IsOwner), so it takes the vanilla path below and Core's keeper claims it back.
                    if (simulated && !zdo.GetBool(ZDOVars.s_tamed))
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
                        //
                        // And only when the object is inside the CALLER's active area, vanilla's own test. "Any
                        // peer covers the sector" (the 2-zone near radius) also holds for the ring between 1.5 and 2 zones,
                        // where no owner ever covers it on vanilla's test, so two players would take that ring from each
                        // other on every pass, each hand-over resent to both clients.
                        if (uid != serverUid && ZNetScene.InActiveArea(zdo.GetPosition(), zone))
                        {
                            NoteHandOff(zdo.m_uid, owner, uid, now);
                            zdo.SetOwner(uid);
                            s_nonSimulatedTransfersToPeer++;
                        }
                    }
                }
            }

            s_zdosProcessed += zdosThisPass;
            s_simulatedSeen += simulatedThisPass;


            // ~10 s rollup. simulatedSeen vs zdosProcessed tells you what
            // fraction of nearby ZDOs are even candidates for server-side
            // simulation. nonSimulatedTransfersToPeer shows interactables
            // flowing to peers via vanilla path.
            if (now >= s_nextStatLogTime)
            {
                if (LoggerOptions.DebugEnabled) LoggerOptions.LogDebug(
                    $"[ServerOwnership-V3] Last 10s: {s_passCount} passes, {s_zdosProcessed} ZDOs processed "
                    + $"({s_simulatedSeen} simulated-class), "
                    + $"{s_transfersToServer} transfers-to-server, {s_takenOver} taken over from a present owner, "
                    + $"{s_interactablesReturned} server-owned interactable(s) handed to the covering player, "
                    + $"{s_nonSimulatedTransfersToPeer} transfers-to-peer (non-simulated), "
                    + $"{s_releases} releases-to-unowned, "
                    + $"{s_flipBacks} handed back to their previous peer within {FlipBackSeconds:F0} s.");
                s_flipBacks = 0;
                PruneHandOffs(now);
                s_passCount = 0;
                s_zdosProcessed = 0;
                s_simulatedSeen = 0;
                s_transfersToServer = 0;
                s_takenOver = 0;
                s_interactablesReturned = 0;
                s_nonSimulatedTransfersToPeer = 0;
                s_releases = 0;
                s_nextStatLogTime = now + 10f;
            }

            return false; // we handled it; skip vanilla
        }

        // A peer hand-over that undoes one made moments ago is ownership ping-pong; counted so a regression of the
        // ring fix shows in the 10 s rollup (healthy = 0, or a handful when players cross each other's areas).
        private const float FlipBackSeconds = 10f;
        private struct HandOff { public long From, To; public float At; }
        private static readonly Dictionary<ZDOID, HandOff> s_handOffs = new Dictionary<ZDOID, HandOff>();
        private static readonly List<ZDOID> s_expiredHandOffs = new List<ZDOID>();
        private static int s_flipBacks;

        private static void NoteHandOff(ZDOID id, long from, long to, float now)
        {
            if (s_handOffs.TryGetValue(id, out HandOff last) && last.From == to && last.To == from
                && now - last.At <= FlipBackSeconds)
                s_flipBacks++;
            s_handOffs[id] = new HandOff { From = from, To = to, At = now };
        }

        private static void PruneHandOffs(float now)
        {
            s_expiredHandOffs.Clear();
            foreach (var entry in s_handOffs)
                if (now - entry.Value.At > FlipBackSeconds) s_expiredHandOffs.Add(entry.Key);
            foreach (ZDOID id in s_expiredHandOffs) s_handOffs.Remove(id);
        }
    }
}
