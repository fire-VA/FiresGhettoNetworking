using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// What this client sends the server, so an upload that outweighs the download can be traced to what sends it: bytes per
    /// RPC from vanilla's own ZRpc sent counter, RoutedRPC by the method it calls, and ZDOData by object prefab, read from the
    /// package ZDOMan.SendZDOs hands to ZRpc.Invoke. Logged with the [Compression] report. CLIENT-SIDE.
    /// </summary>
    [HarmonyPatch]
    public static class UploadBreakdown
    {
        private const int ListedEntries = 8;
        private const long BytesPerKilobyte = 1024;
        private const string RoutedRpcName = "RoutedRPC";
        private const string ZdoDataName = "ZDOData";
        private const int ZdoIdBytes = 12;
        // After its ZDOID, each object record carries its owner revision, data revision, owner and position.
        private const int RecordFieldsAfterIdBytes = 2 + 4 + 8 + 12;
        private const int LengthPrefixBytes = 4;
        private const int ObjectRecordOverheadBytes = ZdoIdBytes + RecordFieldsAfterIdBytes + LengthPrefixBytes;

        private sealed class Usage
        {
            public long Bytes;
            public int Sends;
        }

        private static readonly Dictionary<string, Usage> s_byRpc = new Dictionary<string, Usage>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Usage> s_byRoutedMethod = new Dictionary<string, Usage>(StringComparer.Ordinal);
        private static readonly Dictionary<int, Usage> s_byPrefab = new Dictionary<int, Usage>();
        private static readonly Stack<string> s_routedMethods = new Stack<string>();
        private static long s_totalBytes;
        private static int s_totalSends;
        private static float s_periodStart = -1f;

        [HarmonyPatch(typeof(ZRpc), nameof(ZRpc.Invoke)), HarmonyPrefix]
        static void BeforeInvoke(int ___m_sentData, out int __state) => __state = ___m_sentData;

        [HarmonyPatch(typeof(ZRpc), nameof(ZRpc.Invoke)), HarmonyPostfix]
        static void AfterInvoke(string method, object[] parameters, int ___m_sentData, int __state)
        {
            int bytes = ___m_sentData - __state;
            if (bytes <= 0 || ZNet.instance == null || ZNet.instance.IsServer()) return;
            if (s_periodStart < 0f) s_periodStart = Time.unscaledTime;
            s_totalBytes += bytes;
            s_totalSends++;
            Add(s_byRpc, method, bytes);
            if (method == RoutedRpcName)
            {
                if (s_routedMethods.Count > 0) Add(s_byRoutedMethod, s_routedMethods.Peek(), bytes);
            }
            else if (method == ZdoDataName && parameters != null && parameters.Length > 0 && parameters[0] is ZPackage objects)
            {
                CountObjects(objects);
            }
        }

        [HarmonyPatch(typeof(ZRoutedRpc), nameof(ZRoutedRpc.InvokeRoutedRPC), typeof(long), typeof(ZDOID), typeof(string), typeof(object[]))]
        [HarmonyPrefix]
        static void BeforeRoutedInvoke(string methodName) => s_routedMethods.Push(methodName);

        [HarmonyPatch(typeof(ZRoutedRpc), nameof(ZRoutedRpc.InvokeRoutedRPC), typeof(long), typeof(ZDOID), typeof(string), typeof(object[]))]
        [HarmonyFinalizer]
        static void AfterRoutedInvoke()
        {
            if (s_routedMethods.Count > 0) s_routedMethods.Pop();
        }

        /// <summary>
        /// The report lines for everything counted since the last call, which starts the next period. Nothing on a server.
        /// </summary>
        internal static IEnumerable<string> TakeReport(string period)
        {
            if (s_totalSends == 0) yield break;
            float seconds = Mathf.Max(1f, Time.unscaledTime - s_periodStart);
            yield return $"[Upload] Sent to the server {period}: {Kb(s_totalBytes)} KB in {s_totalSends:N0} RPCs, "
                + $"{s_totalBytes / (float)BytesPerKilobyte / seconds:0.0} KB/s before compression.";
            yield return "[Upload] By RPC: " + Top(s_byRpc, rpc => rpc);
            if (s_byPrefab.Count > 0) yield return "[Upload] ZDOData by object prefab: " + Top(s_byPrefab, PrefabName);
            if (s_byRoutedMethod.Count > 0) yield return "[Upload] RoutedRPC by method: " + Top(s_byRoutedMethod, routed => routed);
            string delta = ZDODeltaPatches.TakeReportLine();
            if (delta != null) yield return delta;
            string fires = FireplaceFuelTicks.TakeReportLine();
            if (fires != null) yield return fires;
            string writes = TransformWriteRate.TakeReportLine();
            if (writes != null) yield return writes;
            StartPeriod();
        }

        // The package ZDOMan.SendZDOs built: the invalidated sectors' ZDOIDs behind their count, then one record per object
        // (ZDOID, the fields after it, and its serialized data behind a length), ending at ZDOID.None. It was already copied
        // into the RPC, so reading it moves nothing that is still used.
        private static void CountObjects(ZPackage package)
        {
            int size = package.Size();
            package.SetPos(0);
            if (size < LengthPrefixBytes) return;
            int invalidated = package.ReadInt();
            int position = package.GetPos() + invalidated * ZdoIdBytes;
            var zdoMan = ZDOMan.instance;
            while (invalidated >= 0 && position + ZdoIdBytes <= size)
            {
                package.SetPos(position);
                ZDOID id = package.ReadZDOID();
                if (id.IsNone()) return;
                int lengthAt = package.GetPos() + RecordFieldsAfterIdBytes;
                if (lengthAt + LengthPrefixBytes > size) return;
                package.SetPos(lengthAt);
                int dataBytes = package.ReadInt();
                if (dataBytes < 0 || lengthAt + LengthPrefixBytes + dataBytes > size) return;
                ZDO zdo = zdoMan != null ? zdoMan.GetZDO(id) : null;
                Add(s_byPrefab, zdo != null ? zdo.GetPrefab() : 0, ObjectRecordOverheadBytes + dataBytes);
                position = lengthAt + LengthPrefixBytes + dataBytes;
            }
        }

        private static void Add<TKey>(Dictionary<TKey, Usage> table, TKey key, int bytes)
        {
            if (!table.TryGetValue(key, out var usage)) table[key] = usage = new Usage();
            usage.Bytes += bytes;
            usage.Sends++;
        }

        private static void StartPeriod()
        {
            s_byRpc.Clear();
            s_byRoutedMethod.Clear();
            s_byPrefab.Clear();
            s_totalBytes = 0;
            s_totalSends = 0;
            s_periodStart = Time.unscaledTime;
        }

        private static string Top<TKey>(Dictionary<TKey, Usage> table, Func<TKey, string> nameOf)
        {
            var ordered = new List<KeyValuePair<TKey, Usage>>(table);
            ordered.Sort((left, right) => right.Value.Bytes.CompareTo(left.Value.Bytes));
            var entries = new StringBuilder();
            for (int i = 0; i < ordered.Count && i < ListedEntries; i++)
            {
                var usage = ordered[i].Value;
                if (i > 0) entries.Append("; ");
                entries.Append($"{nameOf(ordered[i].Key)} {Kb(usage.Bytes)} KB ({usage.Bytes * 100.0 / s_totalBytes:0}%) in {usage.Sends:N0}, "
                    + $"{usage.Bytes / usage.Sends:N0} B each");
            }
            if (ordered.Count > ListedEntries) entries.Append($"; {ordered.Count - ListedEntries} more");
            return entries.ToString();
        }

        private static string PrefabName(int prefabHash)
        {
            if (prefabHash == 0) return "objects no longer loaded";
            var scene = ZNetScene.instance;
            var prefab = scene != null ? scene.GetPrefab(prefabHash) : null;
            return prefab != null ? prefab.name : $"prefab {prefabHash}";
        }

        private static string Kb(long bytes) => ((bytes + BytesPerKilobyte / 2) / BytesPerKilobyte).ToString("N0");
    }
}
