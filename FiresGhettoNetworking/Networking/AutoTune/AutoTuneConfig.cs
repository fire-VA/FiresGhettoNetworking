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
                "Run a brief probe on first login to a server - hardware, ping, download AND upload - pick a tier\n" +
                "(LOW/MED/HIGH), and WRITE the resulting network values into this config: ZDO Send Rate, Queue Size,\n" +
                "Send Rate Min and Send Rate Max. The config then shows exactly what is running.\n" +
                "Upload is measured separately from the tier: a strong PC on a thin uplink gets its upload ceiling\n" +
                "set to its real upload speed instead of the tier's, which can be below vanilla.\n" +
                "While ON those four settings are Auto-Tune's and edits to them are replaced on the next tune.\n" +
                "Turn OFF to set them yourself - they keep the last values Auto-Tune chose. CLIENT-SIDE only.\n" +
                "(Before this version Auto-Tune ran values over the top of the config without writing them, so the\n" +
                "file could show one value while another ran. Nothing that was actually in effect is lost.)");

            EnableServerAutoTune = config.Bind(
                "06 - Auto-Tune",
                "Enable Server Auto-Tune",
                true,
                "When the mod runs on a dedicated server, score the host's CPU/RAM, pick a tier, and WRITE the resulting\n" +
                "network values into this config: ZDO Send Rate, Queue Size, Send Rate Min and Send Rate Max. The config\n" +
                "then shows exactly what is running. Other server knobs (ZDO throttle, AI LOD, RPC AoI, zone radius,\n" +
                "Steam buffers) follow the tier too.\n" +
                "Send rates are ASYMMETRIC: Max scales with the tier so fast players can burst, while Min stays at the LOW\n" +
                "baseline whatever the tier - Min is the floor the per-player controller backs off to, so a slow player's\n" +
                "connection is never forced above what it can take.\n" +
                "While ON those four settings are Auto-Tune's and edits are replaced on restart. Turn OFF to set every\n" +
                "server knob yourself - they keep the last values Auto-Tune chose. SERVER-SIDE only.");

            LogServerSuggestions = config.Bind(
                "06 - Auto-Tune",
                "Log Server Suggestions",
                true,
                "When on, the server logs its own self-tune tier + recommended values at startup, and periodically\n" +
                "logs the median tier reported by connected clients with suggested adjustments. Pure logging —\n" +
                "no settings are changed. SERVER-SIDE only.");

            RetuneOnEveryLogin = config.Bind(
                "06 - Auto-Tune",
                "Retune On Every Login",
                false,
                "When on, ignore the cached tier on disk and re-probe every time you join. Costs ~50KB of probe\n" +
                "traffic per join but reflects current ISP/route conditions. CLIENT-SIDE only.");

            LinkDowngradeCap = config.Bind(
                "06 - Auto-Tune",
                "Link Downgrade Cap",
                1,
                new ConfigDescription(
                    "Your machine's measured tier is the CEILING — a great connection can never push you above\n" +
                    "what your hardware can actually handle. Only a genuinely BAD connection pulls your tier DOWN,\n" +
                    "and this caps how far. 1 (default) = a bad link drops you one tier (e.g. a strong PC on a\n" +
                    "170ms link lands MED, not LOW). 0 = connection never lowers your tier (machine only). 2 = a\n" +
                    "bad link can drop you two tiers. Okay/medium ping costs nothing either way. CLIENT-SIDE only.",
                    new AcceptableValueRange<int>(0, 2)));

            EnableSettleDetection = config.Bind(
                "07 - Auto-Tune - Probe",
                "Enable Settle Detection",
                true,
                new ConfigDescription(
                    "Start the probe when the link actually goes quiet instead of after a fixed delay. The probe\n" +
                    "watches link throughput, the send queue, main-thread stalls and zone loading, and begins once\n" +
                    "all four have been quiet for the hold time below. A fixed delay cannot cover both a vanilla\n" +
                    "server and a modpack whose asset sync runs for minutes past arrival: too short samples the\n" +
                    "sync and files a fast connection as LOW, too long delays every client for the worst case.\n" +
                    "When this is ON, 'Start Delay Seconds' is ignored. CLIENT-SIDE only."));

            SettleMinimumSeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Settle Minimum Seconds",
                DefaultSettleMinimumSeconds,
                new ConfigDescription(
                    "Never probe sooner than this after the player arrives, even if the link already looks quiet.\n" +
                    "Guards against sampling inside the lull between two bursts of arrival traffic.",
                    new AcceptableValueRange<float>(0f, 120f)));

            SettleCeilingSeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Settle Ceiling Seconds",
                DefaultSettleCeilingSeconds,
                new ConfigDescription(
                    "Give up waiting for quiet after this long and probe anyway. A sample taken at the ceiling is\n" +
                    "marked unsettled: it can RAISE the tier but never lower it, so a server that is never quiet\n" +
                    "cannot pin a good connection to a low tier. The rolling monitor re-probes later regardless.",
                    new AcceptableValueRange<float>(30f, 900f)));

            SettleQuietHoldSeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Settle Quiet Hold Seconds",
                DefaultSettleQuietHoldSeconds,
                new ConfigDescription(
                    "How long every signal must stay quiet before the link counts as settled.",
                    new AcceptableValueRange<float>(1f, 60f)));

            SettleQuietKilobytesPerSecond = config.Bind(
                "07 - Auto-Tune - Probe",
                "Settle Quiet KB Per Second",
                DefaultSettleQuietKilobytesPerSecond,
                new ConfigDescription(
                    "Combined send+receive throughput on the server link, below which the link counts as idle.\n" +
                    "Steady-state Valheim play sits well under this; a mod pushing assets or configs sits far above.\n" +
                    "A link that never drops this low can still settle — see Settle Stability Tolerance.",
                    new AcceptableValueRange<float>(1f, 512f)));

            SettleStabilityTolerancePercent = config.Bind(
                "07 - Auto-Tune - Probe",
                "Settle Stability Tolerance",
                DefaultSettleStabilityTolerancePercent,
                new ConfigDescription(
                    "How much the link's throughput may vary across the hold window and still count as settled,\n" +
                    "as a percentage of the highest sample in that window. A link is ready to measure when it is\n" +
                    "STEADY, not only when it is idle: a modpack that sits at a constant 130 KB/s is in its steady\n" +
                    "state, and waiting for silence there waits forever and probes at the ceiling every login.\n" +
                    "What actually ruins a sample is a burst or a stall, and both of those show up as a swing.\n" +
                    "Lower = stricter, demands a flatter line; higher = settles sooner on a noisy link.",
                    new AcceptableValueRange<int>(5, 100)));

            EnableProbeSlotHandshake = config.Bind(
                "07 - Auto-Tune - Probe",
                "Enable Probe Slot Handshake",
                true,
                new ConfigDescription(
                    "Ask the server for a probe slot once the link is settled, and wait for its go-ahead. The\n" +
                    "server hands out one slot at a time so two clients probing at once cannot measure each\n" +
                    "other's traffic and both file themselves too low. Servers without this mod, and unmodded\n" +
                    "clients on a crossplay server, simply never take part — the client proceeds on the timeout."));

            ProbeSlotGrantTimeoutSeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Probe Slot Grant Timeout Seconds",
                DefaultProbeSlotGrantTimeoutSeconds,
                new ConfigDescription(
                    "How long to wait for the server's go-ahead before probing without one. Reached on a server\n" +
                    "that does not run this mod, or one holding the slot for another client; the sample is then\n" +
                    "treated as unsettled and may not lower the tier.",
                    new AcceptableValueRange<float>(5f, 300f)));

            ProbeStartDelaySeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Start Delay Seconds",
                30f,
                new ConfigDescription(
                    "Wait this long AFTER the player has actually arrived in the world (Game.m_playerInitialSpawn\n" +
                    "fired) before starting the probe. Lets the post-spawn burst — inventory equip, ZDO\n" +
                    "zone-load for the spawn point, post-spawn mod work — subside so we don't sample latency\n" +
                    "while Valheim's own arrival traffic is queued ahead of our pings. 30s is the safe default;\n" +
                    "a server with heavy mod-driven sync (large worlds, many players) may benefit from 45-60.\n" +
                    "Worst-case modpacks on slow servers may need 75-90s to fully settle. Lower only if you\n" +
                    "know your fast-load mod handles arrival-burst traffic well.\n" +
                    "A converted multi-million-ZDO world with gigabytes of bundle assets is a class above that:\n" +
                    "measured main-thread stalls ran past T+340s there, and a probe fired inside one reads the\n" +
                    "stall instead of the link (a 54ms connection sampled 4384ms and was filed LOW). 150-240s\n" +
                    "suits that case. The probe also retries an aborted sample now, so an over-long delay costs\n" +
                    "only a later tier, never a wrong one.",
                    new AcceptableValueRange<float>(2f, 300f)));

            PlayerArrivalTimeoutSeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Player Arrival Timeout Seconds",
                180f,
                new ConfigDescription(
                    "Hard cap on how long to wait for the local player to actually finish spawning into the\n" +
                    "world before forcing the probe to start anyway. The probe gates on Game.m_playerInitialSpawn\n" +
                    "(the same event that fires the '$text_player_arrived' chat message) so the probe doesn't\n" +
                    "start while the player is still mid-load — that event normally fires within tens of\n" +
                    "seconds of connect, but heavy modpacks with large worlds + slow disks can take 90s+.\n" +
                    "This timeout is the safety valve: if the spawn fails entirely we eventually proceed\n" +
                    "anyway rather than leave the client stuck at a degraded default tier forever. 180s\n" +
                    "covers worst-case modpack loads on slow disks; raise only if you have repro evidence\n" +
                    "the timeout is firing on a successful spawn.",
                    new AcceptableValueRange<float>(30f, 600f)));

            ProbePingTimeoutSeconds = config.Bind(
                "07 - Auto-Tune - Probe",
                "Ping Timeout Seconds",
                5f,
                new ConfigDescription(
                    "How long to wait for a single ping echo before giving up. On timeout we default to LOW tier.",
                    new AcceptableValueRange<float>(1f, 15f)));

            ProbePingCount = config.Bind(
                "07 - Auto-Tune - Probe",
                "Ping Count",
                10,
                new ConfigDescription(
                    "Number of pings to send for the latency probe. The probe also fires one untracked\n" +
                    "warmup ping first (clears Steam TCP slow-start), and drops the single worst sample\n" +
                    "before computing stats so transient post-spawn queue contention can't tank the\n" +
                    "result on a healthy link. 10 is the sweet spot — enough samples for outlier\n" +
                    "trimming + IQR jitter to be stable; not so many that probe traffic becomes notable.",
                    new AcceptableValueRange<int>(5, 20)));

            ProbePingAbortMs = config.Bind(
                "07 - Auto-Tune - Probe",
                "Ping Abort Ms",
                2000,
                new ConfigDescription(
                    "If any single ping round-trip exceeds this, we hard-default to LOW tier and skip the\n" +
                    "bandwidth probe entirely — the client is already struggling, no point making them prove\n" +
                    "it twice.",
                    new AcceptableValueRange<int>(500, 5000)));

            ProbeBandwidthPayloadBytes = config.Bind(
                "07 - Auto-Tune - Probe",
                "Bandwidth Probe Bytes",
                128 * 1024,
                new ConfigDescription(
                    "Size of the bandwidth-test payload requested from server. Only runs if the latency probe\n" +
                    "puts the client at MED or HIGH tier — LOW-tier clients skip this entirely.\n" +
                    "\n" +
                    "Larger payloads measure throughput more honestly: a request-response with a small\n" +
                    "payload is dominated by RTT, not actual link capacity. With a 64ms RTT, a 32KB payload\n" +
                    "ceilings at ~500 KB/s no matter what the link can deliver. 128KB at the same RTT can\n" +
                    "report up to ~2 MB/s — the transfer time finally dominates the RTT. Default raised\n" +
                    "from 32KB → 128KB on 2026-05-11 because the small-payload probe was systematically\n" +
                    "under-reporting on healthy links and incorrectly tier-downgrading.\n" +
                    "Tradeoff: probe takes ~1.5s longer per session and consumes ~256KB of extra one-time\n" +
                    "bandwidth on the dedicated server.",
                    new AcceptableValueRange<int>(8 * 1024, 512 * 1024)));

            EnableRollingMonitor = config.Bind(
                "08 - Auto-Tune - Monitor",
                "Enable Rolling Monitor",
                true,
                "After the initial probe, keep periodically re-probing latency over the session and\n" +
                "refine the tier from a rolling average. Catches transient server overload (settling\n" +
                "to a higher tier once the initial-sync flood ends) and ISP/route weather changes.\n" +
                "Each re-probe is latency-only — no extra bandwidth probe — so cost is ~5KB per cycle.\n" +
                "CLIENT-SIDE only.");

            RollingMonitorIntervalMinutes = config.Bind(
                "08 - Auto-Tune - Monitor",
                "Re-Probe Interval Minutes",
                5f,
                new ConfigDescription(
                    "How often to re-probe latency after the initial probe. Smaller values catch transient\n" +
                    "issues faster but cost more probe traffic; larger values are quieter but slower to react.",
                    new AcceptableValueRange<float>(1f, 30f)));

            RollingMonitorWindow = config.Bind(
                "08 - Auto-Tune - Monitor",
                "Rolling Window Size",
                5,
                new ConfigDescription(
                    "Number of recent re-probe results held in the rolling buffer for averaging. Tier is\n" +
                    "the most-common tier in the buffer; promotion requires 2 consecutive observations,\n" +
                    "demotion requires 3 — biased to keep current tier rather than oscillate.",
                    new AcceptableValueRange<int>(3, 10)));
        }
    }
}
