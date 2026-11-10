using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.5.91 (lead 10-27, Fire hand-tests the border hitch walking in and out of range of a big base): two Debug-only lines on a
    /// client, so what Fire feels can be lined up with what FGN spends.
    ///  * once per zone border the local player crosses, written CrossingWindowSeconds later:
    ///    "[ZoneLoad] crossing into x,y: created C / destroyed D objects over 2 s, CreateObjects X ms, RemoveObjects Y ms, worst frame Z ms"
    ///    (C / D from ZNetScene.m_instances around each call; the times are the outermost wrappers, so every mod's patches on those
    ///    methods are included);
    ///  * every SplitSeconds: "[NetSplit] last 5 s: ZNet.Update X ms (whole), inside it SendZDOs S ms, HandlePackage R ms; FGN compress
    ///    C ms, decompress D ms" (ZNet.Update and SendZDOs include vanilla; compress / decompress are FGN's own, from its counters).
    /// Nothing is timed unless LoggerOptions.DebugEnabled (one bool check a call); never on a dedicated server.
    /// </summary>
    [HarmonyPatch]
    internal static class ZoneLoadDiag
    {
        private const float CrossingWindowSeconds = 2f, SplitSeconds = 5f;
        private static bool On => LoggerOptions.DebugEnabled && !ServerClientUtils.ZNetIsDedicated();

        // ---- the network split ----
        private static long s_netStart, s_sendStart, s_recvStart;
        private static int s_recvDepth;
        private static double s_netMs, s_sendMs, s_recvMs;
        private static long s_encodeAt, s_decodeAt;
        private static float s_nextSplit = -1f;

        [HarmonyPatch(typeof(ZNet), "Update"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void NetPre() { if (On) s_netStart = Stopwatch.GetTimestamp(); }

        [HarmonyPatch(typeof(ZNet), "Update"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void NetPost()
        {
            if (!On) { s_nextSplit = -1f; return; }
            if (s_netStart != 0) { double nms = (Stopwatch.GetTimestamp() - s_netStart) * 1000.0 / Stopwatch.Frequency; s_netMs += nms; }
            s_netStart = 0;
            float now = Time.realtimeSinceStartup;
            
            if (s_nextSplit < 0f) { s_nextSplit = now + SplitSeconds; ResetSplit(); return; }
            if (now < s_nextSplit) return;
            s_nextSplit = now + SplitSeconds;
            double enc = (CompressionGroup.SessionEncodeTicks - s_encodeAt) * 1000.0 / Stopwatch.Frequency;
            double dec = (CompressionGroup.SessionDecodeTicks - s_decodeAt) * 1000.0 / Stopwatch.Frequency;
            LoggerOptions.LogDebug($"[NetSplit] last {SplitSeconds:0} s: ZNet.Update {s_netMs:0.0} ms (whole, all mods), inside it ZDOMan.SendZDOs {s_sendMs:0.0} ms, " +
                                   $"ZRpc.HandlePackage {s_recvMs:0.0} ms; FGN's own compress {enc:0.0} ms, decompress {dec:0.0} ms; " +
                                   $"ZDOMan.Update {s_subMs[0]:0.0} ms, UpdateSave {s_subMs[1]:0.0} ms, UpdatePeers {s_subMs[2]:0.0} ms, " +
                                   $"Steam sockets {s_subMs[3]:0.0} ms, PlayFab sockets {s_subMs[4]:0.0} ms, rest {s_netMs - s_subMs[0] - s_subMs[1] - s_subMs[2] - s_subMs[3] - s_subMs[4]:0.0} ms " +
                                   $"(server {(ZNet.instance != null && ZNet.instance.IsServer() ? "yes" : "no")}, zdos {(ZDOMan.instance != null ? ZDOMan.instance.m_objectsByID.Count : 0)})");
            ResetSplit();
            
        }

        private static void ResetSplit()
        {
            s_netMs = s_sendMs = s_recvMs = 0;
            System.Array.Clear(s_subMs, 0, s_subMs.Length);
            s_encodeAt = CompressionGroup.SessionEncodeTicks; s_decodeAt = CompressionGroup.SessionDecodeTicks;
        }

        // 1.6.12 ([lead] 10-29, round S: ZNet.Update ~40 ms/s on a local host with SendZDOs / HandlePackage at 0): the other calls
        // vanilla ZNet.Update makes (ZNet.cs:1326-1340), each timed whole (all mods' patches included), Debug only
        private static readonly double[] s_subMs = new double[5];
        private static readonly long[] s_subStart = new long[5];
        private static void SubPre(int i) { if (On) s_subStart[i] = Stopwatch.GetTimestamp(); }
        private static void SubPost(int i) { if (On && s_subStart[i] != 0) s_subMs[i] += (Stopwatch.GetTimestamp() - s_subStart[i]) * 1000.0 / Stopwatch.Frequency; s_subStart[i] = 0; }

        [HarmonyPatch(typeof(ZDOMan), "Update"), HarmonyPrefix, HarmonyPriority(Priority.First)] private static void ZdoManPre() => SubPre(0);
        [HarmonyPatch(typeof(ZDOMan), "Update"), HarmonyPostfix, HarmonyPriority(Priority.Last)] private static void ZdoManPost() => SubPost(0);
        [HarmonyPatch(typeof(ZNet), "UpdateSave"), HarmonyPrefix, HarmonyPriority(Priority.First)] private static void SavePre() => SubPre(1);
        [HarmonyPatch(typeof(ZNet), "UpdateSave"), HarmonyPostfix, HarmonyPriority(Priority.Last)] private static void SavePost() => SubPost(1);
        [HarmonyPatch(typeof(ZNet), "UpdatePeers"), HarmonyPrefix, HarmonyPriority(Priority.First)] private static void PeersPre() => SubPre(2);
        [HarmonyPatch(typeof(ZNet), "UpdatePeers"), HarmonyPostfix, HarmonyPriority(Priority.Last)] private static void PeersPost() => SubPost(2);
        // Steam-only (the Xbox build has no ZSteamSocket): its own class, attached by Ascend only when SteamSide.Present, so reading
        // ZoneLoadDiag's attributes never resolves ZSteamSocket.
        [HarmonyPatch]
        internal static class SteamPatch
        {
            [HarmonyPatch(typeof(ZSteamSocket), "UpdateAllSockets"), HarmonyPrefix, HarmonyPriority(Priority.First)] private static void SteamPre() => SubPre(3);
            [HarmonyPatch(typeof(ZSteamSocket), "UpdateAllSockets"), HarmonyPostfix, HarmonyPriority(Priority.Last)] private static void SteamPost() => SubPost(3);
        }
        [HarmonyPatch(typeof(ZPlayFabSocket), "UpdateAllSockets"), HarmonyPrefix, HarmonyPriority(Priority.First)] private static void PlayFabPre() => SubPre(4);
        [HarmonyPatch(typeof(ZPlayFabSocket), "UpdateAllSockets"), HarmonyPostfix, HarmonyPriority(Priority.Last)] private static void PlayFabPost() => SubPost(4);

        [HarmonyPatch(typeof(ZDOMan), "SendZDOs"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void SendPre() { if (On) s_sendStart = Stopwatch.GetTimestamp(); }

        [HarmonyPatch(typeof(ZDOMan), "SendZDOs"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void SendPost() { if (On && s_sendStart != 0) s_sendMs += (Stopwatch.GetTimestamp() - s_sendStart) * 1000.0 / Stopwatch.Frequency; s_sendStart = 0; }

        // HandlePackage can nest (an RPC handler sending), so only the outermost call is timed
        [HarmonyPatch(typeof(ZRpc), "HandlePackage"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void RecvPre() { if (On && s_recvDepth++ == 0) s_recvStart = Stopwatch.GetTimestamp(); }

        [HarmonyPatch(typeof(ZRpc), "HandlePackage"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void RecvPost()
        {
            if (!On) { s_recvDepth = 0; return; }
            if (--s_recvDepth <= 0) { s_recvDepth = 0; if (s_recvStart != 0) s_recvMs += (Stopwatch.GetTimestamp() - s_recvStart) * 1000.0 / Stopwatch.Frequency; s_recvStart = 0; }
        }

            }
}
