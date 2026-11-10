using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// [wishbone] ownstab (study rows N22 + N23). Two server-side brakes on FGN's creature balancer (CreatureOwnership), both OFF by
    /// default, plus a census of every owner change the dedicated server sees.
    ///
    /// N22 "Ownership Move Hysteresis": a lower-ping player only takes an idle creature after being better by the margin for
    /// Confirm Seconds without a break (a dead band of half the margin keeps the streak alive through ping noise; the owner revision
    /// must not change meanwhile, so a claim restarts it). Moving a creature back to the owner it just left needs twice the margin
    /// for Return Penalty Seconds. Each balancer move within 120 s of the previous one on the same creature doubles its hold
    /// (30 s base, cap 240 s). Replaces the old "2 passes in a row" streak only while the key is on.
    ///
    /// N23 "Ownership Idle Gate": (1) no balancer scan at all while no two ready players can share an active area (zone distance
    /// over 3 in x or y: vanilla's active area reaches 1.5 zones from the player's zone centre, so two areas cannot overlap); (2)
    /// a ping/stall move is never made for an idle creature. Idle (measured on the server's own 2 s passes): not fighting by the
    /// balancer's own CombatReason, and its DataRevision (every owner write: position, rotation, any ZDO var) unchanged for Idle
    /// Seconds. A creature not yet seen changing is idle (fails closed = no move = vanilla's behaviour).
    ///
    /// Never touched (pass straight through, counted): fight moves (FirstFighter), the touch rule (GhostOwner169), every vanilla
    /// ClaimOwnership / SetOwner a client makes (they reach the server via ZDOMan.RPC_ZDOData and are only observed here), vanilla's /
    /// V3's ReleaseNearbyZDOS stale-owner rescues (observed, never delayed: the old owner has no instance, so routed RPCs to it -
    /// open, pick, use - would be dropped while we waited).
    ///
    /// Key off: CreatureOwnership calls return at their first bool; the census hooks are attached only when a key is on at start.
    /// No new RPC. No per-frame allocation (dictionaries of structs, bounded and pruned; strings only on the Debug lines).
    /// </summary>
    [HarmonyPatch]
    public static class OwnershipSteady
    {
        public static ConfigEntry<bool> ConfigHysteresis;
        public static ConfigEntry<float> ConfigConfirmSeconds;
        public static ConfigEntry<float> ConfigReturnPenaltySeconds;
        public static ConfigEntry<bool> ConfigIdleGate;
        public static ConfigEntry<float> ConfigIdleSeconds;

        private const string Section = "10 - Server Authority";
        private const double BaseHoldSeconds = 30.0;      // = CreatureOwnership.MinHoldSeconds
        private const double MaxHoldSeconds = 240.0;
        private const double ReflipWindowSeconds = 120.0;
        private const double RescueFlipBackSeconds = 10.0;
        private const int SharedAreaZones = 3;            // 2 x 1.5 zones (ZNetScene.PointInsideActiveArea), Chebyshev
        private const int MaxTracked = 4096;
        private const double Report10 = 10.0, Report30 = 30.0;

        private struct Confirm { public long Candidate; public double Since; public ushort OwnerRev; public int Passes; public bool OldWouldMove; }
        private struct Move { public long From; public double At; public double Hold; }
        private struct Rev { public uint Last; public double ChangedAt; public double BlockedSince; }
        private struct Rescue { public long From, To; public double At; }

        private static readonly Dictionary<ZDOID, Confirm> s_confirm = new Dictionary<ZDOID, Confirm>();
        private static readonly Dictionary<ZDOID, Move> s_moves = new Dictionary<ZDOID, Move>();
        private static readonly Dictionary<ZDOID, Rev> s_revs = new Dictionary<ZDOID, Rev>();
        private static readonly Dictionary<ZDOID, Rescue> s_rescues = new Dictionary<ZDOID, Rescue>();
        private static readonly List<ZDOID> s_stale = new List<ZDOID>();
        private static readonly Vector2s[] s_zones = new Vector2s[128];

        private static bool s_live;          // a key is on and this is a dedicated server (refreshed every Tick)
        private static int s_inRelease;      // inside ZDOMan.ReleaseNearbyZDOS (vanilla or FGN's V2/V3 prefix)
        private static int s_inZdoData;      // inside ZDOMan.RPC_ZDOData (a peer's change arriving)

        // Census slots; [0] = 10 s window, [1] = 30 s window.
        private enum N { Moves, MovesPing, MovesFight, MovesTouch, AvoidHyst, HeldDoubled, ReturnPenalty, AvoidIdle, PassesNoShared, PassesShared,
                         ClaimsRemote, BalancerLanded, Rescues, RescueFlipBacks, OtherServer, Count }
        private static readonly int[][] s_n = { new int[(int)N.Count], new int[(int)N.Count] };
        private static readonly double[] s_maxStale = new double[2];
        private static double s_next10, s_next30;

        internal static void InitConfig(ConfigFile config)
        {
            ConfigHysteresis = config.Bind(Section, "Ownership Move Hysteresis", false,
                Cfg.Adv("With 'Balance Creature Ownership' on: a lower-ping player takes an idle creature only after being better by the ping margin for 'Ownership Move Confirm Seconds' in a row (half the margin keeps the streak), twice the margin to send it back to the player it just left, and each repeat move within 2 min doubles the 30 s hold (max 4 min). Fights and the touch rule are not slowed. Off = the old 2-pass rule."));
            ConfigConfirmSeconds = config.Bind(Section, "Ownership Move Confirm Seconds", 6f,
                Cfg.Adv("How long a lower-ping player must stay better before an idle creature moves to them (Ownership Move Hysteresis).",
                    new AcceptableValueRange<float>(2f, 60f)));
            ConfigReturnPenaltySeconds = config.Bind(Section, "Ownership Return Penalty Seconds", 120f,
                Cfg.Adv("For this long after a move, sending the creature back to the player it came from needs twice the ping margin (Ownership Move Hysteresis).",
                    new AcceptableValueRange<float>(0f, 600f)));
            ConfigIdleGate = config.Bind(Section, "Ownership Idle Gate", false,
                Cfg.Adv("With 'Balance Creature Ownership' on: skip the balancer while no two players are close enough to share an area, and never move a creature for ping while it is idle (not fighting and unchanged for 'Ownership Idle Seconds'). Fights and the touch rule still move at once."));
            ConfigIdleSeconds = config.Bind(Section, "Ownership Idle Seconds", 10f,
                Cfg.Adv("A creature whose owner wrote no change to it (position, rotation, any value) for this long counts as idle (Ownership Idle Gate).",
                    new AcceptableValueRange<float>(3f, 120f)));
        }

        internal static bool AnyKeyOn => (ConfigHysteresis != null && ConfigHysteresis.Value) || (ConfigIdleGate != null && ConfigIdleGate.Value);
        internal static bool HysteresisOn => ConfigHysteresis != null && ConfigHysteresis.Value;
        internal static bool IdleGateOn => ConfigIdleGate != null && ConfigIdleGate.Value;

        private static void Inc(N slot) { s_n[0][(int)slot]++; s_n[1][(int)slot]++; }
        private static void NoteStale(double seconds)
        {
            if (seconds > s_maxStale[0]) s_maxStale[0] = seconds;
            if (seconds > s_maxStale[1]) s_maxStale[1] = seconds;
        }

        // ------------------------------------------------------------------ called by CreatureOwnership

        /// <summary>Every CreatureOwnership.Balance call (server ZDOMan.Update postfix), before its Active() check.</summary>
        internal static void Tick()
        {
            if (!AnyKeyOn) { s_live = false; return; }
            s_live = ZNet.instance != null && ZNet.instance.IsDedicated();
            if (!s_live) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now >= s_next10) { s_next10 = now + Report10; Flush(0, "10 s", now); }
            if (now >= s_next30) { s_next30 = now + Report30; Flush(1, "30 s", now); }
        }

        /// <summary>N23 (1): true = skip this whole balancer pass, no two candidates can share an active area.</summary>
        internal static bool SkipPassNoSharedArea(List<ZNetPeer> candidates)
        {
            if (!IdleGateOn) return false;
            int n = candidates.Count;
            if (n > s_zones.Length) return false;   // too many to check cheaply: scan as before
            for (int i = 0; i < n; i++) s_zones[i] = ZoneSystem.GetZone(candidates[i].GetRefPos());
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if (Math.Abs(s_zones[i].x - s_zones[j].x) <= SharedAreaZones && Math.Abs(s_zones[i].y - s_zones[j].y) <= SharedAreaZones)
                    {
                        Inc(N.PassesShared);
                        return false;
                    }
            Inc(N.PassesNoShared);
            return true;
        }

        /// <summary>N23 (2) bookkeeping: every creature the balancer visits (owner known) when the idle gate is on.</summary>
        internal static void NoteVisit(ZDO zdo, double now)
        {
            if (!IdleGateOn) return;
            uint rev = zdo.DataRevision;
            if (s_revs.TryGetValue(zdo.m_uid, out Rev r))
            {
                if (r.Last == rev) return;
                r.Last = rev;
                r.ChangedAt = now;
                s_revs[zdo.m_uid] = r;
                return;
            }
            if (s_revs.Count >= MaxTracked) return;   // full: unknown creatures count as idle (no move), never a growing table
            s_revs[zdo.m_uid] = new Rev { Last = rev, ChangedAt = double.NegativeInfinity, BlockedSince = 0.0 };
        }

        /// <summary>N22 hold: the base 30 s, or the doubled hold after a recent balancer move of this creature.</summary>
        internal static double HoldSeconds(ZDOID id, double baseHold, double now)
        {
            if (!HysteresisOn || !s_moves.TryGetValue(id, out Move m) || now - m.At >= ReflipWindowSeconds) return baseHold;
            return m.Hold > baseHold ? m.Hold : baseHold;
        }

        /// <summary>The balancer's hold refused a move that the old 30 s hold would have allowed.</summary>
        internal static void NoteHeldByDoubledHold() => Inc(N.HeldDoubled);

        /// <summary>N22: replaces the 2-pass ping streak. True = the candidate has been better long enough; move now.</summary>
        internal static bool ConfirmPingMove(ZDO zdo, ZNetPeer fastest, float fastestRtt, float ownerRtt, int marginMs, bool ownerStalling, double now)
        {
            ZDOID id = zdo.m_uid;
            bool have = s_confirm.TryGetValue(id, out Confirm c);
            if (fastest == null) { if (have) Drop(id, c, now); return false; }

            float gain = ownerRtt - fastestRtt;
            float margin = marginMs;
            bool returning = s_moves.TryGetValue(id, out Move m) && m.From == fastest.m_uid
                && now - m.At < (ConfigReturnPenaltySeconds != null ? ConfigReturnPenaltySeconds.Value : 120f);
            if (returning) margin *= 2f;
            bool oldEnter = ownerStalling || gain > marginMs;   // the old rule's test (fastestRtt + margin < ownerRtt)
            bool enter = ownerStalling || gain > margin;
            bool keep = ownerStalling || gain > margin * 0.5f;

            if (have && c.Candidate == fastest.m_uid && c.OwnerRev == zdo.OwnerRevision && keep)
            {
                c.Passes++;
                if (oldEnter && c.Passes >= 2) c.OldWouldMove = true;
                double confirm = ConfigConfirmSeconds != null ? ConfigConfirmSeconds.Value : 6f;
                s_confirm[id] = c;
                // Confirmed: kept until NoteMove, so a move the idle gate or the per-owner cap refuses goes the pass it is allowed.
                return now - c.Since >= confirm && enter;
            }
            if (have) Drop(id, c, now);
            if (oldEnter && !enter) Inc(N.ReturnPenalty);   // the old rule would have started (and soon made) this move
            if (!enter || s_confirm.Count >= MaxTracked) return false;
            s_confirm[id] = new Confirm { Candidate = fastest.m_uid, Since = now, OwnerRev = zdo.OwnerRevision, Passes = 1 };
            return false;
        }

        private static void Drop(ZDOID id, Confirm c, double now)
        {
            s_confirm.Remove(id);
            NoteStale(now - c.Since);
            if (c.OldWouldMove) Inc(N.AvoidHyst);
        }

        /// <summary>N23 (2): true = refuse this ping/stall move, the creature is idle.</summary>
        internal static bool IdleBlocks(ZDO zdo, string busy, double now)
        {
            if (!IdleGateOn || busy != null) return false;
            double idleFor = ConfigIdleSeconds != null ? ConfigIdleSeconds.Value : 10f;
            s_revs.TryGetValue(zdo.m_uid, out Rev r);
            bool known = r.ChangedAt != 0.0;   // default struct = not tracked (table full): idle
            if (known && now - r.ChangedAt < idleFor)
            {
                if (r.BlockedSince > 0.0) { NoteStale(now - r.BlockedSince); r.BlockedSince = 0.0; s_revs[zdo.m_uid] = r; }
                return false;
            }
            if (known && r.BlockedSince <= 0.0) { r.BlockedSince = now; s_revs[zdo.m_uid] = r; }
            Inc(N.AvoidIdle);
            return true;
        }

        /// <summary>The balancer asked an owner to hand a creature over (always called; returns at once with both keys off).</summary>
        internal static void NoteMove(ZDOID id, long from, long to, bool forFight, bool touch, double now)
        {
            if (!AnyKeyOn) return;
            if (s_confirm.TryGetValue(id, out Confirm c)) { NoteStale(now - c.Since); s_confirm.Remove(id); }
            Inc(N.Moves);
            Inc(forFight ? N.MovesFight : touch ? N.MovesTouch : N.MovesPing);
            if (!HysteresisOn) return;
            double hold = BaseHoldSeconds;
            if (s_moves.TryGetValue(id, out Move m) && now - m.At < ReflipWindowSeconds) hold = Math.Min(m.Hold * 2.0, MaxHoldSeconds);
            if (s_moves.Count < MaxTracked || s_moves.ContainsKey(id)) s_moves[id] = new Move { From = from, At = now, Hold = hold };
            s_confirm.Remove(id);
        }

        internal static void Prune(ZDOMan zdoMan, double now)
        {
            if (s_confirm.Count == 0 && s_moves.Count == 0 && s_revs.Count == 0 && s_rescues.Count == 0) return;
            s_stale.Clear();
            foreach (var e in s_confirm) if (zdoMan.GetZDO(e.Key) == null || now - e.Value.Since > 600.0) s_stale.Add(e.Key);
            foreach (var id in s_stale) s_confirm.Remove(id);
            s_stale.Clear();
            foreach (var e in s_moves) if (now - e.Value.At >= Math.Max(ReflipWindowSeconds, ConfigReturnPenaltySeconds != null ? ConfigReturnPenaltySeconds.Value : 120f)) s_stale.Add(e.Key);
            foreach (var id in s_stale) s_moves.Remove(id);
            s_stale.Clear();
            foreach (var e in s_revs) if (zdoMan.GetZDO(e.Key) == null) s_stale.Add(e.Key);
            foreach (var id in s_stale) s_revs.Remove(id);
            PruneRescues(now);
        }

        private static void PruneRescues(double now)
        {
            s_stale.Clear();
            foreach (var e in s_rescues) if (now - e.Value.At > RescueFlipBackSeconds) s_stale.Add(e.Key);
            foreach (var id in s_stale) s_rescues.Remove(id);
        }

        internal static void OnShutdown()
        {
            s_confirm.Clear(); s_moves.Clear(); s_revs.Clear(); s_rescues.Clear();
            s_inRelease = 0; s_inZdoData = 0; s_live = false;
            for (int w = 0; w < 2; w++) { Array.Clear(s_n[w], 0, s_n[w].Length); s_maxStale[w] = 0.0; }
        }

        // ------------------------------------------------------------------ census hooks (attached only when a key is on at start)

        [HarmonyPatch(typeof(ZDOMan), "ReleaseNearbyZDOS"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        static void ReleaseBegin() { if (s_live) s_inRelease++; }

        [HarmonyPatch(typeof(ZDOMan), "ReleaseNearbyZDOS"), HarmonyFinalizer]
        static void ReleaseEnd() { if (s_inRelease > 0) s_inRelease--; }

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        static void ZdoDataBegin() { if (s_live) s_inZdoData++; }

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData"), HarmonyFinalizer]
        static void ZdoDataEnd() { if (s_inZdoData > 0) s_inZdoData--; }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwnerInternal)), HarmonyPrefix]
        static void OwnerBefore(ZDO __instance, out long __state)
        {
            __state = s_live ? __instance.GetOwner() : 0L;
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwnerInternal)), HarmonyPostfix]
        static void OwnerAfter(ZDO __instance, long uid, long __state)
        {
            // 0 = key off / not a dedicated server, or a new / released object (vanilla's release to 0 is not a flip)
            if (__state == 0L || uid == 0L || __state == uid) return;
            try { Observe(__instance, __state, uid); }
            catch (Exception ex) { LoggerOptions.LogDebug($"[OwnershipSteady] census threw {ex.GetType().Name}: {ex.Message}; ownership itself is untouched."); }
        }

        private static void Observe(ZDO zdo, long from, long to)
        {
            if (s_inZdoData > 0)
            {
                // A peer's own change: vanilla claim on interaction (doors use RPCs; containers, pickups, ships, carts, saddles, stands,
                // signs, fireplaces, cooking stations, beds claim), a spawn, or a client carrying out FGN's hand-off.
                if (CreatureOwnership.IsPendingTarget(zdo.m_uid, to)) Inc(N.BalancerLanded);
                else Inc(N.ClaimsRemote);
                return;
            }
            if (s_inRelease > 0)
            {
                Inc(N.Rescues);
                double now = Time.realtimeSinceStartupAsDouble;
                if (s_rescues.TryGetValue(zdo.m_uid, out Rescue last) && last.From == to && last.To == from && now - last.At <= RescueFlipBackSeconds)
                    Inc(N.RescueFlipBacks);
                if (s_rescues.Count >= MaxTracked) PruneRescues(now);
                if (s_rescues.Count < MaxTracked) s_rescues[zdo.m_uid] = new Rescue { From = from, To = to, At = now };
                return;
            }
            Inc(N.OtherServer);   // server-side SetOwner outside the release pass (helm, resources, API hand-offs done here)
        }

        // ------------------------------------------------------------------ Debug lines

        private static void Flush(int w, string label, double now)
        {
            int[] n = s_n[w];
            double maxStale = s_maxStale[w];
            if (w == 0 && s_confirm.Count > 0)
                foreach (var e in s_confirm) if (now - e.Value.Since > maxStale) maxStale = now - e.Value.Since;   // still waiting
            if (LoggerOptions.DebugEnabled)
            {
                bool any = false;
                for (int i = 0; i < n.Length; i++) if (n[i] != 0) { any = true; break; }
                if (any || maxStale > 0.0)
                {
                    var sb = new StringBuilder(320);
                    sb.Append("[OwnershipSteady] last ").Append(label).Append(": balancer moves ").Append(n[(int)N.Moves])
                      .Append(" (ping ").Append(n[(int)N.MovesPing]).Append(", fight ").Append(n[(int)N.MovesFight]).Append(", touch ").Append(n[(int)N.MovesTouch])
                      .Append("), landed ").Append(n[(int)N.BalancerLanded])
                      .Append("; avoided by hysteresis ").Append(n[(int)N.AvoidHyst])
                      .Append(" (return penalty ").Append(n[(int)N.ReturnPenalty]).Append(", passes held past 30 s ").Append(n[(int)N.HeldDoubled])
                      .Append("); avoided by idle gate ").Append(n[(int)N.AvoidIdle])
                      .Append(", passes skipped (no shared area) ").Append(n[(int)N.PassesNoShared]).Append(" of ").Append(n[(int)N.PassesNoShared] + n[(int)N.PassesShared])
                      .Append("; max staleness ").Append(maxStale.ToString("F1")).Append(" s")
                      .Append("; passed straight through: client claims ").Append(n[(int)N.ClaimsRemote])
                      .Append(", fight/touch moves ").Append(n[(int)N.MovesFight] + n[(int)N.MovesTouch])
                      .Append("; vanilla release-pass moves ").Append(n[(int)N.Rescues]).Append(" (flip-backs <= 10 s ").Append(n[(int)N.RescueFlipBacks])
                      .Append("), other server moves ").Append(n[(int)N.OtherServer])
                      .Append("; tracked ").Append(s_confirm.Count).Append('/').Append(s_moves.Count).Append('/').Append(s_revs.Count).Append('.');
                    LoggerOptions.LogDebug(sb.ToString());
                }
            }
            Array.Clear(n, 0, n.Length);
            s_maxStale[w] = 0.0;
        }
    }
}
