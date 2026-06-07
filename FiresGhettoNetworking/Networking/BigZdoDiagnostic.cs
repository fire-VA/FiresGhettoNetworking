using System;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
#if !PUBLIC_TEST
    [HarmonyPatch]
    public static class BigZdoDiagnostic
    {
        private const int VanillaWarningThreshold = 100;
        private const int WireFormatByteCountCeiling = 255;

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Save))]
        [HarmonyPrefix]
        public static void ZDO_Save_Prefix(ZDO __instance)
        {
            if (__instance == null || !LargeZdoDiagnosticIsEnabled()) return;
            TryReportOversizedBuckets(__instance, "Save", canTruncateOnWire: false, SnapshotSaveBuckets);
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Serialize))]
        [HarmonyPrefix]
        public static void ZDO_Serialize_Prefix(ZDO __instance)
        {
            if (__instance == null || !LargeZdoDiagnosticIsEnabled()) return;
            TryReportOversizedBuckets(__instance, "Serialize", canTruncateOnWire: true, SnapshotLiveBuckets);
        }

        private static bool LargeZdoDiagnosticIsEnabled()
            => FiresGhettoNetworkMod.ConfigEnableLargeZdoDiagnostics?.Value ?? false;

        private static void TryReportOversizedBuckets(
            ZDO zdo,
            string sourceLabel,
            bool canTruncateOnWire,
            Func<ZDOID, BucketSnapshot> snapshotter)
        {
            try
            {
                BucketSnapshot buckets = snapshotter(zdo.m_uid);
                if (!buckets.AnyExceeds(VanillaWarningThreshold)) return;

                string context = FormatZdoContext(zdo, sourceLabel);
                foreach (var bucket in buckets.AsTuples())
                    LogIfOverThreshold(context, bucket.name, bucket.count, canTruncateOnWire);
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[BigZdoDiag] {sourceLabel} prefix threw: {ex.Message}");
            }
        }

        private static BucketSnapshot SnapshotSaveBuckets(ZDOID uid) => new BucketSnapshot
        {
            Floats      = ZDOExtraData.GetSaveFloats(uid).Count,
            Vector3s    = ZDOExtraData.GetSaveVec3s(uid).Count,
            Quaternions = ZDOExtraData.GetSaveQuaternions(uid).Count,
            Ints        = ZDOExtraData.GetSaveInts(uid).Count,
            Longs       = ZDOExtraData.GetSaveLongs(uid).Count,
            Strings     = ZDOExtraData.GetSaveStrings(uid).Count,
            ByteArrays  = ZDOExtraData.GetSaveByteArrays(uid).Count,
        };

        private static BucketSnapshot SnapshotLiveBuckets(ZDOID uid) => new BucketSnapshot
        {
            Floats      = ZDOExtraData.GetFloats(uid).Count,
            Vector3s    = ZDOExtraData.GetVec3s(uid).Count,
            Quaternions = ZDOExtraData.GetQuaternions(uid).Count,
            Ints        = ZDOExtraData.GetInts(uid).Count,
            Longs       = ZDOExtraData.GetLongs(uid).Count,
            Strings     = ZDOExtraData.GetStrings(uid).Count,
            ByteArrays  = ZDOExtraData.GetByteArrays(uid).Count,
        };

        private static string FormatZdoContext(ZDO zdo, string sourceLabel)
        {
            Vector3 pos = zdo.GetPosition();
            string prefabName = ResolvePrefabNameFromHash(zdo.GetPrefab());
            return $"src={sourceLabel} uid={zdo.m_uid} prefab='{prefabName}'({zdo.GetPrefab()}) " +
                   $"pos=({pos.x:F1},{pos.z:F1},{pos.y:F1}) owner={zdo.GetOwner()}";
        }

        private static void LogIfOverThreshold(string context, string bucketName, int count, bool canTruncateOnWire)
        {
            if (count <= VanillaWarningThreshold) return;

            if (canTruncateOnWire && count > WireFormatByteCountCeiling)
            {
                LoggerOptions.LogWarning(
                    $"[BigZdoDiag TRUNCATING] {context} bucket={bucketName,-10} count={count} " +
                    $"(>{WireFormatByteCountCeiling} — receiver will read wrapped count, payload corrupt)");
                return;
            }
            LoggerOptions.LogWarning($"[BigZdoDiag] {context} bucket={bucketName,-10} count={count}");
        }

        private static string ResolvePrefabNameFromHash(int prefabHash)
        {
            try
            {
                var prefab = ZNetScene.instance?.GetPrefab(prefabHash);
                if (prefab != null) return prefab.name;
            }
            catch { }
            return "<unresolved>";
        }

        private struct BucketSnapshot
        {
            public int Floats, Vector3s, Quaternions, Ints, Longs, Strings, ByteArrays;

            public bool AnyExceeds(int threshold) =>
                Floats > threshold || Vector3s > threshold || Quaternions > threshold ||
                Ints > threshold || Longs > threshold || Strings > threshold || ByteArrays > threshold;

            public (string name, int count)[] AsTuples() => new[]
            {
                ("float",      Floats),
                ("Vector3",    Vector3s),
                ("Quaternion", Quaternions),
                ("int",        Ints),
                ("long",       Longs),
                ("string",     Strings),
                ("byte[]",     ByteArrays),
            };
        }
    }
#endif
}
