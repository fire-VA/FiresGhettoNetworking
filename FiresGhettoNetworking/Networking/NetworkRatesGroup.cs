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

            if (FiresGhettoNetworkMod.ConfigHyperBoost != null)
                FiresGhettoNetworkMod.ConfigHyperBoost.SettingChanged += (_, __) =>
                {
                    LoggerOptions.LogMessage(EffectiveConfig.HyperBoost()
                        ? "HYPERBOOST ENABLED — lifting send/recv rates + buffers to max and pushing live to open connections."
                        : "HYPERBOOST disabled — reverting to Auto-Tune / configured rates.");
                    ApplyEffectiveRatesLiveToAllPeers();
                };

            ApplyUpdateRate();
            ApplySendRates(); // Apply immediately on load
        }

        private static void ApplyUpdateRate()
        {
            LoggerOptions.LogInfo($"Update rate set to {FiresGhettoNetworkMod.ConfigUpdateRate.Value}");
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

            LoggerOptions.LogInfo($"Steam send rates applied: Min {min / 1024} KB/s, Max {max / 1024} KB/s");
        }

        // Temporarily lift global send-rate + send-buffer above the configured tier for fgn_socketramp.
        // Global scope — affects every connection for the duration. Always pair with RestoreSendRates().
        public static void OverrideForStressTest(int sendRateBytesPerSec, int sendBufferBytes)
        {
            AdaptiveSendRate.Suspend = true;
            SetSteamConfig("k_ESteamNetworkingConfig_SendRateMax", sendRateBytesPerSec);
            SetSteamConfig("k_ESteamNetworkingConfig_SendBufferSize", sendBufferBytes);
            LoggerOptions.LogMessage($"Stress test: send-rate Max -> {sendRateBytesPerSec / 1024} KB/s, "
                + $"send buffer -> {sendBufferBytes / 1024} KB (TEMPORARY — restored when the test ends).");
        }

        public static void RestoreSendRates()
        {
            AdaptiveSendRate.Suspend = false;
            ApplySendRates();
            ApplySendBufferSize();
            LoggerOptions.LogMessage("Stress test: send rate + buffer restored to configured values.");
        }

        // ---- PER-CONNECTION (live) config ------------------------------------------------------------
        // Steam reads the GLOBAL send-rate config only at connect time, so changing it mid-session does
        // nothing to an already-open connection (confirmed: a 50 MB/s global override left the live pipe
        // pinned at the connect-time 1 MB/s). These set the value at CONNECTION scope on a specific
        // connection handle, which DOES take effect live — what fgn_socketramp needs to actually raise
        // the test client's open pipe.

        // Reflect ZSteamSocket.m_con (HSteamNetConnection) -> its uint handle. 0 = not a Steam socket
        // (e.g. a PlayFab/crossplay connection, which has no Steam connection handle to target).
        // ServerSync (bundled into ~every mod) wraps each peer's ISocket in a private "BufferingSocket"
        // during the config-sync handshake and exposes the wrapped socket as its `Original` field. With
        // many mods each bundling ServerSync the wrappers NEST (BufferingSocket -> BufferingSocket -> ...)
        // and aren't always unwound, so peer.m_socket can stay wrapped for the whole session. Follow
        // `Original` by name down to the real ZSteamSocket / ZPlayFabSocket.
        // NOTE: BufferingSocket : ZPlayFabSocket, so NEVER test it with `is ZPlayFabSocket` — always by
        // exact GetType().Name, or a wrapped Steam connection mis-reads as PlayFab.
        private const int MaxSocketUnwrapDepth = 16;

        public static ISocket UnwrapSocket(ISocket sock)
        {
            int guard = 0;
            while (sock != null && guard++ < MaxSocketUnwrapDepth && sock.GetType().Name == "BufferingSocket")
            {
                var orig = sock.GetType().GetField("Original", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (orig == null) break;
                var next = orig.GetValue(sock) as ISocket;
                if (next == null || ReferenceEquals(next, sock)) break;
                sock = next;
            }
            return sock;
        }

        // "Wrapper -> Inner" for logging, e.g. "BufferingSocket -> ZSteamSocket".
        public static string UnwrappedSocketName(ZNetPeer peer)
        {
            var s = peer != null ? peer.m_socket : null;
            if (s == null) return "null";
            var inner = UnwrapSocket(s);
            return ReferenceEquals(inner, s) ? s.GetType().Name : (s.GetType().Name + " -> " + inner.GetType().Name);
        }

        // True iff the peer's REAL (unwrapped) transport is a Steam socket.
        public static bool IsSteamSocket(ZNetPeer peer)
        {
            var s = peer != null ? peer.m_socket : null;
            return s != null && UnwrapSocket(s).GetType().Name == "ZSteamSocket";
        }

        public static uint GetConnectionHandle(ZNetPeer peer)
        {
            try
            {
                var s = peer != null ? peer.m_socket : null;
                if (s == null) return 0u;
                var sock = UnwrapSocket(s);
                if (sock.GetType().Name != "ZSteamSocket") return 0u;
                var conField = sock.GetType().GetField("m_con", BindingFlags.NonPublic | BindingFlags.Instance);
                if (conField == null) return 0u;
                object con = conField.GetValue(sock);   // HSteamNetConnection (a struct wrapping one uint)
                if (con == null) return 0u;
                // Read the handle by FIELD TYPE, not name — the wrapped-uint field name can differ between builds.
                foreach (var f in con.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (f.FieldType == typeof(uint)) return (uint)f.GetValue(con);
                return 0u;
            }
            catch { return 0u; }
        }

        // Same reflective SetConfigValue as SetSteamConfig, but CONNECTION scope with the handle as the
        // scope object. Returns whether Steam accepted it, so callers can log proof the set landed.
        public static bool SetConnectionConfig(string enumMemberName, int value, uint connHandle)
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } });
                var enumType = allTypes.FirstOrDefault(t => t.FullName == "Steamworks.ESteamNetworkingConfigValue");
                var scopeType = allTypes.FirstOrDefault(t => t.FullName == "Steamworks.ESteamNetworkingConfigScope");
                var dataType = allTypes.FirstOrDefault(t => t.FullName == "Steamworks.ESteamNetworkingConfigDataType");
                if (enumType == null || scopeType == null || dataType == null) return false;
                // Recv-buffer-family members (RecvBufferSize / RecvMaxMessageSize / …) only exist when
                // FiresSteamworksPatcher is installed. Without it, skip silently so the setting simply caps at
                // Steam's default instead of throwing + warning per connection. Send-side members always exist.
                if (Array.IndexOf(Enum.GetNames(enumType), enumMemberName) < 0) return false;

                var enumVal = Enum.Parse(enumType, enumMemberName);
                var scopeVal = Enum.Parse(scopeType, "k_ESteamNetworkingConfig_Connection");
                var dataVal = Enum.Parse(dataType, "k_ESteamNetworkingConfig_Int32");

                ptr = Marshal.AllocHGlobal(4);
                Marshal.WriteInt32(ptr, value);

                var utilsType = ZNet.instance && ZNet.instance.IsDedicated()
                    ? allTypes.FirstOrDefault(t => t.FullName == "Steamworks.SteamGameServerNetworkingUtils")
                    : allTypes.FirstOrDefault(t => t.FullName == "Steamworks.SteamNetworkingUtils");
                if (utilsType == null) return false;

                var setMethod = utilsType.GetMethod("SetConfigValue", BindingFlags.Public | BindingFlags.Static);
                if (setMethod == null) return false;

                object res = setMethod.Invoke(null, new object[] { enumVal, scopeVal, new IntPtr((long)connHandle), dataVal, ptr });
                return !(res is bool b) || b;
            }
            catch (Exception e)
            {
                LoggerOptions.LogWarning($"SetConnectionConfig {enumMemberName} failed: {e.Message}");
                return false;
            }
            finally
            {
                if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
            }
        }

        public static void OverrideConnectionForStressTest(ZNetPeer peer, int rateMinBytes, int rateMaxBytes, int bufferBytes)
        {
            AdaptiveSendRate.Suspend = true;
            uint conn = GetConnectionHandle(peer);
            if (conn == 0u)
            {
                string sockType = peer?.m_socket?.GetType().Name ?? "null";
                LoggerOptions.LogWarning($"Stress test: no Steam connection handle to lift (socket={sockType}). "
                    + (IsSteamSocket(peer) ? "It IS a Steam socket but the handle read returned 0 — reflection issue." : "Not a Steam socket (PlayFab/crossplay) — rate is PlayFab-governed."));
                return;
            }
            bool rMax = SetConnectionConfig("k_ESteamNetworkingConfig_SendRateMax", rateMaxBytes, conn);
            bool rMin = SetConnectionConfig("k_ESteamNetworkingConfig_SendRateMin", rateMinBytes, conn);
            bool rBuf = SetConnectionConfig("k_ESteamNetworkingConfig_SendBufferSize", bufferBytes, conn);
            LoggerOptions.LogMessage($"Stress test: live per-connection {conn} -> SendRateMax {rateMaxBytes / 1024} KB/s (ok={rMax}), "
                + $"SendRateMin {rateMinBytes / 1024} KB/s (ok={rMin}), SendBuffer {bufferBytes / 1024 / 1024} MB (ok={rBuf}).");
        }

        // Lift the per-connection recv side. Needs FiresSteamworksPatcher (injects the missing enums)
        // on the side calling this — otherwise the SetConnectionConfig calls silently no-op.
        public static void OverrideConnectionRecvForStressTest(ZNetPeer peer, int recvBufferBytes, int recvMaxMessageBytes)
        {
            uint conn = GetConnectionHandle(peer);
            if (conn == 0u) return;
            bool rRb = SetConnectionConfig("k_ESteamNetworkingConfig_RecvBufferSize", recvBufferBytes, conn);
            bool rRm = SetConnectionConfig("k_ESteamNetworkingConfig_RecvMaxMessageSize", recvMaxMessageBytes, conn);
            LoggerOptions.LogMessage($"Stress test: LIVE per-connection {conn} -> RecvBufferSize {recvBufferBytes / 1024 / 1024} MB (set ok={rRb}), "
                + $"RecvMaxMessageSize {recvMaxMessageBytes / 1024} KB (ok={rRm}). "
                + (rRb && rRm ? "" : "Either failed = FiresSteamworksPatcher not installed on this side."));
        }

        // Push the CURRENT effective rates (tier/manual, or HYPERBOOST when on) onto one live connection.
        // Steam reads the GLOBAL config only at connect time, so a mid-session change must be set at
        // connection scope to land on an already-open pipe — this is the method that makes that happen.
        public static void ApplyEffectiveToConnection(ZNetPeer peer)
        {
            uint conn = GetConnectionHandle(peer);
            if (conn == 0u) return;
            SetConnectionConfig("k_ESteamNetworkingConfig_SendRateMax", EffectiveConfig.SteamSendRateMax(), conn);
            SetConnectionConfig("k_ESteamNetworkingConfig_SendRateMin", EffectiveConfig.SteamSendRateMin(), conn);
            SetConnectionConfig("k_ESteamNetworkingConfig_SendBufferSize", EffectiveConfig.SteamSendBufferBytes(), conn);
            SetConnectionConfig("k_ESteamNetworkingConfig_RecvBufferSize", EffectiveConfig.SteamRecvBufferBytes(), conn);
            SetConnectionConfig("k_ESteamNetworkingConfig_RecvMaxMessageSize", EffectiveConfig.SteamRecvMaxMessageBytes(), conn);
        }

        // Pin a connection's send rate: SendRateMin == SendRateMax == rateBytes, so Steam has no adaptive
        // window to drift inside (its sticky-down adapter is what otherwise leaves peers parked near Min).
        // Order the two writes so Min never momentarily exceeds Max: raising -> Max first; lowering -> Min first.
        public static bool SetConnectionRatePinned(uint conn, int rateBytes, bool raising)
        {
            if (conn == 0u) return false;
            bool a, b;
            if (raising)
            {
                a = SetConnectionConfig("k_ESteamNetworkingConfig_SendRateMax", rateBytes, conn);
                b = SetConnectionConfig("k_ESteamNetworkingConfig_SendRateMin", rateBytes, conn);
            }
            else
            {
                a = SetConnectionConfig("k_ESteamNetworkingConfig_SendRateMin", rateBytes, conn);
                b = SetConnectionConfig("k_ESteamNetworkingConfig_SendRateMax", rateBytes, conn);
            }
            return a && b;
        }

        public static void RestoreConnection(ZNetPeer peer)
        {
            AdaptiveSendRate.Suspend = false;
            ApplyEffectiveToConnection(peer);
            LoggerOptions.LogMessage("Stress test: per-connection rates restored to configured values.");
        }

        // Re-assert the effective rates everywhere at once: GLOBAL (so connections opened afterwards inherit
        // them) PLUS a live per-connection push to every open peer (so the change lands on the current pipe
        // with no reconnect). This is how the HYPERBOOST toggle takes hold "right then and there".
        public static void ApplyEffectiveRatesLiveToAllPeers()
        {
            if (ZNet.instance == null) return;
            ApplySendRates();
            ApplySendBufferSize();
            ApplyRecvBufferSize();
            ApplyRecvMaxMessageSize();

            int n = 0;
            try
            {
                var peers = ZNet.instance.GetPeers();
                if (peers != null)
                    foreach (var p in peers)
                        if (p != null) { ApplyEffectiveToConnection(p); n++; }
            }
            catch (Exception e) { LoggerOptions.LogWarning($"Live per-connection rate apply failed: {e.Message}"); }

            LoggerOptions.LogMessage($"Live rates applied to {n} open connection(s): "
                + $"SendRateMax {EffectiveConfig.SteamSendRateMax() / 1024 / 1024} MB/s, "
                + $"SendBuffer {EffectiveConfig.SteamSendBufferBytes() / 1024 / 1024} MB, "
                + $"RecvBuffer {EffectiveConfig.SteamRecvBufferBytes() / 1024 / 1024} MB"
                + (EffectiveConfig.HyperBoost() ? "   [HYPERBOOST ON]" : ""));
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
            LoggerOptions.LogInfo($"Steam send buffer applied: {bytes / 1024} KB");
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
            LoggerOptions.LogInfo($"Steam recv buffer applied: {bytes / 1024} KB");
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
            LoggerOptions.LogInfo($"Steam recv-max-message applied: {bytes / 1024} KB");
        }

        // True when the running Steamworks build exposes the per-connection send-buffer
        // config member — i.e. FiresSteamworksPatcher is installed and the buffer can be
        // raised above Steam's 512 KB default. Order-independent (probes the enum directly)
        // so it's safe to call from any ZNet.Start postfix regardless of patch order.
        // Used by BulkTransferGatePatches to size its gate budget against the real ceiling.
        public static bool IsSendBufferRaiseApplied()
            => HasSteamConfigMember("k_ESteamNetworkingConfig_SendBufferSize");

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

            // ONE always-visible rates summary per world load; the per-knob
            // "applied" lines above are LogInfo detail.
            LoggerOptions.LogMessage(
                $"Network rates applied: update rate {VanillaFloor.Percent(EffectiveConfig.UpdateRate())}%, "
                + $"send {EffectiveConfig.SteamSendRateMin() / 1024}-{EffectiveConfig.SteamSendRateMax() / 1024} KB/s, "
                + $"send buffer {EffectiveConfig.SteamSendBufferBytes() / 1024} KB");
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
                // Skip silently for members only present with FiresSteamworksPatcher (recv-buffer family) —
                // the value caps at Steam's default rather than throwing.
                if (Array.IndexOf(Enum.GetNames(enumType), enumMemberName) < 0) return;

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