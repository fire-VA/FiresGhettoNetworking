using BepInEx.Configuration;

namespace FiresGhettoNetworkMod.AutoTune
{
    /// <summary>
    /// Makes Auto-Tune WRITE the network config instead of silently bypassing it.
    ///
    /// THE CONTRACT: with Auto-Tune on, these settings are Auto-Tune's. It measures, decides, and
    /// writes the values into the config — so the config file always shows what is actually
    /// running. Turn Auto-Tune off and they are yours again, starting from the last values it chose.
    ///
    /// WHY THIS REPLACES THE OLD DESIGN. Auto-Tune used to leave the config untouched and have
    /// EffectiveConfig return a tier preset over the top of it. So a player with Auto-Tune on could
    /// read "Send Rate Max = 512 KB" in their file while the HIGH tier ran 32 MB/s — and the enum
    /// could not even express 32 MB/s. The config and the running value disagreed by design.
    ///
    /// Everything that activates a tier goes through AutoTuneState.SetClient / SetServer — the
    /// probe, a cache restore, the rolling monitor's consensus, the LOW fallback — so hooking the
    /// write there covers every path with no chance of one being missed.
    ///
    /// Knobs written: Send Rate Min, Send Rate Max, ZDO Send Rate, Queue Size. These are the ones
    /// with a player-visible config entry that Auto-Tune decides. Knobs with no config entry have
    /// nothing to disagree with and are left on their existing path.
    /// </summary>
    internal static class AutoTuneApplier
    {
        // Suppresses re-entry: writing a ConfigEntry fires SettingChanged, whose handlers re-apply
        // rates. That must not loop back into another write. NetworkingRatesGroup's handlers also
        // read this and stand down during a batch, so the batch is applied ONCE at the end.
        private static bool s_writing;
        internal static bool Writing => s_writing;

        // ── uplink thresholds ────────────────────────────────────────────────────────
        // Stay under the measured line rather than on it. 0.9, not less: the value is then SNAPPED
        // DOWN to a config option, which adds its own margin, so a smaller fraction applies the
        // safety twice. Measured across 40-1300 KB/s lines with the ~25%-spaced options, this
        // lands a player on 61-90% of their line (median 80%) and never over it. At 0.8 it was
        // 56-80%, throwing away capacity the line could carry. Adaptive Upload handles the dips.
        private const float UplinkBudgetFraction = 0.9f;

        // Client ZDO Send Rate by measured uplink. A client's own updates are small, so only a
        // genuinely thin line needs fewer of them — but on that line it is the change that stops
        // the player rubber-banding for everyone else. Better Networking has shipped the same
        // 75% / 50% steps for years for exactly this.
        private const int UplinkFor100PercentBytes = 256 * 1024;
        private const int UplinkFor75PercentBytes  = 128 * 1024;

        // Below this a smaller in-flight window keeps less queued ahead of a slow line.
        private const int UplinkForVanillaQueueBytes = 256 * 1024;

