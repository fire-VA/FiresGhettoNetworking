using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.25: FGN owns "[Performance] Kinematic sync sweep". 1.6.63: exposed as FgnApi.KinematicSweepEnabled.
    /// 1.6.64 ([lead] audit M10, Fire 11-08: networking perf lives in FGN, no mod relies on another): FGN now also RUNS it (ported from our
    /// former copy, same behaviour). Vanilla ZSyncTransform caches m_isKinematicBody once at init, but bodies turn kinematic at runtime
    /// (sleeping creatures, a companion's freeze / teleport); the stale flag makes non-owners write velocities to kinematic bodies every
    /// tick, which Unity rejects with a warning. Two ways, chosen once at startup from the key (restart to switch):
    ///   OFF: a ClientSync prefix refreshes the flag from the live body, each object every 8th frame (staggered by instance id).
    ///   ON : no per-tick prefix; a sweep every 2 s over ZNetScene's instances fixes any flag that differs from its body.
    /// A mod that toggles a body itself can set the flag in the same call; the sweep catches anything else within 2 s.
    /// </summary>
    internal static class KinematicSweepOwner
    {
        private const string Section = "Performance", Key = "Kinematic sync sweep";
        internal static ConfigEntry<bool> Enabled;
        private static bool s_applied;

        internal static void InitConfig(ConfigFile config)
        {
            Enabled = config.Bind(new ConfigDefinition(Section, Key), true, new ConfigDescription(
                "Restart to apply. On: keeps network-synced bodies' kinematic flag right with a sweep every 2 s, instead of a check on every " +
                "sync tick (~5 ms/s less). A body another mod freezes may log Unity's kinematic-velocity warning for up to 2 s. " +
                "Off: the per-tick check (each object every 8th frame)."));
        }

        /// <summary>Called once from Awake after the config is bound: attaches the prefix, or starts the sweeper.</summary>
        internal static void Apply(Harmony harmony)
        {
            if (s_applied) return;
            s_applied = true;
            try
            {
                if (Enabled.Value)
                {
                    var go = new GameObject("FGN KinematicSync");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    go.hideFlags = HideFlags.HideAndDontSave;
                    go.AddComponent<Sweeper>();
                    // 1.6.75: the sweep walks only live ZSyncTransforms (kept by their own OnEnable / OnDisable), a slice a frame
                    harmony.Patch(AccessTools.Method(typeof(ZSyncTransform), "OnEnable"), postfix: new HarmonyMethod(typeof(KinematicSweepOwner), nameof(OnSyncEnable)));
                    harmony.Patch(AccessTools.Method(typeof(ZSyncTransform), "OnDisable"), postfix: new HarmonyMethod(typeof(KinematicSweepOwner), nameof(OnSyncDisable)));
                    LoggerOptions.LogInfo($"[KinematicSync] '{Key}' on: no ClientSync prefix; kinematic flags swept every {Sweeper.Interval:0} s");
                }
                else
                {
                    harmony.Patch(AccessTools.Method(typeof(ZSyncTransform), "ClientSync"), prefix: new HarmonyMethod(typeof(KinematicSweepOwner), nameof(RefreshPrefix)));
                    LoggerOptions.LogInfo($"[KinematicSync] '{Key}' off: ClientSync refreshes the kinematic flag, each object every {Stride}th frame");
                }
            }
            catch (Exception e) { LoggerOptions.LogWarning($"[KinematicSync] could not attach ({e.GetType().Name}: {e.Message}); vanilla's cached flag stays"); }
        }

        private static readonly AccessTools.FieldRef<ZSyncTransform, Rigidbody> s_body = AccessTools.FieldRefAccess<ZSyncTransform, Rigidbody>("m_body");
        private static readonly AccessTools.FieldRef<ZSyncTransform, bool> s_flag = AccessTools.FieldRefAccess<ZSyncTransform, bool>("m_isKinematicBody");

        // ---- OFF: staggered ClientSync prefix ----
        private const int Stride = 8;
        private static int s_refreshed, s_calls;
        private static float s_nextReport;

        private static void RefreshPrefix(ZSyncTransform __instance)
        {
            s_calls++;
            if (((Time.frameCount + __instance.GetInstanceID()) & (Stride - 1)) != 0) return;
            try { var body = s_body(__instance); if (body != null) { s_flag(__instance) = body.isKinematic; s_refreshed++; } } catch { }
            if (Time.unscaledTime < s_nextReport) return;
            if (s_nextReport > 0f && LoggerOptions.DebugEnabled)
                LoggerOptions.LogDebug($"[KinematicSync] refreshed {s_refreshed} of {s_calls} ClientSync calls in the last minute (every {Stride}th frame per object)");
            s_refreshed = 0; s_calls = 0; s_nextReport = Time.unscaledTime + 60f;
        }

        // ---- ON: 2 s sweep over a registry (1.6.75) ----
        private static readonly List<ZSyncTransform> s_syncs = new List<ZSyncTransform>(1024);
        private static readonly Dictionary<ZSyncTransform, int> s_index = new Dictionary<ZSyncTransform, int>(1024);
        private static void OnSyncEnable(ZSyncTransform __instance)
        {
            if (__instance == null || s_index.ContainsKey(__instance)) return;
            s_index[__instance] = s_syncs.Count; s_syncs.Add(__instance);
        }
        private static void OnSyncDisable(ZSyncTransform __instance)
        {
            if (__instance == null || !s_index.TryGetValue(__instance, out int i)) return;
            int last = s_syncs.Count - 1;
            ZSyncTransform moved = s_syncs[last];
            s_syncs[i] = moved; s_index[moved] = i;   // swap-back removal
            s_syncs.RemoveAt(last); s_index.Remove(__instance);
        }
        private sealed class Sweeper : MonoBehaviour
        {
            internal const float Interval = 2f;
            private float _next, _nextReport;
            private int _bodies, _fixed, _sweeps;
            private readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();

            // 1.6.75 ([lead] SelfCost 11-08: 0.734 ms/frame): was a walk of EVERY ZNetScene instance with a GetComponent each, every 2 s
            // (~134k in a town, one big frame). Now: only live ZSyncTransforms (registered by their own OnEnable / OnDisable), a slice per
            // frame so each one is still checked about every Interval seconds. Same fix, same 2 s bound, no spike.
            private int _cursor;
            private void Update()
            {
                int n = s_syncs.Count;
                if (n == 0) return;
                int slice = Mathf.Clamp(Mathf.CeilToInt(n * Time.unscaledDeltaTime / Interval), 1, n);
                _sw.Start();
                try
                {
                    for (int i = 0; i < slice; i++)
                    {
                        if (_cursor >= s_syncs.Count) { _cursor = 0; _sweeps++; }
                        if (s_syncs.Count == 0) break;
                        ZSyncTransform sync = s_syncs[_cursor++];
                        if (sync == null) continue;
                        var body = s_body(sync);
                        if (body == null) continue;
                        _bodies++;
                        if (s_flag(sync) != body.isKinematic) { s_flag(sync) = body.isKinematic; _fixed++; }
                    }
                }
                catch (Exception ex) { LoggerOptions.LogDebug($"[KinematicSync] sweep skipped: {ex.GetType().Name}: {ex.Message}"); }
                _sw.Stop();
                if (Time.unscaledTime < _nextReport) return;
                if (_nextReport > 0f && LoggerOptions.DebugEnabled)
                    LoggerOptions.LogDebug($"[KinematicSync] sweep: {_sweeps} sweep(s), {_bodies} bodies checked, {_fixed} flag(s) fixed, {_sw.Elapsed.TotalMilliseconds:0.0} ms in the last minute ({_sw.Elapsed.TotalMilliseconds / 60.0:0.000} ms/s)");
                _bodies = 0; _fixed = 0; _sweeps = 0; _sw.Reset();
                _nextReport = Time.unscaledTime + 60f;
            }
        }
    }
}
