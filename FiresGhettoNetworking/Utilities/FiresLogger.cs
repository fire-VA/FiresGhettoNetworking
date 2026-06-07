using BepInEx.Logging;
using FiresCore.Logging;

namespace FiresGhettoNetworkMod
{
    // FiresGhettoNetworkMod's tagged logger. Thin wrapper over the shared FiresCore FiresLog engine,
    // source-linked from FiresUnifiedCore (compiled INTO this assembly — NOT a runtime FUC dependency;
    // VAGhetto still ships and loads standalone). Verbose == the mod's configured log level at Info.
    public static class FiresLogger
    {
        private static readonly FiresLog Log = new FiresLog(
            "FiresGhettoNetworkMod",
            () => FiresGhettoNetworkMod.ConfigLogLevel != null
                  && FiresGhettoNetworkMod.ConfigLogLevel.Value == LogLevel.Info);

        public static bool VerboseEnabled => Log.VerboseEnabled;

        public static void LogInfo(string message) => Log.Info(message);

        public static void LogVerbose(string message) => Log.Verbose(message);

        public static void LogWarning(string message) => Log.Warning(message);

        public static void LogError(string message) => Log.Error(message);
    }
}
