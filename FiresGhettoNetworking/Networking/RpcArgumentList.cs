using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.39 ([fgn], Fire 11-05, group B, first part of "ZRpc invoke"): every received RPC, direct (ZRpc.Deserialize) or routed
    /// (ZNetView.Deserialize), builds a new List of its arguments (growing it past 4) only to copy it into an array. Here one kept list
    /// is filled by vanilla's own reader (ZRpc.Deserialize(paramInfo, pkg, ref list)), so every argument is read exactly as before, and
    /// copied out the same way. Main thread; a nested call (a mod's reader calling it again) gets vanilla's own list. The rest of the
    /// row, DynamicInvoke and its boxing, is not touched here. Not A/B proven yet.
    /// </summary>
    [HarmonyPatch]
    internal static class RpcArgumentList
    {
        internal static ConfigEntry<bool> ConfigEnabled;
        private static readonly List<object> s_list = new List<object>(8);
        private static bool s_busy;

        internal static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("02 - Client Performance", "Reuse RPC Argument Lists", true,
                Cfg.Adv("Reading the values each network message carries reuses one list instead of building a new one per message. The values read are the same."));
        }

        [HarmonyPatch(typeof(ZRpc), nameof(ZRpc.Deserialize), new[] { typeof(ZRpc), typeof(ParameterInfo[]), typeof(ZPackage) }), HarmonyPrefix]
        private static bool Direct(ZRpc rpc, ParameterInfo[] paramInfo, ZPackage pkg, ref object[] __result) => Read(rpc, paramInfo, pkg, ref __result);

        [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.Deserialize), new[] { typeof(long), typeof(ParameterInfo[]), typeof(ZPackage) }), HarmonyPrefix]
        private static bool Routed(long callerID, ParameterInfo[] paramInfo, ZPackage pkg, ref object[] __result) => Read(callerID, paramInfo, pkg, ref __result);

        private static bool Read(object first, ParameterInfo[] paramInfo, ZPackage pkg, ref object[] result)
        {
            if (!ConfigEnabled.Value || s_busy) return true;
            s_busy = true;
            List<object> list = s_list;
            try
            {
                list.Clear();
                list.Add(first);
                ZRpc.Deserialize(paramInfo, pkg, ref list);
                result = list.ToArray();
            }
            finally
            {
                s_list.Clear();   // no message's values stay referenced
                s_busy = false;
            }
            return false;
        }
    }
}
