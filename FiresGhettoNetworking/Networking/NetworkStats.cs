using System.Collections.Generic;
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
                    float localQuality;
                    float remoteQuality;
                    int ping;
                    float outBps;
                    float inBps;
                    socket.GetConnectionQuality(out localQuality, out remoteQuality, out ping, out outBps, out inBps);
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
    }
}
