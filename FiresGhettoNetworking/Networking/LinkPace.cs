using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// What Steam itself reports for each connection while a bulk transfer is on it. Once per connection it logs the route
    /// (remote address, relayed or direct, flags); every five seconds with a quarter megabyte or more pending or unacked it
    /// logs Steam's send rate, the bytes going out, what is pending and unacked, the queue wait, ping and quality. On the rig
    /// (2026-09-28 R5) the bot's connection moved 1 MB/s with the same config that moved 8 MB/s to Fire's; these lines say
    /// whether Steam's own rate, loss, or the route holds such a connection back.
    /// </summary>
    internal static class LinkPace
    {
        private const double ReportSeconds = 5.0;
        private const int BulkBytes = 256 * 1024;
        private const float BytesPerKilobyte = 1024f;
        private const float BytesPerMegabyte = 1024f * 1024f;
        private const float MicrosecondsPerMillisecond = 1000f;
        private const float Percent = 100f;

        private static readonly HashSet<uint> s_routeLogged = new HashSet<uint>();
        private static double s_nextReport;

        public static void ReportPeriodically()
        {
            if (ZNet.instance == null) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < s_nextReport) return;
            s_nextReport = now + ReportSeconds;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer?.m_socket == null || !(NetworkingRatesGroup.UnwrapSocket(peer.m_socket) is ZSteamSocket steam)) continue;
                HSteamNetConnection connection = LinkController.ConnectionOf(steam);
                if (connection == HSteamNetConnection.Invalid) continue;
                string who = Name(peer);
                if (s_routeLogged.Add(connection.m_HSteamNetConnection)) LogRoute(who, connection);
                if (!LoggerOptions.DebugEnabled) continue;
                if (!LinkController.TryStatus(steam, out SteamNetConnectionRealTimeStatus_t status)) continue;
                if (status.m_cbPendingReliable + status.m_cbSentUnackedReliable < BulkBytes) continue;
                LoggerOptions.LogDebug($"[LinkPace] to {who}: Steam's send rate {status.m_nSendRateBytesPerSecond / BytesPerMegabyte:F1} MB/s, "
                    + $"out {status.m_flOutBytesPerSec / BytesPerMegabyte:F2} MB/s in {status.m_flOutPacketsPerSec:F0} packets/s, "
                    + $"in {status.m_flInPacketsPerSec:F0} packets/s; pending reliable {status.m_cbPendingReliable / BytesPerKilobyte:F0} KB, "
                    + $"sent unacked {status.m_cbSentUnackedReliable / BytesPerKilobyte:F0} KB, Steam wait "
                    + $"{(long)status.m_usecQueueTime / MicrosecondsPerMillisecond:F0} ms; ping {status.m_nPing} ms, quality local "
                    + $"{Quality(status.m_flConnectionQualityLocal)} remote {Quality(status.m_flConnectionQualityRemote)}");
            }
        }

        private static void LogRoute(string who, HSteamNetConnection connection)
        {
            try
            {
                SteamNetConnectionInfo_t info;
                bool ok = ZNet.instance.IsDedicated()
                    ? SteamGameServerNetworkingSockets.GetConnectionInfo(connection, out info)
                    : SteamNetworkingSockets.GetConnectionInfo(connection, out info);
                if (!ok) return;
                info.m_addrRemote.ToString(out string remote, true);
                LoggerOptions.LogMessage($"[LinkPace] connection to {who}: remote {remote}, {Flags(info.m_nFlags)}");
            }
            catch (Exception e)
            {
                LoggerOptions.LogWarning($"[LinkPace] could not read the route to {who}: {e.Message}");
            }
        }

        private static string Flags(int flags)
        {
            var names = new List<string>();
            names.Add((flags & Constants.k_nSteamNetworkConnectionInfoFlags_Relayed) != 0 ? "RELAYED through Steam" : "direct");
            if ((flags & Constants.k_nSteamNetworkConnectionInfoFlags_Fast) != 0) names.Add("fast (LAN)");
            if ((flags & Constants.k_nSteamNetworkConnectionInfoFlags_LoopbackBuffers) != 0) names.Add("loopback buffers");
            if ((flags & Constants.k_nSteamNetworkConnectionInfoFlags_Unencrypted) != 0) names.Add("unencrypted");
            if ((flags & Constants.k_nSteamNetworkConnectionInfoFlags_Unauthenticated) != 0) names.Add("unauthenticated");
            if ((flags & Constants.k_nSteamNetworkConnectionInfoFlags_DualWifi) != 0) names.Add("dual wifi");
            return string.Join(", ", names) + $" (flags {flags})";
        }

        private static string Quality(float quality) => quality < 0f ? "n/a" : (quality * Percent).ToString("F0") + "%";

        private static string Name(ZNetPeer peer)
        {
            if (peer.m_server) return "the server";
            return string.IsNullOrEmpty(peer.m_playerName) ? peer.m_uid.ToString() : peer.m_playerName;
        }
    }
}
