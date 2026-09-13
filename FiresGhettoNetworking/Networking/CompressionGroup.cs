using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    // Per-packet compression on the runtime's own Deflate, no external library or shared dictionary. The wire frame and its
    // validation live in PacketFrame.
    //
    // Negotiation follows the ServerSync / AdminSync handshake, which vanilla routing requires: one
    // Register<ZPackage> on ZRoutedRpc.instance, a client reaching the server by targeting 0L (the receiver
    // always dispatches that locally, while the server peer's m_uid goes unanswered), the server replying to
    // that client's m_uid, the handler resolving the peer by sender id without gating its reply on a status
    // map, and a ZPackage payload rather than bare parameters.
    //
    // What is compressed, and what is left alone, is CompressionPolicy's decision. A receiver decodes any packet that starts with
    // the frame magic and passes PacketFrame's checks, so decoding never depends on handshake timing.
    [HarmonyPatch]
    public static class CompressionGroup
    {
        public static ConfigEntry<bool> ConfigCompressionEnabled;

        private const string RpcCompressionHello = "FiresGhetto.CompHello";

        private const double ReportIntervalSeconds = 300.0;
        private const double RejectWarningIntervalSeconds = 10.0;

        // How long a joined client keeps greeting. ServerSync holds the server's routed RPCs to a joining client until its
        // config sync is sent, which can delay the answer by several seconds on big modpacks.
        private const float HandshakeAnswerSeconds = 60f;

        private static readonly CompressionPolicy s_policy =
            new CompressionPolicy("ZDOData".GetStableHashCode(), "RoutedRPC".GetStableHashCode(), LogEncodeFailure);

        private static readonly Totals s_session = new Totals();
        private static readonly Totals s_reported = new Totals();
        private static long s_nextReportTimestamp;
        private static long s_nextRejectWarningTimestamp;
        private static int s_rejectsSinceWarning;
        private static bool s_encodeFailureLogged;
        private static bool s_commandRegistered;

        // Cached at ZNet.Start, exactly as AdminSyncing caches it at ZNet.Awake (AdminSyncing.cs:23).
        private static bool _isServer;

        private struct Tally
        {
            public long Packets;
            public long Bytes;
            public long WireBytes;
        }

        private sealed class Totals
        {
            public readonly Tally[] Sent = new Tally[(int)SendOutcome.NotCompressed + 1];
            public long EncodeTicks;
            public Tally Decoded;
            public long Rejected;
            public long DecodeTicks;

            public void CopyTo(Totals other)
            {
                Array.Copy(Sent, other.Sent, Sent.Length);
                other.EncodeTicks = EncodeTicks;
                other.Decoded = Decoded;
                other.Rejected = Rejected;
                other.DecodeTicks = DecodeTicks;
            }

            public void Clear() => new Totals().CopyTo(this);
        }

        public static void InitConfig(ConfigFile config)
        {
            ConfigCompressionEnabled = FiresGhettoNetworkMod.ConfigEnableCompression;
            ConfigCompressionEnabled.SettingChanged += (_, __) => SetCompressionEnabledFromConfig();
            CompressionStatus.ourStatus.compressionEnabled = ConfigCompressionEnabled?.Value ?? false;
        }

        private static void SetCompressionEnabledFromConfig()
        {
            CompressionStatus.ourStatus.compressionEnabled = ConfigCompressionEnabled.Value;
            LoggerOptions.LogMessage($"Network compression: {(ConfigCompressionEnabled.Value ? "Enabled" : "Disabled")}");
            // Re-greet every peer so a live config change re-negotiates.
            if (ZNet.instance == null || ZRoutedRpc.instance == null) return;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer?.m_socket == null) continue;
                var status = CompressionStatus.GetOrAddStatus(peer.m_socket);
                if (status == null) continue;
                status.helloSent = true;
                SendHelloToPeer(peer);
            }
        }

        // ====================== COMPRESSION STATUS ======================
        // Keyed by the peer's real socket: ServerSync can wrap peer.m_socket, while the send and receive hooks run on the
        // ZSteamSocket itself.
        internal static class CompressionStatus
        {
            public const int CompressionVersion = 9;
            public static readonly SocketStatus ourStatus = new SocketStatus { version = CompressionVersion, compressionEnabled = false };
            private static readonly Dictionary<ISocket, SocketStatus> peerStatus = new Dictionary<ISocket, SocketStatus>();

            public class SocketStatus
            {
                public int version = 0;
                public bool compressionEnabled = false;
                public bool sendingCompressed = false;
                public bool helloSent = false;
            }

            private static ISocket Key(ISocket socket) => socket == null ? null : NetworkingRatesGroup.UnwrapSocket(socket);

            public static void AddPeer(ISocket socket)
            {
                ISocket key = Key(socket);
                if (key == null) return;
                peerStatus[key] = new SocketStatus();
                LoggerOptions.LogInfo($"Compression: New peer connected {key.GetEndPointString()}");
            }

            public static void RemovePeer(ISocket socket)
            {
                ISocket key = Key(socket);
                if (key != null) peerStatus.Remove(key);
            }

            public static SocketStatus GetStatus(ISocket socket)
            {
                ISocket key = Key(socket);
                return key != null && peerStatus.TryGetValue(key, out var status) ? status : null;
            }

            /// <summary>The status for a socket already known to be the peer's real socket, without the unwrap lookup.</summary>
            public static SocketStatus GetStatusOfRealSocket(ISocket socket) =>
                peerStatus.TryGetValue(socket, out var status) ? status : null;

            // Never returns null for a real socket. The handler must not drop a hello just because
            // OnNewConnection hasn't recorded this socket yet — that was a way the reply got skipped.
            // Mirrors how AdminSyncing's handler never gates on a status map.
            public static SocketStatus GetOrAddStatus(ISocket socket)
            {
                ISocket key = Key(socket);
                if (key == null) return null;
                if (!peerStatus.TryGetValue(key, out var status))
                {
                    status = new SocketStatus();
                    peerStatus[key] = status;
                }
                return status;
            }

            public static bool GetSendCompressionStarted(ISocket socket) => GetStatus(socket)?.sendingCompressed ?? false;
        }

        // ====================== CONNECTION + NEGOTIATION ======================
        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        [HarmonyPostfix]
        static void OnNewConnection(ZNetPeer peer)
        {
            // Just track the socket — the routed-RPC handler is registered globally at the ready gate,
            // and the client (not this hook) drives the exchange once it's fully connected.
            CompressionStatus.AddPeer(peer?.m_socket);
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
        [HarmonyPostfix]
        static void OnDisconnect(ZNetPeer peer)
        {
            CompressionStatus.RemovePeer(peer?.m_socket);
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        [HarmonyPostfix]
        static void OnZNetStart()
        {
            // Register on ZRoutedRpc.instance at ZNet.Start — the same point fgn_comptest's working
            // routed RPCs register, re-bound on the fresh instance each world load. Single ZPackage
            // handler, matching AdminSyncing.cs:26 (Register<ZPackage>) rather than bare (int,bool).
            _isServer = ZNet.instance != null && ZNet.instance.IsServer();
            if (ZRoutedRpc.instance != null)
                ZRoutedRpc.instance.Register<ZPackage>(RpcCompressionHello, RPC_CompHello);
            if (FiresGhettoNetworkMod.Instance != null)
                FiresGhettoNetworkMod.Instance.StartCoroutine(CompressionReadyGate());

            if (s_commandRegistered) return;
            s_commandRegistered = true;
            new Terminal.ConsoleCommand("fgn_compstats",
                "FGN: print this side's network compression totals since the world loaded: what was compressed, what was "
                + "left alone and why, bytes saved, and time spent.",
                new Terminal.ConsoleEvent(args =>
                {
                    foreach (string line in DescribeTotals(s_session, "since the world loaded"))
                        args.Context?.AddString(line);
                }));
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        [HarmonyPostfix]
        static void OnZNetShutdown()
        {
            ReportInterval("since the last report");
            s_session.Clear();
            s_reported.Clear();
            s_policy.Reset();
            s_nextReportTimestamp = 0;
        }

        // The client drives the handshake once the world is wired (ZNetScene + ObjectDB exist — the
        // same first-reliable-send gate GuildSync uses, GuildSyncManager.cs:143-149). It greets the
        // server via 0L and retries until the server's reply flips us on, mirroring GuildSync's
        // RequestSync + RetryGuildSync watchdog. The server only answers (in RPC_CompHello).
        private static IEnumerator CompressionReadyGate()
        {
            while (ZNetScene.instance == null || ObjectDB.instance == null)
                yield return null;
            yield return new WaitForEndOfFrame();
            if (ZRoutedRpc.instance == null || ZNet.instance == null) yield break;
            LoggerOptions.LogInfo($"Compression: ready gate reached (server={_isServer}).");
            if (_isServer) yield break;

            // The server drops our routed RPCs until it has accepted this client, and a password prompt alone can hold
            // that back for longer than the greeting window, so greet only once the join has completed.
            while (ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected)
            {
                if (ZNet.instance == null || ZNet.GetConnectionStatus() > ZNet.ConnectionStatus.Connected) yield break;
                yield return null;
            }
            LoggerOptions.LogInfo("Compression: the server accepted this client; greeting it.");

            float deadline = Time.time + HandshakeAnswerSeconds;
            int attempt = 0;
            while (Time.time < deadline)
            {
                if (ZNet.instance == null || ZRoutedRpc.instance == null) yield break;
                ZNetPeer server = ZNet.instance.GetPeers().FirstOrDefault(p => p != null && p.m_server)
                                  ?? ZNet.instance.GetPeers().FirstOrDefault();
                var status = server?.m_socket != null ? CompressionStatus.GetOrAddStatus(server.m_socket) : null;
                if (status != null && status.sendingCompressed) yield break;   // negotiated — done
                if (status != null) status.helloSent = true;
                attempt++;
                LoggerOptions.LogInfo($"Compression: greeting server via 0L routed RPC (attempt {attempt}).");
                SendHelloToServer();
                yield return new WaitForSeconds(2f);
            }
            LoggerOptions.LogWarning($"Compression: the server has not answered {HandshakeAnswerSeconds:0}s after accepting this client, "
                + "so it may not run the same compression version. Greetings stop; a late answer still turns compression on.");
        }

        // Our (version, enabled) as a ZPackage — the only payload shape the proven handshakes use
        // (AdminSyncing.cs:60-61). Read back in the same order in RPC_CompHello.
        private static ZPackage BuildHelloPackage()
        {
            var pkg = new ZPackage();
            pkg.Write(CompressionStatus.ourStatus.version);
            pkg.Write(CompressionStatus.ourStatus.compressionEnabled);
            return pkg;
        }

        // Client -> server. Target 0L (ZRoutedRpc.Everybody): the receiver ALWAYS dispatches it
        // locally (ZRoutedRpc.cs:127), so it reaches the server's handler regardless of peer-uid
        // timing. This is GuildSyncManager.SendRoute's exact target (GuildSyncManager.cs:427).
        private static void SendHelloToServer()
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(0L, RpcCompressionHello, BuildHelloPackage());
        }

        // Reply to a specific peer: peer.m_server ? 0L : peer.m_uid — verbatim ServerSync/ArbbyStuffs
        // (ConfigSync.cs:671, AdminSyncing.cs:123). For a connected client this resolves to peer.m_uid.
        private static void SendHelloToPeer(ZNetPeer peer)
        {
            if (ZRoutedRpc.instance == null || peer == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_server ? 0L : peer.m_uid, RpcCompressionHello, BuildHelloPackage());
        }

        // Both sides run the same handler: record the sender's (version, enabled), decide agreement,
        // reply exactly once. Resolve the peer by sender id and never gate the reply on a status map —
        // mirrors AdminSyncing.RPC_AdminStatusSync (AdminSyncing.cs:66-79).
        private static void RPC_CompHello(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || pkg == null) return;
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            if (peer == null) return;   // self-dispatch of our own 0L send (sender = us) lands here — ignore

            int version = pkg.ReadInt();
            bool enabled = pkg.ReadBool();
            LoggerOptions.LogInfo($"Compression: CompHello received from {GetPeerName(peer)} (peer v{version} enabled={enabled}).");

            var status = CompressionStatus.GetOrAddStatus(peer.m_socket);
            if (status == null) return;
            status.version = version;
            status.compressionEnabled = enabled;
            bool agree = version == CompressionStatus.ourStatus.version
                         && enabled
                         && CompressionStatus.ourStatus.compressionEnabled;
            status.sendingCompressed = agree;
            LoggerOptions.LogMessage($"Compression {(agree ? "ACTIVE" : "off")} with {GetPeerName(peer)} "
                + $"(us v{CompressionStatus.ourStatus.version} enabled={CompressionStatus.ourStatus.compressionEnabled}, "
                + $"peer v{version} enabled={enabled}).");

            // Reply exactly once so the initiator learns our (version, enabled) too.
            if (!status.helloSent)
            {
                status.helloSent = true;
                SendHelloToPeer(peer);
            }
        }

        // ====================== SEND ======================
        /// <summary>
        /// Compresses each packet once, as ZSteamSocket.Send queues it. Rebuilding the queue on every SendQueuedPackages
        /// call re-deflated each queued packet the header check could not skip (those Deflate did not shrink) on every
        /// send, every frame and every flush, for as long as a backed-up peer kept it waiting.
        /// </summary>
        [HarmonyPatch(typeof(ZSteamSocket), nameof(ZSteamSocket.Send), new[] { typeof(ZPackage) })]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Steam_CompressOnEnqueue(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo getArray = AccessTools.Method(typeof(ZPackage), nameof(ZPackage.GetArray));
            MethodInfo packetForQueue = AccessTools.Method(typeof(CompressionGroup), nameof(PacketForSendQueue));
            var code = new List<CodeInstruction>(instructions);
            int wrapped = 0;

            for (int i = 0; i < code.Count; i++)
            {
                if (!code[i].Calls(getArray)) continue;

                var loadSocket = new CodeInstruction(OpCodes.Ldarg_0);
                loadSocket.labels.AddRange(code[i].labels);
                loadSocket.blocks.AddRange(code[i].blocks);
                code[i] = loadSocket;
                code.Insert(i + 1, new CodeInstruction(OpCodes.Call, packetForQueue));
                i++;
                wrapped++;
            }

            if (wrapped == 1) return code;

            LoggerOptions.LogWarning($"Compression: ZSteamSocket.Send has {wrapped} GetArray calls instead of 1; outgoing packets stay uncompressed this session.");
            return instructions;
        }

        public static byte[] PacketForSendQueue(ZPackage pkg, ZSteamSocket socket)
        {
            byte[] packet = pkg.GetArray();
            CompressionStatus.SocketStatus status = CompressionStatus.GetStatusOfRealSocket(socket);
            if (status == null) return packet;

            long start = Stopwatch.GetTimestamp();
            byte[] wire = FrameForPeer(packet, status.sendingCompressed,
                status.version == CompressionStatus.CompressionVersion, out SendOutcome outcome);
            if (outcome != SendOutcome.NotCompressed)
            {
                s_session.EncodeTicks += Stopwatch.GetTimestamp() - start;
                ref Tally tally = ref s_session.Sent[(int)outcome];
                tally.Packets++;
                tally.Bytes += packet.Length;
                tally.WireBytes += wire.Length;
            }
            MaybeReport();
            return wire;
        }

        internal static byte[] FrameForPeer(byte[] packet, bool compress, bool peerReadsFrames, out SendOutcome outcome) =>
            s_policy.FrameForPeer(packet, compress, peerReadsFrames, out outcome);

        private static void LogEncodeFailure(Exception ex)
        {
            if (s_encodeFailureLogged) return;
            s_encodeFailureLogged = true;
            LoggerOptions.LogWarning($"[Compression] Compressing a packet failed ({ex.GetType().Name}: {ex.Message}); it and any others that fail are sent uncompressed.");
        }

        // ====================== RECEIVE ======================
        // Decode on the frame itself, never on a per-socket flag, so no handshake timing can leave a frame unread. A packet
        // that begins with the magic but fails PacketFrame's checks is left as it arrived; its method hash is the magic, which
        // no RPC is registered under, so Valheim ignores it.
        [HarmonyPatch(typeof(ZSteamSocket), nameof(ZSteamSocket.Recv))]
        [HarmonyPostfix]
        static void Steam_RecvCompressed(ref ZPackage __result, ZSteamSocket __instance)
        {
            if (__result == null || __result.Size() < PacketFrame.HeaderBytes) return;
            int method = __result.ReadInt();
            __result.SetPos(0);
            if (method != PacketFrame.Magic) return;

            byte[] wire = __result.GetArray();
            long start = Stopwatch.GetTimestamp();
            PacketFrame.DecodeResult result = PacketFrame.TryDecode(wire, out byte[] packet);
            s_session.DecodeTicks += Stopwatch.GetTimestamp() - start;

            if (result == PacketFrame.DecodeResult.Decoded)
            {
                s_session.Decoded.Packets++;
                s_session.Decoded.Bytes += packet.Length;
                s_session.Decoded.WireBytes += wire.Length;
                __result = new ZPackage(packet);
            }
            else
            {
                s_session.Rejected++;
                WarnRejected(__instance, result, wire.Length);
            }
            MaybeReport();
        }

        private static void WarnRejected(ZSteamSocket socket, PacketFrame.DecodeResult result, int bytes)
        {
            s_rejectsSinceWarning++;
            long now = Stopwatch.GetTimestamp();
            if (now < s_nextRejectWarningTimestamp) return;
            s_nextRejectWarningTimestamp = now + (long)(RejectWarningIntervalSeconds * Stopwatch.Frequency);

            string from;
            try { from = socket.GetEndPointString(); }
            catch { from = "unknown peer"; }
            LoggerOptions.LogWarning($"[Compression] {s_rejectsSinceWarning} packet(s) from {from} began with the frame marker but failed "
                + $"its checks (latest: {result}, {bytes} B); Valheim ignores them.");
            s_rejectsSinceWarning = 0;
        }

        // ====================== REPORTING ======================
        private static void MaybeReport()
        {
            long now = Stopwatch.GetTimestamp();
            if (s_nextReportTimestamp == 0)
            {
                s_nextReportTimestamp = now + (long)(ReportIntervalSeconds * Stopwatch.Frequency);
                return;
            }
            if (now < s_nextReportTimestamp) return;
            s_nextReportTimestamp = now + (long)(ReportIntervalSeconds * Stopwatch.Frequency);
            ReportInterval($"in the last {ReportIntervalSeconds / 60:0} min");
        }

        private static void ReportInterval(string period)
        {
            var interval = new Totals();
            for (int i = 0; i < interval.Sent.Length; i++)
            {
                interval.Sent[i].Packets = s_session.Sent[i].Packets - s_reported.Sent[i].Packets;
                interval.Sent[i].Bytes = s_session.Sent[i].Bytes - s_reported.Sent[i].Bytes;
                interval.Sent[i].WireBytes = s_session.Sent[i].WireBytes - s_reported.Sent[i].WireBytes;
            }
            interval.EncodeTicks = s_session.EncodeTicks - s_reported.EncodeTicks;
            interval.Decoded.Packets = s_session.Decoded.Packets - s_reported.Decoded.Packets;
            interval.Decoded.Bytes = s_session.Decoded.Bytes - s_reported.Decoded.Bytes;
            interval.Decoded.WireBytes = s_session.Decoded.WireBytes - s_reported.Decoded.WireBytes;
            interval.Rejected = s_session.Rejected - s_reported.Rejected;
            interval.DecodeTicks = s_session.DecodeTicks - s_reported.DecodeTicks;
            s_session.CopyTo(s_reported);

            if (interval.Sent.All(tally => tally.Packets == 0) && interval.Decoded.Packets == 0 && interval.Rejected == 0) return;
            foreach (string line in DescribeTotals(interval, period))
                LoggerOptions.LogInfo(line);
        }

        private static IEnumerable<string> DescribeTotals(Totals totals, string period)
        {
            long packets = 0, bytes = 0, wire = 0;
            foreach (Tally tally in totals.Sent)
            {
                packets += tally.Packets;
                bytes += tally.Bytes;
                wire += tally.WireBytes;
            }

            if (packets > 0)
            {
                yield return $"[Compression] Sent {period}: {packets} packets, {Kb(bytes)} KB became {Kb(wire)} KB ({Percent(wire, bytes)}), "
                    + $"{Ms(totals.EncodeTicks)} ms compressing. "
                    + $"Deflated {Describe(totals, SendOutcome.Deflated)}; deflated beside Valheim's gzip data {Describe(totals, SendOutcome.Segmented)}; "
                    + $"already gzip, left as is {Describe(totals, SendOutcome.AllGzip)}; did not shrink {Describe(totals, SendOutcome.DidNotShrink)}; "
                    + $"under {CompressionPolicy.MinCompressBytes} B {Describe(totals, SendOutcome.Small)}; skipped RPCs that do not compress {Describe(totals, SendOutcome.SkippedRpc)}; "
                    + $"escaped {totals.Sent[(int)SendOutcome.Escaped].Packets}; failed {totals.Sent[(int)SendOutcome.EncodeFailed].Packets}.";
            }

            if (totals.Decoded.Packets > 0 || totals.Rejected > 0)
            {
                yield return $"[Compression] Received {period}: {totals.Decoded.Packets} frames, {Kb(totals.Decoded.Bytes)} KB of packets arrived as "
                    + $"{Kb(totals.Decoded.WireBytes)} KB ({Percent(totals.Decoded.WireBytes, totals.Decoded.Bytes)}), {Ms(totals.DecodeTicks)} ms decoding; "
                    + $"{totals.Rejected} rejected.";
            }

            if (packets == 0 && totals.Decoded.Packets == 0 && totals.Rejected == 0)
                yield return $"[Compression] Nothing compressed or decoded {period}.";
        }

        private static string Describe(Totals totals, SendOutcome outcome)
        {
            Tally tally = totals.Sent[(int)outcome];
            return tally.Bytes == tally.WireBytes
                ? $"{tally.Packets} ({Kb(tally.Bytes)} KB)"
                : $"{tally.Packets} ({Kb(tally.Bytes)} -> {Kb(tally.WireBytes)} KB)";
        }

        private static long Kb(long bytes) => (bytes + 512) / 1024;

        private static string Percent(long part, long whole) => whole > 0 ? $"{part * 100.0 / whole:0}%" : "n/a";

        private static string Ms(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("0");

        private static string GetPeerName(ZNetPeer peer)
        {
            if (peer == null) return "unknown";
            try
            {
                if (peer.m_socket != null)
                    return peer.m_socket.GetEndPointString();
            }
            catch { }
            return peer.m_uid.ToString();
        }
    }
}
