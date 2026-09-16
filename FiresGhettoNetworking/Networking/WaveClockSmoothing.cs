using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Every two seconds the server overwrites each client's clock (ZNet.RPC_NetTime); vanilla reads wave height straight from
    /// it, so a correction moves the water under a ship in one physics step and Ship.UpdateWaterForce reads that as a hull slam.
    /// Waves here follow a clock that pays corrections back gradually, unless one is too large to be network timing.
    /// </summary>
    [HarmonyPatch]
    public static class WaveClockSmoothing
    {
        private const double DaySeconds = 86400.0;
        private const double MaxCarriedSeconds = 1.0;
        private const double ApplyAtOnceSeconds = 5.0;

        // Seconds of carried correction paid back per second of game time, so waves run at most 20% fast or slow while
        // a correction is paid back; nowhere near enough to count as an impact.
        private const double PaybackRate = 0.2;

        private const double ReportThresholdSeconds = 0.05;
        private const float ReportIntervalSec = 300f;

        private static double s_carriedSeconds;
        private static int s_reportCount;
        private static double s_reportLargestBehindSeconds;
        private static double s_reportLargestAheadSeconds;
        private static float s_reportLongestFrameSeconds;
        private static DateTime s_reportLongestFrameEnded;
        private static float s_nextReportTime;

        private static bool Enabled => FiresGhettoNetworkMod.ConfigFixBoatDamageFromTimeSync?.Value ?? false;

        [HarmonyPatch(typeof(ZNet), "RPC_NetTime")]
        [HarmonyPrefix]
        public static void ZNet_RPC_NetTime_Prefix(double time, double ___m_netTime)
        {
            double correction = time - ___m_netTime;
            if (!Enabled || Math.Abs(correction) >= ApplyAtOnceSeconds)
            {
                s_carriedSeconds = 0.0;
                return;
            }

            s_carriedSeconds = Math.Max(-MaxCarriedSeconds, Math.Min(MaxCarriedSeconds, s_carriedSeconds - correction));
            if (Math.Abs(correction) >= ReportThresholdSeconds)
                CapeCrashDiagnostics.Log($"Server clock correction {correction * 1000:0} ms, carried {s_carriedSeconds * 1000:0} ms");
            Report(correction);
        }

        [HarmonyPatch(typeof(ZNet), "UpdateNetTime")]
        [HarmonyPostfix]
        public static void ZNet_UpdateNetTime_Postfix(float dt)
        {
            float frameSeconds = Time.unscaledDeltaTime;
            if (frameSeconds > s_reportLongestFrameSeconds)
            {
                s_reportLongestFrameSeconds = frameSeconds;
                s_reportLongestFrameEnded = DateTime.Now;
            }
            if (s_carriedSeconds == 0.0) return;

            double payback = dt * PaybackRate;
            s_carriedSeconds = s_carriedSeconds > 0.0
                ? Math.Max(0.0, s_carriedSeconds - payback)
                : Math.Min(0.0, s_carriedSeconds + payback);
        }

        [HarmonyPatch(typeof(WaterVolume), "UpdateWaterTime")]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> WaterVolume_UpdateWaterTime_UseWaveClock(
            IEnumerable<CodeInstruction> instructions)
        {
            return RedirectWrappedDayTime(instructions, "WaterVolume.UpdateWaterTime");
        }

        [HarmonyPatch(typeof(Fish), nameof(Fish.CustomFixedUpdate))]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Fish_CustomFixedUpdate_UseWaveClock(
            IEnumerable<CodeInstruction> instructions)
        {
            return RedirectWrappedDayTime(instructions, "Fish.CustomFixedUpdate");
        }

        /// <summary>Stands in for ZNet.GetWrappedDayTimeSeconds() at the redirected call sites, so the signature must match it.</summary>
        public static float WrappedWaveTime(ZNet znet)
        {
            if (s_carriedSeconds == 0.0 || !Enabled)
                return znet.GetWrappedDayTimeSeconds();

            double waveTime = znet.GetTimeSeconds() + s_carriedSeconds;
            return (float)(waveTime - Math.Floor(waveTime / DaySeconds) * DaySeconds);
        }

        private static IEnumerable<CodeInstruction> RedirectWrappedDayTime(
            IEnumerable<CodeInstruction> instructions, string patchedMethod)
        {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo netClock = AccessTools.Method(typeof(ZNet), nameof(ZNet.GetWrappedDayTimeSeconds));
            MethodInfo waveClock = AccessTools.Method(typeof(WaveClockSmoothing), nameof(WrappedWaveTime));
            int redirected = 0;

            foreach (CodeInstruction instruction in code)
            {
                if (!instruction.Calls(netClock)) continue;

                instruction.opcode = OpCodes.Call;
                instruction.operand = waveClock;
                redirected++;
            }

            if (redirected == 0)
                LoggerOptions.LogWarning(
                    $"[WaveClock] {patchedMethod} no longer reads ZNet.GetWrappedDayTimeSeconds; its waves keep vanilla's clock.");

            return code;
        }

        private static void Report(double correctionSeconds)
        {
            if (Math.Abs(correctionSeconds) < ReportThresholdSeconds) return;

            s_reportCount++;
            if (correctionSeconds > s_reportLargestBehindSeconds) s_reportLargestBehindSeconds = correctionSeconds;
            if (-correctionSeconds > s_reportLargestAheadSeconds) s_reportLargestAheadSeconds = -correctionSeconds;
            if (Time.realtimeSinceStartup < s_nextReportTime) return;

            LoggerOptions.LogInfo(
                $"[WaveClock] Eased in {s_reportCount} server clock correction(s) of {ReportThresholdSeconds * 1000:F0} ms or more "
                + $"since the last report: the largest {s_reportLargestBehindSeconds * 1000:F0} ms with this client's clock behind the "
                + $"server's, {s_reportLargestAheadSeconds * 1000:F0} ms with it ahead; this client's longest frame was "
                + $"{s_reportLongestFrameSeconds * 1000:F0} ms, ending at {s_reportLongestFrameEnded:HH:mm:ss}. Vanilla applies each one at once, which moves the water under ships in "
                + $"a single step and can damage a boat with players aboard. Next report in {ReportIntervalSec / 60f:F0} min at the earliest.");

            s_reportCount = 0;
            s_reportLargestBehindSeconds = 0.0;
            s_reportLargestAheadSeconds = 0.0;
            s_reportLongestFrameSeconds = 0f;
            s_nextReportTime = Time.realtimeSinceStartup + ReportIntervalSec;
        }
    }
}
