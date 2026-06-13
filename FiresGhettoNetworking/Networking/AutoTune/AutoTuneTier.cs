using System;

namespace FiresGhettoNetworkMod.AutoTune
{
    public enum Tier
    {
        Low,
        Medium,
        High
    }

    public struct TierPreset
    {
        // ---- Client-side knobs ----
        public int   SteamSendRateMinBytes;
        public int   SteamSendRateMaxBytes;
        public int   SteamSendBufferBytes;
        public int   SteamRecvBufferBytes;
        // Per-message ceiling on the receive side. Default in Steam SDK is
        // 524288 (512 KB); we expose it explicitly so the FiresSteamworksPatcher
        // recv-buffer family can raise the cap together with the buffer itself.
        // Without this, a recv buffer >512 KB still has individual messages
        // rejected at 512 KB ("Reliable message size too large").
        public int   SteamRecvMaxMessageBytes;
        public int   ZoneLoadBatchSize;

        // Time-sliced instantiation (Workstream A). Replaces the dumb cap-bump
        // transpiler with a per-frame ms budget — instantiate as many ZDOs as
        // fit in InstantiationBudgetMs, capped by MaxInstancesPerFrame as a
        // safety belt. SafetyFallbackEnabled widens the budget when the
        // pending list grows past SafetyFallbackThreshold (e.g. teleport into
        // a megabase) so we don't fall infinitely behind.
        public int   InstantiationBudgetMs;
        public int   MaxInstancesPerFrame;
        public bool  SafetyFallbackEnabled;
        public int   SafetyFallbackThreshold;

        // ---- Server-side knobs ----
        public UpdateRateOptions UpdateRate;
        public QueueSizeOptions  QueueSize;
        public float             ZDOThrottleDistance;
        public float             AILODNearDistance;
        public float             AILODFarDistance;
        public float             AILODThrottleFactor;
        public float             RpcAoIRadius;
        public int               ExtendedZoneRadius;
    }

