using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using System.Reflection;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.41 ([fgn], Fire 11-05, group D; Tools\DESIGN_disconnect_sweep_20261105.md). Read on 1.0.21: when a player leaves, the server's
    /// ZDOMan.RemovePeer calls RemoveOrphanNonPersistentZDOS, which walks every object in the world (7.2 M on VA DN) to destroy the
    /// non-persistent ones nobody owns any more. Here a kept set holds every live object that is non-persistent, so the leave walks only
    /// those, with vanilla's own test and actions (log line, SetOwner, DestroyZDO).
    /// The set: filled once by a full scan after the world loads (ZDOMan.Load / LoadChunks), then kept by the Persistent setter (false adds,
    /// true removes), ZDOPool.Get (a reset ZDO is non-persistent; reset writes the flag field, not the setter) and ZDOPool.Release.
    /// Objects are kept by reference: ZDO's hash code is its id, which changes when a pooled ZDO is reused.
    /// Self-check (Debug log level, or the key below): each leave also runs vanilla's full walk and compares; any difference warns and turns
    /// the set off for the session. 1.6.44: ON by default (Fire 11-05; rig trial 2 of 2 leaves same as vanilla), self-check off by default.
    /// </summary>
    [HarmonyPatch]
    internal static class DisconnectSweep
    {
        internal static ConfigEntry<bool> ConfigEnabled, SelfCheck, WaitForJoinOnHosts;
        private static bool s_deferred;   // 1.6.82 ([fgn], lead audit 11-09): a host's Seed waits for the first AddPeer; a pure client never loads a world, so this stays false there

        private sealed class ByRef : IEqualityComparer<ZDO>
        {
            public bool Equals(ZDO a, ZDO b) => ReferenceEquals(a, b);
            public int GetHashCode(ZDO z) => RuntimeHelpers.GetHashCode(z);
        }

        private static readonly HashSet<ZDO> s_nonPersistent = new HashSet<ZDO>(new ByRef());
        private static readonly List<ZDO> s_walk = new List<ZDO>();
        private static bool s_seeded, s_broken;

        internal static void InitConfig(ConfigFile config)
        {
            // 1.6.44 (Fire 11-05, after 2 of 2 rig leaves matched vanilla): Fast Leave Cleanup on by default; the self-check off by default,
            // since with it on the full walk still runs every leave. Existing cfgs keep their value.
            ConfigEnabled = config.Bind("09 - Dedicated Server", "Fast Leave Cleanup", true,
                Cfg.Adv("When a player leaves, the server looks only through the objects that can be cleaned up instead of every object in the world, so a leave no longer stalls the server."));
            WaitForJoinOnHosts = config.Bind("09 - Dedicated Server", "Fast Leave Cleanup Waits For First Join On Hosts", true,
                Cfg.Adv("Single player and a host game only build the Fast Leave Cleanup list when the first player joins, so a world nobody joins never pays for it. Dedicated servers build it at load as before."));
            SelfCheck = config.Bind("09 - Dedicated Server", "Fast Leave Cleanup Self Check", false,
                Cfg.Adv("For checking only: with Fast Leave Cleanup on, each leave also runs the full vanilla check and compares (this takes the time back); any difference turns Fast Leave Cleanup off for the session and logs a warning."));
        }

        private static bool Tracking => s_seeded && !s_broken && ConfigEnabled.Value;

        // ---- keeping the set ----
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Persistent), MethodType.Setter), HarmonyPostfix]
        private static void PersistentSet(ZDO __instance, bool value)
        {
            if (!Tracking) return;
            if (value) s_nonPersistent.Remove(__instance); else s_nonPersistent.Add(__instance);
        }

        [HarmonyPatch(typeof(ZDOPool), "Get"), HarmonyPostfix]
        private static void Got(ZDO __result) { if (Tracking && __result != null && !__result.Persistent) s_nonPersistent.Add(__result); }

        [HarmonyPatch(typeof(ZDOPool), nameof(ZDOPool.Release), new[] { typeof(ZDO) }), HarmonyPrefix]
        private static void Released(ZDO zdo) { if (zdo != null) s_nonPersistent.Remove(zdo); }

        private static void ReleasedAll() { s_nonPersistent.Clear(); s_seeded = false; s_deferred = false; }

        // 1.6.51 (Hexium's store check, 11-07: "No overload of Harmony target ZDOPool.Release matches (Dictionary`2<ZDOID,ZDO>)" on Steam
        // dedicated build 26266951): that build DOES have Release(Dictionary<ZDOID, ZDO>) (read from its assembly_valheim, 648F3F8ED7B4, and FGN
        // 1.6.50 runs on it with this patch attached), so the check misread the attribute's generic type. The target is now found in code, and if
        // a game build ever lacks it, Fast Leave Cleanup turns itself off for the session (vanilla's leave walk) instead of keeping a set that
        // would never be cleared at world unload. Every other patch of this class is unchanged.
        [HarmonyPatch]
        internal static class ReleaseAllHook
        {
            private static MethodBase s_target;
            private static MethodBase Find() => s_target ?? (s_target = AccessTools.Method(typeof(ZDOPool), "Release", new[] { typeof(Dictionary<ZDOID, ZDO>) }));
            private static bool Prepare()
            {
                if (Find() != null) return true;
                s_broken = true;
                LoggerOptions.LogInfo("[DisconnectSweep] Fast Leave Cleanup is off on this game build: ZDOPool.Release(Dictionary<ZDOID, ZDO>) is not there (the server uses vanilla's leave cleanup).");
                return false;
            }
            private static MethodBase TargetMethod() => Find();
            private static void Prefix() => ReleasedAll();
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.Load)), HarmonyPostfix]
        private static void Loaded(ZDOMan __instance) => Seed(__instance);

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.LoadChunks)), HarmonyPostfix]
        private static void LoadedChunks(ZDOMan __instance) => Seed(__instance);

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.AddPeer)), HarmonyPostfix]
        private static void PeerAdded(ZDOMan __instance) { if (s_deferred) Seed(__instance, true); }

        private static void Seed(ZDOMan man, bool atJoin = false)
        {
            if (!ConfigEnabled.Value || s_broken) return;
            if (!atJoin && WaitForJoinOnHosts.Value && !ServerClientUtils.ZNetIsDedicated())
            {
                s_deferred = true;
                Plugin_Info("[DisconnectSweep] host or single player: the leave cleanup list is built when the first player joins");
                return;
            }
            s_deferred = false;
            var sw = Stopwatch.StartNew();
            s_nonPersistent.Clear();
            foreach (ZDO z in man.m_objectsByID.Values) if (!z.Persistent) s_nonPersistent.Add(z);
            s_seeded = true;
            Plugin_Info($"[DisconnectSweep] {s_nonPersistent.Count} non-persistent object(s) of {man.m_objectsByID.Count} kept for leave cleanup ({sw.ElapsedMilliseconds} ms, once at load)");
        }

        // ---- the sweep ----
        [HarmonyPatch(typeof(ZDOMan), "RemoveOrphanNonPersistentZDOS"), HarmonyPrefix]
        private static bool Sweep(ZDOMan __instance)
        {
            if (!Tracking) return true;
            var sw = Stopwatch.StartNew();
            HashSet<ZDOID> mine = SelfCheck.Value ? new HashSet<ZDOID>() : null;
            s_walk.Clear();
            foreach (ZDO z in s_nonPersistent) s_walk.Add(z);   // DestroyZDO only queues, but a copy keeps the walk safe from any change
            int destroyed = 0;
            foreach (ZDO z in s_walk)
            {
                if (z.Persistent) { s_nonPersistent.Remove(z); continue; }
                if (!__instance.m_objectsByID.TryGetValue(z.m_uid, out ZDO live) || !ReferenceEquals(live, z)) { s_nonPersistent.Remove(z); continue; }
                if (!z.HasOwner() || !__instance.IsPeerConnected(z.GetOwner()))
                {
                    ZDOID uid = z.m_uid;
                    ZLog.Log("Destroying abandoned non persistent zdo " + uid.ToString() + " owner " + z.GetOwner());
                    mine?.Add(uid);
                    z.SetOwner(__instance.m_sessionID);
                    __instance.DestroyZDO(z);
                    destroyed++;
                }
            }
            s_walk.Clear();
            long fastMs = sw.ElapsedMilliseconds;
            if (mine != null) Check(__instance, mine, fastMs);
            LoggerOptions.LogDebug($"[DisconnectSweep] leave cleanup: {destroyed} destroyed, {s_nonPersistent.Count} kept checked, {fastMs} ms");
            return false;
        }

        // Vanilla's own test over every object, after the fast pass: anything it would still destroy was missed by the set.
        private static void Check(ZDOMan man, HashSet<ZDOID> mine, long fastMs)
        {
            var sw = Stopwatch.StartNew();
            int missed = 0;
            ZDOID first = ZDOID.None;
            foreach (ZDO z in man.m_objectsByID.Values)
            {
                if (z.Persistent || mine.Contains(z.m_uid)) continue;
                if (!z.HasOwner() || !man.IsPeerConnected(z.GetOwner()))
                {
                    if (missed++ == 0) first = z.m_uid;
                    // Do what vanilla would have done, so a miss changes nothing in the world, only turns the fast path off.
                    ZLog.Log("Destroying abandoned non persistent zdo " + z.m_uid.ToString() + " owner " + z.GetOwner());
                    z.SetOwner(man.m_sessionID);
                    man.DestroyZDO(z);
                }
            }
            if (missed > 0)
            {
                s_broken = true;
                LoggerOptions.LogWarning($"[DisconnectSweep] self-check: {missed} object(s) the fast cleanup missed (first {first}); Fast Leave Cleanup is off for this session and vanilla cleans up from the next leave.");
            }
            else LoggerOptions.LogDebug($"[DisconnectSweep] self-check: same as vanilla ({fastMs} ms fast vs {sw.ElapsedMilliseconds} ms full walk)");
        }

        private static void Plugin_Info(string s) => FiresGhettoNetworkMod.Log?.LogInfo(s);
    }
}
