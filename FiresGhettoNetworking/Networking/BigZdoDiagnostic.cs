using System;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    [HarmonyPatch]
    public static class BigZdoDiagnostic
    {
        private const int VanillaWarningThreshold = 100;
        private const int WireFormatByteCountCeiling = 255;

        // True only while ZDOMan is iterating the full ZDO set to disk.
        // During a full-world save every ZDO.Save fires this prefix, millions of calls
        // on a large world; the extra cost can push the save past the Steam peer
        // timeout and drop every peer. A per-ZDO warning is not actionable mid-save,
        // so it is skipped then; the Serialize path still catches oversized buckets
        // during normal play.
        private static bool s_bulkSaveInProgress;

        // SaveChunk is the method that loops zdo.Save() over the world. Bracketing it is
        // self-balancing: the flag cannot get stuck on if a save is abandoned midway, which a
        // PrepareSave/SaveCleanup pair could.
        [HarmonyPatch(typeof(ZDOMan), "SaveChunk")]
        [HarmonyPrefix]
        public static void ZDOMan_SaveChunk_Prefix() => s_bulkSaveInProgress = true;

        [HarmonyPatch(typeof(ZDOMan), "SaveChunk")]
        [HarmonyFinalizer]
        public static void ZDOMan_SaveChunk_Finalizer() => s_bulkSaveInProgress = false;

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Save))]
        [HarmonyPrefix]
        public static void ZDO_Save_Prefix(ZDO __instance)
        {
            if (s_bulkSaveInProgress) return;
            if (__instance == null || !LargeZdoDiagnosticIsEnabled()) return;
            TryReportOversizedBuckets(__instance, "Save", canTruncateOnWire: false, SnapshotSaveBuckets);
        }

        // Called from ZdoWireWriter's ZDO.Serialize hook, FGN's only patch on that method.
        internal static void ReportLiveBuckets(ZDO zdo)
        {
            if (zdo == null || !LargeZdoDiagnosticIsEnabled()) return;
            TryReportOversizedBuckets(zdo, "Serialize", canTruncateOnWire: true, SnapshotLiveBuckets);
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

        private static BucketSnapshot SnapshotSaveBuckets(ZDOID uid)
        {
            ZDOExtraData.GetSaveData(uid,
                out var floats, out var vec3s, out var quats, out var ints,
                out var longs, out var strings, out var byteArrays, out _);
            return Count(floats, vec3s, quats, ints, longs, strings, byteArrays);
        }

        private static BucketSnapshot SnapshotLiveBuckets(ZDOID uid)
        {
            ZDOExtraData.GetData(uid,
                out var floats, out var vec3s, out var quats, out var ints,
                out var longs, out var strings, out var byteArrays, out _);
            return Count(floats, vec3s, quats, ints, longs, strings, byteArrays);
        }

        private static BucketSnapshot Count<TF, TV, TQ, TI, TL, TS, TB>(
            System.Collections.Generic.List<TF> floats,
            System.Collections.Generic.List<TV> vec3s,
            System.Collections.Generic.List<TQ> quats,
            System.Collections.Generic.List<TI> ints,
            System.Collections.Generic.List<TL> longs,
            System.Collections.Generic.List<TS> strings,
            System.Collections.Generic.List<TB> byteArrays) => new BucketSnapshot
            {
                Floats      = floats?.Count     ?? 0,
                Vector3s    = vec3s?.Count      ?? 0,
                Quaternions = quats?.Count      ?? 0,
                Ints        = ints?.Count       ?? 0,
                Longs       = longs?.Count      ?? 0,
                Strings     = strings?.Count    ?? 0,
                ByteArrays  = byteArrays?.Count ?? 0,
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
}
