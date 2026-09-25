using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// FGN's one hook on ZDO.Serialize, plus the nested-package write the send path runs under it. Vanilla builds seven
    /// lists, eight closures and a boxed enumerator per data type for every object it sends, then copies each nested
    /// package into a new array; a busy client sends about 2,000 objects a second. This writes the same bytes straight
    /// from ZDOExtraData's buckets into the outgoing package. The first objects of each session are checked against
    /// vanilla's own bytes, and any difference hands the work back to vanilla. The server's delta frames and the
    /// large-ZDO diagnostic run from this hook too.
    /// </summary>
    [HarmonyPatch]
    public static class ZdoWireWriter
    {
        private const int ChecksAgainstVanilla = 2000;
        private const int NestedSelfTestNumber = 0x5A17C0DE;
        private const string NestedSelfTestText = "fgn nested package";

        // Wire bits, mirroring vanilla's private ZDO.ExtraDataFlags.
        internal const int FlagConnections = 0x0001;
        internal const int FlagFloats      = 0x0002;
        internal const int FlagVec3s       = 0x0004;
        internal const int FlagQuaternions = 0x0008;
        internal const int FlagInts        = 0x0010;
        internal const int FlagLongs       = 0x0020;
        internal const int FlagStrings     = 0x0040;
        internal const int FlagByteArrays  = 0x0080;
        internal const int FlagAnyData     = 0x00FF;
        internal const int FlagPersistent  = 0x0100;
        internal const int FlagDistant     = 0x0200;
        internal const int TypeShift       = 10;
        internal const int FlagRotation    = 0x1000;

        private static AccessTools.FieldRef<ZDO, Vector3> s_rotation;
        private static AccessTools.FieldRef<ZPackage, MemoryStream> s_stream;
        private static AccessTools.FieldRef<ZPackage, BinaryWriter> s_writer;
        private static AccessTools.FieldRef<ZPackage, BinaryReader> s_reader;
        private static bool s_objectsReady;
        private static bool s_nestedReady;
        private static int s_matchedVanilla;

        // Another mod can prefix ZDO.Serialize, skip vanilla and write its own bytes (VikingLands.Core's ZDOCache does).
        // Two prefixes writing into the same ZPackage corrupt the read on the far side. Plugin Awake order is not
        // deterministic, so the check happens once at ZNet.Start, by which point every plugin has applied its patches:
        // with any foreign bool-returning prefix on ZDO.Serialize, FGN's writer and delta frames stand down.
        private static bool s_yieldToForeignSerializer;
        private static bool s_foreignScanDone;

        [ThreadStatic] private static bool t_writingVanillaReference;

        internal static bool YieldsToForeignSerializer => s_yieldToForeignSerializer;

        /// <summary>True once the ZDOExtraData stores and their arrays are bound, so the bucket readers below work.</summary>
        internal static bool BucketsReady => s_objectsReady;

        /// <summary>The object's raw euler rotation, the value vanilla Serialize writes.</summary>
        internal static Vector3 Rotation(ZDO zdo) => s_rotation(zdo);

        internal static int BucketCount<T>(ZDOID uid, out BinarySearchDictionary<int, T> bucket) => Bucket<T>.Count(uid, out bucket);

        internal static int[] BucketKeys<T>(BinarySearchDictionary<int, T> bucket) => Bucket<T>.Keys(bucket);

        internal static T[] BucketValues<T>(BinarySearchDictionary<int, T> bucket) => Bucket<T>.Values(bucket);

        /// <summary>A whole bucket as vanilla writes it: the item count, then each key and value.</summary>
        internal static void WriteBucket<T>(ZPackage pkg, BinarySearchDictionary<int, T> bucket, int count) => Bucket<T>.Write(pkg, bucket, count);

        /// <summary>The same block from a caller's list, for the delta frames' changed entries.</summary>
        internal static void WriteEntries<T>(ZPackage pkg, List<KeyValuePair<int, T>> entries)
        {
            if (entries.Count == 0) return;
            pkg.WriteNumItems(entries.Count);
            Action<ZPackage, T> writeValue = Bucket<T>.WriteValue;
            for (int i = 0; i < entries.Count; i++)
            {
                pkg.Write(entries[i].Key);
                writeValue(pkg, entries[i].Value);
            }
        }

        private static bool Enabled => FiresGhettoNetworkMod.ConfigAllocationFreeZdoWrites?.Value ?? false;

        /// <summary>
        /// One ZDOExtraData store and the arrays inside its BinarySearchDictionary buckets, read in place in the order
        /// CopyTo (and so vanilla's lists) hands the entries out.
        /// </summary>
        private static class Bucket<T>
        {
            internal static Dictionary<ZDOID, BinarySearchDictionary<int, T>> Store;
            internal static AccessTools.FieldRef<BinarySearchDictionary<int, T>, int[]> Keys;
            internal static AccessTools.FieldRef<BinarySearchDictionary<int, T>, T[]> Values;
            internal static AccessTools.FieldRef<BinarySearchDictionary<int, T>, ushort> Length;
            internal static Action<ZPackage, T> WriteValue;

            internal static bool Bind(string storeField, Action<ZPackage, T> writeValue)
            {
                Store = AccessTools.Field(typeof(ZDOExtraData), storeField)?.GetValue(null) as Dictionary<ZDOID, BinarySearchDictionary<int, T>>;
                Keys = AccessTools.FieldRefAccess<BinarySearchDictionary<int, T>, int[]>("m_keys");
                Values = AccessTools.FieldRefAccess<BinarySearchDictionary<int, T>, T[]>("m_values");
                Length = AccessTools.FieldRefAccess<BinarySearchDictionary<int, T>, ushort>("m_length");
                WriteValue = writeValue;
                return Store != null;
            }

            internal static int Count(ZDOID uid, out BinarySearchDictionary<int, T> bucket)
                => Store.TryGetValue(uid, out bucket) ? Length(bucket) : 0;

            internal static void Write(ZPackage pkg, BinarySearchDictionary<int, T> bucket, int count)
            {
                if (count == 0) return;
                pkg.WriteNumItems(count);
                int[] keys = Keys(bucket);
                T[] values = Values(bucket);
                for (int i = 0; i < count; i++)
                {
                    pkg.Write(keys[i]);
                    WriteValue(pkg, values[i]);
                }
            }
        }

        /// <summary>Binds the private members the writers read. Runs before this class is patched.</summary>
        internal static void Initialize()
        {
            try
            {
                s_rotation = AccessTools.FieldRefAccess<ZDO, Vector3>("m_rotation");
                s_stream = AccessTools.FieldRefAccess<ZPackage, MemoryStream>("m_stream");
                s_writer = AccessTools.FieldRefAccess<ZPackage, BinaryWriter>("m_writer");
                s_reader = AccessTools.FieldRefAccess<ZPackage, BinaryReader>("m_reader");
                s_objectsReady = Bucket<float>.Bind("s_floats", (pkg, value) => pkg.Write(value))
                                 & Bucket<Vector3>.Bind("s_vec3", (pkg, value) => pkg.Write(value))
                                 & Bucket<Quaternion>.Bind("s_quats", (pkg, value) => pkg.Write(value))
                                 & Bucket<int>.Bind("s_ints", (pkg, value) => pkg.Write(value))
                                 & Bucket<long>.Bind("s_longs", (pkg, value) => pkg.Write(value))
                                 & Bucket<string>.Bind("s_strings", (pkg, value) => pkg.Write(value))
                                 & Bucket<byte[]>.Bind("s_byteArrays", (pkg, value) => pkg.Write(value));
                s_nestedReady = NestedWriteMatchesVanilla();
            }
            catch (Exception ex)
            {
                s_objectsReady = false;
                s_nestedReady = false;
                LoggerOptions.LogWarning($"[ZdoWrite] vanilla writes kept: this game build's ZDO or ZPackage internals are not the ones FGN expects ({ex.Message}).");
                return;
            }
            if (!s_objectsReady) LoggerOptions.LogWarning("[ZdoWrite] vanilla ZDO writes kept: ZDOExtraData's stores are not the ones FGN expects.");
            if (!s_nestedReady) LoggerOptions.LogWarning("[ZdoWrite] vanilla nested-package writes kept: FGN's copy-free write did not match vanilla's.");
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        [HarmonyPostfix]
        public static void ZNet_Start_DetectForeignSerializer()
        {
            if (s_foreignScanDone) return;

            try
            {
                var serialize = AccessTools.Method(typeof(ZDO), nameof(ZDO.Serialize));
                var info = serialize == null ? null : Harmony.GetPatchInfo(serialize);
                if (info?.Prefixes != null)
                {
                    foreach (var prefix in info.Prefixes)
                    {
                        if (prefix.owner == FiresGhettoNetworkMod.PluginGUID) continue;
                        // Only a bool-returning prefix can skip vanilla and write its own bytes; a void prefix only observes.
                        if (prefix.PatchMethod == null || prefix.PatchMethod.ReturnType != typeof(bool)) continue;

                        s_yieldToForeignSerializer = true;
                        LoggerOptions.LogWarning(
                            $"[ZdoWrite] '{prefix.owner}' already replaces ZDO.Serialize "
                            + $"({prefix.PatchMethod.DeclaringType?.FullName}.{prefix.PatchMethod.Name}). "
                            + "FGN's ZDO writer and delta compression are OFF so the package is never written twice; "
                            + "the other mod's serializer stays in effect.");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                s_yieldToForeignSerializer = true;
                LoggerOptions.LogWarning($"[ZdoWrite] foreign-serializer scan failed ({ex.Message}); FGN's ZDO writer and delta compression are off as a precaution.");
            }
            s_foreignScanDone = true;
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Serialize))]
        [HarmonyPrefix]
        public static bool ZDO_Serialize_Prefix(ZDO __instance, ZPackage pkg)
        {
            if (t_writingVanillaReference) return true;
            BigZdoDiagnostic.ReportLiveBuckets(__instance);
            if (s_yieldToForeignSerializer) return true;

            bool deltaContext = ZDODeltaPatches.ContextActive;
            if (deltaContext && ZDODeltaPatches.TryWriteDelta(__instance, pkg))
            {
                ZDODeltaPatches.RecordSent(__instance);
                return false;
            }
            bool written = TryWriteObject(__instance, pkg);
            if (deltaContext) ZDODeltaPatches.RecordSent(__instance);
            return !written;
        }

        [HarmonyPatch(typeof(ZPackage), nameof(ZPackage.Write), typeof(ZPackage))]
        [HarmonyPrefix]
        public static bool ZPackage_WritePackage_Prefix(ZPackage __instance, ZPackage pkg)
        {
            if (!s_nestedReady || pkg == null || pkg == __instance || !Enabled) return true;
            WriteNestedWithoutCopy(__instance, pkg);
            return false;
        }

        [HarmonyPatch(typeof(ZPackage), nameof(ZPackage.ReadPackage), new[] { typeof(ZPackage) }, new[] { ArgumentType.Ref })]
        [HarmonyPrefix]
        public static bool ZPackage_ReadPackage_Prefix(ZPackage __instance, ref ZPackage pkg)
        {
            if (!s_nestedReady || pkg == null || pkg == __instance || !Enabled) return true;
            ReadNestedWithoutCopy(__instance, pkg);
            return false;
        }

        // Vanilla reads the nested package into a fresh byte array and copies that into the target; a server does it once
        // per object received. This reads the bytes straight into the target's own buffer. Same contents, no array.
        private static void ReadNestedWithoutCopy(ZPackage source, ZPackage nested)
        {
            BinaryReader reader = s_reader(source);
            int length = reader.ReadInt32();
            s_writer(nested).Flush();
            MemoryStream target = s_stream(nested);
            target.SetLength(length);
            int read = length == 0 ? 0 : reader.Read(target.GetBuffer(), 0, length);
            if (read != length) target.SetLength(read);
            target.Position = 0L;
        }

        private static bool TryWriteObject(ZDO zdo, ZPackage pkg)
        {
            if (!s_objectsReady || !s_foreignScanDone || !Enabled) return false;
            if (s_matchedVanilla < ChecksAgainstVanilla && !MatchesVanilla(zdo)) return false;
            WriteObject(zdo, pkg);
            return true;
        }

        // Vanilla ZDO.Serialize field for field: the same flags, in the same order, through the same ZPackage writes.
        private static void WriteObject(ZDO zdo, ZPackage pkg)
        {
            ZDOID uid = zdo.m_uid;
            int floats = Bucket<float>.Count(uid, out var floatBucket);
            int vec3s = Bucket<Vector3>.Count(uid, out var vec3Bucket);
            int quats = Bucket<Quaternion>.Count(uid, out var quatBucket);
            int ints = Bucket<int>.Count(uid, out var intBucket);
            int longs = Bucket<long>.Count(uid, out var longBucket);
            int strings = Bucket<string>.Count(uid, out var stringBucket);
            int byteArrays = Bucket<byte[]>.Count(uid, out var byteArrayBucket);
            ZDOConnection connection = ZDOExtraData.GetConnection(uid);
            bool hasConnection = connection != null && connection.m_type != ZDOExtraData.ConnectionType.None;
            Vector3 rotation = s_rotation(zdo);
            bool hasRotation = rotation != Quaternion.identity.eulerAngles;

            int flags = 0;
            if (hasConnection)  flags |= FlagConnections;
            if (floats > 0)     flags |= FlagFloats;
            if (vec3s > 0)      flags |= FlagVec3s;
            if (quats > 0)      flags |= FlagQuaternions;
            if (ints > 0)       flags |= FlagInts;
            if (longs > 0)      flags |= FlagLongs;
            if (strings > 0)    flags |= FlagStrings;
            if (byteArrays > 0) flags |= FlagByteArrays;
            if (zdo.Persistent) flags |= FlagPersistent;
            if (zdo.Distant)    flags |= FlagDistant;
            flags |= (int)zdo.Type << TypeShift;
            if (hasRotation)    flags |= FlagRotation;

            pkg.Write((ushort)flags);
            pkg.Write(zdo.GetPrefab());
            if (hasRotation) pkg.Write(rotation);
            if ((flags & FlagAnyData) == 0) return;

            if (hasConnection)
            {
                pkg.Write((byte)connection.m_type);
                pkg.Write(connection.m_target);
            }
            Bucket<float>.Write(pkg, floatBucket, floats);
            Bucket<Vector3>.Write(pkg, vec3Bucket, vec3s);
            Bucket<Quaternion>.Write(pkg, quatBucket, quats);
            Bucket<int>.Write(pkg, intBucket, ints);
            Bucket<long>.Write(pkg, longBucket, longs);
            Bucket<string>.Write(pkg, stringBucket, strings);
            Bucket<byte[]>.Write(pkg, byteArrayBucket, byteArrays);
        }

        private static bool MatchesVanilla(ZDO zdo)
        {
            var ours = new ZPackage();
            WriteObject(zdo, ours);
            var vanilla = new ZPackage();
            t_writingVanillaReference = true;
            try
            {
                zdo.Serialize(vanilla);
            }
            finally
            {
                t_writingVanillaReference = false;
            }

            byte[] ourBytes = ours.GetArray();
            byte[] vanillaBytes = vanilla.GetArray();
            int difference = FirstDifference(ourBytes, vanillaBytes);
            if (difference < 0)
            {
                if (++s_matchedVanilla == ChecksAgainstVanilla)
                    LoggerOptions.LogMessage($"[ZdoWrite] the first {ChecksAgainstVanilla} objects sent matched vanilla byte for byte; "
                                             + "FGN writes ZDOs without vanilla's per-object lists and closures from here on.");
                return true;
            }

            s_objectsReady = false;
            LoggerOptions.LogWarning($"[ZdoWrite] {PrefabName(zdo)} ({zdo.m_uid}) came out different from vanilla at byte {difference} "
                                     + $"(FGN {ourBytes.Length} bytes, vanilla {vanillaBytes.Length}); vanilla writes ZDOs for the rest of "
                                     + "this session. Please report this line.");
            return false;
        }

        // Vanilla copies the nested package into a new array and writes that; this writes the same length and bytes
        // straight from its buffer. A package written into itself keeps vanilla's path, which copies before the buffer grows.
        private static void WriteNestedWithoutCopy(ZPackage target, ZPackage nested)
        {
            s_writer(nested).Flush();
            MemoryStream source = s_stream(nested);
            int length = (int)source.Length;
            BinaryWriter writer = s_writer(target);
            writer.Write(length);
            writer.Write(source.GetBuffer(), 0, length);
        }

        // Runs from Initialize, before the patches are applied, so the Write and ReadPackage calls below are vanilla's.
        private static bool NestedWriteMatchesVanilla()
        {
            var nested = new ZPackage();
            nested.Write(NestedSelfTestNumber);
            nested.Write(NestedSelfTestText);
            var vanilla = new ZPackage();
            vanilla.Write(nested);
            var ours = new ZPackage();
            WriteNestedWithoutCopy(ours, nested);
            if (FirstDifference(ours.GetArray(), vanilla.GetArray()) >= 0) return false;

            vanilla.SetPos(0);
            ours.SetPos(0);
            var vanillaRead = new ZPackage();
            vanilla.ReadPackage(ref vanillaRead);
            var ourRead = new ZPackage();
            ReadNestedWithoutCopy(ours, ourRead);
            return FirstDifference(ourRead.GetArray(), vanillaRead.GetArray()) < 0
                   && ours.GetPos() == vanilla.GetPos()
                   && ourRead.GetPos() == vanillaRead.GetPos();
        }

        private static int FirstDifference(byte[] left, byte[] right)
        {
            int shared = Math.Min(left.Length, right.Length);
            for (int i = 0; i < shared; i++)
                if (left[i] != right[i]) return i;
            return left.Length == right.Length ? -1 : shared;
        }

        private static string PrefabName(ZDO zdo)
        {
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            return prefab != null ? prefab.name : zdo.GetPrefab().ToString();
        }
    }
}
