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
        private static readonly Dictionary<Ship, CollisionDetectionMode> _parkedMode = new Dictionary<Ship, CollisionDetectionMode>();   // 1.6.31

        private static int _nextLogFrame;
        private const int LogIntervalFrames = 1800;

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
            if (!___m_nview.IsOwner())
            {
                if (_ready.Count > 0) _ready.Remove(__instance);   // re-checked in full when it comes back
                return true;
            }

            // The full check is costly per physics step and hull, so a hull found ready stays ready while it
            // stays in that zone, so the full check (zone loaded + water + ground) runs again only on a zone change
            // or every ReadyRecheckSteps steps.
            Vector2s zone = FgnRing.Sector(__instance.transform.position);
            ReportChecks();
            if (_ready.TryGetValue(__instance, out ReadyCache cache) && cache.Zone.x == zone.x && cache.Zone.y == zone.y
                && cache.StepsLeft > 0)
            {
                cache.StepsLeft--;
                _ready[__instance] = cache;
                _skippedChecks++;
                return true;
            }

            _fullChecks++;
            if (IsEnvironmentReady(__instance))
            {
                Unpark(__instance, ___m_body);
                _ready[__instance] = new ReadyCache { Zone = zone, StepsLeft = ReadyRecheckSteps };
                return true;
            }

            _ready.Remove(__instance);
            Park(__instance, ___m_body);
            return false;
        }

        private const int ReadyRecheckSteps = 50;   // ~1 s of physics steps
        private const float CheckReportSeconds = 60f;
        private struct ReadyCache { public Vector2s Zone; public int StepsLeft; }
        private static readonly Dictionary<Ship, ReadyCache> _ready = new Dictionary<Ship, ReadyCache>();
        private static readonly List<Ship> _deadShips = new List<Ship>();
        private static int _fullChecks, _skippedChecks;
        private static float _nextCheckReport = -1f;

        private static void ReportChecks()
        {
            float now = Time.realtimeSinceStartup;
            if (_nextCheckReport < 0f) { _nextCheckReport = now + CheckReportSeconds; return; }
            if (now < _nextCheckReport) return;
            _nextCheckReport = now + CheckReportSeconds;

            _deadShips.Clear();
            foreach (var ship in _ready.Keys) if (ship == null) _deadShips.Add(ship);
            foreach (var ship in _deadShips) _ready.Remove(ship);

            if (LoggerOptions.DebugEnabled && (_fullChecks > 0 || _skippedChecks > 0)) LoggerOptions.LogDebug(
                $"[ShipSim] environment checks, last {CheckReportSeconds:F0} s: {_fullChecks} full, {_skippedChecks} skipped "
                + $"(hull already ready in the same zone, full re-check every {ReadyRecheckSteps} steps); {_ready.Count} hull(s) ready.");
            _fullChecks = _skippedChecks = 0;
        }

        // Clearing on world teardown: the set is static and would otherwise hold dead Ship references
        // from a previous ZNetScene across a disconnect/reconnect.
        public static void ZNetScene_Shutdown_ClearParked()
        {
            _parkedByUs.Clear();
            _parkedMode.Clear();
            _ready.Clear();
            _fullChecks = _skippedChecks = 0;
            _nextCheckReport = -1f;
            SssTestAreas.Clear();
            CompanionKeepAlive.Clear();
        }

        private static bool IsEnvironmentReady(Ship ship)
        {
            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem == null) return false;

            Vector3 position = ship.transform.position;

            // IsZoneLoaded is the stricter of the two vanilla checks: the zone exists AND has no
            // objects still streaming into it, which is exactly the window we must not simulate in.
            if (!zoneSystem.IsZoneLoaded(position)) return false;

            WaterVolume probe = null;
            return Floating.GetWaterLevel(position, ref probe) > NoWaterSentinelThreshold && VehicleOwnershipEdge.GroundReady(position);
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
            {
                _parkedMode[ship] = body.collisionDetectionMode;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            }

            body.isKinematic = true;
            _parkedByUs[ship] = Time.time;

            if (Time.frameCount >= _nextLogFrame)
            {
                _nextLogFrame = Time.frameCount + LogIntervalFrames;
                LoggerOptions.LogDebug(
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
            // 1.6.31: Park's switch to speculative was never undone, so a parked hull kept the weaker collision mode for good
            bool restored = _parkedMode.TryGetValue(ship, out CollisionDetectionMode mode);
            if (restored) { body.collisionDetectionMode = mode; _parkedMode.Remove(ship); }

            // Always reported, unlike the park side: the resume is the half that proves the hull
            // was handed back to normal physics rather than left frozen, and it carries the
            // duration that tells us whether zone readiness is behaving or flapping.
            LoggerOptions.LogDebug(
                $"[ShipSim] RESUMED '{ship.name}' after {(Time.time - parkedAt):F1}s parked — zone and water resolved, "
                + "physics handed back" + (restored ? $", collision mode back to {mode}" : "") + ". (Short parks during zone loads are the fix working; long or repeated "
                + "parks while under way would mean zone readiness is flapping.)");
        }
    }
}
