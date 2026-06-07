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
        
        // Direct logger reference for early detection (before LoggerOptions filtering is configured)
        private static ManualLogSource _earlyLogger;

        // Call once at startup (e.g. from DedicatedServerGroup.Init)
        public static void Detect(ManualLogSource logger = null)
        {
            _earlyLogger = logger;
            IsDedicatedServerDetected = false;

            try
            {
                // Method 1: Check executable name - most reliable early detection
                // Supports: valheim_server.exe, valheim_server1.exe, server2.exe, myserver.exe, etc.
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                string exeName = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
                string exeDir = Path.GetDirectoryName(exePath)?.ToLowerInvariant() ?? "";
                
                Log($"Executable name: {exeName}");
                Log($"Executable directory: {exeDir}");
                
                // Check if executable name contains "server" (handles server1, server2, myserver, etc.)
                if (exeName.Contains("server"))
                {
                    IsDedicatedServerDetected = true;
                    Log("Detected dedicated server via executable name containing 'server'.");
                    return;
                }
                
                // Check if the executable is in a folder containing "server" 
                // (common pattern: "Valheim Server 1", "dedicated_server", etc.)
                if (exeDir.Contains("server") || exeDir.Contains("dedicated"))
                {
                    IsDedicatedServerDetected = true;
                    Log("Detected dedicated server via directory path containing 'server' or 'dedicated'.");
                    return;
                }

                // Method 2: Fast check - dedicated servers commonly run in batch mode
                Log($"Application.isBatchMode = {Application.isBatchMode}");
                
                if (Application.isBatchMode)
                {
                    IsDedicatedServerDetected = true;
                    Log("Detected dedicated server via Application.isBatchMode.");
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
                        Log($"Detected dedicated server via command line argument: {arg}");
                        return;
                    }
                }

                // Method 4: Check for headless/no-display environment (common on Linux servers)
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    IsDedicatedServerDetected = true;
                    Log("Detected dedicated server via null graphics device (headless mode).");
                    return;
                }

                // Method 5: Try to call common ZNet detection methods reflectively
                var znetType = AccessTools.TypeByName("ZNet") ?? Type.GetType("ZNet, Assembly-CSharp");
                if (znetType == null)
                {
                    Log("ZNet type not found; assuming client/listen-server.");
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
                        Log("Detected dedicated server via ZNet.IsDedicated().");
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
                        Log("Detected dedicated server via ZNet.IsServer().");
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
                            Log("Detected dedicated server via ZNet.instance.IsServer.");
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
                            Log("Detected dedicated server via ZNet.instance.IsDedicated.");
                            return;
                        }
                    }
                }

                Log("No dedicated-server indicator found; assuming client/listen-server.");
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