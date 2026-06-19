using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Public, version-stable network telemetry surface for other mods to read.
    ///
    /// FGN already owns the network picture on the server — it patches the send path
    /// and tracks per-peer send queues through <see cref="SendCongestion"/>. That makes
    /// it the right place to read the real per-peer figures ONCE, from the stable public
    /// accessors, and hand them out — instead of every consumer reflecting blindly at
    /// vanilla internals whose field names drift between game patches.
    ///
    /// Every value here comes from a public interface method or public API:
    ///   ISocket.GetSendQueueSize()      per-peer send-queue bytes
    ///   ISocket.GetConnectionQuality()  per-peer ping + Tx/Rx bytes/sec (no reset side-effect)
    ///   ZDOMan.GetSentZDOs/GetRecvZDOs  aggregate ZDO throughput per second
    ///   SendCongestion                  FGN's own cap + congestion model
    ///   ZNet.ServerPlayerLimit          configured player slots
    /// No private-field reflection, nothing that breaks on a vanilla update.
    ///
    /// Server-side only — off-server every accessor returns 0 / false. Read at any
    /// cadence; a short-lived snapshot is cached so a caller pulling every figure in
    /// one pass only walks the peer list once.
    /// </summary>
    public static class NetworkStats
    {
        private struct Snapshot
        {
            public int PeerCount;
            public int PlayerLimit;
            public long TotalQueueBytes;
            public int CapBytes;
            public int CongestedPeers;
            public int AvgPingMs;
            public int MaxPingMs;
            public float SendBytesPerSec;
            public float RecvBytesPerSec;
            public int ZdosSentSec;
            public int ZdosRecvSec;
        }

        private const float SnapshotTtlSeconds = 0.5f;
        private static Snapshot s_snapshot;
        private static float s_snapshotTime = -999f;

        // Cached reflection of ZSteamSocket.m_con (the Steam connection handle) for the dedicated-
        // server ping path, plus a one-shot flag for the source-confirmation log.
        private static FieldInfo s_steamConField;
        private static bool s_loggedPeerStatusSource;

        /// <summary>True only when a server (dedicated or host) network is live.</summary>
        public static bool IsServerActive()
        {
            return ZNet.instance != null && ZNet.instance.IsServer();
        }

        public static int PeerCount() => Current().PeerCount;
        public static int PlayerLimit() => Current().PlayerLimit;
        public static long TotalSendQueueBytes() => Current().TotalQueueBytes;
        public static int SendQueueCapBytes() => Current().CapBytes;
        public static int CongestedPeerCount() => Current().CongestedPeers;
        public static int AveragePingMs() => Current().AvgPingMs;
        public static int MaxPingMs() => Current().MaxPingMs;
        public static float TotalSendBytesPerSec() => Current().SendBytesPerSec;
        public static float TotalRecvBytesPerSec() => Current().RecvBytesPerSec;
        public static int ZdosSentPerSec() => Current().ZdosSentSec;
        public static int ZdosRecvPerSec() => Current().ZdosRecvSec;

        /// <summary>
        /// Per-peer outbound throughput (bytes/sec) for one socket, server-side. Uses the game-server
        /// real-time status on a dedicated server (where vanilla GetConnectionQuality reads 0) and falls
        /// back to vanilla for listen-host / non-Steam peers. Returns 0 when unavailable.
        /// </summary>
        public static float PeerSendBytesPerSec(ISocket socket)
        {
            if (socket == null) return 0f;
            try
            {
                int ping; float outBps; float inBps;
                if (TryGameServerPeerStatus(socket, out ping, out outBps, out inBps)) return outBps;
                float lq, rq;
                socket.GetConnectionQuality(out lq, out rq, out ping, out outBps, out inBps);
                return outBps;
            }
            catch { return 0f; }
        }

        private static Snapshot Current()
        {
            float now = Time.realtimeSinceStartup;
            if (s_snapshotTime > 0f && now - s_snapshotTime < SnapshotTtlSeconds)
            {
                return s_snapshot;
            }

            s_snapshot = Build();
            s_snapshotTime = now;
            return s_snapshot;
        }

        private static Snapshot Build()
        {
            var snap = new Snapshot { PlayerLimit = ZNet.ServerPlayerLimit };

            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return snap;
            }

            List<ZNetPeer> peers;
            try { peers = ZNet.instance.GetPeers(); }
            catch { return snap; }
            if (peers == null) return snap;

            int cap = SendCongestion.EffectiveCapBytes();
            float congestionThreshold = SendCongestion.CongestionThresholdBytes();
            snap.CapBytes = cap;

            long totalQueue = 0L;
            int congested = 0;
            int pingSum = 0;
            int pingSamples = 0;
            int pingMax = 0;
            float sendBps = 0f;
            float recvBps = 0f;
            int peerCount = 0;

            foreach (ZNetPeer peer in peers)
            {
                if (peer == null || peer.m_socket == null) continue;
                peerCount++;
                ISocket socket = peer.m_socket;

                int queue;
                try { queue = socket.GetSendQueueSize(); }
                catch { queue = -1; }
                if (queue > 0)
                {
                    totalQueue += queue;
                    if (queue >= congestionThreshold) congested++;
                }

                try
                {
                    int ping;
                    float outBps;
                    float inBps;

                    // On a hosted server the peer connection is owned by the Steam game-server
                    // interface; vanilla GetConnectionQuality queries the client interface and
                    // reads 0 there. Prefer the game-server status, fall back to vanilla for the
                    // listen-host / non-Steam peers where vanilla works.
                    bool viaGameServer = TryGameServerPeerStatus(socket, out ping, out outBps, out inBps);
                    if (!viaGameServer)
                    {
                        float localQuality;
                        float remoteQuality;
                        socket.GetConnectionQuality(out localQuality, out remoteQuality, out ping, out outBps, out inBps);
                    }

                    if (!s_loggedPeerStatusSource)
                    {
                        s_loggedPeerStatusSource = true;
                        LogPeerStatusSource(socket, viaGameServer);
                    }

                    if (ping > 0)
                    {
                        pingSum += ping;
                        pingSamples++;
                        if (ping > pingMax) pingMax = ping;
                    }
                    if (outBps > 0f) sendBps += outBps;
                    if (inBps > 0f) recvBps += inBps;
                }
                catch { }
            }

            snap.PeerCount = peerCount;
            if (peerCount == 0) s_loggedPeerStatusSource = false;
            snap.TotalQueueBytes = totalQueue;
            snap.CongestedPeers = congested;
            snap.AvgPingMs = pingSamples > 0 ? Mathf.RoundToInt((float)pingSum / pingSamples) : 0;
            snap.MaxPingMs = pingMax;
            snap.SendBytesPerSec = sendBps;
            snap.RecvBytesPerSec = recvBps;

            if (ZDOMan.instance != null)
            {
                try { snap.ZdosSentSec = ZDOMan.instance.GetSentZDOs(); } catch { }
                try { snap.ZdosRecvSec = ZDOMan.instance.GetRecvZDOs(); } catch { }
            }

            return snap;
        }

        // Vanilla ISocket.GetConnectionQuality reads the connection's real-time status through the
        // CLIENT Steam networking interface. A hosted server's peer connections are owned by the
        // GAME-SERVER interface, so on a dedicated server that query returns non-OK and ping, Tx,
        // and Rx all read 0 (the panel shows "n/a"). Read straight off the connection handle through
        // the game-server interface; the result code says when it applies — true on a hosted server,
        // false for a client or a non-Steam (PlayFab) peer, where the caller falls back to vanilla.
        private static bool TryGameServerPeerStatus(ISocket socket, out int ping, out float outBytesSec, out float inBytesSec)
        {
            ping = 0;
            outBytesSec = 0f;
            inBytesSec = 0f;

            // Unwrap ServerSync's BufferingSocket wrapper(s) first — on a modded server peer.m_socket stays
            // wrapped for the whole session, and a raw `is ZSteamSocket` test fails on the wrapper, which
            // skips this game-server path and zeroes the throughput read (vanilla GetConnectionQuality then
            // reads 0 on a dedi). Matches what GetConnectionHandle/IsSteamSocket already do.
            if (!(NetworkingRatesGroup.UnwrapSocket(socket) is ZSteamSocket steamSocket))
            {
                return false;
            }

            if (s_steamConField == null)
            {
                s_steamConField = AccessTools.Field(typeof(ZSteamSocket), "m_con");
            }
            if (s_steamConField == null)
            {
                return false;
            }

            var connection = (HSteamNetConnection)s_steamConField.GetValue(steamSocket);
            if (connection == HSteamNetConnection.Invalid)
            {
                return false;
            }

            SteamNetConnectionRealTimeStatus_t status = default;
            SteamNetConnectionRealTimeLaneStatus_t lanes = default;
            if (SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, 0, ref lanes) != EResult.k_EResultOK)
            {
                return false;
            }

            ping = status.m_nPing;
            outBytesSec = status.m_flOutBytesPerSec;
            inBytesSec = status.m_flInBytesPerSec;
            return true;
        }

        // One-shot at the first peer (re-armed when the server empties) so a test run confirms which
        // path produced the figures without spamming the log.
        private static void LogPeerStatusSource(ISocket socket, bool viaGameServer)
        {
            string socketType = socket != null ? socket.GetType().Name : "null";
            LoggerOptions.LogMessage(
                "[NetworkStats] per-peer ping/throughput source = " +
                (viaGameServer ? "game-server interface (dedicated path)" : "vanilla GetConnectionQuality") +
                " for " + socketType +
                ". Vanilla reads 0 on a dedicated server; the game-server path restores ping + Tx/Rx.");
        }
    }
}
