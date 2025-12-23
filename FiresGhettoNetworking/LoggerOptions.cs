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

        public static void LogError(object data) => logger.LogError(data);

        public static void LogWarning(object data) => logger.LogWarning(data);

        public static void LogMessage(object data)
        {
            if (FiresGhettoNetworkMod.ConfigLogLevel != null && FiresGhettoNetworkMod.ConfigLogLevel.Value >= LogLevel.Message)
                logger.LogMessage(data);
        }

        public static void LogInfo(object data)
        {
            if (FiresGhettoNetworkMod.ConfigLogLevel != null && FiresGhettoNetworkMod.ConfigLogLevel.Value >= LogLevel.Info)
                logger.LogInfo(data);
        }
    }
}