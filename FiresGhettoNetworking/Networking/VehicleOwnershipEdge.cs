using System.Collections.Generic;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// When a player's game takes over a ship or cart it starts from where the world says the vehicle is, moving as it was, and
    /// never inside terrain: the body is held still until the heightmap and terrain edits under it exist, then lifted out if buried.
    /// </summary>
    internal static class VehicleOwnershipEdge
    {
        private const double MaxHoldSeconds = 10.0;
        private const float GroundCheckRadius = 8f;
        private const float BuriedToleranceMeters = 0.5f;
        private const float LiftClearanceMeters = 0.2f;
        private const float ReportIntervalSec = 30f;
        private static readonly int TerrainCompilerPrefab = "_TerrainCompiler".GetStableHashCode();

        private struct Held
        {
            public Rigidbody Body;
            public bool IsShip;
            public double Since;
            public Vector3 Velocity;
            public Vector3 AngularVelocity;
        }

        private static readonly Dictionary<ZSyncTransform, Held> s_held = new Dictionary<ZSyncTransform, Held>();
        private static float s_nextReportTime;

        internal static bool HoldsAny => s_held.Count > 0;

        internal static void Clear() => s_held.Clear();

        internal static void OnOwnershipGained(ZSyncTransform sync, ZDO zdo, Rigidbody body)
        {
            if (body == null || ZNet.instance == null || ZNet.instance.IsDedicated()) return;
            bool isShip = sync.GetComponent<Ship>() != null;
            if (!isShip && sync.GetComponent<Vagon>() == null) return;

            if (sync.m_syncPosition) sync.transform.position = zdo.GetPosition();
            if (sync.m_syncRotation) sync.transform.rotation = zdo.GetRotation();
            Vector3 velocity = zdo.GetVec3(ZDOVars.s_bodyVelHash, zdo.GetVec3(ZDOVars.s_velHash, Vector3.zero));
            Vector3 angularVelocity = zdo.GetVec3(ZDOVars.s_bodyAVelHash, Vector3.zero);

            if (!body.isKinematic && !GroundReady(sync.transform.position))
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = true;
                s_held[sync] = new Held
                {
                    Body = body, IsShip = isShip, Since = Time.realtimeSinceStartupAsDouble,
                    Velocity = velocity, AngularVelocity = angularVelocity,
                };
                Physics.SyncTransforms();
                return;
            }
            LiftOutOfGround(sync.transform, isShip);
            if (!body.isKinematic)
            {
                body.linearVelocity = velocity;
                body.angularVelocity = angularVelocity;
            }
            Physics.SyncTransforms();
        }

        /// <summary>True while the vehicle is still held; releases it once the ground under it is ready, the hold runs out, or
        /// another game takes it over.</summary>
        internal static bool KeepHolding(ZSyncTransform sync, bool stillOwner)
        {
            if (!s_held.TryGetValue(sync, out var held)) return false;
            if (held.Body == null)
            {
                s_held.Remove(sync);
                return false;
            }
            if (!stillOwner)
            {
                s_held.Remove(sync);
                held.Body.isKinematic = false;
                return false;
            }
            bool expired = Time.realtimeSinceStartupAsDouble - held.Since > MaxHoldSeconds;
            if (!expired && !GroundReady(sync.transform.position)) return true;

            s_held.Remove(sync);
            LiftOutOfGround(sync.transform, held.IsShip);
            held.Body.isKinematic = false;
            held.Body.linearVelocity = held.Velocity;
            held.Body.angularVelocity = held.AngularVelocity;
            Physics.SyncTransforms();
            if (expired) Report($"[Vehicles] '{sync.name}' was released after {MaxHoldSeconds:F0} s although the ground under it had not "
                + "finished loading.");
            return false;
        }

        /// <summary>The heightmap under the point exists, has no rebuild waiting, and any terrain edits in its zone are applied.</summary>
        internal static bool GroundReady(Vector3 point)
        {
            if (Heightmap.FindHeightmap(point) == null) return false;
            if (Heightmap.HaveQueuedRebuild(point, GroundCheckRadius)) return false;
            return TerrainComp.FindTerrainCompiler(point) != null || !ZoneHasTerrainEdits(point);
        }

        private static bool ZoneHasTerrainEdits(Vector3 point)
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null) return false;
            Vector2s zone = ZoneSystem.GetZone(point);
            var objects = zdoMan.m_objectsBySector[(int)ZoneSystem.SectorToIndex(zone).Sector];
            if (objects == null) return false;
            for (int i = 0; i < objects.Count; i++)
                if (objects[i].GetPrefab() == TerrainCompilerPrefab) return true;
            return false;
        }

        private static void LiftOutOfGround(Transform vehicle, bool isShip)
        {
            if (ZoneSystem.instance == null) return;
            Vector3 position = vehicle.position;
            float support = GroundSnapPatches.SupportOrGroundHeight(ZoneSystem.instance, position);
            if (position.y >= support - BuriedToleranceMeters) return;
            if (isShip)
            {
                WaterVolume water = null;
                if (Floating.GetWaterLevel(position, ref water) > support) return;
            }
            vehicle.position = new Vector3(position.x, support + LiftClearanceMeters, position.z);
            Report($"[Vehicles] '{vehicle.name}' was {support - position.y:F1} m inside the ground when this game took it over; "
                + "lifted onto it before its first physics step.");
        }

        private static void Report(string line)
        {
            if (Time.realtimeSinceStartup < s_nextReportTime) return;
            s_nextReportTime = Time.realtimeSinceStartup + ReportIntervalSec;
            LoggerOptions.LogMessage(line);
        }
    }
}
