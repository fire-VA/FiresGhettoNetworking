using BepInEx.Configuration;
using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    [HarmonyPatch]
    public static class ZDOMemoryManager
    {
        public static ConfigEntry<int> ConfigMaxZDOs;

        private static float startupTimer = 0f;
        private static bool startupComplete = false;
        private static bool warningShown = false; // One-time warning per session

        private const float STARTUP_GRACE_PERIOD = 600f; // 10 minutes

        public static void Init(ConfigFile config)
        {
            ConfigMaxZDOs = config.Bind(
                "Advanced",
                "Max Active ZDOs",
                10000000,
                new ConfigDescription(
                    "Force ZDO cleanup if active ZDOs exceed this after startup (0 = disabled).\n" +
                    "Startup grace period (10 min) prevents spam during world load.\n" +
                    "This feature is CLIENT-ONLY and will not run on dedicated servers.",
                    new AcceptableValueRange<int>(0, 2000000)));
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.Update))]
        [HarmonyPostfix]
        static void CleanupIfTooBig(ZDOMan __instance, float dt)
        {
            // Completely disabled on dedicated servers — servers should never clean up ZDOs aggressively
            if (ZNet.instance && ZNet.instance.IsServer())
                return;

            // Disabled via config
            if (ConfigMaxZDOs.Value <= 0) return;

            // Grace period: no cleanup during first 10 minutes of play
            if (!startupComplete)
            {
                startupTimer += dt;
                if (startupTimer >= STARTUP_GRACE_PERIOD)
                {
                    startupComplete = true;
                    LoggerOptions.LogInfo("ZDO cleanup grace period ended — normal monitoring enabled.");
                }
                return;
            }

            var dictField = AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            if (dictField == null) return;

            var dict = (Dictionary<ZDOID, ZDO>)dictField.GetValue(__instance);
            if (dict == null || dict.Count <= ConfigMaxZDOs.Value) return;

            // Show warning only once per session
            if (!warningShown)
            {
                LoggerOptions.LogWarning($"ZDO pool too big ({dict.Count} > {ConfigMaxZDOs.Value}) — forcing cleanup...");
                LoggerOptions.LogWarning("You have been Exploring a LOT. You should log out to free up RAM.");
                warningShown = true;
            }

            int before = dict.Count;

            // Vanilla orphan cleanup
            AccessTools.Method(typeof(ZDOMan), "RemoveOrphanNonPersistentZDOS").Invoke(__instance, null);

            // Force GC
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();

            dict = (Dictionary<ZDOID, ZDO>)dictField.GetValue(__instance);
            LoggerOptions.LogInfo($"Cleanup complete: {before} → {dict?.Count ?? 0} ZDOs");
        }
    }
}