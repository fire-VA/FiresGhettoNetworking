using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.83 ([fgn], Fire 11-09 single-player split): the client create queue and the streaming signal now live in EasyBakeMeshes (EbmApi). FGN relies on no other mod: this finds EbmApi
    /// by name in the loaded assemblies; without EBM the signal reads false and nothing is forwarded. Kept for one version so callers of the old names keep working.
    /// </summary>
    [HarmonyPatch]
    internal static class EbmBridge
    {
        private const string ApiType = "FiresEasyBakeMeshes.EbmApi";
        private static Type s_api;
        private static Func<bool> s_active;
        private static float s_nextLook;
        private static bool s_said;
        private static readonly List<KeyValuePair<string, Action<Vector2s>>> s_waiting = new List<KeyValuePair<string, Action<Vector2s>>>();

        private static Type Api()
        {
            if (s_api != null) return s_api;
            if (Time.unscaledTime < s_nextLook) return null;
            s_nextLook = Time.unscaledTime + 1f;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(ApiType, false);
                if (t == null) continue;
                s_api = t;
                MethodInfo getter = AccessTools.PropertyGetter(t, "StreamingSignalActive");
                if (getter != null) s_active = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter);
                foreach (var w in s_waiting) Hook(w.Key, w.Value, true);
                s_waiting.Clear();
                if (!s_said) { s_said = true; LoggerOptions.LogInfo("[EbmBridge] EasyBakeMeshes runs the client create queue; the old streaming names forward to EbmApi"); }
                break;
            }
            return s_api;
        }

        internal static bool Active { get { Api(); return s_active != null && s_active(); } }

        private static void Hook(string name, Action<Vector2s> handler, bool add)
        {
            try
            {
                EventInfo e = s_api.GetEvent(name, BindingFlags.Public | BindingFlags.Static);
                if (e == null) return;
                if (add) e.AddEventHandler(null, handler); else e.RemoveEventHandler(null, handler);
            }
            catch (Exception ex) { LoggerOptions.LogWarning($"[EbmBridge] {(add ? "subscribing to" : "leaving")} {name} threw: {(ex.InnerException ?? ex).Message}"); }
        }

        internal static void Subscribe(string name, Action<Vector2s> handler, bool add)
        {
            if (handler == null) return;
            if (Api() != null) { Hook(name, handler, add); return; }
            if (add) s_waiting.Add(new KeyValuePair<string, Action<Vector2s>>(name, handler));
            else s_waiting.RemoveAll(w => w.Key == name && w.Value == handler);
        }

        /// <summary>Calls a public static EbmApi method if it exists; false when EBM or the method is not here or it threw.</summary>
        internal static bool Call(string name, Type[] types, object[] args)
        {
            Type t = Api();
            MethodInfo m = t != null ? AccessTools.Method(t, name, types) : null;
            if (m == null) return false;
            try { m.Invoke(null, args); return true; }
            catch (Exception ex) { LoggerOptions.LogWarning($"[EbmBridge] EbmApi.{name} threw: {(ex.InnerException ?? ex).Message}"); return false; }
        }

        // all plugins are loaded by now: attach waiting subscribers and hand EBM the distant prefabs registered during load
        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix]
        private static void Attach()
        {
            s_nextLook = 0f;
            Api();
            DistantPrefabs.ForwardAll();
        }
    }

    /// <summary>1.6.83: kept for one version. The signal is EbmApi.StreamingSignalActive / ZoneFilling / ZoneSettled now; same meanings.</summary>
    public static class FgnStreaming
    {
        public static bool StreamingSignalActive => EbmBridge.Active;
        public static event Action<Vector2s> ZoneFilling { add => EbmBridge.Subscribe("ZoneFilling", value, true); remove => EbmBridge.Subscribe("ZoneFilling", value, false); }
        public static event Action<Vector2s> ZoneSettled { add => EbmBridge.Subscribe("ZoneSettled", value, true); remove => EbmBridge.Subscribe("ZoneSettled", value, false); }
    }
}
