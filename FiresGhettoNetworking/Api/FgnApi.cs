using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresGhettoNetworkMod.Api
{
    /// <summary>
    /// FGN's PUBLIC API for add-ons, so none of them needs FGN's internals.
    /// CONTRACT: add-only. Never rename or remove a member or a setting key, never change a signature; raise <see cref="Version"/>
    /// when adding. Main thread only. Nothing here throws into the caller: a refused request returns false and logs one line.
    /// </summary>
    public static class FgnApi
    {
        /// <summary>1 = settings read/override, hand-off, routed-RPC filters. 2 = live state: GetRole, GetPeerCount, IsSimulationActive, GetTraffic. 3 =ExcludeFromServerSimulation / IsExcludedFromServerSimulation.</summary>
        public const int Version = 9;   // 9 = RegisterDistantPrefab / UnregisterDistantPrefab / RefreshDistantPrefabs / DistantPrefabCount, IsAutoTuneEnabled / IsAutoTuneSettled / AutoTuneSettled
        // was 8:   // 8 = GetStatsRows + StatsSection*, RegisterFarIdleSkip / UnregisterFarIdleSkip, KinematicSweepEnabled (FGN looks up no other mod)
        // was 7:   // 7 = RegisterServerRemovalKeep / UnregisterServerRemovalKeep (a mod keeps its own objects through FGN's sliced server removal)
        // was 6:   // 6 = SetKeepAliveSettings (the owner mod passes the keep-alive and homestead settings)
        // was 5:   // 4 = companion keep-alive: AddKeepAlive / RemoveKeepAlive / GetKeepAliveState / GetKeepAliveCounts
                                        // 5 = AddKeepAlive(pinned), homesteads (AddHomestead / RemoveHomestead / GetHomesteadState / GetHomesteadCounts),
                                        //     IsServerSimulationActiveForClient / IsHomesteadAvailable

        // ---------------- 1. settings ----------------

        private sealed class Key
        {
            internal string Name;
            internal Func<ConfigEntry<bool>> Get;          // null = not a config entry (read-only in v1)
            internal Action<ConfigEntry<bool>> Set;
            internal Func<bool> Read;                       // for read-only keys
        }

        private static readonly Dictionary<string, Key> s_keys = new Dictionary<string, Key>(StringComparer.Ordinal)
        {
            ["server.simulation"] = new Key { Name = "server.simulation", Get = () => FiresGhettoNetworkMod.ConfigEnableServerAuthority, Set = e => FiresGhettoNetworkMod.ConfigEnableServerAuthority = e },
            ["server.ownership"] = new Key { Name = "server.ownership", Get = () => FiresGhettoNetworkMod.ConfigEnableServerOwnership, Set = e => FiresGhettoNetworkMod.ConfigEnableServerOwnership = e },
            ["server.ownership.selective"] = new Key { Name = "server.ownership.selective", Get = () => FiresGhettoNetworkMod.ConfigEnableServerOwnershipSelective, Set = e => FiresGhettoNetworkMod.ConfigEnableServerOwnershipSelective = e },
            ["creatures.ownership"] = new Key { Name = "creatures.ownership", Get = () => CreatureOwnership.ConfigEnabled, Set = e => CreatureOwnership.ConfigEnabled = e },
            ["stations.routing"] = new Key { Name = "stations.routing", Get = () => StationRouter.ConfigEnabled, Set = e => StationRouter.ConfigEnabled = e },
            ["ships.serverOwns"] = new Key { Name = "ships.serverOwns", Read = () => HelmOwnership.ServerOwnsShips },
            ["creatures.serverOwns"] = new Key { Name = "creatures.serverOwns", Read = () => CreatureOwnership.ServerOwnsCreatures },
        };

        private sealed class Override { internal string Mod, Reason; internal bool Value; internal ConfigEntry<bool> Original; }
        private static readonly Dictionary<string, Override> s_overrides = new Dictionary<string, Override>(StringComparer.Ordinal);
        private static ConfigFile s_session;
        internal static ConfigEntry<string> ConfigAllowOverrides;

        internal static void InitConfig(ConfigFile config)
        {
            ConfigAllowOverrides = config.Bind("15 - API", "Allow Overrides", "all",
                Cfg.Adv("Which FGN settings other mods may change for a session: all, none, or a comma list of setting names."));
        }

        /// <summary>The value FGN acts on right now (an override included). False for an unknown key.</summary>
        public static bool GetSetting(string key)
        {
            if (key == null || !s_keys.TryGetValue(key, out Key k)) return false;
            if (k.Read != null) return k.Read();
            ConfigEntry<bool> e = k.Get();
            return e != null && e.Value;
        }

        public static string[] GetSettingKeys()
        {
            var keys = new string[s_keys.Count];
            s_keys.Keys.CopyTo(keys, 0);
            return keys;
        }

        public static bool IsOverridden(string key, out string byMod, out string reason)
        {
            byMod = reason = null;
            if (key == null || !s_overrides.TryGetValue(key, out Override o)) return false;
            byMod = o.Mod; reason = o.Reason;
            return true;
        }

        /// <summary>For this session only, FGN uses <paramref name="value"/> for <paramref name="key"/>. Only before the world starts
        /// (ZNet not up yet); the first mod to ask wins; the server owner's 'Allow Overrides' decides. The cfg file is never written.</summary>
        public static bool RequestOverride(string key, bool value, string modGuid, string reason)
        {
            string who = string.IsNullOrEmpty(modGuid) ? "an unnamed mod" : modGuid;
            if (key == null || !s_keys.TryGetValue(key, out Key k)) return Refuse($"{who} asked to override unknown key '{key}'");
            if (k.Get == null) return Refuse($"{who} asked to override '{key}', which is read-only in API v{Version}");
            if (ZNet.instance != null) return Refuse($"{who} asked to override '{key}' after the world started (overrides must come before)");
            if (!Allowed(key)) return Refuse($"{who} asked to override '{key}'; the server's [15 - API] Allow Overrides refuses it");
            if (s_overrides.TryGetValue(key, out Override had) && had.Mod != who) return Refuse($"{who} asked to override '{key}', already overridden by {had.Mod}");
            ConfigEntry<bool> ours = had != null ? had.Original : k.Get();
            if (ours == null) return Refuse($"{who} asked to override '{key}' before FGN bound it");
            if (s_session == null) s_session = new ConfigFile(ours.ConfigFile.ConfigFilePath, false) { SaveOnConfigSet = false };
            ConfigEntry<bool> session = s_session.Bind(ours.Definition, (bool)ours.DefaultValue, ours.Description);
            session.Value = value;   // in memory only: the session file is never saved
            k.Set(session);
            s_overrides[key] = new Override { Mod = who, Reason = reason ?? "", Value = value, Original = ours };
            LoggerOptions.LogMessage($"[FgnApi] {key} = {value}, overridden by {who}{(string.IsNullOrEmpty(reason) ? "" : $" (\"{reason}\")")}; your setting {ours.Value} returns when {who} is removed.");
            return true;
        }

        public static void ReleaseOverride(string key, string modGuid)
        {
            if (key == null || !s_overrides.TryGetValue(key, out Override o) || o.Mod != (modGuid ?? "an unnamed mod")) return;
            s_keys[key].Set(o.Original);
            s_overrides.Remove(key);
            LoggerOptions.LogMessage($"[FgnApi] {key} back to your setting ({o.Original.Value}); {o.Mod} released it.");
        }

        private static bool Allowed(string key)
        {
            string allow = ConfigAllowOverrides != null ? (ConfigAllowOverrides.Value ?? "").Trim().ToLowerInvariant() : "all";
            if (allow == "all" || allow == "") return true;
            if (allow == "none") return false;
            foreach (string part in allow.Split(',')) if (part.Trim() == key) return true;
            return false;
        }

        private static bool Refuse(string why) { LoggerOptions.LogWarning("[FgnApi] refused: " + why); return false; }

        // ---------------- 2. hand-off ----------------

        /// <summary>(id, target, accepted, reason) for hand-offs this API started; main thread.</summary>
        public static event Action<ZDOID, long, bool, string> OnHandOffResult;
        private static readonly Dictionary<ZDOID, long> s_apiPending = new Dictionary<ZDOID, long>();

        /// <summary>Server/host only: ask FGN to move ownership of <paramref name="id"/> to <paramref name="targetPeer"/> through its
        /// own hand-off protocol (version gate, vetoes, the owner's answer). The answer comes as <see cref="OnHandOffResult"/>.</summary>
        public static bool RequestHandOff(ZDOID id, long targetPeer, string modGuid)
        {
            string who = string.IsNullOrEmpty(modGuid) ? "an unnamed mod" : modGuid;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return Refuse($"{who} asked for a hand-off on a client (server/host only)");
            if (!CanHandOff(id, targetPeer, out string why)) return Refuse($"{who} asked to hand {id} to {targetPeer}: {why}");
            string sent = CreatureOwnership.ApiHandOff(id, targetPeer);
            if (sent != null) return Refuse($"{who} asked to hand {id} to {targetPeer}: {sent}");
            s_apiPending[id] = targetPeer;
            return true;
        }

        /// <summary>FGN's own checks, without asking anyone: the object exists and has an owner, the target is a ready peer on FGN,
        /// and the object is not tamed, in use, held or possessed.</summary>
        public static bool CanHandOff(ZDOID id, long targetPeer, out string reason)
        {
            reason = CreatureOwnership.ApiCheck(id, targetPeer);
            return reason == null;
        }

        /// <summary>From CreatureOwnership's result handler (and its local path).</summary>
        internal static void NotifyResult(ZDOID id, bool accepted, string reason)
        {
            if (!s_apiPending.TryGetValue(id, out long target)) return;
            s_apiPending.Remove(id);
            try { OnHandOffResult?.Invoke(id, target, accepted, reason ?? ""); }
            catch (Exception ex) { LoggerOptions.LogWarning($"[FgnApi] an OnHandOffResult handler threw {ex.GetType().Name}: {ex.Message}"); }
        }

        // ---------------- 3. routed-RPC filters ----------------

        /// <summary>Server side, before FGN routes a routed RPC. Return true = handled, FGN does nothing more with it. The package
        /// is shared: read it, its position is put back after each filter.</summary>
        public delegate bool RoutedRpcFilter(long sender, ZDOID target, int methodHash, ZPackage pkg);

        private sealed class Filter { internal string Mod; internal RoutedRpcFilter Fn; internal int Priority; internal double Ms; internal long Calls; }
        private static readonly List<Filter> s_filters = new List<Filter>();

        public static void RegisterRoutedRpcFilter(string modGuid, RoutedRpcFilter filter, int priority)
        {
            if (filter == null) return;
            UnregisterRoutedRpcFilter(modGuid);
            s_filters.Add(new Filter { Mod = string.IsNullOrEmpty(modGuid) ? "an unnamed mod" : modGuid, Fn = filter, Priority = priority });
            s_filters.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            LoggerOptions.LogDebug($"[FgnApi] routed-RPC filter registered by {modGuid} (priority {priority})");
        }

        public static void UnregisterRoutedRpcFilter(string modGuid) => s_filters.RemoveAll(f => f.Mod == modGuid);

        /// <summary>From RoutedRpcManager.ProcessRoutedRPC, right after the data is read. True = a filter handled it.</summary>
        internal static bool RunFilters(ZRoutedRpc.RoutedRPCData data)
        {
            if (s_filters.Count == 0 || data == null) return false;
            for (int i = 0; i < s_filters.Count; i++)
            {
                Filter f = s_filters[i];
                ZPackage pkg = data.m_parameters;
                int pos = pkg != null ? pkg.GetPos() : 0;
                float t0 = Time.realtimeSinceStartup;
                bool handled;
                try { handled = f.Fn(data.m_senderPeerID, data.m_targetZDO, data.m_methodHash, pkg); }
                catch (Exception ex)
                {
                    LoggerOptions.LogWarning($"[FgnApi] {f.Mod}'s routed-RPC filter threw {ex.GetType().Name}: {ex.Message}; it is removed");
                    s_filters.RemoveAt(i--);
                    if (pkg != null) pkg.SetPos(pos);
                    continue;
                }
                f.Ms += (Time.realtimeSinceStartup - t0) * 1000.0; f.Calls++;
                if (pkg != null) pkg.SetPos(pos);
                if (handled) return true;
            }
            return false;
        }

        // ---------------- 4. live state (v2) ----------------

        /// <summary>"offline" (no ZNet), "dedicated", "host" (a listen server, solo included) or "client".</summary>
        public static string GetRole()
        {
            ZNet n = ZNet.instance;
            if (n == null) return "offline";
            return n.IsDedicated() ? "dedicated" : n.IsServer() ? "host" : "client";
        }

        /// <summary>Connected peers (a client counts its server as 1).</summary>
        public static int GetPeerCount() => ZNet.instance != null ? ZNet.instance.GetConnectedPeers().Count : 0;

        /// <summary>True while Server-Side Simulation runs on this dedicated server (false on clients and hosts).</summary>
        public static bool IsSimulationActive() => ServerAuthorityPatches.SimulationActive;

        /// <summary>Traffic summed over all connected peers, from the sockets' own counters: out/in KB/s, bytes waiting in send queues,
        /// and the worst ping in ms. False when there are no peers (outputs 0). Cheap: one pass over the peer list.</summary>
        public static bool GetTraffic(out float outKBps, out float inKBps, out int sendQueueBytes, out int worstPingMs)
        {
            outKBps = inKBps = 0f; sendQueueBytes = 0; worstPingMs = 0;
            ZNet n = ZNet.instance; if (n == null) return false;
            var peers = n.GetConnectedPeers(); if (peers.Count == 0) return false;
            foreach (ZNetPeer p in peers)
            {
                ISocket s = p?.m_socket; if (s == null) continue;
                try
                {
                    s.GetConnectionQuality(out _, out _, out int ping, out float o, out float i);
                    outKBps += o / 1024f; inKBps += i / 1024f; if (ping > worstPingMs) worstPingMs = ping;
                    sendQueueBytes += s.GetSendQueueSize();
                }
                catch { }
            }
            return true;
        }

        // ---------------- 5. server-simulation opt-out (v3) ----------------

        /// <summary>
        /// Keeps these prefabs out of the set the server takes ownership of and simulates (Server ZDO Ownership). Use it for creatures
        /// whose AI / animation / physics your mod runs on the CLIENT: the server would own them and nothing would animate them.
        /// They are also never moved between players by creature balancing. Prefab names, exact. Safe to call any time (before or after the set is built); repeat calls add. The Fires companions
        /// (CompanionNpc, CompanionNpc_Wild) are left out by FGN itself.
        /// </summary>
        public static void ExcludeFromServerSimulation(string modGuid, params string[] prefabNames)
        {
            if (prefabNames == null) return;
            int added = 0;
            foreach (string n in prefabNames) if (!string.IsNullOrEmpty(n) && global::FiresGhettoNetworkMod.ServerOwnershipPatchesV3.Exclude(n.Trim())) added++;
            LoggerOptions.LogDebug($"[FgnApi] {(string.IsNullOrEmpty(modGuid) ? "an unnamed mod" : modGuid)} left {added} prefab(s) out of server simulation: {string.Join(", ", prefabNames)}");
        }

        /// <summary>True when FGN leaves this prefab to the clients (built-in or registered with <see cref="ExcludeFromServerSimulation"/>).</summary>
        public static bool IsExcludedFromServerSimulation(string prefabName)
            => !string.IsNullOrEmpty(prefabName) && global::FiresGhettoNetworkMod.ServerOwnershipPatchesV3.IsExcluded(prefabName.Trim().GetStableHashCode());

        // ---------------- 6. companion keep-alive (v4) ----------------

        /// <summary>
        /// Dedicated server with Server-Side Simulation and 'Companion Keep-Alive' on: keeps the world around <paramref name="where"/>
        /// built and simulated like a player's area while no player is near, and the server owns the client-simulated companions
        /// (FGN's excluded set: the Fires companions and ExcludeFromServerSimulation prefabs) inside it, handing each back to the first
        /// player who comes in range. One call per AREA (the caller groups its companions); <paramref name="id"/> is the caller's key,
        /// a repeat call replaces the position source. <paramref name="where"/> is read once a second on the main thread; return null
        /// when the area is gone (it stops counting until it returns a point again, or RemoveKeepAlive). Points are not merged with each
        /// other, only folded into a player's area. Capped by 'Max Companion Keep-Alive Areas' and FGN's frame and free-memory guards,
        /// oldest first. False only for a null id or position source.
        /// </summary>
        public static bool AddKeepAlive(string id, Func<Vector3?> where) => global::FiresGhettoNetworkMod.CompanionKeepAlive.Add(id, where, ZDOID.None);

        /// <summary>Stops keeping that area. Companions the server holds stay the server's until a player comes in range (then handed back).</summary>
        public static bool RemoveKeepAlive(string id) => global::FiresGhettoNetworkMod.CompanionKeepAlive.Remove(id);

        /// <summary>One area's state: "active", "covered" (a player's area has it), "settled" (idle), "refused-cap", "refused-frame",
        /// "refused-memory", "gone" (its position is null), "off" (feature off / not a dedicated server), "unknown" (never added).</summary>
        public static string GetKeepAliveState(string id) => global::FiresGhettoNetworkMod.CompanionKeepAlive.StateOf(id);

        /// <summary>Areas kept now, areas registered, the cap (0 = off) and the guard ("ok", "frame (hold)", "frame (settling)",
        /// "memory (hold)", "memory (settling)").</summary>
        public static void GetKeepAliveCounts(out int active, out int registered, out int cap, out string guard)
        {
            active = global::FiresGhettoNetworkMod.CompanionKeepAlive.Active;
            registered = global::FiresGhettoNetworkMod.CompanionKeepAlive.Registered;
            cap = global::FiresGhettoNetworkMod.CompanionKeepAlive.Cap;
            guard = global::FiresGhettoNetworkMod.CompanionKeepAlive.Guard;
        }

        /// <summary>v6 (1.6.28): the keep-alive and homestead settings, from the mod that owns those config keys (FiresCompanions). Call it
        /// once at start and again when a key changes; values are clamped to the old key ranges. Until it is called both features are off.</summary>
        public static void SetKeepAliveSettings(bool companionOn, int maxAreas, int idleMinutes, float holdMs, float settleMs,
            float memHoldPercent, float memHoldMinGB, float memSettlePercent, bool homeOn, int homeMax, int homesPerOwner)
            => global::FiresGhettoNetworkMod.CompanionKeepAlive.ApplySettings(companionOn, maxAreas, idleMinutes, holdMs, settleMs,
                memHoldPercent, memHoldMinGB, memSettlePercent, homeOn, homeMax, homesPerOwner);

        // ---------------- homesteads (v5) ----------------

        /// <summary>AddKeepAlive with <paramref name="pinned"/> = true: the area never idle-settles (a point that never moves stays kept).</summary>
        public static bool AddKeepAlive(string id, Func<Vector3?> where, bool pinned) => global::FiresGhettoNetworkMod.CompanionKeepAlive.Add(id, where, ZDOID.None, pinned);

        /// <summary>
        /// Server: keep a homestead's base loaded and fully live while no player is near (dedicated server, Server-Side Simulation,
        /// 'Homestead Keep-Alive' on). Keyed by <paramref name="centre"/>: a call within 4 m of a registered centre replaces it, so
        /// re-register from the board ZDOs after a server start. <paramref name="radiusZones"/> is clamped to 1..3 (3 costs more than
        /// one player). Pinned (never idle-settles); own cap 'Max Homestead Areas' and 'Homesteads Per Owner'
        /// (<paramref name="ownerPlayerId"/>, 0 = no owner limit). Returns the state now ("new" until the next second's tick, or
        /// "refused-owner" / "off"); false only when refused or not the server.
        /// </summary>
        public static bool AddHomestead(Vector3 centre, long ownerPlayerId, int radiusZones, out string state)
        {
            state = global::FiresGhettoNetworkMod.CompanionKeepAlive.AddHome(centre, ownerPlayerId, radiusZones, out bool ok);
            return ok;
        }

        /// <summary>Server: stops keeping the homestead registered within 4 m of <paramref name="centre"/>.</summary>
        public static bool RemoveHomestead(Vector3 centre) => global::FiresGhettoNetworkMod.CompanionKeepAlive.RemoveHome(centre);

        /// <summary>Server: "active", "covered" (a player is there), "refused-cap", "refused-owner", "refused-frame", "refused-memory",
        /// "new", "off" (not available on this server), "unknown" (never added).</summary>
        public static string GetHomesteadState(Vector3 centre) => global::FiresGhettoNetworkMod.CompanionKeepAlive.HomeState(centre);

        /// <summary>Server: homesteads kept now, registered, the cap (0 = off) and the zones kept live by them.</summary>
        public static void GetHomesteadCounts(out int active, out int registered, out int cap, out int zones)
            => global::FiresGhettoNetworkMod.CompanionKeepAlive.HomeCounts(out active, out registered, out cap, out zones);

        /// <summary>Client or server: Server-Side Simulation runs on the server. A client learns it from the server's FGN hello
        /// (false until then, and against a server older than FGN 1.5.82).</summary>
        public static bool IsServerSimulationActiveForClient() => global::FiresGhettoNetworkMod.FgnVersionGate.ServerSimulationActive;

        /// <summary>Client or server: the server keeps homestead areas (the upgrade gate). Same source as IsServerSimulationActiveForClient.</summary>
        public static bool IsHomesteadAvailable() => global::FiresGhettoNetworkMod.FgnVersionGate.ServerHomesteadsAvailable;

        // ---------------- 9. network view (v5, 1.6.11; Fires God Complex / Studio Link by reflection) ----------------

        /// <summary>Version of the network-view calls below; 1 = GetPeerNetStats / GetZoneOwners / GetSimulationState.</summary>
        public const int NetViewApiVersion = 1;

        /// <summary>JSON array, one row per connected peer: peerId, name, platform (Steam / PlayFab / other), pingMs, outKBps, inKBps,
        /// sendQueueBytes, compressionRatio / backoff / tier (null until FGN keeps them per peer), quality (good / fair / poor / unknown:
        /// good = ping &lt; 100 ms and queue &lt; 64 KB, poor = ping &gt; 250 ms or queue &gt; 512 KB). Reads the sockets' own counters.</summary>
        public static string GetPeerNetStats() => FgnNetView.PeerNetStats();

        /// <summary>JSON array for the zones within radiusZones (max 10) of zone (cx, cz): x, z, simulatedBy (the owner of the zone's
        /// _ZoneCtrl ZDO: a peer id, "server" on a dedicated server, or null when nobody runs it), ownerName, zdoCountByPeer {peer: n}.
        /// Each zone is cached 2 s.</summary>
        public static string GetZoneOwners(int cx, int cz, int radiusZones) => FgnNetView.ZoneOwners(cx, cz, radiusZones);

        /// <summary>JSON: sss (Server-Side Simulation running), selectiveOwnership (the Selective toggle), serverZones [[x, z]...]
        /// (zones near a peer whose _ZoneCtrl the server owns), homesteads, keepAlive (active counts).</summary>
        public static string GetSimulationState() => FgnNetView.SimulationState();

        // ---------------- 7. server removal keep (Version 7, 1.6.62) ----------------
        // FGN depends on no other mod (Fire 11-08). A mod that must keep some objects loaded on a dedicated server (e.g. a companion mod's
        // kept areas) registers here instead; FGN never looks that mod up.

        /// <summary>
        /// Version 7. FGN's sliced server removal (it replaces vanilla ZNetScene.RemoveObjects on a dedicated server) asks this before it
        /// removes an instance: <paramref name="refresh"/> once per removal cycle (~2 s; return how many objects you keep, for the log),
        /// then <paramref name="isKept"/> per instance (true = leave it loaded). <paramref name="owner"/> is your Harmony id: if your mod
        /// also patches ZNetScene.RemoveObjects, registering tells FGN that patch is covered, so the sliced removal can run. Registering
        /// the same owner again replaces its callbacks. False (and one log line) on an empty owner or a null callback.
        /// </summary>
        public static bool RegisterServerRemovalKeep(string owner, Func<int> refresh, Func<ZDO, bool> isKept)
            => ServerStream.AddRemovalKeep(owner, refresh, isKept);

        /// <summary>Version 7. Removes a keep registered by <paramref name="owner"/>; false when none was.</summary>
        public static bool UnregisterServerRemovalKeep(string owner) => ServerStream.RemoveRemovalKeep(owner);

        // ---------------- 8. stats rows, far-idle skip, kinematic sweep key (Version 8, 1.6.63) ----------------
        // FGN looks up no other mod (Fire 11-08): a hub that shows stats PULLS FGN's rows; a mod whose creatures must stay full speed
        // REGISTERS a skip; the mod that runs the kinematic sweep READS FGN's key.

        /// <summary>Version 8. Suggested id, title, order and refresh (seconds) for a stats card built from <see cref="GetStatsRows"/>.</summary>
        public const string StatsSectionId = "fgn", StatsSectionTitle = "Networking (FGN)";
        public const int StatsSectionOrder = 200;
        public const float StatsSectionRefreshSeconds = 1f;

        /// <summary>
        /// Version 8. FGN's stats as rows of 4 strings: label, value, state ("normal", "good", "warn", "bad", "muted"), tooltip (may be null).
        /// Main thread; call it only while the rows are shown (it walks the peer list once). Never throws: on an error, an empty list.
        /// </summary>
        public static List<string[]> GetStatsRows()
        {
            try { return FgnStatsSection.Rows(); }
            catch (Exception e) { LoggerOptions.LogDebug($"[FGN] stats rows failed: {e.GetType().Name}: {e.Message}"); return new List<string[]>(); }
        }

        /// <summary>
        /// Version 8. "Slow Ticks For Far Idle Creatures" leaves a creature at full speed when <paramref name="keepFullSpeed"/> returns true
        /// for it (e.g. a companion). Called while FGN decides whether a far, idle, owned creature may be slowed. <paramref name="owner"/>
        /// names the caller in FGN's log; registering again replaces. A skip that throws is dropped (one Warning). False on bad args.
        /// </summary>
        public static bool RegisterFarIdleSkip(string owner, Func<Character, bool> keepFullSpeed) => FarIdleHandoff.ForwardSkip(owner, keepFullSpeed);   // 1.6.82: the job moved to FiresFixes

        /// <summary>Version 8. Removes the skip registered by <paramref name="owner"/>; false when none was.</summary>
        public static bool UnregisterFarIdleSkip(string owner) => FarIdleHandoff.ForwardRemove(owner);

        /// <summary>Version 8. FGN's "[Performance] Kinematic sync sweep" value (restart to apply); true before FGN's config is bound.
        /// From 1.6.64 FGN also runs the sweep itself (a mod that toggles a body can still set ZSyncTransform's flag in the same call).</summary>
        public static bool KinematicSweepEnabled => KinematicSweepOwner.Enabled == null || KinematicSweepOwner.Enabled.Value;

        // ---------------- 9. distant prefabs, auto-tune state (Version 9, 1.6.64) ----------------

        /// <summary>
        /// Version 9. Streams <paramref name="prefabName"/>'s objects out to <paramref name="maxDistanceMeters"/> (beyond the active area):
        /// the server marks its ZDOs Distant and force-sends them; clients cache the in-range ones and hand them to vanilla's distant
        /// creation, so vanilla creates and cleans them up. Registering again updates the distance; 0 or less unregisters. After a batch of
        /// registrations call <see cref="RefreshDistantPrefabs"/>. False on an empty name.
        /// </summary>
        public static bool RegisterDistantPrefab(string prefabName, float maxDistanceMeters) => DistantPrefabs.Register(prefabName, maxDistanceMeters);

        /// <summary>Version 9. Stops streaming <paramref name="prefabName"/> far; false when it wasn't registered.</summary>
        public static bool UnregisterDistantPrefab(string prefabName) => DistantPrefabs.Unregister(prefabName);

        /// <summary>Version 9. Rebuilds the client's distant cache and runs the server's mark + send now (instead of within 2 s / 10 s).</summary>
        public static void RefreshDistantPrefabs() => DistantPrefabs.Refresh();

        /// <summary>Version 9. How many prefabs are registered for distant streaming.</summary>
        public static int DistantPrefabCount => DistantPrefabs.Count;

        /// <summary>Version 9. FGN's client bandwidth auto-tune key ("Auto-Tune" / "EnableClientAutoTune").</summary>
        public static bool IsAutoTuneEnabled => AutoTune.AutoTuneConfig.EnableClientAutoTune != null && AutoTune.AutoTuneConfig.EnableClientAutoTune.Value;

        /// <summary>
        /// Version 9. True when a bulk transfer won't skew FGN's bandwidth probe: auto-tune is off, or this session's probe has finished.
        /// A mod that sends a large transfer at join (e.g. an asset sync) waits for this, or for <see cref="AutoTuneSettled"/>.
        /// </summary>
        public static bool IsAutoTuneSettled => !IsAutoTuneEnabled || AutoTune.AutoTuneProbe.ProbeCompletedThisSession;

        /// <summary>Version 9. Raised (main thread) when this session's bandwidth probe finishes. Not raised when auto-tune is off: check
        /// <see cref="IsAutoTuneSettled"/> first. A handler that throws is logged and skipped.</summary>
        public static event Action AutoTuneSettled
        {
            add => AutoTune.AutoTuneProbe.Completed += value;
            remove => AutoTune.AutoTuneProbe.Completed -= value;
        }

        /// <summary>For FGN's perf report: "mod: calls, ms" per filter since the last call.</summary>
        internal static string TakeFilterReport()
        {
            if (s_filters.Count == 0) return null;
            var parts = new List<string>();
            foreach (Filter f in s_filters) { if (f.Calls > 0) parts.Add($"{f.Mod} {f.Calls:N0} calls {f.Ms:F1} ms"); f.Calls = 0; f.Ms = 0; }
            return parts.Count > 0 ? "[FgnApi] routed-RPC filters: " + string.Join("; ", parts) : null;
        }
    }
}
