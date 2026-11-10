using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Vanilla Pickable.RPC_Pick passes Player.m_localPlayer.GetZDOID() to the pick effect. A
    /// dedicated server has no local player, so when the server owns a Pickable (server-side simulation spawned it, or an older
    /// FGN kept it) and runs the pick RPC, it would throw a NullReferenceException and nothing would be picked. Vanilla never runs this on a
    /// server because clients always own pickables; ServerOwnershipPatchesV3 hands such objects back to the covering player,
    /// and this is the safety net: the one local-player read becomes "the local player's ZDOID, or none", so the server can pick.
    /// </summary>
    [HarmonyPatch]
    internal static class ServerSafePick
    {
        private static readonly FieldInfo s_localPlayer = AccessTools.Field(typeof(Player), nameof(Player.m_localPlayer));
        private static readonly MethodInfo s_getZdoid = AccessTools.Method(typeof(Character), nameof(Character.GetZDOID));
        private static readonly MethodInfo s_safe = AccessTools.Method(typeof(ServerSafePick), nameof(LocalPlayerZdoid));
        private static bool s_logged;

        [HarmonyPatch(typeof(Pickable), "RPC_Pick"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> RpcPickTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            int replaced = 0;
            for (int i = 0; i < list.Count - 1; i++)
            {
                if (list[i].LoadsField(s_localPlayer) && list[i + 1].Calls(s_getZdoid))
                {
                    list[i].opcode = OpCodes.Call;
                    list[i].operand = s_safe;
                    list[i + 1].opcode = OpCodes.Nop;
                    list[i + 1].operand = null;
                    replaced++;
                }
            }
            LoggerOptions.LogDebug(replaced > 0
                ? $"[ServerSafePick] Pickable.RPC_Pick made server-safe ({replaced} local-player read(s))."
                : "[ServerSafePick] Pickable.RPC_Pick: the local-player read was not found (game update?); left as vanilla.");
            return list;
        }

        private static ZDOID LocalPlayerZdoid()
        {
            Player p = Player.m_localPlayer;
            if (p != null) return p.GetZDOID();
            if (!s_logged)
            {
                s_logged = true;
                LoggerOptions.LogDebug("[ServerSafePick] the server ran a pick RPC (it owned the Pickable); picked without a local player.");
            }
            return ZDOID.None;
        }
    }
}
