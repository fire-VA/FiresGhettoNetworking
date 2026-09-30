using System;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// The dedicated server's own health in one Message-level line every report window: frame rate, how much game time Unity
    /// dropped on long frames, and the worst frame split by subsystem. Warns when Server-Side Simulation is overloading it.
    /// </summary>
    internal static class ServerHealth
    {
        private const double OverloadedFramesPerSecond = 10.0;
        private const double OverloadedClockSpeed = 0.9;
        private const double MsPerSecond = 1000.0;
        private const double Percent = 100.0;
        private const float CapTolerance = 0.001f;

        private static double s_windowStart = -1.0;
        private static int s_frames;
        private static int s_framesAtTimestepCap;
        private static double s_lostSeconds;
        private static double s_addedBackAtWindowStart;
        private static double s_longestMs;
        private static string s_longestSplit = "";
        private static int s_peakPlayers;

        // Frames before anyone has connected (the world load, several seconds) are the server starting, not a stall anyone
        // saw; they are reported once on their own instead of as the window's worst frame (rig R14: an 8.6 s "worst frame").
        private static bool s_anyoneConnected;
        private static double s_startupLongestMs;
        private static bool s_startupReported;

        internal static void CountFrame(double frameMs, double networkingMs, double zonesMs, double objectsMs, string slowestHandler)
        {
            if (s_windowStart < 0.0) StartWindow(Time.realtimeSinceStartupAsDouble);
            if (!s_anyoneConnected)
            {
                s_anyoneConnected = ZNet.instance != null && ZNet.instance.GetPeers().Count > 0;
                if (!s_anyoneConnected)
                {
                    if (frameMs > s_startupLongestMs) s_startupLongestMs = frameMs;
                    return;
                }
                // The first window starts now too: its uncounted frames' wall time read as 24.5 frames a second (rig R15).
                StartWindow(Time.realtimeSinceStartupAsDouble);
            }
            s_frames++;
            if (Time.unscaledDeltaTime >= Time.maximumDeltaTime - CapTolerance) s_framesAtTimestepCap++;
            int players = ZNet.instance != null ? ZNet.instance.GetNrOfPlayers() : 0;
            if (players > s_peakPlayers) s_peakPlayers = players;
            if (players > 0) s_lostSeconds += Math.Max(0.0, Time.unscaledDeltaTime - Time.deltaTime);
            if (frameMs <= s_longestMs) return;
            s_longestMs = frameMs;
            s_longestSplit = $"networking {networkingMs:F0}{(slowestHandler != null ? $" (slowest RPC handler {slowestHandler})" : string.Empty)}, "
                + $"zone generation {zonesMs:F0}, object creation {objectsMs:F0}, "
                + $"everything else {Math.Max(0.0, frameMs - networkingMs - zonesMs - objectsMs):F0} ms";
        }

        internal static void EmitSummary()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (!s_startupReported && s_anyoneConnected && s_startupLongestMs > 0.0)
            {
                s_startupReported = true;
                LoggerOptions.LogMessage($"[ServerHealth] server start, before anyone connected: longest frame {s_startupLongestMs:F0} ms "
                    + "(the world load; not counted below).");
            }
            if (s_windowStart < 0.0 || s_frames == 0) return;
            double seconds = Math.Max(1.0, now - s_windowStart);
            double framesPerSecond = s_frames / seconds;
            double clockSpeed = Math.Max(0.0, 1.0 - s_lostSeconds / seconds);
            double addedBack = WorldClock.ServerSecondsAddedBack - s_addedBackAtWindowStart;
            bool simulation = ServerAuthorityPatches.SimulationActive;

            string clock = s_lostSeconds < 1.0
                ? "the world clock kept up with real time"
                : addedBack >= 1.0
                    ? $"Unity's clock ran at {clockSpeed * Percent:F0}% of real time, and FGN added {addedBack:F0} s back to the world clock"
                    : $"the world clock ran at {clockSpeed * Percent:F0}% of real time, so every player's clock was pulled back at each re-sync";
            LoggerOptions.LogMessage(
                $"[ServerHealth] last {seconds / 60.0:F0} min: up to {s_peakPlayers} player(s), {framesPerSecond:F1} frames a second "
                + $"({(s_frames > 0 ? Percent * s_framesAtTimestepCap / s_frames : 0.0):F0}% at Unity's {Time.maximumDeltaTime * MsPerSecond:F0} ms "
                + $"step limit), worst frame {s_longestMs:F0} ms ({s_longestSplit}); {clock}. Server-Side Simulation "
                + (simulation ? $"on, Extended Zone Radius {FiresGhettoNetworkMod.ConfigExtendedZoneRadius.Value}." : "off."));

            if (simulation && s_peakPlayers > 0 && (framesPerSecond < OverloadedFramesPerSecond || clockSpeed < OverloadedClockSpeed))
                LoggerOptions.LogWarning(
                    $"[ServerHealth] Server-Side Simulation is more than this server can carry: {framesPerSecond:F1} frames a second "
                    + $"with up to {s_peakPlayers} player(s). It builds every object around every player on the server, so the cost "
                    + "grows with how far apart players are. Creatures, ships and carts reach other players only as often as the "
                    + "server finishes a frame. Turn 'Enable Server-Side Simulation' off, or at least set 'Extended Zone Radius' to 0 "
                    + "and 'Enable Predictive Zone Streaming' off.");

            StartWindow(now);
        }

        private static void StartWindow(double now)
        {
            s_windowStart = now;
            s_frames = 0;
            s_framesAtTimestepCap = 0;
            s_lostSeconds = 0.0;
            s_addedBackAtWindowStart = WorldClock.ServerSecondsAddedBack;
            s_longestMs = 0.0;
            s_longestSplit = "";
            s_peakPlayers = ZNet.instance != null ? ZNet.instance.GetNrOfPlayers() : 0;
        }
    }
}
