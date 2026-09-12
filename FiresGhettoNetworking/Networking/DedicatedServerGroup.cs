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

            // Detection is now done in Ascend.Awake() before this is called
            // Just read the cached result
            isDedicatedDetected = ServerClientUtils.IsDedicatedServerDetected;

            if (isDedicatedDetected)
                LoggerOptions.LogInfo("Running as dedicated server (ServerClientUtils).");
            else
                LoggerOptions.LogInfo("Running as client/listen-server (ServerClientUtils).");
        }

        // ====================== FORCE CROSSPLAY ======================
        [HarmonyPatch(typeof(FejdStartup), "ParseServerArguments")]
        [HarmonyPostfix]
        static void ApplyForceCrossplay()
        {
            LoggerOptions.LogInfo($"[Crossplay] ParseServerArguments postfix: dedi={isDedicatedDetected}, "
                + $"config={FiresGhettoNetworkMod.ConfigForceCrossplay.Value}, backend-before={ZNet.m_onlineBackend}.");
            if (!isDedicatedDetected) return;
            switch (FiresGhettoNetworkMod.ConfigForceCrossplay.Value)
            {
                case ForceCrossplayOptions.playfab:
                    ZNet.m_onlineBackend = OnlineBackendType.PlayFab;
                    LoggerOptions.LogMessage("[Crossplay] Forcing crossplay ENABLED (PlayFab backend).");
                    break;
                case ForceCrossplayOptions.steamworks:
                    ZNet.m_onlineBackend = OnlineBackendType.Steamworks;
                    LoggerOptions.LogMessage("[Crossplay] Forcing crossplay DISABLED (Steamworks backend).");
                    break;
                default:
                    LoggerOptions.LogInfo("[Crossplay] mode: vanilla (respecting command line).");
                    break;
            }
            LoggerOptions.LogInfo($"[Crossplay] backend-after={ZNet.m_onlineBackend}.");
        }

        // Diagnostic: log the FINAL backend when ZNet actually starts. If it differs from what
        // ApplyForceCrossplay set, something reset it after ParseServerArguments (and we'll know to
        // re-assert it on a later hook).
        [HarmonyPatch(typeof(ZNet), "Start")]
        [HarmonyPostfix]
        static void LogBackendAtZNetStart()
        {
            LoggerOptions.LogInfo($"[Crossplay] ZNet.Start — online backend is now {ZNet.m_onlineBackend} (dedi={isDedicatedDetected}).");
        }

        // ====================== CLIENT-SIDE FORCE ======================
        // ApplyForceCrossplay above only covers the dedi (ParseServerArguments). The CLIENT picks its
        // backend when hosting (FejdStartup.GetOnlineBackend) and when joining (ZNet.SetServerHost), so
        // honor "Force Crossplay" there too — otherwise a steamworks config still rides PlayFab client-side.
        private static OnlineBackendType? ForcedBackend()
        {
            switch (FiresGhettoNetworkMod.ConfigForceCrossplay.Value)
            {
                case ForceCrossplayOptions.steamworks: return OnlineBackendType.Steamworks;
                case ForceCrossplayOptions.playfab:    return OnlineBackendType.PlayFab;
                default:                               return (OnlineBackendType?) null; // vanilla — leave Valheim's choice
            }
        }

        // Hosting / start-game backend selection.
        [HarmonyPatch(typeof(FejdStartup), "GetOnlineBackend")]
        [HarmonyPostfix]
        static void ForceClientBackendOnSelect(ref OnlineBackendType __result)
        {
            OnlineBackendType? forced = ForcedBackend();
            if (!forced.HasValue || __result == forced.Value) return;
            LoggerOptions.LogInfo($"[Crossplay] client GetOnlineBackend {__result} -> forced {forced.Value} (config={FiresGhettoNetworkMod.ConfigForceCrossplay.Value}).");
            __result = forced.Value;
        }

        // Joining by IP / explicit backend — this overload carries a real address, so the backend can
        // safely be forced.
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.SetServerHost), new System.Type[] { typeof(string), typeof(int), typeof(OnlineBackendType) })]
        [HarmonyPostfix]
        static void ForceClientBackendOnJoinByAddress()
        {
            OnlineBackendType? forced = ForcedBackend();
            if (!forced.HasValue || ZNet.m_onlineBackend == forced.Value) return;
            LoggerOptions.LogInfo($"[Crossplay] client SetServerHost(addr) {ZNet.m_onlineBackend} -> forced {forced.Value} (config={FiresGhettoNetworkMod.ConfigForceCrossplay.Value}).");
            ZNet.m_onlineBackend = forced.Value;
        }

        // Joining by a PlayFab/crossplay player id — there's NO Steam address here, so it can't be
        // forced to Steamworks. Make that loud instead of silently riding PlayFab against the config.
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.SetServerHost), new System.Type[] { typeof(string) })]
        [HarmonyPostfix]
        static void WarnCrossplayJoinUnderSteamworks()
        {
            if (FiresGhettoNetworkMod.ConfigForceCrossplay.Value == ForceCrossplayOptions.steamworks)
                LoggerOptions.LogWarning("[Crossplay] Joining a crossplay/PlayFab server entry while Force Crossplay=steamworks — a PlayFab join has no Steam address to force, so this connection stays PlayFab. Join by IP (or a Steam server-list entry) for a Steam connection.");
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

        // ====================== ADVERTISED MAX OVERRIDE ======================
        // ConfigAdvertisedPlayerLimit when set, otherwise ConfigPlayerLimit. Steam entry points are prefixed
        // below; the PlayFab stores are rewritten further down.
        private static int ResolveAdvertisedLimit()
        {
            int advertised = FiresGhettoNetworkMod.ConfigAdvertisedPlayerLimit?.Value ?? 0;
            return advertised > 0 ? advertised : FiresGhettoNetworkMod.ConfigPlayerLimit.Value;
        }

        private static bool IsIntConstantLoad(CodeInstruction ins)
        {
            var op = ins.opcode;
            return op == OpCodes.Ldc_I4
                || op == OpCodes.Ldc_I4_S
                || op == OpCodes.Ldc_I4_0 || op == OpCodes.Ldc_I4_1
                || op == OpCodes.Ldc_I4_2 || op == OpCodes.Ldc_I4_3
                || op == OpCodes.Ldc_I4_4 || op == OpCodes.Ldc_I4_5
                || op == OpCodes.Ldc_I4_6 || op == OpCodes.Ldc_I4_7
                || op == OpCodes.Ldc_I4_8;
        }

        // A transpiler on ZSteamMatchmaking.RegisterServer broke the CreateLobby callback chain (v1.3.0), so
        // the Steam SDK entry points are prefixed instead. The game does not call SetMaxPlayerCount today;
        // that prefix is inert until something does.

        [HarmonyPatch(typeof(Steamworks.SteamMatchmaking), nameof(Steamworks.SteamMatchmaking.CreateLobby))]
        [HarmonyPrefix]
        static void OverrideSteamLobbyMaxMembers(Steamworks.ELobbyType eLobbyType, ref int cMaxMembers)
        {
            if (!isDedicatedDetected) return;
            int target = ResolveAdvertisedLimit();
            if (target <= 0 || cMaxMembers == target) return;
            LoggerOptions.LogInfo($"Overriding SteamMatchmaking.CreateLobby cMaxMembers: {cMaxMembers} → {target}");
            cMaxMembers = target;
        }

        [HarmonyPatch(typeof(Steamworks.SteamGameServer), nameof(Steamworks.SteamGameServer.SetMaxPlayerCount))]
        [HarmonyPrefix]
        static void OverrideSteamGameServerMaxPlayers(ref int cPlayersMax)
        {
            if (!isDedicatedDetected) return;
            int target = ResolveAdvertisedLimit();
            if (target <= 0 || cPlayersMax == target) return;
            LoggerOptions.LogInfo($"Overriding SteamGameServer.SetMaxPlayerCount cPlayersMax: {cPlayersMax} → {target}");
            cPlayersMax = target;
        }

        private const int VanillaDedicatedLobbySeats = 11;
        private const int VanillaSessionMaxPlayers = 10;
        private const int MinAdvertisedPlayers = 2;
        private const int MaxAdvertisedPlayers = 64;

        /// <summary>Lobby capacity carries a seat for the dedicated server itself, which vanilla's browser subtracts back off.</summary>
        public static int PlayFabLobbyCapacity()
        {
            int advertised = ResolveAdvertisedLimit();
            if (advertised <= 0) return VanillaDedicatedLobbySeats;
            if (ZNet.instance != null && ZNet.instance.IsDedicated()) advertised += 1;
            return Mathf.Clamp(advertised, MinAdvertisedPlayers, MaxAdvertisedPlayers);
        }

        public static int PlayFabSessionMaxPlayers()
        {
            int advertised = ResolveAdvertisedLimit();
            if (advertised <= 0) return VanillaSessionMaxPlayers;
            return Mathf.Clamp(advertised, MinAdvertisedPlayers, MaxAdvertisedPlayers);
        }

        /// <summary>The crossplay browser reads these two writes; the Steam SDK prefixes above cannot reach them.</summary>
        [HarmonyPatch(typeof(ZPlayFabMatchmaking), "CreateLobby")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> OverridePlayFabLobbyCapacity(IEnumerable<CodeInstruction> instructions)
        {
            return RewriteMaxPlayersStore(instructions, "ZPlayFabMatchmaking.CreateLobby",
                "CreateLobbyRequest::MaxPlayers", nameof(PlayFabLobbyCapacity));
        }

        [HarmonyPatch(typeof(ZPlayFabMatchmaking), "SetPlatformMatchmakingData")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> OverridePlayFabSessionMax(IEnumerable<CodeInstruction> instructions)
        {
            return RewriteMaxPlayersStore(instructions, "ZPlayFabMatchmaking.SetPlatformMatchmakingData",
                "MultiplayerSessionData::m_maxPlayers", nameof(PlayFabSessionMaxPlayers));
        }

        /// <summary>Anchors on the field store rather than the literal: the client build writes 10 there, the dedicated build 11.</summary>
        private static IEnumerable<CodeInstruction> RewriteMaxPlayersStore(
            IEnumerable<CodeInstruction> instructions, string patchedMethod, string fieldId, string capacityGetter)
        {
            if (!isDedicatedDetected) return instructions;

            var code = new List<CodeInstruction>(instructions);
            var capacity = AccessTools.Method(typeof(DedicatedServerGroup), capacityGetter);
            int rewritten = 0;

            for (int i = 1; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Stfld) continue;

                var field = code[i].operand as FieldInfo;
                if (field == null || field.DeclaringType == null) continue;
                if (field.DeclaringType.Name + "::" + field.Name != fieldId) continue;
                if (!IsIntConstantLoad(code[i - 1])) continue;

                code[i - 1] = new CodeInstruction(OpCodes.Call, capacity);
                rewritten++;
            }

            if (rewritten == 0)
                LoggerOptions.LogWarning(
                    $"Advertised player limit: {patchedMethod} has no constant store into {fieldId}; the crossplay browser keeps vanilla's max.");
            else
                LoggerOptions.LogMessage($"Advertised player limit: rewrote {rewritten} store(s) into {fieldId} in {patchedMethod}.");

            return code;
        }
    }
}