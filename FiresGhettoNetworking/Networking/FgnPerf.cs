using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// What FGN's own hook code costs on the simulation and ownership paths. Each point times
    /// only FGN's handler inside its existing hook (no new hooks), and one "[FGN perf]" line a minute gives calls, average
    /// and worst microseconds and the total. Timing runs only at Log Level Debug; otherwise each point is one bool check.
    /// </summary>
    internal static class FgnPerf
    {
        internal enum Point
        {
            ReleaseNearby,      // ZDOMan.ReleaseNearbyZDOS prefix: the selective (V3) / broad ownership pass
            SyncList,           // ZDOMan.CreateSyncList: vanilla's scan with FGN's sector skip inside it
            CharacterFixed,     // Character.CustomFixedUpdate prefix: AI LOD
            SyncTransformFixed, // ZSyncTransform.CustomFixedUpdate prefix: Remote Motion gate + ownership-edge snap
            ShipFixed,          // Ship.CustomFixedUpdate prefix + postfix: helm take-over, ship-sim park, ship fixes
            CreatureDamage,     // Character.RPC_Damage prefix: hit forwarding after a creature hand-off, lag-fair dodge
            CreatureBalance,    // ZDOMan.Update postfix: CreatureOwnership.Balance, the owner pick
            ZdoDeserialize,     // ZDO.Deserialize postfix: player sync + sector journal, per received ZDO
            Count
        }

        private static readonly string[] Names =
        {
            "release-nearby", "sync-list", "character-fixed", "synctransform-fixed", "ship-fixed", "creature-damage",
            "creature-balance", "zdo-deserialize"
        };

        private const float ReportSeconds = 60f;
        private static readonly double MicrosPerTick = 1000000.0 / Stopwatch.Frequency;

        private static readonly long[] s_calls = new long[(int)Point.Count];
        private static readonly long[] s_ticks = new long[(int)Point.Count];
        private static readonly long[] s_worst = new long[(int)Point.Count];
        private static float s_nextReport = -1f;

        /// <summary>Refreshed once a frame from FgnHooks' ZNet.Update postfix.</summary>
        internal static bool On;

        internal static long Begin() => On ? Stopwatch.GetTimestamp() : 0L;

        internal static void End(Point point, long start)
        {
            if (start == 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - start;
            int i = (int)point;
            s_calls[i]++;
            s_ticks[i] += elapsed;
            if (elapsed > s_worst[i]) s_worst[i] = elapsed;
        }

        internal static void Tick()
        {
            bool was = On;
            On = LoggerOptions.DebugEnabled;
            if (!On)
            {
                if (was) Clear();
                s_nextReport = -1f;
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (s_nextReport < 0f) { s_nextReport = now + ReportSeconds; Clear(); return; }
            if (now < s_nextReport) return;
            s_nextReport = now + ReportSeconds;

            var line = new StringBuilder($"[FGN perf] last {ReportSeconds:F0} s, FGN's own hook code (calls, average / worst us, total ms):");
            bool any = false;
            for (int i = 0; i < (int)Point.Count; i++)
            {
                if (s_calls[i] == 0) continue;
                line.Append(any ? "; " : " ").Append(Names[i]).Append(' ')
                    .Append(s_calls[i]).Append(", ")
                    .Append((s_ticks[i] * MicrosPerTick / s_calls[i]).ToString("F1")).Append(" / ")
                    .Append((s_worst[i] * MicrosPerTick).ToString("F0")).Append(" us, ")
                    .Append((s_ticks[i] * MicrosPerTick / 1000.0).ToString("F1")).Append(" ms");
                any = true;
            }
            if (!any) line.Append(" no timed hook ran.");
            LoggerOptions.LogDebug(line.ToString());
            Clear();
        }

        private static void Clear()
        {
            for (int i = 0; i < (int)Point.Count; i++) s_calls[i] = s_ticks[i] = s_worst[i] = 0L;
        }
    }
}
