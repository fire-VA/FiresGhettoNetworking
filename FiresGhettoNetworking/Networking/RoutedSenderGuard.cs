using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Server-side guard for routed RPCs. Vanilla hands every routed handler the sender id the packet itself carries, so a
    /// modified client could claim to be the server or an admin. On the server, a routed packet's sender id is rewritten to
    /// the uid of the connection it arrived on (a packet from no known connection is dropped), so every handler, and every
    /// client the server forwards it to, sees the real sender.
    /// Core carries the same guard; the first of the two to run claims the marker below and the other one stands down.
    /// </summary>
    [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
    internal static class RoutedSenderGuard
    {
        private const string MarkerKey = "Fires.RoutedSenderGuard";
        private const string MyId = "com.Fire.FiresGhettoNetworkMod";
        private const float LogIntervalSeconds = 10f;

        private static readonly Dictionary<long, float> s_nextLog = new Dictionary<long, float>();

        private static bool IsMine()
        {
            object owner = AppDomain.CurrentDomain.GetData(MarkerKey);
            if (owner == null) { AppDomain.CurrentDomain.SetData(MarkerKey, MyId); return true; }
            return owner as string == MyId;
        }

        [HarmonyPrefix]
        private static bool Prefix(ZRpc rpc, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || pkg == null || !IsMine()) return true;
            ZNetPeer peer = ZNet.instance.GetPeer(rpc);
            if (peer == null) return false;   // not from a known connection: drop it

            // 1.6.31: most packets are honest, and the full copy below is garbage per routed RPC. Check the layout and read the sender in place;
            // a malformed packet is still dropped, and only a real mismatch takes the slow path.
            int start = pkg.GetPos();
            try
            {
                pkg.SetPos(0);
                pkg.ReadLong();
                long sender = pkg.ReadLong();
                pkg.ReadLong();
                pkg.ReadZDOID();
                pkg.ReadInt();
                int length = pkg.ReadInt();
                if (length < 0 || length > pkg.Size() - pkg.GetPos()) return false;   // not a readable routed packet: drop it
                if (sender == peer.m_uid) return true;
            }
            catch { return false; }
            finally { pkg.SetPos(start); }

            var data = new ZRoutedRpc.RoutedRPCData();
            try { data.Deserialize(new ZPackage(pkg.GetArray())); }
            catch { return false; }           // not a readable routed packet: drop it
            finally { pkg.SetPos(start); }

            if (data.m_senderPeerID == peer.m_uid) return true;
            long claimed = data.m_senderPeerID;
            data.m_senderPeerID = peer.m_uid;
            pkg.Clear();
            data.Serialize(pkg);
            pkg.SetPos(0);
            LogOnce(peer, claimed, data.m_methodHash);
            return true;
        }

        private static void LogOnce(ZNetPeer peer, long claimed, int methodHash)
        {
            float now = Time.realtimeSinceStartup;
            if (s_nextLog.TryGetValue(peer.m_uid, out float next) && now < next) return;
            s_nextLog[peer.m_uid] = now + LogIntervalSeconds;
            LoggerOptions.LogDebug($"[RpcGuard] corrected sender {claimed} -> {peer.m_uid} for method {methodHash} from {peer.m_playerName}");
        }

        /// <summary>True when a routed call came from the server (for handlers that must only obey the server).</summary>
        internal static bool FromServer(long sender) => ZRoutedRpc.instance != null && sender == ZRoutedRpc.instance.GetServerPeerID();
    }
}
