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
        // binding (ValidateVanillaFloors did exactly that), and logging must never NRE its caller.
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

        public static void LogInfo(object data)
        {
            if (logger != null && FiresGhettoNetworkMod.ConfigLogLevel != null && FiresGhettoNetworkMod.ConfigLogLevel.Value >= LogLevel.Info)
                logger.LogInfo(data);
        }
    }
}