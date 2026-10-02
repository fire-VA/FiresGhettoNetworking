using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Remote Arrows (Tools\PVP_TEST.md, "Why the remote arrow copies are drawn 3-6 m off"): a projectile another player shot is flown
    /// here along its real flight instead of drawn from its synced positions, which trail the shooter's own by the link plus vanilla's
    /// drawing lag (R43: 130-155 ms, 4.5-5.5 m at 35 m/s). The shooter's game stamps each projectile it launches with its start,
    /// velocity and physics time; here it is flown from there with vanilla's own step up to the shooter's present (Remote Motion's clock
    /// for that player), stopped at the first solid or character it meets, and handed back to vanilla's syncing when it stops, goes
    /// off course (a bounce) or the shooter's clock isn't known. Hits are still decided by the shooter's game.
    /// </summary>
    [HarmonyPatch]
    public static class RemoteArrows
    {
        public static ConfigEntry<bool> ConfigEnabled;

        private static readonly int StartHash = "fgn_pa_p".GetStableHashCode();
        private static readonly int VelocityHash = "fgn_pa_v".GetStableHashCode();
        private static readonly int TimeHash = "fgn_pa_t".GetStableHashCode();
        private const double MsPerSecond = 1000.0;
        private const double MaxFlightSeconds = 10.0;
        private const float OffCourseMeters = 3f;
        private const float OffCourseSeconds = 0.5f;
        private const double LeaveBowSeconds = 0.1;
        private const float HeldAtHitSeconds = 0.5f;
        private const int MaxStepsPerFixedUpdate = 50;
        private const int CleanupAbove = 64;

        private sealed class Flight
        {
            public Vector3 Position, Velocity;
            public double Time, Launched;
            public bool Stopped;
            public bool Waited;
            public float StoppedAt;
        }

        // R49: the copies still measured 1.4-8.4 m off and nothing said whether they were flown here at all. What happened to each
        // remote projectile, summed and logged once a minute while any came by.
        private const float ReportEverySeconds = 60f;
        private static float s_nextReport;
        private static int s_flown, s_noStamp, s_noClock, s_stoppedCharacter, s_stoppedSolid, s_offCourse, s_clockLost, s_timedOut;
        private static int s_waited;
        private static double s_worstWait;
        private static float s_worstOffCourse;
        private static double s_leadSum;
        private static int s_leadCount;
        private static int s_characterLayers;

        private static readonly Dictionary<Projectile, Flight> s_flights = new Dictionary<Projectile, Flight>();
        private static readonly HashSet<ZNetView> s_flying = new HashSet<ZNetView>();
        private static readonly HashSet<Projectile> s_vanilla = new HashSet<Projectile>();
        private static int s_solidMask, s_hitMask;

        private static int SolidMask => s_solidMask != 0 ? s_solidMask
            : (s_solidMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle"));

        private static int HitMask => s_hitMask != 0 ? s_hitMask
            : (s_hitMask = SolidMask | LayerMask.GetMask("character", "character_net", "character_ghost", "hitbox", "character_noenv"));

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("03 - Player Sync", "Remote Arrows", true,
                "Draws other players' arrows where they really are in flight, not a few metres behind. Needs Remote Motion on and\n" +
                "the shooter running FGN with this on (it also marks your own shots). Visual only; hits are decided by the shooter.");
        }

        private static bool Drawing => ConfigEnabled != null && ConfigEnabled.Value;

        /// <summary>Whether this object is a projectile flown here, so vanilla's ClientSync stays off it (OwnershipHandoffPatches).</summary>
        internal static bool Handles(ZNetView view) => s_flying.Count > 0 && view != null && s_flying.Contains(view);

        // ------------------------------------------------------------ shooter

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup)), HarmonyPostfix]
        static void StampLaunch(Projectile __instance, Vector3 velocity, ZNetView ___m_nview)
        {
            // Off means off on this side too: no three extra keys on every projectile. Receivers draw an unstamped one as vanilla.
            if (!Drawing) return;
            if (___m_nview == null || !___m_nview.IsValid() || !___m_nview.IsOwner()) return;
            ZDO zdo = ___m_nview.GetZDO();
            zdo.Set(StartHash, __instance.transform.position);
            zdo.Set(VelocityHash, velocity);
            zdo.Set(TimeHash, (long)(Time.fixedTimeAsDouble * MsPerSecond));
        }

        // ------------------------------------------------------------ everyone else

        [HarmonyPatch(typeof(Projectile), "FixedUpdate"), HarmonyPrefix]
        static void FlyRemote(Projectile __instance, ZNetView ___m_nview)
        {
            if (___m_nview == null || !___m_nview.IsValid()) return;
            if (___m_nview.IsOwner()) { if (s_flights.Count > 0) Release(__instance, ___m_nview); return; }
            if (!Drawing) { if (s_flights.Count > 0) Release(__instance, ___m_nview); return; }
            Report();
            Flight flight = Begin(__instance, ___m_nview);
            if (flight == null) return;
            ZDO zdo = ___m_nview.GetZDO();
            if (!RemoteMotion.TrySenderNow(zdo.GetOwner(), out double senderNow))
            {
                s_clockLost++;
                Release(__instance, ___m_nview);
                return;
            }
            if (senderNow < flight.Launched && !flight.Waited)
            {
                flight.Waited = true;
                s_waited++;
                s_worstWait = System.Math.Max(s_worstWait, flight.Launched - senderNow);
            }
            if (senderNow - flight.Launched > MaxFlightSeconds)
            {
                s_timedOut++;
                Release(__instance, ___m_nview);
                return;
            }
            Transform transform = __instance.transform;
            if (flight.Stopped)
            {
                if (Time.time - flight.StoppedAt > HeldAtHitSeconds) Release(__instance, ___m_nview);
                else transform.position = flight.Position;
                return;
            }
            float dt = Time.fixedDeltaTime;
            for (int steps = 0; flight.Time + dt <= senderNow && steps < MaxStepsPerFixedUpdate; steps++)
            {
                Vector3 velocity = flight.Velocity + Vector3.down * (__instance.m_gravity * dt);
                velocity += Mathf.Pow(velocity.magnitude, 2f) * __instance.m_drag * dt * -velocity.normalized;
                Vector3 next = flight.Position + velocity * dt;
                int mask = flight.Time - flight.Launched < LeaveBowSeconds ? SolidMask : HitMask;
                if (Physics.Linecast(flight.Position, next, out RaycastHit hit, mask, QueryTriggerInteraction.Ignore))
                {
                    if (((1 << hit.collider.gameObject.layer) & CharacterLayers) != 0) s_stoppedCharacter++;
                    else s_stoppedSolid++;
                    flight.Position = hit.point;
                    flight.Stopped = true;
                    flight.StoppedAt = Time.time;
                    break;
                }
                flight.Position = next;
                flight.Velocity = velocity;
                flight.Time += dt;
            }
            // Where the shooter's own copy was last reported is behind this one; much further than its speed explains is a bounce.
            float apart = Vector3.Distance(flight.Position, zdo.GetPosition());
            if (apart > flight.Velocity.magnitude * OffCourseSeconds + OffCourseMeters)
            {
                s_offCourse++;
                s_worstOffCourse = Mathf.Max(s_worstOffCourse, apart);
                Release(__instance, ___m_nview);
                return;
            }
            // How far ahead of the last synced position the copy is drawn, in time: the lag vanilla would have shown.
            if (flight.Velocity.sqrMagnitude > 1f)
            {
                s_leadSum += apart / flight.Velocity.magnitude;
                s_leadCount++;
            }
            transform.position = flight.Position;
            if (__instance.m_rotateVisual == 0f && flight.Velocity.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(flight.Velocity);
        }

        private static Flight Begin(Projectile projectile, ZNetView view)
        {
            if (s_flights.TryGetValue(projectile, out Flight flight)) return flight;
            if (s_vanilla.Contains(projectile)) return null;
            if (s_flights.Count + s_vanilla.Count > CleanupAbove) Cleanup();
            ZDO zdo = view.GetZDO();
            long stamp = zdo.GetLong(TimeHash, -1L);
            if (stamp < 0 || !RemoteMotion.TrySenderNow(zdo.GetOwner(), out _))
            {
                if (stamp < 0) s_noStamp++;
                else s_noClock++;
                s_vanilla.Add(projectile);
                return null;
            }
            double launched = stamp / MsPerSecond;
            flight = new Flight
            {
                Position = zdo.GetVec3(StartHash, projectile.transform.position),
                Velocity = zdo.GetVec3(VelocityHash, Vector3.zero),
                Time = launched,
                Launched = launched,
            };
            if (flight.Velocity.sqrMagnitude < 0.01f)
            {
                s_vanilla.Add(projectile);
                return null;
            }
            s_flights[projectile] = flight;
            s_flying.Add(view);
            s_flown++;
            return flight;
        }

        private static int CharacterLayers => s_characterLayers != 0 ? s_characterLayers
            : (s_characterLayers = LayerMask.GetMask("character", "character_net", "character_ghost", "hitbox", "character_noenv"));

        private static void Report()
        {
            float now = Time.time;
            if (now < s_nextReport) return;
            s_nextReport = now + ReportEverySeconds;
            int seen = s_flown + s_noStamp + s_noClock;
            if (seen == 0) return;
            if (LoggerOptions.DebugEnabled) LoggerOptions.LogDebug($"[RemoteArrows] last {ReportEverySeconds:F0} s: {seen} remote projectile(s): {s_flown} flown here "
                + $"(stopped at a character {s_stoppedCharacter}, at a solid {s_stoppedSolid}; handed back: {s_offCourse} off course"
                + (s_offCourse > 0 ? $" (worst {s_worstOffCourse:F1} m from the synced position)" : string.Empty)
                + $", {s_clockLost} shooter's clock lost, {s_timedOut} over {MaxFlightSeconds:F0} s), not flown: {s_noStamp} unstamped (shooter "
                + $"without FGN 1.5.5+ or with Remote Arrows off), {s_noClock} with no clock for the shooter (Remote Motion off or no stamps yet); drawn ahead of the synced "
                + $"position by {(s_leadCount > 0 ? s_leadSum / s_leadCount * 1000.0 : 0.0):F0} ms on average"
                + (s_waited > 0 ? $"; {s_waited} waited for the shooter's clock (worst {s_worstWait * 1000.0:F0} ms behind the launch)" : string.Empty) + ".");
            s_flown = s_noStamp = s_noClock = s_stoppedCharacter = s_stoppedSolid = s_offCourse = s_clockLost = s_timedOut = 0;
            s_worstOffCourse = 0f;
            s_waited = 0;
            s_worstWait = 0.0;
            s_leadSum = 0.0;
            s_leadCount = 0;
        }

        private static void Release(Projectile projectile, ZNetView view)
        {
            if (!s_flights.Remove(projectile)) return;
            s_flying.Remove(view);
            s_vanilla.Add(projectile);
        }

        private static void Cleanup()
        {
            var gone = new List<Projectile>();
            foreach (Projectile p in s_flights.Keys) if (p == null) gone.Add(p);
            foreach (Projectile p in gone) s_flights.Remove(p);
            s_vanilla.RemoveWhere(p => p == null);
            s_flying.RemoveWhere(v => v == null);
        }
    }
}
