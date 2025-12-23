using BepInEx.Configuration;
using HarmonyLib;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection.Emit;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    [HarmonyPatch]
    public static class DedicatedServerGroup
    {
        private static bool isDedicatedDetected = false;

        public static void Init(ConfigFile config)
        {
            // Configs already bound in main class
            LoggerOptions.LogInfo("Dedicated server features initialized.");

            // Reliable detection at startup (replaces brittle IL-scanning approach)
            ServerClientUtils.Detect();
            isDedicatedDetected = ServerClientUtils.IsDedicatedServerDetected;
            if (isDedicatedDetected)
                LoggerOptions.LogInfo("Running as dedicated server detected (ServerClientUtils).");
            else
                LoggerOptions.LogInfo("Running as client/listen-server (ServerClientUtils).");
        }

        // ====================== FORCE CROSSPLAY ======================

        [HarmonyPatch(typeof(FejdStartup), "ParseServerArguments")]
        [HarmonyPostfix]
        static void ApplyForceCrossplay()
        {
            if (!isDedicatedDetected) return;

            switch (FiresGhettoNetworkMod.ConfigForceCrossplay.Value)
            {
                case ForceCrossplayOptions.playfab:
                    ZNet.m_onlineBackend = OnlineBackendType.PlayFab;
                    LoggerOptions.LogInfo("Forcing crossplay ENABLED (PlayFab backend).");
                    break;
                case ForceCrossplayOptions.steamworks:
                    ZNet.m_onlineBackend = OnlineBackendType.Steamworks;
                    LoggerOptions.LogInfo("Forcing crossplay DISABLED (Steamworks backend).");
                    break;
                default:
                    LoggerOptions.LogInfo("Crossplay mode: vanilla (respecting command line).");
                    break;
            }
        }

        // ====================== PLAYER LIMIT OVERRIDE ======================

        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> OverridePlayerLimit(IEnumerable<CodeInstruction> instructions)
        {
            if (!isDedicatedDetected) return instructions;

            var list = new List<CodeInstruction>(instructions);

            for (int i = 0; i < list.Count; i++)
            {
                // Look for the constant 10 (vanilla max players)
                if (list[i].opcode == OpCodes.Ldc_I4_S && (sbyte)list[i].operand == 10)
                {
                    LoggerOptions.LogInfo($"Overriding player limit: 10 → {FiresGhettoNetworkMod.ConfigPlayerLimit.Value}");
                    list[i] = new CodeInstruction(OpCodes.Ldc_I4_S, (sbyte)FiresGhettoNetworkMod.ConfigPlayerLimit.Value);
                }
            }

            return list;
        }
    }
}