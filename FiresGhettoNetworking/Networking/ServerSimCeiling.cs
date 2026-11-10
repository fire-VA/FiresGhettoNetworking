// Idea from [generator]'s SpreadTheLoad study (Tools\STUDY_sections\other_NRF_VPE_STL.md 3.12). SpreadTheLoad has no licence, so only the
// behaviour was studied; this is our own design and code.
using System;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.69 ([fgn], Fire 11-08). "Server Simulation Distance Ceiling": an admin control for a dedicated server. Vanilla caps each joining
    /// client's requested simulation distance at the SERVER's own setting (ZNet.RPC_RequestValidSimulationDistance), and on a dedicated
    /// server that setting comes from the service account's PlayerPrefs, which a service account may never have. With the key at 0 or
    /// above, the ceiling for that one call is SimulationDistance.GetSimulationDistance(level); vanilla's own body then does the capping
    /// and the reply, so every other mod's prefix / postfix on the handler still runs. The server's own value is put back in a finalizer.
    /// -1 (default) = vanilla. One Info line at the first capped request; a Debug line per peer.
    /// </summary>
    [HarmonyPatch]
    internal static class ServerSimCeiling
    {
        internal static BepInEx.Configuration.ConfigEntry<int> ConfigLevel;
        private static readonly AccessTools.FieldRef<ZNet, SimulationDistance> s_sd = AccessTools.FieldRefAccess<ZNet, SimulationDistance>("m_simulationDistance");
        private static bool s_said;

        internal static void InitConfig(BepInEx.Configuration.ConfigFile config)
        {
            ConfigLevel = config.Bind("04 - Networking", "Server Simulation Distance Ceiling", -1,
                new BepInEx.Configuration.ConfigDescription(
                    "Dedicated server: the highest simulation distance a joining player is granted, as the game's own setting level (0 = smallest). " +
                    "-1 = the server's own setting, as in vanilla. A higher ceiling means the server streams more to each player.",
                    new BepInEx.Configuration.AcceptableValueRange<int>(-1, 10)));
        }

        private struct Saved { public bool Set; public SimulationDistance Old; }

        [HarmonyPatch(typeof(ZNet), "RPC_RequestValidSimulationDistance"), HarmonyPrefix]
        private static void Before(ZNet __instance, out Saved __state)
        {
            __state = default;
            int level = ConfigLevel != null ? ConfigLevel.Value : -1;
            if (level < 0 || __instance == null || !__instance.IsServer() || !__instance.IsDedicated()) return;
            try
            {
                SimulationDistance ceiling = SimulationDistance.GetSimulationDistance(level);
                __state.Old = s_sd(__instance); __state.Set = true;
                s_sd(__instance) = ceiling;
                if (!s_said) { s_said = true; LoggerOptions.LogInfo($"[SimDistance] server ceiling on: joining players are granted at most level {level} (near {ceiling.NearSimulationDistance}, total {ceiling.TotalSimulationDistance} zones)"); }
                else if (LoggerOptions.DebugEnabled) LoggerOptions.LogDebug($"[SimDistance] a simulation distance request answered under the level {level} ceiling");
            }
            catch (Exception e) { if (__state.Set) s_sd(__instance) = __state.Old; __state.Set = false; LoggerOptions.LogDebug($"[SimDistance] ceiling skipped: {e.Message}"); }
        }

        [HarmonyPatch(typeof(ZNet), "RPC_RequestValidSimulationDistance"), HarmonyFinalizer]
        private static Exception After(ZNet __instance, Saved __state, Exception __exception)
        {
            if (__state.Set && __instance != null) s_sd(__instance) = __state.Old;
            return __exception;
        }
    }
}
