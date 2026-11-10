using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FiresGhettoNetworkMod.AutoTune;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Server-side relay for routed RPCs, which vanilla broadcasts to every player. A broadcast about an object goes only to
    /// players the server has sent that object, a destroy only to players who held one (captured before vanilla forgets them),
    /// and a damage number only to players near it.
    /// </summary>
    public static class RoutedRpcManager
    {
        public static readonly Dictionary<int, string> HashCodeToMethodNameCache = new Dictionary<int, string>();

        private static readonly Dictionary<int, List<RpcMethodHandler>> s_handlers = new Dictionary<int, List<RpcMethodHandler>>();
        private static readonly ZRoutedRpc.RoutedRPCData s_data = new ZRoutedRpc.RoutedRPCData();
        private static readonly int s_destroyZdoHash = "DestroyZDO".GetStableHashCode();
        private static readonly HashSet<int> s_worldWide = new HashSet<int>
        {
            "SleepStart".GetStableHashCode(),
            "SleepStop".GetStableHashCode(),
        };
        private static readonly List<ZNetPeer> s_recipients = new List<ZNetPeer>();
        private static readonly List<ZDOID> s_ids = new List<ZDOID>();
        private static readonly List<ZNetPeer> s_serverRecipients = new List<ZNetPeer>();
        private static readonly List<ZDOID> s_serverIds = new List<ZDOID>();
        private static readonly List<ZNetPeer> s_serverDestroyRecipients = new List<ZNetPeer>();
        private static bool s_serverDestroyCaptured;
        private static bool s_hasPositionHint;
        private static Vector3 s_positionHint;
        private static float s_radiusHint;

        // Other mods' hooks on ZRoutedRpc.RouteRPC. Expand World Prefabs cancels Say there and tracks teleports there
        // a filtered player broadcast sent straight with SendTo never passes through it, so those
        // hooks went blind under FGN. While any foreign hook is attached, the filtered broadcast goes through RouteRPC and
        // FGN's own prefix (attached last) sends it to the filtered list, after the others have seen it and could veto it.
        // Rechecked every few seconds because some mods patch RouteRPC late (EWP does it when its config loads).
        private const float ForeignRouteRecheckSeconds = 10f;
        private static readonly MethodInfo s_routeRpcMethod = AccessTools.Method(typeof(ZRoutedRpc), "RouteRPC");
        private static bool s_foreignRouteHooks;
        private static float s_foreignRouteCheckedAt = float.NegativeInfinity;
        private static ZRoutedRpc.RoutedRPCData s_viaRouteData;
        private static List<ZNetPeer> s_viaRouteRecipients;

        public static int HandlerCount => s_handlers.Count;

        public static IEnumerable<string> HandlerMethodNames => HashCodeToMethodNameCache.Values;

        public static void AddHandler(string methodName, RpcMethodHandler handler)
        {
            int hash = methodName.GetStableHashCode();
            HashCodeToMethodNameCache[hash] = methodName;
            if (!s_handlers.TryGetValue(hash, out var handlers))
            {
                handlers = new List<RpcMethodHandler>();
                s_handlers[hash] = handlers;
            }
            if (!handlers.Contains(handler)) handlers.Add(handler);
        }

        public static void SetPositionHint(Vector3 position, float radius)
        {
            s_hasPositionHint = true;
            s_positionHint = position;
            s_radiusHint = radius;
        }

        internal static bool RouterEnabled()
            => FiresGhettoNetworkMod.ConfigEnableRpcRouter != null && FiresGhettoNetworkMod.ConfigEnableRpcRouter.Value;

        internal static bool FilteringEnabled()
            => RouterEnabled() && FiresGhettoNetworkMod.ConfigEnableRpcAoI != null && FiresGhettoNetworkMod.ConfigEnableRpcAoI.Value;

        internal static float PositionRadius() => Mathf.Clamp(EffectiveConfig.RpcAoIRadius(), 64f, 1024f);

        public static void ProcessRoutedRPC(ZRoutedRpc routedRpc, ZRpc rpc, ZPackage package)
        {
            var data = s_data;
            data.Deserialize(package);
            long target = data.m_targetPeerID;
            CreatureOwnership.ObserveRoutedRpc(data);
            ResourceOwnership.Observe(data);   // 1.6.69: resources go to whoever hits them (key off = returns at once)
            GhostOwner169.ObserveRoutedRpc(data);   // 1.6.69 ([ghost]): helm requests for the server ship fallback
            if (Api.FgnApi.RunFilters(data)) return;   // add-on filters (FgnApi), before FGN routes it

            if (target == VanillaAccess.RoutedRpcId(routedRpc))
            {
                // 1.6.31: a headless server has no station instance, so vanilla would drop this one. Listen hosts keep vanilla: they may run it.
                if (ZNet.instance != null && ZNet.instance.IsDedicated() && StationRouter.TryRoute(routedRpc, data)) { StationRouter.CountServerTargeted(); return; }
                VanillaAccess.HandleRoutedRpc(routedRpc, data);
                return;
            }

            if (StationRouter.TryRoute(routedRpc, data)) return;

            bool filtering = FilteringEnabled();
            if (target == 0L && filtering && data.m_methodHash == s_destroyZdoHash && CollectDestroyHolders(data, s_recipients))
            {
                VanillaAccess.HandleRoutedRpc(routedRpc, data);
                Deliver(routedRpc, data, s_recipients);
                return;
            }

            if (target == 0L) VanillaAccess.HandleRoutedRpc(routedRpc, data);

            try
            {
                if (RouterEnabled() && !ProcessHandlers(data)) return;
                if (target != 0L || !filtering || s_worldWide.Contains(data.m_methodHash)
                    || !CollectRecipients(routedRpc, data, s_recipients, s_ids))
                {
                    VanillaAccess.RouteRpc(routedRpc, data);
                    return;
                }
                Deliver(routedRpc, data, s_recipients);
            }
            finally
            {
                s_hasPositionHint = false;
            }
        }

        internal static bool TryRelayServerBroadcast(ZRoutedRpc routedRpc, ZRoutedRpc.RoutedRPCData data)
        {
            if (s_worldWide.Contains(data.m_methodHash)) return false;
            if (data.m_methodHash == s_destroyZdoHash)
            {
                if (!s_serverDestroyCaptured) return false;
                s_serverDestroyCaptured = false;
                SendTo(data, s_serverDestroyRecipients);
                return true;
            }
            try
            {
                ProcessHandlers(data);
                if (!CollectRecipients(routedRpc, data, s_serverRecipients, s_serverIds)) return false;
                SendTo(data, s_serverRecipients);
                return true;
            }
            finally
            {
                s_hasPositionHint = false;
            }
        }

        internal static void CaptureServerDestroyHolders()
        {
            s_serverDestroyCaptured = false;
            if (ZDOMan.instance == null) return;
            var pending = VanillaAccess.DestroySendList(ZDOMan.instance);
            if (pending.Count == 0 || ZRoutedRpc.instance == null) return;
            CollectHolders(pending, VanillaAccess.RoutedRpcId(ZRoutedRpc.instance), s_serverDestroyRecipients);
            s_serverDestroyCaptured = true;
        }

        internal static void ClearServerDestroyHolders() => s_serverDestroyCaptured = false;

        private static bool ProcessHandlers(ZRoutedRpc.RoutedRPCData data)
        {
            if (!s_handlers.TryGetValue(data.m_methodHash, out var handlers)) return true;
            bool relay = true;
            foreach (var handler in handlers) relay &= handler.Process(data);
            return relay;
        }

        private static bool CollectRecipients(ZRoutedRpc routedRpc, ZRoutedRpc.RoutedRPCData data, List<ZNetPeer> recipients, List<ZDOID> ids)
        {
            recipients.Clear();
            if (!data.m_targetZDO.IsNone())
            {
                ids.Clear();
                ids.Add(data.m_targetZDO);
                CollectHolders(ids, data.m_senderPeerID, recipients);
                return true;
            }
            if (!s_hasPositionHint) return false;

            float radiusSqr = s_radiusHint * s_radiusHint;
            foreach (var peer in VanillaAccess.RoutedRpcPeers(routedRpc))
            {
                if (peer.m_uid == data.m_senderPeerID || !peer.IsReady()) continue;
                Vector3 position = peer.GetRefPos();
                float dx = position.x - s_positionHint.x;
                float dz = position.z - s_positionHint.z;
                if (dx * dx + dz * dz <= radiusSqr) recipients.Add(peer);
            }
            return true;
        }

        private static bool CollectDestroyHolders(ZRoutedRpc.RoutedRPCData data, List<ZNetPeer> recipients)
        {
            var parameters = data.m_parameters;
            int saved = parameters.GetPos();
            try
            {
                parameters.SetPos(0);
                var ids = parameters.ReadPackage();
                int count = ids.ReadInt();
                s_ids.Clear();
                for (int i = 0; i < count; i++) s_ids.Add(ids.ReadZDOID());
            }
            catch
            {
                return false;
            }
            finally
            {
                parameters.SetPos(saved);
            }
            CollectHolders(s_ids, data.m_senderPeerID, recipients);
            return true;
        }

        private static void CollectHolders(List<ZDOID> ids, long sender, List<ZNetPeer> recipients)
        {
            recipients.Clear();
            if (ZDOMan.instance == null) return;
            foreach (var zdoPeer in VanillaAccess.ZdoPeers(ZDOMan.instance))
            {
                var peer = zdoPeer.m_peer;
                if (peer == null || peer.m_uid == sender || !peer.IsReady()) continue;
                for (int i = 0; i < ids.Count; i++)
                {
                    if (!zdoPeer.m_zdos.ContainsKey(ids[i])) continue;
                    recipients.Add(peer);
                    break;
                }
            }
        }

        private static void Deliver(ZRoutedRpc routedRpc, ZRoutedRpc.RoutedRPCData data, List<ZNetPeer> recipients)
        {
            if (!ForeignRouteHooks())
            {
                SendTo(data, recipients);
                return;
            }
            s_viaRouteData = data;
            s_viaRouteRecipients = recipients;
            try
            {
                VanillaAccess.RouteRpc(routedRpc, data);
            }
            finally
            {
                s_viaRouteData = null;
                s_viaRouteRecipients = null;
            }
        }

        /// <summary>
        /// For FGN's RouteRPC prefix: whether this call is a filtered player broadcast Deliver passed through RouteRPC, and
        /// who it goes to. Taken once, so a nested RouteRPC from another mod's hook is treated as its own call.
        /// </summary>
        internal static bool TakeFilteredRecipients(ZRoutedRpc.RoutedRPCData data, out List<ZNetPeer> recipients)
        {
            recipients = s_viaRouteRecipients;
            if (recipients == null || !ReferenceEquals(data, s_viaRouteData)) return false;
            s_viaRouteData = null;
            s_viaRouteRecipients = null;
            return true;
        }

        private static bool ForeignRouteHooks()
        {
            float now = Time.realtimeSinceStartup;
            if (now - s_foreignRouteCheckedAt < ForeignRouteRecheckSeconds) return s_foreignRouteHooks;
            s_foreignRouteCheckedAt = now;

            string owners = null;
            try
            {
                HarmonyLib.Patches info = s_routeRpcMethod == null ? null : HarmonyLib.Harmony.GetPatchInfo(s_routeRpcMethod);
                if (info != null)
                {
                    IEnumerable<HarmonyLib.Patch> none = Enumerable.Empty<HarmonyLib.Patch>();
                    var foreign = (info.Prefixes ?? none).Concat(info.Postfixes ?? none).Concat(info.Transpilers ?? none)
                        .Concat(info.Finalizers ?? none)
                        .Where(p => p != null && p.owner != FiresGhettoNetworkMod.PluginGUID)
                        .Select(p => p.owner).Distinct().ToArray();
                    if (foreign.Length > 0) owners = string.Join(", ", foreign);
                }
            }
            catch (Exception ex)
            {
                LoggerOptions.LogDebug($"[RpcRouter] could not read the patches on ZRoutedRpc.RouteRPC ({ex.GetType().Name}: {ex.Message}).");
            }

            bool present = owners != null;
            if (present != s_foreignRouteHooks)
            {
                LoggerOptions.LogDebug(present
                    ? $"[RpcRouter] '{owners}' hooks ZRoutedRpc.RouteRPC; players' filtered broadcasts now pass through RouteRPC so those hooks see them and can cancel them."
                    : "[RpcRouter] no other mod hooks ZRoutedRpc.RouteRPC any more; players' filtered broadcasts are sent directly again.");
                s_foreignRouteHooks = present;
            }
            return present;
        }

        internal static void SendTo(ZRoutedRpc.RoutedRPCData data, List<ZNetPeer> recipients)
        {
            if (recipients.Count == 0) return;
            var package = new ZPackage();
            data.Serialize(package);
            foreach (var peer in recipients)
                if (peer.IsReady()) peer.m_rpc.Invoke("RoutedRPC", (object)package);
        }
    }
}
