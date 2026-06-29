using System;
using System.Collections.Generic;

namespace FiresGhettoNetworkMod.AutoTune
{
    // Hard floor for every Steam send/recv rate and the update rate. AutoTune (or a hand-edited
    // config) may RAISE a value above vanilla; it may NEVER drop one below. Vanilla's send-rate floor
    // is 150 KB/s (the m_dataRate minimum behind the 150KB setting); the send/recv buffers floor at
    // Steam's 512 KB default; the update rate floors at 20 Hz (_100, "100% - vanilla" per
    // UpdateRateOptions). Every EffectiveConfig getter routes through these clamps so no tier and no
    // manual value can emit a sub-vanilla rate — the defect the Low tier shipped (_75 update rate),
    // which throttled a peer below stock and made "remove the mod and it's fixed" true by construction.
    public static class VanillaFloor
    {
        public const int SendRateBytes   = 150 * 1024;   // 153600 — vanilla send-rate floor (Min and Max)
        public const int SendBufferBytes = 512 * 1024;   // Steam's default outbound buffer
        public const int RecvBufferBytes = 512 * 1024;   // Steam's default inbound buffer
        public static readonly UpdateRateOptions UpdateRate = UpdateRateOptions._100; // 20 Hz = vanilla

        // Dedupe key = "source:knob" so a value clamped on every apply / reconnect / autotune reassert
        // is reported once, not spammed each frame the getter is read.
        private static readonly HashSet<string> _warned = new HashSet<string>();

        private static void WarnOnce(string knob, string source, object asked, object floored)
        {
            if (!_warned.Add(source + ":" + knob)) return;
            LoggerOptions.LogWarning($"AutoTune VanillaFloor: {source} {knob}={asked} is below vanilla — "
                + $"overriding to {floored}. AutoTune may raise rates above vanilla, never below.");
        }

        public static int ClampSendRate(int value, string knob, string source)
        {
            if (value >= SendRateBytes) return value;
            WarnOnce(knob, source, value, SendRateBytes);
            return SendRateBytes;
        }

        public static int ClampSendBuffer(int value, string source)
        {
            if (value >= SendBufferBytes) return value;
            WarnOnce("SendBuffer", source, value, SendBufferBytes);
            return SendBufferBytes;
        }

        public static int ClampRecvBuffer(int value, string source)
        {
            if (value >= RecvBufferBytes) return value;
            WarnOnce("RecvBuffer", source, value, RecvBufferBytes);
            return RecvBufferBytes;
        }

        public static UpdateRateOptions ClampUpdateRate(UpdateRateOptions value, string source)
        {
            if (Percent(value) >= Percent(UpdateRate)) return value;
            WarnOnce("UpdateRate", source, value, UpdateRate);
            return UpdateRate;
        }

        // UpdateRateOptions is declared _150,_100,_75,_50 — its ordinal does NOT order by Hz, so map
        // each to its documented percentage to compare against the vanilla 100% floor.
        public static int Percent(UpdateRateOptions o)
        {
            switch (o)
            {
                case UpdateRateOptions._150: return 150;
                case UpdateRateOptions._100: return 100;
                case UpdateRateOptions._75:  return 75;
                case UpdateRateOptions._50:  return 50;
                default:                     return 100;
            }
        }
    }

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
                        // Steam's send-rate adapter has a sticky-down quirk: any peer that backs off
                        // toward Min tends to stay there. So Min is a long-term floor, not just a
                        // safety net — keep it well above unplayable. Max is "permission to burst" —
                        // Steam still ramps adaptively, this just removes the artificial ceiling.
                        SteamSendRateMinBytes = 1024 * 1024,
                        SteamSendRateMaxBytes = 32768 * 1024,    // 32 MB/s burst ceiling (was 8). Benchmark: link sustains ~92 MB/s; beats the 14-40 MB/s rivals on AutoTune alone. HyperBoost stays the unlocked max.
                        SteamSendBufferBytes     = 32 * 1024 * 1024,  // 32MB: headroom for the adaptive controller ramping a peer toward ~90 MB/s without k_EResultLimitExceeded
                        // Recv buffer must exceed Steam's 512 KB default AND any large reliable chunk
                        // a peer might send (ClientLogRelay pushes ~400 KB; config syncs burst higher).
                        // Per-message cap is a wire-format ceiling, stays flat across tiers.
                        SteamRecvBufferBytes     = 32 * 1024 * 1024,
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
                        QueueSize             = QueueSizeOptions._80KB,
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
                        SteamSendRateMinBytes = 768  * 1024,
                        SteamSendRateMaxBytes = 16384 * 1024,    // 16 MB/s burst ceiling (was 4).
                        SteamSendBufferBytes     = 8 * 1024 * 1024,
                        SteamRecvBufferBytes     = 16 * 1024 * 1024,
                        SteamRecvMaxMessageBytes = 4 * 1024 * 1024,
                        ZoneLoadBatchSize        = 2,
                        InstantiationBudgetMs   = 3,
                        MaxInstancesPerFrame    = 100,
                        SafetyFallbackEnabled   = true,
                        SafetyFallbackThreshold = 5000,

