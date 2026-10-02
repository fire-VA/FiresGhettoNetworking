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
        private const int DefaultGraceSeconds = 300;
        private const int MaxGraceSeconds = 600;

        private sealed class Joiner
        {
            public double ConnectedAt;
            public bool UsedGrace;
            public bool Settled;
        }

        public static ConfigEntry<int> ConfigGraceSeconds;

        private static readonly ConditionalWeakTable<ZRpc, Joiner> s_joiners = new ConditionalWeakTable<ZRpc, Joiner>();

        public static void InitConfig(ConfigFile config)
        {
            ConfigGraceSeconds = config.Bind("12 - Advanced", "Join Grace Seconds", DefaultGraceSeconds,
                new ConfigDescription(
                    "Vanilla drops anyone silent for 30 s, which cuts off slow machines that freeze while loading a big world.\n" +
                    "A joining player is instead kept until their first spawn, for at most this many seconds after connecting;\n" +
                    "after that vanilla's timeout applies. 0 = vanilla. Server only.",
                    new AcceptableValueRange<int>(0, MaxGraceSeconds)));
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
            if (rpc == null || ZNet.instance == null || !ZNet.instance.IsServer()) return vanilla;

            var joiner = s_joiners.GetValue(rpc, _ => new Joiner { ConnectedAt = Time.realtimeSinceStartupAsDouble });
            if (joiner.Settled) return vanilla;

            ZNetPeer peer = PeerOf(rpc);
            double connectedFor = Time.realtimeSinceStartupAsDouble - joiner.ConnectedAt;
            if (peer != null && !peer.m_characterID.IsNone())
            {
                joiner.Settled = true;
                ResendJoinOnlyLists(peer);
                if (joiner.UsedGrace)
                    LoggerOptions.LogMessage($"[JoinGrace] {NameOf(peer)} spawned {connectedFor:F0} s after connecting; vanilla's "
                        + $"{vanilla:F0} s timeout applies from now.");
                return vanilla;
            }
            int grace = ConfigGraceSeconds != null ? ConfigGraceSeconds.Value : 0;
            if (grace <= 0) return vanilla;
            if (connectedFor >= grace)
            {
                joiner.Settled = true;
                if (joiner.UsedGrace)
                    LoggerOptions.LogWarning($"[JoinGrace] {NameOf(peer)} has not spawned {grace} s after connecting; the grace is over "
                        + $"and vanilla's {vanilla:F0} s timeout applies.");
                return vanilla;
            }
            if (!joiner.UsedGrace && rpc.m_timeSinceLastPing > vanilla)
            {
                joiner.UsedGrace = true;
                LoggerOptions.LogMessage($"[JoinGrace] {NameOf(peer)} has sent nothing for {rpc.m_timeSinceLastPing:F0} s while joining "
                    + $"(connected {connectedFor:F0} s ago), likely still loading the world. Vanilla would drop them now; the server "
                    + $"waits up to {grace} s after they connected for a first spawn.");
            }
            return grace;
        }

        /// <summary>
        /// Vanilla sends the admin list once per join and the player history only when it changes, both in the frame the server
        /// answers PeerInfo. ServerSync-style config sync holds only PeerInfo, RoutedRPC and ZDOData back while it sends configs,
        /// so these two reach the client before the PeerInfo that registers their handlers, and the client drops them: it then
        /// has no admin list all session ([RpcDrops] on the rig, 2026-09-28). Sent again with vanilla's own methods once the
        /// player has spawned; they go to every player, and both are small.
        /// </summary>
        private static void ResendJoinOnlyLists(ZNetPeer peer)
        {
            try
            {
                ZNet.instance.SendAdminList();
                ZNet.instance.SendHistoricalPlayerList();
                LoggerOptions.LogInfo($"[JoinGrace] Sent the admin list and player history again now that {NameOf(peer)} has spawned.");
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
