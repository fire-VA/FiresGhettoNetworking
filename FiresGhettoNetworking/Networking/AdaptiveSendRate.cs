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
                "Sets each Steam player's send rate from their own connection: starts at Send Rate Max, backs off (never below "
                + "Send Rate Min) on lost packets or rising ping, then recovers. Off while HYPERBOOST is on. Server only.");
            ConfigLog = config.Bind("10 - Diagnostics", "Log Adaptive Send Rate Ticks", false,
                "Logs every send rate step for each connection, about once a second: ping, send window, data in flight, Steam "
                + "wait, delivery, pacing, goodput and the decision. Noisy; for tuning only. Logs on a server (Adaptive Send "
                + "Rate) and on a client (Adaptive Upload).");
            ConfigEnabled.SettingChanged += (_, __) =>
            {
                LinkController.ResetRates();
                if (!ConfigEnabled.Value) NetworkingRatesGroup.ApplyEffectiveRatesLiveToAllPeers();
            };
        }
    }
}
