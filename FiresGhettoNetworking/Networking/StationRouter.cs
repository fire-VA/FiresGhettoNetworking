using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Delivers item inserts to the player who really runs a smelter, kiln, fermenter, cooking station, fire, shield generator
    /// or ballista. Vanilla asks whoever the inserting player's game believes owns it, and the item is lost when that is wrong;
    /// the server forwards the request once the real owner's game knows it owns the station.
    /// </summary>
    // Smelter output / inserts could vanish on dedicated servers when the router would (a) hand an OWNED station to the inserting player whenever it judged the owner not to be running it (a stale ref pos after
    // a teleport was enough), so the old vanilla owner and the new one both smelted the same queue; (b) count an owner within a 160 m
    // box as running it, where its game may have no instance and vanilla drops the RPC silently; (c) run the RPC on the headless
    // server. Now an owned station is never reassigned (vanilla's ReleaseNearbyZDOS moves a stale owner), only an owner whose active
    // area holds the station counts, nothing runs on the server, and an insert that cannot be delivered is given back at the player's
    // feet instead of being eaten.
    [HarmonyPatch]
    public static class StationRouter
    {
        public static ConfigEntry<bool> ConfigEnabled;

        private const double HoldLimitSeconds = 30.0;

        private static readonly HashSet<int> s_methods = new HashSet<int>
        {
            "RPC_AddOre".GetStableHashCode(),
            "RPC_AddFuel".GetStableHashCode(),
            "RPC_AddItem".GetStableHashCode(),
            "RPC_AddAmmo".GetStableHashCode(),
            "RPC_AddFuelAmount".GetStableHashCode(),
            "RPC_EmptyProcessed".GetStableHashCode(),
            "RPC_Tap".GetStableHashCode(),
            "RPC_RemoveDoneItem".GetStableHashCode(),
            "RPC_ToggleOn".GetStableHashCode(),
        };

        private static readonly int s_addOre = "RPC_AddOre".GetStableHashCode();
        private static readonly int s_addItem = "RPC_AddItem".GetStableHashCode();
        private static readonly int s_addAmmo = "RPC_AddAmmo".GetStableHashCode();
        private static readonly int s_addFuel = "RPC_AddFuel".GetStableHashCode();

        private sealed class Held
        {
            public ZRoutedRpc.RoutedRPCData Request;
            public long Target;
            public double Since;
        }

        private static readonly Dictionary<int, bool> s_stationPrefabs = new Dictionary<int, bool>();
        private static readonly List<Held> s_held = new List<Held>();
        private static readonly HashSet<ZDOID> s_waiting = new HashSet<ZDOID>();

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("12 - Advanced", "Fix Lost Station Inserts", true,
                Cfg.Adv("Stops smelters, kilns and fires losing items put in or taken out (server only)."));
        }

        internal static void OnZNetShutdown()
        {
            s_held.Clear();
            s_waiting.Clear();
            s_stationPrefabs.Clear();
        }

        // Read on every routed RPC rather than at startup, so the fix turns on and off live.
        internal static bool Enabled => ConfigEnabled != null && ConfigEnabled.Value;

        private static int s_serverTargeted;
        // Why it acted, for the first few and then every 50th: shows how often requests really arrive aimed at the server.
        internal static void CountServerTargeted()
        {
            s_serverTargeted++;
            if (s_serverTargeted <= 5 || s_serverTargeted % 50 == 0) LoggerOptions.LogDebug($"[StationRouter] station request aimed at the headless server went to the router instead of being dropped ({s_serverTargeted} so far).");
        }

        internal static bool TryRoute(ZRoutedRpc routedRpc, ZRoutedRpc.RoutedRPCData data)
        {
            if (!Enabled) return false;
            if (!s_methods.Contains(data.m_methodHash) || data.m_targetZDO.IsNone() || ZDOMan.instance == null) return false;
            var zdo = ZDOMan.instance.GetZDO(data.m_targetZDO);
            if (zdo == null || !IsStation(zdo.GetPrefab())) return false;

            var request = CopyRequest(data);
            if (s_waiting.Contains(zdo.m_uid))
            {
                LoggerOptions.LogDebug($"[StationRouter] {Describe(zdo)}: {MethodName(request)} queued behind an earlier request.");
                Hold(request, zdo.GetOwner());
                return true;
            }

            long owner = ResolveOwner(routedRpc, zdo, request.m_senderPeerID);
            if (owner != 0L && Deliver(zdo, request, owner)) return true;

            if (owner != 0L) ZDOMan.instance.ForceSendZDO(owner, zdo.m_uid);
            LoggerOptions.LogDebug($"[StationRouter] {Describe(zdo)}: {MethodName(request)} from {PeerName(request.m_senderPeerID)} held (owner {zdo.GetOwner()}, target {owner}).");
            Hold(request, owner);
            return true;
        }

        internal static void ReleaseHeld()
        {
            if (s_held.Count == 0) return;
            var routedRpc = ZRoutedRpc.instance;
            if (routedRpc == null || ZDOMan.instance == null || ZNet.instance == null || !ZNet.instance.IsServer())
            {
                s_held.Clear();
                s_waiting.Clear();
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            s_waiting.Clear();
            for (int i = 0; i < s_held.Count; i++)
            {
                var held = s_held[i];
                var station = held.Request.m_targetZDO;
                var zdo = ZDOMan.instance.GetZDO(station);
                if (zdo == null)
                {
                    GiveBack(held, null, "the station no longer exists");
                    s_held.RemoveAt(i--);
                    continue;
                }

                if (!s_waiting.Contains(station))
                {
                    long owner = ResolveOwner(routedRpc, zdo, held.Request.m_senderPeerID);
                    if (owner != 0L && owner != held.Target)
                    {
                        held.Target = owner;
                        ZDOMan.instance.ForceSendZDO(owner, zdo.m_uid);
                    }
                    if (owner != 0L && Deliver(zdo, held.Request, owner))
                    {
                        s_held.RemoveAt(i--);
                        continue;
                    }
                }

                if (now - held.Since > HoldLimitSeconds)
                {
                    GiveBack(held, zdo, "nobody running the station could be reached");
                    s_held.RemoveAt(i--);
                    continue;
                }
                s_waiting.Add(station);
            }
        }

        /// <summary>The peer to deliver to, or 0 (hold). Only a station with NO player owner (none, or the headless server) is handed to
        /// the player using it, and only when it is inside that player's active area. A player owner is never replaced here.</summary>
        private static long ResolveOwner(ZRoutedRpc routedRpc, ZDO zdo, long sender)
        {
            long owner = zdo.GetOwner();
            long server = VanillaAccess.RoutedRpcId(routedRpc);
            if (owner != 0L && owner != server)
            {
                if (RunsStation(owner, zdo)) return owner;
                LoggerOptions.LogDebug($"[StationRouter] {Describe(zdo)}: owner {PeerName(owner)} does not cover it; not reassigned, held for vanilla's hand-off.");
                return 0L;
            }

            var user = ZNet.instance.GetPeer(sender);
            if (user == null || !user.IsReady()
                || !ZNetScene.InActiveArea(zdo.GetPosition(), user.GetRefPos()))
            {
                LoggerOptions.LogDebug($"[StationRouter] {Describe(zdo)}: no player owner and {PeerName(sender)} does not cover it; held.");
                return 0L;
            }
            zdo.SetOwner(sender);
            LoggerOptions.LogDebug($"[StationRouter] {Describe(zdo)} had no player running it ({(owner == 0L ? "no owner" : "server")}), handed to {PeerName(sender)}.");
            return sender;
        }

        private static bool RunsStation(long owner, ZDO zdo)
        {
            var peer = ZNet.instance.GetPeer(owner);
            if (peer == null || !peer.IsReady()) return false;
            return ZNetScene.InActiveArea(zdo.GetPosition(), peer.GetRefPos());
        }

        private static bool Deliver(ZDO zdo, ZRoutedRpc.RoutedRPCData request, long owner)
        {
            request.m_targetPeerID = owner;
            if (!KnowsOwnership(owner, zdo)) return false;
            request.m_parameters.SetPos(0);
            VanillaAccess.RouteRpc(ZRoutedRpc.instance, request);
            LoggerOptions.LogDebug($"[StationRouter] {Describe(zdo)}: {MethodName(request)} from {PeerName(request.m_senderPeerID)} delivered to {PeerName(owner)}.");
            return true;
        }

        private static bool KnowsOwnership(long owner, ZDO zdo)
        {
            var zdoPeer = VanillaAccess.FindZdoPeer(owner);
            return zdoPeer != null
                && zdoPeer.m_zdos.TryGetValue(zdo.m_uid, out var sent)
                && sent.m_ownerRevision >= zdo.OwnerRevision;
        }

        private static void Hold(ZRoutedRpc.RoutedRPCData request, long target)
        {
            s_held.Add(new Held { Request = request, Target = target, Since = Time.realtimeSinceStartupAsDouble });
            s_waiting.Add(request.m_targetZDO);
        }

        /// <summary>An insert that could not be delivered: the player's game already took the item, so one is dropped back at their feet
        /// (a server-made ItemDrop; vanilla hands it to the covering player). A request that carries no item is only logged.</summary>
        private static void GiveBack(Held held, ZDO zdo, string reason)
        {
            string item = ItemOf(held.Request, zdo);
            var peer = ZNet.instance != null ? ZNet.instance.GetPeer(held.Request.m_senderPeerID) : null;
            GameObject prefab = item != null && ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(item) : null;
            if (prefab == null || prefab.GetComponent<ItemDrop>() == null || peer == null)
            {
                LogDropped(held, reason + (item != null ? $" ({item} not given back: {(peer == null ? "player gone" : "unknown item")})" : ""));
                return;
            }
            Vector3 at = peer.GetRefPos() + Vector3.up;
            var drop = Object.Instantiate(prefab, at, Quaternion.identity).GetComponent<ItemDrop>();
            drop.SetStack(1);
            LoggerOptions.LogWarning($"[StationRouter] a {MethodName(held.Request)} to {(zdo != null ? Describe(zdo) : "a station")} from "
                + $"{PeerName(held.Request.m_senderPeerID)} was not delivered ({reason}); 1 {item} given back at ({at.x:F0}, {at.z:F0}).");
        }

        private static string ItemOf(ZRoutedRpc.RoutedRPCData request, ZDO zdo)
        {
            int m = request.m_methodHash;
            // 1.6.31: Fermenter's RPC_AddItem sends the item's prefab hash and a bool, not a name as the cooking station does.
            if (m == s_addItem && zdo != null && ZNetScene.instance != null)
            {
                var station = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                if (station != null && station.GetComponentInChildren<Fermenter>(true) != null)
                {
                    try { request.m_parameters.SetPos(0); var drop = ZNetScene.instance.GetPrefab(request.m_parameters.ReadInt()); return drop != null ? drop.name : null; }
                    catch { return null; }
                }
            }
            if (m == s_addOre || m == s_addItem || m == s_addAmmo)
            {
                try { request.m_parameters.SetPos(0); return request.m_parameters.ReadString(); }
                catch { return null; }
            }
            if (m == s_addFuel && zdo != null && ZNetScene.instance != null)
            {
                var prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                if (prefab == null) return null;
                Smelter smelter = prefab.GetComponentInChildren<Smelter>(true);
                if (smelter != null && smelter.m_fuelItem != null) return smelter.m_fuelItem.name;
                Fireplace fire = prefab.GetComponentInChildren<Fireplace>(true);
                if (fire != null && fire.m_fuelItem != null) return fire.m_fuelItem.name;
                CookingStation cooker = prefab.GetComponentInChildren<CookingStation>(true);
                if (cooker != null && cooker.m_fuelItem != null) return cooker.m_fuelItem.name;
                // Several fuels are allowed and the RPC doesn't say which: only a single-fuel generator can name it.
                ShieldGenerator shield = prefab.GetComponentInChildren<ShieldGenerator>(true);
                if (shield != null && shield.m_fuelItems != null && shield.m_fuelItems.Count == 1 && shield.m_fuelItems[0] != null) return shield.m_fuelItems[0].name;
            }
            return null;
        }

        private static string MethodName(ZRoutedRpc.RoutedRPCData request)
        {
            foreach (string n in new[] { "RPC_AddOre", "RPC_AddFuel", "RPC_AddItem", "RPC_AddAmmo", "RPC_AddFuelAmount", "RPC_EmptyProcessed", "RPC_Tap", "RPC_RemoveDoneItem", "RPC_ToggleOn" })
                if (n.GetStableHashCode() == request.m_methodHash) return n;
            return request.m_methodHash.ToString();
        }

        private static ZRoutedRpc.RoutedRPCData CopyRequest(ZRoutedRpc.RoutedRPCData data)
        {
            var copy = new ZRoutedRpc.RoutedRPCData
            {
                m_msgID = data.m_msgID,
                m_senderPeerID = data.m_senderPeerID,
                m_targetPeerID = data.m_targetPeerID,
                m_targetZDO = data.m_targetZDO,
                m_methodHash = data.m_methodHash,
                m_parameters = new ZPackage(data.m_parameters.GetArray()),
            };
            copy.m_parameters.SetPos(0);
            return copy;
        }

        private static bool IsStation(int prefabHash)
        {
            if (s_stationPrefabs.TryGetValue(prefabHash, out bool station)) return station;
            if (ZNetScene.instance == null) return false;
            var prefab = ZNetScene.instance.GetPrefab(prefabHash);
            station = prefab != null
                && (prefab.GetComponentInChildren<Smelter>(true) != null
                    || prefab.GetComponentInChildren<Fermenter>(true) != null
                    || prefab.GetComponentInChildren<CookingStation>(true) != null
                    || prefab.GetComponentInChildren<Fireplace>(true) != null
                    || prefab.GetComponentInChildren<ShieldGenerator>(true) != null
                    || prefab.GetComponentInChildren<Turret>(true) != null);
            s_stationPrefabs[prefabHash] = station;
            return station;
        }

        private static void LogDropped(Held held, string reason)
        {
            var zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(held.Request.m_targetZDO) : null;
            LoggerOptions.LogWarning($"[StationRouter] a request to {(zdo != null ? Describe(zdo) : "a station")} from "
                + $"{PeerName(held.Request.m_senderPeerID)} was not delivered: {reason}.");
        }

        private static string Describe(ZDO zdo)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            Vector3 p = zdo.GetPosition();
            return $"{(prefab != null ? prefab.name : "station")} at ({p.x:F0}, {p.z:F0})";
        }

        private static string PeerName(long uid)
        {
            var peer = ZNet.instance != null ? ZNet.instance.GetPeer(uid) : null;
            return peer != null && !string.IsNullOrEmpty(peer.m_playerName) ? peer.m_playerName : uid.ToString();
        }
    }
}
