using System;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Dedicated-server frame breakdown for the [Links] report. A server frame over about 100 ms falls behind real time,
    /// and every client then sees its clock corrected, so each slow frame is split into networking (with FGN's sending
    /// inside it), zone generation, object creation and everything else, alongside the garbage collections that ran.
    /// </summary>
    [HarmonyPatch]
    internal static class ServerFrameProfile
    {
        private const float SlowFrameMs = 100f;
        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        internal struct SubsystemTicks
        {
            public long Networking;
            public long Sending;
            public long Zones;
            public long Objects;
            public int Collections;

            public SubsystemTicks Since(SubsystemTicks earlier) => new SubsystemTicks
            {
                Networking = Networking - earlier.Networking,
                Sending = Sending - earlier.Sending,
                Zones = Zones - earlier.Zones,
                Objects = Objects - earlier.Objects,
                Collections = Collections - earlier.Collections,
            };
        }

        private static long s_netTicksEver, s_sendTicksEver, s_zoneTicksEver, s_sceneTicksEver;

        private static int s_frame = -1;
        private static int s_gcAtFrameStart;
        private static long s_netStart, s_sendStart, s_zoneStart, s_sceneStart, s_scanStart;
        private static long s_netTicks, s_sendTicks, s_zoneTicks, s_sceneTicks, s_scanTicks;

        private static int s_frames;
        private static int s_slowFrames;
        private static int s_collections;
        private static int s_scans;
        private static long s_sendTicksTotal;
        private static long s_scanTicksTotal;
        private static double s_windowStart = -1.0;
        private static float s_longestMs;
        private static string s_longestBreakdown = "";

        [HarmonyPatch(typeof(ZNet), "Update"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        static void NetBegin() { NextFrame(); s_netStart = Stopwatch.GetTimestamp(); }

        [HarmonyPatch(typeof(ZNet), "Update"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        static void NetEnd()
        {
            long elapsed = Stopwatch.GetTimestamp() - s_netStart;
            s_netTicks += elapsed;
            s_netTicksEver += elapsed;
        }

        [HarmonyPatch(typeof(ZDOMan), "Update"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        static void SendBegin() { NextFrame(); s_sendStart = Stopwatch.GetTimestamp(); }

        [HarmonyPatch(typeof(ZDOMan), "Update"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        static void SendEnd()
        {
            long elapsed = Stopwatch.GetTimestamp() - s_sendStart;
            s_sendTicks += elapsed;
            s_sendTicksEver += elapsed;
        }

        [HarmonyPatch(typeof(ZDOMan), "CreateSyncList"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        static void ScanBegin() => s_scanStart = Stopwatch.GetTimestamp();

        [HarmonyPatch(typeof(ZDOMan), "CreateSyncList"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        static void ScanEnd()
        {
            s_scanTicks += Stopwatch.GetTimestamp() - s_scanStart;
            s_scans++;
        }

        [HarmonyPatch(typeof(ZoneSystem), "Update"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        static void ZoneBegin() { NextFrame(); s_zoneStart = Stopwatch.GetTimestamp(); }

        [HarmonyPatch(typeof(ZoneSystem), "Update"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        static void ZoneEnd()
        {
            long elapsed = Stopwatch.GetTimestamp() - s_zoneStart;
            s_zoneTicks += elapsed;
            s_zoneTicksEver += elapsed;
        }

        [HarmonyPatch(typeof(ZNetScene), "Update"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        static void SceneBegin() { NextFrame(); s_sceneStart = Stopwatch.GetTimestamp(); }

        [HarmonyPatch(typeof(ZNetScene), "Update"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        static void SceneEnd()
        {
            long elapsed = Stopwatch.GetTimestamp() - s_sceneStart;
            s_sceneTicks += elapsed;
            s_sceneTicksEver += elapsed;
        }

        /// <summary>Running totals since the server started, for the time spent in each subsystem between two points in time.</summary>
        internal static SubsystemTicks SubsystemTicksSoFar() => new SubsystemTicks
        {
            Networking = s_netTicksEver,
            Sending = s_sendTicksEver,
            Zones = s_zoneTicksEver,
            Objects = s_sceneTicksEver,
            Collections = GC.CollectionCount(0),
        };

        internal static string Report()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            double seconds = s_windowStart >= 0.0 ? Math.Max(0.001, now - s_windowStart) : 0.0;
            long sectors = SectorChangeTracker.SectorsScanned + SectorChangeTracker.SectorsSkipped;
            string report = $"server frames: longest {s_longestMs:F0} ms{s_longestBreakdown}, {s_slowFrames} over {SlowFrameMs:F0} ms, "
                + $"{s_collections} garbage collections; ZDO sending averaged "
                + $"{(s_frames > 0 ? s_sendTicksTotal * MsPerTick / s_frames : 0.0):F2} ms per frame over {s_frames} frames, of which "
                + $"rescanning the objects around players {(s_frames > 0 ? s_scanTicksTotal * MsPerTick / s_frames : 0.0):F2} ms "
                + $"({(seconds > 0.0 ? s_scans / seconds : 0.0):F1} rescans a second, "
                + $"{(sectors > 0 ? 100.0 * SectorChangeTracker.SectorsSkipped / sectors : 0.0):F0}% of sectors skipped as unchanged)";
            SectorChangeTracker.SectorsScanned = SectorChangeTracker.SectorsSkipped = 0;
            s_frames = s_slowFrames = s_collections = s_scans = 0;
            s_sendTicksTotal = s_scanTicksTotal = 0L;
            s_longestMs = 0f;
            s_longestBreakdown = "";
            s_windowStart = now;
            return report;
        }

        private static void NextFrame()
        {
            int frame = Time.frameCount;
            if (frame == s_frame) return;

            int collections = GC.CollectionCount(0);
            if (s_frame >= 0)
            {
                float frameMs = Time.unscaledDeltaTime * 1000f;
                int collectedLastFrame = collections - s_gcAtFrameStart;
                s_frames++;
                s_collections += collectedLastFrame;
                s_sendTicksTotal += s_sendTicks;
                s_scanTicksTotal += s_scanTicks;
                if (frameMs > SlowFrameMs) s_slowFrames++;
                if (frameMs > s_longestMs)
                {
                    double net = s_netTicks * MsPerTick;
                    double send = s_sendTicks * MsPerTick;
                    double scan = s_scanTicks * MsPerTick;
                    double zones = s_zoneTicks * MsPerTick;
                    double scene = s_sceneTicks * MsPerTick;
                    s_longestMs = frameMs;
                    s_longestBreakdown = $" (networking {net:F0} ms, of which ZDO sending {send:F0} ms and rescans {scan:F0} ms; "
                        + $"zone generation {zones:F0} ms; object creation {scene:F0} ms; "
                        + $"everything else {Math.Max(0.0, frameMs - net - zones - scene):F0} ms; "
                        + $"{collectedLastFrame} garbage collection{(collectedLastFrame == 1 ? "" : "s")})";
                }
            }
            if (s_windowStart < 0.0) s_windowStart = Time.realtimeSinceStartupAsDouble;
            s_frame = frame;
            s_gcAtFrameStart = collections;
            s_netTicks = s_sendTicks = s_zoneTicks = s_sceneTicks = s_scanTicks = 0L;
        }
    }
}
