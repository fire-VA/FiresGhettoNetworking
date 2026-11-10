using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.5.75 (Fire 10-24: a companion "is its own player" under server-side simulation). Another mod registers a
    /// companion with FgnApi.AddKeepAlive; while it is away from every player, the dedicated server keeps the world around it built
    /// and simulated like a player's area (zone loading, object create/destroy, the active-area test: the same three places FGN's
    /// fgn_sss_areas test areas use) and owns the companion itself. When a player comes within range, the companion is handed to
    /// that player once, as vanilla would.
    /// Areas: one per companion, merged when within MergeZones of a player or of an area already kept; companions that have not moved
    /// for 'Idle Minutes' settle (no area) until they move again. Capped by 'Max Companion Keep-Alive Areas' and two guards that
    /// protect small servers whatever the cap says:
    ///   frame  - no new area while the server's smoothed frame time is over 'Frame Guard Hold Ms'; one area settles every
    ///            SettleStepSeconds while it stays over 'Frame Guard Settle Ms' for SettleAfterSeconds.
    ///   memory - reads this machine's free physical memory: no new area below max(10 %, 8 GB) free; below 5 % one area settles
    ///            every SettleStepSeconds. Where the OS call is unavailable the memory guard is off (logged once).
    /// Server only, dedicated only. Nothing is sent on the wire.
    ///
    /// 1.5.82 (Fire 10-27, the Job Board's homestead upgrade): HOMESTEAD areas. Pinned (never idle-settle), a radius in zones (1..3),
    /// keyed by their centre position (a repeat registration within PositionKeyMeters replaces it, so Core re-registers from the
    /// board ZDOs after a restart with no id to remember), their own cap ('Max Homestead Areas') and 'Homesteads Per Owner'. They
    /// are filled before companion areas and settle last under the guards. An unattended homestead is fully live (Fire 10-27).
    /// The client reads availability from the server's version hello (FgnVersionGate).
    /// </summary>
    internal static class CompanionKeepAlive
    {
        private static bool s_companionOn, s_homeOn;
        private static int s_maxAreas = DefaultMaxAreas, s_homeMax = 4, s_homesPerOwner = 1, s_idleMinutes = 10;
        private static float s_holdMs = 20f, s_settleMs = 25f, s_memHoldPercent = 10f, s_memHoldMinGB = 2f, s_memSettlePercent = 5f;
        private const int HomeMaxLimit = 32, HomeRadiusMin = 1, HomeRadiusMax = 3;
        private const float PositionKeyMeters = 4f;

        private const int DefaultMaxAreas = 6, MaxAreasLimit = 128;
        private const int MergeZones = 2;
        private const float TickSeconds = 1f, SettleAfterSeconds = 30f, SettleStepSeconds = 10f, MoveMeters = 2f;
        private const double GB = 1024.0 * 1024 * 1024;
        private const float SeedMaxMs = 1000f;   // 1.5.84: a frame longer than this (the world load) never seeds the average
        private const float FrameSmoothing = 0.05f;
        private const float LogIntervalSeconds = 60f;

        private sealed class Entry
        {
            internal string Id;
            internal Func<Vector3?> Where;
            internal ZDOID Zdo = ZDOID.None;
            internal Vector3 Last;
            internal bool Known;
            internal float LastMove, Added;
            internal string State = "new";   // FgnApi.GetKeepAliveState: active, covered, settled, refused-cap, refused-frame, refused-memory, gone, off
            internal bool Pinned;            // 1.5.82: never idle-settles
        }

        // 1.5.82: a homestead area (FgnApi.AddHomestead), keyed by its centre
        private sealed class Home
        {
            internal Vector3 Centre;
            internal long Owner;
            internal int Radius, Zones;
            internal Vector3[] Points;       // the centre, plus satellites when the radius is wider than one player's area
            internal string State = "new";   // active, covered, refused-cap, refused-owner, refused-frame, refused-memory, off
            internal string Why = "";        // last decision, for the why-line
        }

        private static readonly List<Home> s_homes = new List<Home>();   // oldest first
        private static int s_homePoints, s_homeActive;

        private static readonly Dictionary<string, Entry> s_entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly List<Entry> s_ordered = new List<Entry>();
        private static readonly List<Vector3> s_points = new List<Vector3>();
        private static readonly List<Vector2s> s_peerZones = new List<Vector2s>();
        private static float s_nextTick, s_frameMs, s_overSince = -1f, s_nextSettle, s_nextLog;
        private static int s_allowed = int.MaxValue;
        private static string s_guard = "ok";
        private static bool s_memoryUnavailableLogged;
        private static long s_taken, s_handedBack;

        /// <summary>The kept areas' centres, read by ServerAuthorityPatches next to SssTestAreas.Points. Server only; empty when off.</summary>
        internal static List<Vector3> Points => s_points;

        internal static int Registered => s_entries.Count;
        internal static int Active => s_points.Count;
        internal static int Cap => s_companionOn ? s_maxAreas : 0;
        internal static int HomeCap => s_homeOn ? s_homeMax : 0;

        /// <summary>This process is a dedicated server running Server-Side Simulation with homestead areas on.</summary>
        internal static bool HomesteadsAvailable => HomeCap > 0 && ServerAuthorityPatches.SimulationActive && ZNet.instance != null && ZNet.instance.IsDedicated();
        internal static string Guard => s_guard;

        // 1.6.28 (Fire 11-02: "the configs for companion keep alive and all of that should be in companions not fgn"): FGN binds none of
        // these any more. The owner mod (FiresCompanions) binds the keys and passes the values in with FgnApi.SetKeepAliveSettings, again
        // whenever one changes. Until a mod calls it, keep-alive and homesteads are off, so FGN alone behaves as with the old default keys.
        internal static void ApplySettings(bool companionOn, int maxAreas, int idleMinutes, float holdMs, float settleMs,
            float memHoldPercent, float memHoldMinGB, float memSettlePercent, bool homeOn, int homeMax, int homesPerOwner)
        {
            s_companionOn = companionOn;
            s_maxAreas = Mathf.Clamp(maxAreas, 0, MaxAreasLimit);
            s_idleMinutes = Mathf.Clamp(idleMinutes, 0, 240);
            s_holdMs = Mathf.Clamp(holdMs, 5f, 200f);
            s_settleMs = Mathf.Clamp(settleMs, 5f, 500f);
            s_memHoldPercent = Mathf.Clamp(memHoldPercent, 0f, 50f);
            s_memHoldMinGB = Mathf.Clamp(memHoldMinGB, 0f, 64f);
            s_memSettlePercent = Mathf.Clamp(memSettlePercent, 0f, 50f);
            s_homeOn = homeOn;
            s_homeMax = Mathf.Clamp(homeMax, 0, HomeMaxLimit);
            s_homesPerOwner = Mathf.Clamp(homesPerOwner, 1, HomeMaxLimit);
            LoggerOptions.LogDebug($"[KeepAlive] settings from the owner mod: companion areas {(s_companionOn ? s_maxAreas.ToString() : "off")}, idle {s_idleMinutes} min, frame guard {s_holdMs:0.#}/{s_settleMs:0.#} ms, memory hold {s_memHoldPercent:0.#} % / {s_memHoldMinGB:0.#} GB, settle {s_memSettlePercent:0.#} %, homesteads {(s_homeOn ? s_homeMax.ToString() : "off")} ({s_homesPerOwner} per owner)");
        }

        // ---------------- homesteads (FgnApi, 1.5.82) ----------------

        internal static string AddHome(Vector3 centre, long owner, int radiusZones, out bool ok)
        {
            ok = false;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return "off";
            int radius = Mathf.Clamp(radiusZones, HomeRadiusMin, HomeRadiusMax);
            Home h = FindHome(centre);
            if (h == null)
            {
                int mine = 0;
                foreach (Home o in s_homes) if (o.Owner == owner) mine++;
                int perOwner = s_homesPerOwner;
                if (owner != 0L && mine >= perOwner)
                {
                    LoggerOptions.LogDebug($"[KeepAlive] homestead at {Fmt(centre)} refused: owner {owner} already has {mine} (Homesteads Per Owner {perOwner})");
                    return "refused-owner";
                }
                h = new Home();
                s_homes.Add(h);
                LoggerOptions.LogDebug($"[KeepAlive] homestead registered at {Fmt(centre)}, owner {owner}, radius {radius} zone(s)");
            }
            h.Centre = centre; h.Owner = owner; h.Radius = radius;
            h.Points = HomePoints(centre, radius, out h.Zones);
            ok = true;
            return h.State;
        }

        internal static bool RemoveHome(Vector3 centre)
        {
            Home h = FindHome(centre);
            if (h == null) return false;
            s_homes.Remove(h);
            LoggerOptions.LogDebug($"[KeepAlive] homestead at {Fmt(h.Centre)} removed (owner {h.Owner})");
            return true;
        }

        internal static string HomeState(Vector3 centre)
        {
            if (!HomesteadsAvailable) return "off";
            Home h = FindHome(centre);
            return h != null ? h.State : "unknown";
        }

        internal static void HomeCounts(out int active, out int registered, out int cap, out int zones)
        {
            active = s_homeActive; registered = s_homes.Count; cap = HomesteadsAvailable ? HomeCap : 0; zones = 0;
            foreach (Home h in s_homes) if (h.State == "active") zones += h.Zones;
        }

        private static Home FindHome(Vector3 centre)
        {
            foreach (Home h in s_homes)
            {
                float dx = h.Centre.x - centre.x, dz = h.Centre.z - centre.z;
                if (dx * dx + dz * dz <= PositionKeyMeters * PositionKeyMeters) return h;
            }
            return null;
        }

        // One point simulates a player's area (Near zones round it). A wider radius adds a ring of points (radius - Near) zones out.
        private static Vector3[] HomePoints(Vector3 centre, int radius, out int zones)
        {
            int near = SimDistance.Near();
            Vector2s c = FgnRing.Sector(centre);
            zones = 0;
            for (int y = -radius; y <= radius; y++)
                for (int x = -radius; x <= radius; x++)
                    if (SimDistance.ZoneInRadius(c, new Vector2s(c.x + x, c.y + y), radius)) zones++;
            int step = radius - near;
            if (step <= 0) return new[] { centre };
            float m = step * ZoneSystem.c_ZoneSize;
            var list = new List<Vector3>();
            for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                    list.Add(centre + new Vector3(x * m, 0f, y * m));
            return list.ToArray();
        }

        private static string Fmt(Vector3 p) => $"({p.x:0},{p.z:0})";

        // ---------------- registry (FgnApi) ----------------

        internal static bool Add(string id, Func<Vector3?> where, ZDOID zdo) => Add(id, where, zdo, false);

        internal static bool Add(string id, Func<Vector3?> where, ZDOID zdo, bool pinned)
        {
            if (Add0(id, where, zdo)) { s_entries[id].Pinned = pinned; return true; }
            return false;
        }

        private static bool Add0(string id, Func<Vector3?> where, ZDOID zdo)
        {
            if (string.IsNullOrEmpty(id) || where == null) return false;
            if (!s_entries.TryGetValue(id, out Entry e))
            {
                e = new Entry { Id = id, Added = Time.realtimeSinceStartup, LastMove = Time.realtimeSinceStartup };
                s_entries[id] = e;
                s_ordered.Add(e);
            }
            e.Where = where;
            if (!zdo.IsNone()) e.Zdo = zdo;
            return true;
        }

        internal static bool Remove(string id)
        {
            if (string.IsNullOrEmpty(id) || !s_entries.TryGetValue(id, out Entry e)) return false;
            s_entries.Remove(id);
            s_ordered.Remove(e);
            return true;
        }

        internal static void Clear()
        {
            s_entries.Clear(); s_ordered.Clear(); s_points.Clear(); s_held.Clear();
            s_homes.Clear(); s_homePoints = s_homeActive = 0;
            s_allowed = int.MaxValue; s_guard = "ok"; s_overSince = -1f; s_frameMs = 0f;
        }

        // ---------------- the tick (ServerAuthorityPatches.ZoneSystem_Update_Postfix, every frame on a dedicated server) ----------------

        internal static void Update()
        {
            float dt = Time.unscaledDeltaTime * 1000f;
            if (s_frameMs <= 0f && dt > SeedMaxMs) { } else s_frameMs = s_frameMs <= 0f ? dt : s_frameMs + (dt - s_frameMs) * FrameSmoothing;
            float now = Time.realtimeSinceStartup;
            if (now < s_nextTick) return;
            s_nextTick = now + TickSeconds;
            if ((Cap <= 0 && HomeCap <= 0) || ZNet.instance == null || !ZNet.instance.IsDedicated() || !(FiresGhettoNetworkMod.ConfigEnableServerAuthority?.Value ?? false))
            {
                s_points.Clear();
                return;
            }
            UpdateGuard(now);
            BuildPoints(now);
            UpdateOwnership();
            if (s_entries.Count > 0 && now >= s_nextLog)
            {
                s_nextLog = now + LogIntervalSeconds;
                LoggerOptions.LogDebug($"[KeepAlive] {s_points.Count} area(s) kept for {s_entries.Count} registered companion(s), cap {Cap}, guard {s_guard}, "
                    + $"frame {s_frameMs:0.0} ms; ownership: {s_taken} taken by the server, {s_handedBack} handed back so far");
            }
        }

        private static void UpdateGuard(float now)
        {
            string before = s_guard;
            int active = s_points.Count;
            bool memHold = false, memSettle = false;
            if (FreeMemory(out double freeBytes, out double totalBytes))
            {
                memHold = freeBytes < MemHoldBytes(totalBytes);
                memSettle = freeBytes < MemSettleBytes(totalBytes);
            }
            // 1.5.83 (lead 10-27, the rig dedi read 33.3 ms all night, so 'frame (settling)' never ended): a frame-rate cap makes every
            // frame last at least 1000/cap ms while the server idles, so the thresholds are never below 1.2x / 1.5x the capped frame.
            int cap = Application.targetFrameRate;
            float capMs = cap > 0 ? 1000f / cap : 0f;
            if (cap != s_loggedCap)
            {
                s_loggedCap = cap;
                LoggerOptions.LogDebug($"[KeepAlive] server frame-rate cap {(cap > 0 ? $"{cap} fps ({capMs:0.0} ms a frame)" : "none")}: frame guard hold {HoldMs(capMs):0.0} ms, settle {SettleMs(capMs):0.0} ms; "
                    + $"memory guard {(FreeMemory(out double mf, out double mt) ? $"hold below {MemHoldBytes(mt) / GB:0.0} GB free ({s_memHoldPercent:0.#} % / min {s_memHoldMinGB:0.#} GB), settle below {MemSettleBytes(mt) / GB:0.0} GB ({s_memSettlePercent:0.#} %), now {mf / GB:0.0} of {mt / GB:0.0} GB free" : "off (free memory unreadable)")}. "
                    + "The guard only limits how many keep-alive areas the server builds; it never pauses companion or creature AI.");
            }
            // 1.5.85 (lead 10-27, round #8b flipped hold<->ok 28 times on single 45-65 ms frames): hysteresis on the 1 s ticks. Hold
            // only after HoldAfterTicks slow ticks in a row; release only after ReleaseAfterTicks fast ones in a row.
            if (s_frameMs > HoldMs(capMs)) { s_slowTicks++; s_fastTicks = 0; } else { s_fastTicks++; s_slowTicks = 0; }
            if (!s_frameHeld && s_slowTicks >= HoldAfterTicks) { s_frameHeld = true; s_frameFlips++; }
            else if (s_frameHeld && s_fastTicks >= ReleaseAfterTicks) { s_frameHeld = false; s_frameFlips++; }
            bool frameHold = s_frameHeld;
            bool frameOver = s_frameMs > SettleMs(capMs);
            if (frameOver) { if (s_overSince < 0f) s_overSince = now; } else s_overSince = -1f;
            bool frameSettle = frameOver && now - s_overSince >= SettleAfterSeconds;

            if (memSettle || frameSettle)
            {
                s_guard = memSettle ? "memory (settling)" : "frame (settling)";
                if (now >= s_nextSettle) { s_allowed = Math.Max(0, Math.Min(s_allowed, active) - 1); s_nextSettle = now + SettleStepSeconds; }
            }
            else if (memHold || frameHold)
            {
                s_guard = memHold ? "memory (hold)" : "frame (hold)";
                s_allowed = Math.Min(s_allowed, active);   // keep what runs, add nothing
            }
            else
            {
                s_guard = "ok";
                s_allowed = int.MaxValue;
            }
            if (s_guard != before)
                LoggerOptions.LogDebug($"[KeepAlive] guard {before} -> {s_guard}: frame {s_frameMs:0.0} ms (hold {HoldMs(capMs):0.0} / settle {SettleMs(capMs):0.0}), "
                    + $"free memory {(FreeMemory(out double f, out double t) ? $"{f / 1073741824.0:0.0} of {t / 1073741824.0:0.0} GB (hold below {MemHoldBytes(t) / GB:0.0} GB, settle below {MemSettleBytes(t) / GB:0.0} GB)" : "unknown")}; {active} area(s) kept, "
                    + $"{(s_allowed == int.MaxValue ? "no limit" : s_allowed + " allowed")}; frame hold flips so far {s_frameFlips} "
                    + $"(hold after {HoldAfterTicks} slow s in a row, release after {ReleaseAfterTicks} fast s). Only keep-alive areas are limited; companion AI is never paused by FGN.");
        }

        private const int HoldAfterTicks = 5, ReleaseAfterTicks = 10;
        private static int s_slowTicks, s_fastTicks, s_frameFlips;
        private static bool s_frameHeld;
        private static int s_loggedCap = int.MinValue;
        private static double MemHoldBytes(double total) => Math.Max(total * s_memHoldPercent / 100.0, s_memHoldMinGB * GB);
        private static double MemSettleBytes(double total) => total * s_memSettlePercent / 100.0;
        private static float HoldMs(float capMs) => Math.Max(s_holdMs, capMs * 1.2f);
        private static float SettleMs(float capMs) => Math.Max(s_settleMs, capMs * 1.5f);

        private static void BuildPoints(float now)
        {
            s_peerZones.Clear();
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                if (peer != null && peer.IsReady()) s_peerZones.Add(FgnRing.Sector(peer.GetRefPos()));
            s_points.Clear();
            BuildHomePoints();
            int limit = s_homePoints + Math.Min(Cap, Math.Max(0, s_allowed - s_homePoints));
            int idleSeconds = s_idleMinutes * 60;
            // Core sends one point per AREA (its anchor companion; the others in that area ride free), already >= 2 zones apart, so
            // points are not merged with each other here; a point only folds into a PLAYER's area, where it costs nothing extra.
            foreach (Entry e in s_ordered)   // oldest first: a new area never pushes out one already kept
            {
                Vector3? where = null;
                try { where = e.Where(); } catch (Exception ex) { LoggerOptions.LogDebug($"[KeepAlive] '{e.Id}' position threw {ex.GetType().Name}"); }
                if (!where.HasValue) { e.State = "gone"; continue; }
                Vector3 p = where.Value;
                if (!e.Known || (p - e.Last).sqrMagnitude > MoveMeters * MoveMeters) { e.Last = p; e.Known = true; e.LastMove = now; }
                if (!e.Pinned && idleSeconds > 0 && now - e.LastMove > idleSeconds) { e.State = "settled"; continue; }
                Vector2s zone = FgnRing.Sector(p);
                if (Near(zone, s_peerZones)) { e.State = "covered"; continue; }
                if (s_points.Count >= limit)
                {
                    e.State = s_points.Count - s_homePoints < Cap ? (s_guard.StartsWith("memory") ? "refused-memory" : "refused-frame") : "refused-cap";
                    continue;
                }
                s_points.Add(p);
                e.State = "active";
            }
        }

        // 1.5.82: homesteads first (oldest first), so companion areas fill what is left and settle before them. Each state change
        // logs why (cap, owner, guard, a player there).
        private static void BuildHomePoints()
        {
            s_homePoints = 0; s_homeActive = 0;
            int cap = HomesteadsAvailable ? HomeCap : 0;
            foreach (Home h in s_homes)
            {
                string state, why;
                if (cap <= 0) { state = "off"; why = "Homestead Keep-Alive off, or no Server-Side Simulation"; }
                else if (Near(FgnRing.Sector(h.Centre), s_peerZones)) { state = "covered"; why = "a player's area has it"; }
                else if (s_homeActive >= cap) { state = "refused-cap"; why = $"Max Homestead Areas {cap} reached"; }
                else if (s_points.Count + h.Points.Length > s_allowed)
                {
                    state = s_guard.StartsWith("memory") ? "refused-memory" : "refused-frame";
                    why = $"guard {s_guard}: {s_points.Count} point(s) kept, {s_allowed} allowed, this one needs {h.Points.Length}";
                }
                else
                {
                    s_points.AddRange(h.Points);
                    s_homePoints += h.Points.Length;
                    s_homeActive++;
                    state = "active";
                    why = $"no player near; {h.Zones} zone(s) live from {h.Points.Length} point(s)";
                }
                if (state != h.State || why != h.Why)
                {
                    if (state != h.State)
                        LoggerOptions.LogDebug($"[KeepAlive] homestead {Fmt(h.Centre)} (owner {h.Owner}, radius {h.Radius}) {h.State} -> {state}: {why}");
                    h.State = state; h.Why = why;
                }
            }
        }

        private static bool Near(Vector2s zone, List<Vector2s> zones)
        {
            foreach (Vector2s z in zones) if (SimDistance.ZoneInRadius(z, zone, MergeZones)) return true;
            return false;
        }

        // The server takes a registered companion when no player's active area reaches it and its area is kept; it hands it back to
        // the first player whose active area reaches it. One change per side of that line (no ping-pong: taking needs one zone more).
        // The companions to own: every ZDO of a client-simulated companion prefab (ServerOwnershipPatchesV3's excluded set: the
        // Fires companions + FgnApi.ExcludeFromServerSimulation) inside a kept area (the anchor and the ones riding in its area), plus
        // every one the server took earlier (so it is handed back when a player arrives, even after its area ended).
        private static readonly List<ZDO> s_scan = new List<ZDO>();
        private static readonly HashSet<ZDOID> s_held = new HashSet<ZDOID>();
        private static readonly List<ZDO> s_candidates = new List<ZDO>();

        private static void UpdateOwnership()
        {
            if (ZDOMan.instance == null) return;
            long server = ZDOMan.GetSessionID();
            int near = SimDistance.Near();
            s_candidates.Clear();
            var seen = new HashSet<ZDOID>();
            foreach (Vector3 p in s_points)
            {
                s_scan.Clear();
                ZDOMan.instance.FindSectorObjects(FgnRing.Sector(p), new SimulationDistance(MergeZones, 0), s_scan);
                foreach (ZDO z in s_scan)
                    if (z != null && ServerOwnershipPatchesV3.IsExcluded(z.GetPrefab()) && seen.Add(z.m_uid)) s_candidates.Add(z);
            }
            foreach (ZDOID id in new List<ZDOID>(s_held))
            {
                ZDO z = ZDOMan.instance.GetZDO(id);
                if (z == null) { s_held.Remove(id); continue; }
                if (seen.Add(id)) s_candidates.Add(z);
            }
            foreach (ZDO zdo in s_candidates)
            {
                Vector2s zone = FgnRing.Sector(zdo.GetPosition());
                long coveringPeer = 0L; bool anyWithinTake = false;
                foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                {
                    if (peer == null || !peer.IsReady()) continue;
                    Vector2s pz = FgnRing.Sector(peer.GetRefPos());
                    if (coveringPeer == 0L && SimDistance.ZoneInRadius(pz, zone, near)) coveringPeer = peer.m_uid;
                    if (SimDistance.ZoneInRadius(pz, zone, near + 1)) anyWithinTake = true;
                }
                long owner = zdo.GetOwner();
                if (coveringPeer != 0L)
                {
                    if (owner == server && s_held.Remove(zdo.m_uid))
                    {
                        zdo.SetOwner(coveringPeer);
                        s_handedBack++;
                        LoggerOptions.LogDebug($"[KeepAlive] companion {zdo.m_uid} handed back to peer {coveringPeer} (a player is in range)");
                    }
                }
                else if (!anyWithinTake && owner != server && IsKept(zone))
                {
                    zdo.SetOwner(server);
                    s_held.Add(zdo.m_uid);
                    s_taken++;
                    LoggerOptions.LogDebug($"[KeepAlive] companion {zdo.m_uid} taken by the server (no player in range; its area is kept)");
                }
            }
        }

        private static bool IsKept(Vector2s zone)
        {
            foreach (Vector3 p in s_points) if (SimDistance.ZoneInRadius(FgnRing.Sector(p), zone, MergeZones)) return true;
            return false;
        }

        /// <summary>True when a kept area covers this sector within the radius (ServerOwnershipPatchesV3's release rule).</summary>
        internal static bool Covers(Vector2s sector, int radius)
        {
            foreach (Vector3 p in s_points) if (SimDistance.ZoneInRadius(FgnRing.Sector(p), sector, radius)) return true;
            return false;
        }

        /// <summary>One registered area's state for FgnApi: active, covered (a player's area has it), settled (idle), refused-cap,
        /// refused-frame, refused-memory, gone (its position is null), off (feature off / not a dedicated server), unknown.</summary>
        internal static string StateOf(string id)
        {
            if (Cap <= 0 || ZNet.instance == null || !ZNet.instance.IsDedicated()) return "off";
            return id != null && s_entries.TryGetValue(id, out Entry e) ? e.State : "unknown";
        }

        internal static string StatusSegment() => Cap <= 0 && HomeCap <= 0 ? "off" : $"{s_points.Count}/{s_entries.Count} (cap {Cap}, guard {s_guard}, frame flips {s_frameFlips}), homesteads {s_homeActive}/{s_homes.Count} (cap {HomeCap}, {s_homePoints} point(s))";

        // ---------------- free memory (Windows; elsewhere the memory guard is off) ----------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MemoryStatusEx
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

        private static bool FreeMemory(out double free, out double total)
        {
            free = total = 0;
            try
            {
                var m = new MemoryStatusEx();
                if (!GlobalMemoryStatusEx(m)) return false;
                free = m.ullAvailPhys; total = m.ullTotalPhys;
                return total > 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                if (!s_memoryUnavailableLogged) { s_memoryUnavailableLogged = true; LoggerOptions.LogInfo("[KeepAlive] free memory is not readable on this OS; the memory guard is off (the frame guard still runs)."); }
                return false;
            }
        }
    }
}
