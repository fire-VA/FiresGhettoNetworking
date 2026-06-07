using System.Collections.Generic;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Server-side routed-RPC manager.
    ///
    /// Replaces vanilla ZRoutedRpc.RPC_RoutedRPC on the server with a path that
    /// (1) runs each registered handler — handlers can mutate the package, drop
    /// the RPC entirely, or set an AoI position hint — and (2) routes the
    /// surviving RPC, optionally restricting the broadcast to peers within a
    /// configurable radius of the target ZDO (or hinted position).
    ///
    /// Vanilla RPC_RoutedRPC semantics this MUST preserve:
    ///   - if target == server's own id → handle locally, DO NOT forward.
    ///   - if target == 0 (broadcast)    → handle locally AND forward.
    ///   - otherwise                     → forward only.
    /// See assembly_valheim/ZRoutedRpc.cs:123 RPC_RoutedRPC for the reference.
    /// </summary>
    public static class RoutedRpcManager
    {
        public static readonly Dictionary<int, string> HashCodeToMethodNameCache
            = new Dictionary<int, string>();

        private static readonly Dictionary<int, List<RpcMethodHandler>> _rpcMethodHandlers
            = new Dictionary<int, List<RpcMethodHandler>>();

        // Reused per call. RPC routing is main-thread single-threaded; safe.
        private static readonly ZRoutedRpc.RoutedRPCData _routedRpcData
            = new ZRoutedRpc.RoutedRPCData();

        // Per-RPC AoI radius override — set by a handler before returning true,
        // consumed by ProcessRoutedRPC on the same call. -1 = use config radius.
        private static float _aoiRadiusOverride = -1f;

        public static void SetAoIRadiusOverride(float radius)
        {
            _aoiRadiusOverride = radius;
        }

        public static void AddHandler(string methodName, RpcMethodHandler handler)
        {
            int methodHashCode = methodName.GetStableHashCode();
            HashCodeToMethodNameCache[methodHashCode] = methodName;

            LoggerOptions.LogInfo($"[RpcRouter] Registering handler for {methodName} ({methodHashCode}): {handler.GetType().Name}");

            List<RpcMethodHandler> handlers;
            if (!_rpcMethodHandlers.TryGetValue(methodHashCode, out handlers))
            {
                handlers = new List<RpcMethodHandler>();
                _rpcMethodHandlers[methodHashCode] = handlers;
            }
            handlers.Add(handler);
        }

        /// <summary>
        /// Entry point from the ZRoutedRpc.RPC_RoutedRPC prefix patch.
        /// Mirrors vanilla's local-vs-forward gating, then runs handlers and
        /// performs (optionally AoI-filtered) forwarding.
        /// </summary>
        public static void ProcessRoutedRPC(ZRoutedRpc routedRpc, ZRpc rpc, ZPackage package)
        {
            _routedRpcData.Deserialize(package);

            long target = _routedRpcData.m_targetPeerID;
            long selfId = routedRpc.m_id;

            if (target == selfId)
            {
                routedRpc.HandleRoutedRPC(_routedRpcData);
                ClearTransientState();
                return;
            }

            if (target == 0L)
            {
                routedRpc.HandleRoutedRPC(_routedRpcData);
            }

            if (!ProcessHandlers(_routedRpcData))
            {
                ClearTransientState();
                return;
            }

            ForwardRpc(routedRpc, target);
            ClearTransientState();
        }

        // Forwards the RPC to the appropriate peer(s). For broadcasts with AoI
        // enabled, narrows to peers within radius of the target ZDO (or the
        // handler-provided position hint). Otherwise calls vanilla RouteRPC,
        // which knows how to handle both the broadcast and the specific-peer
        // cases.
        private static void ForwardRpc(ZRoutedRpc routedRpc, long target)
        {
            bool isBroadcast = target == 0L;
            bool aoiEnabled = FiresGhettoNetworkMod.ConfigEnableRpcAoI != null
                              && FiresGhettoNetworkMod.ConfigEnableRpcAoI.Value;

            if (!isBroadcast || !aoiEnabled)
            {
                routedRpc.RouteRPC(_routedRpcData);
                return;
            }

            float radius = _aoiRadiusOverride > 0f
                ? _aoiRadiusOverride
                : FiresGhettoNetworkMod.ConfigRpcAoIRadius.Value;

            if (!_routedRpcData.m_targetZDO.IsNone())
            {
                RouteRPCWithAoIFromZDO(routedRpc, _routedRpcData, radius);
                return;
            }

            if (SpawnedZonePositionHint.HasHint)
            {
                RouteRPCWithAoIFromPosition(routedRpc, _routedRpcData,
                    SpawnedZonePositionHint.Position, radius);
                return;
            }

            routedRpc.RouteRPC(_routedRpcData);
        }

        private static void ClearTransientState()
        {
            _aoiRadiusOverride = -1f;
            SpawnedZonePositionHint.Clear();
        }

        // AoI broadcast filtered by the target ZDO's world position.
        // Falls back to a plain broadcast if the ZDO can't be resolved.
        private static void RouteRPCWithAoIFromZDO(
            ZRoutedRpc routedRpc,
            ZRoutedRpc.RoutedRPCData rpcData,
            float radius)
        {
            ZDO targetZdo;
            if (!ZDOMan.instance.m_objectsByID.TryGetValue(rpcData.m_targetZDO, out targetZdo))
            {
                routedRpc.RouteRPC(rpcData);
                return;
            }
            RouteRPCWithAoIFromPosition(routedRpc, rpcData, targetZdo.m_position, radius);
        }

        // AoI broadcast filtered by explicit world position.
        // Serialise once, peek each peer's refpos, invoke RoutedRPC manually
        // for peers within radius — bypasses vanilla RouteRPC, since vanilla
        // has no AoI hook.
        private static void RouteRPCWithAoIFromPosition(
            ZRoutedRpc routedRpc,
            ZRoutedRpc.RoutedRPCData rpcData,
            Vector3 worldPos,
            float radius)
        {
            float radiusSqr = radius * radius;

            ZPackage pkg = new ZPackage();
            rpcData.Serialize(pkg);

            foreach (ZNetPeer peer in routedRpc.m_peers)
            {
                if (rpcData.m_senderPeerID == peer.m_uid || !peer.IsReady()) continue;

                Vector3 peerPos = peer.GetRefPos();
                float dx = peerPos.x - worldPos.x;
                float dz = peerPos.z - worldPos.z;

                if (dx * dx + dz * dz <= radiusSqr)
                    peer.m_rpc.Invoke("RoutedRPC", (object)pkg);
            }
        }

        // Runs every registered handler. Each handler can drop the RPC by
        // returning false; AND-semantics so any one drop blocks forwarding.
        public static bool ProcessHandlers(ZRoutedRpc.RoutedRPCData routedRpcData)
        {
            List<RpcMethodHandler> handlers;
            if (!_rpcMethodHandlers.TryGetValue(routedRpcData.m_methodHash, out handlers))
                return true;

            bool result = true;
            foreach (RpcMethodHandler handler in handlers)
                result &= handler.Process(routedRpcData);
            return result;
        }

        public static string MethodHashToString(int methodHash)
        {
            string methodName;
            if (HashCodeToMethodNameCache.TryGetValue(methodHash, out methodName))
                return methodName;
            return string.Format("RPC_{0}", methodHash);
        }

        public static void Reset()
        {
            _rpcMethodHandlers.Clear();
            HashCodeToMethodNameCache.Clear();
            _aoiRadiusOverride = -1f;
        }
    }
}