    public static class TierPresets
    {
        public static TierPreset For(Tier tier)
        {
            switch (tier)
            {
                case Tier.High:
                    return new TierPreset
                    {
                        // Min stays at the safe baseline regardless of tier so Steam can
                        // always back off for a struggling peer. Only Max scales with tier
                        // — that's "permission to burst", not "must push this fast".
                        SteamSendRateMinBytes = 150  * 1024,
                        SteamSendRateMaxBytes = 1024 * 1024,
                        SteamSendBufferBytes     = 2 * 1024 * 1024, // 2MB — big headroom to absorb initial-sync flood
                        // 8 MB recv buffer / 4 MB per-message ceiling. Recv buffer needs to
                        // exceed both Steam's 512 KB default and any large reliable chunk a
                        // peer might send (ClientLogRelay pushes ~400 KB chunks). Per-message
                        // cap stays flat across tiers because it's a wire-format ceiling, not
                        // a steady-state throughput hint.
                        SteamRecvBufferBytes     = 8 * 1024 * 1024,
                        SteamRecvMaxMessageBytes = 4 * 1024 * 1024,
                        ZoneLoadBatchSize        = 4,
                        // 5 ms steady-state budget on a high-tier client. Capable
                        // boxes can afford a wider slice each frame and still hold
                        // 60 fps. Hard cap at 200 instances/frame keeps a runaway
                        // list from monopolising one tick even if the budget allows.
                        InstantiationBudgetMs   = 5,
                        MaxInstancesPerFrame    = 200,
                        SafetyFallbackEnabled   = true,
                        SafetyFallbackThreshold = 5000,

                        UpdateRate            = UpdateRateOptions._150,
                        QueueSize             = QueueSizeOptions._48KB,
                        ZDOThrottleDistance   = 700f,
                        AILODNearDistance     = 150f,
                        AILODFarDistance      = 500f,
                        AILODThrottleFactor   = 0.5f,
                        RpcAoIRadius          = 384f,
                        ExtendedZoneRadius    = 2,
                    };

                case Tier.Medium:
                    return new TierPreset
                    {
                        // Same safe baseline Min as HIGH — see HIGH-tier note above.
                        SteamSendRateMinBytes = 150 * 1024,
                        SteamSendRateMaxBytes = 512 * 1024,
                        SteamSendBufferBytes     = 1 * 1024 * 1024, // 1MB
                        SteamRecvBufferBytes     = 4 * 1024 * 1024,
                        SteamRecvMaxMessageBytes = 4 * 1024 * 1024,
                        ZoneLoadBatchSize        = 2,
                        InstantiationBudgetMs   = 3,
                        MaxInstancesPerFrame    = 100,
                        SafetyFallbackEnabled   = true,
                        SafetyFallbackThreshold = 5000,

                        UpdateRate            = UpdateRateOptions._100,
                        QueueSize             = QueueSizeOptions._32KB,
                        ZDOThrottleDistance   = 500f,
                        AILODNearDistance     = 100f,
                        AILODFarDistance      = 300f,
                        AILODThrottleFactor   = 0.5f,
                        RpcAoIRadius          = 256f,
                        ExtendedZoneRadius    = 1,
                    };

                case Tier.Low:
                default:
                    return new TierPreset
                    {
                        // Baseline Min for all tiers; bumped Max from 256→384 so even
                        // weak boxes aren't artificially throttled below MED's floor.
                        SteamSendRateMinBytes = 150 * 1024,
                        SteamSendRateMaxBytes = 384 * 1024,
                        SteamSendBufferBytes     = 512 * 1024, // 512KB — still bigger than vanilla, gentle on weak boxes
                        // Even at Low we hold the recv ceiling at 2 MB; less than that
                        // narrows from Steam's own 512 KB default and lets ClientLogRelay-
                        // sized chunks fail. The per-connection cost (2 MB × peer count)
                        // is trivial on any host that can run a Valheim server.
                        SteamRecvBufferBytes     = 2 * 1024 * 1024,
                        SteamRecvMaxMessageBytes = 4 * 1024 * 1024,
                        ZoneLoadBatchSize        = 1,
                        // Tight 2 ms budget on a low-tier client; cap 50 keeps us
                        // friendly to 30-fps targets. Safety threshold lifted to
                        // 8000 — weak boxes are exactly where a teleport into a
                        // megabase will produce the longest backlog and we'd rather
                        // widen budget than let the queue bleed across many seconds.
                        InstantiationBudgetMs   = 2,
                        MaxInstancesPerFrame    = 50,
                        SafetyFallbackEnabled   = true,
                        SafetyFallbackThreshold = 8000,

                        UpdateRate            = UpdateRateOptions._75,
                        QueueSize             = QueueSizeOptions._vanilla,
                        ZDOThrottleDistance   = 350f,
                        AILODNearDistance     = 80f,
                        AILODFarDistance      = 200f,
                        AILODThrottleFactor   = 0.4f,
                        RpcAoIRadius          = 192f,
                        ExtendedZoneRadius    = 0,
                    };
            }
        }
    }

    /// <summary>
    /// Holds the live auto-tune result for the current side.
    /// Client tier is set by AutoTuneProbe; server tier by ServerAutoTune.
    /// HasResult guards every read so partial state never leaks through.
    /// </summary>
    public static class AutoTuneState
    {
        public static bool HasClientResult { get; private set; }
        public static Tier ClientTier      { get; private set; } = Tier.Medium;
        public static int  ClientPingMedianMs { get; private set; }

        public static bool HasServerResult { get; private set; }
        public static Tier ServerTier      { get; private set; } = Tier.Medium;

        public static void SetClient(Tier tier, int pingMedianMs)
        {
            ClientTier = tier;
            ClientPingMedianMs = pingMedianMs;
            HasClientResult = true;
        }

        public static void ClearClient()
        {
            HasClientResult = false;
            ClientTier = Tier.Medium;
            ClientPingMedianMs = 0;
        }

