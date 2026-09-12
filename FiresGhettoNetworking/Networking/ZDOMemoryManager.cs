using BepInEx.Configuration;
using HarmonyLib;
using System.Collections.Generic;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Client-side safety valve for long exploration sessions: once the ZDO pool passes the configured
    /// cap, run vanilla's orphan cleanup and reclaim. Dedicated servers are never pruned this way.
    /// </summary>
    [HarmonyPatch]
    public static class ZDOMemoryManager
    {
        private const float StartupGraceSeconds = 600f;

        public static ConfigEntry<int> ConfigMaxZDOs;

        private static float _sessionElapsedSeconds;
        private static bool _graceExpired;
        private static bool _capWarningLogged;

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.Update))]
        [HarmonyPostfix]
        static void PruneOrphanZdosWhenOverCap(ZDOMan __instance, float dt)
        {
            if (ZNet.instance && ZNet.instance.IsServer()) return;
            if (ConfigMaxZDOs.Value <= 0) return;

            if (!_graceExpired)
            {
                _sessionElapsedSeconds += dt;
                if (_sessionElapsedSeconds >= StartupGraceSeconds)
                {
                    _graceExpired = true;
                    LoggerOptions.LogInfo("ZDO cleanup grace period ended — normal monitoring enabled.");
                }
                return;
            }

            var zdosById = AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            if (zdosById == null) return;

            var zdos = (Dictionary<ZDOID, ZDO>)zdosById.GetValue(__instance);
            if (zdos == null || zdos.Count <= ConfigMaxZDOs.Value) return;

            if (!_capWarningLogged)
            {
                LoggerOptions.LogWarning($"ZDO pool too big ({zdos.Count} > {ConfigMaxZDOs.Value}) — forcing cleanup...");
                LoggerOptions.LogWarning("You have been Exploring a LOT. You should log out to free up RAM.");
                _capWarningLogged = true;
            }

            int countBeforePrune = zdos.Count;
            AccessTools.Method(typeof(ZDOMan), "RemoveOrphanNonPersistentZDOS").Invoke(__instance, null);

            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();

            zdos = (Dictionary<ZDOID, ZDO>)zdosById.GetValue(__instance);
            LoggerOptions.LogInfo($"Cleanup complete: {countBeforePrune} → {zdos?.Count ?? 0} ZDOs");
        }
    }
}
