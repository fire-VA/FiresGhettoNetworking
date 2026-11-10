using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.73 ([lead] 11-08: "split the +1.4 ms update.scripts by mod and cut it"). "Self Cost Census" (key, OFF: it is a measuring aid
    /// and costs two timestamps per call): once the world is loaded, every Harmony prefix / postfix / finalizer THIS mod owns, and every
    /// Update / LateUpdate / FixedUpdate of a MonoBehaviour in this assembly, is timed (a prefix + finalizer on that method itself, so the
    /// game's patched method is not touched again). Every 30 s one Debug line lists the top 10 by ms per frame, with calls per frame,
    /// plus this mod's total ms per frame. Transpilers are not timed (they run once). Turning the key off stops the timing at once.
    /// </summary>
    internal static class SelfCost
    {
        internal static ConfigEntry<bool> ConfigEnabled;
        private const float ReportSeconds = 30f;

        private sealed class Row { public string Name; public long Ticks, Calls, Bytes; }
        // allocation census (lead 11-08): bytes allocated on this thread inside each hook (GC.GetAllocatedBytesForCurrentThread, bound once; n/a if this runtime lacks it)
        internal struct St { public long T, B; }
        internal static string AllocCounter = "n/a";
        private static readonly Func<long> s_alloc = BindAlloc();
        // [lead] 11-08: "0.0 KB/s for both mods: real or n/a?" Self-tested at load with a known 64 KB allocation: the per-thread counter is used
        // only if it sees it; otherwise Unity's Profiler.GetMonoUsedSizeLong (the Mono heap in use, grows with every allocation until a GC; a
        // negative delta = a GC ran inside the hook and is ignored). Which counter is in use is named in the report; none = "n/a".
        // AllocCounter is declared above s_alloc: field initialisers run in text order, and BindAlloc sets it
        private static Func<long> BindAlloc()
        {
            try
            {
                var m = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Public | BindingFlags.Static);
                if (m != null)
                {
                    var f = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), m);
                    long a = f(); var probe = new byte[65536]; long b = f(); GC.KeepAlive(probe);
                    if (b - a >= 65536) { AllocCounter = "GC.GetAllocatedBytesForCurrentThread (self-test passed)"; return f; }
                }
            }
            catch { }
            try
            {
                var u = typeof(UnityEngine.Profiling.Profiler).GetMethod("GetMonoUsedSizeLong", BindingFlags.Public | BindingFlags.Static);
                if (u != null)
                {
                    var f = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), u);
                    long a = f(); var probe = new byte[1 << 20]; long b = f(); GC.KeepAlive(probe);
                    if (b - a > 0) { AllocCounter = "Profiler.GetMonoUsedSizeLong (per-thread counter unsupported here; self-test passed)"; return f; }
                }
            }
            catch { }
            AllocCounter = "n/a (no allocation counter on this runtime passed the self-test)";
            return null;
        }
        private static readonly Dictionary<MethodBase, Row> s_rows = new Dictionary<MethodBase, Row>();
        private static readonly List<Row> s_sort = new List<Row>();
        private static Harmony s_timer;
        private static bool s_installed;
        private static int s_frames, s_lastFrame;
        private static float s_next;

        internal static void InitConfig(ConfigFile config)
        {
            ConfigAfterArrival = config.Bind("12 - Advanced", "Self Cost Census Starts After Arrival", true,
                "With the census on, its timers start once you are in the world and the area has loaded, so the report is about play and world loading does not pay for them. Off: they start before the world loads.");
            ConfigEnabled = config.Bind("12 - Advanced", "Self Cost Census", false,
                "Measuring aid: times every hook and update loop of this mod and logs the 10 most expensive (Debug, every 30 s). Costs a little itself; leave off in play.");
        }

        /// <summary>From FGN's ZNetScene.Awake (the world is loaded; every patch is in): install the timers once if the key is on.</summary>
        internal static void TryInstall(Harmony mine)
        {
            if (s_installed || ConfigEnabled == null || !ConfigEnabled.Value || mine == null) return;
            s_installed = true;
            s_timer = new Harmony(mine.Id + ".selfcost");
            var pre = new HarmonyMethod(AccessTools.Method(typeof(SelfCost), nameof(Pre)));
            var fin = new HarmonyMethod(AccessTools.Method(typeof(SelfCost), nameof(Fin)));
            var targets = new HashSet<MethodBase>();
            foreach (MethodBase original in mine.GetPatchedMethods())
            {
                Patches info = Harmony.GetPatchInfo(original);
                if (info == null) continue;
                foreach (var list in new[] { info.Prefixes, info.Postfixes, info.Finalizers })
                    foreach (Patch p in list)
                        if (p.owner == mine.Id && p.PatchMethod != null) targets.Add(p.PatchMethod);
            }
            foreach (Type t in AccessTools.GetTypesFromAssembly(typeof(SelfCost).Assembly))
            {
                if (t == null || !typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                foreach (string n in new[] { "Update", "LateUpdate", "FixedUpdate" })
                {
                    MethodInfo m = t.GetMethod(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    if (m != null) targets.Add(m);
                }
            }
            int ok = 0, failed = 0;
            foreach (MethodBase m in targets)
            {
                if (m.DeclaringType == typeof(SelfCost) || m.DeclaringType == typeof(ArrivalWait)) continue;
                try { s_rows[m] = new Row { Name = m.DeclaringType.Name + "." + m.Name }; s_timer.Patch(m, prefix: pre, finalizer: fin); ok++; }
                catch { s_rows.Remove(m); failed++; }
            }
            LoggerOptions.LogInfo($"[SelfCost] timing {ok} hook(s) / update loop(s) of this mod ({failed} could not be timed); top 10 at Debug every {ReportSeconds:0} s");
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        internal static class InstallOnWorldLoad
        {
            [HarmonyPostfix, HarmonyPriority(Priority.Last)]
            private static void Postfix() { try { StartInstall(FiresGhettoNetworkMod.Harmony); } catch (Exception e) { LoggerOptions.LogDebug("[SelfCost] not installed: " + e.Message); } }
        }

        // 1.6.82 ([fgn], lead audit 11-09): installed at ZNetScene.Awake the timers wrapped every world-load hook (31k calls a frame), so the first report was only about loading
        internal static ConfigEntry<bool> ConfigAfterArrival;
        private static ArrivalWait s_wait;

        private static void StartInstall(Harmony mine)
        {
            if (s_installed || ConfigEnabled == null || !ConfigEnabled.Value) return;
            if (ConfigAfterArrival == null || !ConfigAfterArrival.Value || ServerClientUtils.ZNetIsDedicated()) { TryInstall(mine); return; }   // a dedicated server has no player to wait for
            if (s_wait != null) return;
            s_wait = new GameObject("FGN SelfCost wait").AddComponent<ArrivalWait>();
            s_wait.Mine = mine;
            LoggerOptions.LogInfo("[SelfCost] timers start once the player has arrived and the area has loaded");
        }

        private sealed class ArrivalWait : MonoBehaviour
        {
            internal Harmony Mine;
            private void Update()
            {
                Player me = Player.m_localPlayer;
                if (me == null || me.IsTeleporting() || ZNet.instance == null || ZoneSystem.instance == null || !ZoneSystem.instance.IsActiveAreaLoaded()) return;
                Destroy(gameObject);
                try { TryInstall(Mine); } catch (Exception e) { LoggerOptions.LogDebug("[SelfCost] not installed: " + e.Message); }
            }
        }

        private static void Pre(out St __state) { __state = default; if (!ConfigEnabled.Value) return; __state.B = s_alloc != null ? s_alloc() : 0L; __state.T = Stopwatch.GetTimestamp(); }

        private static Exception Fin(MethodBase __originalMethod, St __state, Exception __exception)
        {
            if (__state.T == 0L) return __exception;
            long dt = Stopwatch.GetTimestamp() - __state.T; long db = s_alloc != null && __state.B != 0L ? s_alloc() - __state.B : 0L;
            if (__originalMethod != null && s_rows.TryGetValue(__originalMethod, out Row r)) { r.Ticks += dt; r.Calls++; if (db > 0) r.Bytes += db; }
            int f = Time.frameCount;
            if (f != s_lastFrame) { s_lastFrame = f; s_frames++; Report(); }
            return __exception;
        }

        private static void Report()
        {
            float now = Time.realtimeSinceStartup;
            if (now < s_next) return;
            if (s_next > 0f && s_frames > 0 && LoggerOptions.DebugEnabled)
            {
                s_sort.Clear(); s_sort.AddRange(s_rows.Values);
                s_sort.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));
                double toMs = 1000.0 / Stopwatch.Frequency;
                long total = 0; foreach (Row r in s_sort) total += r.Ticks;
                var sb = new StringBuilder();
                sb.Append($"[SelfCost] last {ReportSeconds:0} s: this mod {total * toMs / s_frames:0.000} ms per frame over {s_frames} frame(s); top: ");
                for (int i = 0; i < s_sort.Count && i < 10; i++)
                {
                    Row r = s_sort[i];
                    if (r.Calls == 0) break;
                    if (i > 0) sb.Append(", ");
                    sb.Append(r.Name).Append(' ').Append((r.Ticks * toMs / s_frames).ToString("0.000")).Append(" ms (").Append(((double)r.Calls / s_frames).ToString("0.#")).Append("/frame)");
                }
                LoggerOptions.LogDebug(sb.ToString());
                if (s_alloc == null) LoggerOptions.LogDebug("[SelfCost] allocations: n/a (this runtime has no per-thread allocation counter)");
                else
                {
                    var al = new List<Row>(s_rows.Values); al.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
                    long totalB = 0; foreach (Row r in al) totalB += r.Bytes;
                    var ab = new StringBuilder(); ab.Append($"[SelfCost] allocations, last {ReportSeconds:0} s: this mod {totalB / 1024.0 / ReportSeconds:0.0} KB/s ({AllocCounter}); top: ");
                    for (int i = 0; i < al.Count && i < 10; i++) { if (al[i].Bytes <= 0) break; if (i > 0) ab.Append(", "); ab.Append(al[i].Name).Append(' ').Append((al[i].Bytes / 1024.0 / ReportSeconds).ToString("0.0")).Append(" KB/s (").Append(al[i].Calls > 0 ? (al[i].Bytes / al[i].Calls).ToString() : "0").Append(" B/call)"); }
                    LoggerOptions.LogDebug(ab.ToString());
                }
            }
            foreach (Row r in s_rows.Values) { r.Ticks = 0; r.Calls = 0; r.Bytes = 0; }
            s_frames = 0;
            s_next = now + ReportSeconds;
        }
    }
}
