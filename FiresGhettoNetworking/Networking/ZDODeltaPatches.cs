using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;
using static FiresGhettoNetworkMod.ZdoWireWriter;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Sends only the ZDO fields that changed since the last send to each peer, with a full keyframe every
    /// MaxDeltaWindowSec so a dropped delta self-heals within that window. Safe because vanilla's Deserialize
    /// only sets the keys present and never clears a bucket, which is why it works in both directions: the
    /// server's updates to each player, and the objects a player owns going up to the server. ZdoWireWriter's
    /// ZDO.Serialize hook calls in here, and yields entirely to any other mod that replaces ZDO.Serialize.
    /// </summary>
    [HarmonyPatch]
    public static class ZDODeltaPatches
    {
        private const float MaxDeltaWindowSec = 5f;
        private const float SnapshotSweepIntervalSec = 1f;

        private static readonly Dictionary<long, Dictionary<ZDOID, ZDOSnapshot>> _peerSnapshots
            = new Dictionary<long, Dictionary<ZDOID, ZDOSnapshot>>();
        private static readonly List<ZDOID> _expiredSnapshotIds = new List<ZDOID>();
        private static float _nextSnapshotSweepTime;

        private sealed class ZDOSnapshot
        {
            public readonly Dictionary<int, float>      floats  = new Dictionary<int, float>();
            public readonly Dictionary<int, Vector3>    vec3s   = new Dictionary<int, Vector3>();
            public readonly Dictionary<int, Quaternion> quats   = new Dictionary<int, Quaternion>();
            public readonly Dictionary<int, int>        ints    = new Dictionary<int, int>();
            public readonly Dictionary<int, long>       longs   = new Dictionary<int, long>();
            public readonly Dictionary<int, string>     strings = new Dictionary<int, string>();
            // Byte arrays are held as a hash, not a copy: a companion's inventory blob is hundreds of bytes and there is
            // one snapshot per peer per object.
            public readonly Dictionary<int, ulong>      blobs   = new Dictionary<int, ulong>();
            public uint  dataRevision;
            public float lastKeyframeTime;
        }

        private const ulong FnvOffsetBasis = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        private static long _deltaFrames;
        private static long _keyframes;
        private static long _fieldsHeldBack;
        private static long _blobBytesHeldBack;

        /// <summary>What the delta saved since the last report, for the upload report; resets the counters.</summary>
        internal static string TakeReportLine()
        {
            if (_deltaFrames == 0 && _keyframes == 0) return null;
            string line = $"[Upload] ZDO delta: {_deltaFrames:N0} delta frames, {_keyframes:N0} keyframes, "
                          + $"{_fieldsHeldBack:N0} unchanged fields held back including {_blobBytesHeldBack / 1024f:0.0} KB of blobs.";
            _deltaFrames = 0;
            _keyframes = 0;
            _fieldsHeldBack = 0;
            _blobBytesHeldBack = 0;
            return line;
        }

        private static long _currentPeerUID;
        private static bool _contextActive;
        private static bool _pendingKeyframe;

        internal static bool ContextActive => _contextActive;

        [HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
        [HarmonyPrefix]
        public static void SendZDOs_Prefix(ZDOMan.ZDOPeer peer, bool flush)
        {
            if (FiresGhettoNetworkMod.ConfigEnableZDODelta == null
                || !FiresGhettoNetworkMod.ConfigEnableZDODelta.Value
                || !ZdoWireWriter.BucketsReady)
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

        /// <summary>
        /// Writes a delta for this (peer, ZDO) and returns true, or returns false when a full keyframe is due and the
        /// caller writes the whole object. Called only while ContextActive.
        /// </summary>
        internal static bool TryWriteDelta(ZDO zdo, ZPackage pkg)
        {
            Dictionary<ZDOID, ZDOSnapshot> peerMap;
            if (!_peerSnapshots.TryGetValue(_currentPeerUID, out peerMap))
            {
                _pendingKeyframe = true;
                return false;
            }

            ZDOSnapshot snap;
            if (!peerMap.TryGetValue(zdo.m_uid, out snap))
            {
                _pendingKeyframe = true;
                return false;
            }

            if (IsPastDeltaWindow(snap, Time.realtimeSinceStartup))
            {
                _pendingKeyframe = true;
                return false;
            }

            _pendingKeyframe = false;
            WriteDelta(zdo, pkg, snap);
            _deltaFrames++;
            return true;
        }

        /// <summary>
        /// Records what this peer now holds for the ZDO, after a delta or keyframe. Serialize never changes the ZDO's
        /// data, so this reads the same values whether it runs before or after the bytes are written.
        /// </summary>
        internal static void RecordSent(ZDO zdo)
        {
            Dictionary<ZDOID, ZDOSnapshot> peerMap;
            if (!_peerSnapshots.TryGetValue(_currentPeerUID, out peerMap))
            {
                peerMap = new Dictionary<ZDOID, ZDOSnapshot>();
                _peerSnapshots[_currentPeerUID] = peerMap;
            }

            ZDOSnapshot snap;
            if (!peerMap.TryGetValue(zdo.m_uid, out snap))
            {
                snap = new ZDOSnapshot();
                peerMap[zdo.m_uid] = snap;
            }

            CaptureSnapshot(zdo, snap);
            snap.dataRevision = zdo.DataRevision;

            if (_pendingKeyframe)
            {
                snap.lastKeyframeTime = Time.realtimeSinceStartup;
                _pendingKeyframe = false;
                _keyframes++;
            }
        }

        [HarmonyPatch(typeof(ZDOMan), "RemovePeer")]
        [HarmonyPostfix]
        public static void RemovePeer_Postfix(ZNetPeer netPeer)
        {
            if (netPeer == null) return;
            _peerSnapshots.Remove(netPeer.m_uid);
        }

        [HarmonyPatch(typeof(ZDOMan), "HandleDestroyedZDO")]
        [HarmonyPostfix]
        public static void HandleDestroyedZDO_ForgetSnapshots(ZDOID uid)
        {
            foreach (var peerMap in _peerSnapshots.Values) peerMap.Remove(uid);
        }

        /// <summary>
        /// A snapshot past the delta window is only ever overwritten by the next keyframe, so dropping it changes nothing that is sent.
        /// </summary>
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.Update))]
        [HarmonyPostfix]
        public static void ZDOMan_Update_ForgetExpiredSnapshots()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSnapshotSweepTime) return;
            _nextSnapshotSweepTime = now + SnapshotSweepIntervalSec;
            foreach (var peerMap in _peerSnapshots.Values) ForgetSnapshotsPastDeltaWindow(peerMap, now);
        }

        internal static int SnapshotCountFor(long peerUid)
            => _peerSnapshots.TryGetValue(peerUid, out var peerMap) ? peerMap.Count : 0;

        /// <summary>
        /// A snapshot records what the server last sent a player, but a player's own writes are never sent back to it, so
        /// once it has written an object its copy no longer matches the snapshot. Every object a player sends is sent back
        /// to it in full next time.
        /// </summary>
        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData")]
        [HarmonyPrefix]
        public static void RPC_ZDOData_Prefix(ZRpc rpc, ZPackage pkg)
        {
            if (YieldsToForeignSerializer || pkg == null || _peerSnapshots.Count == 0) return;
            if (FiresGhettoNetworkMod.ConfigEnableZDODelta == null || !FiresGhettoNetworkMod.ConfigEnableZDODelta.Value) return;
            var sender = ConnectionEcho.PeerOf(rpc);
            if (sender == null || !_peerSnapshots.TryGetValue(sender.m_uid, out var peerMap) || peerMap.Count == 0) return;
            ForgetObjectsIn(pkg, peerMap);
        }

        private static void ForgetObjectsIn(ZPackage pkg, Dictionary<ZDOID, ZDOSnapshot> peerMap)
        {
            int start = pkg.GetPos();
            try
            {
                int invalidated = pkg.ReadInt();
                for (int i = 0; i < invalidated; i++) pkg.ReadZDOID();
                for (ZDOID id = pkg.ReadZDOID(); !id.IsNone(); id = pkg.ReadZDOID())
                {
                    pkg.ReadUShort();
                    pkg.ReadUInt();
                    pkg.ReadLong();
                    pkg.ReadVector3();
                    int dataBytes = pkg.ReadInt();
                    pkg.SetPos(pkg.GetPos() + dataBytes);
                    peerMap.Remove(id);
                }
            }
            catch
            {
                peerMap.Clear();
            }
            finally
            {
                pkg.SetPos(start);
            }
        }

        private static void ForgetSnapshotsPastDeltaWindow(Dictionary<ZDOID, ZDOSnapshot> peerMap, float now)
        {
            foreach (var entry in peerMap)
                if (IsPastDeltaWindow(entry.Value, now)) _expiredSnapshotIds.Add(entry.Key);
            foreach (var id in _expiredSnapshotIds) peerMap.Remove(id);
            _expiredSnapshotIds.Clear();
        }

        private static bool IsPastDeltaWindow(ZDOSnapshot snapshot, float now)
            => now - snapshot.lastKeyframeTime > MaxDeltaWindowSec;

        /// <summary>
        /// Vanilla's wire layout carrying only the float, vec3, quaternion, int and long entries that changed
        /// since the last send to this peer; strings and byte arrays always go in full. Blocks are written by
        /// vanilla's own helper so the item-count encoding matches what ZDO.Deserialize reads.
        /// </summary>
        private static void WriteDelta(ZDO zdo, ZPackage pkg, ZDOSnapshot snapshot)
        {
            ZDOID uid = zdo.m_uid;
            int floatCount = ZdoWireWriter.BucketCount<float>(uid, out var floatBucket);
            int vec3Count = ZdoWireWriter.BucketCount<Vector3>(uid, out var vec3Bucket);
            int quatCount = ZdoWireWriter.BucketCount<Quaternion>(uid, out var quatBucket);
            int intCount = ZdoWireWriter.BucketCount<int>(uid, out var intBucket);
            int longCount = ZdoWireWriter.BucketCount<long>(uid, out var longBucket);
            int stringCount = ZdoWireWriter.BucketCount<string>(uid, out var stringBucket);
            int byteArrayCount = ZdoWireWriter.BucketCount<byte[]>(uid, out var byteArrayBucket);
            ZDOConnection connection = ZDOExtraData.GetConnection(uid);

            var changedFloats  = Changed(floatBucket,  floatCount,  snapshot.floats,  (previous, current) => !Same(previous, current));
            var changedVec3s   = Changed(vec3Bucket,   vec3Count,   snapshot.vec3s,   (previous, current) => !Same(previous, current));
            var changedQuats   = Changed(quatBucket,   quatCount,   snapshot.quats,   (previous, current) => !Same(previous, current));
            var changedInts    = Changed(intBucket,    intCount,    snapshot.ints,    (previous, current) => previous != current);
            var changedLongs   = Changed(longBucket,   longCount,   snapshot.longs,   (previous, current) => previous != current);
            var changedStrings = Changed(stringBucket, stringCount, snapshot.strings, (previous, current) => !string.Equals(previous, current, StringComparison.Ordinal));
            var changedBlobs   = ChangedBlobs(byteArrayBucket, byteArrayCount, snapshot.blobs);

            bool hasConnection = connection != null && connection.m_type != ZDOExtraData.ConnectionType.None;

            // Vanilla sets the rotation bit on rotation != identity, not on change; the keyframe covers drift.
            Vector3 rotation = ZdoWireWriter.Rotation(zdo);
            bool hasRotation = rotation != Quaternion.identity.eulerAngles;

            int flags = 0;
            if (hasConnection)           flags |= FlagConnections;
            if (changedFloats.Count > 0) flags |= FlagFloats;
            if (changedVec3s.Count > 0)  flags |= FlagVec3s;
            if (changedQuats.Count > 0)  flags |= FlagQuaternions;
            if (changedInts.Count > 0)   flags |= FlagInts;
            if (changedLongs.Count > 0)  flags |= FlagLongs;
            if (changedStrings.Count > 0) flags |= FlagStrings;
            if (changedBlobs.Count > 0)  flags |= FlagByteArrays;
            if (zdo.Persistent)          flags |= FlagPersistent;
            if (zdo.Distant)             flags |= FlagDistant;
            flags |= ((int)zdo.Type) << TypeShift;
            if (hasRotation)             flags |= FlagRotation;

            pkg.Write((ushort)flags);
            pkg.Write(zdo.GetPrefab());
            if (hasRotation) pkg.Write(rotation);
            if ((flags & FlagAnyData) == 0) return;

            if (hasConnection)
            {
                pkg.Write((byte)connection.m_type);
                pkg.Write(connection.m_target);
            }

            // Written through ZdoWireWriter, which is the block vanilla writes without vanilla's per-call lists and closures.
            ZdoWireWriter.WriteEntries(pkg, changedFloats);
            ZdoWireWriter.WriteEntries(pkg, changedVec3s);
            ZdoWireWriter.WriteEntries(pkg, changedQuats);
            ZdoWireWriter.WriteEntries(pkg, changedInts);
            ZdoWireWriter.WriteEntries(pkg, changedLongs);
            ZdoWireWriter.WriteEntries(pkg, changedStrings);
            ZdoWireWriter.WriteEntries(pkg, changedBlobs);

            _fieldsHeldBack += (floatCount - changedFloats.Count) + (vec3Count - changedVec3s.Count)
                               + (quatCount - changedQuats.Count) + (intCount - changedInts.Count)
                               + (longCount - changedLongs.Count) + (stringCount - changedStrings.Count)
                               + (byteArrayCount - changedBlobs.Count);
        }

        /// <summary>
        /// Byte arrays whose bytes differ from the last send to this peer. A companion carries its inventory as one of
        /// these, hundreds of bytes that rarely change, and sending it on every update was most of what a companion cost.
        /// </summary>
        private static List<KeyValuePair<int, byte[]>> ChangedBlobs(BinarySearchDictionary<int, byte[]> bucket, int count, Dictionary<int, ulong> snapshot)
        {
            var changed = ChangedBuffer<byte[]>.List;
            changed.Clear();
            if (count == 0) return changed;
            int[] keys = ZdoWireWriter.BucketKeys(bucket);
            byte[][] values = ZdoWireWriter.BucketValues(bucket);
            for (int i = 0; i < count; i++)
            {
                byte[] value = values[i];
                ulong previous;
                if (snapshot.TryGetValue(keys[i], out previous) && previous == HashOf(value))
                {
                    _blobBytesHeldBack += value == null ? 0 : value.Length;
                    continue;
                }
                changed.Add(new KeyValuePair<int, byte[]>(keys[i], value));
            }
            return changed;
        }

        private static ulong HashOf(byte[] value)
        {
            if (value == null) return FnvOffsetBasis;
            ulong hash = FnvOffsetBasis;
            for (int i = 0; i < value.Length; i++) hash = (hash ^ value[i]) * FnvPrime;
            return hash ^ (ulong)value.Length;
        }

        /// <summary>
        /// One reusable result list per value type. WriteDelta asks for floats, vec3s, quats, ints and longs
        /// in turn, so every live result has a different T and therefore its own list; none outlives the call
        /// that asked for it. Allocating a fresh list here instead meant five short-lived lists for every ZDO
        /// in every package, which is Gen0 garbage measured in thousands per second on a busy server.
        /// </summary>
        private static class ChangedBuffer<T>
        {
            internal static readonly List<KeyValuePair<int, T>> List = new List<KeyValuePair<int, T>>();
        }

        // Exact, value for value, and never Unity's ==. Unity compares quaternions by dot product, so any pair whose
        // components are larger than a rotation's counts as equal and its update would never be sent; its Vector3 == also
        // calls anything within a hair equal. float.Equals is used rather than ==, so a field left at NaN is not re-sent
        // for ever. What a peer holds has to be what the owner holds, so anything that differs at all is sent.
        private static bool Same(float previous, float current) => previous.Equals(current);

        private static bool Same(Vector3 previous, Vector3 current)
            => previous.x.Equals(current.x) && previous.y.Equals(current.y) && previous.z.Equals(current.z);

        private static bool Same(Quaternion previous, Quaternion current)
            => previous.x.Equals(current.x) && previous.y.Equals(current.y)
               && previous.z.Equals(current.z) && previous.w.Equals(current.w);


        private static List<KeyValuePair<int, T>> Changed<T>(
            BinarySearchDictionary<int, T> bucket, int count, Dictionary<int, T> snapshot, Func<T, T, bool> differs)
        {
            var changed = ChangedBuffer<T>.List;
            changed.Clear();
            if (count == 0) return changed;
            int[] keys = ZdoWireWriter.BucketKeys(bucket);
            T[] values = ZdoWireWriter.BucketValues(bucket);
            for (int i = 0; i < count; i++)
            {
                T previous;
                if (!snapshot.TryGetValue(keys[i], out previous) || differs(previous, values[i]))
                    changed.Add(new KeyValuePair<int, T>(keys[i], values[i]));
            }
            return changed;
        }

        private static void CaptureSnapshot(ZDO zdo, ZDOSnapshot snap)
        {
            ZDOID uid = zdo.m_uid;
            Capture(snap.floats, ZdoWireWriter.BucketCount<float>(uid, out var floats), floats);
            Capture(snap.vec3s, ZdoWireWriter.BucketCount<Vector3>(uid, out var vec3s), vec3s);
            Capture(snap.quats, ZdoWireWriter.BucketCount<Quaternion>(uid, out var quats), quats);
            Capture(snap.ints, ZdoWireWriter.BucketCount<int>(uid, out var ints), ints);
            Capture(snap.longs, ZdoWireWriter.BucketCount<long>(uid, out var longs), longs);
            Capture(snap.strings, ZdoWireWriter.BucketCount<string>(uid, out var strings), strings);
            CaptureBlobs(snap.blobs, ZdoWireWriter.BucketCount<byte[]>(uid, out var blobs), blobs);
        }

        private static void CaptureBlobs(Dictionary<int, ulong> snapshot, int count, BinarySearchDictionary<int, byte[]> bucket)
        {
            snapshot.Clear();
            if (count == 0) return;
            int[] keys = ZdoWireWriter.BucketKeys(bucket);
            byte[][] values = ZdoWireWriter.BucketValues(bucket);
            for (int i = 0; i < count; i++) snapshot[keys[i]] = HashOf(values[i]);
        }

        private static void Capture<T>(Dictionary<int, T> snapshot, int count, BinarySearchDictionary<int, T> bucket)
        {
            snapshot.Clear();
            if (count == 0) return;
            int[] keys = ZdoWireWriter.BucketKeys(bucket);
            T[] values = ZdoWireWriter.BucketValues(bucket);
            for (int i = 0; i < count; i++) snapshot[keys[i]] = values[i];
        }
    }
}
