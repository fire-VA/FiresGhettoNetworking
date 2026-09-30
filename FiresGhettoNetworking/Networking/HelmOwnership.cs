using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// A ship belongs to whoever sails it: the owning game (a player's or the server's) hands the ship to the player it lets
    /// take the helm and passes on steering already on its way. When the server simulates ships, a ship left empty and at rest
    /// is handed back to it by the owning player's game, so the hand-over happens where the physics runs.
    /// </summary>
    [HarmonyPatch]
    public static class HelmOwnership
    {
        public static ConfigEntry<bool> ConfigEnabled;
        internal static bool ServerOwnsShips;
        private const double PassOnSteeringSeconds = 5.0;
        private const int PruneAboveEntries = 32;

        private const string ShipHandBackRpc = "FGN_ShipHandBack";
        private const double ShipPassSeconds = 2.0;
        private const double EmptySecondsBeforeHandBack = 5.0;
        private const double HandBackRetrySeconds = 10.0;
        private const float OccupiedRadiusMeters = 12f;
        private const float AtRestMeters = 1f;
        private const float AtRestSpeed = 0.5f;
        private const int SectorsEachSide = 1;

        private struct HandOff
        {
            public long Helmsman;
            public double At;
        }

        private struct EmptyShip
        {
            public Vector3 Position;
            public double EmptySince;
            public double AskedAt;
        }

        private static readonly Dictionary<ZDOID, HandOff> s_handedOff = new Dictionary<ZDOID, HandOff>();
        private static readonly List<ZDOID> s_expired = new List<ZDOID>();
        private static readonly Dictionary<ZDOID, EmptyShip> s_emptyShips = new Dictionary<ZDOID, EmptyShip>();
        private static readonly HashSet<ZDOID> s_seenThisPass = new HashSet<ZDOID>();
        private static readonly HashSet<int> s_shipPrefabs = new HashSet<int>();
        private static bool s_shipPrefabsScanned;
        private static double s_nextShipPass;

        private static readonly AccessTools.FieldRef<ShipControlls, ZNetView> s_controlsView
            = AccessTools.FieldRefAccess<ShipControlls, ZNetView>("m_nview");

        // ---- The slam on a handoff -----------------------------------------------------------------------------------------
        // R70's MP voyage: both wave slams came 0.3 and 0.6 s after the ship changed owner, with the two peers' seas 1.56 m and 0.14 m
        // apart at that second. Each client blends its own wind (EnvMan starts a 5 s transition whenever its previous one ends, and
        // aims it along the ship it is on), so the new owner's water is not where the old owner's held the hull; the hull settles
        // into it at once and vanilla's slam rule (sinking faster than 2.5 m/s) takes 10 hp. Only a change of owner does this, so
        // vanilla's own 2 s slam interval starts the moment this peer takes a ship over. Single player never changes owner.
        private static readonly AccessTools.FieldRef<Ship, float> s_lastWaterImpactTime = AccessTools.FieldRefAccess<Ship, float>("m_lastWaterImpactTime");
        private static readonly Dictionary<int, bool> s_wasOwner = new Dictionary<int, bool>();
        internal static int HandoffSlamsHeld;

        /// <summary>From FgnHooks' one Ship.CustomFixedUpdate prefix, before vanilla's water force runs.</summary>
        internal static void HoldSlamOnTakeover(Ship ship, ZNetView view)
        {
            if (!(ConfigEnabled?.Value ?? false) || view == null || !view.IsValid()) return;
            bool owner = view.IsOwner();
            int id = ship.GetInstanceID();
            if (owner && s_wasOwner.TryGetValue(id, out bool was) && !was)
            {
                s_lastWaterImpactTime(ship) = Time.time;
                HandoffSlamsHeld++;
                LoggerOptions.LogInfo($"[HelmOwnership] took over '{ship.name}': its water-impact slam waits vanilla's 2 s while the hull settles into this peer's sea.");
            }
            s_wasOwner[id] = owner;
        }

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("11 - Ship Fixes", "Give The Ship To Its Helmsman", true,
                "Vanilla leaves a ship with whichever game owns it (a player's, or the server's with Server-Side Ship Simulation),\n" +
                "so anyone else at the helm steers it through the server and sees it move only once that game's updates come back.\n" +
                "With this on, the owning game hands the ship to the player it lets take the helm and passes on steering already on\n" +
                "its way. With Server-Side Ship Simulation, a ship left empty and at rest goes back to the server after 5 seconds.\n" +
                "Only the ship's current owner needs FGN.");
        }

        internal static void OnShutdown()
        {
            s_handedOff.Clear();
            s_emptyShips.Clear();
            s_shipPrefabs.Clear();
            s_shipPrefabsScanned = false;
        }

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
            s_emptyShips.Remove(zdo.m_uid);
            var helmsman = Player.GetPlayer(playerID);
            LoggerOptions.LogMessage($"[Helm] Handed the {PrefabName(zdo)} to "
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

        internal static void RegisterPeerRpcs(ZNet znet, ZNetPeer peer)
        {
            if (znet.IsServer()) return;
            peer.m_rpc.Register<ZDOID>(ShipHandBackRpc, OnShipHandBackRequested);
        }

        /// <summary>Dedicated server with Server-Side Ship Simulation, every few seconds: a player-owned ship near players that has
        /// no helmsman, nobody near it and has not moved for a while is handed back by its owner's game.</summary>
        internal static void ReturnEmptyShipsToServer(ZDOMan zdoMan)
        {
            if (!Active() || !ServerOwnsShips || ZNet.instance == null || !ZNet.instance.IsDedicated()) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < s_nextShipPass) return;
            s_nextShipPass = now + ShipPassSeconds;
            if (!s_shipPrefabsScanned) ScanShipPrefabs();
            if (s_shipPrefabs.Count == 0) return;

            long serverUid = ZDOMan.GetSessionID();
            var peers = ZNet.instance.GetPeers();
            s_seenThisPass.Clear();
            foreach (var peer in peers)
            {
                if (peer == null || !peer.IsReady()) continue;
                Vector2s zone = ZoneSystem.GetZone(peer.GetRefPos());
                for (int dy = -SectorsEachSide; dy <= SectorsEachSide; dy++)
                    for (int dx = -SectorsEachSide; dx <= SectorsEachSide; dx++)
                    {
                        var objects = zdoMan.m_objectsBySector[(int)ZoneSystem.SectorToIndex(zone.x + dx, zone.y + dy).Sector];
                        if (objects == null) continue;
                        for (int i = 0; i < objects.Count; i++)
                        {
                            var zdo = objects[i];
                            if (!s_shipPrefabs.Contains(zdo.GetPrefab()) || !s_seenThisPass.Add(zdo.m_uid)) continue;
                            ConsiderHandBack(zdo, serverUid, peers, now);
                        }
                    }
            }
            if (s_emptyShips.Count > 0) ForgetShipsNotSeen();
        }

        private static void ConsiderHandBack(ZDO zdo, long serverUid, List<ZNetPeer> peers, double now)
        {
            long owner = zdo.GetOwner();
            ZNetPeer ownerPeer = owner != 0L && owner != serverUid ? ZNet.instance.GetPeer(owner) : null;
            Vector3 position = zdo.GetPosition();
            if (ownerPeer == null || !ownerPeer.IsReady() || !ConnectionEcho.HasFgn(ownerPeer)
                || zdo.GetLong(ZDOVars.s_user) != 0L || AnyPlayerNear(peers, position))
            {
                s_emptyShips.Remove(zdo.m_uid);
                return;
            }
            if (!s_emptyShips.TryGetValue(zdo.m_uid, out var empty) || (empty.Position - position).sqrMagnitude > AtRestMeters * AtRestMeters)
            {
                s_emptyShips[zdo.m_uid] = new EmptyShip { Position = position, EmptySince = now };
                return;
            }
            if (now - empty.EmptySince < EmptySecondsBeforeHandBack || now - empty.AskedAt < HandBackRetrySeconds) return;
            empty.AskedAt = now;
            s_emptyShips[zdo.m_uid] = empty;
            ownerPeer.m_rpc.Invoke(ShipHandBackRpc, zdo.m_uid);
        }

        private static void OnShipHandBackRequested(ZRpc rpc, ZDOID id)
        {
            var zdo = ZDOMan.instance?.GetZDO(id);
            var view = zdo != null && ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
            var serverPeer = ZNet.instance?.GetServerPeer();
            if (view == null || !view.IsValid() || !view.IsOwner() || serverPeer == null) return;
            var ship = view.GetComponent<Ship>();
            if (ship == null || ship.HasPlayerOnboard() || zdo.GetLong(ZDOVars.s_user) != 0L) return;
            var body = view.GetComponent<Rigidbody>();
            if (body != null && body.linearVelocity.magnitude > AtRestSpeed) return;
            ZDOMan.instance.ForceSendZDO(id);
            zdo.SetOwner(serverPeer.m_uid);
            LoggerOptions.LogMessage($"[Helm] Gave the empty {PrefabName(zdo)} back to the server, which sails ships nobody is aboard.");
        }

        private static bool AnyPlayerNear(List<ZNetPeer> peers, Vector3 position)
        {
            foreach (var peer in peers)
                if (peer != null && peer.IsReady() && (peer.GetRefPos() - position).sqrMagnitude < OccupiedRadiusMeters * OccupiedRadiusMeters)
                    return true;
            return false;
        }

        private static void ForgetShipsNotSeen()
        {
            s_expired.Clear();
            foreach (var id in s_emptyShips.Keys)
                if (!s_seenThisPass.Contains(id)) s_expired.Add(id);
            foreach (var id in s_expired) s_emptyShips.Remove(id);
        }

        private static void ScanShipPrefabs()
        {
            if (ZNetScene.instance == null) return;
            s_shipPrefabsScanned = true;
            foreach (var prefab in ZNetScene.instance.m_prefabs)
                if (prefab != null && prefab.GetComponent<Ship>() != null)
                    s_shipPrefabs.Add(prefab.name.GetStableHashCode());
        }

        private static string PrefabName(ZDO zdo)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            return prefab != null ? prefab.name : "ship";
        }

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

        private static bool Active() => ConfigEnabled != null && ConfigEnabled.Value;
    }
}
