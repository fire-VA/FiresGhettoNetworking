// [wishbone] arrival (FGN study rows N42 "Refuse empty teleport arrival" and the arrival half of N36). Our own code; no other mod's code used.
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// "Refuse Empty Teleport Arrival" (key, default OFF) and "Teleport Arrival Census" (key, default OFF, Debug measuring aid).
    /// Client side (also a local host); a dedicated server never teleports a local player, so nothing here runs there.
    ///
    /// Vanilla (Player.UpdateTeleport, assembly_valheimREF1.0.15 Player.cs:4478-4529): fade 2 s (:4488), move the player to the target, then
    /// release when (distant: timer > 8 s, :4497) AND ZNetScene.IsAreaReady(target) (ZNetScene.cs:125-139) AND FindFloor; a distant trip whose
    /// FindFloor still fails is dropped on the solid height at 15 s (:4508-4515), a short trip (dungeon door) bounces back.
    /// IsAreaReady's weakness: it checks only the target zone is loaded and that every ZDO the client ALREADY HAS in the 3 x 3 has an
    /// instance. ZDOs the server has not sent yet are not counted, so on a client of a dedicated server it passes on an empty destination
    /// (the "empty arrival"): the player lands, then the buildings pop in.
    ///
    /// OUR CHECK ("the destination has its objects"), ANDed into vanilla's own IsAreaReady result, only for the local player's own
    /// UpdateTeleport call (never for any other caller of IsAreaReady):
    ///   1. vanilla's IsAreaReady is true (we never release earlier than vanilla: we can only turn its true into false);
    ///   2. vanilla's ZoneSystem.IsActiveAreaLoaded() is true and NONE of the 9 zones around the target is still loading objects
    ///      (ZoneSystem.m_loadingObjectsInZones: dungeon rooms, FiresFixes' spread room spawns, location objects), not just the centre zone;
    ///   3. the incoming stream for those 9 zones has gone quiet: the count of ZDOs there that this session did not create itself (so local
    ///      spawns do not count) has not changed for "Teleport Arrival Quiet Ms" (default 1000), and the quiet clock cannot start before the
    ///      server can know where we are: move + 0.5 s when the server runs FGN (its Live Player Positions reads our character ZDO), move +
    ///      2.5 s otherwise (vanilla clients report their reference position every 2 s, ZNet.cs:1208-1213, + 0.5 s), 0 when hosting.
    /// CAP: vanilla's own 15 s teleport fallback (Player.cs:4508, timer counted from TeleportTo). Once m_teleportTimer >= 15 we stop
    /// holding and vanilla's result stands (release anyway, exactly as vanilla would).
    /// Player-visible cost (key on): the loading screen may stay up longer, at most until 15 s after the portal was entered; never shorter.
    ///
    /// Census (Debug; one line per teleport and a 10 s summary while trips happened): wait from the move to vanilla's pass and to ours,
    /// ms held by us, ZDOs and created objects in the 3 x 3 at release, cap hits, and for portals how many of the release-time ZDOs were
    /// already on the client when the player was moved (pre-streamed, e.g. by "Send Portal Destination On Use").
    /// With both keys off the patches are not attached at all (checked at start; a restart is needed to attach them).
    /// </summary>
    [HarmonyPatch]
    public static class ArrivalGate
    {
        public static ConfigEntry<bool> ConfigEnabled;
        public static ConfigEntry<bool> ConfigCensus;
        public static ConfigEntry<int> ConfigQuietMs;

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("04 - Networking", "Refuse Empty Teleport Arrival", false,
                Cfg.Adv("A teleport or portal trip only ends when the objects around the destination have arrived from the server and been built, not just the ground. The loading screen can stay up longer (never past vanilla's 15 s fallback), never shorter. Off until measured; restart to attach."));
            ConfigCensus = config.Bind("04 - Networking", "Teleport Arrival Census", false,
                Cfg.Adv("Debug log: how long each teleport waited for vanilla's check and for the full-destination check, and how much was there on arrival. Measuring aid; restart to attach."));
            ConfigQuietMs = config.Bind("04 - Networking", "Teleport Arrival Quiet Ms", 1000,
                Cfg.Adv("For 'Refuse Empty Teleport Arrival': how long no new objects may arrive for the destination before it counts as complete.",
                    new AcceptableValueRange<int>(250, 5000)));
        }

        /// <summary>Attach only when one of the keys is on at start (no hooks, no cost otherwise).</summary>
        public static bool KeyOnAtStart => (ConfigEnabled != null && ConfigEnabled.Value) || (ConfigCensus != null && ConfigCensus.Value);

        private static bool GateOn => ConfigEnabled != null && ConfigEnabled.Value;
        private static bool CensusOn => ConfigCensus != null && ConfigCensus.Value;

        // vanilla numbers (Player.cs, 1.0.15 reference)
        private const float VanillaMoveTimer = 2f;    // :4488 the player is moved after 2 s
        private const float VanillaCapTimer = 15f;    // :4508 the fallback; our hard cap
        private const float SampleEvery = 0.1f;
        private const float GraceFgnServer = 0.5f, GraceVanillaServer = 2.5f;   // ZNet.cs:1211 (2 s periodic ref pos) + 0.5 s

        private static readonly AccessTools.FieldRef<Player, bool> f_teleporting = AccessTools.FieldRefAccess<Player, bool>("m_teleporting");
        private static readonly AccessTools.FieldRef<Player, bool> f_distant = AccessTools.FieldRefAccess<Player, bool>("m_distantTeleport");
        private static readonly AccessTools.FieldRef<Player, float> f_timer = AccessTools.FieldRefAccess<Player, float>("m_teleportTimer");
        private static readonly AccessTools.FieldRef<Player, Vector3> f_target = AccessTools.FieldRefAccess<Player, Vector3>("m_teleportTargetPos");
        private static readonly AccessTools.FieldRef<ZoneSystem, Dictionary<Vector2s, List<ZDO>>> f_loadingZones =
            AccessTools.FieldRefAccess<ZoneSystem, Dictionary<Vector2s, List<ZDO>>>("m_loadingObjectsInZones");

        private static readonly List<ZDO> s_area = new List<ZDO>();

        // trip state (one local player)
        private static bool s_inTrip, s_scope, s_moved, s_held, s_capHit, s_distantTrip, s_broken;
        private static float s_lastTimer, s_tripStart, s_moveAt, s_vanillaPassAt, s_oursPassAt, s_heldSince, s_nextSample, s_countChangedAt, s_grace;
        private static int s_count = -1, s_countAtMove = -1;
        private static Vector3 s_target;
        private static string s_lastWhy;

        // 10 s window
        private static int w_trips, w_held, w_capHits, w_portalTrips;
        private static float w_heldMsTotal, w_heldMsMax, w_vanillaMsTotal, w_oursMsTotal, w_streamedPctTotal;
        private static float s_nextWindow;

        [HarmonyPatch(typeof(Player), "UpdateTeleport"), HarmonyPrefix, HarmonyPriority(Priority.Last)]
        private static void UpdateTeleportPrefix(Player __instance)
        {
            s_scope = false;
            if (s_broken || __instance == null || __instance != Player.m_localPlayer) return;
            if (!f_teleporting(__instance)) return;
            if (!GateOn && !CensusOn) return;
            float timer = f_timer(__instance);
            if (!s_inTrip || timer < s_lastTimer) Begin(__instance);   // vanilla restarts the timer at 0 for each trip
            s_lastTimer = timer;
            s_scope = true;   // vanilla's own IsAreaReady call below (after this prefix) is the one we AND into
            if (timer <= VanillaMoveTimer || ZDOMan.instance == null || ZNet.instance == null) return;
            float now = Time.unscaledTime;
            if (!s_moved) { s_moved = true; s_moveAt = now; s_nextSample = 0f; }
            if (now >= s_nextSample) { s_nextSample = now + SampleEvery; Sample(now); }
        }

        [HarmonyPatch(typeof(Player), "UpdateTeleport"), HarmonyPostfix, HarmonyPriority(Priority.First)]
        private static void UpdateTeleportPostfix(Player __instance)
        {
            s_scope = false;
            if (__instance == null || __instance != Player.m_localPlayer) return;
            if (s_inTrip && !f_teleporting(__instance)) End();
            float now = Time.unscaledTime;
            if (now >= s_nextWindow) Window(now);
        }

        [HarmonyPatch(typeof(Player), "UpdateTeleport"), HarmonyFinalizer]
        private static void UpdateTeleportFinalizer() { s_scope = false; }

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.IsAreaReady)), HarmonyPostfix]
        private static void IsAreaReadyPostfix(Vector3 point, ref bool __result)
        {
            if (!s_scope || !s_inTrip || !s_moved) return;
            s_scope = false;   // one decision per UpdateTeleport call
            try
            {
                var player = Player.m_localPlayer;
                if (player == null) return;
                float now = Time.unscaledTime;
                bool vanilla = __result;
                if (vanilla && s_vanillaPassAt < 0f) s_vanillaPassAt = now;
                if (!vanilla) return;   // vanilla holds by itself
                string why = WhyNotComplete(point, now);
                if (why == null) { if (s_oursPassAt < 0f) s_oursPassAt = now; return; }
                if (!GateOn) return;   // census only: shadow measurement
                if (f_timer(player) >= VanillaCapTimer)
                {
                    if (!s_capHit) { s_capHit = true; if (LoggerOptions.DebugEnabled) LoggerOptions.LogDebug($"[ArrivalGate] cap: vanilla's 15 s fallback reached, releasing as vanilla would (still missing: {why})."); }
                    return;
                }
                if (!s_held) { s_held = true; s_heldSince = now; }
                if (!ReferenceEquals(why, s_lastWhy)) { s_lastWhy = why; if (LoggerOptions.DebugEnabled) LoggerOptions.LogDebug($"[ArrivalGate] holding at {f_timer(player):F1} s: {why}"); }
                __result = false;
            }
            catch (System.Exception ex)
            {
                s_broken = true;
                LoggerOptions.LogWarning($"[ArrivalGate] turned off for this session after an error; vanilla arrival is unchanged. {ex.Message}");
            }
        }

        // constant reason strings (no allocation per frame); null = complete
        private const string WhyActiveArea = "the active area's zones are not all loaded";
        private const string WhyZoneLoading = "a zone around the destination is still loading its objects (dungeon rooms or a location)";
        private const string WhyNoServerYet = "the server cannot know our new position yet";
        private const string WhyStreaming = "objects for the destination are still arriving";

        private static string WhyNotComplete(Vector3 point, float now)
        {
            var zs = ZoneSystem.instance;
            if (zs == null) return null;
            if (!zs.IsActiveAreaLoaded()) return WhyActiveArea;
            var loading = f_loadingZones(zs);
            if (loading != null && loading.Count > 0)
            {
                Vector2s c = ZoneSystem.GetZone(point);
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (loading.ContainsKey(new Vector2s(c.x + dx, c.y + dy))) return WhyZoneLoading;
            }
            if (now < s_moveAt + s_grace) return WhyNoServerYet;
            float quiet = (ConfigQuietMs != null ? ConfigQuietMs.Value : 1000) / 1000f;
            float since = Mathf.Max(s_countChangedAt, s_moveAt + s_grace);
            if (s_count < 0 || now - since < quiet) return WhyStreaming;
            return null;
        }

        private static void Sample(float now)
        {
            int n = CountReceived(s_target, out _);
            if (n != s_count) { s_count = n; s_countChangedAt = now; }
            if (s_countAtMove < 0) s_countAtMove = n;
        }

        // ZDOs in the 3 x 3 around the target that this session did not create (server- or other-peer-made); created = of those, with an instance
        private static int CountReceived(Vector3 target, out int created)
        {
            created = 0;
            s_area.Clear();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(target), new SimulationDistance(1, 0), s_area);
            long me = ZDOMan.GetSessionID();
            int n = 0;
            for (int i = 0; i < s_area.Count; i++)
            {
                var z = s_area[i];
                if (z == null || z.m_uid.UserID == me) continue;
                n++;
                if (z.Created) created++;
            }
            s_area.Clear();
            return n;
        }

        private static void Begin(Player p)
        {
            s_inTrip = true; s_moved = false; s_held = false; s_capHit = false; s_lastWhy = null;
            s_tripStart = Time.unscaledTime; s_moveAt = 0f; s_vanillaPassAt = -1f; s_oursPassAt = -1f; s_heldSince = 0f;
            s_count = -1; s_countAtMove = -1; s_countChangedAt = 0f; s_nextSample = 0f;
            s_target = f_target(p);
            s_distantTrip = f_distant(p);
            var zn = ZNet.instance;
            if (zn == null || zn.IsServer()) s_grace = 0f;
            else
            {
                var server = zn.GetServerPeer();
                s_grace = server != null && FgnVersionGate.IsMatched(server) ? GraceFgnServer : GraceVanillaServer;
            }
        }

        private static void End()
        {
            s_inTrip = false;
            float now = Time.unscaledTime;
            float heldMs = s_held ? (now - s_heldSince) * 1000f : 0f;
            float vanillaMs = s_vanillaPassAt >= 0f && s_moved ? (s_vanillaPassAt - s_moveAt) * 1000f : -1f;
            float oursMs = s_oursPassAt >= 0f && s_moved ? (s_oursPassAt - s_moveAt) * 1000f : -1f;
            bool portal = s_distantTrip && PortalAhead.TripWasPortal(s_tripStart);
            w_trips++;
            if (s_held) { w_held++; w_heldMsTotal += heldMs; if (heldMs > w_heldMsMax) w_heldMsMax = heldMs; }
            if (s_capHit) w_capHits++;
            if (vanillaMs >= 0f) w_vanillaMsTotal += vanillaMs;
            if (oursMs >= 0f) w_oursMsTotal += oursMs;
            if (!LoggerOptions.DebugEnabled || ZDOMan.instance == null) return;
            int received = CountReceived(s_target, out int created);
            float streamedPct = received > 0 && s_countAtMove >= 0 ? 100f * Mathf.Min(s_countAtMove, received) / received : 100f;
            if (portal) { w_portalTrips++; w_streamedPctTotal += streamedPct; }
            LoggerOptions.LogDebug($"[ArrivalGate] trip {(s_distantTrip ? (portal ? "portal" : "distant") : "short")}: {(now - s_tripStart):F1} s total; " +
                $"from the move: vanilla check passed {(vanillaMs >= 0f ? vanillaMs.ToString("F0") + " ms" : "never (its own timer/floor path)")}, ours {(oursMs >= 0f ? oursMs.ToString("F0") + " ms" : "never")}; " +
                $"held by us {heldMs:F0} ms{(GateOn ? "" : " (key off: shadow only)")}{(s_capHit ? ", CAP (15 s) hit" : "")}; " +
                $"at release {received} received objects, {created} built; already here at the move {s_countAtMove} ({streamedPct:F0} %)" +
                $"{(portal ? (PortalAhead.UseAskSentFor(s_tripStart) ? ", early send asked at portal use" : ", no early send") : "")}");
        }

        private static void Window(float now)
        {
            s_nextWindow = now + 10f;
            if (w_trips == 0) return;
            if (LoggerOptions.DebugEnabled)
                LoggerOptions.LogDebug($"[ArrivalGate] last 10 s: {w_trips} trip(s), held {w_held} (total {w_heldMsTotal:F0} ms, worst {w_heldMsMax:F0} ms), cap hits {w_capHits}; " +
                    $"mean from move: vanilla {w_vanillaMsTotal / w_trips:F0} ms, ours {w_oursMsTotal / w_trips:F0} ms" +
                    $"{(w_portalTrips > 0 ? $"; portals {w_portalTrips}, mean pre-streamed {w_streamedPctTotal / w_portalTrips:F0} %" : "")}");
            w_trips = w_held = w_capHits = w_portalTrips = 0;
            w_heldMsTotal = w_heldMsMax = w_vanillaMsTotal = w_oursMsTotal = w_streamedPctTotal = 0f;
        }

        internal static void OnShutdown()
        {
            s_inTrip = false; s_scope = false; s_moved = false; s_held = false;
            s_area.Clear();
        }
    }
}
