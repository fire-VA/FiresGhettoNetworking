using System;

namespace FiresGhettoNetworkMod.AutoTune
{
    public enum Tier
    {
        Low,
        Medium,
        High
    }

    public static class VanillaFloor
    {
        public const int SendRateMinBytes    = 153600;
        public const int SendRateMaxBytes    = 153600;
        public const int SendBufferBytes     = 524288;
        public const int RecvBufferBytes     = 524288;
        public const UpdateRateOptions UpdateRate = UpdateRateOptions._100;

        public static int ClampSendRateMin(int value, string source)
            => ClampInt(value, SendRateMinBytes, source, nameof(SendRateMinBytes));

        public static int ClampSendRateMax(int value, string source)
            => ClampInt(value, SendRateMaxBytes, source, nameof(SendRateMaxBytes));

        public static int ClampSendBuffer(int value, string source)
            => ClampInt(value, SendBufferBytes, source, nameof(SendBufferBytes));

        public static int ClampRecvBuffer(int value, string source)
            => ClampInt(value, RecvBufferBytes, source, nameof(RecvBufferBytes));

        public static UpdateRateOptions ClampUpdateRate(UpdateRateOptions value, string source)
        {
            if (UpdateRateHz(value) < UpdateRateHz(UpdateRate))
            {
                LoggerOptions.LogWarning(
                    $"[VanillaFloor] {source} requested UpdateRate {value} ({UpdateRateHz(value)}Hz) " +
                    $"below vanilla {UpdateRate} ({UpdateRateHz(UpdateRate)}Hz) — clamped up. " +
                    "AutoTune must never tick below stock.");
                return UpdateRate;
            }
            return value;
        }

        private static int ClampInt(int value, int floor, string source, string knob)
        {
            if (value < floor)
            {
                LoggerOptions.LogWarning(
                    $"[VanillaFloor] {source} requested {knob}={value} below vanilla floor {floor} " +
                    "— clamped up. AutoTune must never drop a connection below stock.");
                return floor;
            }
            return value;
        }

        private static int UpdateRateHz(UpdateRateOptions opt)
        {
            switch (opt)
            {
                case UpdateRateOptions._50:  return 10;
                case UpdateRateOptions._75:  return 15;
                case UpdateRateOptions._150: return 30;
                default:                     return 20;
            }
        }
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
        public bool  EnablePrediction;
        public float SmoothingMaxInterval;
        public float SmoothingMinInterval;

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
        public static void ValidateVanillaFloors()
        {
            int violations = 0;
            foreach (Tier tier in new[] { Tier.Low, Tier.Medium, Tier.High })
            {
                TierPreset p = For(tier);
                violations += CheckFloor($"{tier}.SendRateMin", p.SteamSendRateMinBytes, VanillaFloor.SendRateMinBytes);
                violations += CheckFloor($"{tier}.SendRateMax", p.SteamSendRateMaxBytes, VanillaFloor.SendRateMaxBytes);
                violations += CheckFloor($"{tier}.SendBuffer",  p.SteamSendBufferBytes,  VanillaFloor.SendBufferBytes);
                violations += CheckFloor($"{tier}.RecvBuffer",  p.SteamRecvBufferBytes,  VanillaFloor.RecvBufferBytes);
                violations += CheckUpdateRate($"{tier}.UpdateRate", p.UpdateRate);
            }

            if (violations > 0)
                LoggerOptions.LogError(
                    $"[VanillaFloor] STARTUP SELF-CHECK FAILED: {violations} tier preset value(s) sit below vanilla. " +
                    "These are clamped at runtime so players are protected, but the presets above should be corrected. " +
                    "AutoTune must never drop a connection below stock.");
            else
                LoggerOptions.LogMessage("[VanillaFloor] Startup self-check passed: no tier preset drops below vanilla.");
        }

        private static int CheckFloor(string name, int value, int floor)
        {
            if (value >= floor) return 0;
            LoggerOptions.LogError($"[VanillaFloor] {name}={value} is BELOW vanilla floor {floor}.");
            return 1;
        }

