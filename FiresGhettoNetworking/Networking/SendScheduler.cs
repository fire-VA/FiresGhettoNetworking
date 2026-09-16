using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using FiresGhettoNetworkMod.AutoTune;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Sends every player their world updates at the ZDO Send Rate within a per-frame time budget, where vanilla serves one
    /// player per frame so each player's rate falls as the server fills. A player whose update did not fit is served again
    /// next frame, and a player with nothing to send is checked less often until something changes.
    /// </summary>
    [HarmonyPatch]
    public static class SendScheduler
    {
        public static ConfigEntry<bool> ConfigEnabled;
        public static ConfigEntry<float> ConfigBudgetMs;

        private const float VanillaSendHz = 20f;
        private const float MinBudgetMs = 0.5f;
        private const float MaxBudgetMs = 20f;

        private sealed class Slot
        {
            public double NextDue;
            public bool Backlogged;
            public int IdleStreak;
        }

        private const int MaxIdleStreak = 3;
        private const double IdleMaxWaitSeconds = 0.2;

        private static readonly Dictionary<ZDOMan.ZDOPeer, Slot> s_slots = new Dictionary<ZDOMan.ZDOPeer, Slot>();
        private static AccessTools.FieldRef<ZDOMan, List<ZDOMan.ZDOPeer>> s_peers;
        private static AccessTools.FieldRef<ZDOMan, List<ZDO>> s_toSync;
        private static AccessTools.FieldRef<ZDOMan, int> s_zdosSent;
        private static Func<ZDOMan, ZDOMan.ZDOPeer, bool, bool> s_sendZDOs;
        private static bool s_resolved;
        private static bool s_usable;
        private static string s_yieldTo;
        private static int s_cursor;

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("04 - Networking", "Send To Every Player Each Frame", true,
                "Vanilla sends world updates to one player per frame, so every player's update rate drops as the server fills: "
                + "about 3 updates a second each with ten players on a 30 fps dedicated server. With this on, every player gets "
                + "the ZDO Send Rate, within Send Budget Per Frame. Applies on both sides.");
            ConfigBudgetMs = config.Bind("04 - Networking", "Send Budget Per Frame", 4f,
                new ConfigDescription(
                    "Milliseconds per frame the send scheduler may spend building world updates. Players left over when a frame's "
                    + "budget runs out are served first next frame, and at least one player is always served per frame, as in vanilla.",
                    new AcceptableValueRange<float>(MinBudgetMs, MaxBudgetMs)));
            ConfigEnabled.SettingChanged += (_, __) => ResetSlots();
        }

        public static bool Active() => ConfigEnabled != null && ConfigEnabled.Value && s_yieldTo == null && ResolveVanillaAccess();

        public static float TargetHz() => VanillaSendHz * VanillaFloor.Percent(EffectiveConfig.UpdateRate()) / 100f;

        [HarmonyPatch(typeof(ZNet), "Start"), HarmonyPostfix]
        static void OnZNetStart()
        {
            ResetSlots();
            s_yieldTo = ForeignScheduler();
            if (s_yieldTo != null)
                LoggerOptions.LogWarning($"[SendScheduler] '{s_yieldTo}' already replaces the ZDO send order, so FGN leaves sending to it.");
            else if (Active())
                LoggerOptions.LogMessage($"[SendScheduler] every player is sent world updates at {TargetHz():F0} per second, "
                    + $"within {Mathf.Clamp(ConfigBudgetMs.Value, MinBudgetMs, MaxBudgetMs):F1} ms per frame.");
        }

        [HarmonyPatch(typeof(ZNet), "Shutdown"), HarmonyPostfix]
        static void OnZNetShutdown() => ResetSlots();

        [HarmonyPatch(typeof(ZDOMan), "RemovePeer"), HarmonyPostfix]
        static void OnRemovePeer(ZNetPeer netPeer)
        {
            if (netPeer == null || s_slots.Count == 0) return;
            ZDOMan.ZDOPeer gone = null;
            foreach (var peer in s_slots.Keys)
            {
                if (peer.m_peer != netPeer) continue;
                gone = peer;
                break;
            }
            if (gone != null) s_slots.Remove(gone);
        }

        [HarmonyPatch(typeof(ZDOMan), "SendZDOToPeers2"), HarmonyPrefix]
        static bool SendZDOToPeers2_Prefix(ZDOMan __instance)
        {
            if (!Active()) return true;
            SendDuePlayers(__instance);
            return false;
        }

        private static void SendDuePlayers(ZDOMan zdoMan)
        {
            var peers = s_peers(zdoMan);
            int count = peers.Count;
            if (count == 0) return;

            double now = Time.realtimeSinceStartupAsDouble;
            double interval = 1.0 / TargetHz();
            long budgetTicks = (long)(Mathf.Clamp(ConfigBudgetMs.Value, MinBudgetMs, MaxBudgetMs) * Stopwatch.Frequency / 1000.0);
            long started = Stopwatch.GetTimestamp();
            if (s_cursor >= count) s_cursor = 0;

            for (int step = 0; step < count; step++)
            {
                int index = (s_cursor + step) % count;
                var peer = peers[index];
                if (peer?.m_peer?.m_socket == null) continue;

                if (!s_slots.TryGetValue(peer, out var slot))
                {
                    slot = new Slot { NextDue = now };
                    s_slots[peer] = slot;
                }
                bool due = now >= slot.NextDue;
                bool urgent = peer.m_forceSend.Count > 0 || peer.m_invalidSector.Count > 0;
                if (!due && !slot.Backlogged && !urgent) continue;
                if (!LinkController.HasRoom(peer)) continue;

                int sentBefore = s_zdosSent(zdoMan);
                bool sent = s_sendZDOs(zdoMan, peer, false);
                int written = s_zdosSent(zdoMan) - sentBefore;
                slot.Backlogged = written > 0 && written < s_toSync(zdoMan).Count;
                slot.IdleStreak = sent ? 0 : Math.Min(slot.IdleStreak + 1, MaxIdleStreak);
                if (due)
                {
                    double wait = Math.Min(interval * (1 << slot.IdleStreak), Math.Max(interval, IdleMaxWaitSeconds));
                    slot.NextDue = Math.Max(slot.NextDue + wait, now);
                }
                LinkController.CountSend(peer);

                if (Stopwatch.GetTimestamp() - started < budgetTicks) continue;
                s_cursor = (index + 1) % count;
                return;
            }
            s_cursor = (s_cursor + 1) % count;
        }

        private static void ResetSlots()
        {
            s_slots.Clear();
            s_cursor = 0;
        }

        private static bool ResolveVanillaAccess()
        {
            if (s_resolved) return s_usable;
            s_resolved = true;
            try
            {
                s_peers = AccessTools.FieldRefAccess<ZDOMan, List<ZDOMan.ZDOPeer>>("m_peers");
                s_toSync = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>>("m_tempToSync");
                s_zdosSent = AccessTools.FieldRefAccess<ZDOMan, int>("m_zdosSent");
                var send = AccessTools.Method(typeof(ZDOMan), "SendZDOs", new[] { typeof(ZDOMan.ZDOPeer), typeof(bool) });
                if (send != null) s_sendZDOs = AccessTools.MethodDelegate<Func<ZDOMan, ZDOMan.ZDOPeer, bool, bool>>(send);
                s_usable = s_sendZDOs != null;
                if (!s_usable) LoggerOptions.LogWarning("[SendScheduler] ZDOMan.SendZDOs not found; vanilla send order stays.");
            }
            catch (Exception e)
            {
                s_usable = false;
                LoggerOptions.LogWarning($"[SendScheduler] could not attach, vanilla send order stays: {e.Message}");
            }
            return s_usable;
        }

        private static string ForeignScheduler()
        {
            try
            {
                var original = AccessTools.Method(typeof(ZDOMan), "SendZDOToPeers2");
                var info = original != null ? Harmony.GetPatchInfo(original) : null;
                if (info == null) return null;
                foreach (var patch in info.Prefixes)
                    if (patch.owner != FiresGhettoNetworkMod.PluginGUID && patch.PatchMethod != null && patch.PatchMethod.ReturnType == typeof(bool))
                        return patch.owner;
                foreach (var patch in info.Transpilers)
                    if (patch.owner != FiresGhettoNetworkMod.PluginGUID)
                        return patch.owner;
            }
            catch { }
            return null;
        }
    }
}
