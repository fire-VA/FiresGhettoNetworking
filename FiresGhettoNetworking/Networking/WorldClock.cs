using System;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Keeps the world clock (ZNet.m_netTime) continuous for everyone. A dedicated server adds back the game time Unity drops
    /// on frames longer than its maximum timestep, and so does a client; a client eases each server correction in and never steps
    /// its clock back.
    /// </summary>
    [HarmonyPatch]
    public static class WorldClock
    {
        private const float MinLostSecondsToAddBack = 0.001f;
        private const float MaxLostSecondsPerFrame = 300f;
        private const double ApplyAtOnceAheadSeconds = 5.0;
        private const double ApplyAtOnceBehindSeconds = 300.0;
        private const double SlewTimeSeconds = 2.0;
        private const double MaxFasterRate = 1.0;
        private const double MaxSlowerRate = 0.5;
        private const double ReportThresholdSeconds = 0.05;
        private const double NotableCorrectionSeconds = 0.5;
        private const float ReportIntervalSec = 300f;
        private const double MsPerSecond = 1000.0;

        private static ZNet s_syncedTo;
        private static double s_pendingSeconds;
        private static int s_correctionFrame = -1;
        private static int s_snappedFrame = -1;

        private static int s_reportCount;
        private static double s_reportLargestBehindSeconds;
        private static double s_reportLargestAheadSeconds;
        private static float s_reportLongestFrameSeconds;
        private static DateTime s_reportLongestFrameEnded;
        private static double s_reportClientAddedBackSeconds;
        private static float s_nextReportTime;

        internal static double ServerSecondsAddedBack;

        private static bool ServerCatchUpEnabled => FiresGhettoNetworkMod.ConfigKeepWorldClockAtRealTime?.Value ?? false;
        private static bool ClientSlewEnabled => FiresGhettoNetworkMod.ConfigSmoothServerClockCorrections?.Value ?? false;

        /// <summary>Called once per dedicated-server frame. Unity advances game time by at most Time.maximumDeltaTime a frame;
        /// whatever a longer frame loses is added to the world clock, the same way vanilla advances it (players online only).</summary>
        internal static void AddBackTimeLostToSlowServerFrame(ZNet znet)
        {
            if (!ServerCatchUpEnabled || znet == null || !znet.IsServer() || znet.GetNrOfPlayers() <= 0) return;
            if (!Mathf.Approximately(Time.timeScale, 1f)) return;
            float lost = Time.unscaledDeltaTime - Time.deltaTime;
            if (lost < MinLostSecondsToAddBack || lost > MaxLostSecondsPerFrame) return;
            znet.m_netTime += lost;
            ServerSecondsAddedBack += lost;
        }

        /// <summary>
        /// A client's clock loses the same time on its own long frames (a load or a hitch), and the server's next re-sync then
        /// pulls it forward by seconds (R43: 2.7 s after 5.5 s and 1.7 s frames, with a 30 fps dedi). Adding the loss back here
        /// keeps the client with the server, so the correction it eases in stays small. Only once synced to a server.
        /// </summary>
        private static void AddBackTimeLostToSlowClientFrame(ZNet znet)
        {
            if (!ClientSlewEnabled || znet == null || znet.IsServer() || !ReferenceEquals(s_syncedTo, znet)) return;
            if (!Mathf.Approximately(Time.timeScale, 1f)) return;
            // This runs after ZNet.Update, which already handled any server time that queued up during the long frame. That
            // correction measured the loss too, so adding it back as well counted it twice (R52: 10.3 s added back, then the
            // client 11.4 s AHEAD of the server, eased out at half speed): a correction taken as-is this frame leaves nothing to
            // add, and one being eased in shrinks by what is added here instead.
            if (s_snappedFrame == Time.frameCount) return;
            float lost = Time.unscaledDeltaTime - Time.deltaTime;
            if (lost < MinLostSecondsToAddBack || lost > MaxLostSecondsPerFrame) return;
            znet.m_netTime += lost;
            if (s_correctionFrame == Time.frameCount) s_pendingSeconds -= lost;
            s_reportClientAddedBackSeconds += lost;
        }

        [HarmonyPatch(typeof(ZNet), "RPC_NetTime"), HarmonyPrefix]
        static bool EaseInServerTime(ZNet __instance, double time)
        {
            double correction = time - __instance.m_netTime;
            if (!ClientSlewEnabled || __instance.IsServer())
            {
                s_pendingSeconds = 0.0;
                return true;
            }
            if (s_syncedTo != __instance)
            {
                s_syncedTo = __instance;
                s_pendingSeconds = 0.0;
                s_snappedFrame = Time.frameCount;
                return true;
            }
            if (correction >= ApplyAtOnceAheadSeconds || correction <= -ApplyAtOnceBehindSeconds)
            {
                s_pendingSeconds = 0.0;
                s_snappedFrame = Time.frameCount;
                return true;
            }
            s_pendingSeconds = correction;
            s_correctionFrame = Time.frameCount;
            if (Math.Abs(correction) >= ReportThresholdSeconds)
                CapeCrashDiagnostics.Log($"Server clock correction {correction * MsPerSecond:0} ms, easing in");
            Report(correction);
            return false;
        }

        [HarmonyPatch(typeof(ZNet), "UpdateNetTime"), HarmonyPostfix]
        static void PayBackPendingCorrection(ZNet __instance, float dt)
        {
            if (s_pendingSeconds == 0.0 || __instance.IsServer()) return;
            double step = s_pendingSeconds * Math.Min(1.0, dt / SlewTimeSeconds);
            step = Math.Max(-MaxSlowerRate * dt, Math.Min(MaxFasterRate * dt, step));
            __instance.m_netTime += step;
            s_pendingSeconds -= step;
        }

        /// <summary>
        /// Once per rendered frame, from ZNet.Update. Not from UpdateNetTime: that runs in FixedUpdate, where unscaledDeltaTime is
        /// the gap since the previous fixed step, and with game time stopped (a paused menu) that gap spans the whole pause.
        /// </summary>
        internal static void MeasureFrame()
        {
            AddBackTimeLostToSlowClientFrame(ZNet.instance);
            float frameSeconds = Time.unscaledDeltaTime;
            if (frameSeconds <= s_reportLongestFrameSeconds) return;
            s_reportLongestFrameSeconds = frameSeconds;
            s_reportLongestFrameEnded = DateTime.Now;
        }

        private static void Report(double correctionSeconds)
        {
            if (Math.Abs(correctionSeconds) < ReportThresholdSeconds) return;
            s_reportCount++;
            if (correctionSeconds > s_reportLargestBehindSeconds) s_reportLargestBehindSeconds = correctionSeconds;
            if (-correctionSeconds > s_reportLargestAheadSeconds) s_reportLargestAheadSeconds = -correctionSeconds;
            if (Time.realtimeSinceStartup < s_nextReportTime) return;

            string line = $"[WorldClock] Eased in {s_reportCount} server clock correction(s) of {ReportThresholdSeconds * MsPerSecond:F0} ms "
                + $"or more since the last report: the largest {s_reportLargestBehindSeconds * MsPerSecond:F0} ms with this client's "
                + $"clock behind the server's, {s_reportLargestAheadSeconds * MsPerSecond:F0} ms with it ahead; this client's longest "
                + $"frame was {s_reportLongestFrameSeconds * MsPerSecond:F0} ms, ending at {s_reportLongestFrameEnded:HH:mm:ss}. "
                + $"This client added back {s_reportClientAddedBackSeconds * MsPerSecond:F0} ms its own long frames dropped. "
                + $"The world clock never stepped back. Next report in {ReportIntervalSec / 60f:F0} min at the earliest.";
            // A clock left behind by at most this client's own longest frame fell behind while this client stalled (a load or a
            // hitch here); the server is only to blame for what that frame doesn't cover (rig R14: a 16.9 s join frame, 30 fps dedi).
            bool ownStall = s_reportLargestBehindSeconds >= s_reportLargestAheadSeconds
                && s_reportLongestFrameSeconds >= s_reportLargestBehindSeconds;
            if (Math.Max(s_reportLargestBehindSeconds, s_reportLargestAheadSeconds) < NotableCorrectionSeconds)
                LoggerOptions.LogInfo(line);
            else if (ownStall)
                LoggerOptions.LogMessage(line + " This client's own long frame covers the correction: its clock fell behind while it was "
                    + "stalled here (a load or a hitch), not because of the server.");
            else
                LoggerOptions.LogMessage(line + " Corrections this large mean the server's frames are too slow or its link is unsteady.");

            s_reportCount = 0;
            s_reportLargestBehindSeconds = 0.0;
            s_reportLargestAheadSeconds = 0.0;
            s_reportLongestFrameSeconds = 0f;
            s_reportClientAddedBackSeconds = 0.0;
            s_nextReportTime = Time.realtimeSinceStartup + ReportIntervalSec;
        }
    }
}
