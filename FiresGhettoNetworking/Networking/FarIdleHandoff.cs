using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.82 ([fgn], Fire 11-09 single-player split): "Slow Ticks For Far Idle Creatures" now lives in FiresFixes, which owns the job. FGN relies on no other mod: if FiresFixes'
    /// public FixesApi is there, FGN tells it about companion keep-alive areas (they stay here) and forwards FgnApi.RegisterFarIdleSkip; if not, nothing is slowed and nothing else changes.
    /// </summary>
    [HarmonyPatch]
    internal static class FarIdleHandoff
    {
        private const string ApiType = "FiresFixes.Api.FixesApi";
        private static bool s_registered;

        private static MethodInfo Find(string name, params Type[] args)
        {
            // 1.6.83 ([generator] review): the loaded assemblies are asked directly, so a missing FiresFixes prints nothing from Harmony's type search
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(ApiType, false);
                if (t != null) return AccessTools.Method(t, name, args);
            }
            return null;
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix]
        private static void RegisterKeepAlive()
        {
            if (s_registered) return;
            s_registered = true;
            MethodInfo m = Find("RegisterAreaKeepAlive", typeof(string), typeof(Func<Vector3, bool>));
            if (m == null) { LoggerOptions.LogInfo("[FarIdle] FiresFixes' keep-alive API is not here: far idle creature ticks are not running"); return; }
            Func<Vector3, bool> covers = p => CompanionKeepAlive.Covers(FgnRing.Sector(p), SimDistance.Near());
            try { m.Invoke(null, new object[] { "FiresGhettoNetworking", covers }); }   // 1.6.83 ([generator] review): inside a ZNetScene.Awake postfix
            catch (Exception e) { LoggerOptions.LogWarning("[FarIdle] registering keep-alive areas with FiresFixes threw: " + (e.InnerException ?? e).Message); return; }
            LoggerOptions.LogInfo("[FarIdle] companion keep-alive areas registered with FiresFixes, which runs far idle creature ticks");
        }

        internal static bool ForwardSkip(string owner, Func<Character, bool> test)
        {
            MethodInfo m = Find("RegisterCreatureSkip", typeof(string), typeof(Func<Character, bool>));
            if (m == null) { LoggerOptions.LogDebug($"[FarIdle] RegisterFarIdleSkip from {owner}: FiresFixes is not here, so nothing is slowed"); return false; }
            return (bool)m.Invoke(null, new object[] { owner, test });
        }

        internal static bool ForwardRemove(string owner)
        {
            MethodInfo m = Find("UnregisterCreatureSkip", typeof(string));
            return m != null && (bool)m.Invoke(null, new object[] { owner });
        }
    }
}
