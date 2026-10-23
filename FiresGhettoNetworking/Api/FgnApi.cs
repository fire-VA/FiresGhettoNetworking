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
        public const int Version = 3;

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
            LoggerOptions.LogMessage($"[FgnApi] routed-RPC filter registered by {modGuid} (priority {priority})");
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
            LoggerOptions.LogMessage($"[FgnApi] {(string.IsNullOrEmpty(modGuid) ? "an unnamed mod" : modGuid)} left {added} prefab(s) out of server simulation: {string.Join(", ", prefabNames)}");
        }

        /// <summary>True when FGN leaves this prefab to the clients (built-in or registered with <see cref="ExcludeFromServerSimulation"/>).</summary>
        public static bool IsExcludedFromServerSimulation(string prefabName)
            => !string.IsNullOrEmpty(prefabName) && global::FiresGhettoNetworkMod.ServerOwnershipPatchesV3.IsExcluded(prefabName.Trim().GetStableHashCode());

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
