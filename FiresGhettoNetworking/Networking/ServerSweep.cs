using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// [wishbone] server rows N39 + N43, dedicated server only. All keys default OFF; this class is attached only when one is on
    /// at start AND the process is headless, so a client never has these patches.
    ///
    /// N39 "Spread Server Ownership Sweep": the V2 broad ownership pass (ServerOwnershipPatches, run from vanilla's
    /// ZDOMan.ReleaseZDOS every 2 s for the server and every peer, all in ONE frame) was FGN's heaviest dedi item on 11-09
    /// ([FGN perf] release-nearby 60 calls/min, 4.9 ms average, 11-16 ms worst, two calls in the same frame). With the key on,
    /// the pass still collects its objects at vanilla's moment, but the per-object rule runs from a FIFO queue at a per-frame
    /// ms budget. Nothing waits past 'Server Sweep Max Delay' (at most 1.5 s, below vanilla's 2 s pass interval): the frame
    /// that reaches the deadline finishes the rest whatever the budget, and a new pass first finishes any overdue rest.
    ///
    /// N43 "Hold Outer Ghost Zones On Full Queue": vanilla ZoneSystem.CreateGhostZones (every 0.1 s, at most one new zone per
    /// player per call) generates never-generated zones around each player out to the total (near + far) radius; the new
    /// zone's objects then queue for that player. While that player's send queue is over the gate SendZDOs itself uses, a
    /// zone OUTSIDE every player's near radius + 1 ring is not generated this call (vanilla SpawnZone's own "not ready" result,
    /// so the scan simply moves on). Zones a player stands in or is entering (near radius + 1) are never held, for any player.
    /// A hold lasts at most MaxHoldSeconds per player, then generation goes on as vanilla until the queue drains once.
    /// </summary>
    [HarmonyPatch]
    public static class ServerSweep
    {
        public static ConfigEntry<bool> ConfigSpread;
        public static ConfigEntry<float> ConfigBudgetMs;
        public static ConfigEntry<float> ConfigMaxDelay;
        public static ConfigEntry<bool> ConfigZoneGate;
        public static ConfigEntry<bool> ConfigCensus;

        // Vanilla ZDOMan.ReleaseZDOS: "m_releaseZDOTimer > 2.0" (ZDOMan.cs:715-724, 1.0.15 reference).
        private const float VanillaReleaseIntervalSeconds = 2f;
        private const float MaxDelayCeilingSeconds = 1.5f;     // deadline + one long frame stays under the 2 s interval
        // Vanilla ZDOMan.SendZDOs: skips a peer while its queue is over 10240 bytes (ZDOMan.cs:820-822).
        private const int VanillaQueueGateBytes = 10240;
        private const float MaxHoldSeconds = 2f;
        private const int CheckClockEvery = 32;
        private const float ReportSeconds = 60f;
        private const int HistBuckets = 1001;                  // 0.5 ms buckets, 0..500 ms (last bucket = 500 ms and over)

        private static bool s_attached;
        internal static bool AnyKeyOn => (ConfigSpread != null && ConfigSpread.Value) || (ConfigZoneGate != null && ConfigZoneGate.Value)
            || (ConfigCensus != null && ConfigCensus.Value);

        public static void InitConfig(ConfigFile config)
        {
            ConfigSpread = config.Bind("10 - Server Authority", "Spread Server Ownership Sweep", false,
                Cfg.Adv("Dedicated server, broad server ownership: the 2-second ownership pass is spread over the next frames within 'Server Sweep Budget Ms' instead of one long frame. No object waits more than 'Server Sweep Max Delay'. Restart to turn on."));
            ConfigBudgetMs = config.Bind("10 - Server Authority", "Server Sweep Budget Ms", 1f,
                Cfg.Adv("Milliseconds per server frame for the spread ownership pass.", new AcceptableValueRange<float>(0.25f, 8f)));
            ConfigMaxDelay = config.Bind("10 - Server Authority", "Server Sweep Max Delay", 0.5f,
                Cfg.Adv("Seconds: the longest any object of a pass may wait; then the rest runs in that frame. Kept under vanilla's 2 s pass interval.",
                    new AcceptableValueRange<float>(0.1f, MaxDelayCeilingSeconds)));
            ConfigZoneGate = config.Bind("04 - Networking", "Hold Outer Ghost Zones On Full Queue", false,
                Cfg.Adv("Dedicated server: while a player's send queue is full, zones beyond their near area + 1 ring are generated a little later (at most 2 s). Zones a player is in or entering are never held. Restart to turn on."));
            ConfigCensus = config.Bind("14 - Diagnostics", "Server Sweep Census", false,
                Cfg.Adv("Dedicated server, Log Level Debug: one [ServerSweep] line a minute with server frame ms (average, p99, worst), the spread sweep's deferrals and longest wait, and outer-zone holds with the queue size. Restart to turn on."));
        }

        /// <summary>Called by Ascend after InitConfig: patches only on a headless process with a key on.</summary>
        internal static bool ShouldAttach() => AnyKeyOn && SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

        /// <summary>After Ascend's TryPatchAll: the spread queue is used only when its drain postfix is really on ZDOMan.Update,
        /// so a failed patch can never leave objects queued with nothing to run them.</summary>
        internal static void NoteAttached()
        {
            s_attached = false;
            try
            {
                var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.Update)));
                if (info != null)
                    foreach (var p in info.Postfixes)
                        if (p.PatchMethod != null && p.PatchMethod.DeclaringType == typeof(ServerSweep)) { s_attached = true; break; }
            }
            catch { s_attached = false; }
            LoggerOptions.LogDebug(s_attached
                ? $"[ServerSweep] attached: spread ownership sweep {ConfigSpread.Value} (budget {ConfigBudgetMs.Value:F2} ms, max delay {MaxDelay():F2} s, vanilla pass every {VanillaReleaseIntervalSeconds:F0} s), outer ghost zone hold {ConfigZoneGate.Value}, census {ConfigCensus.Value}."
                : "[ServerSweep] drain hook not found on ZDOMan.Update: the ownership pass stays in one frame (as without the key).");
        }

        private static bool IsDedi => ZNet.instance != null && ZNet.instance.IsDedicated();

        // ───────────── N39: spread V2 ownership pass ─────────────

        private struct Segment
        {
            public long Uid;
            public long ServerUid;
            public Vector2s Zone;
            public int End;
            public float QueuedAt;
            public int Frame;
        }

        private static readonly List<ZDO> s_items = new List<ZDO>(16384);
        private static readonly List<ZDOID> s_ids = new List<ZDOID>(16384);
        private static readonly List<Segment> s_segments = new List<Segment>(16);
        private static int s_head, s_segment;

        /// <summary>Read by ServerOwnershipPatches' V2 prefix (already dedicated-only).</summary>
        internal static bool SpreadActive => s_attached && ConfigSpread != null && ConfigSpread.Value;

        internal static void Enqueue(List<ZDO> near, long uid, long serverUid, Vector2s zone)
        {
            var zdoMan = ZDOMan.instance;
            float now = Time.realtimeSinceStartup;
            // A new pass never starts behind an overdue rest of an older one.
            if (s_head < s_items.Count && now - s_segments[s_segment].QueuedAt >= MaxDelay()) Drain(zdoMan, true);

            int added = 0;
            for (int i = 0; i < near.Count; i++)
            {
                var zdo = near[i];
                if (zdo == null || !zdo.Persistent) continue;
                s_items.Add(zdo);
                s_ids.Add(zdo.m_uid);
                added++;
            }
            s_segments.Add(new Segment { Uid = uid, ServerUid = serverUid, Zone = zone, End = s_items.Count, QueuedAt = now, Frame = Time.frameCount });
            s_passes++;
            s_queued += added;
        }

        private static float MaxDelay() => Mathf.Clamp(ConfigMaxDelay?.Value ?? 0.5f, 0.1f, MaxDelayCeilingSeconds);

        private static void Drain(ZDOMan zdoMan, bool force)
        {
            if (s_head >= s_items.Count) { ResetQueue(); return; }
            long start = Stopwatch.GetTimestamp();
            long budget = (long)(Mathf.Clamp(ConfigBudgetMs?.Value ?? 1f, 0.25f, 8f) * Stopwatch.Frequency / 1000.0);
            float now = Time.realtimeSinceStartup;
            float maxDelay = MaxDelay();
            int frame = Time.frameCount;
            int sinceCheck = 0;

            while (s_head < s_items.Count)
            {
                var seg = s_segments[s_segment];
                if (s_head >= seg.End) { s_segment++; continue; }
                if (!force && now - seg.QueuedAt >= maxDelay) force = true;   // deadline reached: finish everything now

                // The caller left since the pass was queued: vanilla would have run its pass while it was here; nothing to hand it now.
                if (seg.Uid != seg.ServerUid && (ZNet.instance == null || ZNet.instance.GetPeer(seg.Uid) == null))
                {
                    s_dropped += seg.End - s_head;
                    s_head = seg.End;
                    continue;
                }

                var zdo = s_items[s_head];
                var id = s_ids[s_head];
                s_head++;
                if (zdo == null || zdo.m_uid != id) { s_stale++; continue; }   // destroyed (pooled ZDOs are Reset to ZDOID.None) or reused

                ServerOwnershipPatches.ProcessOne(zdoMan, zdo, seg.Uid, seg.ServerUid, seg.Zone);
                s_processed++;
                if (frame != seg.Frame)
                {
                    s_deferred++;
                    float wait = now - seg.QueuedAt;
                    if (wait > s_maxWait) s_maxWait = wait;
                }

                if (force || ++sinceCheck < CheckClockEvery) continue;
                sinceCheck = 0;
                if (Stopwatch.GetTimestamp() - start >= budget) break;
            }

            long spent = Stopwatch.GetTimestamp() - start;
            s_drainTicks += spent;
            if (spent > s_drainWorstTicks) s_drainWorstTicks = spent;
            if (force) s_forced++;
            if (s_head >= s_items.Count) ResetQueue();
        }

        private static void ResetQueue()
        {
            s_items.Clear();
            s_ids.Clear();
            s_segments.Clear();
            s_head = 0;
            s_segment = 0;
        }

        internal static void OnShutdown()
        {
            ResetQueue();
            s_holdSince.Clear();
            s_holdExpired.Clear();
            s_ctxPeer = null;
        }

        // ZDOMan.Update runs vanilla's ReleaseZDOS (which queues) and then this, so a pass starts in the frame it was collected.
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.Update)), HarmonyPostfix, HarmonyPriority(Priority.Low)]
        private static void ZDOMan_Update_Postfix(ZDOMan __instance)
        {
            if (s_items.Count > 0)
            {
                // Key turned off mid-session (or no longer a dedi): finish what is queued now, then stay idle.
                bool on = ConfigSpread != null && ConfigSpread.Value && IsDedi;
                Drain(__instance, !on);
            }
            if (ConfigCensus == null || !ConfigCensus.Value || !LoggerOptions.DebugEnabled || !IsDedi)
            {
                if (s_queued > 100000000) ClearCensus();   // counters only feed the census; keep them from wrapping when it is off
                return;
            }
            CensusTick();
        }

        // ───────────── N43: hold outer ghost zones while the player's queue is full ─────────────

        private static ZNetPeer s_ctxPeer;
        private static readonly Dictionary<ZNetPeer, float> s_holdSince = new Dictionary<ZNetPeer, float>();
        private static readonly HashSet<ZNetPeer> s_holdExpired = new HashSet<ZNetPeer>();

        [HarmonyPatch(typeof(ZoneSystem), "CreateGhostZones"), HarmonyPrefix]
        private static void CreateGhostZones_Prefix(Vector3 refPoint)
        {
            s_ctxPeer = null;
            if (ConfigZoneGate == null || !ConfigZoneGate.Value || !IsDedi) return;
            // Vanilla calls this with the server's own reference position, then once per peer with peer.GetRefPos().
            var peers = ZNet.instance.GetPeers();
            if (s_holdSince.Count > peers.Count || s_holdExpired.Count > peers.Count) { s_holdSince.Clear(); s_holdExpired.Clear(); }   // a peer left
            for (int i = 0; i < peers.Count; i++)
            {
                var p = peers[i];
                if (p != null && p.m_socket != null && p.GetRefPos() == refPoint) { s_ctxPeer = p; return; }
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), "CreateGhostZones"), HarmonyFinalizer]
        private static void CreateGhostZones_Finalizer() => s_ctxPeer = null;

        [HarmonyPatch(typeof(ZoneSystem), "SpawnZone"), HarmonyPrefix, HarmonyPriority(Priority.Low)]
        private static bool SpawnZone_Prefix(Vector2s zoneID, ZoneSystem.SpawnMode mode, ref GameObject root, ref bool __result)
        {
            var peer = s_ctxPeer;
            if (peer == null || mode != ZoneSystem.SpawnMode.Ghost) return true;   // only inside CreateGhostZones for a player
            if (ConfigZoneGate == null || !ConfigZoneGate.Value) return true;

            // Never a zone any player stands in or is entering: near radius + 1 ring around every connected player.
            int guard = SimDistance.Near() + 1;
            var peers = ZNet.instance.GetPeers();
            for (int i = 0; i < peers.Count; i++)
            {
                var p = peers[i];
                if (p != null && SimDistance.ZoneInRadius(FgnRing.Sector(p.GetRefPos()), zoneID, guard)) return true;
            }

            int queue;
            try { queue = peer.m_socket.GetSendQueueSize(); }
            catch { return true; }
            int limit = LinkController.TryRoomLimit(peer, out int window) ? window : VanillaQueueGateBytes;
            float now = Time.realtimeSinceStartup;
            if (queue <= limit)
            {
                s_holdSince.Remove(peer);
                s_holdExpired.Remove(peer);
                return true;
            }
            if (s_holdExpired.Contains(peer)) return true;   // held MaxHoldSeconds already: vanilla until the queue drains once
            if (!s_holdSince.TryGetValue(peer, out float since)) { since = now; s_holdSince[peer] = now; }
            float held = now - since;
            if (held >= MaxHoldSeconds) { s_holdExpired.Add(peer); s_holdTimeouts++; return true; }

            s_holds++;
            if (held > s_maxHold) s_maxHold = held;
            if (queue > s_holdQueueMax) s_holdQueueMax = queue;
            s_holdQueueLast = queue;
            s_holdLimitLast = limit;
            root = null;
            __result = false;   // vanilla's own "terrain not ready" answer: CreateGhostZones goes on to the next zone
            return false;
        }

        // ───────────── census ─────────────

        private static readonly int[] s_hist = new int[HistBuckets];
        private static int s_frames;
        private static double s_frameMsSum;
        private static float s_frameMsWorst;
        private static float s_nextReport = -1f;
        private static int s_passes, s_queued, s_processed, s_deferred, s_stale, s_dropped, s_forced, s_holds, s_holdTimeouts;
        private static int s_holdQueueMax, s_holdQueueLast, s_holdLimitLast;
        private static float s_maxWait, s_maxHold;
        private static long s_drainTicks, s_drainWorstTicks;

        private static void CensusTick()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            s_frames++;
            s_frameMsSum += ms;
            if (ms > s_frameMsWorst) s_frameMsWorst = ms;
            int b = (int)(ms * 2f);
            s_hist[b < 0 ? 0 : b >= HistBuckets ? HistBuckets - 1 : b]++;

            float now = Time.realtimeSinceStartup;
            if (s_nextReport < 0f) { s_nextReport = now + ReportSeconds; ClearCensus(); return; }
            if (now < s_nextReport) return;
            s_nextReport = now + ReportSeconds;

            int target = Mathf.CeilToInt(s_frames * 0.99f), acc = 0, p99Bucket = HistBuckets - 1;
            for (int i = 0; i < HistBuckets; i++) { acc += s_hist[i]; if (acc >= target) { p99Bucket = i; break; } }
            double msPerTick = 1000.0 / Stopwatch.Frequency;
            LoggerOptions.LogDebug(
                $"[ServerSweep] last {ReportSeconds:F0} s: server frame avg {(s_frames > 0 ? s_frameMsSum / s_frames : 0):F2} ms, p99 {(p99Bucket + 1) * 0.5f:F1} ms, worst {s_frameMsWorst:F1} ms over {s_frames} frames"
                + $" | ownership sweep {(SpreadActive ? "spread" : "off")}: {s_passes} pass(es), {s_queued} object(s) queued, {s_processed} run, {s_deferred} on a later frame, "
                + $"{s_stale} gone before their turn, {s_dropped} dropped (caller left), {s_forced} deadline/catch-up frame(s); longest wait {s_maxWait * 1000f:F0} ms "
                + $"(cap {MaxDelay() * 1000f:F0} ms, vanilla interval {VanillaReleaseIntervalSeconds * 1000f:F0} ms); drain {s_drainTicks * msPerTick:F1} ms total, worst frame {s_drainWorstTicks * msPerTick:F2} ms"
                + $" | outer ghost zones {(ConfigZoneGate != null && ConfigZoneGate.Value ? "gated" : "off")}: {s_holds} hold(s), longest {s_maxHold * 1000f:F0} ms (cap {MaxHoldSeconds * 1000f:F0} ms), {s_holdTimeouts} cap hit(s); "
                + $"queue when held max {s_holdQueueMax} B, last {s_holdQueueLast} B against a {s_holdLimitLast} B limit; {s_items.Count - s_head} still queued.");
            ClearCensus();
        }

        private static void ClearCensus()
        {
            System.Array.Clear(s_hist, 0, HistBuckets);
            s_frames = 0; s_frameMsSum = 0; s_frameMsWorst = 0f;
            s_passes = s_queued = s_processed = s_deferred = s_stale = s_dropped = s_forced = s_holds = s_holdTimeouts = 0;
            s_holdQueueMax = s_holdQueueLast = s_holdLimitLast = 0;
            s_maxWait = s_maxHold = 0f;
            s_drainTicks = s_drainWorstTicks = 0;
        }
    }
}
