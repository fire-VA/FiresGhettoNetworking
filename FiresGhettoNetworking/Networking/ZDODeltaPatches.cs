using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// ZDO delta compression with bounded-staleness self-heal.
    ///
    /// Sends only changed fields on re-syncs and periodically (every
    /// MaxDeltaWindowSec) emits a full-state vanilla "keyframe" so any silently
    /// dropped delta self-heals within that window. Same idea as keyframes in
    /// video encoding — the I-frame between P-frames bounds the corruption
    /// window when a P-frame is lost.
    ///
    /// Why the window matters: Steam reliable sends can fail to queue when the
    /// per-connection buffer saturates (k_EResultLimitExceeded). When that
    /// happens the server snapshot advances but the client never sees the
    /// data. A naive delta would then compute subsequent diffs against the
    /// advanced snapshot, applying changes on top of state the client doesn't
    /// have — permanently corrupt fields until the field is mutated again.
    /// The periodic keyframe bounds worst-case desync to MaxDeltaWindowSec.
    ///
    /// Wire format stays byte-for-byte compatible with vanilla ZDO.Deserialize:
    /// vanilla sets only the keys present in the packet and never clears the
    /// field dicts, so a delta packet applies cleanly on top of existing client
    /// state. See assembly_valheim/ZDO.cs:565 Deserialize for the contract.
    ///
    /// Public-test note: ZDOExtraData.Get* helpers were removed in the
    /// public-test refactor; the whole class compiles out under PUBLIC_TEST
    /// and sessions fall back to vanilla full-state serialization.
    /// </summary>