                        UpdateRate            = UpdateRateOptions._100,
                        QueueSize             = QueueSizeOptions._48KB,
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
                        // Low Min is still a real Min — Steam's sticky-down behaviour means any peer that
                        // backs off here stays here. 512 KB/s is ~3.4x vanilla floor: safe for slow links,
                        // but not "modded server falls over" territory.
                        SteamSendRateMinBytes = 512  * 1024,
                        SteamSendRateMaxBytes = 2048 * 1024,
                        SteamSendBufferBytes     = 2 * 1024 * 1024,
                        SteamRecvBufferBytes     = 8 * 1024 * 1024,
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

        // Startup self-check: audit every tier preset against the vanilla floor and LogError on any
        // sub-vanilla value, so a future preset edit is caught loudly at load instead of as a player
        // complaint in the field. The runtime clamps still correct it; this just surfaces it early.
        public static void ValidateVanillaFloors()
        {
            bool ok = true;
            foreach (Tier tier in new[] { Tier.Low, Tier.Medium, Tier.High })
            {
                var p = For(tier);
                ok &= CheckFloor(tier, "SendRateMin", p.SteamSendRateMinBytes, VanillaFloor.SendRateBytes);
                ok &= CheckFloor(tier, "SendRateMax", p.SteamSendRateMaxBytes, VanillaFloor.SendRateBytes);
                ok &= CheckFloor(tier, "SendBuffer",  p.SteamSendBufferBytes,  VanillaFloor.SendBufferBytes);
                ok &= CheckFloor(tier, "RecvBuffer",  p.SteamRecvBufferBytes,  VanillaFloor.RecvBufferBytes);
                if (VanillaFloor.Percent(p.UpdateRate) < VanillaFloor.Percent(VanillaFloor.UpdateRate))
                {
                    ok = false;
                    LoggerOptions.LogError($"AutoTune VanillaFloor SELF-CHECK FAILED: {tier} tier UpdateRate="
                        + $"{p.UpdateRate} is below vanilla ({VanillaFloor.UpdateRate}). Fix the preset.");
                }
            }
            if (ok)
                LoggerOptions.LogMessage("AutoTune VanillaFloor self-check passed: every tier is at or above vanilla.");
        }

