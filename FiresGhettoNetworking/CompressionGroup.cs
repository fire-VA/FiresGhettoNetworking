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
        public static ConfigEntry<bool> ConfigCompressionEnabled;

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
            private const int COMPRESSION_VERSION = 6;
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
            if (compressor == null) return data;
            var compType = compressor.GetType();
            var wrapMethod = compType.GetMethod("Wrap", new Type[] { typeof(byte[]) }) ?? compType.GetMethod("Wrap");
            var result = wrapMethod.Invoke(compressor, new object[] { data });
            if (result is byte[] arr) return arr;
            var toArray = result?.GetType().GetMethod("ToArray", Type.EmptyTypes);
            if (toArray != null) return (byte[])toArray.Invoke(result, null);
            return data;
        }

        internal static byte[] Decompress(byte[] data)
        {
            if (decompressor == null) throw new Exception("Decompressor not initialized");
            var decompType = decompressor.GetType();
            var unwrapMethod = decompType.GetMethod("Unwrap", new Type[] { typeof(byte[]) }) ?? decompType.GetMethod("Unwrap");
            var result = unwrapMethod.Invoke(decompressor, new object[] { data });
            if (result is byte[] arr) return arr;
            var toArray = result?.GetType().GetMethod("ToArray", Type.EmptyTypes);
            if (toArray != null) return (byte[])toArray.Invoke(result, null);
            throw new Exception("Failed to decompress data");
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

        [HarmonyPatch(typeof(ZSteamSocket), nameof(ZSteamSocket.Recv))]
        [HarmonyPostfix]
        static void Steam_RecvCompressed(ref ZPackage __result, ZSteamSocket __instance)
        {
            if (__result == null || decompressor == null) return;

            var status = CompressionStatus.GetStatus(__instance);
            if (status == null || !status.receivingCompressed) return;

            try
            {
                __result = new ZPackage(Decompress(__result.GetArray()));
            }
            catch
            {
                LoggerOptions.LogWarning("Failed to decompress incoming Steamworks package - falling back to uncompressed");
                status.receivingCompressed = false;
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