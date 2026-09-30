using System;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// PvP lag-fair dodge (Fire, 2026-09-28; Tools\REMOTE_MOTION.md). A player-to-player hit that reaches the victim just after
    /// their dodge's invincibility ended counts as dodged when the dodge covered any moment in the last min(2 × ping + 100 ms,
    /// 200 ms): the time in which the attacker could still have been looking at the dodge. The victim's own game applies damage in
    /// vanilla, so it decides here, and needs FGN; the SERVER turns it on (config, default off) and tells each FGN client that
    /// asks. Only dodgeable hits from another player change; everything else, and every hit on a server without it, is vanilla.
    /// No hooks of its own: CreatureOwnership's Character.RPC_Damage prefix asks Forgive, PlayerPositionSyncPatches'
    /// Player.LateUpdate postfix calls Sample for the local player, and LinkController's ZNet.Start postfix calls RegisterRpcs.
    /// </summary>
    public static class LagFairDodge
    {
        public static ConfigEntry<bool> ConfigEnabled;

        private const string RulesRequestRpc = "FGN_RulesRequest";
        private const string RulesRpc = "FGN_Rules";
        private const int RuleLagFairDodge = 1;
        private const float MaxWindowSeconds = 0.2f;
        private const float RenderDelaySeconds = 0.1f;
        private const float PingRefreshSeconds = 1f;
        private const float DefaultPingSeconds = 0.05f;
        private const float MsPerSecond = 1000f;

        private static bool s_serverOn;
        private static bool s_asked;
        private static float s_lastInvincible = float.MinValue;
        private static float s_pingSeconds = DefaultPingSeconds;
        private static float s_pingReadAt = float.MinValue;
        private static int s_forgiven;

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("10 - Server Authority", "PvP Lag-Fair Dodge", false,
                "PvP only. A hit from another player that reaches you just after your dodge's invincibility ended still counts as\n" +
                "dodged if the dodge covered any moment in the last 2 × your ping + 100 ms (at most 200 ms), the time in which the\n" +
                "attacker may still have been looking at your dodge. Fixes \"I dodged but still got hit\" at a small cost to attackers.\n" +
                "Set on the SERVER; it applies to players who run FGN. Off by default.");
        }

        /// <summary>Per session: the rules RPCs, and nothing known about this server until it answers.</summary>
        public static void RegisterRpcs()
        {
            s_serverOn = false;
            s_asked = false;
            s_forgiven = 0;
            s_lastInvincible = float.MinValue;
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.Register(RulesRequestRpc, new Action<long>(RPC_RulesRequest));
            ZRoutedRpc.instance.Register<int>(RulesRpc, RPC_Rules);
        }

        private static void RPC_RulesRequest(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZRoutedRpc.instance == null) return;
            int rules = ConfigEnabled != null && ConfigEnabled.Value ? RuleLagFairDodge : 0;
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RulesRpc, rules);
        }

        private static void RPC_Rules(long sender, int rules)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            ZNetPeer server = ZNet.instance.GetServerPeer();
            if (server == null || sender != server.m_uid) return;
            bool on = (rules & RuleLagFairDodge) != 0;
            if (on) LoggerOptions.LogInfo("[LagFairDodge] this server has PvP Lag-Fair Dodge on: a player's hit that arrives within 2 × ping + 100 ms "
                + "(at most 200 ms) of your dodge ending counts as dodged.");
            s_serverOn = on;
        }

        /// <summary>Every frame for the local player: when it was last dodge-invincible, the ping, and (once per session) asking the
        /// server for its rules.</summary>
        public static void Sample(Player me)
        {
            if (me == null) return;
            float now = Time.time;
            if (me.IsDodgeInvincible()) s_lastInvincible = now;
            if (ZNet.instance == null || ZNet.instance.IsServer() || ZRoutedRpc.instance == null) return;
            if (!s_asked && ZNet.instance.GetServerPeer() != null)
            {
                s_asked = true;
                ZRoutedRpc.instance.InvokeRoutedRPC(RulesRequestRpc);
            }
            if (s_serverOn && now - s_pingReadAt >= PingRefreshSeconds)
            {
                s_pingReadAt = now;
                ZNet.instance.GetNetStats(out _, out _, out int ping, out _, out _);
                if (ping > 0) s_pingSeconds = ping / MsPerSecond;
            }
        }

        /// <summary>
        /// True when this hit on the local player counts as dodged here; the caller then skips vanilla's RPC_Damage. What vanilla
        /// does with a hit it counts as dodged is kept: the stagger (it comes before vanilla's dodge check), nothing else.
        /// </summary>
        public static bool Forgive(Character victim, HitData hit)
        {
            if (!s_serverOn || hit == null || !hit.m_dodgeable) return false;
            if (!(victim is Player me) || me != Player.m_localPlayer) return false;
            if (!(hit.GetAttacker() is Player attacker) || attacker == me) return false;
            if (me.IsDebugFlying() || me.IsDodgeInvincible() || me.IsDead()) return false;   // vanilla's own answer
            float window = Mathf.Min(2f * s_pingSeconds + RenderDelaySeconds, MaxWindowSeconds);
            float since = Time.time - s_lastInvincible;
            if (since < 0f || since > window) return false;
            if (!me.InCutscene() && !me.IsTeleporting() && hit.m_staggerMultiplier >= 100f) me.Stagger(hit.m_dir);
            s_forgiven++;
            LoggerOptions.LogInfo($"[LagFairDodge] {attacker.GetPlayerName()}'s hit arrived {since * MsPerSecond:F0} ms after your dodge ended "
                + $"(window {window * MsPerSecond:F0} ms): dodged ({s_forgiven} this session).");
            return true;
        }
    }
}
