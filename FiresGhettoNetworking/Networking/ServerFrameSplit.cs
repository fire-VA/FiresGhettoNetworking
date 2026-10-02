using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.5.21 (BlueHills proof, 1.5.20): with Server-Side Simulation on, FGN's per-frame server pass (CreateDestroyObjects_Prefix)
    /// was ~16 ms of a stall frame outside creation, and nothing said which part. This splits it into collect (the ZDOs around every
    /// player), filter (dedupe + loaded-zone checks), create (the budgeted instantiation) and remove (vanilla RemoveObjects), and
    /// logs the average and worst of each once a minute while players are on. Timing only; it changes nothing.
    /// </summary>
    internal static class ServerFrameSplit
    {
        private const float ReportSeconds = 60f;
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        private static long s_collect, s_filter, s_create, s_remove;
        private static long s_maxCollect, s_maxFilter, s_maxCreate, s_maxRemove;
        private static long s_created, s_maxCreateOne;
        private static int s_frames;
        private static int s_nearMax, s_distantMax;
        private static float s_nextReport = -1f;

        public static void Frame(long collect, long filter, long create, long remove, int near, int distant)
        {
            s_frames++;
            s_collect += collect; s_filter += filter; s_create += create; s_remove += remove;
            if (collect > s_maxCollect) s_maxCollect = collect;
            if (filter > s_maxFilter) s_maxFilter = filter;
            if (create > s_maxCreate) s_maxCreate = create;
            if (remove > s_maxRemove) s_maxRemove = remove;
            if (near > s_nearMax) s_nearMax = near;
            if (distant > s_distantMax) s_distantMax = distant;

            float now = Time.realtimeSinceStartup;
            if (s_nextReport < 0f) { s_nextReport = now + ReportSeconds; return; }
            if (now < s_nextReport) return;
            s_nextReport = now + ReportSeconds;
            if (LoggerOptions.DebugEnabled) LoggerOptions.LogDebug(
                $"[SSS] server pass, last {ReportSeconds:F0} s over {s_frames} passes (average / worst ms per pass): collect {Avg(s_collect)} / {Ms(s_maxCollect)}, "
                + $"filter {Avg(s_filter)} / {Ms(s_maxFilter)}, create {Avg(s_create)} / {Ms(s_maxCreate)} ({s_created} objects, slowest one "
                + $"{Ms(s_maxCreateOne)}), remove {Avg(s_remove)} / {Ms(s_maxRemove)}; up to {s_nearMax} near and {s_distantMax} distant objects a pass; "
                + $"most created: {TopCreated()}.");
            s_createdByPrefab.Clear();
            s_collect = s_filter = s_create = s_remove = 0;
            s_maxCollect = s_maxFilter = s_maxCreate = s_maxRemove = 0;
            s_created = s_maxCreateOne = 0;
            s_frames = 0;
            s_nearMax = s_distantMax = 0;
        }

        // 1.5.23: creates per prefab, so a churn (proof 3: ~136k creates in 4 min against ~129k objects in range) is named.
        private const int TopPrefabs = 5;
        private static readonly Dictionary<int, int> s_createdByPrefab = new Dictionary<int, int>();

        public static void CreatedOne(int prefab, long ticks)
        {
            s_created++;
            if (ticks > s_maxCreateOne) s_maxCreateOne = ticks;
            s_createdByPrefab[prefab] = s_createdByPrefab.TryGetValue(prefab, out int n) ? n + 1 : 1;
        }

        private static string TopCreated()
        {
            if (s_createdByPrefab.Count == 0) return "none";
            return string.Join(", ", s_createdByPrefab.OrderByDescending(kv => kv.Value).Take(TopPrefabs).Select(kv =>
            {
                GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(kv.Key) : null;
                return $"{(prefab != null ? prefab.name : kv.Key.ToString())} {kv.Value}";
            }));
        }

        private static string Avg(long total) => s_frames > 0 ? (total * TicksToMs / s_frames).ToString("F2") : "0";
        private static string Ms(long ticks) => (ticks * TicksToMs).ToString("F1");
    }
}
