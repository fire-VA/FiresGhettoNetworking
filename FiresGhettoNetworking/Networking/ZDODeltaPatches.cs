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

            ZDODataHelper.WriteData(pkg, changedFloats, new Action<float>(pkg.Write));
            ZDODataHelper.WriteData(pkg, changedVec3s,  new Action<Vector3>(pkg.Write));
            ZDODataHelper.WriteData(pkg, changedQuats,  new Action<Quaternion>(pkg.Write));
            ZDODataHelper.WriteData(pkg, changedInts,   new Action<int>(pkg.Write));
            ZDODataHelper.WriteData(pkg, changedLongs,  new Action<long>(pkg.Write));
            ZDODataHelper.WriteData(pkg, strings,       new Action<string>(pkg.Write));
            ZDODataHelper.WriteData(pkg, byteArrays,    new Action<byte[]>(pkg.Write));
        }

        // Unity's == on Vector3 and Quaternion is approximate; the comparer is passed in so each type keeps its own.
        private static List<KeyValuePair<int, T>> Changed<T>(
            List<KeyValuePair<int, T>> current, Dictionary<int, T> snapshot, Func<T, T, bool> differs)
        {
            var changed = new List<KeyValuePair<int, T>>();
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
