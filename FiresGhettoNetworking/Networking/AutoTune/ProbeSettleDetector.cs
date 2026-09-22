using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace FiresGhettoNetworkMod.AutoTune
{
    public enum SettleOutcome
    {
        LinkWentQuiet,
        ForcedAtCeiling,
        LinkLost
    }

    /// <summary>
    /// Waits until the client's link and main thread are quiet enough that a latency sample
    /// measures the connection rather than whatever the modpack is still transferring. A fixed
    /// post-arrival delay cannot cover both a vanilla server and a modpack whose asset sync runs
    /// for minutes past arrival, so the wait ends on observed quiet instead of on a clock.
    /// </summary>
    public static class ProbeSettleDetector
    {
        private const float SampleIntervalSeconds = 0.25f;
        private const float ProgressReportIntervalSeconds = 15f;
        private const int PlayFabInFlightQueueShare = 4;
        private const int BytesPerKilobyte = 1024;
        private const float MillisecondsPerSecond = 1000f;
        private const float QuietSendQueueKilobytes = 16f;
        private const float QuietFrameMilliseconds = 120f;
        private const int MinimumWindowSamples = 4;

        public static SettleOutcome LastOutcome { get; private set; } = SettleOutcome.ForcedAtCeiling;

        public static IEnumerator WaitForQuietLink(ZNetPeer serverPeer)
        {
            float minimumSeconds = AutoTuneConfig.SettleMinimumSeconds?.Value ?? AutoTuneConfig.DefaultSettleMinimumSeconds;
            float ceilingSeconds = AutoTuneConfig.SettleCeilingSeconds?.Value ?? AutoTuneConfig.DefaultSettleCeilingSeconds;
            float requiredQuietSeconds = AutoTuneConfig.SettleQuietHoldSeconds?.Value ?? AutoTuneConfig.DefaultSettleQuietHoldSeconds;
            float quietBytesPerSecond = (AutoTuneConfig.SettleQuietKilobytesPerSecond?.Value ?? AutoTuneConfig.DefaultSettleQuietKilobytesPerSecond) * BytesPerKilobyte;
            bool crossplay = NetworkingRatesGroup.IsCrossplay(serverPeer);

            float stabilityTolerance = (AutoTuneConfig.SettleStabilityTolerancePercent?.Value ?? AutoTuneConfig.DefaultSettleStabilityTolerancePercent) / 100f;
            int windowSamples = Mathf.Max(MinimumWindowSamples, Mathf.CeilToInt(requiredQuietSeconds / SampleIntervalSeconds));
            var throughputWindow = new Queue<float>(windowSamples);

            float waitedSeconds = 0f;
            float sinceSampleSeconds = 0f;
            float worstFrameMilliseconds = 0f;
            float sinceReportSeconds = 0f;
            string lastBlocker = "starting";

            while (waitedSeconds < ceilingSeconds)
            {
                if (!LinkIsUsable(serverPeer))
                {
                    LastOutcome = SettleOutcome.LinkLost;
                    yield break;
                }

                yield return null;

                float frameSeconds = Time.unscaledDeltaTime;
                waitedSeconds += frameSeconds;
                sinceSampleSeconds += frameSeconds;
                sinceReportSeconds += frameSeconds;
                worstFrameMilliseconds = Mathf.Max(worstFrameMilliseconds, frameSeconds * MillisecondsPerSecond);

                if (sinceSampleSeconds < SampleIntervalSeconds) continue;

                float linkBytesPerSecond = ReadLinkBytesPerSecond(serverPeer);
                float sendQueueKilobytes = ReadSendQueueBytes(serverPeer, crossplay) / (float)BytesPerKilobyte;
                bool activeAreaLoaded = ActiveAreaLoaded();

                float worstFrameThisSample = worstFrameMilliseconds;
                sinceSampleSeconds = 0f;
                worstFrameMilliseconds = 0f;

                // A stall, a backed-up queue or a half-loaded zone does not just fail this sample: it
                // invalidates the ones before it, because whatever caused it was distorting them too.
                string hardBlocker = DescribeHardBlocker(sendQueueKilobytes, worstFrameThisSample, activeAreaLoaded);
                if (hardBlocker != null)
                {
                    throughputWindow.Clear();
                    lastBlocker = hardBlocker;
                }
                else
                {
                    throughputWindow.Enqueue(linkBytesPerSecond);
                    while (throughputWindow.Count > windowSamples) throughputWindow.Dequeue();
                    lastBlocker = throughputWindow.Count < windowSamples
                        ? "building a throughput baseline"
                        : DescribeUnsteadyWindow(throughputWindow, quietBytesPerSecond, stabilityTolerance);
                }

                float windowSeconds = throughputWindow.Count * SampleIntervalSeconds;
                if (sinceReportSeconds >= ProgressReportIntervalSeconds)
                {
                    sinceReportSeconds = 0f;
                    LoggerOptions.LogInfo(
                        $"[AutoTune] Settling — {waitedSeconds:0}s waited, link {linkBytesPerSecond / BytesPerKilobyte:0.0} KB/s, "
                        + $"queue {sendQueueKilobytes:0.0} KB, worst frame {worstFrameThisSample:0}ms, steady for {windowSeconds:0.0}s"
                        + (lastBlocker == null ? "" : $" — waiting on {lastBlocker}"));
                }

                if (lastBlocker == null && waitedSeconds >= minimumSeconds)
                {
                    LoggerOptions.LogInfo(
                        $"[AutoTune] Link steady for {windowSeconds:0.0}s after {waitedSeconds:0.0}s "
                        + $"({DescribeWindow(throughputWindow)}) — probing now.");
                    LastOutcome = SettleOutcome.LinkWentQuiet;
                    yield break;
                }
            }

            LoggerOptions.LogWarning(
                $"[AutoTune] Link never settled within {ceilingSeconds:0}s (last blocker: {lastBlocker}). Probing anyway — "
                + "this sample is marked unsettled and may not lower the applied tier.");
            LastOutcome = SettleOutcome.ForcedAtCeiling;
        }

        private static string DescribeHardBlocker(
            float sendQueueKilobytes,
            float worstFrameMilliseconds,
            bool activeAreaLoaded)
        {
            if (!activeAreaLoaded) return "zone load";
            if (sendQueueKilobytes > QuietSendQueueKilobytes) return $"send queue {sendQueueKilobytes:0.0} KB";
            if (worstFrameMilliseconds > QuietFrameMilliseconds) return $"main-thread stall {worstFrameMilliseconds:0}ms";
            return null;
        }

        /// <summary>
        /// Null once the window is fit to measure on. Two ways to qualify: genuinely idle, or holding a
        /// steady rate. The second matters because a heavy modpack never goes idle — it settles into a
        /// constant load, and demanding silence there means never settling and probing at the ceiling on
        /// every login. A burst or a stall, which are what actually corrupt a sample, both show up as a
        /// swing across the window.
        /// </summary>
        private static string DescribeUnsteadyWindow(Queue<float> window, float quietBytesPerSecond, float tolerance)
        {
            float lowest = float.MaxValue, highest = 0f;
            foreach (float sample in window)
            {
                if (sample < lowest) lowest = sample;
                if (sample > highest) highest = sample;
            }

            if (highest <= quietBytesPerSecond) return null;
            if (highest > 0f && highest - lowest <= highest * tolerance) return null;
            return $"link traffic swinging {lowest / BytesPerKilobyte:0.0}-{highest / BytesPerKilobyte:0.0} KB/s";
        }

        private static string DescribeWindow(Queue<float> window)
        {
            float lowest = float.MaxValue, highest = 0f;
            foreach (float sample in window)
            {
                if (sample < lowest) lowest = sample;
                if (sample > highest) highest = sample;
            }
            return $"{lowest / BytesPerKilobyte:0.0}-{highest / BytesPerKilobyte:0.0} KB/s";
        }

        /// <summary>
        /// Outbound plus inbound bytes per second for the server link. ZSteamSocket reports Steam's
        /// own figures; ZPlayFabSocket inherits ZNetStats, whose byte counters are real even though
        /// its ping and quality fields are always zero.
        /// </summary>
        private static float ReadLinkBytesPerSecond(ZNetPeer serverPeer)
        {
            try
            {
                ISocket socket = NetworkingRatesGroup.UnwrapSocket(serverPeer.m_socket);
                if (socket == null) return 0f;
                float localQuality, remoteQuality, outBytesPerSecond, inBytesPerSecond;
                int ping;
                socket.GetConnectionQuality(out localQuality, out remoteQuality, out ping, out outBytesPerSecond, out inBytesPerSecond);
                return outBytesPerSecond + inBytesPerSecond;
            }
            catch { return 0f; }
        }

        private static float ReadSendQueueBytes(ZNetPeer serverPeer, bool crossplay)
        {
            try
            {
                ISocket socket = NetworkingRatesGroup.UnwrapSocket(serverPeer.m_socket);
                if (socket == null) return 0f;
                int reported = socket.GetSendQueueSize();
                return crossplay ? reported * PlayFabInFlightQueueShare : reported;
            }
            catch { return 0f; }
        }

        private static bool ActiveAreaLoaded()
        {
            try { return ZoneSystem.instance == null || ZoneSystem.instance.IsActiveAreaLoaded(); }
            catch { return true; }
        }

        private static bool LinkIsUsable(ZNetPeer serverPeer)
        {
            return ZNet.instance != null && serverPeer != null && serverPeer.m_rpc != null;
        }
    }
}
