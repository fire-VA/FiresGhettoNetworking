using BepInEx.Configuration;

namespace FiresGhettoNetworkMod.AutoTune
{
    /// <summary>
    /// BepInEx config entries that gate the auto-tune subsystem.
    /// Bound from FiresGhettoNetworkMod.BindConfigs() during Awake.
    /// </summary>
    public static class AutoTuneConfig
    {
        public static ConfigEntry<bool> EnableClientAutoTune;
        public static ConfigEntry<bool> EnableServerAutoTune;
        public static ConfigEntry<bool> LogServerSuggestions;
        public static ConfigEntry<bool> RetuneOnEveryLogin;
        public static ConfigEntry<int>  LinkDowngradeCap;

        public const float DefaultSettleMinimumSeconds = 8f;
        public const float DefaultSettleCeilingSeconds = 300f;
        public const float DefaultSettleQuietHoldSeconds = 6f;
        public const float DefaultSettleQuietKilobytesPerSecond = 24f;
        public const int DefaultSettleStabilityTolerancePercent = 25;
        public const float DefaultProbeSlotGrantTimeoutSeconds = 45f;

        // Settle detection — replaces the fixed post-arrival delay when enabled
        public static ConfigEntry<bool>  EnableSettleDetection;
        public static ConfigEntry<float> SettleMinimumSeconds;
        public static ConfigEntry<float> SettleCeilingSeconds;
        public static ConfigEntry<float> SettleQuietHoldSeconds;
        public static ConfigEntry<float> SettleQuietKilobytesPerSecond;
        public static ConfigEntry<int>   SettleStabilityTolerancePercent;
        public static ConfigEntry<bool>  EnableProbeSlotHandshake;
        public static ConfigEntry<float> ProbeSlotGrantTimeoutSeconds;

        // Probe timing knobs (rarely user-tuned, but exposed for emergencies)
        public static ConfigEntry<float> ProbeStartDelaySeconds;
        public static ConfigEntry<float> PlayerArrivalTimeoutSeconds;
        public static ConfigEntry<float> ProbePingTimeoutSeconds;
        public static ConfigEntry<int>   ProbePingCount;
        public static ConfigEntry<int>   ProbeBandwidthPayloadBytes;
        public static ConfigEntry<int>   ProbePingAbortMs;

        // Rolling-monitor knobs (continuous re-probing after initial probe)
        public static ConfigEntry<bool>  EnableRollingMonitor;
        public static ConfigEntry<float> RollingMonitorIntervalMinutes;
        public static ConfigEntry<int>   RollingMonitorWindow;

        public static void Init(ConfigFile config)
        {
            EnableClientAutoTune = config.Bind(
                "06 - Auto-Tune",
                "Enable Client Auto-Tune",
                true,
                Cfg.Basic("Tests your PC and connection on join and picks the network settings for you."));

            EnableServerAutoTune = config.Bind(
                "06 - Auto-Tune",
                "Enable Server Auto-Tune",
                true,
                Cfg.Basic("Rates the server's CPU and RAM at startup and picks the network settings for it."));

            LogServerSuggestions = config.Bind(
                "06 - Auto-Tune",
                "Log Server Suggestions",
                false,   // off by default: no periodic log chatter
                Cfg.Adv("Writes tuning hints to the server log. Logging only."));

            RetuneOnEveryLogin = config.Bind(
                "06 - Auto-Tune",
                "Retune On Every Login",
                false,
                Cfg.Adv("Runs the full test on every join instead of reusing the saved result."));

            LinkDowngradeCap = config.Bind(
                "06 - Auto-Tune",
                "Link Downgrade Cap",
                1,
                Cfg.Adv("How many tiers a bad connection may drop you below your PC's tier (0 = never).",
                    new AcceptableValueRange<int>(0, 2)));

            EnableSettleDetection = config.Bind(
                "07 - Auto-Tune - Probe",
                "Enable Settle Detection",
                true,
                Cfg.Adv("Waits until your connection calms down after joining before testing."));

            SettleMinimumSeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Settle Minimum Seconds",
                DefaultSettleMinimumSeconds,
                Cfg.Adv("Never test sooner than this many seconds after joining.",
                    new AcceptableValueRange<float>(0f, 120f)));

            SettleCeilingSeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Settle Ceiling Seconds",
                DefaultSettleCeilingSeconds,
                Cfg.Adv("Test anyway after waiting this many seconds for the connection to settle.",
                    new AcceptableValueRange<float>(30f, 900f)));

            SettleQuietHoldSeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Settle Quiet Hold Seconds",
                DefaultSettleQuietHoldSeconds,
                Cfg.Adv("Seconds the connection must stay steady to count as settled.",
                    new AcceptableValueRange<float>(1f, 60f)));

            SettleQuietKilobytesPerSecond = config.Bind(
                "07 - Auto-Tune - Probe",
                "Settle Quiet KB Per Second",
                DefaultSettleQuietKilobytesPerSecond,
                Cfg.Adv("Traffic in KB/s below which the connection counts as idle.",
                    new AcceptableValueRange<float>(1f, 512f)));

            SettleStabilityTolerancePercent = config.Bind(
                "07 - Auto-Tune - Probe",
                "Settle Stability Tolerance",
                DefaultSettleStabilityTolerancePercent,
                Cfg.Adv("How much traffic may swing (percent) and still count as settled.",
                    new AcceptableValueRange<int>(5, 100)));

            EnableProbeSlotHandshake = config.Bind(
                "07 - Auto-Tune - Probe",
                "Enable Probe Slot Handshake",
                true,
                Cfg.Adv("Takes turns with other joining players so tests do not skew each other."));

            ProbeSlotGrantTimeoutSeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Probe Slot Grant Timeout Seconds",
                DefaultProbeSlotGrantTimeoutSeconds,
                Cfg.Adv("Seconds to wait for your turn before testing anyway.",
                    new AcceptableValueRange<float>(5f, 300f)));

            ProbeStartDelaySeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Start Delay Seconds",
                30f,
                Cfg.Adv("Fixed wait before testing when Settle Detection is off.",
                    new AcceptableValueRange<float>(2f, 300f)));

            PlayerArrivalTimeoutSeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Player Arrival Timeout Seconds",
                180f,
                Cfg.Adv("Seconds to wait for you to spawn in before the test moves on.",
                    new AcceptableValueRange<float>(30f, 600f)));

            ProbePingTimeoutSeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Ping Timeout Seconds",
                5f,
                Cfg.Adv("Seconds before a test ping counts as failed.",
                    new AcceptableValueRange<float>(1f, 15f)));

            ProbePingCount = config.Bind(
                "07 - Auto-Tune - Probe",
                "Ping Count",
                10,
                Cfg.Adv("Test pings per check. More = steadier result, slightly slower.",
                    new AcceptableValueRange<int>(5, 20)));

            ProbePingAbortMs = config.Bind(
                "07 - Auto-Tune - Probe",
                "Ping Abort Ms",
                2000,
                Cfg.Adv("A test ping slower than this (ms) counts as failed.",
                    new AcceptableValueRange<int>(500, 5000)));

            ProbeBandwidthPayloadBytes = config.Bind(
                "07 - Auto-Tune - Probe",
                "Bandwidth Probe Bytes",
                128 * 1024,
                Cfg.Adv("Size in bytes of each download test sample.",
                    new AcceptableValueRange<int>(8 * 1024, 512 * 1024)));

            EnableRollingMonitor = config.Bind(
                "08 - Auto-Tune - Monitor",
                "Enable Rolling Monitor",
                true,
                Cfg.Adv("Re-checks your ping while you play and adjusts your tier if it changes."));

            RollingMonitorIntervalMinutes = config.Bind(
                "08 - Auto-Tune - Monitor",
                "Re-Probe Interval Minutes",
                5f,
                Cfg.Adv("Minutes between ping re-checks.",
                    new AcceptableValueRange<float>(1f, 30f)));

            RollingMonitorWindow = config.Bind(
                "08 - Auto-Tune - Monitor",
                "Rolling Window Size",
                5,
                Cfg.Adv("How many recent re-checks decide your tier.",
                    new AcceptableValueRange<int>(3, 10)));
        }
    }
}
