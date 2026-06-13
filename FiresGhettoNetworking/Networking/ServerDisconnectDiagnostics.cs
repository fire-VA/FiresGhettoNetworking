using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Always-on, server-side disconnect logger. Records when each peer drops,
    /// how long it was connected, and — critically — how many peers dropped in a
    /// short window. A BURST of disconnects is the fingerprint of a main-thread
    /// stall (a long world save, a GC pause, a config-reload storm): the server
    /// freezes long enough that every peer's Steam connection times out at once,
    /// then they all reconnect and every ServerSync mod re-distributes its full
    /// config simultaneously. A lone disconnect is just one client's link dying.
    ///
    /// This distinction is exactly what's needed to tell "the server hitched and
    /// dropped everyone" apart from "one player rage-quit" without a structured
    /// BepInEx log — the two read identically in vanilla output. Logged at Message
    /// level (disconnects are low-volume) so it's always in the screenlog/console.
    /// </summary>
    [HarmonyPatch]
    public static class ServerDisconnectDiagnostics
    {
        private const float BurstWindowSec = 10f;
        private const int BurstWarnThreshold = 3;

        // Connection start time keyed by the peer's socket (stable for the
        // connection's lifetime, and available before the uid handshake finishes).
        private static readonly Dictionary<ISocket, float> _connectedSince
            = new Dictionary<ISocket, float>();

        // Sliding window of recent disconnect timestamps for burst detection.
        private static readonly List<float> _recentDisconnects = new List<float>();

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        [HarmonyPostfix]
        public static void OnNewConnection_StampConnectTime(ZNetPeer peer)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (peer?.m_socket == null) return;
            _connectedSince[peer.m_socket] = Time.realtimeSinceStartup;
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
        [HarmonyPrefix]
        public static void Disconnect_LogPeer(ZNetPeer peer)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (peer == null) return;

            float now = Time.realtimeSinceStartup;

            string durationText = "duration=unknown";
            if (peer.m_socket != null && _connectedSince.TryGetValue(peer.m_socket, out float since))
            {
                durationText = $"connected={now - since:F0}s";
                _connectedSince.Remove(peer.m_socket);
            }

            int burst = CountRecentDisconnectsAndRecord(now);

            string name = string.IsNullOrEmpty(peer.m_playerName) ? "<no-name>" : peer.m_playerName;
            string endpoint = SafeEndpoint(peer);

            LoggerOptions.LogMessage(
                $"[Disconnect] peer={name} uid={peer.m_uid} {endpoint} {durationText} "
                + $"(#{burst} in last {BurstWindowSec:F0}s, {ConnectedPeerCount()} still connected).");

            if (burst >= BurstWarnThreshold)
            {
                LoggerOptions.LogWarning(
                    $"[Disconnect] {burst} peers dropped within {BurstWindowSec:F0}s — looks like a mass "
                    + "timeout, not individual link failures. The usual cause is a main-thread stall (a long "
                    + "world save, a GC pause, or a config-reload storm) that freezes the server past the Steam "
                    + "connection timeout. Check the save duration / frame hitches right before this window.");
            }
        }

        private static int CountRecentDisconnectsAndRecord(float now)
        {
            _recentDisconnects.Add(now);
            float cutoff = now - BurstWindowSec;
            // Drop stale timestamps from the front (list stays small — bounded by
            // peer count × disconnects-per-window).
            int firstFresh = 0;
            while (firstFresh < _recentDisconnects.Count && _recentDisconnects[firstFresh] < cutoff)
                firstFresh++;
            if (firstFresh > 0) _recentDisconnects.RemoveRange(0, firstFresh);
            return _recentDisconnects.Count;
        }

        private static int ConnectedPeerCount()
        {
            return ZNet.instance?.GetConnectedPeers()?.Count ?? 0;
        }

        private static string SafeEndpoint(ZNetPeer peer)
        {
            try
            {
                string host = peer.m_socket?.GetHostName();
                return string.IsNullOrEmpty(host) ? "endpoint=?" : $"endpoint={host}";
            }
            catch
            {
                return "endpoint=?";
            }
        }
    }
}
