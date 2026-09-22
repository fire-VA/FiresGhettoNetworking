using BepInEx.Configuration;
using FiresGhettoNetworkMod.AutoTune;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Steamworks;
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

            LogAppliedIfChanged("SendRates", $"Steam send rates applied: Min {min / 1024} KB/s, Max {max / 1024} KB/s");
        }

        private static readonly Dictionary<string, string> s_lastAppliedLog = new Dictionary<string, string>();

        // The same values are re-applied on every connection and tier check, so only a change is worth a line.
        private static void LogAppliedIfChanged(string setting, string message)
        {
            if (s_lastAppliedLog.TryGetValue(setting, out string last) && last == message) return;
            s_lastAppliedLog[setting] = message;
            LoggerOptions.LogInfo(message);
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
            var socket = peer != null ? peer.m_socket : null;
            if (socket == null) return "null";
            var inner = UnwrapSocket(socket);
            return ReferenceEquals(inner, socket) ? socket.GetType().Name : (socket.GetType().Name + " -> " + inner.GetType().Name);
        }

        // True iff the peer's REAL (unwrapped) transport is a Steam socket.
        public static bool IsSteamSocket(ZNetPeer peer)
        {
            var socket = peer != null ? peer.m_socket : null;
            return socket != null && UnwrapSocket(socket).GetType().Name == "ZSteamSocket";
        }

        public static bool IsCrossplay(ZNetPeer peer)
        {
            var socket = peer != null ? peer.m_socket : null;
            return socket != null && UnwrapSocket(socket).GetType() == typeof(ZPlayFabSocket);
        }

        public static uint GetConnectionHandle(ZNetPeer peer)
        {
            try
            {
                var socket = peer != null ? peer.m_socket : null;
                if (socket == null) return 0u;
                var sock = UnwrapSocket(socket);
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

        // CONNECTION scope with the handle as the scope object. Returns whether Steam accepted it, so callers
        // can log proof the set landed. Recv-buffer-family members (RecvBufferSize / RecvMaxMessageSize) only
        // exist when FiresSteamworksPatcher is installed; without it the setting is skipped and caps at Steam's
        // default.
        public static bool SetConnectionConfig(string enumMemberName, int value, uint connHandle)
        {
            if (!TryGetSteamConfigMember(enumMemberName, out var setting)) return false;
            CapeCrashDiagnostics.Log($"Steam config {enumMemberName} (id {(int)setting}) = {value} on connection {connHandle}: calling SetConfigValue");
            bool accepted = SetConfigInt32(setting, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, new IntPtr((long)connHandle), value);
            CapeCrashDiagnostics.Log($"Steam config {enumMemberName} on connection {connHandle}: returned {accepted}");
            return accepted;
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
        public static bool PinConnectionRate(uint conn, int rateBytes, bool raising)
        {
            if (conn == 0u) return false;
            var first = raising ? ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax : ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin;
            var second = raising ? ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin : ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax;
            var scope = ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection;
            var handle = new IntPtr((long)conn);
            bool a = SetConfigInt32(first, scope, handle, rateBytes);
            bool b = SetConfigInt32(second, scope, handle, rateBytes);
            return a && b;
        }

        private static IntPtr s_int32Value;

        private static bool SetConfigInt32(ESteamNetworkingConfigValue setting, ESteamNetworkingConfigScope scope, IntPtr scopeObject, int value)
        {
            try
            {
                if (s_int32Value == IntPtr.Zero) s_int32Value = Marshal.AllocHGlobal(4);
                Marshal.WriteInt32(s_int32Value, value);
                var dataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32;
                return ZNet.instance != null && ZNet.instance.IsDedicated()
                    ? SteamGameServerNetworkingUtils.SetConfigValue(setting, scope, scopeObject, dataType, s_int32Value)
                    : SteamNetworkingUtils.SetConfigValue(setting, scope, scopeObject, dataType, s_int32Value);
            }
            catch (Exception e)
            {
                LoggerOptions.LogWarning($"Steam {scope} {setting} = {value} failed: {e.Message}");
                return false;
            }
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

            int applied = 0;
            try
            {
                var peers = ZNet.instance.GetPeers();
                if (peers != null)
                    foreach (var p in peers)
                        if (p != null) { ApplyEffectiveToConnection(p); applied++; }
            }
            catch (Exception e) { LoggerOptions.LogWarning($"Live per-connection rate apply failed: {e.Message}"); }

            LoggerOptions.LogMessage($"Live rates applied to {applied} open connection(s): "
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
            LogAppliedIfChanged("SendBuffer", $"Steam send buffer applied: {bytes / 1024} KB");
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
            LogAppliedIfChanged("RecvBuffer", $"Steam recv buffer applied: {bytes / 1024} KB");
        }

        /// <summary>
        /// Raises Steam's per-connection max message size alongside the recv buffer. Without it the buffer
        /// holds the bytes but the 512 KB per-message gate still rejects delivery. The enum is missing from
        /// Valheim's bundled Steamworks.NET wrapper and is injected by FiresSteamworksPatcher at preload, so
        /// on a host without that patcher the probe fails and this no-ops, as ApplyRecvBufferSize does.
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
            LogAppliedIfChanged("RecvMaxMessage", $"Steam recv-max-message applied: {bytes / 1024} KB");
        }

        // True when the running Steamworks build exposes the per-connection send-buffer
        // config member — i.e. FiresSteamworksPatcher is installed and the buffer can be
        // raised above Steam's 512 KB default. Order-independent (probes the enum directly)
        // so it's safe to call from any ZNet.Start postfix regardless of patch order.
        // Used by BulkTransferGatePatches to size its gate budget against the real ceiling.
        public static bool IsSendBufferRaiseApplied()
            => HasSteamConfigMember("k_ESteamNetworkingConfig_SendBufferSize");

        private static bool HasSteamConfigMember(string memberName) => TryGetSteamConfigMember(memberName, out _);

        // The enum's members are read by name once. FiresSteamworksPatcher adds members at preload, before this
        // assembly loads, so the runtime enum already carries them.
        private static Dictionary<string, ESteamNetworkingConfigValue> s_steamConfigMembers;

        private static bool TryGetSteamConfigMember(string memberName, out ESteamNetworkingConfigValue setting)
        {
            if (s_steamConfigMembers == null)
            {
                var members = new Dictionary<string, ESteamNetworkingConfigValue>(StringComparer.Ordinal);
                foreach (var name in Enum.GetNames(typeof(ESteamNetworkingConfigValue)))
                    members[name] = (ESteamNetworkingConfigValue)Enum.Parse(typeof(ESteamNetworkingConfigValue), name);
                s_steamConfigMembers = members;
            }
            return s_steamConfigMembers.TryGetValue(memberName, out setting);
        }

        // ====================== UPDATE RATE PATCH ======================
        [HarmonyPatch(typeof(ZDOMan), "SendZDOToPeers2")]
        [HarmonyPrefix]
        static void AdjustUpdateInterval(ref float dt)
        {
            if (SendScheduler.Active()) return;
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
        // Members only present with FiresSteamworksPatcher (recv-buffer family) are skipped, so the value caps at
        // Steam's default.
        private static void SetSteamConfig(string enumMemberName, int value)
        {
            if (!TryGetSteamConfigMember(enumMemberName, out var setting)) return;
            CapeCrashDiagnostics.Log($"Steam config {enumMemberName} (id {(int)setting}) = {value} (global): calling SetConfigValue");
            bool accepted = SetConfigInt32(setting, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, value);
            CapeCrashDiagnostics.Log($"Steam config {enumMemberName} (global): returned {accepted}");
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

        // Vanilla SendZDOs: skip the peer while queue > 10240, budget = 10240 - queue, skip under 2048. The queue check
        // becomes the peer's own window (recording when the window held the peer back), the budget uses the same window,
        // and the package it fills is clamped to Queue Size so a wide window never means one huge package.
        [HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> SendZDOs_WindowTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var gate = AccessTools.Method(typeof(LinkController), nameof(LinkController.SendGateWindow));
            var window = AccessTools.Method(typeof(LinkController), nameof(LinkController.WindowBytes), new[] { typeof(ZDOMan.ZDOPeer) });
            var budget = AccessTools.Method(typeof(LinkController), nameof(LinkController.PackageBudget));

            int gates = 0, windows = 0, clamps = 0;
            int queueCall = code.FindIndex(ins => (ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call)
                && ins.operand is MethodInfo mi && mi.Name == "GetSendQueueSize");
            for (int i = queueCall < 0 ? code.Count : queueCall + 1; i < code.Count && i <= queueCall + QueueSiteSearchSpan; i++)
            {
                if (!(code[i].opcode == OpCodes.Ldc_I4 && code[i].operand is int constant && constant >= VanillaQueueLimitBytes)) continue;

                if (code[i - 1].IsLdloc() && i + 1 < code.Count && IsCompareBranch(code[i + 1].opcode))
                {
                    code[i].opcode = code[i - 1].opcode;
                    code[i].operand = code[i - 1].operand;
                    code.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_1));
                    code.Insert(i + 2, new CodeInstruction(OpCodes.Call, gate));
                    gates++;
                    i += 2;
                    continue;
                }

                code[i].opcode = OpCodes.Ldarg_1;
                code[i].operand = null;
                code.Insert(i + 1, new CodeInstruction(OpCodes.Call, window));
                windows++;
                i += 1;
                if (i + 2 < code.Count && code[i + 1].IsLdloc() && code[i + 2].opcode == OpCodes.Sub)
                {
                    code.Insert(i + 3, new CodeInstruction(OpCodes.Ldarg_1));
                    code.Insert(i + 4, new CodeInstruction(OpCodes.Call, budget));
                    clamps++;
                    i += 4;
                }
            }

            s_queueLimitPatched = gates + windows > 0;
            if (!s_queueLimitPatched)
                LoggerOptions.LogWarning("ZDOMan.SendZDOs has no send-queue limit this FGN version recognises (game update or another mod); "
                    + "per-player send windows and Queue Size are not applied.");
            else if (gates != 1 || windows != 1 || clamps != 1)
                LoggerOptions.LogWarning($"ZDOMan.SendZDOs changed shape: {gates} queue check(s), {windows} budget(s), {clamps} package clamp(s) "
                    + "attached where 1 of each was expected. Per-player windows still apply where attached.");
            else if (!s_queueLimitAnnounced)
            {
                s_queueLimitAnnounced = true;
                LoggerOptions.LogInfo("ZDOMan.SendZDOs: send-queue limit follows each player's send window, packages capped at Queue Size.");
            }
            return code;
        }

        private static bool IsCompareBranch(OpCode opcode)
        {
            return opcode == OpCodes.Ble || opcode == OpCodes.Ble_S || opcode == OpCodes.Ble_Un || opcode == OpCodes.Ble_Un_S
                || opcode == OpCodes.Bgt || opcode == OpCodes.Bgt_S || opcode == OpCodes.Bgt_Un || opcode == OpCodes.Bgt_Un_S
                || opcode == OpCodes.Bge || opcode == OpCodes.Bge_S || opcode == OpCodes.Bge_Un || opcode == OpCodes.Bge_Un_S
                || opcode == OpCodes.Blt || opcode == OpCodes.Blt_S || opcode == OpCodes.Blt_Un || opcode == OpCodes.Blt_Un_S;
        }

        private const int QueueSiteSearchSpan = 24;

        private const int VanillaQueueLimitBytes = 10240;

        private static bool s_queueLimitPatched;

        // Harmony re-runs this transpiler whenever another mod patches SendZDOs, so the success line prints once.
        private static bool s_queueLimitAnnounced;

        /// <summary>
        /// The send-queue cap ZDOMan.SendZDOs checks, read on every call. Harmony re-runs this transpiler whenever another
        /// patch lands on SendZDOs, so a value baked in at patch time depended on registration order relative to the
        /// server auto-tune, and the congestion gate measured against a different cap than the one in force.
        /// </summary>
        public static int ZdoSendQueueCapBytes() => EffectiveConfig.QueueSizeBytes(VanillaQueueLimitBytes);

        /// <summary>The cap actually in force: the live Queue Size once the transpiler has attached, vanilla's otherwise.</summary>
        public static int ZdoSendQueueCapInForceBytes() => s_queueLimitPatched ? ZdoSendQueueCapBytes() : VanillaQueueLimitBytes;
    }
}