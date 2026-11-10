using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.31 ([fgn], player report 11-05): after a portal another player's map icon slid across the map, because vanilla
    /// Minimap.UpdatePlayerPins moves it at 200 m/s (12 km takes a minute), and the list behind it is only sent every 2 s.
    ///   * client: a pin more than JumpMeters from where the list says the player is goes there at once;
    ///   * server: a player whose reported position is more than JumpMeters from their entry in the last list gets vanilla's
    ///     SendPlayerList now, at most once per ListSeconds. No new RPC, so unmodded clients get the fresher list too.
    /// Vanilla only hands out players who share their position, so a hidden player stays hidden.
    /// </summary>
    [HarmonyPatch]
    internal static class MapPinJump
    {
        internal static ConfigEntry<bool> ConfigEnabled;
        private const float JumpMeters = 64f, ListSeconds = 0.5f, LogSeconds = 5f;
        private static float s_nextList, s_nextPinLog, s_nextServerLog;
        private static int s_pinsSinceLog, s_sentSinceLog, s_heldSinceLog;

        internal static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("03 - Player Sync", "Map Icons Jump With Teleports", true,
                Cfg.Adv("When another player teleports, their map icon goes straight to the new place instead of sliding across the map."));
        }

        private static bool On => ConfigEnabled != null && ConfigEnabled.Value;

        [HarmonyPatch(typeof(Minimap), "UpdatePlayerPins"), HarmonyPostfix]
        private static void PinsPostfix(Minimap __instance)
        {
            if (!On) return;
            var pins = __instance.m_playerPins;
            var players = __instance.m_tempPlayerInfo;
            int count = Mathf.Min(pins.Count, players.Count);   // vanilla keeps these parallel; another mod might not
            for (int i = 0; i < count; i++)
            {
                Minimap.PinData pin = pins[i];
                ZNet.PlayerInfo player = players[i];
                if (pin.m_name != player.m_name) continue;   // a censored name never matches; vanilla places those pins directly
                float distance = Utils.DistanceXZ(pin.m_pos, player.m_position);
                if (distance <= JumpMeters) continue;
                pin.m_pos = player.m_position;
                __instance.m_pinUpdateRequired = true;
                s_pinsSinceLog++;
                float now = Time.unscaledTime;
                if (now >= s_nextPinLog)
                {
                    s_nextPinLog = now + LogSeconds;
                    LoggerOptions.LogDebug($"[MapPin] {player.m_name}'s icon was {distance:0} m from the player list (over {JumpMeters:0} m): moved at once instead of sliding at 200 m/s; {s_pinsSinceLog} icon move(s) since the last line");
                    s_pinsSinceLog = 0;
                }
            }
        }

        // Compared with the last list, not the previous report: on a dedicated server RefPosAudit can already have moved m_refPos to the
        // character, so the report itself would show no jump although nobody has been told yet.
        [HarmonyPatch(typeof(ZNet), "RPC_ServerSyncedPlayerData"), HarmonyPostfix]
        private static void ReportPostfix(ZNet __instance, ZRpc rpc)
        {
            if (!On || !__instance.IsServer()) return;
            ZNetPeer peer = __instance.GetPeer(rpc);
            if (peer == null || !peer.m_publicRefPos || peer.m_characterID.IsNone()) return;
            if (!LastListed(__instance, peer.m_characterID, out Vector3 listed)) return;   // not listed with a position yet: the regular list adds them
            float distance = Utils.DistanceXZ(listed, peer.m_refPos);
            if (distance <= JumpMeters) return;

            float now = Time.unscaledTime;
            bool send = now >= s_nextList;
            if (send) { s_nextList = now + ListSeconds; s_sentSinceLog++; __instance.SendPlayerList(); }
            else s_heldSinceLog++;
            if (now >= s_nextServerLog)
            {
                s_nextServerLog = now + LogSeconds;
                string what = send ? "list sent now" : "a list went out under " + ListSeconds + " s ago, the next one carries it";
                LoggerOptions.LogDebug($"[MapPin] {peer.m_playerName} moved {distance:0} m since the last player list: {what}; {s_sentSinceLog} sent, {s_heldSinceLog} held since the last line");
                s_sentSinceLog = s_heldSinceLog = 0;
            }
        }

        private static bool LastListed(ZNet net, ZDOID character, out Vector3 position)
        {
            foreach (ZNet.PlayerInfo info in net.m_players)
                if (info.m_characterID == character && info.m_publicPosition) { position = info.m_position; return true; }
            position = default;
            return false;
        }
    }
}
