using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Fight traffic, measured and ordered. Three keys, all off by default; with all three off at start this class is not attached
    /// at all, so the game runs exactly as before.
    ///
    /// Hit latency census (Debug): the attacker notes each hit it sends (Character.Damage), the owner of the target notes when
    /// it arrives (Character.RPC_Damage) and answers with a small Debug-only RPC carrying the arrival time. Times are the PC's
    /// performance counter (Stopwatch), which every process on one PC shares, so with everything on one PC the delay is exact.
    /// The hit itself is never changed: the answer is matched to the hit by target id plus a hash of the hit's point, direction
    /// and attacker, which travel unchanged. A hit with no answer within 2 s from an owner that does answer is counted lost; a hit
    /// that reached a peer that no longer owns the target (vanilla drops it) is counted as dropped.
    ///
    /// Stagger and knockback census (Debug): on the owner, the time from a hit arriving to the stagger and the push taking effect;
    /// on the attacker, the time from sending the hit to the owner's stagger or push (carried in the same answer), and to the
    /// target's position first changing in an update from the owner.
    ///
    /// Fight traffic first: inside each world-update list a player's own character and objects in a fight (hit within 5 s,
    /// projectiles) go ahead of the rest, never ahead of the group vanilla sends first. On Steam, a routed hit or stagger that
    /// has to wait in the send queue goes ahead of the world-update packets queued directly in front of it, never ahead of other
    /// RPCs. Nothing is dropped or held; only the order changes, inside caps written below.
    /// </summary>
    [HarmonyPatch]
    public static class FightNet
    {
        public static ConfigEntry<bool> ConfigHitCensus;
        public static ConfigEntry<bool> ConfigStaggerCensus;
        public static ConfigEntry<bool> ConfigFightFirst;

        // ---- caps (the send order rule) ----
        // World-update list: at most this many fight objects are moved ahead per list, and never more than half the list.
        internal const int MaxFightPerList = 8;
        // Every Nth list sent to the same peer keeps vanilla's order untouched, so bulk gets whole lists to itself.
        internal const int VanillaTurnEvery = 4;
        // How long an object counts as "in a fight" after a hit on it or by it.
        private const double FightMemorySeconds = 5.0;
        private const int MaxFightObjects = 1024;

        // ---- census ----
        private const string AckRpc = "FGN_FightHitAck";
        private const int AckBytes = 12 + 4 + 8 + 1;   // ZDOID (long + uint), hit hash, arrival time, flags
        private const double LostAfterSeconds = 2.0;
        private const double ReportSeconds = 10.0;
        private const double PruneSeconds = 0.5;
        private const double MaxPlausibleSeconds = 10.0;
        private const int MaxPending = 512;
        private const int MaxWatches = 256;
        private const int MaxRecentHits = 512;
        private const int MaxAnsweringPeers = 64;
        private const float MovedMetresSqr = 0.05f * 0.05f;

        private const byte FlagApplied = 1, FlagNotOwner = 2, FlagSkipped = 4, FlagStaggered = 8, FlagKnocked = 16, FlagBlocked = 32;

        internal static void InitConfig(ConfigFile config)
        {
            ConfigHitCensus = config.Bind("14 - Diagnostics", "Fight Census Hit Latency", false,
                Cfg.Adv("Debug log every 10 s: how long hits take from the attacker to the target's owner, and hits lost or dropped. "
                    + "Needs FGN Log Level Debug, and this key on for the attacker and the owner. Read at start: turning it on needs a restart."));
            ConfigStaggerCensus = config.Bind("14 - Diagnostics", "Fight Census Stagger And Knockback", false,
                Cfg.Adv("Debug log every 10 s: how long stagger and knockback take to show after a hit, on the owner and on the attacker. "
                    + "Needs FGN Log Level Debug. Read at start: turning it on needs a restart."));
            ConfigFightFirst = config.Bind("04 - Networking", "Fight Traffic First", false,
                Cfg.Adv("Your own character, creatures in a fight, projectiles, and hits are sent ahead of other world updates. "
                    + "Nothing is held back: other updates still go in the same send, and every fourth send keeps the game's order. "
                    + "Read at start: turning it on needs a restart."));
        }

        internal static bool AnyKeyOn =>
            (ConfigHitCensus != null && ConfigHitCensus.Value) || (ConfigStaggerCensus != null && ConfigStaggerCensus.Value)
            || (ConfigFightFirst != null && ConfigFightFirst.Value);

        private static bool HitOn() => ConfigHitCensus != null && ConfigHitCensus.Value;
        private static bool StaggerOn() => ConfigStaggerCensus != null && ConfigStaggerCensus.Value;
        private static bool CensusOn() => HitOn() || StaggerOn();
        internal static bool OrderOn() => ConfigFightFirst != null && ConfigFightFirst.Value;

        private static long Now() => Stopwatch.GetTimestamp();
        private static float Ms(long ticks) => (float)(ticks * 1000.0 / Stopwatch.Frequency);
        private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);

        // ================= shared state =================

        private struct HitKey : IEquatable<HitKey>
        {
            public ZDOID Target;
            public int Hash;
            public bool Equals(HitKey other) => Hash == other.Hash && Target.Equals(other.Target);
            public override bool Equals(object obj) => obj is HitKey other && Equals(other);
            public override int GetHashCode() => unchecked(Target.GetHashCode() * 397 ^ Hash);
        }

        private struct Sent
        {
            public long At;
            public long Owner;
        }

        private struct Watch
        {
            public long At;
            public Vector3 From;
        }

        /// <summary>Fixed-size sample of delays in ms. No allocation after start; the report sorts it in place.</summary>
        private sealed class Window
        {
            private readonly float[] m_values = new float[256];
            private int m_count;
            public int Total;
            public float Max;

            public void Add(float ms)
            {
                Total++;
                if (ms > Max) Max = ms;
                if (m_count < m_values.Length) m_values[m_count++] = ms;
            }

            public void Reset() { m_count = 0; Total = 0; Max = 0f; }

            public string Describe()
            {
                if (Total == 0) return "none";
                Array.Sort(m_values, 0, m_count);
                return $"{Total} (p50 {At(0.50):F0} ms, p95 {At(0.95):F0} ms, max {Max:F0} ms)";
            }

            private float At(double q) => m_values[Mathf.Clamp((int)Math.Ceiling(q * m_count) - 1, 0, m_count - 1)];
        }

        private static readonly Dictionary<HitKey, Sent> s_pending = new Dictionary<HitKey, Sent>();
        private static readonly List<HitKey> s_staleKeys = new List<HitKey>();
        private static readonly HashSet<long> s_answering = new HashSet<long>();
        private static readonly Dictionary<ZDOID, Watch> s_watches = new Dictionary<ZDOID, Watch>();
        private static readonly Dictionary<ZDOID, long> s_recentHits = new Dictionary<ZDOID, long>();
        private static readonly Dictionary<ZDOID, long> s_fightObjects = new Dictionary<ZDOID, long>();
        private static readonly List<ZDOID> s_staleIds = new List<ZDOID>();
        private static ZRoutedRpc s_ackRegisteredOn;
        private static bool s_failed;
        private static long s_nextPrune, s_nextReport;

        // N1 counters (this window)
        private static int s_sent, s_sentRemote, s_lost, s_unanswered, s_dropReported, s_dropHere, s_skipped, s_blocked,
            s_clockOff, s_unmatched, s_overflow, s_appliedHere, s_acksSent;
        private static readonly Window s_landRemote = new Window();
        private static readonly Window s_landLocal = new Window();

        // N2 (this window)
        private static readonly Window s_sendToStagger = new Window();
        private static readonly Window s_sendToKnock = new Window();
        private static readonly Window s_arriveToStagger = new Window();
        private static readonly Window s_arriveToKnock = new Window();
        private static readonly Window s_staggerByCall = new Window();
        private static readonly Window s_sendToMove = new Window();
        private static int s_noMove, s_staggerNoHit;

        // Order rule (this window)
        private static int s_lists, s_reordered, s_movedHead, s_movedAfter, s_ownFirst, s_vanillaTurns, s_capped, s_marksFull;
        internal static int LaneFight, LaneQueued, LaneJumped, LaneJumpedOver, LaneJumpedBytes, LaneCapped, LaneMarked;

        // Hit being applied right now (owner side), set in the RPC_Damage prefix and read by the stagger and push hooks.
        private static Character s_inDamage;
        private static long s_inDamageAt;
        private static int s_inDamageHash;
        private static byte s_inDamageFlags;
        private static bool s_inDamageOwner;

        internal static void OnShutdown()
        {
            s_pending.Clear();
            s_answering.Clear();
            s_watches.Clear();
            s_recentHits.Clear();
            s_fightObjects.Clear();
            s_turns.Clear();
            s_projectiles.Clear();
            s_projectileScanned = 0;
            s_inDamage = null;
            s_ackRegisteredOn = null;
            FightLane.OnShutdown();
        }

        private static void Fail(string where, Exception e)
        {
            if (s_failed) return;
            s_failed = true;
            LoggerOptions.LogWarning($"[FightNet] {where} threw ({e.GetType().Name}: {e.Message}); the game's own handling is untouched. Logged once.");
        }

        private static int HitHash(HitData hit)
        {
            // Point, direction and attacker reach the owner exactly as sent (HitData.Serialize writes them as raw floats and ids).
            unchecked
            {
                int h = 17;
                h = h * 31 + hit.m_point.x.GetHashCode();
                h = h * 31 + hit.m_point.y.GetHashCode();
                h = h * 31 + hit.m_point.z.GetHashCode();
                h = h * 31 + hit.m_dir.x.GetHashCode();
                h = h * 31 + hit.m_dir.y.GetHashCode();
                h = h * 31 + hit.m_dir.z.GetHashCode();
                h = h * 31 + hit.m_attacker.GetHashCode();
                return h;
            }
        }

        internal static void MarkFight(ZDOID id, bool byRelay)
        {
            if (id.IsNone()) return;
            if (s_fightObjects.Count >= MaxFightObjects && !s_fightObjects.ContainsKey(id)) { s_marksFull++; return; }
            s_fightObjects[id] = Now() + Ticks(FightMemorySeconds);
            if (byRelay) LaneMarked++;
        }

        // ================= attacker side =================

        [HarmonyPatch(typeof(Character), nameof(Character.Damage)), HarmonyPrefix, HarmonyPriority(Priority.Last)]
        static void Damage_Prefix(Character __instance, HitData hit, bool __runOriginal)
        {
            if (!__runOriginal || hit == null || __instance == null) return;
            bool census = CensusOn(), order = OrderOn();
            if (!census && !order) return;
            try { NoteSent(__instance, hit, census, order); }
            catch (Exception e) { Fail("Character.Damage census", e); }
        }

        private static void NoteSent(Character target, HitData hit, bool census, bool order)
        {
            ZDOID id = target.GetZDOID();
            if (id.IsNone() || ZDOMan.instance == null) return;
            ZDO zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null) return;
            if (order)
            {
                MarkFight(id, false);
                MarkFight(hit.m_attacker, false);
            }
            if (!census) return;

            long now = Now();
            EnsureAckRpc();
            long owner = zdo.GetOwner();
            bool remote = owner != ZDOMan.GetSessionID();
            if (s_pending.Count >= MaxPending) s_overflow++;
            else s_pending[new HitKey { Target = id, Hash = HitHash(hit) }] = new Sent { At = now, Owner = owner };
            s_sent++;
            if (remote) s_sentRemote++;

            // Observer view of the push: watch for the owner's first position update after a hit that pushes.
            if (StaggerOn() && remote && hit.m_pushForce > 0f && s_watches.Count < MaxWatches)
                s_watches[id] = new Watch { At = now, From = zdo.GetPosition() };
            Tick(now);
        }

        private static void EnsureAckRpc()
        {
            var routed = ZRoutedRpc.instance;
            if (routed == null || ReferenceEquals(routed, s_ackRegisteredOn)) return;
            s_ackRegisteredOn = routed;
            try { routed.Register<ZPackage>(AckRpc, RPC_HitAck); }
            catch (ArgumentException) { }   // already registered on this instance
        }

        /// <summary>The owner's answer. Debug-only; validated: exact size, a real sender, and it must match a hit this peer sent.</summary>
        private static void RPC_HitAck(long sender, ZPackage pkg)
        {
            try
            {
                if (!CensusOn() || pkg == null || sender == 0L || pkg.Size() != AckBytes) return;
                if (ZDOMan.instance == null || sender == ZDOMan.GetSessionID()) return;   // own answers never come over the network
                pkg.SetPos(0);
                ZDOID target = pkg.ReadZDOID();
                int hash = pkg.ReadInt();
                long at = pkg.ReadLong();
                byte flags = pkg.ReadByte();
                OnAck(sender, target, hash, at, flags);
            }
            catch (Exception e) { Fail("hit answer", e); }
        }

        private static void OnAck(long from, ZDOID target, int hash, long at, byte flags)
        {
            var key = new HitKey { Target = target, Hash = hash };
            if (!s_pending.TryGetValue(key, out Sent sent)) { s_unmatched++; return; }
            s_pending.Remove(key);
            if (s_answering.Count < MaxAnsweringPeers) s_answering.Add(from);

            if ((flags & FlagNotOwner) != 0) { s_dropReported++; return; }
            if ((flags & FlagSkipped) != 0) { s_skipped++; return; }
            if ((flags & FlagApplied) == 0) return;
            if ((flags & FlagBlocked) != 0) s_blocked++;

            long delta = at - sent.At;
            if (delta < 0 || delta > Ticks(MaxPlausibleSeconds)) { s_clockOff++; return; }   // another PC: its counter is not ours
            float ms = Ms(delta);
            if (from == ZDOMan.GetSessionID()) s_landLocal.Add(ms); else s_landRemote.Add(ms);
            if ((flags & FlagStaggered) != 0) s_sendToStagger.Add(ms);
            if ((flags & FlagKnocked) != 0) s_sendToKnock.Add(ms);
        }

        // ================= owner side =================

        [HarmonyPatch(typeof(Character), "RPC_Damage"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        static void RpcDamage_Prefix(Character __instance, HitData hit)
        {
            s_inDamage = null;
            if (hit == null || __instance == null) return;
            bool census = CensusOn(), order = OrderOn();
            if (!census && !order) return;
            try
            {
                if (order)
                {
                    MarkFight(__instance.GetZDOID(), false);
                    MarkFight(hit.m_attacker, false);
                }
                if (!census) return;
                s_inDamage = __instance;
                s_inDamageAt = Now();
                s_inDamageHash = HitHash(hit);
                s_inDamageOwner = __instance.IsOwner();
                s_inDamageFlags = (byte)(hit.m_blockable && __instance.IsBlocking() ? FlagBlocked : 0);
            }
            catch (Exception e) { s_inDamage = null; Fail("Character.RPC_Damage census", e); }
        }

        // __runOriginal: false when another mod's or FGN's own prefix took the hit (forwarded after a hand-off, or a lag-fair dodge).
        [HarmonyPatch(typeof(Character), "RPC_Damage"), HarmonyPostfix]
        static void RpcDamage_Postfix(Character __instance, long sender, bool __runOriginal)
        {
            if (s_inDamage == null || !ReferenceEquals(s_inDamage, __instance)) return;
            s_inDamage = null;
            try
            {
                byte flags = s_inDamageFlags;
                ZDOID id = __instance.GetZDOID();
                if (!__runOriginal) flags |= FlagSkipped;
                else if (!s_inDamageOwner) { flags |= FlagNotOwner; s_dropHere++; }
                else
                {
                    flags |= FlagApplied;
                    s_appliedHere++;
                    if (s_recentHits.Count < MaxRecentHits || s_recentHits.ContainsKey(id)) s_recentHits[id] = s_inDamageAt;
                }
                SendAck(sender, id, s_inDamageHash, s_inDamageAt, flags);
                Tick(Now());
            }
            catch (Exception e) { Fail("Character.RPC_Damage answer", e); }
        }

        private static void SendAck(long to, ZDOID target, int hash, long at, byte flags)
        {
            if (ZDOMan.instance == null) return;
            if (to == ZDOMan.GetSessionID()) { OnAck(to, target, hash, at, flags); return; }
            var routed = ZRoutedRpc.instance;
            if (to == 0L || routed == null) return;
            var pkg = new ZPackage();   // one small package per hit, Debug key only
            pkg.Write(target);
            pkg.Write(hash);
            pkg.Write(at);
            pkg.Write(flags);
            routed.InvokeRoutedRPC(to, AckRpc, pkg);
            s_acksSent++;
        }

        [HarmonyPatch(typeof(Character), "RPC_Stagger"), HarmonyPrefix]
        static void RpcStagger_Prefix(Character __instance, out bool __state)
        {
            __state = true;   // "already staggering": the postfix then does nothing
            if (__instance == null || !StaggerOn()) return;
            __state = __instance.IsStaggering();
        }

        [HarmonyPatch(typeof(Character), "RPC_Stagger"), HarmonyPostfix]
        static void RpcStagger_Postfix(Character __instance, bool __state)
        {
            if (__state || __instance == null || !StaggerOn()) return;   // vanilla returns at once while already staggering
            try
            {
                long now = Now();
                if (ReferenceEquals(__instance, s_inDamage))
                {
                    s_inDamageFlags |= FlagStaggered;
                    s_arriveToStagger.Add(Ms(now - s_inDamageAt));
                    return;
                }
                // A stagger raised by its own call (a non-owner's Stagger, or a mod): time since the last hit applied here.
                if (s_recentHits.TryGetValue(__instance.GetZDOID(), out long hitAt) && now - hitAt <= Ticks(LostAfterSeconds))
                    s_staggerByCall.Add(Ms(now - hitAt));
                else s_staggerNoHit++;
            }
            catch (Exception e) { Fail("Character.RPC_Stagger census", e); }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.ApplyPushback), new[] { typeof(Vector3), typeof(float) }), HarmonyPostfix]
        static void Pushback_Postfix(Character __instance, float pushForce)
        {
            if (pushForce <= 0f || __instance == null || !ReferenceEquals(__instance, s_inDamage) || !StaggerOn()) return;
            if (!__instance.IsKnockedBack()) return;
            s_inDamageFlags |= FlagKnocked;
            s_arriveToKnock.Add(Ms(Now() - s_inDamageAt));
        }

        /// <summary>From FgnHooks' ZDO.Deserialize postfix: the attacker sees the pushed target move in an update from its owner.</summary>
        internal static void OnDeserialize(ZDO zdo)
        {
            if (s_watches.Count == 0 || zdo == null) return;
            if (!StaggerOn()) { s_watches.Clear(); return; }
            if (!s_watches.TryGetValue(zdo.m_uid, out Watch watch)) return;
            if ((zdo.GetPosition() - watch.From).sqrMagnitude < MovedMetresSqr) return;
            s_watches.Remove(zdo.m_uid);
            s_sendToMove.Add(Ms(Now() - watch.At));
        }

        // ================= prune and report =================

        private static void Tick(long now)
        {
            if (now < s_nextPrune) return;
            s_nextPrune = now + Ticks(PruneSeconds);
            long lostBefore = now - Ticks(LostAfterSeconds);

            if (s_pending.Count > 0)
            {
                s_staleKeys.Clear();
                foreach (var entry in s_pending)
                {
                    if (entry.Value.At > lostBefore) continue;
                    s_staleKeys.Add(entry.Key);
                    long owner = entry.Value.Owner;
                    if (owner == ZDOMan.GetSessionID() || s_answering.Contains(owner)) s_lost++;
                    else s_unanswered++;
                }
                for (int i = 0; i < s_staleKeys.Count; i++) s_pending.Remove(s_staleKeys[i]);
            }
            if (s_watches.Count > 0)
            {
                s_staleIds.Clear();
                foreach (var entry in s_watches) if (entry.Value.At <= lostBefore) s_staleIds.Add(entry.Key);
                for (int i = 0; i < s_staleIds.Count; i++) s_watches.Remove(s_staleIds[i]);
                s_noMove += s_staleIds.Count;
            }
            if (s_recentHits.Count > MaxRecentHits / 2)
            {
                s_staleIds.Clear();
                foreach (var entry in s_recentHits) if (entry.Value <= lostBefore) s_staleIds.Add(entry.Key);
                for (int i = 0; i < s_staleIds.Count; i++) s_recentHits.Remove(s_staleIds[i]);
            }
            if (s_fightObjects.Count > 0)
            {
                s_staleIds.Clear();
                foreach (var entry in s_fightObjects) if (entry.Value <= now) s_staleIds.Add(entry.Key);
                for (int i = 0; i < s_staleIds.Count; i++) s_fightObjects.Remove(s_staleIds[i]);
            }

            if (now < s_nextReport) return;
            bool first = s_nextReport == 0;
            s_nextReport = now + Ticks(ReportSeconds);
            if (!first && LoggerOptions.DebugEnabled) Report();
            ResetWindow();
        }

        private static void Report()
        {
            if (HitOn() && (s_sent > 0 || s_appliedHere > 0 || s_dropHere > 0 || s_unmatched > 0))
                LoggerOptions.LogDebug($"[FightCensus] hits, last 10 s: sent {s_sent} ({s_sentRemote} to a target another peer owns). "
                    + $"Landed at the other owner: {s_landRemote.Describe()}; landed here: {s_landLocal.Describe()}. "
                    + $"Lost (no answer in 2 s from an owner that answers) {s_lost}; unanswered (owner runs no census) {s_unanswered}; "
                    + $"dropped at a peer that no longer owned the target: {s_dropReported} reported back, {s_dropHere} here; "
                    + $"taken by another hook (forwarded or dodged) {s_skipped}; blocked {s_blocked}; clock mismatch (other PC) {s_clockOff}; "
                    + $"answers matching no hit {s_unmatched}; not tracked (table full) {s_overflow}. "
                    + $"As owner here: {s_appliedHere} applied, {s_acksSent} answers sent. Times are this PC's performance counter.");

            if (StaggerOn() && (s_arriveToStagger.Total + s_arriveToKnock.Total + s_sendToStagger.Total + s_sendToKnock.Total
                + s_staggerByCall.Total + s_sendToMove.Total + s_noMove + s_staggerNoHit) > 0)
                LoggerOptions.LogDebug($"[FightCensus] stagger and knockback, last 10 s: hit sent to stagger on the owner {s_sendToStagger.Describe()}; "
                    + $"hit sent to knockback on the owner {s_sendToKnock.Describe()}; on the owner here, hit arrival to stagger "
                    + $"{s_arriveToStagger.Describe()}, to knockback {s_arriveToKnock.Describe()}; stagger by its own call after a hit "
                    + $"{s_staggerByCall.Describe()} ({s_staggerNoHit} with no hit in the last 2 s); seen here: pushing hit sent to the "
                    + $"target's first position change {s_sendToMove.Describe()}, no change within 2 s {s_noMove}.");

            if (OrderOn() && (s_lists > 0 || LaneFight > 0))
                LoggerOptions.LogDebug($"[FightOrder] last 10 s: world-update lists {s_lists}, reordered {s_reordered}; fight objects moved ahead "
                    + $"{s_movedHead + s_movedAfter} ({s_movedHead} inside the group the game sends first, {s_movedAfter} right after it); "
                    + $"own character put first {s_ownFirst}; lists left in the game's order for bulk's turn {s_vanillaTurns}; "
                    + $"fight objects over the cap of {MaxFightPerList} a list {s_capped}; fight table full {s_marksFull}. "
                    + $"Steam hits and staggers sent {LaneFight}, {LaneQueued} had to wait, {LaneJumped} went ahead of {LaneJumpedOver} "
                    + $"world-update packet(s) ({LaneJumpedBytes} bytes), {LaneCapped} kept their place (byte cap {FightLane.MaxJumpBytesPerFrame} a frame); "
                    + $"objects marked in a fight by relayed hits {LaneMarked}.");
        }

        private static void ResetWindow()
        {
            s_sent = s_sentRemote = s_lost = s_unanswered = s_dropReported = s_dropHere = s_skipped = s_blocked = 0;
            s_clockOff = s_unmatched = s_overflow = s_appliedHere = s_acksSent = 0;
            s_landRemote.Reset(); s_landLocal.Reset();
            s_sendToStagger.Reset(); s_sendToKnock.Reset(); s_arriveToStagger.Reset(); s_arriveToKnock.Reset();
            s_staggerByCall.Reset(); s_sendToMove.Reset();
            s_noMove = s_staggerNoHit = 0;
            s_lists = s_reordered = s_movedHead = s_movedAfter = s_ownFirst = s_vanillaTurns = s_capped = s_marksFull = 0;
            LaneFight = LaneQueued = LaneJumped = LaneJumpedOver = LaneJumpedBytes = LaneCapped = LaneMarked = 0;
        }

        // ================= send order rule (N14 + N15, world updates) =================

        private static readonly Dictionary<ZDOMan.ZDOPeer, int> s_turns = new Dictionary<ZDOMan.ZDOPeer, int>();
        private static readonly HashSet<int> s_projectiles = new HashSet<int>();
        private static int s_projectileScanned;
        private static readonly List<ZDO> s_fightHead = new List<ZDO>();
        private static readonly List<ZDO> s_restHead = new List<ZDO>();
        private static readonly List<ZDO> s_fightAfter = new List<ZDO>();
        private static readonly List<ZDO> s_restAfter = new List<ZDO>();

        // Last, so it sees the list after vanilla's sort and after ZDOThrottlingPatches' re-sort.
        [HarmonyPatch(typeof(ZDOMan), "ServerSortSendZDOS"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        static void ServerSort_Postfix(List<ZDO> objects, ZDOMan.ZDOPeer peer)
        {
            if (!OrderOn() || objects == null || objects.Count < 2 || peer?.m_peer == null) return;
            try { Reorder(objects, peer, true); }
            catch (Exception e) { Fail("server send order", e); }
        }

        [HarmonyPatch(typeof(ZDOMan), "ClientSortSendZDOS"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        static void ClientSort_Postfix(List<ZDO> objects, ZDOMan.ZDOPeer peer)
        {
            if (!OrderOn() || objects == null || objects.Count < 2 || peer?.m_peer == null) return;
            try { Reorder(objects, peer, false); }
            catch (Exception e) { Fail("client send order", e); }
        }

        /// <summary>
        /// Vanilla's list starts with a group it always sends first: on the server, creatures and players another peer owns
        /// (ZDOMan.ServerSendCompare); on a client, Prioritized objects (ClientSendCompare). That group stays first. Inside it,
        /// the client's own character goes to the very front, then fight objects; right after it, fight objects from the rest of
        /// the list; then everything else in its existing order. At most MaxFightPerList (and half the list) move, and every
        /// VanillaTurnEvery-th list to a peer is left exactly as vanilla sorted it.
        /// </summary>
        private static void Reorder(List<ZDO> objects, ZDOMan.ZDOPeer peer, bool server)
        {
            long now = Now();
            Tick(now);
            s_lists++;

            s_turns.TryGetValue(peer, out int turn);
            if (s_turns.Count >= 256 && turn == 0) s_turns.Clear();
            s_turns[peer] = ++turn;
            if (turn % VanillaTurnEvery == 0) { s_vanillaTurns++; return; }

            ScanProjectiles();
            ZDOID own = !server && Player.m_localPlayer != null ? Player.m_localPlayer.GetZDOID() : ZDOID.None;
            if (s_fightObjects.Count == 0 && s_projectiles.Count == 0 && own.IsNone()) return;

            long receiver = peer.m_peer.m_uid;
            int count = objects.Count;
            int head = 0;
            while (head < count && InFirstGroup(objects[head], server, receiver)) head++;

            int cap = Math.Max(1, Math.Min(MaxFightPerList, count / 2));
            int moved = 0, firstFightAt = -1;
            ZDO ownZdo = null;
            for (int i = 0; i < count; i++)
            {
                ZDO zdo = objects[i];
                if (i < head && ownZdo == null && !own.IsNone() && zdo.m_uid.Equals(own)) { ownZdo = zdo; continue; }
                bool fight = IsFight(zdo, now);
                if (fight && moved >= cap) { s_capped++; fight = false; }
                if (fight) { moved++; if (firstFightAt < 0) firstFightAt = i; }
                if (i < head) (fight ? s_fightHead : s_restHead).Add(zdo);
                else (fight ? s_fightAfter : s_restAfter).Add(zdo);
            }

            bool ownMoves = ownZdo != null && !ReferenceEquals(objects[0], ownZdo);
            // Nothing to do when no fight object would change place and the own character is already first.
            bool fightMoves = moved > 0 && !AlreadyInPlace(firstFightAt, head);
            if (fightMoves || ownMoves)
            {
                int at = 0;
                if (ownZdo != null) objects[at++] = ownZdo;
                for (int i = 0; i < s_fightHead.Count; i++) objects[at++] = s_fightHead[i];
                for (int i = 0; i < s_restHead.Count; i++) objects[at++] = s_restHead[i];
                for (int i = 0; i < s_fightAfter.Count; i++) objects[at++] = s_fightAfter[i];
                for (int i = 0; i < s_restAfter.Count; i++) objects[at++] = s_restAfter[i];
                s_reordered++;
                s_movedHead += s_fightHead.Count;
                s_movedAfter += s_fightAfter.Count;
                if (ownMoves) s_ownFirst++;
            }
            s_fightHead.Clear(); s_restHead.Clear(); s_fightAfter.Clear(); s_restAfter.Clear();
        }

        // Cheap check only for the common "one fight object, already at the front of its part" case; anything else is rewritten.
        private static bool AlreadyInPlace(int firstFightAt, int head)
            => s_fightHead.Count + s_fightAfter.Count == 1 && (firstFightAt == 0 || firstFightAt == head);

        private static bool InFirstGroup(ZDO zdo, bool server, long receiver)
        {
            if (zdo.Type != ZDO.ObjectType.Prioritized) return false;
            return !server || (zdo.HasOwner() && zdo.GetOwner() != receiver);
        }

        private static bool IsFight(ZDO zdo, long now)
            => (s_fightObjects.TryGetValue(zdo.m_uid, out long until) && until > now) || s_projectiles.Contains(zdo.GetPrefab());

        // Mods add prefabs to ZNetScene.m_prefabs after start; the list only grows, so only the new tail is scanned.
        private static void ScanProjectiles()
        {
            var list = ZNetScene.instance != null ? ZNetScene.instance.m_prefabs : null;
            if (list == null || list.Count <= s_projectileScanned) return;
            int before = s_projectiles.Count;
            for (int i = s_projectileScanned; i < list.Count; i++)
            {
                var prefab = list[i];
                if (prefab != null && prefab.GetComponent<Projectile>() != null) s_projectiles.Add(prefab.name.GetStableHashCode());
            }
            s_projectileScanned = list.Count;
            LoggerOptions.LogDebug($"[FightOrder] {s_projectiles.Count - before} projectile prefab(s) found ({s_projectiles.Count} in all); they count as fight objects.");
        }
    }

    /// <summary>
    /// Steam send queue part of the fight order rule. ZSteamSocket queues every packet Steam cannot take yet, in order. A routed
    /// hit or stagger that has to wait goes ahead of the world-update (ZDOData) packets queued directly in front of it, and of
    /// nothing else, so its order against every other RPC is kept. Caps: hit packets over MaxFightPacketBytes never jump, at most
    /// MaxJumpOver packets are passed, and at most MaxJumpBytesPerFrame bytes of hits jump per connection per frame (a world
    /// update is up to about 10 KB, so bulk keeps well over half of each frame's send even in the worst case).
    /// Called only from FgnHooks' Steam-only ZSteamSocket.Send hook; no field names a Steam type (Store builds load this class).
    /// </summary>
    // 1.6.84 merge ([fgn], [lead] decision): NOT WIRED. No hook calls BeforeSend/AfterSend, so the Steam send queue is never reordered: a hit moved ahead of its target's
    // spawn/ZDO packet would be a hit on a target that does not exist yet. Kept only so the [FightOrder] line compiles; its Lane counters read 0.
    internal static class FightLane
    {
        internal const int MaxFightPacketBytes = 1024;
        internal const int MaxJumpBytesPerFrame = 4096;
        internal const int MaxJumpOver = 64;

        private const int KindNone = -1, KindOther = 0, KindBulk = 1, KindFight = 2;
        private const int RoutedHeaderBytes = 48;   // ZRpc hash, package length, msg id, sender, target, target ZDOID, method hash

        private static readonly int s_routedRpcHash = "RoutedRPC".GetStableHashCode();
        private static readonly int s_zdoDataHash = "ZDOData".GetStableHashCode();
        private static readonly HashSet<int> s_fightMethods = new HashSet<int>
        {
            "RPC_Damage".GetStableHashCode(),   // Character, and also WearNTear / Destructible / TreeBase hits (same name)
            "RPC_Stagger".GetStableHashCode(),
        };

        private sealed class Lane
        {
            public int BulkTail;   // world-update packets at the end of the queue, in a row
            public int Frame;
            public int Bytes;
        }

        private static readonly Dictionary<object, Lane> s_lanes = new Dictionary<object, Lane>();
        private static AccessTools.FieldRef<ZPackage, MemoryStream> s_stream;
        private static object s_sendQueue;   // AccessTools.FieldRef<ZSteamSocket, Queue<byte[]>>
        private static AccessTools.FieldRef<Queue<byte[]>, byte[][]> s_queueArray;
        private static AccessTools.FieldRef<Queue<byte[]>, int> s_queueHead;
        private static AccessTools.FieldRef<Queue<byte[]>, int> s_queueVersion;
        private static bool s_resolved, s_usable;
        private static int s_kind = KindNone;
        private static int s_bytes;

        internal static void OnShutdown() => s_lanes.Clear();

        /// <summary>ZSteamSocket.Send prefix: classify the outgoing packet from its first bytes (no copy).</summary>
        internal static void BeforeSend(ZPackage pkg)
        {
            s_kind = KindNone;
            if (pkg == null || !FightNet.OrderOn() || !Resolve()) return;
            try { s_kind = Classify(pkg, out s_bytes); }
            catch (Exception) { s_kind = KindNone; }
        }

        private static int Classify(ZPackage pkg, out int size)
        {
            size = pkg.Size();   // flushes the writer before the buffer is read
            if (size < 4) return KindOther;
            MemoryStream stream = s_stream(pkg);
            if (stream == null || !stream.TryGetBuffer(out ArraySegment<byte> segment) || segment.Count < size) return KindOther;
            byte[] bytes = segment.Array;
            int o = segment.Offset;
            int method = BitConverter.ToInt32(bytes, o);
            if (method == s_zdoDataHash) return KindBulk;
            if (method != s_routedRpcHash || size < RoutedHeaderBytes) return KindOther;
            if (BitConverter.ToInt32(bytes, o + 4) != size - 8) return KindOther;   // not the plain one-package layout
            if (!s_fightMethods.Contains(BitConverter.ToInt32(bytes, o + 44))) return KindOther;
            FightNet.LaneFight++;
            FightNet.MarkFight(new ZDOID(BitConverter.ToInt64(bytes, o + 28), BitConverter.ToUInt32(bytes, o + 36)), true);
            return KindFight;
        }

        /// <summary>ZSteamSocket.Send postfix: the packet is either sent (queue empty) or waiting at the end of the queue.</summary>
        internal static void AfterSend(ZSteamSocket socket)
        {
            int kind = s_kind;
            s_kind = KindNone;
            if (kind == KindNone || socket == null) return;
            try { Place(socket, kind); }
            catch (Exception) { }
        }

        private static void Place(ZSteamSocket socket, int kind)
        {
            var queue = ((AccessTools.FieldRef<ZSteamSocket, Queue<byte[]>>)s_sendQueue)(socket);
            if (queue == null) return;
            if (!s_lanes.TryGetValue(socket, out Lane lane))
            {
                if (s_lanes.Count >= 256) s_lanes.Clear();
                lane = new Lane();
                s_lanes[socket] = lane;
            }
            int count = queue.Count;
            if (count == 0) { lane.BulkTail = 0; return; }   // sent at once: nothing to order
            // The queue only loses packets from the front, so the world-update run at its end can only have shrunk.
            int tail = Math.Min(lane.BulkTail, count - 1);
            if (kind == KindBulk) { lane.BulkTail = tail + 1; return; }
            if (kind == KindOther) { lane.BulkTail = 0; return; }

            FightNet.LaneQueued++;
            int frame = Time.frameCount;
            if (lane.Frame != frame) { lane.Frame = frame; lane.Bytes = 0; }
            int pass = Math.Min(tail, MaxJumpOver);
            if (pass > 0 && (s_bytes > MaxFightPacketBytes || lane.Bytes + s_bytes > MaxJumpBytesPerFrame)) { FightNet.LaneCapped++; pass = 0; }
            if (pass == 0 || !MoveForward(queue, count, pass)) { lane.BulkTail = 0; return; }
            lane.Bytes += s_bytes;
            lane.BulkTail = pass;   // the passed world updates now sit behind it, at the end
            FightNet.LaneJumped++;
            FightNet.LaneJumpedOver += pass;
            FightNet.LaneJumpedBytes += s_bytes;
        }

        // Moves the last queued packet `pass` places forward inside Queue<T>'s ring buffer. No allocation.
        private static bool MoveForward(Queue<byte[]> queue, int count, int pass)
        {
            byte[][] ring = s_queueArray(queue);
            if (ring == null || ring.Length < count) return false;
            int head = s_queueHead(queue);
            int len = ring.Length;
            byte[] mine = ring[(head + count - 1) % len];
            for (int i = count - 1; i > count - 1 - pass; i--)
                ring[(head + i) % len] = ring[(head + i - 1) % len];
            ring[(head + count - 1 - pass) % len] = mine;
            s_queueVersion(queue)++;
            return true;
        }

        private static bool Resolve()
        {
            if (s_resolved) return s_usable;
            s_resolved = true;
            try
            {
                var queueField = AccessTools.Field(typeof(ZSteamSocket), "m_sendQueue");
                var streamField = AccessTools.Field(typeof(ZPackage), "m_stream");
                var arrayField = AccessTools.Field(typeof(Queue<byte[]>), "_array");
                var headField = AccessTools.Field(typeof(Queue<byte[]>), "_head");
                var versionField = AccessTools.Field(typeof(Queue<byte[]>), "_version");
                if (queueField == null || queueField.FieldType != typeof(Queue<byte[]>) || streamField == null
                    || streamField.FieldType != typeof(MemoryStream) || arrayField == null || headField == null || versionField == null)
                {
                    LoggerOptions.LogWarning("[FightOrder] the Steam send queue or ZPackage no longer look as expected; hits keep their place in the Steam queue (world-update order rule still on).");
                    return s_usable = false;
                }
                s_sendQueue = AccessTools.FieldRefAccess<ZSteamSocket, Queue<byte[]>>(queueField);
                s_stream = AccessTools.FieldRefAccess<ZPackage, MemoryStream>(streamField);
                s_queueArray = AccessTools.FieldRefAccess<Queue<byte[]>, byte[][]>(arrayField);
                s_queueHead = AccessTools.FieldRefAccess<Queue<byte[]>, int>(headField);
                s_queueVersion = AccessTools.FieldRefAccess<Queue<byte[]>, int>(versionField);
                LoggerOptions.LogDebug("[FightOrder] Steam send queue found; waiting hits and staggers go ahead of queued world updates.");
                return s_usable = true;
            }
            catch (Exception e)
            {
                LoggerOptions.LogWarning($"[FightOrder] could not attach to the Steam send queue ({e.Message}); hits keep their place there.");
                return s_usable = false;
            }
        }
    }
}
