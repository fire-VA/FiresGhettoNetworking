using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Debug only: how often the allocating network paths run, so fixes go where the garbage is. Restart to change. Off by default.
    /// ZDO.Deserialize, ZDOMan.SendZDOs and routed RPCs are counted, with GC gen0 count and Mono used, every 30 s.
    /// </summary>
    [HarmonyPatch]
    internal static class AllocationSiteCensus
    {
        public static ConfigEntry<bool> ConfigEnabled;
        private static int s_deserialize, s_send, s_routed;
        private static float s_next;
        private static int s_lastGc;

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("14 - Diagnostics", "Log Allocation Sites", false,
                Cfg.Adv("Debug log: how often the network paths that allocate run, every 30 s. Restart to change."));
        }

        private static bool Prepare() => ConfigEnabled != null && ConfigEnabled.Value;

        [HarmonyPatch(typeof(ZDO), "Deserialize"), HarmonyPostfix]
        private static void AfterDeserialize() { s_deserialize++; Tick(); }

        [HarmonyPatch(typeof(ZDOMan), "SendZDOs"), HarmonyPostfix]
        private static void AfterSend() { s_send++; }

        [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC"), HarmonyPostfix]
        private static void AfterRouted() { s_routed++; Tick(); }

        private static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now < s_next) return;
            if (s_next > 0f)
            {
                int gc = System.GC.CollectionCount(0);
                LoggerOptions.LogDebug($"[AllocSites] last 30 s: ZDO.Deserialize {s_deserialize}, SendZDOs {s_send}, routed RPC {s_routed}; GC gen0 +{gc - s_lastGc}, Mono used {System.GC.GetTotalMemory(false) / 1048576} MB");
                s_lastGc = gc;
            }
            s_deserialize = s_send = s_routed = 0;
            s_next = now + 30f;
        }
    }
}
