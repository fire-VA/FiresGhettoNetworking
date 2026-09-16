using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using FiresGhettoNetworkMod.AutoTune;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Per-player link state. Sizes the world data allowed in flight to each player from that connection's own delay, loss
    /// and queueing, and on a server steps the Steam send rate pinned for it. Vanilla gives every player one 10 KB window,
    /// which a slow link overflows and a fast but distant link cannot fill.
    /// </summary>
    [HarmonyPatch]
    public static class LinkController
    {
        public static ConfigEntry<bool> ConfigAdaptiveWindow;
        public static ConfigEntry<int> ConfigMaxWindowKB;

        public const int MinChunkBytes = 2048;
        private const int VanillaWindowBytes = 10240;
        private const int SteamRateCeilingBytes = 100 * 1024 * 1024;
        private const int BytesPerKilobyte = 1024;
        private const float BytesPerMegabyte = 1024f * 1024f;
        private const double UpdateSeconds = 0.25;
        private const double RateStepSeconds = 1.0;
        private const double SlowStartRearmSeconds = 10.0;
        private const double RecoverSeconds = 10.0;
        private const double MinRttEpochSeconds = 30.0;
        private const float QueueHoldMs = 10f;
        private const float QueueShrinkMs = 50f;
        private const float LossyDelivery = 0.95f;
        private const int SustainUpdates = 2;
        private const float GrowFactor = 1.25f;
        private const float ShrinkFactor = 0.75f;
        private const float GoodputHeadroom = 1.25f;
        private const float DelayToleranceMs = 30f;
        private const float BadConnectionSeconds = 5f;
        private const int BadConnectionSteps = 3;

        private const string RpcLinksRequest = "FGN_LinksReq";
        private const string RpcLinksReply = "FGN_LinksResp";

        internal sealed class Link
        {
            public ZNetPeer Peer;
            public ISocket OuterSocket;
            public ZSteamSocket Steam;
            public int Window;
            public bool Limited;
            public bool HasStatus;
            public bool Congested;
            public double NextUpdate;
            public double LastSample;
            public int PingMs;
            public float SmoothedRttMs;
            public int MinRttCurrent;
            public int MinRttPrevious;
            public double MinRttEpochStart;
            public float QueueMs;
            public int PendingBytes;
            public int InFlightBytes;
            public float Delivery = -1f;
            public int PacingBytes;
            public bool GoodputPrimed;
            public int LastTotalSent;
            public long SentTotal;
            public long DeliveredTotal;
            public float Goodput;
            public float GoodputPeak;
            public int LossyUpdates;
            public int DelayedUpdates;
            public uint Connection;
            public bool RatePinned;
            public bool RateSlowStart;
            public int RateTarget;
            public double NextRateStep;
            public double LastBackoff;
            public int BadConnectionCount;
            public int Sends;
            public double SendsSince;
        }

        private static readonly Dictionary<ZNetPeer, Link> s_links = new Dictionary<ZNetPeer, Link>();
        private static AccessTools.FieldRef<ZSteamSocket, HSteamNetConnection> s_connection;
        private static AccessTools.FieldRef<ZSteamSocket, int> s_totalSent;
        private static bool s_commandRegistered;
        private static double s_nextReport;
        private const double ReportSeconds = 300.0;

        public static void InitConfig(ConfigFile config)
        {
            ConfigAdaptiveWindow = config.Bind("04 - Networking", "Adaptive Send Window", true,
                "Sizes how much world data may be on its way to each player at once from that player's own connection. It grows "
                + "while the connection keeps up and shrinks as soon as Steam reports queueing, lost packets or rising ping. "
                + "Vanilla gives everyone the same 10 KB, which a slow link overflows and a fast but distant link cannot fill. "
                + "Off = every player uses Queue Size. Crossplay players always use Queue Size, Steam has no figures for them.\n"
                + "Applies on both sides: a server sizes what it sends each player, a client what it sends the server.");
            ConfigMaxWindowKB = config.Bind("04 - Networking", "Max Send Window", 1024,
                new ConfigDescription("Upper limit, in KB per player, for Adaptive Send Window.",
                    new AcceptableValueRange<int>(64, 8192)));
            ConfigAdaptiveWindow.SettingChanged += (_, __) => ResetWindows();
            ConfigMaxWindowKB.SettingChanged += (_, __) => ClampWindows();

            s_connection = AccessTools.FieldRefAccess<ZSteamSocket, HSteamNetConnection>("m_con");
            s_totalSent = AccessTools.FieldRefAccess<ZSteamSocket, int>("m_totalSent");
        }

        [HarmonyPatch(typeof(ZNet), "Start"), HarmonyPostfix]
        static void OnZNetStart()
        {
            s_links.Clear();
            if (ZRoutedRpc.instance != null)
            {
                ZRoutedRpc.instance.Register(RpcLinksRequest, new Action<long>(RPC_LinksRequest));
                ZRoutedRpc.instance.Register<string>(RpcLinksReply, RPC_LinksReply);
            }
            if (s_commandRegistered) return;
            s_commandRegistered = true;
            new Terminal.ConsoleCommand("fgn_links",
                "FGN diagnostic (admin): each player's connection as FGN sees it: ping, send window, data in flight, Steam "
                + "wait, delivery, pacing, goodput and sends per second.",
                new Terminal.ConsoleEvent(OnLinksCommand));
        }

        [HarmonyPatch(typeof(ZNet), "Shutdown"), HarmonyPostfix]
        static void OnZNetShutdown() => s_links.Clear();

        [HarmonyPatch(typeof(ZNet), "OnDestroy"), HarmonyPostfix]
        static void OnZNetDestroy() => s_links.Clear();

        [HarmonyPatch(typeof(ZDOMan), "RemovePeer"), HarmonyPostfix]
        static void OnRemovePeer(ZNetPeer netPeer)
        {
            if (netPeer == null || !s_links.TryGetValue(netPeer, out var link)) return;
            if (ZNet.instance != null && ZNet.instance.IsDedicated() && link.HasStatus)
            {
                double span = Math.Max(0.001, Time.realtimeSinceStartupAsDouble - link.SendsSince);
                LoggerOptions.LogInfo($"[Links] left: {Describe(link)}, sends {link.Sends / span:F1}/s"
                    + (link.RatePinned ? $", rate pinned {link.RateTarget / BytesPerMegabyte:F1} MB/s" : ""));
            }
            s_links.Remove(netPeer);
        }

        public static int SendGateWindow(int queueBytes, ZDOMan.ZDOPeer peer)
        {
            var link = LinkOf(peer);
            if (link == null) return StaticWindowBytes();
            UpdateLink(link);
            if (queueBytes > link.Window - MinChunkBytes) link.Limited = true;
            return link.Window;
        }

        public static int WindowBytes(ZDOMan.ZDOPeer peer)
        {
            var link = LinkOf(peer);
            return link != null ? link.Window : StaticWindowBytes();
        }

        public static int PackageBudget(int budgetBytes, ZDOMan.ZDOPeer peer) => Mathf.Min(budgetBytes, PackageCapBytes());

        public static int StaticWindowBytes() => EffectiveConfig.QueueSizeBytes(VanillaWindowBytes);

        public static int PackageCapBytes() => EffectiveConfig.QueueSizeBytes(VanillaWindowBytes);

        internal static int WindowBytes(ZNetPeer peer)
        {
            return peer != null && s_links.TryGetValue(peer, out var link) ? link.Window : StaticWindowBytes();
        }

        internal static int PingMs(ZNetPeer peer)
        {
            return peer != null && s_links.TryGetValue(peer, out var link) && link.HasStatus ? link.PingMs : 0;
        }

        internal static bool HasRoom(ZDOMan.ZDOPeer peer)
        {
            var link = LinkOf(peer);
            if (link == null) return true;
            UpdateLink(link);
            int queueBytes;
            try { queueBytes = peer.m_peer.m_socket.GetSendQueueSize(); }
            catch { return false; }
            if (queueBytes <= link.Window - MinChunkBytes) return true;
            link.Limited = true;
            return false;
        }

        internal static void CountSend(ZDOMan.ZDOPeer peer)
        {
            var link = LinkOf(peer);
            if (link != null) link.Sends++;
        }

        internal static void ResetRates()
        {
            foreach (var link in s_links.Values) link.RatePinned = false;
        }

        private static void ResetWindows()
        {
            foreach (var link in s_links.Values)
            {
                link.Window = StaticWindowBytes();
                link.NextUpdate = 0.0;
            }
        }

        private static void ClampWindows()
        {
            int max = MaxWindowBytes();
            foreach (var link in s_links.Values)
                if (link.Window > max) link.Window = max;
        }

        private static Link LinkOf(ZDOMan.ZDOPeer peer)
        {
            var netPeer = peer?.m_peer;
            if (netPeer == null) return null;
            if (!s_links.TryGetValue(netPeer, out var link))
            {
                if (s_links.Count == 0) ServerFrameProfile.Report();
                link = new Link { Peer = netPeer, Window = StaticWindowBytes(), SendsSince = Time.realtimeSinceStartupAsDouble };
                s_links[netPeer] = link;
            }
            return link;
        }

        private static bool AdaptiveWindowEnabled() => ConfigAdaptiveWindow == null || ConfigAdaptiveWindow.Value;

        private static int MaxWindowBytes()
            => Mathf.Max(StaticWindowBytes(), (ConfigMaxWindowKB != null ? ConfigMaxWindowKB.Value : 1024) * BytesPerKilobyte);

        private static bool LogTicks() => AdaptiveSendRate.ConfigLog != null && AdaptiveSendRate.ConfigLog.Value;

        private static void UpdateLink(Link link)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < link.NextUpdate) return;
            link.NextUpdate = now + UpdateSeconds;
            SampleSteamStatus(link, now);
            AdjustWindow(link);
            if (RateControlActive()) StepRate(link, now);
            else link.RatePinned = false;
        }

        private static void SampleSteamStatus(Link link, double now)
        {
            var socket = link.Peer.m_socket;
            if (!ReferenceEquals(socket, link.OuterSocket))
            {
                link.OuterSocket = socket;
                link.Steam = socket != null ? NetworkingRatesGroup.UnwrapSocket(socket) as ZSteamSocket : null;
                link.Connection = 0u;
                link.RatePinned = false;
                link.GoodputPrimed = false;
            }

            if (link.Steam == null || !TryStatus(link.Steam, out var status))
            {
                link.HasStatus = false;
                return;
            }
            link.HasStatus = true;

            double elapsed = link.LastSample > 0.0 ? now - link.LastSample : 0.0;
            link.LastSample = now;

            link.PingMs = status.m_nPing;
            if (link.PingMs > 0)
            {
                link.SmoothedRttMs = link.SmoothedRttMs > 0f ? Mathf.Lerp(link.SmoothedRttMs, link.PingMs, 0.25f) : link.PingMs;
                if (now - link.MinRttEpochStart >= MinRttEpochSeconds)
                {
                    link.MinRttPrevious = link.MinRttCurrent;
                    link.MinRttCurrent = link.PingMs;
                    link.MinRttEpochStart = now;
                }
                else if (link.MinRttCurrent <= 0 || link.PingMs < link.MinRttCurrent)
                {
                    link.MinRttCurrent = link.PingMs;
                }
            }

            link.QueueMs = (long)status.m_usecQueueTime / 1000f;
            link.PendingBytes = status.m_cbPendingReliable + status.m_cbPendingUnreliable;
            link.InFlightBytes = link.PendingBytes + status.m_cbSentUnackedReliable;
            link.Delivery = status.m_flConnectionQualityRemote;
            link.PacingBytes = status.m_nSendRateBytesPerSecond;

            int totalSent = s_totalSent(link.Steam);
            if (link.GoodputPrimed && elapsed > 0.0)
            {
                link.SentTotal += unchecked((uint)(totalSent - link.LastTotalSent));
                long deliveredTotal = link.SentTotal - link.InFlightBytes;
                float sample = Mathf.Max(0f, (float)((deliveredTotal - link.DeliveredTotal) / elapsed));
                link.DeliveredTotal = deliveredTotal;
                link.Goodput = Mathf.Lerp(link.Goodput, sample, 0.5f);
                link.GoodputPeak = Mathf.Max(sample, link.GoodputPeak * 0.9f);
            }
            else
            {
                link.GoodputPrimed = true;
                link.SentTotal = 0L;
                link.DeliveredTotal = -link.InFlightBytes;
                link.Goodput = 0f;
                link.GoodputPeak = 0f;
            }
            link.LastTotalSent = totalSent;
        }

        private static bool TryStatus(ZSteamSocket socket, out SteamNetConnectionRealTimeStatus_t status)
        {
            status = default;
            var connection = s_connection(socket);
            if (connection == HSteamNetConnection.Invalid) return false;
            SteamNetConnectionRealTimeLaneStatus_t lanes = default;
            try
            {
                EResult result = ZNet.instance != null && ZNet.instance.IsDedicated()
                    ? SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, 0, ref lanes)
                    : SteamNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, 0, ref lanes);
                return result == EResult.k_EResultOK;
            }
            catch
            {
                return false;
            }
        }

        private static void AdjustWindow(Link link)
        {
            if (!link.HasStatus)
            {
                link.Window = StaticWindowBytes();
                link.Congested = false;
                link.Limited = false;
                return;
            }

            int minRtt = MinRtt(link);
            bool lossy = link.Delivery >= 0f && link.Delivery < LossyDelivery;
            bool delayed = minRtt > 0 && link.SmoothedRttMs > minRtt + Mathf.Max(DelayToleranceMs, minRtt * 0.5f);
            link.LossyUpdates = lossy ? link.LossyUpdates + 1 : 0;
            link.DelayedUpdates = delayed ? link.DelayedUpdates + 1 : 0;
            link.Congested = link.LossyUpdates >= SustainUpdates || link.DelayedUpdates >= SustainUpdates;

            if (!AdaptiveWindowEnabled())
                link.Window = StaticWindowBytes();
            else if (link.Congested || link.QueueMs > QueueShrinkMs)
                link.Window = Mathf.Max(VanillaWindowBytes, (int)(link.Window * ShrinkFactor));
            else if (link.Limited && link.QueueMs <= QueueHoldMs)
                link.Window = Mathf.Min(MaxWindowBytes(), Mathf.Max(link.Window + MinChunkBytes, (int)(link.Window * GrowFactor)));
            link.Limited = false;
        }

        private static int MinRtt(Link link)
        {
            if (link.MinRttPrevious <= 0) return link.MinRttCurrent;
            if (link.MinRttCurrent <= 0) return link.MinRttPrevious;
            return Mathf.Min(link.MinRttCurrent, link.MinRttPrevious);
        }

        private static bool RateControlActive()
        {
            return ZNet.instance != null && ZNet.instance.IsServer()
                && AdaptiveSendRate.ConfigEnabled != null && AdaptiveSendRate.ConfigEnabled.Value
                && !AdaptiveSendRate.Suspend
                && !EffectiveConfig.HyperBoost();
        }

        private static void StepRate(Link link, double now)
        {
            if (!link.HasStatus) return;
            if (link.Connection == 0u) link.Connection = NetworkingRatesGroup.GetConnectionHandle(link.Peer);
            if (link.Connection == 0u) return;

            int floor = EffectiveConfig.SteamSendRateMin();
            int start = Mathf.Clamp(EffectiveConfig.SteamSendRateMax(), floor, SteamRateCeilingBytes);
            if (!link.RatePinned)
            {
                link.RateTarget = start;
                link.RateSlowStart = true;
                link.BadConnectionCount = 0;
                link.NextRateStep = now + RateStepSeconds;
                link.RatePinned = NetworkingRatesGroup.PinConnectionRate(link.Connection, start, raising: true);
                if (LogTicks())
                    LoggerOptions.LogMessage($"[AdaptiveRate] {Describe(link)}; rate pinned at {start / BytesPerMegabyte:F1} MB/s"
                        + (link.RatePinned ? "" : " [PIN FAILED]"));
                return;
            }
            if (now < link.NextRateStep) return;
            link.NextRateStep = now + RateStepSeconds;
            if (!link.RateSlowStart && now - link.LastBackoff >= SlowStartRearmSeconds) link.RateSlowStart = true;

            float pingAge = 0f;
            try { if (link.Peer.IsReady() && link.Peer.m_rpc != null) pingAge = link.Peer.m_rpc.GetTimeSinceLastPing(); }
            catch { }
            bool pipeStuck = link.Congested || link.QueueMs > QueueShrinkMs || link.InFlightBytes >= link.Window;
            link.BadConnectionCount = pingAge > BadConnectionSeconds && pipeStuck ? link.BadConnectionCount + 1 : 0;

            int current = Mathf.Max(floor, link.RateTarget);
            int next = current;
            string decision = "hold";
            if (link.BadConnectionCount >= BadConnectionSteps)
            {
                link.BadConnectionCount = 0;
                link.RateSlowStart = false;
                link.LastBackoff = now;
                next = Mathf.Max(floor, current / 2);
                decision = "back off, ping reply overdue";
            }
            else if (link.Congested)
            {
                link.RateSlowStart = false;
                link.LastBackoff = now;
                long eased = (long)(current * ShrinkFactor);
                if (link.GoodputPeak > 0f) eased = Math.Min(eased, (long)(link.GoodputPeak * GoodputHeadroom));
                next = (int)Math.Max(floor, eased);
                decision = "back off, loss or rising ping";
            }
            else if (link.QueueMs > QueueHoldMs && link.PendingBytes > 0 && current < SteamRateCeilingBytes)
            {
                long raised = (long)(current * (link.RateSlowStart ? 2f : GrowFactor));
                next = (int)Math.Min(raised, SteamRateCeilingBytes);
                decision = "raise, Steam is queueing";
            }
            else if (current < start && now - link.LastBackoff >= RecoverSeconds)
            {
                next = (int)Math.Min(start, (long)(current * GrowFactor));
                decision = "recover toward Send Rate Max, link healthy";
            }

            bool? pinned = null;
            if (next != link.RateTarget)
                pinned = NetworkingRatesGroup.PinConnectionRate(link.Connection, next, raising: next > link.RateTarget);
            link.RateTarget = next;

            if (LogTicks())
                LoggerOptions.LogMessage($"[AdaptiveRate] {Describe(link)}; {decision}: rate {current / BytesPerMegabyte:F1} -> {next / BytesPerMegabyte:F1} MB/s"
                    + (pinned.HasValue && !pinned.Value ? " [PIN FAILED]" : ""));
        }

        private static string Describe(Link link)
        {
            string delivery = link.Delivery < 0f ? "n/a" : (link.Delivery * 100f).ToString("F0") + "%";
            return $"{PeerName(link.Peer)}: ping {link.PingMs} ms (min {MinRtt(link)}), window {link.Window / BytesPerKilobyte} KB, "
                + $"in flight {link.InFlightBytes / BytesPerKilobyte} KB, Steam wait {link.QueueMs:F0} ms, delivered {delivery}, "
                + $"pacing {link.PacingBytes / BytesPerMegabyte:F1} MB/s, goodput {link.Goodput / BytesPerMegabyte:F2} MB/s";
        }

        private static string PeerName(ZNetPeer peer)
        {
            if (peer == null) return "?";
            return string.IsNullOrEmpty(peer.m_playerName) ? peer.m_uid.ToString() : peer.m_playerName;
        }

        private static void OnLinksCommand(Terminal.ConsoleEventArgs args)
        {
            if (ZNet.instance == null || ZRoutedRpc.instance == null)
            {
                args.Context?.AddString("FGN: not connected.");
                return;
            }
            string report = BuildReport();
            args.Context?.AddString(report);
            LoggerOptions.LogMessage(report);
            if (!ZNet.instance.IsServer()) ZRoutedRpc.instance.InvokeRoutedRPC(RpcLinksRequest);
        }

        private static void RPC_LinksRequest(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            string reply = "FGN links denied, admin only.";
            if (ServerClientUtils.IsAdmin(sender))
            {
                reply = BuildReport();
                LoggerOptions.LogMessage(reply);
            }
            try { ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcLinksReply, reply); }
            catch { }
        }

        private static void RPC_LinksReply(long sender, string report)
        {
            if (global::Console.instance != null) global::Console.instance.AddString(report);
            LoggerOptions.LogMessage(report);
        }

        [HarmonyPatch(typeof(ZNet), "Update"), HarmonyPostfix]
        static void ReportPeriodically(ZNet __instance)
        {
            if (s_links.Count == 0 || !__instance.IsDedicated()) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (s_nextReport <= 0.0) s_nextReport = now + ReportSeconds;
            if (now < s_nextReport) return;
            s_nextReport = now + ReportSeconds;
            LoggerOptions.LogInfo(BuildReport());
        }

        private static string BuildReport()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            var sb = new StringBuilder(256);
            sb.Append(ZNet.instance.IsServer() ? "[Links] server to each player" : "[Links] this client to the server")
              .Append(", target ").Append(SendScheduler.TargetHz().ToString("F0")).Append(" sends/s")
              .Append(SendScheduler.Active() ? "" : " (scheduler off)")
              .Append(AdaptiveWindowEnabled() ? "" : ", adaptive window off");
            foreach (var link in s_links.Values)
            {
                double span = Math.Max(0.001, now - link.SendsSince);
                float sendsPerSecond = (float)(link.Sends / span);
                link.Sends = 0;
                link.SendsSince = now;
                sb.Append('\n').Append(link.HasStatus ? Describe(link) : PeerName(link.Peer) + ": no Steam figures, window "
                    + (link.Window / BytesPerKilobyte) + " KB")
                  .Append(", sends ").Append(sendsPerSecond.ToString("F1")).Append("/s");
                if (link.RatePinned) sb.Append(", rate pinned ").Append((link.RateTarget / BytesPerMegabyte).ToString("F1")).Append(" MB/s");
                if (ZNet.instance.IsServer())
                    sb.Append(ConnectionEcho.TryRtt(link.Peer, out float echoMs) ? ", FGN round trip " + echoMs.ToString("F0") + " ms" : ", no FGN reply");
            }
            if (s_links.Count == 0) sb.Append("\nno connections yet");
            if (ZNet.instance.IsDedicated())
            {
                sb.Append('\n').Append(CreatureOwnership.Report())
                  .Append('\n').Append(ServerFrameProfile.Report());
            }
            return sb.ToString();
        }
    }
}
