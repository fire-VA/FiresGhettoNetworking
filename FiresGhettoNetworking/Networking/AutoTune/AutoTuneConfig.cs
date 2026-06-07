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
                "Run a brief network/hardware probe on first login to a server and pick a perf tier (LOW/MED/HIGH).\n" +
                "Effective values shadow the BepInEx config — your manual settings are never overwritten.\n" +
                "Disable to use only the values set above. CLIENT-SIDE only.");

            EnableServerAutoTune = config.Bind(
                "06 - Auto-Tune",
                "Enable Server Auto-Tune",
                true,
                "When the mod runs on a dedicated server, score the host's CPU/RAM and APPLY a tier preset to\n" +
                "ZDO throttle / AI LOD / RPC AoI / queue size / extended zone radius / update rate / Steam buffers.\n" +
                "Default ON — the tier preset is conservative and the asymmetric send-rate pattern keeps slow\n" +
                "clients safe (Min stays at the LOW baseline regardless of tier). Disable if you've manually tuned\n" +
                "every server knob and want your values used verbatim. SERVER-SIDE only.");

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
                    "know your fast-load mod handles arrival-burst traffic well.",
                    new AcceptableValueRange<float>(2f, 90f)));

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
