using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Server-driven ship physics never runs in an unloaded environment. Vanilla's water lookup returns a
    /// sentinel until the zone's WaterVolume exists, which skips buoyancy but leaves gravity running, so a
    /// server-owned hull free-falls and damages itself on the seabed. Hulls are parked (still and kinematic)
    /// until water resolves, then released; only bodies this patch changed are ever restored. Dedicated
    /// server and server-owned ships only.
    /// </summary>
    [HarmonyPatch]
    public static class ServerShipSimulationPatches
    {
        // GetWaterLevel seeds its accumulator at -10000 and returns it untouched when nothing is in
        // range. Anything near that is "no water data", not a real surface — real ocean sits at
        // ZoneSystem.m_waterLevel (30 by default), and even carved-out puddles are far above this.
        private const float NoWaterSentinelThreshold = -9000f;

        // Only bodies we made kinematic ourselves; guarantees we never un-kinematic something that
        // was authored that way or parked by another system. Value is the Time.time at which the
        // hull was parked, so the resume log can report how long the environment took to load —
        // a few short parks during zone loads is the fix working; long or repeated parks while
        // sailing would mean zone readiness is flapping and needs a hysteresis pass.
        private static readonly Dictionary<Ship, float> _parkedByUs = new Dictionary<Ship, float>();

        private static int _nextLogFrame;
        private const int LogIntervalFrames = 1800;

        [HarmonyPatch(typeof(Ship), nameof(Ship.CustomFixedUpdate))]
        [HarmonyPrefix]
        public static bool Ship_CustomFixedUpdate_ParkUntilEnvironmentReady(
            Ship __instance,
            ZNetView ___m_nview,
            Rigidbody ___m_body)
        {
            if (!ServerClientUtils.ZNetIsDedicated()) return true;
            if (__instance == null || ___m_body == null) return true;
            if (___m_nview == null || !___m_nview.IsValid()) return true;

            // Not ours to simulate: vanilla already early-outs for non-owners and ZSyncTransform
            // drives the hull from whoever does own it.
            if (!___m_nview.IsOwner()) return true;

            if (IsEnvironmentReady(__instance))
            {
                Unpark(__instance, ___m_body);
                return true;
            }

            Park(__instance, ___m_body);
            return false;
        }

        // Clearing on world teardown: the set is static and would otherwise hold dead Ship references
        // from a previous ZNetScene across a disconnect/reconnect.
        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        [HarmonyPostfix]
        public static void ZNetScene_Shutdown_ClearParked() => _parkedByUs.Clear();


        private static bool IsEnvironmentReady(Ship ship)
        {
            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem == null) return false;

            Vector3 position = ship.transform.position;

            // IsZoneLoaded is the stricter of the two vanilla checks: the zone exists AND has no
            // objects still streaming into it, which is exactly the window we must not simulate in.
            if (!zoneSystem.IsZoneLoaded(position)) return false;

            WaterVolume probe = null;
            return Floating.GetWaterLevel(position, ref probe) > NoWaterSentinelThreshold;
        }

        private static void Park(Ship ship, Rigidbody body)
        {
            if (body.isKinematic) return;   // already still; nothing to do and nothing to restore

            // Zero BEFORE going kinematic: assigning a velocity to a kinematic body logs
            // "Setting velocity of a kinematic body is not supported" every tick.
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;

            // Kinematic bodies only support Speculative continuous detection; switching first keeps
            // Unity from warning on every park.
            if (body.collisionDetectionMode == CollisionDetectionMode.Continuous
                || body.collisionDetectionMode == CollisionDetectionMode.ContinuousDynamic)
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            body.isKinematic = true;
            _parkedByUs[ship] = Time.time;

            if (Time.frameCount >= _nextLogFrame)
            {
                _nextLogFrame = Time.frameCount + LogIntervalFrames;
                LoggerOptions.LogMessage(
                    $"[ShipSim] PARKED '{ship.name}' at {ship.transform.position} — its zone/water is not resolvable yet, "
                    + "so its physics is held rather than left to free-fall into the seabed. It resumes when the area finishes loading.");
            }
        }

        private static void Unpark(Ship ship, Rigidbody body)
        {
            float parkedAt;
            if (!_parkedByUs.TryGetValue(ship, out parkedAt)) return;
            _parkedByUs.Remove(ship);
            body.isKinematic = false;

            // Always reported, unlike the park side: the resume is the half that proves the hull
            // was handed back to normal physics rather than left frozen, and it carries the
            // duration that tells us whether zone readiness is behaving or flapping.
            LoggerOptions.LogMessage(
                $"[ShipSim] RESUMED '{ship.name}' after {(Time.time - parkedAt):F1}s parked — zone and water resolved, "
                + "physics handed back. (Short parks during zone loads are the fix working; long or repeated "
                + "parks while under way would mean zone readiness is flapping.)");
        }
    }
}
