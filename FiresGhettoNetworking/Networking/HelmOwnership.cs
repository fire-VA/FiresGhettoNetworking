using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Hands a ship to the player who takes its helm. Vanilla leaves it with whichever game owns it, so a helmsman's steering
    /// reaches that game through the server and the ship's movement comes back the same way. The owner's game hands the ship
    /// over as it grants the helm and passes on steering that was already on its way to it.
    /// </summary>
    [HarmonyPatch]
    public static class HelmOwnership
    {
        public static ConfigEntry<bool> ConfigEnabled;

        internal static bool ServerOwnsShips;

        private const double PassOnSteeringSeconds = 5.0;
        private const int PruneAboveEntries = 32;

        private struct HandOff
        {
            public long Helmsman;
            public double At;
        }

        private static readonly Dictionary<ZDOID, HandOff> s_handedOff = new Dictionary<ZDOID, HandOff>();
        private static readonly List<ZDOID> s_expired = new List<ZDOID>();
        private static readonly AccessTools.FieldRef<ShipControlls, ZNetView> s_controlsView
            = AccessTools.FieldRefAccess<ShipControlls, ZNetView>("m_nview");

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("11 - Ship Fixes", "Give The Ship To Its Helmsman", true,
                "Vanilla leaves a ship with whichever player's game owns it, so anyone else at the helm steers it through the server\n" +
                "and sees it move only once that game's updates come back. With this on, the owning game hands the ship to the player\n" +
                "it lets take the helm and passes on steering already on its way. Only the ship's current owner needs FGN.\n" +
                "Does nothing while the server owns ships.");
        }

        [HarmonyPatch(typeof(ZNet), "Shutdown"), HarmonyPostfix]
        static void OnShutdown() => s_handedOff.Clear();

        [HarmonyPatch(typeof(ShipControlls), "RPC_RequestControl"), HarmonyPostfix]
        static void OnRequestControl(ShipControlls __instance, long sender, long playerID)
        {
            if (!Active() || sender == 0L || sender == ZDOMan.GetSessionID()) return;
            var view = s_controlsView(__instance);
            if (view == null || !view.IsValid() || !view.IsOwner()) return;
            var zdo = view.GetZDO();
            if (zdo.GetLong(ZDOVars.s_user) != playerID || !__instance.m_ship.IsPlayerInBoat(playerID)) return;

            ZDOMan.instance.ForceSendZDO(zdo.m_uid);
            zdo.SetOwner(sender);
            if (s_handedOff.Count > PruneAboveEntries) PruneExpired();
            s_handedOff[zdo.m_uid] = new HandOff { Helmsman = sender, At = Time.realtimeSinceStartupAsDouble };

            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            var helmsman = Player.GetPlayer(playerID);
            LoggerOptions.LogMessage($"[Helm] Handed the {(prefab != null ? prefab.name : "ship")} to "
                + $"{(helmsman != null ? helmsman.GetPlayerName() : sender.ToString())}, who took its helm.");
        }

        [HarmonyPatch(typeof(Ship), "RPC_Forward"), HarmonyPrefix]
        static bool OnForward(ZNetView ___m_nview, long sender) => !PassOnSteering(___m_nview, sender, "Forward");

        [HarmonyPatch(typeof(Ship), "RPC_Backward"), HarmonyPrefix]
        static bool OnBackward(ZNetView ___m_nview, long sender) => !PassOnSteering(___m_nview, sender, "Backward");

        [HarmonyPatch(typeof(Ship), "RPC_Stop"), HarmonyPrefix]
        static bool OnStop(ZNetView ___m_nview, long sender) => !PassOnSteering(___m_nview, sender, "Stop");

        [HarmonyPatch(typeof(Ship), "RPC_Rudder"), HarmonyPrefix]
        static bool OnRudder(ZNetView ___m_nview, long sender, float value) => !PassOnSteering(___m_nview, sender, "Rudder", value);

        private static bool PassOnSteering(ZNetView view, long sender, string method, params object[] parameters)
        {
            if (s_handedOff.Count == 0 || view == null || !view.IsValid() || view.IsOwner()) return false;
            var zdo = view.GetZDO();
            if (!s_handedOff.TryGetValue(zdo.m_uid, out var handOff)) return false;
            if (sender != handOff.Helmsman || zdo.GetOwner() != handOff.Helmsman) return false;
            if (Time.realtimeSinceStartupAsDouble - handOff.At > PassOnSteeringSeconds) return false;
            view.InvokeRPC(handOff.Helmsman, method, parameters);
            return true;
        }

        private static void PruneExpired()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            s_expired.Clear();
            foreach (var entry in s_handedOff)
                if (now - entry.Value.At > PassOnSteeringSeconds) s_expired.Add(entry.Key);
            foreach (var id in s_expired) s_handedOff.Remove(id);
        }

        private static bool Active() => ConfigEnabled != null && ConfigEnabled.Value && !ServerOwnsShips;
    }
}
