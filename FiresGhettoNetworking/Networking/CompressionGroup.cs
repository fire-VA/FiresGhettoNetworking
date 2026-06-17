using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    // Per-packet network compression using the runtime's built-in Deflate (System.IO.Compression).
    // No external library, no dictionary, no extra DLLs.
    //
    // Negotiation is copied from the proven ServerSync / ArbbyStuffs-AdminSync handshake (the shape
    // every config-sync mod uses), verified against vanilla routing in assembly_valheim\ZRoutedRpc.cs:
    //   * one Register<ZPackage> on ZRoutedRpc.instance (NOT bare typed params, NOT per-peer m_rpc) —
    //     matches AdminSyncing.cs:26;
    //   * a client reaches the server by targeting 0L (ZRoutedRpc.Everybody), which the receiver
    //     ALWAYS dispatches locally (ZRoutedRpc.cs:127). Targeting the server peer's m_uid was the bug
    //     that left every greet unanswered. See GuildSyncManager.cs:427 and ConfigSync.cs:671
    //     (peer.m_server ? 0L : peer.m_uid);
    //   * the server replies to a client by targeting that client's peer.m_uid;
    //   * the handler resolves the peer by sender id and NEVER gates its reply on a status map
    //     (AdminSyncing.cs:66) — the old "if (status == null) return;" was the silent dead-end;
    //   * the payload is always a ZPackage (AdminSyncing.cs:60), never bare (int,bool).
    [HarmonyPatch]
    public static class CompressionGroup
    {
        public static ConfigEntry<bool> ConfigCompressionEnabled;

        // Self-describing frame: every compressed packet carries this magic prefix so the receiver
        // decompresses on the marker alone, never a per-socket flag. "FGD1" = Fires Ghetto Deflate v1.
        private static readonly byte[] CompressionMagic = { (byte)'F', (byte)'G', (byte)'D', (byte)'1' };

        private const string RPC_COMP_HELLO = "FiresGhetto.CompHello";

        // Cached at ZNet.Start, exactly as AdminSyncing caches it at ZNet.Awake (AdminSyncing.cs:23).
        private static bool _isServer;

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
        internal static class CompressionStatus
        {
            private const int COMPRESSION_VERSION = 8;
            public static readonly SocketStatus ourStatus = new SocketStatus { version = COMPRESSION_VERSION, compressionEnabled = false };
            private static readonly Dictionary<ISocket, SocketStatus> peerStatus = new Dictionary<ISocket, SocketStatus>();

            public class SocketStatus
            {
                public int version = 0;
                public bool compressionEnabled = false;
                public bool sendingCompressed = false;
                public bool helloSent = false;
            }

            public static void AddPeer(ISocket socket)
            {
                if (socket == null) return;
                peerStatus[socket] = new SocketStatus();
                LoggerOptions.LogMessage($"Compression: New peer connected {socket.GetEndPointString()}");
            }

            public static void RemovePeer(ISocket socket)
            {
                if (socket != null) peerStatus.Remove(socket);
            }

            public static SocketStatus GetStatus(ISocket socket) =>
                socket != null && peerStatus.TryGetValue(socket, out var status) ? status : null;

            // Never returns null for a real socket. The handler must not drop a hello just because
            // OnNewConnection hasn't recorded this socket yet — that was a way the reply got skipped.
            // Mirrors how AdminSyncing's handler never gates on a status map.
            public static SocketStatus GetOrAddStatus(ISocket socket)
            {
                if (socket == null) return null;
                if (!peerStatus.TryGetValue(socket, out var status))
                {
                    status = new SocketStatus();
                    peerStatus[socket] = status;
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
                ZRoutedRpc.instance.Register<ZPackage>(RPC_COMP_HELLO, RPC_CompHello);
            if (FiresGhettoNetworkMod.Instance != null)
                FiresGhettoNetworkMod.Instance.StartCoroutine(CompressionReadyGate());
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
            LoggerOptions.LogMessage($"Compression: ready gate reached (server={_isServer}).");
            if (_isServer) yield break;

            float deadline = Time.time + 30f;
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
                LoggerOptions.LogMessage($"Compression: greeting server via 0L routed RPC (attempt {attempt}).");
                SendHelloToServer();
                yield return new WaitForSeconds(2f);
            }
            LoggerOptions.LogWarning("Compression: handshake never completed in 30s — compression stays off this session.");
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
            ZRoutedRpc.instance.InvokeRoutedRPC(0L, RPC_COMP_HELLO, BuildHelloPackage());
        }

        // Reply to a specific peer: peer.m_server ? 0L : peer.m_uid — verbatim ServerSync/ArbbyStuffs
        // (ConfigSync.cs:671, AdminSyncing.cs:123). For a connected client this resolves to peer.m_uid.
        private static void SendHelloToPeer(ZNetPeer peer)
        {
            if (ZRoutedRpc.instance == null || peer == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_server ? 0L : peer.m_uid, RPC_COMP_HELLO, BuildHelloPackage());
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
            LoggerOptions.LogMessage($"Compression: CompHello received from {GetPeerName(peer)} (peer v{version} enabled={enabled}).");

            var status = CompressionStatus.GetOrAddStatus(peer.m_socket);
            if (status == null) return;
            status.version = version;
            status.compressionEnabled = enabled;
            bool agree = version == CompressionStatus.ourStatus.version
                         && enabled
                         && CompressionStatus.ourStatus.compressionEnabled;
            status.sendingCompressed = agree;
            LoggerOptions.LogMessage($"Compression {(agree ? "ACTIVE" : "off")} with {GetPeerName(peer)} "
                + $"(us v{CompressionStatus.ourStatus.version} enabled={CompressionStatus.ourStatus.compressionEnabled}).");

            // Reply exactly once so the initiator learns our (version, enabled) too.
            if (!status.helloSent)
            {
                status.helloSent = true;
                SendHelloToPeer(peer);
            }
        }

        // ====================== ACTUAL COMPRESSION (built-in Deflate) ======================
        internal static byte[] Compress(byte[] data)
        {
            if (data == null || data.Length == 0) return data;
            if (HasCompressionHeader(data)) return data;
            return AddCompressionHeaderIfUseful(data, Deflate(data));
        }

        internal static byte[] Decompress(byte[] data)
        {
            if (!HasCompressionHeader(data)) return data;
            return Inflate(StripCompressionHeader(data));
        }

        private static byte[] Deflate(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                using (var stream = new DeflateStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                    stream.Write(data, 0, data.Length);
                return output.ToArray();
            }
        }

        private static byte[] Inflate(byte[] data)
        {
            using (var input = new MemoryStream(data))
            using (var stream = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                stream.CopyTo(output);
                return output.ToArray();
            }
        }

        private static bool HasCompressionHeader(byte[] data)
        {
            if (data == null || data.Length < CompressionMagic.Length) return false;
            for (int i = 0; i < CompressionMagic.Length; i++)
                if (data[i] != CompressionMagic[i]) return false;
            return true;
        }

        private static byte[] AddCompressionHeaderIfUseful(byte[] original, byte[] compressed)
        {
            if (compressed == null || original == null) return original;
            if (compressed.Length + CompressionMagic.Length >= original.Length) return original;

            byte[] framed = new byte[compressed.Length + CompressionMagic.Length];
            Buffer.BlockCopy(CompressionMagic, 0, framed, 0, CompressionMagic.Length);
            Buffer.BlockCopy(compressed, 0, framed, CompressionMagic.Length, compressed.Length);
            return framed;
        }

        private static byte[] StripCompressionHeader(byte[] data)
        {
            byte[] payload = new byte[data.Length - CompressionMagic.Length];
            Buffer.BlockCopy(data, CompressionMagic.Length, payload, 0, payload.Length);
            return payload;
        }

        // Steamworks compression hooks
        [HarmonyPatch(typeof(ZSteamSocket), "SendQueuedPackages")]
        [HarmonyPrefix]
        static bool Steam_SendCompressed(ref Queue<byte[]> ___m_sendQueue, ZSteamSocket __instance)
        {
            if (!CompressionStatus.GetSendCompressionStarted(__instance))
                return true;

            ___m_sendQueue = new Queue<byte[]>(___m_sendQueue.Select(p => Compress(p)));
            return true;
        }

        // Decompress on the framing MAGIC, never on a per-socket flag — every packet is self-describing,
        // so the negotiation boundary can never corrupt the stream. On a decompress miss the ORIGINAL
        // packet passes through unchanged rather than disabling decompression for the session.
        [HarmonyPatch(typeof(ZSteamSocket), nameof(ZSteamSocket.Recv))]
        [HarmonyPostfix]
        static void Steam_RecvCompressed(ref ZPackage __result, ZSteamSocket __instance)
        {
            if (__result == null) return;

            byte[] bytes = __result.GetArray();
            if (!HasCompressionHeader(bytes)) return;   // no magic -> sent uncompressed, leave as-is

            try
            {
                __result = new ZPackage(Decompress(bytes));
            }
            catch
            {
                LoggerOptions.LogWarning("Compression: framed packet failed to decompress — passing through unchanged.");
            }
        }

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
