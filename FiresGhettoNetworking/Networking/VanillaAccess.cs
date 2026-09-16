using System;
using System.Collections.Generic;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>Accessors for private vanilla networking members; the live assembly refuses direct access at runtime.</summary>
    internal static class VanillaAccess
    {
        internal static readonly AccessTools.FieldRef<ZRoutedRpc, long> RoutedRpcId
            = AccessTools.FieldRefAccess<ZRoutedRpc, long>("m_id");

        internal static readonly AccessTools.FieldRef<ZRoutedRpc, bool> RoutedRpcIsServer
            = AccessTools.FieldRefAccess<ZRoutedRpc, bool>("m_server");

        internal static readonly AccessTools.FieldRef<ZRoutedRpc, List<ZNetPeer>> RoutedRpcPeers
            = AccessTools.FieldRefAccess<ZRoutedRpc, List<ZNetPeer>>("m_peers");

        internal static readonly Action<ZRoutedRpc, ZRoutedRpc.RoutedRPCData> HandleRoutedRpc
            = AccessTools.MethodDelegate<Action<ZRoutedRpc, ZRoutedRpc.RoutedRPCData>>(AccessTools.Method(typeof(ZRoutedRpc), "HandleRoutedRPC"));

        internal static readonly Action<ZRoutedRpc, ZRoutedRpc.RoutedRPCData> RouteRpc
            = AccessTools.MethodDelegate<Action<ZRoutedRpc, ZRoutedRpc.RoutedRPCData>>(AccessTools.Method(typeof(ZRoutedRpc), "RouteRPC"));

        internal static readonly AccessTools.FieldRef<ZDOMan, List<ZDOMan.ZDOPeer>> ZdoPeers
            = AccessTools.FieldRefAccess<ZDOMan, List<ZDOMan.ZDOPeer>>("m_peers");

        internal static readonly AccessTools.FieldRef<ZDOMan, List<ZDOID>> DestroySendList
            = AccessTools.FieldRefAccess<ZDOMan, List<ZDOID>>("m_destroySendList");

        internal static ZDOMan.ZDOPeer FindZdoPeer(long uid)
        {
            if (ZDOMan.instance == null) return null;
            var peers = ZdoPeers(ZDOMan.instance);
            for (int i = 0; i < peers.Count; i++)
                if (peers[i].m_peer != null && peers[i].m_peer.m_uid == uid) return peers[i];
            return null;
        }
    }
}
