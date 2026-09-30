using System;
using System.Collections.Generic;
using System.Diagnostics;
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
                if (hash != 0 && !s_names.ContainsKey(hash)) NoteDropped(__instance, hash);
                if (s_dropped.Count > 0 && Time.unscaledTime >= s_nextDropCheck) ReportLateHandlers();
                s_handling = package;
                s_handlingAt = position;
                s_handlingHash = hash;
                s_handlingStart = Stopwatch.GetTimestamp();
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[Download] counting failed, stopping: {ex.Message}");
                Reset();
            }
        }

        // ---- how long each package's handler ran ----
        // A handler runs inside ZNet.Update, so a slow one reads in ServerHealth as "networking" (R34: an 808 ms dedi frame,
        // 546 ms "networking", of which FGN's own sending was 8 ms). Timed here and named, so the worst frame says whose it was.

        private const double SlowHandlerMs = 50.0;
        private const int SlowHandlersListed = 5;
        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
        private static readonly int RoutedRpcHash = "RoutedRPC".GetStableHashCode();

        private static ZPackage s_handling;
        private static int s_handlingAt;
        private static int s_handlingHash;
        private static long s_handlingStart;
        private static int s_slowestFrame = -1;
        private static double s_slowestMs;
        private static string s_slowestName;
        private static readonly Dictionary<string, (int Count, double MaxMs)> s_slowHandlers = new Dictionary<string, (int, double)>();

        [HarmonyPatch(typeof(ZRpc), "HandlePackage")]
        [HarmonyPostfix]
        public static void HandlePackage_Postfix(ZRpc __instance, ZPackage package)
        {
            if (s_handlingStart == 0L || !ReferenceEquals(package, s_handling)) return;
            double ms = (Stopwatch.GetTimestamp() - s_handlingStart) * MsPerTick;
            s_handlingStart = 0L;
            s_handling = null;
            try
            {
                int frame = Time.frameCount;
                if (frame != s_slowestFrame)
                {
                    s_slowestFrame = frame;
                    s_slowestMs = 0.0;
                    s_slowestName = null;
                }
                if (ms <= s_slowestMs && ms < SlowHandlerMs) return;
                string name = HandledName(__instance, package);
                if (ms > s_slowestMs)
                {
                    s_slowestMs = ms;
                    s_slowestName = name;
                }
                if (ms < SlowHandlerMs) return;
                s_slowHandlers.TryGetValue(name, out var seen);
                s_slowHandlers[name] = (seen.Count + 1, Math.Max(seen.MaxMs, ms));
            }
            catch
            {
                s_slowestName = null;
            }
        }

        /// <summary>The slowest handler of that frame and its time, e.g. "VAngarde.RPC_Log 412 ms"; null when none ran then.</summary>
        internal static string SlowestHandler(int frame) =>
            frame == s_slowestFrame && s_slowestName != null ? $"{s_slowestName} {s_slowestMs:F0} ms" : null;

        /// <summary>
        /// The handler's name, and for a routed RPC the routed method's own, read back from the package only for the slow ones.
        /// The package is spent by now, so its routed header is re-read from where the prefix saw it start.
        /// </summary>
        private static string HandledName(ZRpc rpc, ZPackage package)
        {
            string name = s_names.TryGetValue(s_handlingHash, out string known) ? known : s_handlingHash.ToString();
            if (s_handlingHash != RoutedRpcHash) return name;
            int end = package.GetPos();
            try
            {
                package.SetPos(s_handlingAt);
                package.ReadInt();
                ZPackage routed = package.ReadPackage();
                routed.ReadLong();
                routed.ReadLong();
                routed.ReadLong();
                routed.ReadZDOID();
                return "routed " + RoutedName(routed.ReadInt());
            }
            catch
            {
                return name;
            }
            finally
            {
                package.SetPos(end);
            }
        }

        private static FieldInfo s_routedFunctions;

        private static string RoutedName(int hash)
        {
            try
            {
                if (ZRoutedRpc.instance == null) return hash.ToString();
                s_routedFunctions = s_routedFunctions ?? AccessTools.Field(typeof(ZRoutedRpc), "m_functions");
                if (!(s_routedFunctions?.GetValue(ZRoutedRpc.instance) is System.Collections.IDictionary table) || !table.Contains(hash)) return hash.ToString();
                object method = table[hash];
                FieldInfo action = method?.GetType().GetField("m_action", BindingFlags.Instance | BindingFlags.NonPublic);
                return action?.GetValue(method) is Delegate handler ? $"{handler.Method.DeclaringType?.Name}.{handler.Method.Name}" : hash.ToString();
            }
            catch
            {
                return hash.ToString();
            }
        }

        private static IEnumerable<string> TakeSlowHandlers()
        {
            if (s_slowHandlers.Count == 0) yield break;
            yield return $"[Download] Handlers over {SlowHandlerMs:F0} ms: " + string.Join("; ", s_slowHandlers
                .OrderByDescending(entry => entry.Value.MaxMs)
                .Take(SlowHandlersListed)
                .Select(entry => $"{entry.Key} {entry.Value.Count}x, up to {entry.Value.MaxMs:F0} ms"));
            s_slowHandlers.Clear();
        }

        /// <summary>The report lines for everything counted since the last call, which starts the next period.</summary>
        internal static IEnumerable<string> TakeReport(string period)
        {
            if (s_totalCalls == 0) yield break;
            float seconds = Mathf.Max(1f, Time.unscaledTime - s_periodStart);
            yield return $"[Download] Received {period}: {s_totalBytes / BytesPerKilobyte:N0} KB in {s_totalCalls:N0} packages, "
                + $"{s_totalCalls / seconds:0} a second, {s_totalBytes / (float)BytesPerKilobyte / seconds:0.0} KB/s after decompression.";
            yield return "[Download] By RPC: " + Top();
            foreach (string line in TakeSlowHandlers()) yield return line;
            foreach (string line in TakeNeverRegistered()) yield return line;
            Reset();
        }

        // ---- packages for a handler that isn't registered (yet) ----
        // Vanilla ZRpc.HandlePackage drops such a package without a word. A mod that pushes data in the server's RPC_PeerInfo
        // frame loses it whenever the client registers its handler later in the join (Vedr's settings at the 02:45 Craters
        // join, 2026-09-28), and nothing in either log said so.

        private const float DropCheckIntervalSeconds = 1f;
        private const float NeverRegisteredAfterSeconds = 60f;
        private const int MaxTrackedDrops = 64;
        private const string VanillaAssembly = "assembly_valheim";

        private sealed class Dropped
        {
            public ZRpc Rpc;
            public int Count;
            public float FirstAt;
        }

        private static readonly Dictionary<int, Dropped> s_dropped = new Dictionary<int, Dropped>();
        private static float s_nextDropCheck;

        /// <summary>
        /// A hash TryName could not name is one ZRpc's table has no handler for, so the package is about to be dropped. Read
        /// from the name cache rather than the table itself, which would box the hash on every package.
        /// </summary>
        private static void NoteDropped(ZRpc rpc, int hash)
        {
            if (!s_dropped.TryGetValue(hash, out Dropped dropped))
            {
                if (s_dropped.Count >= MaxTrackedDrops) return;
                s_dropped[hash] = dropped = new Dropped { Rpc = rpc, FirstAt = Time.unscaledTime };
            }
            dropped.Count++;
        }

        /// <summary>Once a second: a dropped package whose handler has since been registered names the mod that lost it.</summary>
        private static void ReportLateHandlers()
        {
            float now = Time.unscaledTime;
            s_nextDropCheck = now + DropCheckIntervalSeconds;
            foreach (int hash in s_dropped.Keys.ToList())
            {
                Dropped dropped = s_dropped[hash];
                string name = NameOf(dropped.Rpc, hash);
                if (name == null) continue;
                s_dropped.Remove(hash);
                string line = $"[RpcDrops] {name}: {dropped.Count} package(s) arrived up to {now - dropped.FirstAt:0.0} s before this handler "
                    + "was registered, and Valheim dropped them.";
                MethodInfo handler = HandlerOf(dropped.Rpc, hash);
                bool vanilla = handler?.DeclaringType?.Assembly.GetName().Name == VanillaAssembly;
                if (vanilla && Array.IndexOf(VanillaResentOnATimer, handler.Name) >= 0)
                    LoggerOptions.LogInfo(line + " Valheim sends this again every couple of seconds, so nothing is lost.");
                else if (vanilla && Array.IndexOf(VanillaResentByFgnAfterSpawn, handler.Name) >= 0)
                    LoggerOptions.LogInfo(line + " Valheim sends this only at the join (player history: when it changes); an FGN 1.4.72+ "
                        + "server sends it again once the player has spawned, an older one leaves this client without it.");
                else if (vanilla)
                    LoggerOptions.LogMessage(line + " Valheim doesn't send this again on a timer, so this client is without it until it's next sent.");
                else
                    LoggerOptions.LogMessage(line + " If the mod sent it once while the player joined, it never arrived: that mod should send it "
                        + "later or have the client ask for it.");
            }
        }

        private static IEnumerable<string> TakeNeverRegistered()
        {
            float now = Time.unscaledTime;
            var old = s_dropped.Where(entry => now - entry.Value.FirstAt >= NeverRegisteredAfterSeconds).ToList();
            if (old.Count == 0) yield break;
            foreach (var entry in old) s_dropped.Remove(entry.Key);
            yield return "[RpcDrops] packages for a handler that was never registered here, dropped: "
                + string.Join("; ", old.Select(entry => $"{DescribeHash(entry.Key)} x{entry.Value.Count}"))
                + ". The sender runs a mod (or version) this side doesn't.";
        }

        private static string DescribeHash(int hash) =>
            hash == PacketFrame.Magic ? "an FGN frame that could not be decoded" : "#" + hash;

        // ZNet.SendPeriodicData sends these every couple of seconds; JoinGrace re-sends the other two after the first spawn.
        private static readonly string[] VanillaResentOnATimer = { "RPC_PlayerList", "RPC_NetTime" };
        private static readonly string[] VanillaResentByFgnAfterSpawn = { "RPC_AdminList", "RPC_HistoricalPlayerList" };

        private static MethodInfo HandlerOf(ZRpc rpc, int hash)
        {
            try
            {
                if (!(s_functions?.GetValue(rpc) is System.Collections.IDictionary table) || !table.Contains(hash)) return null;
                object method = table[hash];
                FieldInfo action = method?.GetType().GetField("m_action", BindingFlags.Instance | BindingFlags.NonPublic);
                return action?.GetValue(method) is Delegate handler ? handler.Method : null;
            }
            catch
            {
                return null;
            }
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