        private static bool CheckFloor(Tier tier, string knob, int value, int floor)
        {
            if (value >= floor) return true;
            LoggerOptions.LogError($"AutoTune VanillaFloor SELF-CHECK FAILED: {tier} tier {knob}={value} "
                + $"is below the vanilla floor {floor}. Fix the preset.");
            return false;
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
        // ---------------- HYPERBOOST ----------------
        // A single max-throughput override that wins over BOTH auto-tune and manual config, pushed to the
        // limits the Steam API itself allows. These configs are Int32 BYTES, so the send rate maxes at
        // int.MaxValue (~2 GB/s) and the buffers sit just under it — there is no higher value to set; a
        // bigger number simply overflows the field. The High tier caps SendRateMax at 8 MB/s, which is the
        // everyday ceiling HyperBoost removes; fgn_socketramp lifts a connection to these exact values to
        // find the real cliff. The send/recv buffers are CEILINGS ("up to", not preallocated), so the cost
        // is headroom a genuine flood can fill — hence opt-in, default OFF.
        public const int HyperBoostSendRateMaxBytes    = int.MaxValue;       // ~2 GB/s — the Int32 API ceiling
        public const int HyperBoostSendRateMinBytes    =  640 * 1024 * 1024; // 640 MB/s floor (defeats Steam sticky-down)
        public const int HyperBoostSendBufferBytes     = 2047 * 1024 * 1024; // ~2 GB outbound staging (just under Int32 max)
        public const int HyperBoostRecvBufferBytes     = 1024 * 1024 * 1024; // 1 GB inbound
        public const int HyperBoostRecvMaxMessageBytes =   64 * 1024 * 1024; // 64 MB per-message ceiling

        public static bool HyperBoost()
            => FiresGhettoNetworkMod.ConfigHyperBoost != null && FiresGhettoNetworkMod.ConfigHyperBoost.Value;

        // ---------------- Client knobs ----------------

        // Steam send rates apply per-process: on the client this scales the client's
        // outbound to the server, on the dedicated server it scales the server's
        // outbound to all peers. So we pick which tier drives them based on side:
        // server tier wins when running on a dedicated server with auto-tune enabled,
        // otherwise client tier (or manual config).
        public static int SteamSendRateMin()
        {
            if (HyperBoost()) return HyperBoostSendRateMinBytes;
            if (IsDedicatedServerRuntime() && UseServerAutoTune())
                return VanillaFloor.ClampSendRate(TierPresets.For(AutoTuneState.ServerTier).SteamSendRateMinBytes, "SendRateMin", "ServerTier");
            if (UseClientAutoTune())
                return VanillaFloor.ClampSendRate(TierPresets.For(AutoTuneState.ClientTier).SteamSendRateMinBytes, "SendRateMin", "ClientTier");
            return VanillaFloor.ClampSendRate(SendRateMinFromEnum(FiresGhettoNetworkMod.ConfigSendRateMin.Value), "SendRateMin", "ManualConfig");
        }

        public static int SteamSendRateMax()
        {
            if (HyperBoost()) return HyperBoostSendRateMaxBytes;
            if (IsDedicatedServerRuntime() && UseServerAutoTune())
                return VanillaFloor.ClampSendRate(TierPresets.For(AutoTuneState.ServerTier).SteamSendRateMaxBytes, "SendRateMax", "ServerTier");
            if (UseClientAutoTune())
                return VanillaFloor.ClampSendRate(TierPresets.For(AutoTuneState.ClientTier).SteamSendRateMaxBytes, "SendRateMax", "ClientTier");
            return VanillaFloor.ClampSendRate(SendRateMaxFromEnum(FiresGhettoNetworkMod.ConfigSendRateMax.Value), "SendRateMax", "ManualConfig");
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
            if (HyperBoost()) return HyperBoostRecvBufferBytes;
            if (IsDedicatedServerRuntime() && UseServerAutoTune())
                return VanillaFloor.ClampRecvBuffer(TierPresets.For(AutoTuneState.ServerTier).SteamRecvBufferBytes, "ServerTier");
            if (UseClientAutoTune())
                return VanillaFloor.ClampRecvBuffer(TierPresets.For(AutoTuneState.ClientTier).SteamRecvBufferBytes, "ClientTier");
            // Fallback bumped to 2 MB (was 256 KB). Even without auto-tune, no
            // sensible Valheim deployment wants a recv buffer below Steam's own
            // 512 KB default — and 2 MB cleanly absorbs ClientLogRelay-sized chunks.
            return VanillaFloor.ClampRecvBuffer(Math.Max(2 * 1024 * 1024, FiresGhettoNetworkMod.ConfigZPackageReceiveBufferSize?.Value ?? 2 * 1024 * 1024), "ManualConfig");
        }

        // Per-message ceiling, exposed by the FiresSteamworksPatcher recv-buffer
        // enum family. Steam SDK default is 524288 (512 KB); we bake a flat 4 MB
        // across all tiers because per-message size is wire-format, not a
        // throughput knob. Falls back to the same value when no tier is active.
        public static int SteamRecvMaxMessageBytes()
        {
            if (HyperBoost()) return HyperBoostRecvMaxMessageBytes;
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
            if (HyperBoost()) return HyperBoostSendBufferBytes;
            if (IsDedicatedServerRuntime() && UseServerAutoTune())
                return VanillaFloor.ClampSendBuffer(TierPresets.For(AutoTuneState.ServerTier).SteamSendBufferBytes, "ServerTier");
            if (UseClientAutoTune())
                return VanillaFloor.ClampSendBuffer(TierPresets.For(AutoTuneState.ClientTier).SteamSendBufferBytes, "ClientTier");
            // Fallback: generous default that won't break anything
            return VanillaFloor.ClampSendBuffer(1 * 1024 * 1024, "ManualConfig");
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
                return VanillaFloor.ClampUpdateRate(TierPresets.For(AutoTuneState.ServerTier).UpdateRate, "ServerTier");
            return VanillaFloor.ClampUpdateRate(FiresGhettoNetworkMod.ConfigUpdateRate.Value, "ManualConfig");
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
            // Defer to Render Limits when present — it owns zone-load sizing, so FGN adds no layers.
            if (RenderLimitsCompat.Present) return 0;
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
                case SendRateMaxOptions._8192KB: return 8192 * 1024;
                case SendRateMaxOptions._4096KB: return 4096 * 1024;
                case SendRateMaxOptions._2048KB: return 2048 * 1024;
                case SendRateMaxOptions._1024KB: return 1024 * 1024;
                case SendRateMaxOptions._768KB:  return 768  * 1024;
                case SendRateMaxOptions._512KB:  return 512  * 1024;
                case SendRateMaxOptions._256KB:  return 256  * 1024;
                default:                         return 150  * 1024;
            }
        }
    }
}