        public static void SetServer(Tier tier)
        {
            ServerTier = tier;
            HasServerResult = true;
        }
    }

    /// <summary>
    /// Single point of truth for "what value do I use right now?".
    /// Every consumer (NetworkRatesGroup, PlayerPositionSyncPatches, ZDOThrottling, etc.)
    /// reads through here. When auto-tune is off OR a result hasn't landed yet, we
    /// fall back to the user's BepInEx-bound config so manual configuration always wins
    /// when explicitly opted into. We never overwrite the bound config values themselves.
    /// </summary>
    public static class EffectiveConfig
    {
        // ---------------- Client knobs ----------------

        // Steam send rates apply per-process: on the client this scales the client's
        // outbound to the server, on the dedicated server it scales the server's
        // outbound to all peers. So we pick which tier drives them based on side:
        // server tier wins when running on a dedicated server with auto-tune enabled,
        // otherwise client tier (or manual config).
        public static int SteamSendRateMin()
        {
            if (IsDedicatedServerRuntime() && UseServerAutoTune())
                return TierPresets.For(AutoTuneState.ServerTier).SteamSendRateMinBytes;
            if (UseClientAutoTune())
                return TierPresets.For(AutoTuneState.ClientTier).SteamSendRateMinBytes;
            return SendRateMinFromEnum(FiresGhettoNetworkMod.ConfigSendRateMin.Value);
        }

        public static int SteamSendRateMax()
        {
            if (IsDedicatedServerRuntime() && UseServerAutoTune())
                return TierPresets.For(AutoTuneState.ServerTier).SteamSendRateMaxBytes;
            if (UseClientAutoTune())
                return TierPresets.For(AutoTuneState.ClientTier).SteamSendRateMaxBytes;
            return SendRateMaxFromEnum(FiresGhettoNetworkMod.ConfigSendRateMax.Value);
        }

        private static bool IsDedicatedServerRuntime()
        {
            // Cached early-detection covers the case where ZNet hasn't started yet.
            if (ServerClientUtils.IsDedicatedServerDetected) return true;
            try { return ZNet.instance != null && ZNet.instance.IsDedicated(); }
            catch { return false; }
        }

        // Same side-aware shape as SteamSendBufferBytes — the recv buffer on a
        // dedicated server is the lever that decides how big an inbound reliable
        // chunk a client may push (ClientLogRelay etc). Reading only ClientTier
        // here would fall back to a much smaller default on the server side and
        // silently downgrade Steam's own default; that's exactly the bug that
        // caused the "Reliable message size too large" disconnect.
        public static int SteamRecvBufferBytes()
        {
            if (IsDedicatedServerRuntime() && UseServerAutoTune())
                return TierPresets.For(AutoTuneState.ServerTier).SteamRecvBufferBytes;
            if (UseClientAutoTune())
                return TierPresets.For(AutoTuneState.ClientTier).SteamRecvBufferBytes;
            // Fallback bumped to 2 MB (was 256 KB). Even without auto-tune, no
            // sensible Valheim deployment wants a recv buffer below Steam's own
            // 512 KB default — and 2 MB cleanly absorbs ClientLogRelay-sized chunks.
            return Math.Max(2 * 1024 * 1024, FiresGhettoNetworkMod.ConfigZPackageReceiveBufferSize?.Value ?? 2 * 1024 * 1024);
        }

        // Per-message ceiling, exposed by the FiresSteamworksPatcher recv-buffer
        // enum family. Steam SDK default is 524288 (512 KB); we bake a flat 4 MB
        // across all tiers because per-message size is wire-format, not a
        // throughput knob. Falls back to the same value when no tier is active.
        public static int SteamRecvMaxMessageBytes()
        {
            if (IsDedicatedServerRuntime() && UseServerAutoTune())
                return TierPresets.For(AutoTuneState.ServerTier).SteamRecvMaxMessageBytes;
            if (UseClientAutoTune())
                return TierPresets.For(AutoTuneState.ClientTier).SteamRecvMaxMessageBytes;
            return 4 * 1024 * 1024;
        }

