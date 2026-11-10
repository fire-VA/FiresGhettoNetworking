using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// A joining player whose game freezes while it loads a big world sends no keepalives, and vanilla drops them after 30 s.
    /// Until their first spawn, and for at most the configured grace, the server waits instead; after it, vanilla's timeout applies.
    /// </summary>
    [HarmonyPatch]
    public static class JoinGrace
    {
        private const int DefaultGraceSeconds = 900;   // seconds of silence allowed before the first spawn
        private const int MaxGraceSeconds = 3600;
        private const int OldDefaultGraceSeconds = 300;

        private sealed class Joiner
        {
            public double ConnectedAt;
            public bool UsedGrace;
            public bool Settled;
            public bool Resumed;
        }

        public static ConfigEntry<int> ConfigGraceSeconds;

        private static readonly ConditionalWeakTable<ZRpc, Joiner> s_joiners = new ConditionalWeakTable<ZRpc, Joiner>();

        public static void InitConfig(ConfigFile config)
        {
            ConfigGraceSeconds = config.Bind("12 - Advanced", "Join Grace Seconds", DefaultGraceSeconds,
                Cfg.Adv("Seconds a joining player may stay silent while loading before being timed out (0 = vanilla).",
                    new AcceptableValueRange<int>(0, MaxGraceSeconds)));
            // One-time migration: a cfg still on the old default moves to the new one; a value someone chose is kept.
            if (ConfigGraceSeconds.Value == OldDefaultGraceSeconds)
            {
                ConfigGraceSeconds.Value = DefaultGraceSeconds;
                LoggerOptions.LogInfo($"[JoinGrace] 'Join Grace Seconds' was the old default {OldDefaultGraceSeconds}; now {DefaultGraceSeconds} (a fresh Fires world can take ~8 min to build on a first join).");
            }
        }

        [HarmonyPatch(typeof(ZRpc), "UpdatePing"), HarmonyTranspiler]
        static IEnumerable<CodeInstruction> AllowSilenceWhileJoining(IEnumerable<CodeInstruction> instructions)
        {
            FieldInfo timeout = AccessTools.Field(typeof(ZRpc), nameof(ZRpc.m_timeout));
            MethodInfo allowed = AccessTools.Method(typeof(JoinGrace), nameof(SilenceAllowedSeconds));
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.LoadsField(timeout))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                    yield return new CodeInstruction(OpCodes.Call, allowed);
                    replaced++;
                    continue;
                }
                yield return instruction;
            }
            if (replaced == 0)
                LoggerOptions.LogWarning("[JoinGrace] ZRpc.UpdatePing no longer reads its timeout; joining players get vanilla's timeout.");
        }

        public static float SilenceAllowedSeconds(ZRpc rpc)
        {
            float vanilla = ZRpc.m_timeout;
            if (rpc == null || ZNet.instance == null) return vanilla;
            if (!ZNet.instance.IsServer()) return ClientSilenceAllowedSeconds(rpc, vanilla);

            var joiner = s_joiners.GetValue(rpc, _ => new Joiner { ConnectedAt = Time.realtimeSinceStartupAsDouble });
            if (joiner.Settled) return vanilla;

            ZNetPeer peer = PeerOf(rpc);
            double connectedFor = Time.realtimeSinceStartupAsDouble - joiner.ConnectedAt;
            if (peer != null && !peer.m_characterID.IsNone())
            {
                joiner.Settled = true;
                ResendJoinOnlyLists(peer);
                if (joiner.UsedGrace)
                    LoggerOptions.LogDebug($"[JoinGrace] {NameOf(peer)} spawned {connectedFor:F0} s after connecting; vanilla's "
                        + $"{vanilla:F0} s timeout applies from now.");
                return vanilla;
            }
            int grace = ConfigGraceSeconds != null ? ConfigGraceSeconds.Value : 0;
            if (grace <= 0) return vanilla;
            // A first join to a fresh world can build the world on the client's main thread for minutes, sending nothing.
            // The grace is a budget of silence for a peer that has not spawned yet: a loading peer that is quiet for up to
            // 'grace' seconds is kept, and once it talks again (its pings reset the silence) it is never cut off for being slow.
            if (rpc.m_timeSinceLastPing >= grace)
            {
                joiner.Settled = true;
                LoggerOptions.LogWarning($"[JoinGrace] {NameOf(peer)} has sent nothing for {rpc.m_timeSinceLastPing:F0} s while joining "
                    + $"(connected {connectedFor:F0} s ago); the {grace} s grace is over and vanilla's {vanilla:F0} s timeout applies.");
                return vanilla;
            }
            if (joiner.UsedGrace && rpc.m_timeSinceLastPing < vanilla && !joiner.Resumed)
            {
                joiner.Resumed = true;
                LoggerOptions.LogDebug($"[JoinGrace] {NameOf(peer)} is talking again {connectedFor:F0} s after connecting (still loading); kept.");
            }
            if (!joiner.UsedGrace && rpc.m_timeSinceLastPing > vanilla)
            {
                joiner.UsedGrace = true;
                LoggerOptions.LogInfo($"[JoinGrace] {NameOf(peer)} has sent nothing for {rpc.m_timeSinceLastPing:F0} s while joining "
                    + $"(connected {connectedFor:F0} s ago), likely still loading the world. Vanilla would drop them now; until they "
                    + $"first spawn the server allows up to {grace} s of silence (any message from them resets it).");
            }
            return grace;
        }

        // ---- the client side ----
        // While a client downloads a large world cache, the server's ping reply can sit behind the download in Steam's
        // in-order delivery, so the client sees no ping while data pours in and times out. Until the local player first
        // spawns, any package from the server counts as a sign of life, and the silence allowed is the same 'Join Grace Seconds'.

        private const float ClientResetLogAfterSeconds = 1f;
        private static bool s_clientGraceLogged;
        private static bool s_clientResetLogged;

        private static bool ClientJoining(ZRpc rpc)
        {
            return Player.m_localPlayer == null && ZNet.instance != null && !ZNet.instance.IsServer()
                && rpc == ZNet.instance.GetServerRPC();
        }

        /// <summary>Called for every package a ZRpc receives (DownloadBreakdown's HandlePackage prefix).</summary>
        internal static void OnPackageReceived(ZRpc rpc)
        {
            if (rpc == null || ConfigGraceSeconds == null || ConfigGraceSeconds.Value <= 0 || !ClientJoining(rpc)) return;
            if (rpc.m_timeSinceLastPing > ClientResetLogAfterSeconds && !s_clientResetLogged)
            {
                s_clientResetLogged = true;
                LoggerOptions.LogDebug($"[JoinGrace] Client still joining: data from the server reset the ping timer after "
                    + $"{rpc.m_timeSinceLastPing:F1} s (a ping reply queued behind the download no longer times us out).");
            }
            rpc.m_timeSinceLastPing = 0f;
        }

        private static float ClientSilenceAllowedSeconds(ZRpc rpc, float vanilla)
        {
            if (!ClientJoining(rpc))
            {
                s_clientGraceLogged = false;
                s_clientResetLogged = false;
                return vanilla;
            }
            int grace = ConfigGraceSeconds != null ? ConfigGraceSeconds.Value : 0;
            if (grace <= vanilla) return vanilla;
            if (!s_clientGraceLogged && rpc.m_timeSinceLastPing > vanilla)
            {
                s_clientGraceLogged = true;
                LoggerOptions.LogInfo($"[JoinGrace] Nothing from the server for {rpc.m_timeSinceLastPing:F0} s while joining. Vanilla "
                    + $"would disconnect now; until we spawn we allow up to {grace} s of silence.");
            }
            return grace;
        }

        /// <summary>
        /// Vanilla sends the admin list once per join and the player history only when it changes, both in the frame the server
        /// answers PeerInfo. ServerSync-style config sync holds only PeerInfo, RoutedRPC and ZDOData back while it sends configs,
        /// so these two reach the client before the PeerInfo that registers their handlers, and the client drops them: it then
        /// has no admin list all session. Sent again with vanilla's own methods once the
        /// player has spawned; they go to every player, and both are small.
        /// </summary>
        private static void ResendJoinOnlyLists(ZNetPeer peer)
        {
            try
            {
                ZNet.instance.SendAdminList();
                ZNet.instance.SendHistoricalPlayerList();
                LoggerOptions.LogDebug($"[JoinGrace] Sent the admin list and player history again now that {NameOf(peer)} has spawned.");
            }
            catch (System.Exception e)
            {
                LoggerOptions.LogWarning($"[JoinGrace] Couldn't send the admin list and player history again: {e.Message}");
            }
        }

        private static ZNetPeer PeerOf(ZRpc rpc)
        {
            foreach (var peer in ZNet.instance.m_peers)
                if (peer != null && peer.m_rpc == rpc) return peer;
            return null;
        }

        private static string NameOf(ZNetPeer peer)
        {
            if (peer == null) return "A joining player";
            if (!string.IsNullOrEmpty(peer.m_playerName)) return peer.m_playerName;
            return peer.m_socket != null ? peer.m_socket.GetHostName() : "A joining player";
        }
    }
}
