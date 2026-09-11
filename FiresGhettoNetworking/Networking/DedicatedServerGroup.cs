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
        // Vanilla hard-codes "10" as the advertised max in three matchmaking
        // sites. Steam server browser → BattleMetrics reads the value from
        // ZSteamMatchmaking.RegisterServer; PlayFab session matchmaking reads
        // it from ZPlayFabMatchmaking.SetPlatformMatchmakingData; PlayFab Party
        // network capacity is set in ZPlayFabMatchmaking.CreateAndJoinNetwork.
        // We rewrite each "ldc.i4.s 10" literal to the configured advertised
        // limit so the public-facing max-player count actually matches what
        // the server is provisioned for (or whatever marketing value the
        // operator wants to display).
        //
        // Order of preference for the replacement value:
        //   1. ConfigAdvertisedPlayerLimit if > 0 (operator wants a display
        //      value distinct from the real cap)
        //   2. ConfigPlayerLimit (advertised matches the real cap)
        //   3. PlayFab paths get +1 (vanilla counts the host as a slot in
        //      non-dedicated mode; matches the existing OverridePlayerLimit
        //      adjustment)
        //
        // PUBLIC_TEST note: ZSteamMatchmaking was refactored out in the public
        // test build (matchmaking now goes through MultiBackendMatchmaking).
        // The Steam transpiler is conditionally compiled.

        private static int ResolveAdvertisedLimit(bool addPlayFabHostSlot)
        {
            int adv = FiresGhettoNetworkMod.ConfigAdvertisedPlayerLimit?.Value ?? 0;
            int target = adv > 0 ? adv : FiresGhettoNetworkMod.ConfigPlayerLimit.Value;
            if (addPlayFabHostSlot && ZNet.m_onlineBackend == OnlineBackendType.PlayFab)
                target += 1;
            return target;
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

        // BATTLE-METRICS ADVERTISED MAX OVERRIDE.
        //
        // First attempt (v1.3.0) transpiled ZSteamMatchmaking.RegisterServer and
        // ZPlayFabMatchmaking.* — Harmony attaching to RegisterServer broke the
        // SteamMatchmaking::CreateLobby async callback chain, leaving the lobby
        // half-created (log showed "Registering lobby" but never "Lobby was
        // created"). Server then never appeared on the Steam Community list.
        //
        // Correct approach (stolen from Azumatt's MaxPlayerCount mod): prefix
        // the SDK methods themselves with a byref-int rewrite. Two surfaces:
        //
        //   1. Steamworks.SteamMatchmaking.CreateLobby(ELobbyType, int cMaxMembers)
        //      — this is the actual call site Valheim's RegisterServer makes
        //      with a hardcoded 10. cMaxMembers is what Steam stores on the
        //      lobby and what the server browser / favorite query display as
        //      the "max" half of "X / max".
        //
        //   2. Steamworks.SteamGameServer.SetMaxPlayerCount(int cPlayersMax)
        //      — defensive: assembly_valheim doesn't appear to call this in
        //      0.221.12, but PlayFab / mods / future Valheim versions might.
        //      No-op if never invoked.
        //
        // Both prefixes pull the override from ConfigAdvertisedPlayerLimit
        // (falling back to ConfigPlayerLimit), matching the same ResolveAdvertisedLimit
        // helper used elsewhere in this file.

        [HarmonyPatch(typeof(Steamworks.SteamMatchmaking), nameof(Steamworks.SteamMatchmaking.CreateLobby))]
        [HarmonyPrefix]
        static void OverrideSteamLobbyMaxMembers(Steamworks.ELobbyType eLobbyType, ref int cMaxMembers)
        {
            if (!isDedicatedDetected) return;
            int target = ResolveAdvertisedLimit(addPlayFabHostSlot: false);
            if (target <= 0 || cMaxMembers == target) return;
            LoggerOptions.LogInfo($"Overriding SteamMatchmaking.CreateLobby cMaxMembers: {cMaxMembers} → {target}");
            cMaxMembers = target;
        }

        [HarmonyPatch(typeof(Steamworks.SteamGameServer), nameof(Steamworks.SteamGameServer.SetMaxPlayerCount))]
        [HarmonyPrefix]
        static void OverrideSteamGameServerMaxPlayers(ref int cPlayersMax)
        {
            if (!isDedicatedDetected) return;
            int target = ResolveAdvertisedLimit(addPlayFabHostSlot: false);
            if (target <= 0 || cPlayersMax == target) return;
            LoggerOptions.LogInfo($"Overriding SteamGameServer.SetMaxPlayerCount cPlayersMax: {cPlayersMax} → {target}");
            cPlayersMax = target;
        }

        private static List<CodeInstruction> RewriteConstBeforeFieldStore(
            IEnumerable<CodeInstruction> instructions,
            string fieldName,
            string methodLabel,
            bool addPlayFabHostSlot,
            bool silentIfNotFound = false)
        {
            var list = instructions as List<CodeInstruction> ?? new List<CodeInstruction>(instructions);
            bool patched = false;
            for (int i = 0; i < list.Count; i++)
            {
                var ins = list[i];
                if (ins.opcode != OpCodes.Stfld) continue;
                if (!(ins.operand is FieldInfo fi) || fi.Name != fieldName) continue;
                if (i == 0 || !IsIntConstantLoad(list[i - 1])) continue;

                int target = ResolveAdvertisedLimit(addPlayFabHostSlot);
                LoggerOptions.LogInfo($"Overriding {methodLabel} {fieldName} → {target}");
                list[i - 1] = new CodeInstruction(OpCodes.Ldc_I4, target);
                patched = true;
                break;
            }
            if (!patched && !silentIfNotFound)
                LoggerOptions.LogWarning($"{fieldName} constant not found in {methodLabel}. Patch skipped.");
            return list;
        }

        private static List<CodeInstruction> RewriteConstBeforePropertySet(
            List<CodeInstruction> list,
            string propertyName,
            string methodLabel,
            bool addPlayFabHostSlot)
        {
            string setterName = "set_" + propertyName;
            bool patched = false;
            for (int i = 0; i < list.Count; i++)
            {
                var ins = list[i];
                if (ins.opcode != OpCodes.Callvirt && ins.opcode != OpCodes.Call) continue;
                if (!(ins.operand is MethodInfo mi) || mi.Name != setterName) continue;
                if (i == 0 || !IsIntConstantLoad(list[i - 1])) continue;

                int target = ResolveAdvertisedLimit(addPlayFabHostSlot);
                LoggerOptions.LogInfo($"Overriding {methodLabel} {propertyName} → {target}");
                list[i - 1] = new CodeInstruction(OpCodes.Ldc_I4, target);
                patched = true;
                break;
            }
            if (!patched)
                LoggerOptions.LogWarning($"{propertyName} constant not found in {methodLabel}. Patch skipped.");
            return list;
        }
    }
}