        private static int CheckUpdateRate(string name, UpdateRateOptions value)
        {
            if (value != UpdateRateOptions._50 && value != UpdateRateOptions._75) return 0;
            LoggerOptions.LogError($"[VanillaFloor] {name}={value} ticks below vanilla {VanillaFloor.UpdateRate}.");
            return 1;
        }

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
                        EnablePrediction      = true,
                        SmoothingMaxInterval  = 0.15f,
                        SmoothingMinInterval  = 0.0f,
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
                        EnablePrediction      = false,
                        SmoothingMaxInterval  = 0.20f,
                        SmoothingMinInterval  = 0.0f,
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
                        // Low tier means "don't apply the enhanced ceilings," never
                        // "drop below stock." Max held at 1 MB so a burst (e.g. a
                        // ServerCharacters compressed-inventory push) is never walled
                        // below what vanilla would have negotiated; the 384 KB cap here
                        // was starving time-boxed transfers and causing 30s-timeout
                        // disconnects. Min stays at vanilla's floor.
                        SteamSendRateMinBytes = 150 * 1024,
                        SteamSendRateMaxBytes = 1024 * 1024,
                        SteamSendBufferBytes     = 1024 * 1024,
                        // Even at Low we hold the recv ceiling at 2 MB; less than that
                        // narrows from Steam's own 512 KB default and lets ClientLogRelay-
                        // sized chunks fail. The per-connection cost (2 MB × peer count)
                        // is trivial on any host that can run a Valheim server.
                        SteamRecvBufferBytes     = 2 * 1024 * 1024,
                        SteamRecvMaxMessageBytes = 4 * 1024 * 1024,
                        ZoneLoadBatchSize        = 1,
                        EnablePrediction      = false,
                        SmoothingMaxInterval  = 0.30f,
                        SmoothingMinInterval  = 0.0f,
                        // Tight 2 ms budget on a low-tier client; cap 50 keeps us
                        // friendly to 30-fps targets. Safety threshold lifted to
                        // 8000 — weak boxes are exactly where a teleport into a
                        // megabase will produce the longest backlog and we'd rather
                        // widen budget than let the queue bleed across many seconds.
                        InstantiationBudgetMs   = 2,
                        MaxInstancesPerFrame    = 50,
                        SafetyFallbackEnabled   = true,
                        SafetyFallbackThreshold = 8000,

                        UpdateRate            = UpdateRateOptions._100,
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
                return VanillaFloor.ClampSendRateMin(TierPresets.For(AutoTuneState.ServerTier).SteamSendRateMinBytes, "ServerTier");
            if (UseClientAutoTune())
                return VanillaFloor.ClampSendRateMin(TierPresets.For(AutoTuneState.ClientTier).SteamSendRateMinBytes, "ClientTier");
            return SendRateMinFromEnum(FiresGhettoNetworkMod.ConfigSendRateMin.Value);
        }

        public static int SteamSendRateMax()
        {
            if (IsDedicatedServerRuntime() && UseServerAutoTune())
                return VanillaFloor.ClampSendRateMax(TierPresets.For(AutoTuneState.ServerTier).SteamSendRateMaxBytes, "ServerTier");
            if (UseClientAutoTune())
                return VanillaFloor.ClampSendRateMax(TierPresets.For(AutoTuneState.ClientTier).SteamSendRateMaxBytes, "ClientTier");
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
                return VanillaFloor.ClampRecvBuffer(TierPresets.For(AutoTuneState.ServerTier).SteamRecvBufferBytes, "ServerTier");
            if (UseClientAutoTune())
                return VanillaFloor.ClampRecvBuffer(TierPresets.For(AutoTuneState.ClientTier).SteamRecvBufferBytes, "ClientTier");
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
                return VanillaFloor.ClampSendBuffer(TierPresets.For(AutoTuneState.ServerTier).SteamSendBufferBytes, "ServerTier");
            if (UseClientAutoTune())
                return VanillaFloor.ClampSendBuffer(TierPresets.For(AutoTuneState.ClientTier).SteamSendBufferBytes, "ClientTier");
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

        public static bool EnablePlayerPrediction()
        {
            if (UseClientAutoTune())
            {
                // Tier may say HIGH→prediction on, but only honor it if measured ping
                // is high enough that prediction actually helps. Below ~80ms, prediction
                // overshoots and feels worse than plain interpolation.
                bool tierAllows = TierPresets.For(AutoTuneState.ClientTier).EnablePrediction;
                bool latencyJustifies = AutoTuneState.ClientPingMedianMs >= 80;
                return tierAllows && latencyJustifies;
            }
            return PlayerPositionSyncPatches.ConfigEnablePlayerPrediction?.Value ?? false;
        }

        public static float SmoothingMaxInterval()
        {
            if (UseClientAutoTune())
                return TierPresets.For(AutoTuneState.ClientTier).SmoothingMaxInterval;
            return PlayerPositionSyncPatches.ConfigSmoothingMaxInterval?.Value ?? 0.20f;
        }

        public static float SmoothingMinInterval()
        {
            if (UseClientAutoTune())
                return TierPresets.For(AutoTuneState.ClientTier).SmoothingMinInterval;
            return PlayerPositionSyncPatches.ConfigSmoothingMinInterval?.Value ?? 0.0f;
        }

        // ---------------- Server knobs ----------------

        public static UpdateRateOptions UpdateRate()
        {
            if (UseServerAutoTune())
                return VanillaFloor.ClampUpdateRate(TierPresets.For(AutoTuneState.ServerTier).UpdateRate, "ServerTier");
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
