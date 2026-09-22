using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Sends only the ZDO fields that changed since the last send to each peer, with a full vanilla keyframe
    /// every MaxDeltaWindowSec so a dropped delta self-heals within that window. Safe because vanilla's
    /// Deserialize only sets the keys present and never clears a bucket. Yields entirely to any other mod
    /// that replaces ZDO.Serialize.
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

        // Another mod can prefix ZDO.Serialize, skip vanilla and write its own bytes (VikingLands.Core's
        // ZDOCache does). Both prefixes writing into the same ZPackage corrupts the read on the far side.
        // Plugin Awake order is not deterministic, so the check happens once at ZNet.Start, by which point
        // every plugin has applied its patches: any foreign bool-returning prefix on ZDO.Serialize and the
        // delta prefix stands down. Either one alone is correct; both together are not.
        private static bool s_yieldToForeignSerializer;
        private static bool s_foreignScanDone;

        [HarmonyPatch(typeof(ZNet), "Start")]
        [HarmonyPostfix]
        public static void ZNet_Start_DetectForeignSerializer()
        {
            if (s_foreignScanDone) return;
            s_foreignScanDone = true;

            try
            {
                var serialize = AccessTools.Method(typeof(ZDO), nameof(ZDO.Serialize));
                if (serialize == null) return;

                var info = Harmony.GetPatchInfo(serialize);
                if (info?.Prefixes == null) return;

                foreach (var prefix in info.Prefixes)
                {
                    // Our own prefixes (this delta + the read-only BigZdoDiagnostic) share our owner id.
                    if (prefix.owner == FiresGhettoNetworkMod.PluginGUID) continue;
                    // Only a bool-returning prefix CAN skip vanilla and write its own bytes;
                    // a void prefix is a read-only observer and never conflicts.
                    if (prefix.PatchMethod == null || prefix.PatchMethod.ReturnType != typeof(bool)) continue;

                    s_yieldToForeignSerializer = true;
                    LoggerOptions.LogWarning(
                        $"[ZDODelta] '{prefix.owner}' already replaces ZDO.Serialize "
                        + $"({prefix.PatchMethod.DeclaringType?.FullName}.{prefix.PatchMethod.Name}). "
                        + "FGN ZDO delta compression DISABLED to avoid double-writing the package and "
                        + "corrupting the wire — the other mod's serialization optimization stays in effect.");
                    return;
                }

                LoggerOptions.LogInfo("[ZDODelta] No foreign ZDO.Serialize replacer detected — delta compression active.");
            }
            catch (System.Exception ex)
            {
                // Fail SAFE: if the scan throws (Harmony API drift), yield rather than risk corruption.
                s_yieldToForeignSerializer = true;
                LoggerOptions.LogWarning($"[ZDODelta] Foreign-serializer scan failed ({ex.Message}); delta disabled as a precaution.");
            }
        }

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
            // Another mod owns ZDO serialization (see ZNet_Start_DetectForeignSerializer) —
            // yield so we never double-write the package.
            if (s_yieldToForeignSerializer) return true;
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

            if (IsPastDeltaWindow(snap, Time.realtimeSinceStartup))
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
            if (s_yieldToForeignSerializer) return;
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
            if (s_yieldToForeignSerializer || pkg == null || _peerSnapshots.Count == 0) return;
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

        // Wire bits, mirroring vanilla's private ZDO.ExtraDataFlags.
        private const int FlagConnections = 0x0001;
        private const int FlagFloats      = 0x0002;
        private const int FlagVec3s       = 0x0004;
        private const int FlagQuaternions = 0x0008;
        private const int FlagInts        = 0x0010;
        private const int FlagLongs       = 0x0020;
        private const int FlagStrings     = 0x0040;
        private const int FlagByteArrays  = 0x0080;
        private const int FlagAnyData     = 0x00FF;
        private const int FlagPersistent  = 0x0100;
        private const int FlagDistant     = 0x0200;
        private const int TypeShift       = 10;
        private const int FlagRotation    = 0x1000;

        /// <summary>
        /// Vanilla's wire layout carrying only the float, vec3, quaternion, int and long entries that changed
        /// since the last send to this peer; strings and byte arrays always go in full. Blocks are written by
        /// vanilla's own helper so the item-count encoding matches what ZDO.Deserialize reads.
        /// </summary>
        private static void WriteDelta(ZDO zdo, ZPackage pkg, ZDOSnapshot snapshot)
        {
            ZDOExtraData.GetData(zdo.m_uid,
                out var floats, out var vec3s, out var quats, out var ints,
                out var longs, out var strings, out var byteArrays, out var connection);

            var changedFloats = Changed(floats, snapshot.floats, (previous, current) => previous != current);
            var changedVec3s  = Changed(vec3s,  snapshot.vec3s,  (previous, current) => previous != current);
            var changedQuats  = Changed(quats,  snapshot.quats,  (previous, current) => previous != current);
            var changedInts   = Changed(ints,   snapshot.ints,   (previous, current) => previous != current);
            var changedLongs  = Changed(longs,  snapshot.longs,  (previous, current) => previous != current);

            bool hasConnection = connection != null && connection.m_type != ZDOExtraData.ConnectionType.None;

            // Vanilla sets the rotation bit on rotation != identity, not on change; the keyframe covers drift.
            Vector3 rotation = zdo.GetRotation().eulerAngles;
            bool hasRotation = rotation != Quaternion.identity.eulerAngles;

            int flags = 0;
            if (hasConnection)           flags |= FlagConnections;
            if (changedFloats.Count > 0) flags |= FlagFloats;
            if (changedVec3s.Count > 0)  flags |= FlagVec3s;
            if (changedQuats.Count > 0)  flags |= FlagQuaternions;
            if (changedInts.Count > 0)   flags |= FlagInts;
            if (changedLongs.Count > 0)  flags |= FlagLongs;
            if (strings.Count > 0)       flags |= FlagStrings;
            if (byteArrays.Count > 0)    flags |= FlagByteArrays;
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

            // Each of these delegates is a heap object, built before the call even when the block is empty,
            // and this runs for every ZDO in every package. Vanilla's helper returns immediately on an empty
            // list, so skipping the call skips the allocation with no change to what reaches the wire.
            if (changedFloats.Count > 0) ZDODataHelper.WriteData(pkg, changedFloats, new Action<float>(pkg.Write));
            if (changedVec3s.Count > 0)  ZDODataHelper.WriteData(pkg, changedVec3s,  new Action<Vector3>(pkg.Write));
            if (changedQuats.Count > 0)  ZDODataHelper.WriteData(pkg, changedQuats,  new Action<Quaternion>(pkg.Write));
            if (changedInts.Count > 0)   ZDODataHelper.WriteData(pkg, changedInts,   new Action<int>(pkg.Write));
            if (changedLongs.Count > 0)  ZDODataHelper.WriteData(pkg, changedLongs,  new Action<long>(pkg.Write));
            if (strings.Count > 0)       ZDODataHelper.WriteData(pkg, strings,       new Action<string>(pkg.Write));
            if (byteArrays.Count > 0)    ZDODataHelper.WriteData(pkg, byteArrays,    new Action<byte[]>(pkg.Write));
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

        // Unity's == on Vector3 and Quaternion is approximate; the comparer is passed in so each type keeps its own.
        private static List<KeyValuePair<int, T>> Changed<T>(
            List<KeyValuePair<int, T>> current, Dictionary<int, T> snapshot, Func<T, T, bool> differs)
        {
            var changed = ChangedBuffer<T>.List;
            changed.Clear();
            foreach (var entry in current)
            {
                T previous;
                if (!snapshot.TryGetValue(entry.Key, out previous) || differs(previous, entry.Value))
                    changed.Add(entry);
            }
            return changed;
        }

        private static void CaptureSnapshot(ZDO zdo, ZDOSnapshot snap)
        {
            snap.floats.Clear();
            snap.vec3s.Clear();
            snap.quats.Clear();
            snap.ints.Clear();
            snap.longs.Clear();
            ZDOExtraData.GetData(zdo.m_uid,
                out var floats, out var vec3s, out var quats, out var ints,
                out var longs, out _, out _, out _);
            if (floats != null) foreach (var kv in floats) snap.floats[kv.Key] = kv.Value;
            if (vec3s  != null) foreach (var kv in vec3s)  snap.vec3s[kv.Key]  = kv.Value;
            if (quats  != null) foreach (var kv in quats)  snap.quats[kv.Key]  = kv.Value;
            if (ints   != null) foreach (var kv in ints)   snap.ints[kv.Key]   = kv.Value;
            if (longs  != null) foreach (var kv in longs)  snap.longs[kv.Key]  = kv.Value;
        }
    }
}
