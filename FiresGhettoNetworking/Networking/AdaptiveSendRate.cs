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
                "Pins each player's Steam send rate and steps it from that player's own connection. It starts at the Auto-Tune "
                + "or manual Send Rate Max, rises while Steam is holding data back for that player, and backs off toward the rate "
                + "actually getting through, never below Send Rate Min, when packets go missing or ping climbs. Defers to "
                + "HYPERBOOST while that is on. SERVER-SIDE only.");
            ConfigLog = config.Bind("10 - Diagnostics", "Log Adaptive Send Rate Ticks", false,
                "Logs each player's link once a second: ping, send window, data in flight, Steam wait, delivery, pacing, "
                + "goodput and the send rate decision. Noisy, for tuning only. SERVER-SIDE only.");
            ConfigEnabled.SettingChanged += (_, __) =>
            {
                LinkController.ResetRates();
                if (!ConfigEnabled.Value) NetworkingRatesGroup.ApplyEffectiveRatesLiveToAllPeers();
            };
        }
    }
}
