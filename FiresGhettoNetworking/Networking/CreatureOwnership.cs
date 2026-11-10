using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Moves a creature to the best-placed player near it: the one fighting it, otherwise one with clearly lower ping. The
    /// server only chooses - the current owner's game pushes its latest state and hands ownership over itself, as vanilla does
    /// when someone opens a chest - and the old owner forwards hits that were already on their way to it.
    /// </summary>
    [HarmonyPatch]
    public static class CreatureOwnership
    {
        public static ConfigEntry<bool> ConfigEnabled;
        public static ConfigEntry<int> ConfigPingMarginMs;
        // 1.6.84 ([fgn], Fire 11-09: "It should be configurable ... let vanilla ownership run or hand off to the lowest ping player"). Applies with Balance Creature Ownership on and
        // server-side creature ownership off. Vanilla = a fighting creature is never moved (as before). LowestPing = fights do not block the ping hand-off. FirstFighter = a creature in a fight
        // goes to the first player who hit it in that fight and stays there until the fight ends.
        public enum FightMode { Vanilla, LowestPing, FirstFighter }
        public static ConfigEntry<FightMode> ConfigFightMode;
        private static readonly Dictionary<ZDOID, Stamp> s_firstFighter = new Dictionary<ZDOID, Stamp>();

        internal static bool ServerOwnsCreatures;

        private const string HandOffRpc = "FGN_HandOff";
        private const string ResultRpc = "FGN_HandOffResult";
        private const double PassSeconds = 2.0;
        private const double MinHoldSeconds = 30.0;   // hysteresis: no creature changes owner more often than this
        private const double SkipLogSeconds = 10.0;
        private const double FightMemorySeconds = 6.0;
        private const double PendingSeconds = 5.0;
        private const double DeclinedCooldownSeconds = 6.0;
        private const double ForwardHitsSeconds = 5.0;
        private const double PruneSeconds = 30.0;
        private const float FighterPingAllowanceMs = 250f;
        private const float UnmeasuredRttMs = 400f;
        private const int WinsBeforePingMove = 2;
        private const int MaxHandOffsPerPass = 16;
        private const int MaxHandOffsPerOwner = 6;
        private const int MaxTrackedHits = 256;
        private const int DefaultPingMarginMs = 40;

        private static readonly int s_damageHash = "RPC_Damage".GetStableHashCode();
        private static readonly int s_vikHavnHoldEndsHash = "VikHavnCombat_HoldEndsMs".GetStableHashCode();

        private struct Owned
        {
            public long Owner;
            public double Since;
        }

        private struct Streak
        {
            public long Candidate;
            public int Wins;
        }

        private struct Stamp
        {
            public long Peer;
            public double At;
        }

        private static readonly Dictionary<ZDOID, Owned> s_owned = new Dictionary<ZDOID, Owned>();
        private static readonly Dictionary<ZDOID, Streak> s_streaks = new Dictionary<ZDOID, Streak>();
        private static readonly Dictionary<ZDOID, Stamp> s_lastHitBy = new Dictionary<ZDOID, Stamp>();
        private static readonly Dictionary<ZDOID, Stamp> s_pending = new Dictionary<ZDOID, Stamp>();
        private static readonly Dictionary<ZDOID, double> s_cooldowns = new Dictionary<ZDOID, double>();
        private static readonly Dictionary<ZDOID, Stamp> s_handedOff = new Dictionary<ZDOID, Stamp>();
        private static readonly Dictionary<ZDOID, double> s_localHits = new Dictionary<ZDOID, double>();
        private static readonly Dictionary<long, int> s_perOwner = new Dictionary<long, int>();
        private static readonly Dictionary<string, int> s_declineReasons = new Dictionary<string, int>();
        private static readonly HashSet<ZDOID> s_visited = new HashSet<ZDOID>();
        private static readonly List<ZNetPeer> s_candidates = new List<ZNetPeer>();
        private static readonly List<ZDOID> s_stale = new List<ZDOID>();
        private static readonly HashSet<int> s_creaturePrefabs = new HashSet<int>();
        private static int s_creatureTypeMask;
        private static bool s_prefabsScanned;
        private static double s_nextPass;
        private static double s_nextPrune;
        private static int s_toFighters, s_toLowerPing, s_accepted, s_declined;

        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> s_objectsBySector
            = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");
        private static readonly AccessTools.FieldRef<Character, ZNetView> s_characterView
            = AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("10 - Server Authority", "Balance Creature Ownership", false,
                Cfg.Adv("Hands idle creatures to the nearby player with clearly lower ping; a creature in a fight is never moved."));
            ConfigPingMarginMs = config.Bind("10 - Server Authority", "Ownership Ping Margin", 40,
                Cfg.Adv("How much lower (ms) a player's ping must be to take over an idle creature.",
                    new AcceptableValueRange<int>(10, 300)));
            ConfigFightMode = config.Bind("10 - Server Authority", "Creature Ownership In Fights", FightMode.Vanilla,
                Cfg.Adv("Who runs a creature that is fighting, with Balance Creature Ownership on and server-side creature ownership off. Vanilla: it stays with whoever has it. LowestPing: it may move to the nearby player with clearly lower ping, fight or not. FirstFighter: it moves to the first player who hit it and stays there until the fight ends."));
        }

        internal static void OnNewConnection(ZNet __instance, ZNetPeer peer)
        {
            if (peer?.m_rpc == null) return;
            if (__instance.IsServer()) peer.m_rpc.Register<ZDOID, bool, string>(ResultRpc, OnResult);
            else peer.m_rpc.Register<ZDOID, long>(HandOffRpc, OnHandOff);
            HelmOwnership.RegisterPeerRpcs(__instance, peer);
        }

        internal static void OnShutdown()
        {
            s_owned.Clear();
            s_streaks.Clear();
            s_lastHitBy.Clear(); s_firstFighter.Clear();
            s_pending.Clear();
            s_askedOwner.Clear();
            s_cooldowns.Clear();
            s_handedOff.Clear();
            s_localHits.Clear();
            s_creaturePrefabs.Clear();
            s_prefabsScanned = false;
            GhostOwner169.OnShutdown();   // 1.6.69 ([ghost])
            OwnershipSteady.OnShutdown();   // [wishbone] ownstab
        }

        /// <summary>[wishbone] ownstab census: is this owner change the hand-off the balancer asked for (sent less than 5 s ago)?</summary>
        internal static bool IsPendingTarget(ZDOID id, long target)
            => s_pending.TryGetValue(id, out var p) && p.Peer == target && Time.realtimeSinceStartupAsDouble - p.At < PendingSeconds;

        internal static void ObserveRoutedRpc(ZRoutedRpc.RoutedRPCData data)
        {
            if (data.m_methodHash != s_damageHash || data.m_targetZDO.IsNone() || !Active()) return;
            double at = Time.realtimeSinceStartupAsDouble;
            s_lastHitBy[data.m_targetZDO] = new Stamp { Peer = data.m_senderPeerID, At = at };
            // 1.6.84: the first player to hit it in this fight is kept while hits keep coming
            if (s_firstFighter.TryGetValue(data.m_targetZDO, out var first) && at - first.At <= FightMemorySeconds) s_firstFighter[data.m_targetZDO] = new Stamp { Peer = first.Peer, At = at };
            else s_firstFighter[data.m_targetZDO] = new Stamp { Peer = data.m_senderPeerID, At = at };
        }

        internal static string Report()
        {
            var sb = new StringBuilder();
            sb.Append("creature ownership ").Append(Active() ? "on" : ServerOwnsCreatures ? "off (server-side simulation owns creatures)" : "off")
              .Append(": ").Append(s_toFighters).Append(" handed to the player fighting them, ")
              .Append(s_toLowerPing).Append(" to lower ping, ").Append(s_accepted).Append(" accepted, ").Append(s_declined).Append(" declined");
            if (s_declineReasons.Count > 0)
            {
                sb.Append(" (");
                bool first = true;
                foreach (var reason in s_declineReasons)
                {
                    if (!first) sb.Append(", ");
                    sb.Append(reason.Key).Append(' ').Append(reason.Value);
                    first = false;
                }
                sb.Append(')');
            }
            s_toFighters = s_toLowerPing = s_accepted = s_declined = 0;
            s_declineReasons.Clear();
            return sb.ToString();
        }

        private static bool Active()
        {
            return ConfigEnabled != null && ConfigEnabled.Value && !ServerOwnsCreatures
                && ZNet.instance != null && ZNet.instance.IsDedicated();
        }

        internal static void Balance(ZDOMan __instance)
        {
            HelmOwnership.ReturnEmptyShipsToServer(__instance);
            GhostOwner169.HelmFallbackTick();   // 1.6.69 ([ghost]): nothing to do unless a helm request is pending
            OwnershipSteady.Tick();   // [wishbone] ownstab: census flush; one bool read with its keys off
            if (!Active()) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < s_nextPass) return;
            s_nextPass = now + PassSeconds;
            if (!s_prefabsScanned) ScanPrefabs(); else ScanNewPrefabs();   // 1.6.27
            if (s_creaturePrefabs.Count == 0) return;

            s_candidates.Clear();
            foreach (var peer in ZNet.instance.GetPeers())
                if (peer != null && peer.IsReady() && peer.GetRefPos() != Vector3.zero) s_candidates.Add(peer);
            if (s_candidates.Count >= 2 && !OwnershipSteady.SkipPassNoSharedArea(s_candidates)) BalanceCreaturesNearPlayers(__instance, now);   // [wishbone] ownstab N23: no scan while no two players can share an area (key off = false)
            if (now >= s_nextPrune) { Prune(__instance, now); OwnershipSteady.Prune(__instance, now); }
        }

        private static void BalanceCreaturesNearPlayers(ZDOMan zdoMan, double now)
        {
            var sectors = s_objectsBySector(zdoMan);
            s_visited.Clear();
            s_perOwner.Clear();
            int handOffs = 0;
            foreach (var candidate in s_candidates)
            {
                Vector2s zone = FgnRing.Sector(candidate.GetRefPos());
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        var objects = sectors[(int)ZoneSystem.SectorToIndex(zone.x + dx, zone.y + dy).Sector];
                        if (objects == null) continue;
                        for (int i = 0; i < objects.Count; i++)
                        {
                            var zdo = objects[i];
                            if ((s_creatureTypeMask & (1 << (int)zdo.Type)) == 0) continue;
                            if (!s_creaturePrefabs.Contains(zdo.GetPrefab()) || ServerOwnershipPatchesV3.IsExcluded(zdo.GetPrefab()) || !s_visited.Add(zdo.m_uid)) continue;   // client-run creatures are never balanced
                            if (TryHandOffCreature(zdo, now) && ++handOffs >= MaxHandOffsPerPass) return;
                        }
                    }
                }
            }
        }

        private static bool TryHandOffCreature(ZDO zdo, double now)
        {
            long owner = zdo.GetOwner();
            if (owner == 0L) return false;
            var id = zdo.m_uid;
            if (!s_owned.TryGetValue(id, out var owned) || owned.Owner != owner)
            {
                s_owned[id] = new Owned { Owner = owner, Since = now };
                s_streaks.Remove(id);
                owned = s_owned[id];
            }
            OwnershipSteady.NoteVisit(zdo, now);   // [wishbone] ownstab N23: revision watch for the idle test (key off = one bool read)

            var ownerPeer = ZNet.instance.GetPeer(owner);
            // FGN_HandOff's payload is FGN's own, so only a peer on our FGN build (FgnVersionGate) is asked or handed to.
            if (ownerPeer == null || !ownerPeer.IsReady() || !FgnVersionGate.IsMatched(ownerPeer)) return false;
            if (s_pending.TryGetValue(id, out var pending) && now - pending.At < PendingSeconds) return false;
            if (s_cooldowns.TryGetValue(id, out var cooldownEnds) && now < cooldownEnds) return false;
            if (zdo.GetBool(ZDOVars.s_tamed) || zdo.GetLong(ZDOVars.s_user) != 0L || IsHeld(zdo)) return false;

            Vector3 position = zdo.GetPosition();
            if (!ZNetScene.InActiveArea(position, ownerPeer.GetRefPos())) return false;

            // Group fights look out of sync when creatures hop between owners mid-fight (to whoever hit them
            // last, or to a lower ping). A creature that is fighting is never moved, and none moves more than once per
            // MinHoldSeconds (s_owned.Since restarts on every owner change), so it only changes hands while idle.
            string busy = CombatReason(zdo, now);
            FightMode mode = ConfigFightMode != null ? ConfigFightMode.Value : FightMode.Vanilla;
            ZNetPeer fighter = null;   // 1.6.84: set only in FirstFighter mode; before, it was never set and the fighter branch below was unreachable
            if (busy != null && mode == FightMode.FirstFighter)
            {
                if (s_firstFighter.TryGetValue(id, out var first)) fighter = ZNet.instance.GetPeer(first.Peer);
                if (fighter == null || fighter.m_uid == owner) { NoteSkip(fighter == null ? "fight, first fighter unknown or gone" : "fight, held by its first fighter", zdo, now); return false; }
            }
            bool fightAllowed = busy == null || mode != FightMode.Vanilla;
            bool holdApplies = !(busy != null && mode == FightMode.FirstFighter);   // a fight's first hand-off goes now, not after 30 s
            double hold = OwnershipSteady.HysteresisOn ? OwnershipSteady.HoldSeconds(id, MinHoldSeconds, now) : MinHoldSeconds;   // [wishbone] ownstab N22: doubling hold
            if (!fightAllowed || (holdApplies && now - owned.Since < hold))
            {
                s_streaks.Remove(id);
                if (fightAllowed && now - owned.Since >= MinHoldSeconds) OwnershipSteady.NoteHeldByDoubledHold();
                NoteSkip(!fightAllowed ? busy : now - owned.Since < MinHoldSeconds ? "held < 30 s" : "held (doubled hold)", zdo, now);
                return false;
            }

            float ownerRtt = Rtt(ownerPeer);
            ZNetPeer fastest = null;
            float fastestRtt = float.MaxValue;
            int present = 0;
            bool fighterPresent = false;
            foreach (var candidate in s_candidates)
            {
                if (!ZNetScene.InActiveArea(position, candidate.GetRefPos())) continue;
                present++;
                if (candidate == ownerPeer) continue;
                if (candidate == fighter) fighterPresent = true;
                if (!FgnVersionGate.IsMatched(candidate)) continue;
                if (PeerStalls.IsStalling(candidate.m_uid)) continue;   // 1.6.69: a struggling game is not handed idle creatures
                float rtt = Rtt(candidate);
                if (rtt >= fastestRtt) continue;
                fastest = candidate;
                fastestRtt = rtt;
            }
            if (present < 2) return false;
            ZNetPeer toucher = GhostOwner169.Toucher(zdo, ownerPeer, s_candidates);   // 1.6.69 ([ghost]): standing on it (key off by default)

            ZNetPeer target = null;
            bool forFight = false;
            if (fighterPresent && (mode == FightMode.FirstFighter || Rtt(fighter) <= ownerRtt + FighterPingAllowanceMs))
            {
                target = fighter;
                forFight = true;
            }
            else if (toucher != null)
            {
                target = toucher;
                s_streaks.Remove(id);
            }
            // [wishbone] ownstab N22: time-confirmed streak with a dead band and a return penalty instead of the 2-pass streak
            else if (OwnershipSteady.HysteresisOn)
            {
                s_streaks.Remove(id);
                if (OwnershipSteady.ConfirmPingMove(zdo, fastest, fastestRtt, ownerRtt, PingMarginMs(), PeerStalls.IsStalling(owner), now)) target = fastest;
            }
            // 1.6.69: an idle creature run by a struggling game may go to a healthy nearby player without the ping margin (same streak / hold rules)
            else if (fastest != null && (fastestRtt + PingMarginMs() < ownerRtt || PeerStalls.IsStalling(owner)))
            {
                s_streaks.TryGetValue(id, out var streak);
                streak = streak.Candidate == fastest.m_uid ? new Streak { Candidate = fastest.m_uid, Wins = streak.Wins + 1 } : new Streak { Candidate = fastest.m_uid, Wins = 1 };
                s_streaks[id] = streak;
                if (streak.Wins >= WinsBeforePingMove) target = fastest;
            }
            else
            {
                s_streaks.Remove(id);
            }
            if (target == null) return false;
            bool byTouch = !forFight && target == toucher;
            // [wishbone] ownstab N23: a ping / stall move never takes an idle creature; fights and touch pass straight through
            if (!forFight && !byTouch && OwnershipSteady.IdleBlocks(zdo, busy, now)) return false;

            s_perOwner.TryGetValue(owner, out int fromOwner);
            if (fromOwner >= MaxHandOffsPerOwner) return false;
            s_perOwner[owner] = fromOwner + 1;

            NoteAsked(id, ownerPeer.m_uid);
            ownerPeer.m_rpc.Invoke(HandOffRpc, id, target.m_uid);
            s_pending[id] = new Stamp { Peer = target.m_uid, At = now };
            s_streaks.Remove(id);
            OwnershipSteady.NoteMove(id, owner, target.m_uid, forFight, byTouch, now);   // [wishbone] ownstab (returns at once with its keys off)
            if (forFight) s_toFighters++;
            else s_toLowerPing++;
            return true;
        }

        /// <summary>Why a creature counts as fighting (null = idle). The server reads what every owner writes to the ZDO
        /// (BaseAI 'alert', MonsterAI 'haveTarget'), the hits it routes (s_lastHitBy), and the live Character when it has one.</summary>
        private static string CombatReason(ZDO zdo, double now)
        {
            if (s_lastHitBy.TryGetValue(zdo.m_uid, out var hit) && now - hit.At <= FightMemorySeconds) return "hit recently";
            if (zdo.GetBool(ZDOVars.s_haveTargetHash)) return "has a target";
            if (zdo.GetBool(ZDOVars.s_alert)) return "alerted";
            var view = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
            if (view == null) return null;
            var character = view.GetComponent<Character>();
            if (character != null && character.InAttack()) return "attacking";
            var monster = view.GetComponent<MonsterAI>();
            if (monster != null && monster.GetTargetCreature() != null) return "has a target";
            return null;
        }

        private static readonly Dictionary<string, int> s_skipReasons = new Dictionary<string, int>();
        private static double s_nextSkipLog;

        private static void NoteSkip(string reason, ZDO zdo, double now)
        {
            if (!LoggerOptions.DebugEnabled) return;
            // Only the creatures a move was otherwise possible for (owner present, 2+ players near) reach here.
            s_skipReasons.TryGetValue(reason, out int count);
            s_skipReasons[reason] = count + 1;
            if (now < s_nextSkipLog) return;
            s_nextSkipLog = now + SkipLogSeconds;
            var sb = new StringBuilder("[CreatureOwnership] Kept with their owner (no mid-fight / rapid moves) in the last 10 s:");
            foreach (var entry in s_skipReasons) sb.Append(' ').Append(entry.Key).Append(' ').Append(entry.Value).Append(',');
            sb.Length--;
            sb.Append($". Latest: prefab {zdo.GetPrefab()} owner {zdo.GetOwner()} ({reason}).");
            LoggerOptions.LogDebug(sb.ToString());
            s_skipReasons.Clear();
        }

        private static bool IsHeld(ZDO zdo)
        {
            long holdEndsMs = zdo.GetLong(s_vikHavnHoldEndsHash);
            return holdEndsMs != 0L && ZNet.instance != null && holdEndsMs > (long)(ZNet.instance.GetTimeSeconds() * 1000.0);
        }

        private static float Rtt(ZNetPeer peer)
        {
            if (ConnectionEcho.TryRtt(peer, out float echo)) return echo;
            int ping = LinkController.PingMs(peer);
            return ping > 0 ? ping : UnmeasuredRttMs;
        }

        private static int PingMarginMs() => ConfigPingMarginMs != null ? ConfigPingMarginMs.Value : DefaultPingMarginMs;

        private const int MaxReasonLength = 64;

        private const double AskedOwnerSeconds = 60.0;
        private static readonly Dictionary<ZDOID, Stamp> s_askedOwner = new Dictionary<ZDOID, Stamp>();
        private static float s_nextForgedLog;

        private static void NoteAsked(ZDOID id, long owner)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (s_askedOwner.Count > MaxTrackedHits) RemoveOlderThan(s_askedOwner, now - AskedOwnerSeconds);
            s_askedOwner[id] = new Stamp { Peer = owner, At = now };
        }

        private static bool AnsweredByAskedOwner(ZRpc rpc, ZDOID id)
        {
            if (ZNet.instance == null || !s_askedOwner.TryGetValue(id, out Stamp asked)) return false;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                if (peer != null && peer.m_rpc == rpc) return peer.m_uid == asked.Peer;
            return false;
        }

        private static void OnResult(ZRpc rpc, ZDOID id, bool accepted, string reason)
        {
            // 1.6.31: any client could answer for any hand-off, before the checks and the length cap. Only the owner that was asked counts.
            if (reason != null && reason.Length > MaxReasonLength) reason = reason.Substring(0, MaxReasonLength);
            if (!AnsweredByAskedOwner(rpc, id))
            {
                float now = Time.unscaledTime;
                if (now >= s_nextForgedLog)
                {
                    s_nextForgedLog = now + 60f;
                    LoggerOptions.LogDebug($"[CreatureOwnership] ignored a hand-off answer for {id}: the sender is not the owner this server asked");
                }
                return;
            }
            s_askedOwner.Remove(id);
            Api.FgnApi.NotifyResult(id, accepted, reason);   // hand-offs an add-on asked for (FgnApi)
            if (accepted)
            {
                s_accepted++;
                return;
            }
                        if (!s_pending.ContainsKey(id)) return;   // not a hand-off this peer asked for
s_declined++;
            s_pending.Remove(id);
            s_cooldowns[id] = Time.realtimeSinceStartupAsDouble + DeclinedCooldownSeconds;
            string key = string.IsNullOrEmpty(reason) ? "other" : reason;
            s_declineReasons.TryGetValue(key, out int count);
            s_declineReasons[key] = count + 1;
        }

        private static void OnHandOff(ZRpc rpc, ZDOID id, long newOwner)
        {
            var zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(id) : null;
            string decline = Veto(zdo, newOwner);
            if (decline == null)
            {
                ZDOMan.instance.ForceSendZDO(id);
                zdo.SetOwner(newOwner);
                if (s_handedOff.Count > MaxTrackedHits) PruneHandedOff();
                s_handedOff[id] = new Stamp { Peer = newOwner, At = Time.realtimeSinceStartupAsDouble };
            }
            rpc.Invoke(ResultRpc, id, decline == null, decline ?? "");
        }

        /// <summary>FGN's own checks for a hand-off an add-on asks for; null = fine, else the reason.</summary>
        internal static string ApiCheck(ZDOID id, long target)
        {
            if (ZNet.instance == null || ZDOMan.instance == null) return "no world";
            ZDO zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null) return "gone";
            long owner = zdo.GetOwner();
            if (owner == 0L) return "no owner";
            if (target == 0L) return "bad target";
            if (target == owner) return "already the owner";
            long me = ZDOMan.GetSessionID();
            if (target != me)
            {
                ZNetPeer tp = ZNet.instance.GetPeer(target);
                if (tp == null || !tp.IsReady()) return "target not connected";
                if (!FgnVersionGate.IsMatched(tp)) return "target not on this FGN build";
            }
            if (owner != me)
            {
                ZNetPeer op = ZNet.instance.GetPeer(owner);
                if (op == null || !op.IsReady()) return "owner not connected";
                if (!FgnVersionGate.IsMatched(op)) return "owner not on this FGN build";
            }
            if (zdo.GetBool(ZDOVars.s_tamed)) return "tamed";
            if (zdo.GetLong(ZDOVars.s_user) != 0L) return "in use";
            if (GhostOwner169.InUse(zdo)) return "open (in use)";   // 1.6.69 ([ghost]): never hand an open chest / occupied station
            if (IsHeld(zdo)) return "held";
            if (zdo.GetBool(ServerOwnershipPatches.ServerKeepsKey)) return "server keeps";   // 1.6.24: same flag as the V2/V3 rule
            if (ValcastPossession.IsPossessed(zdo)) return "possessed";
            return null;
        }

        /// <summary>Starts a hand-off on FGN's own protocol; null = sent (or done here), else why not.</summary>
        internal static string ApiHandOff(ZDOID id, long target)
        {
            ZDO zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null) return "gone";
            long owner = zdo.GetOwner();
            if (owner == ZDOMan.GetSessionID())
            {
                string decline = Veto(zdo, target);
                if (decline == null) { ZDOMan.instance.ForceSendZDO(id); zdo.SetOwner(target); }
                Api.FgnApi.NotifyResult(id, decline == null, decline);
                return null;
            }
            ZNetPeer ownerPeer = ZNet.instance.GetPeer(owner);
            if (ownerPeer == null) return "owner not connected";
            NoteAsked(id, ownerPeer.m_uid);
            ownerPeer.m_rpc.Invoke(HandOffRpc, id, target);
            return null;
        }

        private static string Veto(ZDO zdo, long newOwner)
        {
            if (zdo == null) return "gone";
            if (!zdo.IsOwner()) return "not owner";
            if (newOwner == 0L || newOwner == ZDOMan.GetSessionID()) return "bad target";
            if (s_localHits.TryGetValue(zdo.m_uid, out double hitAt) && Time.realtimeSinceStartupAsDouble - hitAt <= FightMemorySeconds)
                return "owner fighting";
            if (IsHeld(zdo)) return "held";
            if (GhostOwner169.InUse(zdo)) return "open (in use)";   // 1.6.69 ([ghost])
            if (zdo.GetBool(ServerOwnershipPatches.ServerKeepsKey)) return "server keeps";   // 1.6.24: same flag as the V2/V3 rule
            if (ValcastPossession.IsPossessed(zdo)) return "possessed";   // FiresValcast creature possession
            var view = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
            if (view == null) return null;
            var character = view.GetComponent<Character>();
            if (character != null)
            {
                if (character.IsDead()) return "dead";
                if (character.IsBoss()) return "boss";
                if (character.IsTamed()) return "tamed";
                if (character.InAttack()) return "attacking";
                if (character.IsStaggering()) return "staggering";
            }
            var saddle = view.GetComponentInChildren<Sadle>();
            if (saddle != null && saddle.HaveValidUser()) return "ridden";
            return null;
        }

        [HarmonyPatch(typeof(Character), "RPC_Damage"), HarmonyPrefix]
        static bool OnDamage(Character __instance, long sender, HitData hit)
        {
            long perf = FgnPerf.Begin();
            bool runOriginal = HandleDamage(__instance, sender, hit);
            FgnPerf.End(FgnPerf.Point.CreatureDamage, perf);
            return runOriginal;
        }

        private static bool HandleDamage(Character __instance, long sender, HitData hit)
        {
            if (hit == null) return true;
            // [wishbone] handoff (N6): owner-change hit forwarding/holding, also on a dedicated server (before the local player check). Off = one bool read.
            if (FightHandoff.On && !FightHandoff.OnDamage(__instance, sender, hit)) return false;
            if (Player.m_localPlayer == null) return true;
            // This is FGN's one Character.RPC_Damage hook; the PvP lag-fair dodge rides on it.
            if (LagFairDodge.Forgive(__instance, hit)) return false;
            var view = s_characterView(__instance);
            if (view == null || !view.IsValid()) return true;
            var zdo = view.GetZDO();
            double now = Time.realtimeSinceStartupAsDouble;
            if (hit.GetAttacker() == Player.m_localPlayer)
            {
                if (s_localHits.Count > MaxTrackedHits) PruneLocalHits(now);
                s_localHits[zdo.m_uid] = now;
                return true;
            }
            if (view.IsOwner() || !s_handedOff.TryGetValue(zdo.m_uid, out var handOff)) return true;
            if (now - handOff.At > ForwardHitsSeconds || zdo.GetOwner() != handOff.Peer) return true;
            view.InvokeRPC(handOff.Peer, "RPC_Damage", hit);
            return false;
        }

        private static void PruneLocalHits(double now)
        {
            s_stale.Clear();
            foreach (var entry in s_localHits)
                if (now - entry.Value > FightMemorySeconds) s_stale.Add(entry.Key);
            foreach (var id in s_stale) s_localHits.Remove(id);
        }

        // 1.6.27 (Fire 10-31 "Fix it"): prefabs another mod adds to ZNetScene.m_prefabs after the first scan were never balanced. The
        // list only grows (mods Add to it directly, there is no event), so each pass compares its count and scans just the new tail.
        private static int s_scannedCount;

        private static void ScanNewPrefabs()
        {
            var list = ZNetScene.instance != null ? ZNetScene.instance.m_prefabs : null;
            if (list == null || list.Count <= s_scannedCount) return;
            int before = s_creaturePrefabs.Count;
            for (int i = s_scannedCount; i < list.Count; i++)
            {
                var prefab = list[i];
                if (prefab == null) continue;
                var character = prefab.GetComponent<Character>();
                var view = prefab.GetComponent<ZNetView>();
                if (character == null || view == null || prefab.GetComponent<BaseAI>() == null || character is Player || character.IsBoss()) continue;
                int hash = prefab.name.GetStableHashCode();
                if (ServerOwnershipPatchesV3.IsExcluded(hash) || !s_creaturePrefabs.Add(hash)) continue;
                s_creatureTypeMask |= 1 << (int)view.m_type;
                LoggerOptions.LogDebug($"[CreatureOwnership] late prefab picked up: {prefab.name} (registered after the first scan)");
            }
            s_scannedCount = list.Count;
            if (s_creaturePrefabs.Count > before && !s_lateInfoLogged)
            {
                s_lateInfoLogged = true;
                LoggerOptions.LogDebug($"[CreatureOwnership] {s_creaturePrefabs.Count - before} creature prefab(s) registered late by other mods are now balanced too");
            }
        }
        private static bool s_lateInfoLogged;

        private static void ScanPrefabs()
        {
            if (ZNetScene.instance == null) return;
            s_prefabsScanned = true;
            s_creaturePrefabs.Clear();
            s_creatureTypeMask = 0;
            s_scannedCount = ZNetScene.instance.m_prefabs.Count;
            int bosses = 0;
            foreach (var prefab in ZNetScene.instance.m_prefabs)
            {
                if (prefab == null) continue;
                var character = prefab.GetComponent<Character>();
                var view = prefab.GetComponent<ZNetView>();
                if (character == null || view == null || prefab.GetComponent<BaseAI>() == null || character is Player) continue;
                if (character.IsBoss())
                {
                    bosses++;
                    continue;
                }
                // Creatures left to the clients (FgnApi.ExcludeFromServerSimulation) are not balanced either: their owner is chosen
                // elsewhere, a load-based move would trade them every pass and each hand-over rebuilds visuals and resets AI.
                // Also checked per ZDO above for late registrations.
                if (ServerOwnershipPatchesV3.IsExcluded(prefab.name.GetStableHashCode())) continue;
                s_creaturePrefabs.Add(prefab.name.GetStableHashCode());
                s_creatureTypeMask |= 1 << (int)view.m_type;
            }
            LoggerOptions.LogDebug($"[CreatureOwnership] {s_creaturePrefabs.Count} creature prefabs can be handed between players ({bosses} bosses never are).");
        }

        private static void Prune(ZDOMan zdoMan, double now)
        {
            s_nextPrune = now + PruneSeconds;
            s_stale.Clear();
            foreach (var entry in s_owned)
                if (zdoMan.GetZDO(entry.Key) == null) s_stale.Add(entry.Key);
            foreach (var id in s_stale)
            {
                s_owned.Remove(id);
                s_streaks.Remove(id);
            }
            RemoveOlderThan(s_lastHitBy, now - FightMemorySeconds);
            RemoveOlderThan(s_firstFighter, now - FightMemorySeconds);
            RemoveOlderThan(s_pending, now - PendingSeconds);
            s_stale.Clear();
            foreach (var entry in s_cooldowns)
                if (entry.Value < now) s_stale.Add(entry.Key);
            foreach (var id in s_stale) s_cooldowns.Remove(id);
        }

        private static void PruneHandedOff() => RemoveOlderThan(s_handedOff, Time.realtimeSinceStartupAsDouble - ForwardHitsSeconds);

        private static void RemoveOlderThan(Dictionary<ZDOID, Stamp> stamps, double cutoff)
        {
            s_stale.Clear();
            foreach (var entry in stamps)
                if (entry.Value.At < cutoff) s_stale.Add(entry.Key);
            foreach (var id in s_stale) stamps.Remove(id);
        }
    }
}