#if !PUBLIC_TEST
    [HarmonyPatch]
    public static class ZDODeltaPatches
    {
        private const float MaxDeltaWindowSec = 5f;

        private static readonly Dictionary<long, Dictionary<ZDOID, ZDOSnapshot>> _peerSnapshots
            = new Dictionary<long, Dictionary<ZDOID, ZDOSnapshot>>();

        private sealed class ZDOSnapshot
        {
            public readonly Dictionary<int, float>      floats = new Dictionary<int, float>();
            public readonly Dictionary<int, Vector3>    vec3s  = new Dictionary<int, Vector3>();
            public readonly Dictionary<int, Quaternion> quats  = new Dictionary<int, Quaternion>();
            public readonly Dictionary<int, int>        ints   = new Dictionary<int, int>();
            public readonly Dictionary<int, long>       longs  = new Dictionary<int, long>();
            public uint  dataRevision;
            public float lastKeyframeTime;
        }

        private static long _currentPeerUID;
        private static bool _contextActive;
        private static bool _pendingKeyframe;

        [HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
        [HarmonyPrefix]
        public static void SendZDOs_Prefix(ZDOMan.ZDOPeer peer, bool flush)
        {
            if (FiresGhettoNetworkMod.ConfigEnableZDODelta == null
                || !FiresGhettoNetworkMod.ConfigEnableZDODelta.Value
                || ZNet.instance == null
                || !ZNet.instance.IsDedicated())
            {
                _contextActive = false;
                return;
            }
            _currentPeerUID = peer.m_peer.m_uid;
            _contextActive  = true;
        }

        [HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
        [HarmonyPostfix]
        public static void SendZDOs_Postfix(ZDOMan.ZDOPeer peer, bool flush)
        {
            _contextActive = false;
        }

        // Decide keyframe (full vanilla) vs delta for this (peer, ZDO).
        // Keyframe paths return true so vanilla Serialize runs untouched;
        // the postfix records the keyframe time on the snapshot.
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Serialize))]
        [HarmonyPrefix]
        public static bool ZDO_Serialize_Prefix(ZDO __instance, ZPackage pkg)
        {
            if (!_contextActive) return true;

            Dictionary<ZDOID, ZDOSnapshot> peerMap;
            if (!_peerSnapshots.TryGetValue(_currentPeerUID, out peerMap))
            {
                _pendingKeyframe = true;
                return true;
            }

            ZDOSnapshot snap;
            if (!peerMap.TryGetValue(__instance.m_uid, out snap))
            {
                _pendingKeyframe = true;
                return true;
            }

            if (Time.realtimeSinceStartup - snap.lastKeyframeTime > MaxDeltaWindowSec)
            {
                _pendingKeyframe = true;
                return true;
            }

            _pendingKeyframe = false;
            WriteDelta(__instance, pkg, snap);
            return false;
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Serialize))]
        [HarmonyPostfix]
        public static void ZDO_Serialize_Postfix(ZDO __instance)
        {
            if (!_contextActive) return;

            Dictionary<ZDOID, ZDOSnapshot> peerMap;
            if (!_peerSnapshots.TryGetValue(_currentPeerUID, out peerMap))
            {
                peerMap = new Dictionary<ZDOID, ZDOSnapshot>();
                _peerSnapshots[_currentPeerUID] = peerMap;
            }

            ZDOSnapshot snap;
            if (!peerMap.TryGetValue(__instance.m_uid, out snap))
            {
                snap = new ZDOSnapshot();
                peerMap[__instance.m_uid] = snap;
            }

            CaptureSnapshot(__instance, snap);
            snap.dataRevision = __instance.DataRevision;

            if (_pendingKeyframe)
            {
                snap.lastKeyframeTime = Time.realtimeSinceStartup;
                _pendingKeyframe = false;
            }
        }

        [HarmonyPatch(typeof(ZDOMan), "RemovePeer")]
        [HarmonyPostfix]
        public static void RemovePeer_Postfix(ZNetPeer netPeer)
        {
            if (netPeer == null) return;
            _peerSnapshots.Remove(netPeer.m_uid);
        }

        // Vanilla-compatible delta: emits the flags + per-type blocks vanilla
        // Deserialize expects, with only the (key, value) pairs whose value
        // differs from the snapshot. See assembly_valheim/ZDO.cs:461 Serialize
        // for the reference wire format this mirrors.
        private static void WriteDelta(ZDO zdo, ZPackage pkg, ZDOSnapshot snap)
        {
            var allFloats  = ZDOExtraData.GetFloats(zdo.m_uid);
            var allVec3s   = ZDOExtraData.GetVec3s(zdo.m_uid);
            var allQuats   = ZDOExtraData.GetQuaternions(zdo.m_uid);
            var allInts    = ZDOExtraData.GetInts(zdo.m_uid);
            var allLongs   = ZDOExtraData.GetLongs(zdo.m_uid);
            var allStrings = ZDOExtraData.GetStrings(zdo.m_uid);
            var allBytes   = ZDOExtraData.GetByteArrays(zdo.m_uid);
            var conn       = ZDOExtraData.GetConnection(zdo.m_uid);

            var dF = DiffFloats(allFloats, snap.floats);
            var dV = DiffVec3s (allVec3s,  snap.vec3s);
            var dQ = DiffQuats (allQuats,  snap.quats);
            var dI = DiffInts  (allInts,   snap.ints);
            var dL = DiffLongs (allLongs,  snap.longs);

            bool hasConn = conn != null && conn.m_type != ZDOExtraData.ConnectionType.None;

            // Vanilla sets the rotation bit when current rotation != identity,
            // not on change-since-snap. Match the rule so wire bits line up;
            // the periodic keyframe handles rotation drift if it occurs.
            Vector3 rotEuler = zdo.GetRotation().eulerAngles;
            bool hasRot = rotEuler != Quaternion.identity.eulerAngles;

            ushort bits = 0;
            if (hasConn)              bits |= 0x0001;
            if (dF.Count > 0)         bits |= 0x0002;
            if (dV.Count > 0)         bits |= 0x0004;
            if (dQ.Count > 0)         bits |= 0x0008;
            if (dI.Count > 0)         bits |= 0x0010;
            if (dL.Count > 0)         bits |= 0x0020;
            if (allStrings.Count > 0) bits |= 0x0040;
            if (allBytes.Count > 0)   bits |= 0x0080;

            int dataInt = bits;
            if (zdo.Persistent) dataInt |= 0x0100;
            if (zdo.Distant)    dataInt |= 0x0200;
            dataInt |= ((int)zdo.Type) << 10;
            if (hasRot)         dataInt |= 0x1000;
            ushort data = (ushort)dataInt;

            pkg.Write(data);
            pkg.Write(zdo.GetPrefab());
            if (hasRot) pkg.Write(rotEuler);
            if ((data & 0xFF) == 0) return;

            if (hasConn)
            {
                pkg.Write((byte)conn.m_type);
                pkg.Write(conn.m_target);
            }

            WriteFloatBlock(pkg, dF);
            WriteVec3Block (pkg, dV);
            WriteQuatBlock (pkg, dQ);
            WriteIntBlock  (pkg, dI);
            WriteLongBlock (pkg, dL);
            WriteStringBlock(pkg, allStrings);
            WriteByteBlock  (pkg, allBytes);
        }

        private static List<KeyValuePair<int, float>> DiffFloats(
            List<KeyValuePair<int, float>> current, Dictionary<int, float> snap)
        {
            var d = new List<KeyValuePair<int, float>>();
            foreach (var kv in current)
            {
                float prev;
                if (!snap.TryGetValue(kv.Key, out prev) || prev != kv.Value) d.Add(kv);
            }
            return d;
        }

        private static List<KeyValuePair<int, Vector3>> DiffVec3s(
            List<KeyValuePair<int, Vector3>> current, Dictionary<int, Vector3> snap)
        {
            var d = new List<KeyValuePair<int, Vector3>>();
            foreach (var kv in current)
            {
                Vector3 prev;
                if (!snap.TryGetValue(kv.Key, out prev) || prev != kv.Value) d.Add(kv);
            }
            return d;
        }

        private static List<KeyValuePair<int, Quaternion>> DiffQuats(
            List<KeyValuePair<int, Quaternion>> current, Dictionary<int, Quaternion> snap)
        {
            var d = new List<KeyValuePair<int, Quaternion>>();
            foreach (var kv in current)
            {
                Quaternion prev;
                if (!snap.TryGetValue(kv.Key, out prev) || prev != kv.Value) d.Add(kv);
            }
            return d;
        }

        private static List<KeyValuePair<int, int>> DiffInts(
            List<KeyValuePair<int, int>> current, Dictionary<int, int> snap)
        {
            var d = new List<KeyValuePair<int, int>>();
            foreach (var kv in current)
            {
                int prev;
                if (!snap.TryGetValue(kv.Key, out prev) || prev != kv.Value) d.Add(kv);
            }
            return d;
        }

        private static List<KeyValuePair<int, long>> DiffLongs(
            List<KeyValuePair<int, long>> current, Dictionary<int, long> snap)
        {
            var d = new List<KeyValuePair<int, long>>();
            foreach (var kv in current)
            {
                long prev;
                if (!snap.TryGetValue(kv.Key, out prev) || prev != kv.Value) d.Add(kv);
            }
            return d;
        }

        private static void WriteFloatBlock(ZPackage pkg, List<KeyValuePair<int, float>> list)
        {
            if (list.Count == 0) return;
            pkg.Write((byte)list.Count);
            foreach (var kv in list) { pkg.Write(kv.Key); pkg.Write(kv.Value); }
        }

        private static void WriteVec3Block(ZPackage pkg, List<KeyValuePair<int, Vector3>> list)
        {
            if (list.Count == 0) return;
            pkg.Write((byte)list.Count);
            foreach (var kv in list) { pkg.Write(kv.Key); pkg.Write(kv.Value); }
        }

        private static void WriteQuatBlock(ZPackage pkg, List<KeyValuePair<int, Quaternion>> list)
        {
            if (list.Count == 0) return;
            pkg.Write((byte)list.Count);
            foreach (var kv in list) { pkg.Write(kv.Key); pkg.Write(kv.Value); }
        }

        private static void WriteIntBlock(ZPackage pkg, List<KeyValuePair<int, int>> list)
        {
            if (list.Count == 0) return;
            pkg.Write((byte)list.Count);
            foreach (var kv in list) { pkg.Write(kv.Key); pkg.Write(kv.Value); }
        }

        private static void WriteLongBlock(ZPackage pkg, List<KeyValuePair<int, long>> list)
        {
            if (list.Count == 0) return;
            pkg.Write((byte)list.Count);
            foreach (var kv in list) { pkg.Write(kv.Key); pkg.Write(kv.Value); }
        }

        private static void WriteStringBlock(ZPackage pkg, List<KeyValuePair<int, string>> list)
        {
            if (list.Count == 0) return;
            pkg.Write((byte)list.Count);
            foreach (var kv in list) { pkg.Write(kv.Key); pkg.Write(kv.Value); }
        }

        private static void WriteByteBlock(ZPackage pkg, List<KeyValuePair<int, byte[]>> list)
        {
            if (list.Count == 0) return;
            pkg.Write((byte)list.Count);
            foreach (var kv in list) { pkg.Write(kv.Key); pkg.Write(kv.Value); }
        }

        private static void CaptureSnapshot(ZDO zdo, ZDOSnapshot snap)
        {
            snap.floats.Clear();
            snap.vec3s.Clear();
            snap.quats.Clear();
            snap.ints.Clear();
            snap.longs.Clear();
            foreach (var kv in ZDOExtraData.GetFloats(zdo.m_uid))      snap.floats[kv.Key] = kv.Value;
            foreach (var kv in ZDOExtraData.GetVec3s(zdo.m_uid))       snap.vec3s[kv.Key]  = kv.Value;
            foreach (var kv in ZDOExtraData.GetQuaternions(zdo.m_uid)) snap.quats[kv.Key]  = kv.Value;
            foreach (var kv in ZDOExtraData.GetInts(zdo.m_uid))        snap.ints[kv.Key]   = kv.Value;
            foreach (var kv in ZDOExtraData.GetLongs(zdo.m_uid))       snap.longs[kv.Key]  = kv.Value;
        }
    }
#endif
}