        // Steam SendBufferSize is the per-connection outbound buffer inside Steam's
        // networking layer — bigger means the server can stage more outbound bytes
        // before SendMessageToConnection returns k_EResultLimitExceeded. The flood
        // during a client's initial-sync is exactly when this matters: server is
        // pushing 50K+ ZDOs in a burst and the connection-level buffer fills before
        // the client can drain it. Same side-aware logic as send rates.
        public static int SteamSendBufferBytes()
        {
            if (IsDedicatedServerRuntime() && UseServerAutoTune())
                return TierPresets.For(AutoTuneState.ServerTier).SteamSendBufferBytes;
            if (UseClientAutoTune())
                return TierPresets.For(AutoTuneState.ClientTier).SteamSendBufferBytes;
            // Fallback: generous default that won't break anything
            return 1 * 1024 * 1024;
        }

        public static int ZoneLoadBatchSize()
        {
            if (UseClientAutoTune())
                return TierPresets.For(AutoTuneState.ClientTier).ZoneLoadBatchSize;
            return Math.Max(1, FiresGhettoNetworkMod.ConfigZoneLoadBatchSize?.Value ?? 2);
        }

        // Time-sliced instantiation accessors. Auto-tune wins when active; otherwise
        // user-bound config wins; final fallback is the Medium-tier default so a
        // freshly installed mod with auto-tune disabled doesn't fall to nothing.
        public static int InstantiationBudgetMs()
        {
            if (UseClientAutoTune())
                return TierPresets.For(AutoTuneState.ClientTier).InstantiationBudgetMs;
            int v = FiresGhettoNetworkMod.ConfigInstantiationBudgetMs?.Value ?? 3;
            return Math.Max(1, v);
        }

        public static int MaxInstancesPerFrame()
        {
            if (UseClientAutoTune())
                return TierPresets.For(AutoTuneState.ClientTier).MaxInstancesPerFrame;
            int v = FiresGhettoNetworkMod.ConfigMaxInstancesPerFrame?.Value ?? 100;
            return Math.Max(10, v);
        }

        public static bool SafetyFallbackEnabled()
        {
            if (UseClientAutoTune())
                return TierPresets.For(AutoTuneState.ClientTier).SafetyFallbackEnabled;
            return FiresGhettoNetworkMod.ConfigSafetyFallbackEnabled?.Value ?? true;
        }

        public static int SafetyFallbackThreshold()
        {
            if (UseClientAutoTune())
                return TierPresets.For(AutoTuneState.ClientTier).SafetyFallbackThreshold;
            int v = FiresGhettoNetworkMod.ConfigSafetyFallbackThreshold?.Value ?? 5000;
            return Math.Max(100, v);
        }

        public static bool TimeSliceInstantiationEnabled()
        {
            // Master gate is purely user-config; no tier override. Tier values
            // only kick in WHEN this is enabled.
            return FiresGhettoNetworkMod.ConfigEnableTimeSliceInstantiation?.Value ?? true;
        }

        // Visual-rendering preferences (how OTHER players look on your screen) are
        // pure client taste — they never affect PvE outcomes, and prediction in
        // particular was never dialed in (it overshoots/rubber-bands). AutoTune does
        // NOT touch them: they read straight from the bound config, default OFF, and
        // are the player's choice to enable. (Interpolation has always been a direct
        // read; prediction + smoothing now match it.)
        public static bool EnablePlayerPrediction()
        {
            return PlayerPositionSyncPatches.ConfigEnablePlayerPrediction?.Value ?? false;
        }

        public static float SmoothingMaxInterval()
        {
            return PlayerPositionSyncPatches.ConfigSmoothingMaxInterval?.Value ?? 0.20f;
        }

