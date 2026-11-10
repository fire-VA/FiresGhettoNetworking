using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.5.92 (lead 10-27: Fire standing still at the homestead, 8-12 FPS, overlay ZRoutedRpc.HandleRoutedRPC 33 ms/s "with nothing
    /// happening"): a Debug census of every routed RPC this peer HANDLES, once every CensusSeconds:
    ///   "[RpcCensus] last 5 s: N routed RPC(s), X ms; top: Handler (target prefab) xCount Y ms (from Z peer(s)); ..."
    /// One outermost timer around ZRoutedRpc.HandleRoutedRPC (First prefix / Last postfix, so every mod's patches on it count, FGN's
    /// own relay filters included). The name is the handler's own C# method (Type.Method), read once per method hash from the
    /// registered delegate (global RPCs: ZRoutedRpc.m_functions; object RPCs: the target ZNetView's m_functions), plus the target
    /// object's prefab for object RPCs, so a mod's chatter names its own code. Nothing is timed unless FGN Log Level = Debug.
    /// </summary>
    [HarmonyPatch]
    internal static class RpcCensus
    {
        private const float CensusSeconds = 5f;
        private const int TopN = 12;

        private sealed class Row { internal string Name; internal int Count; internal double Ms; internal HashSet<long> Senders = new HashSet<long>(); }

        private static readonly Dictionary<long, Row> s_rows = new Dictionary<long, Row>();   // key: method hash, + target prefab for object RPCs
        private static readonly Dictionary<long, string> s_names = new Dictionary<long, string>();
        private static long s_start;
        private static int s_depth;
        private static float s_next = -1f;
        private static int s_total;
        private static double s_totalMs;

        private static readonly FieldInfo s_globalFunctions = AccessTools.Field(typeof(ZRoutedRpc), "m_functions");
        private static readonly FieldInfo s_viewFunctions = AccessTools.Field(typeof(ZNetView), "m_functions");

        [HarmonyPatch(typeof(ZRoutedRpc), "HandleRoutedRPC"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Pre() { if (LoggerOptions.DebugEnabled && s_depth++ == 0) s_start = Stopwatch.GetTimestamp(); }

        [HarmonyPatch(typeof(ZRoutedRpc), "HandleRoutedRPC"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Post(ZRoutedRpc __instance, ZRoutedRpc.RoutedRPCData data)
        {
            if (!LoggerOptions.DebugEnabled) { s_depth = 0; s_next = -1f; return; }
            if (--s_depth > 0) return;
            s_depth = 0;
            if (s_start == 0 || data == null) return;
            double ms = (Stopwatch.GetTimestamp() - s_start) * 1000.0 / Stopwatch.Frequency;
            s_start = 0;
            try
            {
                int prefab = 0;
                ZNetView view = null;
                if (!data.m_targetZDO.IsNone())
                {
                    ZDO zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(data.m_targetZDO) : null;
                    if (zdo != null) { prefab = zdo.GetPrefab(); view = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null; }
                }
                long key = ((long)data.m_methodHash << 32) ^ (uint)prefab;
                if (!s_rows.TryGetValue(key, out Row row)) { row = new Row { Name = NameOf(__instance, view, data.m_methodHash, prefab) }; s_rows[key] = row; }
                row.Count++; row.Ms += ms; row.Senders.Add(data.m_senderPeerID);
                s_total++; s_totalMs += ms;
            }
            catch (Exception ex) { LoggerOptions.LogDebug($"[RpcCensus] could not record an RPC: {ex.GetType().Name}"); }
            Report();
        }

        private static void Report()
        {
            float now = Time.realtimeSinceStartup;
            if (s_next < 0f) { s_next = now + CensusSeconds; Clear(); return; }
            if (now < s_next) return;
            s_next = now + CensusSeconds;
            var sb = new StringBuilder();
            foreach (Row r in s_rows.Values.OrderByDescending(r => r.Ms).Take(TopN))
                sb.Append($" {r.Name} x{r.Count} {r.Ms:0.0} ms (from {r.Senders.Count} peer(s));");
            LoggerOptions.LogDebug($"[RpcCensus] last {CensusSeconds:0} s: {s_total} routed RPC(s), {s_totalMs:0.0} ms; top by time:{sb}");
            Clear();
        }

        private static void Clear() { s_rows.Clear(); s_total = 0; s_totalMs = 0; }

        // "Type.Method" of the registered handler (+ " on <prefab>" for an object RPC), cached per hash; the hash when unresolvable
        private static string NameOf(ZRoutedRpc rpc, ZNetView view, int hash, int prefab)
        {
            long key = ((long)hash << 32) ^ (uint)prefab;
            if (s_names.TryGetValue(key, out string cached)) return cached;
            string handler = null;
            object table = view != null ? s_viewFunctions?.GetValue(view) : s_globalFunctions?.GetValue(rpc);
            if (table is System.Collections.IDictionary dict && dict.Contains(hash))
            {
                object method = dict[hash];
                var action = method != null ? AccessTools.Field(method.GetType(), "m_action")?.GetValue(method) as Delegate : null;
                if (action != null) handler = $"{action.Method.DeclaringType?.Name}.{action.Method.Name}";
            }
            string prefabName = null;
            if (prefab != 0 && ZNetScene.instance != null) { GameObject p = ZNetScene.instance.GetPrefab(prefab); prefabName = p != null ? p.name : prefab.ToString(); }
            // 1.6.62 (lead / Fire 11-08): no handler on the target (not loaded here, or it never registered that RPC) -> the name from the
            // names this peer has SEEN in use (sent through InvokeRoutedRPC, or registered through a non-generic Register). Nothing reads
            // any assembly's code. Never seen here: the row stays "hash N".
            string where = view == null ? (prefab != 0 ? " (target not loaded here)" : "") : " (target has NO handler for it)";
            string seen = handler == null ? SeenName(hash) : null;
            string label = handler ?? (seen != null ? $"'{seen}'{where}" : $"hash {hash}{where}");
            string name = label + (prefabName != null ? $" on {prefabName}" : "");
            if (handler != null || seen != null) s_names[key] = name;
            return name;
        }

        // ---- 1.6.62: observed RPC names (hash -> name), bounded; filled only while FGN Log Level = Debug (the census is Debug-only) ----
        private const int SeenCap = 4096;
        private static readonly Dictionary<int, string> s_seen = new Dictionary<int, string>();
        private static bool s_saidSeen;

        private static string SeenName(int hash)
        {
            if (!s_saidSeen) { s_saidSeen = true; LoggerOptions.LogInfo($"[RpcCensus] RPC names for handlers not on this peer come from the names seen in use here (sent or registered), {s_seen.Count} so far, at most {SeenCap}"); }
            return s_seen.TryGetValue(hash, out string s) ? s : null;
        }

        private static void Note(string name)
        {
            if (!LoggerOptions.DebugEnabled || string.IsNullOrEmpty(name) || s_seen.Count >= SeenCap) return;
            int h = name.GetStableHashCode();
            if (!s_seen.ContainsKey(h)) s_seen[h] = name;
        }

        // Observe-only postfixes: they never skip, replace or change the call (other mods hook these too).
        // Every send funnels into this overload (ZNetView.InvokeRPC and the shorter InvokeRoutedRPC overloads call it).
        [HarmonyPatch(typeof(ZRoutedRpc), nameof(ZRoutedRpc.InvokeRoutedRPC), new[] { typeof(long), typeof(ZDOID), typeof(string), typeof(object[]) }), HarmonyPostfix]
        private static void AfterInvoke(string methodName) => Note(methodName);

        // The non-generic Register(string, Action<long>) of ZRoutedRpc and ZNetView, found by a TargetMethod lookup: a generic argument
        // in a [HarmonyPatch] attribute can be misread by a store checker (as FGN 1.6.51 did for Fast Leave Cleanup).
        [HarmonyPatch]
        private static class GlobalRegister
        {
            private static bool Prepare() => PlainRegister(typeof(ZRoutedRpc)) != null;   // not found: this observer is skipped, nothing else
            private static MethodBase TargetMethod() => PlainRegister(typeof(ZRoutedRpc));
            [HarmonyPostfix] private static void Postfix(string name) => Note(name);
        }

        [HarmonyPatch]
        private static class ViewRegister
        {
            private static bool Prepare() => PlainRegister(typeof(ZNetView)) != null;
            private static MethodBase TargetMethod() => PlainRegister(typeof(ZNetView));
            [HarmonyPostfix] private static void Postfix(string name) => Note(name);
        }

        private static MethodBase PlainRegister(Type owner)
        {
            foreach (MethodInfo m in owner.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "Register" || m.IsGenericMethodDefinition) continue;
                var p = m.GetParameters();
                if (p.Length == 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(Action<long>)) return m;
            }
            LoggerOptions.LogInfo($"[RpcCensus] {owner.Name}.Register(string, Action<long>) not found on this build: names registered there are not observed");
            return null;
        }
    }
}
