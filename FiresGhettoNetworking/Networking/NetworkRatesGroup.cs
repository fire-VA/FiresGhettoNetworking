using BepInEx.Configuration;
using FiresGhettoNetworkMod.AutoTune;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    [HarmonyPatch]
    public static class NetworkingRatesGroup
    {
        public static void Init(ConfigFile config)
        {
            FiresGhettoNetworkMod.ConfigUpdateRate.SettingChanged += (_, __) => ApplyUpdateRate();
            FiresGhettoNetworkMod.ConfigSendRateMin.SettingChanged += (_, __) => ApplySendRates();
            FiresGhettoNetworkMod.ConfigSendRateMax.SettingChanged += (_, __) => ApplySendRates();
            FiresGhettoNetworkMod.ConfigQueueSize.SettingChanged += (_, __) => LoggerOptions.LogInfo("Queue size changed - restart recommended.");

            ApplyUpdateRate();
            ApplySendRates(); // Apply immediately on load
        }

        private static void ApplyUpdateRate()
        {
            LoggerOptions.LogMessage($"Update rate set to {FiresGhettoNetworkMod.ConfigUpdateRate.Value}");
        }

        public static void ApplySendRates()
        {
            if (ZNet.instance == null) return;

            // Routed through EffectiveConfig so Auto-Tune can shadow these without
            // overwriting the user's bound config values. Manual values still win when
            // Auto-Tune is off OR no probe result has landed yet.
            int min = EffectiveConfig.SteamSendRateMin();
            int max = EffectiveConfig.SteamSendRateMax();

            SetSteamConfig("k_ESteamNetworkingConfig_SendRateMin", min);
            SetSteamConfig("k_ESteamNetworkingConfig_SendRateMax", max);

            LoggerOptions.LogMessage($"Steam send rates applied: Min {min / 1024} KB/s, Max {max / 1024} KB/s");
        }

        // RecvBufferSize is only present in newer Steamworks SDKs. Valheim's bundled
        // com.rlabrecque.steamworks.net.dll predates it and only exposes SendBufferSize.
        // We probe the enum once and skip silently after — no warning spam.
        //
        // FiresSteamworksPatcher (a preloader patcher) injects the missing enum
        // members at boot time when installed on the server side. With the patcher
        // present, both _recvBuffer and _recvMaxMessage probes succeed and the
        // Apply* paths become live. Without it, all three remain silent no-ops.
        private static bool _recvBufferProbed;
        private static bool _recvBufferSupported;
        private static bool _recvMaxMessageProbed;
        private static bool _recvMaxMessageSupported;
        private static bool _sendBufferProbed;
        private static bool _sendBufferSupported;

        /// <summary>
        /// Apply Steam per-connection send buffer size. This is the buffer Steam fills
        /// before SendMessageToConnection starts returning k_EResultLimitExceeded —
        /// directly relevant to the "Failed to send data k_EResultLimitExceeded" spam
        /// observed during initial-sync floods. Bigger buffer = more headroom for
        /// the server to stage outbound bytes while the client drains them.
        /// SendBufferSize IS exposed in Valheim's bundled Steamworks build.
        /// </summary>
        public static void ApplySendBufferSize()
        {
            if (ZNet.instance == null) return;

            if (!_sendBufferProbed)
            {
                _sendBufferProbed = true;
                _sendBufferSupported = HasSteamConfigMember("k_ESteamNetworkingConfig_SendBufferSize");
                if (!_sendBufferSupported)
                {
                    LoggerOptions.LogInfo("Steam ESteamNetworkingConfigValue does not expose SendBufferSize in this game build; AutoTune buffer scaling has no effect.");
                }
            }
            if (!_sendBufferSupported) return;

            int bytes = EffectiveConfig.SteamSendBufferBytes();
            SetSteamConfig("k_ESteamNetworkingConfig_SendBufferSize", bytes);
            LoggerOptions.LogMessage($"Steam send buffer applied: {bytes / 1024} KB");
        }

        /// <summary>
        /// Apply the Steam recv-buffer size if this Steamworks build exposes it.
        /// Skips silently (after one info-level note) when the enum member is absent
        /// — the case for current Valheim builds.
        /// </summary>
        public static void ApplyRecvBufferSize()
        {
            if (ZNet.instance == null) return;

            if (!_recvBufferProbed)
            {
                _recvBufferProbed = true;
                _recvBufferSupported = HasSteamConfigMember("k_ESteamNetworkingConfig_RecvBufferSize");
                if (!_recvBufferSupported)
                {
                    LoggerOptions.LogInfo("Steam ESteamNetworkingConfigValue does not expose RecvBufferSize in this game build; 'ZPackage Receive Buffer Bytes' setting has no effect (Valheim ships an older Steamworks SDK).");
                }
            }
            if (!_recvBufferSupported) return;

            int bytes = EffectiveConfig.SteamRecvBufferBytes();
            SetSteamConfig("k_ESteamNetworkingConfig_RecvBufferSize", bytes);
            LoggerOptions.LogMessage($"Steam recv buffer applied: {bytes / 1024} KB");
        }

        /// <summary>
        /// Apply Steam per-connection max-message ceiling on the receive side.
        /// Without this, raising the recv buffer above 512 KB still hits "Reliable
        /// message size too large" rejections at the 512 KB Steam default — the
        /// buffer holds the bytes but the per-message gate blocks delivery. This
        /// method pairs with ApplyRecvBufferSize: both should rise together.
        ///
        /// Enum k_ESteamNetworkingConfig_RecvMaxMessageSize is missing from
        /// Valheim's bundled Steamworks.NET wrapper; FiresSteamworksPatcher
        /// injects it at preloader time on the server. On a host without that
        /// patcher, the probe fails and this method silently no-ops — same
        /// behavior pattern as ApplyRecvBufferSize.
        /// </summary>
        public static void ApplyRecvMaxMessageSize()
        {
            if (ZNet.instance == null) return;

            if (!_recvMaxMessageProbed)
            {
                _recvMaxMessageProbed = true;
                _recvMaxMessageSupported = HasSteamConfigMember("k_ESteamNetworkingConfig_RecvMaxMessageSize");
                if (!_recvMaxMessageSupported)
                {
                    LoggerOptions.LogInfo("Steam ESteamNetworkingConfigValue does not expose RecvMaxMessageSize in this game build; per-message cap stays at Steam's 512 KB default (install FiresSteamworksPatcher on the server to enable).");
                }
            }
            if (!_recvMaxMessageSupported) return;

            int bytes = EffectiveConfig.SteamRecvMaxMessageBytes();
            SetSteamConfig("k_ESteamNetworkingConfig_RecvMaxMessageSize", bytes);
            LoggerOptions.LogMessage($"Steam recv-max-message applied: {bytes / 1024} KB");
        }

        private static bool HasSteamConfigMember(string memberName)
        {
            try
            {
                var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } });
                var enumType = allTypes.FirstOrDefault(t => t.FullName == "Steamworks.ESteamNetworkingConfigValue");
                if (enumType == null) return false;
                foreach (var name in Enum.GetNames(enumType))
                {
                    if (name == memberName) return true;
                }
                return false;
            }
            catch { return false; }
        }

        private static int GetSendRateValue(object option)
        {
            string optionStr = option.ToString();
            return optionStr switch
            {
                "_1024KB" => 1024 * 1024,
                "_768KB" => 768 * 1024,
                "_512KB" => 512 * 1024,
                "_256KB" => 256 * 1024,
                _ => 150 * 1024
            };
        }

        // ====================== UPDATE RATE PATCH ======================
        [HarmonyPatch(typeof(ZDOMan), "SendZDOToPeers2")]
        [HarmonyPrefix]
        static void AdjustUpdateInterval(ref float dt)
        {
            switch (EffectiveConfig.UpdateRate())
            {
                case UpdateRateOptions._150:
                    dt *= 1.5f;  // 30Hz — fires 50% more often
                    break;
                case UpdateRateOptions._75:
                    dt *= 0.75f;
                    break;
                case UpdateRateOptions._50:
                    dt *= 0.5f;
                    break;
                // _100 = no scaling (vanilla 20Hz)
            }
        }

        // ====================== ENSURE RATES APPLY ON SERVER START ======================
        [HarmonyPatch(typeof(ZNet), "Start")]
        [HarmonyPostfix]
        static void EnsureRatesOnStart()
        {
            ApplySendRates();
            ApplySendBufferSize();
            ApplyRecvBufferSize();
            ApplyRecvMaxMessageSize();
        }

        // ====================== SEND RATE PATCHES (Steamworks) ======================
        private static void SetSteamConfig(string enumMemberName, int value)
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } });

                var enumType = allTypes.FirstOrDefault(t => t.FullName == "Steamworks.ESteamNetworkingConfigValue");
                var scopeType = allTypes.FirstOrDefault(t => t.FullName == "Steamworks.ESteamNetworkingConfigScope");
                var dataType = allTypes.FirstOrDefault(t => t.FullName == "Steamworks.ESteamNetworkingConfigDataType");

                if (enumType == null || scopeType == null || dataType == null)
                {
                    LoggerOptions.LogWarning("Steamworks.NET types not found - send rate config skipped.");
                    return;
                }

                var enumVal = Enum.Parse(enumType, enumMemberName);
                var scopeVal = Enum.Parse(scopeType, "k_ESteamNetworkingConfig_Global");
                var dataVal = Enum.Parse(dataType, "k_ESteamNetworkingConfig_Int32");

                ptr = Marshal.AllocHGlobal(4);
                Marshal.WriteInt32(ptr, value);

                var utilsType = ZNet.instance && ZNet.instance.IsDedicated()
                    ? allTypes.FirstOrDefault(t => t.FullName == "Steamworks.SteamGameServerNetworkingUtils")
                    : allTypes.FirstOrDefault(t => t.FullName == "Steamworks.SteamNetworkingUtils");

                if (utilsType == null)
                {
                    LoggerOptions.LogWarning("Steamworks utils type not found - send rate config skipped.");
                    return;
                }

                var setMethod = utilsType.GetMethod("SetConfigValue", BindingFlags.Public | BindingFlags.Static);
                if (setMethod == null)
                {
                    LoggerOptions.LogWarning("SetConfigValue method not found - send rate config skipped.");
                    return;
                }

                setMethod.Invoke(null, new object[] { enumVal, scopeVal, IntPtr.Zero, dataVal, ptr });
            }
            catch (Exception e)
            {
                LoggerOptions.LogWarning($"Failed to set Steam config {enumMemberName}: {e.Message}");
            }
            finally
            {
                if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
            }
        }

        [HarmonyPatch(typeof(ZSteamSocket), "RegisterGlobalCallbacks")]
        [HarmonyPostfix]
        static void ApplySendRatesOnConnect()
        {
            ApplySendRates();
            ApplySendBufferSize();
            ApplyRecvBufferSize();
            ApplyRecvMaxMessageSize();
        }

        // ====================== QUEUE SIZE PATCH - WORKING ON CURRENT VALHEIM ======================
        [HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> SendZDOs_QueueLimitTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int patchedCount = 0;

            int newLimit = GetConfiguredQueueLimit();

            for (int i = 0; i < code.Count; i++)
            {
                // Find large constants (>=10240) that are likely queue limits
                if ((code[i].opcode == OpCodes.Ldc_I4 || code[i].opcode == OpCodes.Ldc_I4_S) &&
                    code[i].operand is int constant &&
                    constant >= 10240)
                {
                    bool isQueueLimit = false;

                    // Check nearby for GetSendQueueSize call (using string literal to avoid any compile issues)
                    for (int j = Math.Max(0, i - 15); j < Math.Min(code.Count, i + 15); j++)
                    {
                        if (code[j].opcode == OpCodes.Callvirt &&
                            code[j].operand is MethodInfo mi &&
                            mi.Name == "GetSendQueueSize")
                        {
                            isQueueLimit = true;
                            break;
                        }
                    }

                    if (isQueueLimit)
                    {
                        LoggerOptions.LogInfo($"Overriding ZDOMan.SendZDOs queue limit #{patchedCount + 1}: original {constant} → {newLimit} bytes");
                        code[i].opcode = OpCodes.Ldc_I4;
                        code[i].operand = newLimit;
                        patchedCount++;
                    }
                }
            }

            if (patchedCount == 0)
            {
                LoggerOptions.LogWarning("No queue limit constants found in ZDOMan.SendZDOs — queue size config not applied (game update may have changed IL).");
            }
            else
            {
                LoggerOptions.LogInfo($"Successfully patched {patchedCount} queue limit constant(s).");
            }

            return code.AsEnumerable();
        }

        private static int GetConfiguredQueueLimit()
        {
            // NOTE: This runs ONCE at patch time (transpiler), not at runtime — so
            // tier changes mid-session don't take effect on the queue limit until the
            // patch is reloaded. That's a known limitation; the trade-off is keeping
            // the transpiler simple. Steam send rates and recv buffer DO update live.
            return EffectiveConfig.QueueSize() switch
            {
                QueueSizeOptions._80KB => 80 * 1024,
                QueueSizeOptions._64KB => 64 * 1024,
                QueueSizeOptions._48KB => 48 * 1024,
                QueueSizeOptions._32KB => 32 * 1024,
                _ => 10240 // vanilla fallback
            };
        }
    }
}