        public static void ApplyClient()
        {
            if (AutoTuneConfig.EnableClientAutoTune == null || !AutoTuneConfig.EnableClientAutoTune.Value) return;
            var preset = TierPresets.For(AutoTuneState.ClientTier);

            // Tier presets are chosen from ping, hardware and downlink — guesses about the uplink —
            // so they are floored at vanilla, exactly as before.
            int tierMax = VanillaFloor.ClampSendRate(preset.SteamSendRateMaxBytes, "SendRateMax", "ClientTier");
            int tierMin = VanillaFloor.ClampSendRate(preset.SteamSendRateMinBytes, "SendRateMin", "ClientTier");

            // A MEASURED uplink is not a guess. When the line is the limit it overrides the tier,
            // including below vanilla — that is the case this whole change exists for.
            // Only a value the LINE limited may lower anything. A sample capped by Steam's own send
            // rate proves only that the uplink is at least that fast — trusting it would throttle a
            // fat line down to whatever cap happened to be in force when it was measured.
            int uplink = AutoTuneState.ClientUplinkBytes;
            bool isLimit = AutoTuneState.ClientUplinkIsLimit;
            bool thin = isLimit && uplink * UplinkBudgetFraction < tierMax;

            int maxBytes = thin ? (int)(uplink * UplinkBudgetFraction) : tierMax;
            // Min never above Max: Steam holds Min while a link struggles, so a Min above the real
            // line keeps pushing more than it carries.
            int minBytes = System.Math.Min(tierMin, maxBytes);

            var maxOpt = EffectiveConfig.SendRateMaxAtMost(maxBytes);
            var minOpt = EffectiveConfig.SendRateMinAtMost(minBytes);
            // Snapping can put Min above Max when the two tables' steps differ — re-clamp.
            if (EffectiveConfig.SendRateMinFromEnum(minOpt) > EffectiveConfig.SendRateMaxFromEnum(maxOpt))
                minOpt = EffectiveConfig.SendRateMinAtMost(EffectiveConfig.SendRateMaxFromEnum(maxOpt));

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
                       : thin ? $"upload {uplink / 1024} KB/s is the limit, staying under it"
                       : $"upload {uplink / 1024} KB/s, above the tier's ceiling",
                Max = maxOpt, Min = minOpt, Rate = rate, Queue = queue,
            });
        }

        public static void ApplyServer()
        {
            if (AutoTuneConfig.EnableServerAutoTune == null || !AutoTuneConfig.EnableServerAutoTune.Value) return;
            var preset = TierPresets.For(AutoTuneState.ServerTier);

            int maxBytes = VanillaFloor.ClampSendRate(preset.SteamSendRateMaxBytes, "SendRateMax", "ServerTier");

            // ASYMMETRIC, as the Enable Server Auto-Tune description has always promised: Max scales
            // with the tier so a fast client can burst, but Min stays at the LOW baseline whatever the
            // tier. On a server, Send Rate Min is the floor the per-player controller backs off to —
            // so a HIGH tier's 1024 KB/s Min meant it could never back a slow client off below 1 MB/s
            // and flooded their DOWNLINK. The description said Min stayed low; the code used each
            // tier's own Min. This makes it do what it said.
            int lowMin = TierPresets.For(Tier.Low).SteamSendRateMinBytes;
            int minBytes = System.Math.Min(
                VanillaFloor.ClampSendRate(lowMin, "SendRateMin", "ServerTier"), maxBytes);

            Write(new Writes
            {
                Side = "server",
                Tier = AutoTuneState.ServerTier.ToString(),
                Reason = "host CPU/RAM tier",
                Max = EffectiveConfig.SendRateMaxAtMost(maxBytes),
                Min = EffectiveConfig.SendRateMinAtMost(minBytes),
                Rate = VanillaFloor.ClampUpdateRate(preset.UpdateRate, "ServerTier"),
                Queue = preset.QueueSize,
            });
        }

        private struct Writes
        {
            public string Side, Tier, Reason;
            public SendRateMaxOptions Max;
            public SendRateMinOptions Min;
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
                // Only touch entries whose value actually changes. Every write fires SettingChanged
                // and saves the file; rewriting identical values on each rolling-monitor pass would
                // churn both for nothing.
                changed |= Set(FiresGhettoNetworkMod.ConfigSendRateMax, w.Max);
                changed |= Set(FiresGhettoNetworkMod.ConfigSendRateMin, w.Min);
                changed |= Set(FiresGhettoNetworkMod.ConfigUpdateRate,  w.Rate);
                changed |= Set(FiresGhettoNetworkMod.ConfigQueueSize,   w.Queue);

                if (changed)
                    LoggerOptions.LogMessage($"[AutoTune] {w.Side} {w.Tier}: {w.Reason}. Config set to "
                        + $"Send Rate Max {w.Max}, Min {w.Min}, ZDO Send Rate {w.Rate}, Queue Size {w.Queue}.");
            }
            finally { s_writing = false; }

            // After the batch, with Min and Max both final — never between them. Reaches connections
            // that are already open, which is where a mid-session probe result has to land.
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
