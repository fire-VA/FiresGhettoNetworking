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
    [HarmonyPatch]
    public static class StationRouter
    {
        public static ConfigEntry<bool> ConfigEnabled;

        private const double HoldLimitSeconds = 30.0;
        private const float OwnerReachMeters = 160f;

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
                "Vanilla takes the item out of your inventory, then asks whoever your game thinks runs that smelter, kiln,\n" +
                "fermenter, cooking station, fire, shield generator or ballista to add it. If that player has left, is out of\n" +
                "range or has only just been handed the station, the request is ignored and the item is lost. With this on, the\n" +
                "server delivers the request to the station's real owner once their game knows it owns it, and hands a station\n" +
                "nobody is running to the player using it.\n" +
                "DEDICATED SERVER ONLY. No client install needed.");
        }

        [HarmonyPatch(typeof(ZNet), "Shutdown"), HarmonyPostfix]
        static void OnZNetShutdown()
        {
            s_held.Clear();
            s_waiting.Clear();
            s_stationPrefabs.Clear();
        }

        internal static bool TryRoute(ZRoutedRpc routedRpc, ZRoutedRpc.RoutedRPCData data)
        {
            if (ConfigEnabled == null || !ConfigEnabled.Value) return false;
            if (!s_methods.Contains(data.m_methodHash) || data.m_targetZDO.IsNone() || ZDOMan.instance == null) return false;
            var zdo = ZDOMan.instance.GetZDO(data.m_targetZDO);
            if (zdo == null || !IsStation(zdo.GetPrefab())) return false;

            var request = CopyRequest(data);
            if (s_waiting.Contains(zdo.m_uid))
            {
                Hold(request, zdo.GetOwner());
                return true;
            }

            long owner = ResolveOwner(routedRpc, zdo, request.m_senderPeerID);
            if (owner == 0L) return false;
            if (Deliver(routedRpc, zdo, request, owner)) return true;

            ZDOMan.instance.ForceSendZDO(owner, zdo.m_uid);
            Hold(request, owner);
            return true;
        }

        [HarmonyPatch(typeof(ZDOMan), "Update"), HarmonyPostfix]
        static void ReleaseHeld()
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
                    LogDropped(held, "the station no longer exists");
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
                    if (owner != 0L && Deliver(routedRpc, zdo, held.Request, owner))
                    {
                        s_held.RemoveAt(i--);
                        continue;
                    }
                }

                if (now - held.Since > HoldLimitSeconds)
                {
                    LogDropped(held, "nobody running the station could be reached");
                    s_held.RemoveAt(i--);
                    continue;
                }
                s_waiting.Add(station);
            }
        }

        private static long ResolveOwner(ZRoutedRpc routedRpc, ZDO zdo, long sender)
        {
            long owner = zdo.GetOwner();
            if (RunsStation(routedRpc, owner, zdo)) return owner;

            var user = ZNet.instance.GetPeer(sender);
            if (user == null || !user.IsReady() || !ZNetScene.InActiveArea(zdo.GetPosition(), user.GetRefPos())) return 0L;
            if (owner != sender)
            {
                zdo.SetOwner(sender);
                LoggerOptions.LogInfo($"[StationRouter] {Describe(zdo)} was not being run by anyone, handed to {PeerName(sender)}.");
            }
            return sender;
        }

        private static bool RunsStation(ZRoutedRpc routedRpc, long owner, ZDO zdo)
        {
            if (owner == 0L) return false;
            if (owner == VanillaAccess.RoutedRpcId(routedRpc))
                return ZNetScene.instance != null && ZNetScene.instance.FindInstance(zdo) != null;
            var peer = ZNet.instance.GetPeer(owner);
            if (peer == null || !peer.IsReady()) return false;
            Vector3 position = zdo.GetPosition();
            Vector3 refPos = peer.GetRefPos();
            return ZNetScene.InActiveArea(position, refPos)
                || Mathf.Max(Mathf.Abs(position.x - refPos.x), Mathf.Abs(position.z - refPos.z)) <= OwnerReachMeters;
        }

        private static bool Deliver(ZRoutedRpc routedRpc, ZDO zdo, ZRoutedRpc.RoutedRPCData request, long owner)
        {
            request.m_targetPeerID = owner;
            if (owner == VanillaAccess.RoutedRpcId(routedRpc))
            {
                request.m_parameters.SetPos(0);
                VanillaAccess.HandleRoutedRpc(routedRpc, request);
                return true;
            }
            if (!KnowsOwnership(owner, zdo)) return false;
            VanillaAccess.RouteRpc(routedRpc, request);
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
