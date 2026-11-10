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
                LoggerOptions.LogDebug(
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

            Vector2s zone = FgnRing.Sector(refPosition);

            __instance.m_tempNearObjects.Clear();
            // Near band only (far distance 0).
            __instance.FindSectorObjects(zone, new SimulationDistance(SimDistance.Near(), 0, SimDistance.Synced().IsClassic), __instance.m_tempNearObjects);   // 1.6.31: classic as vanilla's ReleaseNearbyZDOS passes it

            // [wishbone] server N39: with 'Spread Server Ownership Sweep' on, the per-object rule below runs from ServerSweep's
            // budgeted queue over the next frames (bounded by its max delay, under vanilla's 2 s pass interval) instead of all here.
            if (ServerSweep.SpreadActive)
                ServerSweep.Enqueue(__instance.m_tempNearObjects, uid, serverUid, zone);
            else
                foreach (var zdo in __instance.m_tempNearObjects)
                    ProcessOne(__instance, zdo, uid, serverUid, zone);

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

        /// <summary>One object of a V2 pass: the loop body of ReleaseNearbyZDOS_Prefix, also called by ServerSweep's queue.</summary>
        internal static void ProcessOne(ZDOMan zdoMan, ZDO zdo, long uid, long serverUid, Vector2s zone)
        {
            if (zdo == null || !zdo.Persistent) return;
            // 1.6.31: skipped only when the simulated set is missing. With it, a portal takes the peer-owned branch below, which is what
            // TPP needs; skipping it here left unowned portals with no owner and server-owned ones with the server (same fault as carts, 1.5.80).
            if (_portalExclusionActive && !ServerOwnershipPatchesV3.IsBuilt && _teleportWorldPrefabs.Contains(zdo.m_prefab)) return;
            // 1.5.80 (Fire 10-26: cart storage would not open): carts used to be skipped here, but this pass REPLACES vanilla's,
            // so a skipped cart never got an owner and its Container.RequestOpen went unanswered. They now take the peer-owned
            // branch below (vanilla's rule: the covering player owns it, never the server).
            ServerStatusDiagnostics.s_ownership_zdosProcessed++;
            ApplySssOwnershipRule(zdoMan, zdo, uid, serverUid, zone);
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

            // 1.6.24 ([lead] + [ghost] 10-30): one generic opt-in, no per-object rule. A ZDO flagged "fgn_server_keeps" (set server
            // side by its spawner, e.g. the God Complex walk clone) stays server-owned and is never handed to a peer. Core's
            // CompanionOwnershipKeeper honours the same flag.
            if (zdo.GetBool(ServerKeepsKey))
            {
                if (owner != serverUid) zdo.SetOwner(serverUid);
                NoteServerKeeps(zdo.m_prefab);
                return;
            }

            // ItemDrops (pickup runs on the owner), TerrainComp (edits apply on its owner) and pieces/interactables must not be
            // server-owned, or pickups never complete and terrain edits are lost. Only V3's simulated set (Characters, ships when
            // ship sim is on) goes to the server; everything else follows vanilla's rule, and one the server already holds goes
            // back to the covering player.
            if ((ServerOwnershipPatchesV3.IsBuilt && !ServerOwnershipPatchesV3.IsSimulatedPrefab(zdo.m_prefab)) || _cartPrefabs.Contains(zdo.m_prefab))
            {
                _nonCharacterSeen++;
                bool inCallerArea = callerUid != serverUid && ZNetScene.InActiveArea(zdo.GetPosition(), callerZone);
                if (callerUid != serverUid && (owner == callerUid || owner == serverUid) && IsCharacterPrefab(zdo.m_prefab))
                    NoteKept(zdo.m_prefab, ServerOwnershipPatchesV3.IsBuilt ? "not in the simulated set" : "simulated set not built");
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

            if (ValcastPossession.Handle(zdo)) { if (callerUid != serverUid && zdo.GetOwner() == callerUid) NoteKept(zdo.m_prefab, "possessed"); return; }   // a possessed creature stays with its possessor's peer
            owner = zdo.GetOwner();
            // 1.6.16 ([lead] 10-30: 1.6.15 took nothing back in Fire's 03:23 session, 16 still owned): why a player keeps a creature
            if (owner == callerUid && callerUid != serverUid && coveredByAnyPeer)
            {
                if (zdo.GetBool(ZDOVars.s_tamed) && !ServerTakesThisTame(zdo)) NoteKept(zdo.m_prefab, ServerTakesTames ? "tamed, following a player" : "tamed");
                else if (!ReclaimFromPlayers) NoteKept(zdo.m_prefab, "key off");
                else if (HelmOwnership.HelmsmanHolds(zdo)) NoteKept(zdo.m_prefab, "at the helm");
            }

            if (owner == callerUid || owner == serverUid)
            {
                if (!coveredByAnyPeer)
                {
                    zdo.SetOwner(0L);
                    ServerStatusDiagnostics.s_ownership_releases++;
                    return;
                }
                // 1.6.15 ([lead] 10-30, Fire's client owned 10 town Dvergers with server-side simulation on: 74% of its character
                // time): a simulated creature a PLAYER already owns was kept by that player here forever (the SSS port only claimed
                // ones whose owner had left). It now goes to the server too; tamed ones stay with the player (vanilla's rule above).
                if (owner == callerUid && callerUid != serverUid && ReclaimFromPlayers && (!zdo.GetBool(ZDOVars.s_tamed) || ServerTakesThisTame(zdo)) && !HelmOwnership.HelmsmanHolds(zdo))   // 1.6.31: never from the helmsman
                {
                    zdo.SetOwner(serverUid);
                    ServerStatusDiagnostics.s_ownership_transfersToServer++;
                    NoteReclaim(zdo.m_prefab);
                    if (zdo.GetBool(ZDOVars.s_tamed)) NoteTame(zdo, "idle / staying tame: server simulates it");
                }
                // 1.6.22 ([lead] 10-30: a server-owned follower followed in 1 of 2 runs): a tame the server holds that is now FOLLOWING the
                // player of this pass goes back to that player, so follow stays vanilla (the follower's owner is the one it follows)
                else if (owner == serverUid && callerUid != serverUid && ServerTakesTames && zdo.GetBool(ZDOVars.s_tamed) && FollowsCaller(zdo, callerUid))
                {
                    zdo.SetOwner(callerUid);
                    NoteTame(zdo, "following this player: handed back to them");
                }
                // 1.6.31: with the key off a tame stays with the player, but one tamed while the server owned it stayed on the server. It goes to the player in range, on vanilla's area test.
                else if (owner == serverUid && callerUid != serverUid && !ServerTakesTames && zdo.GetBool(ZDOVars.s_tamed) && ZNetScene.InActiveArea(zdo.GetPosition(), callerZone))
                {
                    zdo.SetOwner(callerUid);
                    NoteTameReturned(zdo.m_prefab);
                }
                return;
            }

            // IsInPeerActiveArea takes a world point; vanilla passes the ZDO's own position here.
            bool currentOwnerCovers = owner != 0L && zdoMan.IsInPeerActiveArea(zdo.GetPosition(), owner);
            if (!currentOwnerCovers && coveredByAnyPeer)
            {
                // A tamed ZDO (Core companions) never goes to the server: vanilla's rule, the covering peer takes it.
                if (zdo.GetBool(ZDOVars.s_tamed) && !ServerTakesThisTame(zdo))
                {
                    if (callerUid != serverUid) zdo.SetOwner(callerUid);
                    return;
                }
                zdo.SetOwner(serverUid);
                ServerStatusDiagnostics.s_ownership_transfersToServer++;
            }
        }

        // 1.6.15: "Server Takes Creatures Back From Players" (default ON, a fix): Info once, then a Debug line every 10 s by prefab.
        internal static BepInEx.Configuration.ConfigEntry<bool> ConfigReclaimFromPlayers;
        private static bool ReclaimFromPlayers => ConfigReclaimFromPlayers == null || ConfigReclaimFromPlayers.Value;
        private static readonly Dictionary<int, int> s_reclaimed = new Dictionary<int, int>();
        private static bool s_reclaimInfo;
        private static float s_nextReclaimLog;

        // 1.6.17 ([lead] 10-30, rig A/B; the shipping default is Fire's call): with broad server ownership, tamed creatures that are
        // not companions (town Dvergers, wolves, lox; Core companions stay excluded by prefab) go to the server like the rest. 1.6.16
        // proved the 10 town Dvergers Fire's game owned were kept "tamed" (74% of its character time). Off = tames stay with the player.
        internal static BepInEx.Configuration.ConfigEntry<bool> ConfigServerTakesTames;
        internal static bool ServerTakesTames => ConfigServerTakesTames != null && ConfigServerTakesTames.Value;

        // 1.6.22: only tames NOT following a player go to the server (vanilla ZDO "follow" is the followed player's name, "" when staying)
        private static bool ServerTakesThisTame(ZDO zdo) => ServerTakesTames && string.IsNullOrEmpty(zdo.GetString(ZDOVars.s_follow));
        private static bool FollowsCaller(ZDO zdo, long callerUid)
        {
            string follow = zdo.GetString(ZDOVars.s_follow);
            if (string.IsNullOrEmpty(follow)) return false;
            ZNetPeer peer = ZNet.instance != null ? ZNet.instance.GetPeer(callerUid) : null;
            return peer != null && peer.m_playerName == follow;
        }
        private static bool s_tameInfo;
        private static void NoteTame(ZDO zdo, string why)
        {
            if (!s_tameInfo) { s_tameInfo = true; LoggerOptions.LogInfo($"[ServerOwnership] tamed {PrefabName(zdo.m_prefab)}: {why} (Server Simulates Tamed Creatures Too)"); }
            else if (LoggerOptions.DebugEnabled) LoggerOptions.LogDebug($"[ServerOwnership] tamed {PrefabName(zdo.m_prefab)} at {zdo.GetPosition():F0}: {why}");
        }

        // 1.6.31: Debug every 10 s at most, shared by both ownership passes
        private static int s_tamesReturned;
        private static float s_nextTameReturnLog;
        internal static void NoteTameReturned(int prefab)
        {
            s_tamesReturned++;
            float now = Time.realtimeSinceStartup;
            if (!LoggerOptions.DebugEnabled || now < s_nextTameReturnLog) return;
            s_nextTameReturnLog = now + 10f;
            LoggerOptions.LogDebug($"[ServerOwnership] {s_tamesReturned} server-held tame(s) handed to the player in range, last {PrefabName(prefab)}: 'Server Simulates Tamed Creatures Too' is off.");
            s_tamesReturned = 0;
        }

        internal static void InitConfig(BepInEx.Configuration.ConfigFile config)
        {
            ConfigServerTakesTames = config.Bind("10 - Server Authority", "Server Simulates Tamed Creatures Too", false,
                Cfg.Adv("With broad server ZDO ownership on: tamed creatures (not companions) are simulated by the server too, instead of staying with the nearest player. Test setting."));
            ConfigReclaimFromPlayers = config.Bind("10 - Server Authority", "Server Takes Creatures Back From Players", true,
                Cfg.Adv("With broad server ZDO ownership on: a creature a player's game already owns (not a tame) is taken back by the server, like every other creature in the area. Off = the player keeps it."));
        }

        private static void NoteReclaim(int prefab)
        {
            s_reclaimed.TryGetValue(prefab, out int c); s_reclaimed[prefab] = c + 1;
            if (!s_reclaimInfo)
            {
                s_reclaimInfo = true;
                LoggerOptions.LogDebug($"[ServerOwnership] took a creature back from a player's game ({PrefabName(prefab)}); the server simulates it now");
            }
            float now = Time.realtimeSinceStartup;
            if (now < s_nextReclaimLog) return;
            s_nextReclaimLog = now + 10f;
            var sb = new System.Text.StringBuilder();
            foreach (var kv in s_reclaimed) sb.Append(sb.Length == 0 ? "" : ", ").Append(PrefabName(kv.Key)).Append(" x").Append(kv.Value);
            LoggerOptions.LogDebug($"[ServerOwnership] taken back from players (last 10 s): {sb}");
            s_reclaimed.Clear();
        }

        // 1.6.16: Debug every 10 s, "[ServerOwnership] kept by players (last 10 s): Dverger x9 (tamed), ..." (one count per pass)
        private static readonly Dictionary<string, int> s_kept = new Dictionary<string, int>();
        private static readonly Dictionary<int, bool> s_isCharacter = new Dictionary<int, bool>();
        private static float s_nextKeptLog;

        private static bool IsCharacterPrefab(int hash)
        {
            if (s_isCharacter.TryGetValue(hash, out bool c)) return c;
            GameObject p = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
            c = p != null && p.GetComponent<Character>() != null && p.GetComponent<Player>() == null;
            s_isCharacter[hash] = c;
            return c;
        }

        public const string ServerKeepsKey = "fgn_server_keeps";
        private static readonly Dictionary<string, int> s_serverKeeps = new Dictionary<string, int>();
        private static float s_nextServerKeepsLog;

        private static void NoteServerKeeps(int prefab)
        {
            if (!LoggerOptions.DebugEnabled) return;
            string k = PrefabName(prefab);
            s_serverKeeps.TryGetValue(k, out int n); s_serverKeeps[k] = n + 1;
            float now = Time.realtimeSinceStartup;
            if (now < s_nextServerKeepsLog) return;
            s_nextServerKeepsLog = now + 10f;
            var sb = new System.Text.StringBuilder();
            foreach (var kv in s_serverKeeps) sb.Append(sb.Length == 0 ? "" : ", ").Append(kv.Key).Append(" x").Append(kv.Value);
            LoggerOptions.LogDebug($"[ServerOwnership] kept by the server (fgn_server_keeps, last 10 s, counted per pass): {sb}");
            s_serverKeeps.Clear();
        }

        private static void NoteKept(int prefab, string why)
        {
            if (!LoggerOptions.DebugEnabled) return;
            string k = PrefabName(prefab) + " (" + why + ")";
            s_kept.TryGetValue(k, out int n); s_kept[k] = n + 1;
            float now = Time.realtimeSinceStartup;
            if (now < s_nextKeptLog) return;
            s_nextKeptLog = now + 10f;
            var sb = new System.Text.StringBuilder();
            foreach (var kv in s_kept) sb.Append(sb.Length == 0 ? "" : ", ").Append(kv.Key).Append(" x").Append(kv.Value);
            LoggerOptions.LogDebug($"[ServerOwnership] kept by players (last 10 s, counted per pass): {sb}");
            s_kept.Clear();
        }

        private static string PrefabName(int hash)
        {
            GameObject p = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
            return p != null ? p.name : hash.ToString();
        }

        private static bool SectorCoveredByAnyConnectedPeer(Vector2s sector)
        {
            int near = SimDistance.Near();
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer == null) continue;
                // ZNetScene.InActiveArea takes a world point, not a sector, so compare zones directly.
                if (SimDistance.ZoneInRadius(FgnRing.Sector(peer.GetRefPos()), sector, near))
                    return true;
            }
            return false;
        }

        private static void LogFirstFireOnce(long serverUid)
        {
            if (_firstFireLogged) return;
            _firstFireLogged = true;
            LoggerOptions.LogDebug(
                $"[ServerOwnership] Server-side ZDO ownership transfer engaged. "
                + $"serverUid={serverUid}, peers={ZNet.instance.GetPeers().Count}. "
                + $"(First ZDOMan.ReleaseNearbyZDOS pass on this session; further passes aggregate into [ServerStatus].)");
        }
    }
}
