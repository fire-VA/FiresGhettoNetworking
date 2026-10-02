using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.5.24 ([worldgen] 2026-10-01): a VoxelWrap dedicated server is data-only (no voxel colliders), so ZoneSystem.GetGroundHeight
    /// and a physics ray both read the UNCARVED ground there, and GroundSnapPatches snapped a server-owned drop, floater or tombstone
    /// in a carved pond or pit up to it. FiresAdminTerrain's VoxelWorld.TrySampleSurfaceY reads the EDITED density (a lookup and a
    /// column scan, no physics, never builds a zone). Looked up softly by name: FGN does not reference FiresAdminTerrain, and without
    /// it (or where no column is loaded) this answers nothing and the old path stands.
    /// </summary>
    internal static class VoxelSurfaceProbe
    {
        private const string TypeName = "VerdantsAscent.Terrain.Voxel.VoxelWorld";
        private const string MethodName = "TrySampleSurfaceY";
        private const float AboveObjectMeters = 3f;     // [worldgen]: a few m above the object, so a roof or overhang is not taken
        private const float WindowMeters = 60f;

        private delegate bool SampleSurface(float wx, float wz, float topY, float botY, out float surfY, out bool solidAtTop);

        private static SampleSurface s_sample;
        private static bool s_resolved;

        /// <summary>The edited voxel ground under (or around) point, or false to fall back.</summary>
        public static bool TryGetSupport(Vector3 point, out float surfaceY)
        {
            surfaceY = 0f;
            if (!Resolve()) return false;
            float top = point.y + AboveObjectMeters;
            try
            {
                if (s_sample(point.x, point.z, top, top - WindowMeters, out float y, out bool _))
                {
                    surfaceY = y;
                    return true;
                }
            }
            catch (Exception ex)
            {
                s_sample = null;
                LoggerOptions.LogWarning($"[GroundSnap] FiresAdminTerrain's voxel surface sampler threw and is off for this session: {ex.Message}");
            }
            return false;
        }

        private static bool Resolve()
        {
            if (s_resolved) return s_sample != null;
            s_resolved = true;
            try
            {
                Type world = AccessTools.TypeByName(TypeName);
                MethodInfo method = world == null ? null : world.GetMethod(MethodName, BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(float), typeof(float), typeof(float), typeof(float), typeof(float).MakeByRefType(), typeof(bool).MakeByRefType() }, null);
                if (method == null || method.ReturnType != typeof(bool)) return false;
                s_sample = (SampleSurface)Delegate.CreateDelegate(typeof(SampleSurface), method);
                LoggerOptions.LogInfo("[GroundSnap] dedicated server: drops, floaters and tombstones below the heightmap rest on FiresAdminTerrain's edited voxel ground.");
            }
            catch (Exception ex)
            {
                s_sample = null;
                LoggerOptions.LogWarning($"[GroundSnap] FiresAdminTerrain's voxel surface sampler could not be bound: {ex.Message}");
            }
            return s_sample != null;
        }
    }
}
