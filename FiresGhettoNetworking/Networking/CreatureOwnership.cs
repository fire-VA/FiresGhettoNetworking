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

        internal static bool ServerOwnsCreatures;

        private const string HandOffRpc = "FGN_HandOff";
        private const string ResultRpc = "FGN_HandOffResult";
        private const double PassSeconds = 2.0;
        private const double MinHoldSeconds = 10.0;
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
            ConfigEnabled = config.Bind("10 - Server Authority", "Balance Creature Ownership", true,
                "Vanilla gives a creature to whichever nearby player the server checks first and keeps it there. With this on, a\n" +
                "creature being fought moves to the player fighting it, otherwise to a nearby player with clearly lower ping, so its\n" +
                "movement, attacks and the hits on it are worked out on the best-placed machine. The current owner's game hands the\n" +
                "creature over itself, as vanilla does with a chest someone opens, so no update in flight can undo the move, and hits\n" +
                "already on their way are passed on. Bosses, tames, ridden creatures and creatures mid-attack are never moved.\n" +
                "Off while Server-Side Simulation owns creatures. DEDICATED SERVER; only players with FGN hand creatures over.");
            ConfigPingMarginMs = config.Bind("10 - Server Authority", "Ownership Ping Margin", 40,
                new ConfigDescription("How much lower, in ms, a nearby player's ping must be before a creature nobody is fighting moves to them.",
                    new AcceptableValueRange<int>(10, 300)));
        }

        [HarmonyPatch(typeof(ZNet), "OnNewConnection"), HarmonyPostfix]
        static void OnNewConnection(ZNet __instance, ZNetPeer peer)
        {
            if (peer?.m_rpc == null) return;
            if (__instance.IsServer()) peer.m_rpc.Register<ZDOID, bool, string>(ResultRpc, OnResult);
            else peer.m_rpc.Register<ZDOID, long>(HandOffRpc, OnHandOff);
        }

        [HarmonyPatch(typeof(ZNet), "Shutdown"), HarmonyPostfix]
        static void OnShutdown()
        {
            s_owned.Clear();
            s_streaks.Clear();
            s_lastHitBy.Clear();
            s_pending.Clear();
            s_cooldowns.Clear();
            s_handedOff.Clear();
            s_localHits.Clear();
            s_creaturePrefabs.Clear();
            s_prefabsScanned = false;
        }

        internal static void ObserveRoutedRpc(ZRoutedRpc.RoutedRPCData data)
        {
            if (data.m_methodHash != s_damageHash || data.m_targetZDO.IsNone() || !Active()) return;
            s_lastHitBy[data.m_targetZDO] = new Stamp { Peer = data.m_senderPeerID, At = Time.realtimeSinceStartupAsDouble };
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

        [HarmonyPatch(typeof(ZDOMan), "Update"), HarmonyPostfix]
        static void Balance(ZDOMan __instance)
        {
            if (!Active()) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < s_nextPass) return;
            s_nextPass = now + PassSeconds;
            if (!s_prefabsScanned) ScanPrefabs();
            if (s_creaturePrefabs.Count == 0) return;

            s_candidates.Clear();
            foreach (var peer in ZNet.instance.GetPeers())
                if (peer != null && peer.IsReady() && peer.GetRefPos() != Vector3.zero) s_candidates.Add(peer);
            if (s_candidates.Count >= 2) BalanceCreaturesNearPlayers(__instance, now);
            if (now >= s_nextPrune) Prune(__instance, now);
        }

        private static void BalanceCreaturesNearPlayers(ZDOMan zdoMan, double now)
        {
            var sectors = s_objectsBySector(zdoMan);
            s_visited.Clear();
            s_perOwner.Clear();
            int handOffs = 0;
            foreach (var candidate in s_candidates)
            {
                Vector2s zone = ZoneSystem.GetZone(candidate.GetRefPos());
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
                            if (!s_creaturePrefabs.Contains(zdo.GetPrefab()) || !s_visited.Add(zdo.m_uid)) continue;
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

            var ownerPeer = ZNet.instance.GetPeer(owner);
            if (ownerPeer == null || !ownerPeer.IsReady() || !ConnectionEcho.HasFgn(ownerPeer)) return false;
            if (s_pending.TryGetValue(id, out var pending) && now - pending.At < PendingSeconds) return false;
            if (s_cooldowns.TryGetValue(id, out var cooldownEnds) && now < cooldownEnds) return false;
            if (zdo.GetBool(ZDOVars.s_tamed) || zdo.GetLong(ZDOVars.s_user) != 0L || IsHeld(zdo)) return false;

            Vector3 position = zdo.GetPosition();
            if (!ZNetScene.InActiveArea(position, ownerPeer.GetRefPos())) return false;

            ZNetPeer fighter = null;
            if (s_lastHitBy.TryGetValue(id, out var hit) && now - hit.At <= FightMemorySeconds)
            {
                if (hit.Peer == owner) return false;
                fighter = ZNet.instance.GetPeer(hit.Peer);
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
                if (!ConnectionEcho.HasFgn(candidate)) continue;
                float rtt = Rtt(candidate);
                if (rtt >= fastestRtt) continue;
                fastest = candidate;
                fastestRtt = rtt;
            }
            if (present < 2) return false;

            ZNetPeer target = null;
            bool forFight = false;
            if (fighterPresent && Rtt(fighter) <= ownerRtt + FighterPingAllowanceMs)
            {
                target = fighter;
                forFight = true;
            }
            else if (fighter == null && fastest != null && fastestRtt + PingMarginMs() < ownerRtt && now - owned.Since >= MinHoldSeconds)
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

            s_perOwner.TryGetValue(owner, out int fromOwner);
            if (fromOwner >= MaxHandOffsPerOwner) return false;
            s_perOwner[owner] = fromOwner + 1;

            ownerPeer.m_rpc.Invoke(HandOffRpc, id, target.m_uid);
            s_pending[id] = new Stamp { Peer = target.m_uid, At = now };
            s_streaks.Remove(id);
            if (forFight) s_toFighters++;
            else s_toLowerPing++;
            return true;
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

        private static int PingMarginMs() => ConfigPingMarginMs != null ? ConfigPingMarginMs.Value : 40;

        private static void OnResult(ZRpc rpc, ZDOID id, bool accepted, string reason)
        {
            if (accepted)
            {
                s_accepted++;
                return;
            }
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
                if (s_handedOff.Count > 256) PruneHandedOff();
                s_handedOff[id] = new Stamp { Peer = newOwner, At = Time.realtimeSinceStartupAsDouble };
            }
            rpc.Invoke(ResultRpc, id, decline == null, decline ?? "");
        }

        private static string Veto(ZDO zdo, long newOwner)
        {
            if (zdo == null) return "gone";
            if (!zdo.IsOwner()) return "not owner";
            if (newOwner == 0L || newOwner == ZDOMan.GetSessionID()) return "bad target";
            if (s_localHits.TryGetValue(zdo.m_uid, out double hitAt) && Time.realtimeSinceStartupAsDouble - hitAt <= FightMemorySeconds)
                return "owner fighting";
            if (IsHeld(zdo)) return "held";
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
        static bool OnDamage(Character __instance, HitData hit)
        {
            if (hit == null || Player.m_localPlayer == null) return true;
            var view = s_characterView(__instance);
            if (view == null || !view.IsValid()) return true;
            var zdo = view.GetZDO();
            double now = Time.realtimeSinceStartupAsDouble;
            if (hit.GetAttacker() == Player.m_localPlayer)
            {
                if (s_localHits.Count > 256) PruneLocalHits(now);
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

        private static void ScanPrefabs()
        {
            if (ZNetScene.instance == null) return;
            s_prefabsScanned = true;
            s_creaturePrefabs.Clear();
            s_creatureTypeMask = 0;
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
                s_creaturePrefabs.Add(prefab.name.GetStableHashCode());
                s_creatureTypeMask |= 1 << (int)view.m_type;
            }
            LoggerOptions.LogInfo($"[CreatureOwnership] {s_creaturePrefabs.Count} creature prefabs can be handed between players ({bosses} bosses never are).");
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
