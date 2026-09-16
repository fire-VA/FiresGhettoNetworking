using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Times each player's round trip from the server's side. The server stamps a small echo, the player's FGN bounces it
    /// straight back, and the server measures the gap, so the figure covers crossplay players Steam has no ping for and
    /// cannot be reported lower than it is. Players without FGN never answer, which also marks who has it.
    /// </summary>
    [HarmonyPatch]
    public static class ConnectionEcho
    {
        public const int Protocol = 1;

        private const string EchoRpc = "FGN_Echo";
        private const string ReplyRpc = "FGN_EchoReply";
        private const double IntervalSeconds = 2.0;
        private const double SweepSeconds = 0.5;
        private const float SmoothingFactor = 0.3f;
        private const float MaxPlausibleRttMs = 10000f;

        private sealed class Echo
        {
            public long PendingStamp;
            public double NextSend;
            public float RttMs;
            public int Protocol;
        }

        private static readonly Dictionary<ZNetPeer, Echo> s_echoes = new Dictionary<ZNetPeer, Echo>();
        private static readonly Dictionary<ZRpc, ZNetPeer> s_peersByRpc = new Dictionary<ZRpc, ZNetPeer>();
        private static double s_nextSweep;

        public static bool TryRtt(ZNetPeer peer, out float rttMs)
        {
            rttMs = 0f;
            if (peer == null || !s_echoes.TryGetValue(peer, out var echo) || echo.Protocol <= 0) return false;
            rttMs = echo.RttMs;
            return true;
        }

        public static bool HasFgn(ZNetPeer peer) => peer != null && s_echoes.TryGetValue(peer, out var echo) && echo.Protocol > 0;

        internal static ZNetPeer PeerOf(ZRpc rpc) => rpc != null && s_peersByRpc.TryGetValue(rpc, out var peer) ? peer : null;

        [HarmonyPatch(typeof(ZNet), "OnNewConnection"), HarmonyPostfix]
        static void OnNewConnection(ZNet __instance, ZNetPeer peer)
        {
            if (peer?.m_rpc == null) return;
            s_peersByRpc[peer.m_rpc] = peer;
            if (__instance.IsServer())
            {
                s_echoes[peer] = new Echo { NextSend = Time.realtimeSinceStartupAsDouble + IntervalSeconds };
                peer.m_rpc.Register<long, int>(ReplyRpc, OnReply);
            }
            else
            {
                peer.m_rpc.Register<long>(EchoRpc, OnEcho);
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect)), HarmonyPostfix]
        static void OnDisconnect(ZNetPeer peer)
        {
            if (peer == null) return;
            s_echoes.Remove(peer);
            if (peer.m_rpc != null) s_peersByRpc.Remove(peer.m_rpc);
        }

        [HarmonyPatch(typeof(ZNet), "Shutdown"), HarmonyPostfix]
        static void OnShutdown()
        {
            s_echoes.Clear();
            s_peersByRpc.Clear();
        }

        [HarmonyPatch(typeof(ZNet), "Update"), HarmonyPostfix]
        static void SendEchoes(ZNet __instance)
        {
            if (s_echoes.Count == 0 || !__instance.IsServer()) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < s_nextSweep) return;
            s_nextSweep = now + SweepSeconds;
            foreach (var entry in s_echoes)
            {
                var peer = entry.Key;
                var echo = entry.Value;
                if (now < echo.NextSend || peer.m_rpc == null || !peer.IsReady()) continue;
                echo.NextSend = now + IntervalSeconds;
                echo.PendingStamp = Stopwatch.GetTimestamp();
                peer.m_rpc.Invoke(EchoRpc, echo.PendingStamp);
            }
        }

        private static void OnEcho(ZRpc rpc, long stamp) => rpc.Invoke(ReplyRpc, stamp, Protocol);

        private static void OnReply(ZRpc rpc, long stamp, int protocol)
        {
            var peer = PeerOf(rpc);
            if (peer == null || !s_echoes.TryGetValue(peer, out var echo) || stamp != echo.PendingStamp) return;
            echo.PendingStamp = 0L;
            float sample = (float)((Stopwatch.GetTimestamp() - stamp) * 1000.0 / Stopwatch.Frequency);
            if (sample < 0f || sample > MaxPlausibleRttMs) return;
            echo.RttMs = echo.Protocol > 0 ? Mathf.Lerp(echo.RttMs, sample, SmoothingFactor) : sample;
            echo.Protocol = protocol;
        }
    }
}
