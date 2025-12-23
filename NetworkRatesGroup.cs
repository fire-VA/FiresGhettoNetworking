using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    [HarmonyPatch]
    public static class NetworkingRatesGroup
    {
        private const int VANILLA_QUEUE_SIZE = 10240;
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
            int min = GetSendRateValue(FiresGhettoNetworkMod.ConfigSendRateMin.Value);
            int max = GetSendRateValue(FiresGhettoNetworkMod.ConfigSendRateMax.Value);
            SetSteamConfig("k_ESteamNetworkingConfig_SendRateMin", min);
            SetSteamConfig("k_ESteamNetworkingConfig_SendRateMax", max);
            LoggerOptions.LogMessage($"Steam send rates applied: Min {min / 1024} KB/s, Max {max / 1024} KB/s");
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
            switch (FiresGhettoNetworkMod.ConfigUpdateRate.Value)
            {
                case UpdateRateOptions._75:
                    dt *= 0.75f;
                    break;
                case UpdateRateOptions._50:
                    dt *= 0.5f;
                    break;
            }
        }

        // ====================== ENSURE RATES APPLY ON SERVER START ======================
        [HarmonyPatch(typeof(ZNet), "Start")]
        [HarmonyPostfix]
        static void EnsureRatesOnStart()
        {
            // Apply on both client and server (especially important for dedicated servers)
            ApplySendRates();
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
        }

        // ====================== QUEUE SIZE PATCH (fixed sign) ======================
        [HarmonyPatch(typeof(ZSteamSocket), nameof(ZSteamSocket.GetSendQueueSize))]
        [HarmonyPostfix]
        static void AdjustSteamQueueSize(ref int __result)
        {
            int configBytes = FiresGhettoNetworkMod.ConfigQueueSize.Value switch
            {
                QueueSizeOptions._80KB => 80 * 1024,
                QueueSizeOptions._64KB => 64 * 1024,
                QueueSizeOptions._48KB => 48 * 1024,
                QueueSizeOptions._32KB => 32 * 1024,
                _ => VANILLA_QUEUE_SIZE
            };

            if (configBytes == VANILLA_QUEUE_SIZE) return;

            // Negative adjustment to allow the actual queue to grow larger before forcing a send
            int adjustment = VANILLA_QUEUE_SIZE - configBytes;
            __result += adjustment;

            // Clamp to 0 to avoid negative reported sizes (vanilla check is > 10240)
            if (__result < 0) __result = 0;
        }
    }
}