using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Picks each player's world update around where they actually are rather than where they were.
    /// Clients report a reference position only in ZNet.SendPeriodicData, which runs on a 2 second
    /// timer, so the server chooses sectors and sorts by distance against a position up to two
    /// seconds old — at sprint or sailing speed, far enough behind to gather the wrong sectors and
    /// sort the nearest objects last. The player's own character ZDO is already on the server and is
    /// updated continuously as they move, so it is both fresher and exact: no extrapolation, and a
    /// teleport lands where the player really is instead of being predicted through.
    /// </summary>
    [HarmonyPatch]
    public static class SyncListRefPosPatches
    {
        public static ConfigEntry<bool> ConfigLivePlayerPositions;

        private static bool s_patched;

        // Harmony re-runs the transpiler whenever another mod patches CreateSyncList, so the success line prints once.
        private static bool s_announced;

        public static void InitConfig(ConfigFile config)
        {
            ConfigLivePlayerPositions = config.Bind("04 - Networking", "Live Player Positions", true,
                "Chooses which world objects to send each player using their character's live position instead of the "
                + "reference position they last reported, which vanilla refreshes only every 2 seconds. A moving player "
                + "otherwise has their update built around where they were up to two seconds ago, which gathers the "
                + "sectors behind them and sorts the objects in front of them last. Falls back to the reported position "
                + "for any player whose character is not resolvable. SERVER-SIDE.");
        }

        /// <summary>
        /// Where the peer's character actually is, or the reported reference position when no character
        /// ZDO is resolvable — a player who has not spawned, is mid-respawn, or has no character yet.
        /// </summary>
        public static Vector3 LiveRefPos(ZNetPeer peer)
        {
            if (peer == null) return Vector3.zero;
            if (!(ConfigLivePlayerPositions == null || ConfigLivePlayerPositions.Value)) return peer.GetRefPos();

            ZDOID characterId = peer.m_characterID;
            if (characterId.IsNone()) return peer.GetRefPos();

            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null) return peer.GetRefPos();

            ZDO character = zdoMan.GetZDO(characterId);
            return character != null ? character.GetPosition() : peer.GetRefPos();
        }

        [HarmonyPatch(typeof(ZDOMan), "CreateSyncList")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> CreateSyncList_LiveRefPosTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var vanillaRefPos = AccessTools.Method(typeof(ZNetPeer), nameof(ZNetPeer.GetRefPos));
            var liveRefPos = AccessTools.Method(typeof(SyncListRefPosPatches), nameof(LiveRefPos));

            int swapped = 0;
            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Callvirt && code[i].opcode != OpCodes.Call) continue;
                if (!(code[i].operand is MethodInfo called) || called != vanillaRefPos) continue;
                code[i].opcode = OpCodes.Call;
                code[i].operand = liveRefPos;
                swapped++;
            }

            s_patched = swapped > 0;
            if (swapped == 1)
            {
                if (!s_announced)
                {
                    s_announced = true;
                    LoggerOptions.LogInfo("ZDOMan.CreateSyncList: each player's update is gathered and sorted around their live character position.");
                }
            }
            else if (swapped == 0)
                LoggerOptions.LogWarning("ZDOMan.CreateSyncList has no reference-position read this FGN version recognises (game update or another mod); "
                    + "players keep vanilla's 2-second-old reported position.");
            else
                LoggerOptions.LogWarning($"ZDOMan.CreateSyncList changed shape: {swapped} reference-position reads redirected where 1 was expected. "
                    + "Live positions still apply where attached.");
            return code;
        }

        public static bool LivePositionsAttached => s_patched;
    }
}
