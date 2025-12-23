using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    [HarmonyPatch]
    public static class MonsterAIPatches
    {
        // ====================================================================
        // Remove m_localPlayer check in RandEventSystem.FixedUpdate — use any player instead
        // ====================================================================
        [HarmonyPatch(typeof(RandEventSystem), "FixedUpdate")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> RandEventSystem_FixedUpdate_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var matcher = new CodeMatcher(instructions);
            matcher.MatchForward(false,
                new CodeMatch(OpCodes.Ldsfld, AccessTools.Field(typeof(Player), "m_localPlayer")),
                new CodeMatch(OpCodes.Callvirt, AccessTools.Method(typeof(UnityEngine.Object), "op_Implicit"))
            );

            if (matcher.IsInvalid)
            {
                LoggerOptions.LogWarning("RandEventSystem transpiler failed to find m_localPlayer check");
                return instructions;
            }

            matcher.RemoveInstructions(2); // Remove local player check
            matcher.Insert(
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Player), "GetAllPlayers")), // Load all players
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MonsterAIPatches), nameof(HasAnyPlayerNearby))) // Call our custom method
            );

            return matcher.InstructionEnumeration();
        }

        // ====================================================================
        // Remove m_localPlayer check in SpawnSystem.UpdateSpawning — use any player instead
        // ====================================================================
        [HarmonyPatch(typeof(SpawnSystem), "UpdateSpawning")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> SpawnSystem_UpdateSpawning_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var matcher = new CodeMatcher(instructions);
            matcher.MatchForward(false,
                new CodeMatch(OpCodes.Ldsfld, AccessTools.Field(typeof(Player), "m_localPlayer")),
                new CodeMatch(OpCodes.Ldnull),
                new CodeMatch(OpCodes.Call, AccessTools.Method(typeof(UnityEngine.Object), "op_Equality")),
                new CodeMatch(OpCodes.Brfalse)
            );

            if (matcher.IsInvalid)
            {
                LoggerOptions.LogWarning("SpawnSystem transpiler failed to find m_localPlayer check");
                return instructions;
            }

            matcher.RemoveInstructions(3); // Remove the equality check
            matcher.SetInstructionAndAdvance(
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Player), "GetAllPlayers")) // Load all players
            );

            matcher.InsertAndAdvance(
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MonsterAIPatches), nameof(HasAnyPlayerNearby))) // Call our custom method
            );

            matcher.SetOpcodeAndAdvance(OpCodes.Brfalse); // Branch if no player nearby (skip spawning)

            return matcher.InstructionEnumeration();
        }

        // Custom helper: Check if ANY player is nearby (replace local player check)
        private static bool HasAnyPlayerNearby(List<Player> allPlayers)
        {
            if (allPlayers == null || allPlayers.Count == 0) return false;
            // "Nearby" logic: Check if any player is within active area (simplify — use vanilla range or customize)
            foreach (Player player in allPlayers)
            {
                if (player != null && ZNetScene.InActiveArea(ZoneSystem.GetZone(player.transform.position), ZoneSystem.GetZone(ZoneSystem.instance.m_activeArea * ZoneSystem.c_ZoneSize * Vector3.one)))
                    return true;
            }
            return false;
        }
    }
}