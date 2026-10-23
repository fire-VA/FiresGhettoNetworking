using BepInEx.Configuration;

namespace FiresGhettoNetworkMod
{
    /// <summary>Settings for the per-player Steam send rate that LinkController steps on a server.</summary>
    public static class AdaptiveSendRate
    {
        public static ConfigEntry<bool> ConfigEnabled;
        public static ConfigEntry<bool> ConfigLog;

        // Raised by the socket stress tests while they drive a connection's rate by hand.
        public static bool Suspend;

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("06 - Auto-Tune", "Adaptive Send Rate", true,
                Cfg.Adv("Server sets each Steam player's send rate from their own connection quality."));
            ConfigLog = config.Bind("14 - Diagnostics", "Log Adaptive Send Rate Ticks", false,
                Cfg.Adv("Debug log: every send rate step per connection, about once a second. Noisy."));
            ConfigEnabled.SettingChanged += (_, __) =>
            {
                LinkController.ResetRates();
                if (!ConfigEnabled.Value) NetworkingRatesGroup.ApplyEffectiveRatesLiveToAllPeers();
            };
        }
    }
}
