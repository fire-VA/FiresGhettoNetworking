using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    public static class ServerClientUtils
    {
        public static bool IsDedicatedServerDetected { get; private set; }

        // Call once at startup (e.g. from DedicatedServerGroup.Init)
        public static void Detect()
        {
            IsDedicatedServerDetected = false;

            try
            {
                // Fast check: dedicated servers commonly run in batch mode
                if (Application.isBatchMode)
                {
                    IsDedicatedServerDetected = true;
                    LoggerOptions.LogInfo("Detected dedicated server via Application.isBatchMode.");
                    return;
                }

                // Try to call common ZNet detection methods reflectively to remain compatible across versions
                var znetType = AccessTools.TypeByName("ZNet") ?? Type.GetType("ZNet, Assembly-CSharp");
                if (znetType == null)
                {
                    LoggerOptions.LogInfo("ZNet type not found; assuming client/listen-server.");
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
                        LoggerOptions.LogInfo("Detected dedicated server via ZNet.IsDedicated().");
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
                        LoggerOptions.LogInfo("Detected dedicated server via ZNet.IsServer().");
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
                            LoggerOptions.LogInfo("Detected dedicated server via ZNet.instance.IsServer.");
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
                            LoggerOptions.LogInfo("Detected dedicated server via ZNet.instance.IsDedicated.");
                            return;
                        }
                    }
                }

                LoggerOptions.LogInfo("No dedicated-server indicator found; assuming client/listen-server.");
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"Server detection failed: {ex.Message}");
                IsDedicatedServerDetected = false;
            }
        }
    }
}