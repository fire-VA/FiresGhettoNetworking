using BepInEx.Configuration;
using UnityEngine;

namespace FiresGhettoNetworkMod.AutoTune
{
    /// <summary>
    /// Writes Auto-Tune's decision into the config: Send Rate Min, Send Rate Max, ZDO Send Rate and
    /// Queue Size. With Auto-Tune on those four are Auto-Tune's, so the config file always shows what
    /// is running; with it off they keep the last values Auto-Tune chose.
    /// Plan: Docs/PLAN_UploadBudgetAndAutoTune.md
    /// </summary>
    internal static class AutoTuneApplier
    {
        private static bool s_writing;

        /// <summary>True while a batch is being written, so the rate handlers stand down.</summary>
        internal static bool Writing => s_writing;

        private const int MinShareOfMaxDivisor = 2;
        private const int UplinkFor100PercentBytes = 256 * 1024;
        private const int UplinkFor75PercentBytes  = 128 * 1024;
        private const int UplinkForVanillaQueueBytes = 256 * 1024;

        public static void ApplyClient()
        {
            if (AutoTuneConfig.EnableClientAutoTune == null || !AutoTuneConfig.EnableClientAutoTune.Value) return;
            var preset = TierPresets.For(AutoTuneState.ClientTier);

            int tierMax = VanillaFloor.ClampSendRate(preset.SteamSendRateMaxBytes, "SendRateMax", "ClientTier");
            int tierMin = VanillaFloor.ClampSendRate(preset.SteamSendRateMinBytes, "SendRateMin", "ClientTier");

            int uplink = AutoTuneState.ClientUplinkBytes;
            bool isLimit = AutoTuneState.ClientUplinkIsLimit;
            bool uplinkIsTheCeiling = isLimit && uplink < tierMax;

            int maxBytes = uplinkIsTheCeiling ? uplink : tierMax;
            int minBytes = System.Math.Min(tierMin, maxBytes / MinShareOfMaxDivisor);

            UpdateRateOptions rate = UpdateRateOptions._100;
            if (isLimit)
            {
                if (uplink < UplinkFor75PercentBytes)       rate = UpdateRateOptions._50;
                else if (uplink < UplinkFor100PercentBytes) rate = UpdateRateOptions._75;
            }

            QueueSizeOptions queue = isLimit && uplink < UplinkForVanillaQueueBytes
                ? QueueSizeOptions._vanilla
                : preset.QueueSize;

            Write(new Writes
            {
                Side = "client",
                Tier = AutoTuneState.ClientTier.ToString(),
                Reason = uplink < 0 ? "upload unknown, tier values"
                       : !isLimit ? $"upload at least {uplink / 1024} KB/s, not the limit"
                       : uplinkIsTheCeiling ? $"upload {uplink / 1024} KB/s is the limit, ceiling set to it"
                       : $"upload {uplink / 1024} KB/s, above the tier's ceiling",
                MaxBytes = maxBytes, MinBytes = minBytes, Rate = rate, Queue = queue,
            });
        }

        public static void ApplyServer()
        {
            if (AutoTuneConfig.EnableServerAutoTune == null || !AutoTuneConfig.EnableServerAutoTune.Value) return;
            var preset = TierPresets.For(AutoTuneState.ServerTier);

            int maxBytes = VanillaFloor.ClampSendRate(preset.SteamSendRateMaxBytes, "SendRateMax", "ServerTier");
            int lowBaselineMin = VanillaFloor.ClampSendRate(TierPresets.For(Tier.Low).SteamSendRateMinBytes, "SendRateMin", "ServerTier");
            int minBytes = System.Math.Min(lowBaselineMin, maxBytes / MinShareOfMaxDivisor);

            Write(new Writes
            {
                Side = "server",
                Tier = AutoTuneState.ServerTier.ToString(),
                Reason = "host CPU/RAM tier",
                MaxBytes = maxBytes,
                MinBytes = minBytes,
                Rate = VanillaFloor.ClampUpdateRate(preset.UpdateRate, "ServerTier"),
                Queue = preset.QueueSize,
            });
        }

        private struct Writes
        {
            public string Side, Tier, Reason;
            public int MaxBytes, MinBytes;
            public int MaxKb, MinKb;
            public UpdateRateOptions Rate;
            public QueueSizeOptions Queue;
        }

        private static void Write(Writes w)
        {
            if (s_writing) return;
            s_writing = true;
            bool changed = false;
            try
            {
                int maxKb = Mathf.Clamp(w.MaxBytes / 1024,
                                        FiresGhettoNetworkMod.SendRateKbLow * MinShareOfMaxDivisor,
                                        FiresGhettoNetworkMod.SendRateKbHigh);
                int minKb = Mathf.Clamp(System.Math.Min(w.MinBytes / 1024, maxKb / MinShareOfMaxDivisor),
                                        FiresGhettoNetworkMod.SendRateKbLow, maxKb / MinShareOfMaxDivisor);
                changed |= Set(FiresGhettoNetworkMod.ConfigSendRateMax, maxKb);
                changed |= Set(FiresGhettoNetworkMod.ConfigSendRateMin, minKb);
                w.MaxKb = maxKb; w.MinKb = minKb;
                changed |= Set(FiresGhettoNetworkMod.ConfigUpdateRate,  w.Rate);
                changed |= Set(FiresGhettoNetworkMod.ConfigQueueSize,   w.Queue);

                if (changed)
                    LoggerOptions.LogMessage($"[AutoTune] {w.Side} {w.Tier}: {w.Reason}. Config set to "
                        + $"Send Rate Max {w.MaxKb} KB/s, Min {w.MinKb} KB/s, ZDO Send Rate {w.Rate}, Queue Size {w.Queue}.");
            }
            finally { s_writing = false; }

            if (changed) NetworkingRatesGroup.ApplyTunedRatesLive();
        }

        private static bool Set<T>(ConfigEntry<T> entry, T value)
        {
            if (entry == null || Equals(entry.Value, value)) return false;
            entry.Value = value;
            return true;
        }
    }
}
