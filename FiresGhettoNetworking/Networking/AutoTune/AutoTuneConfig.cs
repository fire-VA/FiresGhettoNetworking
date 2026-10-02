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
                "When you join a server, tests your PC, ping and connection, picks a LOW/MED/HIGH tier and writes ZDO Send Rate,\n" +
                "Queue Size and Send Rate Min/Max here, replacing your edits. Turn off to set them yourself. Client only.");

            EnableServerAutoTune = config.Bind(
                "06 - Auto-Tune",
                "Enable Server Auto-Tune",
                true,
                "Rates the host's CPU and RAM at startup, picks a LOW/MED/HIGH tier and writes ZDO Send Rate, Queue Size and\n" +
                "Send Rate Min/Max here. While on, the tier also overrides ZDO Throttle Distance, AI LOD, RPC AoI Radius\n" +
                "and Steam buffers. Server only.");

            LogServerSuggestions = config.Bind(
                "06 - Auto-Tune",
                "Log Server Suggestions",
                true,
                "Write tuning hints to the server log: at startup, the tier and values this server would use (only\n" +
                "while Enable Server Auto-Tune is off), and at most every 15 minutes a summary of the tiers and ping\n" +
                "reported by connected players. Logging only, nothing is changed. Server only.");

            RetuneOnEveryLogin = config.Bind(
                "06 - Auto-Tune",
                "Retune On Every Login",
                false,
                "Run the full test every time you join instead of reusing the tier saved for this server and PC\n" +
                "(saved results last 7 days). Uses up to about 1 MB of test traffic per join. Client only.");

            LinkDowngradeCap = config.Bind(
                "06 - Auto-Tune",
                "Link Downgrade Cap",
                1,
                new ConfigDescription(
                    "Your PC's tier is the ceiling. Only a bad connection lowers it, and this sets by how many tiers:\n" +
                    "0 = never, 1 = one tier, 2 = up to two tiers. An average connection never lowers it. Client only.",
                    new AcceptableValueRange<int>(0, 2)));

            EnableSettleDetection = config.Bind(
                "07 - Auto-Tune - Probe",
                "Enable Settle Detection",
                true,
                new ConfigDescription(
                    "Start the test once your connection to the server has settled (steady traffic, no send backlog,\n" +
                    "no frame stalls, nearby zones loaded) instead of after a fixed delay. Helps with modpacks that keep\n" +
                    "syncing after you arrive. While on, Start Delay Seconds is ignored. Client only."));

            SettleMinimumSeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Settle Minimum Seconds",
                DefaultSettleMinimumSeconds,
                new ConfigDescription(
                    "Never start the test sooner than this many seconds after you arrive in the world, even if the\n" +
                    "connection already looks settled. Client only.",
                    new AcceptableValueRange<float>(0f, 120f)));

            SettleCeilingSeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Settle Ceiling Seconds",
                DefaultSettleCeilingSeconds,
                new ConfigDescription(
                    "Stop waiting for the connection to settle after this many seconds and test anyway. A result taken\n" +
                    "this way can raise your tier but never lower it, and is not saved, so the next join tests again.\n" +
                    "Client only.",
                    new AcceptableValueRange<float>(30f, 900f)));

            SettleQuietHoldSeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Settle Quiet Hold Seconds",
                DefaultSettleQuietHoldSeconds,
                new ConfigDescription(
                    "How many seconds the connection must stay quiet or steady before it counts as settled. Higher is\n" +
                    "more careful but starts the test later. Client only.",
                    new AcceptableValueRange<float>(1f, 60f)));

            SettleQuietKilobytesPerSecond = config.Bind(
                "07 - Auto-Tune - Probe",
                "Settle Quiet KB Per Second",
                DefaultSettleQuietKilobytesPerSecond,
                new ConfigDescription(
                    "Upload plus download to the server, in KB/s, below which the connection counts as idle. A busier\n" +
                    "connection can still settle if its traffic is steady (see Settle Stability Tolerance). Client only.",
                    new AcceptableValueRange<float>(1f, 512f)));

            SettleStabilityTolerancePercent = config.Bind(
                "07 - Auto-Tune - Probe",
                "Settle Stability Tolerance",
                DefaultSettleStabilityTolerancePercent,
                new ConfigDescription(
                    "How much traffic may swing during the hold time and still count as settled, as a percent of the\n" +
                    "highest reading. Lets a modpack with constant background traffic settle without going silent.\n" +
                    "Lower is stricter; higher settles sooner on a noisy connection. Client only.",
                    new AcceptableValueRange<int>(5, 100)));

            EnableProbeSlotHandshake = config.Bind(
                "07 - Auto-Tune - Probe",
                "Enable Probe Slot Handshake",
                true,
                new ConfigDescription(
                    "Ask the server for a turn before testing, so players joining at the same time do not skew each\n" +
                    "other's results. A server without this mod never answers; the test then starts after Probe Slot\n" +
                    "Grant Timeout Seconds. Client only."));

            ProbeSlotGrantTimeoutSeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Probe Slot Grant Timeout Seconds",
                DefaultProbeSlotGrantTimeoutSeconds,
                new ConfigDescription(
                    "How many seconds to wait for the server to give you a turn before testing anyway. A result taken\n" +
                    "this way can raise your tier but never lower it. Client only.",
                    new AcceptableValueRange<float>(5f, 300f)));

            ProbeStartDelaySeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Start Delay Seconds",
                30f,
                new ConfigDescription(
                    "Fixed number of seconds to wait after you arrive in the world before testing. Only used when\n" +
                    "Enable Settle Detection is off. Raise it for heavy modpacks that keep loading after you arrive.\n" +
                    "Client only.",
                    new AcceptableValueRange<float>(2f, 300f)));

            PlayerArrivalTimeoutSeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Player Arrival Timeout Seconds",
                180f,
                new ConfigDescription(
                    "The test waits for you to finish spawning into the world. If that has not happened after this\n" +
                    "many seconds, it moves on anyway. Raise only if very slow loads hit this limit. Client only.",
                    new AcceptableValueRange<float>(30f, 600f)));

            ProbePingTimeoutSeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Ping Timeout Seconds",
                5f,
                new ConfigDescription(
                    "How many seconds to wait for one test ping to return before counting it as failed. The download\n" +
                    "and upload tests wait 1.5 times this per sample. Client only.",
                    new AcceptableValueRange<float>(1f, 15f)));

            ProbePingCount = config.Bind(
                "07 - Auto-Tune - Probe",
                "Ping Count",
                10,
                new ConfigDescription(
                    "Number of test pings per ping check. The slowest one is dropped before scoring. More pings give a\n" +
                    "steadier result but take a little longer. Client only.",
                    new AcceptableValueRange<int>(5, 20)));

            ProbePingAbortMs = config.Bind(
                "07 - Auto-Tune - Probe",
                "Ping Abort Ms",
                2000,
                new ConfigDescription(
                    "A ping this slow (in milliseconds) or one that times out counts as failed, and two failed pings\n" +
                    "stop the check. On join the check is retried up to 3 times before your tier is set to LOW; a\n" +
                    "failed check during play leaves your tier unchanged. Client only.",
                    new AcceptableValueRange<int>(500, 5000)));

            ProbeBandwidthPayloadBytes = config.Bind(
                "07 - Auto-Tune - Probe",
                "Bandwidth Probe Bytes",
                128 * 1024,
                new ConfigDescription(
                    "Size in bytes of each download test sample the server sends (3 samples, the fastest counts).\n" +
                    "Larger samples measure fast connections more accurately but use more data. Values above 256 KB are\n" +
                    "capped to 256 KB by the server. Client only.",
                    new AcceptableValueRange<int>(8 * 1024, 512 * 1024)));

            EnableRollingMonitor = config.Bind(
                "08 - Auto-Tune - Monitor",
                "Enable Rolling Monitor",
                true,
                "After the first test, re-check your ping on a timer while you play and move your tier up or down\n" +
                "if your connection changes. Re-checks are ping only and use very little traffic. Client only.");

            RollingMonitorIntervalMinutes = config.Bind(
                "08 - Auto-Tune - Monitor",
                "Re-Probe Interval Minutes",
                5f,
                new ConfigDescription(
                    "Minutes between ping re-checks. Shorter reacts faster to connection changes; longer sends less\n" +
                    "test traffic. Client only.",
                    new AcceptableValueRange<float>(1f, 30f)));

            RollingMonitorWindow = config.Bind(
                "08 - Auto-Tune - Monitor",
                "Rolling Window Size",
                5,
                new ConfigDescription(
                    "How many recent re-checks are remembered; the most common tier among them wins. Moving up needs\n" +
                    "2 agreeing re-checks in a row, moving down needs 3. Client only.",
                    new AcceptableValueRange<int>(3, 10)));
        }
    }
}
