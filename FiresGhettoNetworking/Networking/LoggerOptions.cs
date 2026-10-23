using BepInEx.Logging;

namespace FiresGhettoNetworkMod
{
    public static class LoggerOptions
    {
        private static ManualLogSource logger;

        public static void Init(ManualLogSource source)
        {
            logger = source;
        }

        // Guard every call on the logger being set — these can fire before Init() during early config
        // binding, and logging must never NRE its caller.
        public static void LogError(object data)
        {
            if (logger != null) logger.LogError(data);
        }

        public static void LogWarning(object data)
        {
            if (logger != null) logger.LogWarning(data);
        }

        public static void LogMessage(object data)
        {
            if (logger != null && FiresGhettoNetworkMod.ConfigLogLevel != null && FiresGhettoNetworkMod.ConfigLogLevel.Value >= LogLevel.Message)
                logger.LogMessage(data);
        }

        public static bool VerboseEnabled =>
            FiresGhettoNetworkMod.ConfigLogLevel != null && FiresGhettoNetworkMod.ConfigLogLevel.Value >= LogLevel.Info;

        public static void LogInfo(object data)
        {
            if (logger != null && FiresGhettoNetworkMod.ConfigLogLevel != null && FiresGhettoNetworkMod.ConfigLogLevel.Value >= LogLevel.Info)
                logger.LogInfo(data);
        }

        // Periodic diagnostics are debug logs, not info. Passes only at FGN's Log Level Debug, the
        // value added after Info in FGN's own LogLevel enum (Ascend.cs). Written through LogInfo because the
        // BepInEx.cfg disk/console LogLevels hide Debug. Guard a line built every tick with DebugEnabled so it costs nothing off.
        public static bool DebugEnabled =>
            FiresGhettoNetworkMod.ConfigLogLevel != null && FiresGhettoNetworkMod.ConfigLogLevel.Value >= LogLevel.Debug;

        public static void LogDebug(object data)
        {
            if (logger != null && DebugEnabled)
                logger.LogInfo(data);
        }
    }
}