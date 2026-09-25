using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// What arrives, counted the way UploadBreakdown counts what leaves: every package ZRpc dispatches, by the handler
    /// it reaches. A profile measured 1.94 M dispatches in 14.3 minutes on one client, about 2,260 a second, and the
    /// wire carries only a hash, so the names come from ZRpc's own function table the first time each hash is seen.
    /// </summary>
    [HarmonyPatch]
    public static class DownloadBreakdown
    {
        private const int TopEntries = 6;
        private const int BytesPerKilobyte = 1024;
        private const string PingName = "ping/pong";
        private const int NameAttempts = 32;

        private static readonly Dictionary<int, long> s_bytes = new Dictionary<int, long>();
        private static readonly Dictionary<int, long> s_calls = new Dictionary<int, long>();
        private static readonly Dictionary<int, string> s_names = new Dictionary<int, string>();
        private static readonly Dictionary<int, int> s_nameAttempts = new Dictionary<int, int>();
        private static float s_periodStart;
        private static long s_totalBytes;
        private static long s_totalCalls;

        private static FieldInfo s_functions;

        [HarmonyPatch(typeof(ZRpc), "HandlePackage")]
        [HarmonyPrefix]
        public static void HandlePackage_Prefix(ZRpc __instance, ZPackage package)
        {
            try
            {
                int position = package.GetPos();
                int hash = package.ReadInt();
                package.SetPos(position);

                s_totalCalls++;
                s_totalBytes += package.Size();
                s_calls.TryGetValue(hash, out long calls);
                s_calls[hash] = calls + 1;
                s_bytes.TryGetValue(hash, out long bytes);
                s_bytes[hash] = bytes + package.Size();
                TryName(__instance, hash);
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[Download] counting failed, stopping: {ex.Message}");
                Reset();
            }
        }

        /// <summary>The report lines for everything counted since the last call, which starts the next period.</summary>
        internal static IEnumerable<string> TakeReport(string period)
        {
            if (s_totalCalls == 0) yield break;
            float seconds = Mathf.Max(1f, Time.unscaledTime - s_periodStart);
            yield return $"[Download] Received {period}: {s_totalBytes / BytesPerKilobyte:N0} KB in {s_totalCalls:N0} packages, "
                + $"{s_totalCalls / seconds:0} a second, {s_totalBytes / (float)BytesPerKilobyte / seconds:0.0} KB/s after decompression.";
            yield return "[Download] By RPC: " + Top();
            Reset();
        }

        private static string Top()
        {
            return string.Join("; ", s_calls
                .OrderByDescending(entry => entry.Value)
                .Take(TopEntries)
                .Select(entry =>
                {
                    long bytes = s_bytes.TryGetValue(entry.Key, out long b) ? b : 0L;
                    string name = s_names.TryGetValue(entry.Key, out string n) ? n : entry.Key.ToString();
                    return $"{name} {entry.Value:N0} ({entry.Value * 100 / Math.Max(1L, s_totalCalls)}%), {bytes / BytesPerKilobyte:N0} KB";
                }));
        }

        private static void Reset()
        {
            s_bytes.Clear();
            s_calls.Clear();
            s_totalBytes = 0;
            s_totalCalls = 0;
            s_periodStart = Time.unscaledTime;
        }

        // A package can arrive before its handler is registered — vanilla's own PlayerList does, right behind PeerInfo —
        // so a failed lookup is retried on later sightings instead of being cached as a number for the session.
        private static void TryName(ZRpc rpc, int hash)
        {
            if (s_names.ContainsKey(hash)) return;
            s_nameAttempts.TryGetValue(hash, out int attempts);
            if (attempts >= NameAttempts) return;
            string name = NameOf(rpc, hash);
            if (name == null) s_nameAttempts[hash] = attempts + 1;
            else s_names[hash] = name;
        }

        // The wire carries the hash; ZRpc's own table holds the delegate it dispatches to, so the handler names itself.
        // Returns null when the table has no entry yet, so the caller can ask again later.
        private static string NameOf(ZRpc rpc, int hash)
        {
            if (hash == 0) return PingName;
            try
            {
                // Read as the non-generic IDictionary: the table's value type is ZRpc's own private interface.
                if (s_functions == null) s_functions = AccessTools.Field(typeof(ZRpc), "m_functions");
                if (!(s_functions?.GetValue(rpc) is System.Collections.IDictionary table) || !table.Contains(hash)) return null;
                object method = table[hash];
                if (method == null) return null;
                FieldInfo action = method.GetType().GetField("m_action", BindingFlags.Instance | BindingFlags.NonPublic);
                if (!(action?.GetValue(method) is Delegate handler)) return method.GetType().Name;
                return $"{handler.Method.DeclaringType?.Name}.{handler.Method.Name}";
            }
            catch
            {
                return null;
            }
        }
    }
}
