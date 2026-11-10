using System;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    public static class ServerClientUtils
    {
        public static bool IsDedicatedServerDetected { get; private set; }

        // valheim.exe on Windows, valheim.x86_64 on Linux; the dedicated server is valheim_server.
        private const string ClientExeName = "valheim";

        // Direct logger reference for early detection (before LoggerOptions filtering is configured)
        private static ManualLogSource _earlyLogger;

        // Call once at startup (e.g. from DedicatedServerGroup.Init)
        public static void Detect(ManualLogSource logger = null)
        {
            _earlyLogger = logger;
            IsDedicatedServerDetected = false;
            string exeName = "";

            // One summary line ("Detected: DEDICATED (exe via method)") through the early raw logger.
            void Summarize(string via)
            {
                string side = IsDedicatedServerDetected ? "DEDICATED" : "CLIENT/LISTEN";
                Log($"Detected: {side} ({exeName}{(string.IsNullOrEmpty(via) ? "" : " via " + via)})");
            }

            try
            {
                // Method 1: Check executable name - most reliable early detection
                // Supports: valheim_server.exe, valheim_server1.exe, server2.exe, myserver.exe, etc.
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                exeName = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
                string exeDir = Path.GetDirectoryName(exePath)?.ToLowerInvariant() ?? "";

                // The game client can't host a dedicated server. Run headless (-batchmode -nographics) it is
                // still a client, which the batch-mode, argument and graphics checks below would call a server.
                if (exeName == ClientExeName)
                {
                    Summarize(Application.isBatchMode ? "game client, headless" : "game client");
                    return;
                }

                // Check if executable name contains "server" (handles server1, server2, myserver, etc.)
                if (exeName.Contains("server"))
                {
                    IsDedicatedServerDetected = true;
                    Summarize("exe name");
                    return;
                }

                // Check if the executable is in a folder containing "server"
                // (common pattern: "Valheim Server 1", "dedicated_server", etc.)
                if (exeDir.Contains("server") || exeDir.Contains("dedicated"))
                {
                    IsDedicatedServerDetected = true;
                    Summarize("exe directory");
                    return;
                }

                // Method 2: Fast check - dedicated servers commonly run in batch mode
                if (Application.isBatchMode)
                {
                    IsDedicatedServerDetected = true;
                    Summarize("Application.isBatchMode");
                    return;
                }

                // Method 3: Check command line arguments for -batchmode or dedicated server indicators
                string[] args = Environment.GetCommandLineArgs();
                foreach (string arg in args)
                {
                    string lowerArg = arg.ToLowerInvariant();
                    if (lowerArg == "-batchmode" || lowerArg == "-nographics" || lowerArg.Contains("dedicated"))
                    {
                        IsDedicatedServerDetected = true;
                        Summarize($"command line arg '{arg}'");
                        return;
                    }
                }

                // Method 4: Check for headless/no-display environment (common on Linux servers)
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    IsDedicatedServerDetected = true;
                    Summarize("null graphics device");
                    return;
                }

                // Method 5: Try to call common ZNet detection methods reflectively
                var znetType = AccessTools.TypeByName("ZNet") ?? Type.GetType("ZNet, Assembly-CSharp");
                if (znetType == null)
                {
                    Summarize("no ZNet type found");
                    return;
                }

                // Try static IsDedicated()
                var isDedMethod = znetType.GetMethod("IsDedicated", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (isDedMethod != null)
                {
                    var result = isDedMethod.Invoke(null, null);
                    if (result is bool b && b)
                    {
                        IsDedicatedServerDetected = true;
                        Summarize("ZNet.IsDedicated()");
                        return;
                    }
                }

                // Try static IsServer() / IsServer property
                var isServerMethod = znetType.GetMethod("IsServer", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (isServerMethod != null)
                {
                    var result = isServerMethod.Invoke(null, null);
                    if (result is bool b && b)
                    {
                        IsDedicatedServerDetected = true;
                        Summarize("ZNet.IsServer()");
                        return;
                    }
                }

                // Try instance-based detection (ZNet.instance / m_instance)
                var instanceField = znetType.GetField("m_instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                   ?? znetType.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var instance = instanceField?.GetValue(null);
                if (instance != null)
                {
                    var prop = znetType.GetProperty("IsServer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (prop != null)
                    {
                        var val = prop.GetValue(instance);
                        if (val is bool vb && vb)
                        {
                            IsDedicatedServerDetected = true;
                            Summarize("ZNet.instance.IsServer");
                            return;
                        }
                    }

                    var instMethod = znetType.GetMethod("IsDedicated", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (instMethod != null)
                    {
                        var res = instMethod.Invoke(instance, null);
                        if (res is bool vb2 && vb2)
                        {
                            IsDedicatedServerDetected = true;
                            Summarize("ZNet.instance.IsDedicated");
                            return;
                        }
                    }
                }

                Summarize(null);
            }
            catch (Exception ex)
            {
                LogWarn($"Server detection failed: {ex.Message}");
                IsDedicatedServerDetected = false;
            }
        }

        // Direct logging that bypasses LoggerOptions filtering (for early startup)
        private static void Log(string message)
        {
            _earlyLogger?.LogInfo(message);
        }

        private static void LogWarn(string message)
        {
            _earlyLogger?.LogWarning(message);
        }
        /// <summary>Runtime check for patch bodies: true only once ZNet exists and reports a dedicated server.</summary>
        public static bool ZNetIsDedicated() => ZNet.instance != null && ZNet.instance.IsDedicated();

        /// <summary>
        /// The gate for FGN's server-side stress / test commands: only on a server with "Server test commands" on, and only for an
        /// admin. Returns false with the reason to send back otherwise.
        /// </summary>
        public static bool TestCommandAllowed(long sender, string command, out string refusal)
        {
            refusal = null;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) refusal = $"FGN {command}: server only.";
            else if (FiresGhettoNetworkMod.ConfigServerTestCommands == null || !FiresGhettoNetworkMod.ConfigServerTestCommands.Value)
                refusal = $"FGN {command} is off on this server (FGN config: Server test commands).";
            else if (!IsAdmin(sender)) refusal = $"FGN {command} denied: admin only.";
            return refusal == null;
        }

        /// <summary>
        /// Admin check for routed-RPC senders. Fails closed: a sender that is no connected peer is allowed only when it is this
        /// process itself (the server's own calls); any other unknown sender id is refused.
        /// </summary>
        public static bool IsAdmin(long sender)
        {
            if (ZNet.instance == null) return false;
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            if (peer == null) return sender == ZNet.GetUID();

            string host = peer.m_rpc?.GetSocket()?.GetHostName();
            return !string.IsNullOrEmpty(host) && ZNet.instance.IsAdmin(host);
        }


        /// <summary>
        /// Runtime check that can be called after ZNet is initialized
        /// </summary>
        public static bool IsRunningOnDedicatedServer()
        {
            // Use cached early detection result
            if (IsDedicatedServerDetected)
                return true;

            // Runtime check if ZNet is available
            if (ZNet.instance != null)
                return ZNet.instance.IsDedicated();

            return false;
        }
    }
}