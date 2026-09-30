using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Vanilla ZSteamSocket.Close sleeps the main thread 100 ms so the packets it just flushed can leave before it closes the
    /// connection without lingering. On a running server that is a 100 ms frame for every player at each disconnect (rig R15: a
    /// 205 ms frame, 158 ms of it blocked). While the server runs, the sleep is skipped and the connection closes with Steam's
    /// linger, which delivers the pending reliable data in the background instead. Clients, and a server that is shutting down,
    /// keep vanilla's sleep and close.
    /// </summary>
    [HarmonyPatch]
    internal static class CloseWithoutSleep
    {
        private const string CloseConnectionName = "CloseConnection";

        [HarmonyPatch(typeof(ZSteamSocket), nameof(ZSteamSocket.Close)), HarmonyTranspiler]
        static IEnumerable<CodeInstruction> SkipSleepWhileServing(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo sleep = AccessTools.Method(typeof(Thread), nameof(Thread.Sleep), new[] { typeof(int) });
            MethodInfo sleepUnlessServing = AccessTools.Method(typeof(CloseWithoutSleep), nameof(SleepUnlessServing));
            MethodInfo lingerWhileServing = AccessTools.Method(typeof(CloseWithoutSleep), nameof(LingerWhileServing));
            int sleeps = 0, lingers = 0;
            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].Calls(sleep))
                {
                    code[i].operand = sleepUnlessServing;
                    sleeps++;
                }
                else if (i > 0 && IsCloseConnection(code[i]) && code[i - 1].opcode == OpCodes.Ldc_I4_0)
                {
                    code[i - 1].opcode = OpCodes.Call;
                    code[i - 1].operand = lingerWhileServing;
                    lingers++;
                }
            }
            if (sleeps == 1 && lingers == 1) return code;
            LoggerOptions.LogWarning($"[Disconnect] ZSteamSocket.Close has {sleeps} sleep(s) and {lingers} close call(s) where 1 of each was "
                + "expected; left as it is, so each disconnect still holds the server's frame for 100 ms.");
            return instructions;
        }

        private static bool IsCloseConnection(CodeInstruction instruction)
        {
            if (!(instruction.operand is MethodInfo method) || method.Name != CloseConnectionName) return false;
            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length > 0 && parameters[parameters.Length - 1].ParameterType == typeof(bool);
        }

        private static bool Serving() => ZNet.instance != null && ZNet.instance.IsServer() && !ZNet.instance.HaveStopped;

        public static void SleepUnlessServing(int milliseconds)
        {
            if (!Serving()) Thread.Sleep(milliseconds);
        }

        public static bool LingerWhileServing() => Serving();
    }
}
