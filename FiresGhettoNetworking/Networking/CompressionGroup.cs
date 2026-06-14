using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    [HarmonyPatch]
    public static class CompressionGroup
    {
        private static string ZSTD_DICT_RESOURCE_NAME = "FiresGhettoNetworkMod.dict.small";
        private static int ZSTD_LEVEL = 1;
        private static object compressor;
        private static object decompressor;
        // Resolved once at InitCompressor instead of per-packet. Type.GetMethod walks
        // the type's method table on every call; doing that for every Wrap/Unwrap on
        // the send/recv hot path was pure overhead. Cache the MethodInfo and reuse.
        private static MethodInfo _wrapMethod;
        private static MethodInfo _unwrapMethod;
        public static ConfigEntry<bool> ConfigCompressionEnabled;

        private static readonly byte[] CompressionMagic = { (byte)'F', (byte)'G', (byte)'Z', (byte)'7' };

        private const string RPC_COMPRESSION_VERSION = "FiresGhetto.CompressionVersion";
        private const string RPC_COMPRESSION_ENABLED = "FiresGhetto.CompressionEnabled";
        private const string RPC_COMPRESSION_STARTED = "FiresGhetto.CompressedStarted";

        public static void InitConfig(ConfigFile config)
        {
            ConfigCompressionEnabled = FiresGhettoNetworkMod.ConfigEnableCompression;
            ConfigCompressionEnabled.SettingChanged += (_, __) => SetCompressionEnabledFromConfig();
            CompressionStatus.ourStatus.compressionEnabled = ConfigCompressionEnabled?.Value ?? false;
        }

        public static void InitCompressor()
        {
            try
            {
                var compType = Type.GetType("ZstdSharp.Compressor, ZstdSharp");
                var decompType = Type.GetType("ZstdSharp.Decompressor, ZstdSharp");
                if (compType == null || decompType == null)
                {
                    LoggerOptions.LogWarning("ZstdSharp assembly not found - compression disabled.");
                    return;
                }

                byte[] dict;
                var assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream(ZSTD_DICT_RESOURCE_NAME))
                {
                    if (stream == null)
                    {
                        LoggerOptions.LogError("Compression dictionary resource not found. Compression disabled.");
                        return;
                    }
                    dict = new byte[stream.Length];
                    stream.Read(dict, 0, dict.Length);
                }

                compressor = Activator.CreateInstance(compType, ZSTD_LEVEL);
                compType.GetMethod("LoadDictionary")?.Invoke(compressor, new object[] { dict });
                decompressor = Activator.CreateInstance(decompType);
                decompType.GetMethod("LoadDictionary")?.Invoke(decompressor, new object[] { dict });

                // Cache the per-packet method handles now so Compress/Decompress never
                // do a GetMethod lookup on the hot path again.
                _wrapMethod   = compType.GetMethod("Wrap", new[] { typeof(byte[]) })   ?? compType.GetMethod("Wrap");
                _unwrapMethod = decompType.GetMethod("Unwrap", new[] { typeof(byte[]) }) ?? decompType.GetMethod("Unwrap");
                if (_wrapMethod == null || _unwrapMethod == null)
                {
                    LoggerOptions.LogWarning("ZstdSharp Wrap/Unwrap not found - compression disabled.");
                    compressor = null;
                    decompressor = null;
                    return;
                }

                LoggerOptions.LogInfo("ZSTD compression dictionary loaded successfully.");
            }
            catch (Exception e)
            {
                LoggerOptions.LogError($"Failed to initialize compressor: {e}");
            }
        }

        private static void SetCompressionEnabledFromConfig()
        {
            bool enabled = ConfigCompressionEnabled.Value;
            CompressionStatus.ourStatus.compressionEnabled = enabled;
            LoggerOptions.LogMessage($"Network compression: {(enabled ? "Enabled" : "Disabled")}");
            SendCompressionEnabledStatusToAll();
        }

        // ====================== COMPRESSION STATUS ======================
        internal static class CompressionStatus
        {
            private const int COMPRESSION_VERSION = 7;
            public static readonly SocketStatus ourStatus = new SocketStatus { version = COMPRESSION_VERSION, compressionEnabled = false };
            private static readonly Dictionary<ISocket, SocketStatus> peerStatus = new Dictionary<ISocket, SocketStatus>();

            public class SocketStatus
            {
                public int version = 0;
                public bool compressionEnabled = false;
                public bool sendingCompressed = false;
                public bool receivingCompressed = false;
            }

            public static void AddPeer(ISocket socket)
            {
                if (socket == null) return;
                if (peerStatus.ContainsKey(socket))
                    peerStatus.Remove(socket);
                peerStatus[socket] = new SocketStatus();
                LoggerOptions.LogMessage($"Compression: New peer connected {socket.GetEndPointString()}");
            }

            public static void RemovePeer(ISocket socket)
            {
                peerStatus.Remove(socket);
            }

            public static SocketStatus GetStatus(ISocket socket) =>
                peerStatus.TryGetValue(socket, out var status) ? status : null;

            public static bool IsCompatible(ISocket socket)
            {
                var status = GetStatus(socket);
                return status != null && status.version == ourStatus.version;
            }

            public static bool GetSendCompressionStarted(ISocket socket) => GetStatus(socket)?.sendingCompressed ?? false;
            public static bool GetReceiveCompressionStarted(ISocket socket) => GetStatus(socket)?.receivingCompressed ?? false;
            public static void SetSendCompressionStarted(ISocket socket, bool started) => GetStatus(socket).sendingCompressed = started;
            public static void SetReceiveCompressionStarted(ISocket socket, bool started) => GetStatus(socket).receivingCompressed = started;
        }

        // ====================== CONNECTION HANDLING ======================
        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        [HarmonyPostfix]
        static void OnNewConnection(ZNetPeer peer)
        {
            if (compressor == null) return;
            CompressionStatus.AddPeer(peer.m_socket);
            RegisterRPCs(peer);
            SendCompressionVersion(peer);
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
        [HarmonyPostfix]
        static void OnDisconnect(ZNetPeer peer)
        {
            CompressionStatus.RemovePeer(peer.m_socket);
        }

        private static void RegisterRPCs(ZNetPeer peer)
        {
            peer.m_rpc.Register<int>(RPC_COMPRESSION_VERSION, RPC_CompressionVersion);
            peer.m_rpc.Register<bool>(RPC_COMPRESSION_ENABLED, RPC_CompressionEnabled);
            peer.m_rpc.Register<bool>(RPC_COMPRESSION_STARTED, RPC_CompressionStarted);
        }

        private static void SendCompressionVersion(ZNetPeer peer)
        {
            peer.m_rpc.Invoke(RPC_COMPRESSION_VERSION, CompressionStatus.ourStatus.version);
        }

        private static void RPC_CompressionVersion(ZRpc rpc, int version)
        {
            ZNetPeer peer = FindPeerByRpc(rpc);
            if (peer == null) return;
            var status = CompressionStatus.GetStatus(peer.m_socket);
            if (status != null)
                status.version = version;

            if (version == CompressionStatus.ourStatus.version)
                LoggerOptions.LogMessage($"Compression compatible with {GetPeerName(peer)}");
            else
                LoggerOptions.LogWarning($"Compression version mismatch with {GetPeerName(peer)} (them: {version}, us: {CompressionStatus.ourStatus.version})");

            if (CompressionStatus.IsCompatible(peer.m_socket))
                SendCompressionEnabledStatus(peer);
        }

        private static void SendCompressionEnabledStatusToAll()
        {
            if (ZNet.instance == null) return;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (CompressionStatus.IsCompatible(peer.m_socket))
                    SendCompressionEnabledStatus(peer);
            }
        }

        private static void SendCompressionEnabledStatus(ZNetPeer peer)
        {
            peer.m_rpc.Invoke(RPC_COMPRESSION_ENABLED, CompressionStatus.ourStatus.compressionEnabled);
            bool shouldCompress = CompressionStatus.ourStatus.compressionEnabled && CompressionStatus.GetStatus(peer.m_socket)?.compressionEnabled == true;
            SendCompressionStarted(peer, shouldCompress);
        }

        private static void RPC_CompressionEnabled(ZRpc rpc, bool enabled)
        {
            ZNetPeer peer = FindPeerByRpc(rpc);
            if (peer == null) return;
            var status = CompressionStatus.GetStatus(peer.m_socket);
            if (status != null)
                status.compressionEnabled = enabled;

            bool shouldCompress = CompressionStatus.ourStatus.compressionEnabled && enabled;
            SendCompressionStarted(peer, shouldCompress);
        }

        private static void SendCompressionStarted(ZNetPeer peer, bool started)
        {
            var status = CompressionStatus.GetStatus(peer.m_socket);
            if (status == null || status.sendingCompressed == started) return;

            peer.m_rpc.Invoke(RPC_COMPRESSION_STARTED, started);
            Flush(peer);
            status.sendingCompressed = started;
            LoggerOptions.LogMessage($"Compression {(started ? "started" : "stopped")} with {GetPeerName(peer)}");
        }

        private static void Flush(ZNetPeer peer)
        {
            switch (ZNet.m_onlineBackend)
            {
                case OnlineBackendType.Steamworks:
                    peer.m_socket.Flush();
                    break;
                case OnlineBackendType.PlayFab:
                    // Placeholder for PlayFab flush
                    break;
            }
        }

        private static void RPC_CompressionStarted(ZRpc rpc, bool started)
        {
            ZNetPeer peer = FindPeerByRpc(rpc);
            if (peer == null) return;
            var status = CompressionStatus.GetStatus(peer.m_socket);
            if (status != null)
                status.receivingCompressed = started;

            LoggerOptions.LogMessage($"Receiving {(started ? "compressed" : "uncompressed")} data from {GetPeerName(peer)}");
        }

        // ====================== ACTUAL COMPRESSION ======================
        internal static byte[] Compress(byte[] data)
        {
            if (compressor == null || _wrapMethod == null) return data;
            if (HasCompressionHeader(data)) return data;

            var result = _wrapMethod.Invoke(compressor, new object[] { data });
            if (result is byte[] arr) return AddCompressionHeaderIfUseful(data, arr);
            var toArray = result?.GetType().GetMethod("ToArray", Type.EmptyTypes);
            if (toArray != null) return AddCompressionHeaderIfUseful(data, (byte[])toArray.Invoke(result, null));
            return data;
        }

        internal static byte[] Decompress(byte[] data)
        {
            if (!HasCompressionHeader(data)) return data;
            if (decompressor == null || _unwrapMethod == null) throw new Exception("Decompressor not initialized");
            byte[] payload = StripCompressionHeader(data);
            var result = _unwrapMethod.Invoke(decompressor, new object[] { payload });
            if (result is byte[] arr) return arr;
            var toArray = result?.GetType().GetMethod("ToArray", Type.EmptyTypes);
            if (toArray != null) return (byte[])toArray.Invoke(result, null);
            throw new Exception("Failed to decompress data");
        }

        private static bool HasCompressionHeader(byte[] data)
        {
            if (data == null || data.Length < CompressionMagic.Length) return false;

            for (int i = 0; i < CompressionMagic.Length; i++)
            {
                if (data[i] != CompressionMagic[i]) return false;
            }

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

        // Steamworks compression hooks (exact BN)
        [HarmonyPatch(typeof(ZSteamSocket), "SendQueuedPackages")]
        [HarmonyPrefix]
        static bool Steam_SendCompressed(ref Queue<byte[]> ___m_sendQueue, ZSteamSocket __instance)
        {
            if (compressor == null || !CompressionStatus.GetSendCompressionStarted(__instance))
                return true;

            ___m_sendQueue = new Queue<byte[]>(___m_sendQueue.Select(p => Compress(p)));
            return true;
        }

        // Decompress on the framing MAGIC, never on the per-socket receivingCompressed flag.
        //
        // Why: under load Steam's send buffer can be full when SendCompressionStarted flushes —
        // ZSteamSocket.SendQueuedPackages gets k_EResultLimitExceeded and BREAKS, leaving packets
        // queued. The "compression started" control packet is then still in the queue when
        // sendingCompressed flips true, so it ships COMPRESSED. A receivingCompressed-gated reader
        // never sees that signal, so it keeps reading every subsequent compressed packet as raw
        // bytes -> permanent desync -> disconnect (the "many players sending at once" corruption,
        // hit by joiners during a busy event). Keying off the magic makes every packet
        // self-describing: a compressed control packet decompresses fine and the start boundary
        // can never corrupt the stream. Wire format is unchanged, so this interoperates with peers
        // still on the old behaviour.
        [HarmonyPatch(typeof(ZSteamSocket), nameof(ZSteamSocket.Recv))]
        [HarmonyPostfix]
        static void Steam_RecvCompressed(ref ZPackage __result, ZSteamSocket __instance)
        {
            if (__result == null || decompressor == null) return;

            byte[] bytes = __result.GetArray();
            if (!HasCompressionHeader(bytes)) return;   // no magic -> sent uncompressed, leave as-is

            try
            {
                __result = new ZPackage(Decompress(bytes));
            }
            catch
            {
                // Magic present but the payload didn't decompress — a rare collision where real
                // packet bytes began with the magic, or a one-off. Pass the ORIGINAL packet through
                // unchanged rather than disabling decompression (which would silently desync this
                // peer for the rest of the session). __result still holds the original packet.
                LoggerOptions.LogWarning("Compression: framed packet failed to decompress — passing through unchanged.");
            }
        }

        // Helper methods
        private static ZNetPeer FindPeerByRpc(ZRpc rpc)
        {
            try
            {
                if (rpc == null || ZRoutedRpc.instance == null) return null;
                var peers = (List<ZNetPeer>)AccessTools.Field(typeof(ZRoutedRpc), "m_peers").GetValue(ZRoutedRpc.instance);
                return peers?.FirstOrDefault(p => p.m_rpc == rpc);
            }
            catch
            {
                return null;
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