using BepInEx.Configuration;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
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
        // Stealing Azumatt's proven pattern outright (updated for current Valheim as of late 2025)
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> OverridePlayerLimit(IEnumerable<CodeInstruction> instructions)
        {
            if (!isDedicatedDetected) return instructions;

            var codeList = new List<CodeInstruction>(instructions);
            bool patched = false;

            for (int i = 0; i < codeList.Count; i++)
            {
                // Find the call to ZNet.GetNrOfPlayers()
                if (codeList[i].opcode == OpCodes.Call &&
                    codeList[i].operand is MethodInfo method &&
                    method.Name == "GetNrOfPlayers")
                {
                    // Look for the following constant load (the vanilla max player check)
                    for (int j = i + 1; j < codeList.Count; j++)
                    {
                        if (codeList[j].opcode == OpCodes.Ldc_I4_S ||
                            codeList[j].opcode == OpCodes.Ldc_I4 ||
                            codeList[j].opcode == OpCodes.Ldc_I4_0 ||
                            codeList[j].opcode == OpCodes.Ldc_I4_1 ||
                            codeList[j].opcode == OpCodes.Ldc_I4_2 ||
                            codeList[j].opcode == OpCodes.Ldc_I4_3 ||
                            codeList[j].opcode == OpCodes.Ldc_I4_4 ||
                            codeList[j].opcode == OpCodes.Ldc_I4_5 ||
                            codeList[j].opcode == OpCodes.Ldc_I4_6 ||
                            codeList[j].opcode == OpCodes.Ldc_I4_7 ||
                            codeList[j].opcode == OpCodes.Ldc_I4_8)
                        {
                            int newLimit = FiresGhettoNetworkMod.ConfigPlayerLimit.Value;

                            // PlayFab often needs +1 (host counts extra) – matches Azumatt's logic
                            if (ZNet.m_onlineBackend == OnlineBackendType.PlayFab)
                            {
                                newLimit += 1;
                                LoggerOptions.LogInfo("Applied +1 player limit for PlayFab backend.");
                            }

                            LoggerOptions.LogInfo($"Overriding player limit constant → {newLimit}");

                            // Use full Ldc_I4 for safety (supports values >127 without cast issues)
                            codeList[j] = new CodeInstruction(OpCodes.Ldc_I4, newLimit);
                            patched = true;
                            break;
                        }
                    }

                    if (patched) break; // Only one player limit check in the method
                }
            }

            if (!patched)
            {
                LoggerOptions.LogWarning("Player limit constant not found in ZNet.RPC_PeerInfo. Patch skipped – possible game update or conflicting mod.");
            }

            return codeList;
        }
    }
}