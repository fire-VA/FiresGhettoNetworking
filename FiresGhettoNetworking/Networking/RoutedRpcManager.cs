using System.Collections.Generic;
using FiresGhettoNetworkMod.AutoTune;
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

            if (target == VanillaAccess.RoutedRpcId(routedRpc))
            {
                VanillaAccess.HandleRoutedRpc(routedRpc, data);
                return;
            }

            if (StationRouter.TryRoute(routedRpc, data)) return;

            bool filtering = FilteringEnabled();
            if (target == 0L && filtering && data.m_methodHash == s_destroyZdoHash && CollectDestroyHolders(data, s_recipients))
            {
                VanillaAccess.HandleRoutedRpc(routedRpc, data);
                SendTo(data, s_recipients);
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
                SendTo(data, s_recipients);
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

        private static void SendTo(ZRoutedRpc.RoutedRPCData data, List<ZNetPeer> recipients)
        {
            if (recipients.Count == 0) return;
            var package = new ZPackage();
            data.Serialize(package);
            foreach (var peer in recipients)
                if (peer.IsReady()) peer.m_rpc.Invoke("RoutedRPC", (object)package);
        }
    }
}