        public static float SmoothingMinInterval()
        {
            return PlayerPositionSyncPatches.ConfigSmoothingMinInterval?.Value ?? 0.0f;
        }

        // ---------------- Server knobs ----------------

        public static UpdateRateOptions UpdateRate()
        {
            if (UseServerAutoTune())
                return TierPresets.For(AutoTuneState.ServerTier).UpdateRate;
            return FiresGhettoNetworkMod.ConfigUpdateRate.Value;
        }

        public static QueueSizeOptions QueueSize()
        {
            if (UseServerAutoTune())
                return TierPresets.For(AutoTuneState.ServerTier).QueueSize;
            return FiresGhettoNetworkMod.ConfigQueueSize.Value;
        }

        public static float ZDOThrottleDistance()
        {
            if (UseServerAutoTune())
                return TierPresets.For(AutoTuneState.ServerTier).ZDOThrottleDistance;
            return FiresGhettoNetworkMod.ConfigZDOThrottleDistance.Value;
        }

        public static float AILODNearDistance()
        {
            if (UseServerAutoTune())
                return TierPresets.For(AutoTuneState.ServerTier).AILODNearDistance;
            return FiresGhettoNetworkMod.ConfigAILODNearDistance.Value;
        }

        public static float AILODFarDistance()
        {
            if (UseServerAutoTune())
                return TierPresets.For(AutoTuneState.ServerTier).AILODFarDistance;
            return FiresGhettoNetworkMod.ConfigAILODFarDistance.Value;
        }

        public static float AILODThrottleFactor()
        {
            if (UseServerAutoTune())
                return TierPresets.For(AutoTuneState.ServerTier).AILODThrottleFactor;
            return FiresGhettoNetworkMod.ConfigAILODThrottleFactor.Value;
        }

        public static float RpcAoIRadius()
        {
            if (UseServerAutoTune())
                return TierPresets.For(AutoTuneState.ServerTier).RpcAoIRadius;
            return FiresGhettoNetworkMod.ConfigRpcAoIRadius.Value;
        }

        public static int ExtendedZoneRadius()
        {
            if (UseServerAutoTune())
                return TierPresets.For(AutoTuneState.ServerTier).ExtendedZoneRadius;
            return FiresGhettoNetworkMod.ConfigExtendedZoneRadius.Value;
        }

        // ---------------- Gates ----------------

        private static bool UseClientAutoTune()
        {
            return AutoTuneConfig.EnableClientAutoTune != null
                && AutoTuneConfig.EnableClientAutoTune.Value
                && AutoTuneState.HasClientResult;
        }

        private static bool UseServerAutoTune()
        {
            return AutoTuneConfig.EnableServerAutoTune != null
                && AutoTuneConfig.EnableServerAutoTune.Value
                && AutoTuneState.HasServerResult;
        }

        // ---------------- Enum→bytes helpers ----------------
        // (Duplicates NetworkRatesGroup.GetSendRateValue intentionally — keeps this
        // module self-contained so callers don't need a runtime dependency on the
        // patch class. The two helpers must stay in sync; if a new option is added
        // there, mirror it here.)

        private static int SendRateMinFromEnum(SendRateMinOptions opt)
        {
            switch (opt)
            {
                case SendRateMinOptions._1024KB: return 1024 * 1024;
                case SendRateMinOptions._768KB:  return 768  * 1024;
                case SendRateMinOptions._512KB:  return 512  * 1024;
                case SendRateMinOptions._256KB:  return 256  * 1024;
                default:                         return 150  * 1024;
            }
        }

        private static int SendRateMaxFromEnum(SendRateMaxOptions opt)
        {
            switch (opt)
            {
                case SendRateMaxOptions._1024KB: return 1024 * 1024;
                case SendRateMaxOptions._768KB:  return 768  * 1024;
                case SendRateMaxOptions._512KB:  return 512  * 1024;
                case SendRateMaxOptions._256KB:  return 256  * 1024;
                default:                         return 150  * 1024;
            }
        }
    }
}
