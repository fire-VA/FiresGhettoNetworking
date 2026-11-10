using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// What happens to a fight when a creature changes owner, from any cause (a vanilla claim, FGN's balancer, an API hand-off, the
    /// server taking it). Every owner change goes through ZDO.SetOwnerInternal (ZDO.SetOwner locally, ZDOMan.RPC_ZDOData for one
    /// that arrives), so that one method is the edge; each game sees its own side of it:
    ///   - the old owner (lost it): N6 forwards fight RPCs that still reach it to the current owner for a short window, once each;
    ///     N7 sends the creature's fight state that is not in the ZDO to the new owner in one message.
    ///   - the new owner (gained it): N6 holds fight RPCs the old owner forwarded before this game's copy of the change arrived, and
    ///     applies them (and N7's state) at the creature's first AI update as owner, after the ZDO data is in.
    ///   - everyone else, and the old owner from then on (N8): the drawn model (Character.m_visual) eases out a pop of the root
    ///     for a few frames. The root, its colliders, physics and the ZDO are never moved.
    /// Every key is off by default. With all off at start this class is never patched, and the one call from CreatureOwnership's
    /// RPC_Damage hook is a single bool read.
    /// </summary>
    [HarmonyPatch]
    public static class FightHandoff
    {
        public static ConfigEntry<bool> ConfigForwardHits;
        public static ConfigEntry<bool> ConfigCarryState;
        public static ConfigEntry<bool> ConfigEase;

        /// <summary>True once this class's hooks are attached (a key was on at start). Read by CreatureOwnership.HandleDamage.</summary>
        internal static bool On;

        private const string StateRpc = "FGN_FightState";
        private const byte StateVersion = 1;

        private const double WindowSeconds = 3.0;        // N6: how long the old owner forwards after it lost a creature
        private const double HoldSeconds = 2.0;          // N6: how long the new owner holds a forwarded RPC before its copy of the change arrives
        private const double StateWaitSeconds = 2.0;     // N7: a state message older than this (from the gain, or parked) is dropped
        private const double FingerprintSeconds = 3.0;
        private const double PruneEverySeconds = 0.5;
        private const double ReportSeconds = 10.0;
        private const int MaxTracked = 256;              // lost / gained tables
        private const int MaxHeld = 64;
        private const int MaxHeldPerObject = 16;
        private const int MaxParked = 32;
        private const int FingerprintSlots = 128;
        private const int MaxStatesPerSecond = 32;
        private const int MaxChargeName = 64;
        // Fixed part of the state message: version 1, id 12, flags 1, stagger 4, push 12, backstab 4, hurt 4, target 12, last pos 12,
        // been-at 1, attacking 4, sensed 4, target timer 4, last attack 4, weapon hash 4, weapon age 4 = 87, plus the charge string
        // (7-bit length + UTF-8 bytes, at least 1).
        private const int FixedStateBytes = 87;
        private const int MaxStateBytes = FixedStateBytes + 2 + MaxChargeName * 3;
        private const float MaxCarriedAge = 3600f;
        private const float MaxCarriedTimer = 600f;
        private const float MaxCarriedPush = 200f;
        private const float AdoptMaxMeters = 60f;

        private const double EaseWindowSeconds = 1.5;    // N8: pops are eased only this long after an owner switch
        private const float EaseTauSeconds = 0.05f;      // N8: offset decays to 5 % in ~150 ms
        private const float PopMeters = 0.5f;            // N8: per-frame jump beyond the synced velocity that counts as a pop
        private const float TeleportMeters = 8f;         // N8: at or above this it is a real teleport: not eased
        private const int MaxEases = 32;

        private enum Kind : byte { Damage = 0, Stagger = 1, Heal = 2 }

        private struct Edge { public long Peer; public double At; public bool AiRan; }

        private struct Fp { public ZDOID Id; public int Hash; public double At; }

        private struct Held
        {
            public ZDOID Id; public Kind Kind; public long Sender; public double At;
            public HitData Hit; public Vector3 Dir; public float Hp; public bool ShowText;
        }

        private struct FightState
        {
            public ZDOID Id; public byte Flags; public float StaggerDamage; public Vector3 Push; public float BackstabAge; public float SinceHurt;
            public ZDOID Target; public Vector3 LastKnownPos; public bool BeenAtLastPos; public float SinceAttacking; public float SinceSensed;
            public float UpdateTargetTimer; public float SinceLastAttack; public int WeaponHash; public float WeaponAge; public string Charge;
        }
        private const byte FlagMonster = 1, FlagHumanoid = 2, FlagInAttack = 4, FlagStaggering = 8;

        private struct Parked { public FightState State; public long Sender; public double At; }

        private struct Ease
        {
            public Character C; public Transform Root; public Transform Visual; public Vector3 BaseLocal; public Vector3 LastRoot;
            public Vector3 Offset; public double Until; public bool Moved;
        }

        private static readonly Dictionary<ZDOID, Edge> s_lost = new Dictionary<ZDOID, Edge>();
        private static readonly Dictionary<ZDOID, Edge> s_gained = new Dictionary<ZDOID, Edge>();
        private static readonly List<ZDOID> s_stale = new List<ZDOID>();
        private static readonly Fp[] s_forwardRing = new Fp[FingerprintSlots];
        private static readonly Fp[] s_appliedRing = new Fp[FingerprintSlots];
        private static int s_forwardNext, s_appliedNext;
        private static readonly Held[] s_held = new Held[MaxHeld];
        private static int s_heldCount;
        private static readonly Parked[] s_parked = new Parked[MaxParked];
        private static int s_parkedCount;
        private static readonly Ease[] s_eases = new Ease[MaxEases];
        private static int s_easeCount;
        private static double s_nextPrune, s_nextReport, s_stateWindowStart;
        private static int s_statesThisSecond;
        private static bool s_dirty;
        private static float s_nextWarn;

        // counters (reset per report)
        private static int s_fwdDamage, s_fwdStagger, s_fwdHeal, s_fwdLoopSkipped, s_dedupedAtOwner, s_held_n, s_heldFull, s_heldApplied, s_heldExpired, s_heldWrongSender;
        private static int s_stateSent, s_stateSkippedIdle, s_stateRateCapped, s_stateAppliedEarly, s_stateAppliedLate, s_stateParked, s_stateRejected, s_stateExpired;
        private static int s_lostEdges, s_gainedEdges, s_observerEdges;
        private static int s_easeStarted, s_easePops, s_easeTeleports, s_easeDone, s_easeFull;
        private static string s_lastReject;
        private static float s_maxPop;

        private static readonly AccessTools.FieldRef<Character, ZNetView> s_charView = AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Character, GameObject> s_visual = AccessTools.FieldRefAccess<Character, GameObject>("m_visual");
        private static readonly AccessTools.FieldRef<Character, float> s_staggerDamage = AccessTools.FieldRefAccess<Character, float>("m_staggerDamage");
        private static readonly AccessTools.FieldRef<Character, Vector3> s_push = AccessTools.FieldRefAccess<Character, Vector3>("m_pushForce");
        private static readonly AccessTools.FieldRef<Character, float> s_backstabTime = AccessTools.FieldRefAccess<Character, float>("m_backstabTime");
        private static readonly AccessTools.FieldRef<BaseAI, ZNetView> s_aiView = AccessTools.FieldRefAccess<BaseAI, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<BaseAI, float> s_sinceHurt = AccessTools.FieldRefAccess<BaseAI, float>("m_timeSinceHurt");
        private static readonly AccessTools.FieldRef<BaseAI, string> s_charging = AccessTools.FieldRefAccess<BaseAI, string>("m_charging");
        private static readonly AccessTools.FieldRef<MonsterAI, Character> s_target = AccessTools.FieldRefAccess<MonsterAI, Character>("m_targetCreature");
        private static readonly AccessTools.FieldRef<MonsterAI, Vector3> s_lastKnown = AccessTools.FieldRefAccess<MonsterAI, Vector3>("m_lastKnownTargetPos");
        private static readonly AccessTools.FieldRef<MonsterAI, bool> s_beenAt = AccessTools.FieldRefAccess<MonsterAI, bool>("m_beenAtLastPos");
        private static readonly AccessTools.FieldRef<MonsterAI, float> s_sinceAttacking = AccessTools.FieldRefAccess<MonsterAI, float>("m_timeSinceAttacking");
        private static readonly AccessTools.FieldRef<MonsterAI, float> s_sinceSensed = AccessTools.FieldRefAccess<MonsterAI, float>("m_timeSinceSensedTargetCreature");
        private static readonly AccessTools.FieldRef<MonsterAI, float> s_updateTargetTimer = AccessTools.FieldRefAccess<MonsterAI, float>("m_updateTargetTimer");
        private static readonly AccessTools.FieldRef<Humanoid, float> s_sinceLastAttack = AccessTools.FieldRefAccess<Humanoid, float>("m_timeSinceLastAttack");

        // Vanilla's own handlers, called for held RPCs once this game owns the creature (they go through every Harmony patch on them).
        private static readonly Action<Character, long, HitData> s_rpcDamage = Del<Action<Character, long, HitData>>(AccessTools.Method(typeof(Character), "RPC_Damage"));
        private static readonly Action<Character, long, Vector3> s_rpcStagger = Del<Action<Character, long, Vector3>>(AccessTools.Method(typeof(Character), "RPC_Stagger"));
        private static readonly Action<Character, long, float, bool> s_rpcHeal = Del<Action<Character, long, float, bool>>(AccessTools.Method(typeof(Character), "RPC_Heal"));
        private static T Del<T>(System.Reflection.MethodInfo m) where T : Delegate { try { return m != null ? AccessTools.MethodDelegate<T>(m) : null; } catch { return null; } }

        public static void InitConfig(ConfigFile config)
        {
            ConfigForwardHits = config.Bind("04 - Networking", "Hand-off Forward Fight Hits", false,
                Cfg.Adv("For 3 s after a creature changes owner (any cause), hits, staggers and heals that still reach the old owner are passed to the new owner once, and the new owner keeps ones that arrive before it knows it owns the creature. Needs FGN with this on at both players; with it off at either end, hits in that window are lost as in vanilla."));
            ConfigCarryState = config.Bind("04 - Networking", "Hand-off Carry Fight State", false,
                Cfg.Adv("When a creature changes owner, the old owner sends its stagger build-up, knockback, attack timers, target and charge state to the new owner, so the creature carries on its attack, stagger and chase instead of starting over. Needs FGN with this on at both players."));
            ConfigEase = config.Bind("04 - Networking", "Hand-off Ease Shown Position", false,
                Cfg.Adv("When a creature you do not run changes owner, a sudden jump in where it is drawn is smoothed out over a few frames. Only the drawn model moves; where the creature really is (hits, collisions, the world data) is untouched. Works on its own on your game."));
        }

        internal static bool AnyKeyOn() =>
            (ConfigForwardHits != null && ConfigForwardHits.Value) || (ConfigCarryState != null && ConfigCarryState.Value) || (ConfigEase != null && ConfigEase.Value);

        private static bool ForwardOn() => ConfigForwardHits != null && ConfigForwardHits.Value;
        private static bool CarryOn() => ConfigCarryState != null && ConfigCarryState.Value;
        private static bool EaseOn() => ConfigEase != null && ConfigEase.Value && !ServerClientUtils.ZNetIsDedicated();

        /// <summary>Called by Ascend after TryPatchAll(typeof(FightHandoff)): On only if the owner-edge postfix really attached.</summary>
        internal static void MarkAttached()
        {
            On = false;
            var info = HarmonyLib.Harmony.GetPatchInfo(AccessTools.Method(typeof(ZDO), nameof(ZDO.SetOwnerInternal)));
            if (info != null)
                foreach (var patch in info.Postfixes)
                    if (patch.PatchMethod != null && patch.PatchMethod.DeclaringType == typeof(FightHandoff)) On = true;
            LoggerOptions.LogDebug($"[FightHandoff] attached={On}: forward hits {ForwardOn()}, carry state {CarryOn()}, ease shown position {(ConfigEase != null && ConfigEase.Value)}"
                + (ServerClientUtils.ZNetIsDedicated() ? " (dedicated: ease never runs, nothing is drawn here)" : ""));
        }

        // ------------------------------------------------------------------ lifecycle

        [HarmonyPatch(typeof(ZNet), "Start"), HarmonyPostfix]
        static void OnZNetStart()
        {
            ClearAll();
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.Register<ZPackage>(StateRpc, RPC_State);
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown)), HarmonyPostfix]
        static void OnZNetShutdown() => ClearAll();

        private static void ClearAll()
        {
            for (int i = 0; i < s_easeCount; i++) Restore(ref s_eases[i]);
            Array.Clear(s_eases, 0, s_eases.Length);
            s_easeCount = 0;
            s_lost.Clear();
            s_gained.Clear();
            Array.Clear(s_held, 0, s_held.Length);
            s_heldCount = 0;
            Array.Clear(s_parked, 0, s_parked.Length);
            s_parkedCount = 0;
            Array.Clear(s_forwardRing, 0, s_forwardRing.Length);
            Array.Clear(s_appliedRing, 0, s_appliedRing.Length);
        }

        // ------------------------------------------------------------------ the owner edge

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwnerInternal)), HarmonyPrefix]
        static void OwnerBefore(ZDO __instance, out long __state)
        {
            __state = 0L;
            if (!On || !AnyKeyOn()) return;
            __state = __instance.GetOwner();
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwnerInternal)), HarmonyPostfix]
        static void OwnerAfter(ZDO __instance, long uid, long __state)
        {
            // __state == 0: key off, or no previous owner (a new object, or one the server had released): nothing to carry or ease.
            if (__state == 0L || uid == 0L || __state == uid) return;
            try { OnOwnerChanged(__instance, __state, uid); }
            catch (Exception ex) { Warn("owner change", ex); }
        }

        private static void OnOwnerChanged(ZDO zdo, long prev, long next)
        {
            long me = ZDOMan.GetSessionID();
            bool lost = prev == me, gained = next == me;
            if (!lost && !gained && !EaseOn()) return;
            ZNetView view = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
            if (view == null) return;
            Character c = view.GetComponent<Character>();
            if (c == null || c is Player) return;
            double now = Time.realtimeSinceStartupAsDouble;
            ZDOID id = zdo.m_uid;
            s_dirty = true;

            if (lost)
            {
                s_lostEdges++;
                if (ForwardOn()) { if (s_lost.Count >= MaxTracked) PruneEdges(s_lost, now, WindowSeconds); if (s_lost.Count < MaxTracked) s_lost[id] = new Edge { Peer = next, At = now }; }
                if (CarryOn()) SendState(c, id, next, now);
                if (EaseOn()) StartEase(c, now);
                if (LoggerOptions.DebugEnabled && s_lostEdges <= 3)
                    LoggerOptions.LogDebug($"[FightHandoff] lost {c.name} {id} to {next}: forwarding for {WindowSeconds:F0} s={ForwardOn()}, state sent={CarryOn()}");
                return;
            }
            if (gained)
            {
                s_gainedEdges++;
                if (s_gained.Count >= MaxTracked) PruneEdges(s_gained, now, WindowSeconds);
                if (s_gained.Count < MaxTracked) s_gained[id] = new Edge { Peer = prev, At = now, AiRan = false };
                RemoveEase(c);   // this game now runs it: the body is the drawing
                return;
            }
            s_observerEdges++;
            StartEase(c, now);
        }

        // ------------------------------------------------------------------ N6: fight RPCs across the change

        /// <summary>From CreatureOwnership.HandleDamage (FGN's one Character.RPC_Damage prefix). False = handled here, skip vanilla.</summary>
        internal static bool OnDamage(Character c, long sender, HitData hit)
        {
            if (!ForwardOn() || hit == null) return true;
            try { return Route(c, Kind.Damage, sender, hit, Vector3.zero, 0f, false); }
            catch (Exception ex) { Warn("RPC_Damage", ex); return true; }
        }

        [HarmonyPatch(typeof(Character), "RPC_Stagger"), HarmonyPrefix]
        static bool BeforeStagger(Character __instance, long sender, Vector3 forceDirection)
        {
            if (!ForwardOn()) return true;
            try { return Route(__instance, Kind.Stagger, sender, null, forceDirection, 0f, false); }
            catch (Exception ex) { Warn("RPC_Stagger", ex); return true; }
        }

        [HarmonyPatch(typeof(Character), "RPC_Heal"), HarmonyPrefix]
        static bool BeforeHeal(Character __instance, long sender, float hp, bool showText)
        {
            if (!ForwardOn()) return true;
            try { return Route(__instance, Kind.Heal, sender, null, Vector3.zero, hp, showText); }
            catch (Exception ex) { Warn("RPC_Heal", ex); return true; }
        }

        private static bool Route(Character c, Kind kind, long sender, HitData hit, Vector3 dir, float hp, bool showText)
        {
            if (c is Player) return true;
            ZNetView view = s_charView(c);
            if ((object)view == null || !view.IsValid()) return true;
            ZDO zdo = view.GetZDO();
            ZDOID id = zdo.m_uid;
            long me = ZDOMan.GetSessionID();

            if (zdo.IsOwner())
            {
                // Dedupe at the owner, only just after it gained the creature and only for remote calls: a hit (point, direction,
                // damage, attacker) or a stagger (direction, sender) seen twice in 3 s is one call that arrived by two paths.
                // Heals are never deduped here (a healer's repeated ticks are identical by design).
                if (sender == 0L || sender == me || kind == Kind.Heal || s_gained.Count == 0) return true;
                if (!s_gained.TryGetValue(id, out Edge g)) return true;
                double t = Time.realtimeSinceStartupAsDouble;
                if (t - g.At > WindowSeconds) return true;
                int fp = Fingerprint(id, kind, sender, hit, dir);
                if (Seen(s_appliedRing, id, fp, t)) { s_dedupedAtOwner++; s_dirty = true; return false; }
                Remember(s_appliedRing, ref s_appliedNext, id, fp, t);
                return true;
            }

            long owner = zdo.GetOwner();
            double now = Time.realtimeSinceStartupAsDouble;

            // Old owner: pass it on to whoever owns it now (follows chains), never straight back to the peer that sent it.
            if (s_lost.Count > 0 && s_lost.TryGetValue(id, out Edge lost) && now - lost.At <= WindowSeconds
                && owner != 0L && owner != me && owner != sender)
            {
                int fp = Fingerprint(id, kind, sender, hit, dir);
                if (kind != Kind.Heal && Seen(s_forwardRing, id, fp, now)) { s_fwdLoopSkipped++; s_dirty = true; return true; }   // forwarded once already: vanilla drops it
                if (kind != Kind.Heal) Remember(s_forwardRing, ref s_forwardNext, id, fp, now);
                switch (kind)
                {
                    case Kind.Damage: view.InvokeRPC(owner, "RPC_Damage", hit); s_fwdDamage++; break;
                    case Kind.Stagger: view.InvokeRPC(owner, "RPC_Stagger", dir); s_fwdStagger++; break;
                    default: view.InvokeRPC(owner, "RPC_Heal", hp, showText); s_fwdHeal++; break;
                }
                s_dirty = true;
                return false;
            }

            // New owner before its copy of the change arrived: the peer this game still thinks owns it sent the call here, which only
            // happens when that peer has handed it to us. Keep it until the change lands (applied at the first AI update as owner).
            if (sender != 0L && sender != me && sender == owner)
                return !Hold(id, kind, sender, hit, dir, hp, showText, now);
            return true;
        }

        private static bool Hold(ZDOID id, Kind kind, long sender, HitData hit, Vector3 dir, float hp, bool showText, double now)
        {
            if (s_heldCount >= MaxHeld) PruneHeld(now);
            int perObject = 0;
            for (int i = 0; i < s_heldCount; i++) if (s_held[i].Id == id) perObject++;
            if (s_heldCount >= MaxHeld || perObject >= MaxHeldPerObject) { s_heldFull++; s_dirty = true; return false; }
            s_held[s_heldCount++] = new Held { Id = id, Kind = kind, Sender = sender, At = now, Hit = hit, Dir = dir, Hp = hp, ShowText = showText };
            s_held_n++;
            s_dirty = true;
            return true;
        }

        private static void FlushHeld(Character c, ZDOID id, long prev, double now)
        {
            for (int i = 0; i < s_heldCount; )
            {
                if (s_held[i].Id != id) { i++; continue; }
                Held h = s_held[i];
                s_held[i] = s_held[--s_heldCount];
                s_held[s_heldCount] = default;
                if (now - h.At > HoldSeconds) { s_heldExpired++; continue; }
                if (h.Sender != prev) { s_heldWrongSender++; continue; }   // only the previous owner may hand us calls
                switch (h.Kind)
                {
                    case Kind.Damage: if (s_rpcDamage != null) { s_rpcDamage(c, h.Sender, h.Hit); s_heldApplied++; } break;
                    case Kind.Stagger: if (s_rpcStagger != null) { s_rpcStagger(c, h.Sender, h.Dir); s_heldApplied++; } break;
                    default: if (s_rpcHeal != null) { s_rpcHeal(c, h.Sender, h.Hp, h.ShowText); s_heldApplied++; } break;
                }
            }
        }

        private static int Fingerprint(ZDOID id, Kind kind, long sender, HitData hit, Vector3 dir)
        {
            unchecked
            {
                int h = id.GetHashCode() * 31 + (int)kind;
                if (hit != null)
                {
                    h = h * 31 + hit.m_point.GetHashCode();
                    h = h * 31 + hit.m_dir.GetHashCode();
                    h = h * 31 + hit.m_damage.GetTotalDamage().GetHashCode();
                    h = h * 31 + hit.m_attacker.GetHashCode();
                }
                else
                {
                    h = h * 31 + dir.GetHashCode();
                    h = h * 31 + sender.GetHashCode();
                }
                return h;
            }
        }

        private static bool Seen(Fp[] ring, ZDOID id, int hash, double now)
        {
            for (int i = 0; i < ring.Length; i++)
                if (ring[i].Hash == hash && ring[i].Id == id && now - ring[i].At <= FingerprintSeconds) return true;
            return false;
        }

        private static void Remember(Fp[] ring, ref int next, ZDOID id, int hash, double now)
        {
            ring[next] = new Fp { Id = id, Hash = hash, At = now };
            next = (next + 1) % ring.Length;
        }

        // ------------------------------------------------------------------ N7: fight state across the change

        private static void SendState(Character c, ZDOID id, long newOwner, double now)
        {
            if (ZRoutedRpc.instance == null) return;
            BaseAI ai = c.GetComponent<BaseAI>();
            MonsterAI monster = ai as MonsterAI;
            Humanoid humanoid = c as Humanoid;
            float t = Time.time;

            Vector3 push = s_push(c);
            float stagger = s_staggerDamage(c);
            string charge = ai != null ? s_charging(ai) : null;
            Character target = monster != null ? s_target(monster) : null;
            bool fighting = stagger > 0f || push != Vector3.zero || target != null || c.InAttack() || c.IsStaggering() || charge != null
                || (ai != null && (ai.IsAlerted() || s_sinceHurt(ai) < 30f));
            if (!fighting) { s_stateSkippedIdle++; return; }   // an idle creature has nothing worth a message
            if (now - s_stateWindowStart >= 1.0) { s_stateWindowStart = now; s_statesThisSecond = 0; }
            if (++s_statesThisSecond > MaxStatesPerSecond) { s_stateRateCapped++; return; }

            byte flags = 0;
            if (monster != null) flags |= FlagMonster;
            if (humanoid != null) flags |= FlagHumanoid;
            if (c.InAttack()) flags |= FlagInAttack;
            if (c.IsStaggering()) flags |= FlagStaggering;

            int weaponHash = 0;
            float weaponAge = MaxCarriedAge;
            if (humanoid != null)
            {
                ItemDrop.ItemData weapon = humanoid.GetCurrentWeapon();
                if (weapon?.m_shared != null && !string.IsNullOrEmpty(weapon.m_shared.m_name))
                {
                    weaponHash = weapon.m_shared.m_name.GetStableHashCode();
                    weaponAge = Mathf.Clamp(t - weapon.m_lastAttackTime, 0f, MaxCarriedAge);
                }
            }
            if (charge != null && charge.Length > MaxChargeName) charge = null;
            ZNetView targetView = target != null ? target.GetComponent<ZNetView>() : null;
            ZDOID targetId = targetView != null && targetView.IsValid() ? targetView.GetZDO().m_uid : ZDOID.None;

            var pkg = new ZPackage();
            pkg.Write(StateVersion);
            pkg.Write(id);
            pkg.Write(flags);
            pkg.Write(stagger);
            pkg.Write(push);
            pkg.Write(Mathf.Clamp(t - s_backstabTime(c), 0f, MaxCarriedAge));
            pkg.Write(ai != null ? Mathf.Min(s_sinceHurt(ai), MaxCarriedAge) : MaxCarriedAge);
            pkg.Write(targetId);
            pkg.Write(monster != null ? s_lastKnown(monster) : Vector3.zero);
            pkg.Write(monster != null && s_beenAt(monster));
            pkg.Write(monster != null ? s_sinceAttacking(monster) : 0f);
            pkg.Write(monster != null ? s_sinceSensed(monster) : 0f);
            pkg.Write(monster != null ? s_updateTargetTimer(monster) : 0f);
            pkg.Write(humanoid != null ? Mathf.Min(s_sinceLastAttack(humanoid), MaxCarriedAge) : MaxCarriedAge);
            pkg.Write(weaponHash);
            pkg.Write(weaponAge);
            pkg.Write(charge ?? "");
            ZRoutedRpc.instance.InvokeRoutedRPC(newOwner, StateRpc, pkg);
            s_stateSent++;
            s_dirty = true;
        }

        private static void RPC_State(long sender, ZPackage pkg)
        {
            if (!On || !CarryOn()) return;
            try { ReceiveState(sender, pkg); }
            catch (Exception ex) { Warn("FGN_FightState", ex); }
        }

        private static void ReceiveState(long sender, ZPackage pkg)
        {
            s_dirty = true;
            long me = ZDOMan.GetSessionID();
            if (pkg == null || sender == 0L || sender == me) { Reject("bad sender"); return; }
            int size = pkg.Size();
            if (size < FixedStateBytes || size > MaxStateBytes) { Reject($"size {size}"); return; }
            if (!TryRead(pkg, out FightState st, out string why)) { Reject(why); return; }
            ZDO zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(st.Id) : null;
            if (zdo == null) { Reject("unknown object"); return; }
            double now = Time.realtimeSinceStartupAsDouble;

            if (zdo.IsOwner())
            {
                if (!s_gained.TryGetValue(st.Id, out Edge g) || g.Peer != sender) { Reject("sender is not the previous owner"); return; }
                if (now - g.At > StateWaitSeconds) { s_stateExpired++; return; }
                ZNetView view = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
                Character c = view != null ? view.GetComponent<Character>() : null;
                if (c == null) return;
                Apply(c, c.GetComponent<BaseAI>(), st, early: !g.AiRan);
                return;
            }
            // The sender still owns it in our copy: its change has not reached us yet. Keep the state for the first AI update as owner.
            if (zdo.GetOwner() != sender) { Reject("sender does not own the creature"); return; }
            if (s_parkedCount >= MaxParked) PruneParked(now);
            if (s_parkedCount >= MaxParked) { Reject("parked table full"); return; }
            for (int i = 0; i < s_parkedCount; i++)
                if (s_parked[i].State.Id == st.Id) { s_parked[i] = new Parked { State = st, Sender = sender, At = now }; s_stateParked++; return; }
            s_parked[s_parkedCount++] = new Parked { State = st, Sender = sender, At = now };
            s_stateParked++;
        }

        private static bool TryRead(ZPackage pkg, out FightState st, out string why)
        {
            st = default;
            why = null;
            try
            {
                pkg.SetPos(0);
                if (pkg.ReadByte() != StateVersion) { why = "unknown version"; return false; }
                st.Id = pkg.ReadZDOID();
                st.Flags = pkg.ReadByte();
                st.StaggerDamage = pkg.ReadSingle();
                st.Push = pkg.ReadVector3();
                st.BackstabAge = pkg.ReadSingle();
                st.SinceHurt = pkg.ReadSingle();
                st.Target = pkg.ReadZDOID();
                st.LastKnownPos = pkg.ReadVector3();
                st.BeenAtLastPos = pkg.ReadBool();
                st.SinceAttacking = pkg.ReadSingle();
                st.SinceSensed = pkg.ReadSingle();
                st.UpdateTargetTimer = pkg.ReadSingle();
                st.SinceLastAttack = pkg.ReadSingle();
                st.WeaponHash = pkg.ReadInt();
                st.WeaponAge = pkg.ReadSingle();
                st.Charge = pkg.ReadString();
                if (pkg.GetPos() != pkg.Size()) { why = "trailing bytes"; return false; }
            }
            catch { why = "malformed"; return false; }
            if (st.Id.IsNone()) { why = "no object id"; return false; }
            if (!Finite(st.StaggerDamage) || st.StaggerDamage < 0f || st.StaggerDamage > 1e7f) { why = "stagger out of range"; return false; }
            if (!Finite(st.Push) || st.Push.magnitude > MaxCarriedPush) { why = "push out of range"; return false; }
            if (!Finite(st.LastKnownPos)) { why = "position not finite"; return false; }
            if (!InRange(st.BackstabAge, MaxCarriedAge) || !InRange(st.SinceHurt, 1e6f) || !InRange(st.SinceAttacking, 1e6f) || !InRange(st.SinceSensed, 1e6f)
                || !InRange(st.UpdateTargetTimer, MaxCarriedTimer) || !InRange(st.SinceLastAttack, 1e6f) || !InRange(st.WeaponAge, MaxCarriedAge))
            { why = "timer out of range"; return false; }
            if (st.Charge != null && st.Charge.Length > MaxChargeName) { why = "charge name too long"; return false; }
            return true;
        }

        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        private static bool InRange(float f, float max) => Finite(f) && f >= 0f && f <= max;

        /// <summary>Early = before the new owner's first AI update: carried values replace the ones this game never simulated.
        /// Late = the new owner already ran: merge so nothing repeats (the more recent attack, hurt and stagger win; no second push).</summary>
        private static void Apply(Character c, BaseAI ai, FightState st, bool early)
        {
            float t = Time.time;
            s_staggerDamage(c) = Mathf.Max(s_staggerDamage(c), st.StaggerDamage);
            if (early && st.Push.sqrMagnitude > s_push(c).sqrMagnitude) s_push(c) = st.Push;
            if (st.BackstabAge < MaxCarriedAge) s_backstabTime(c) = Mathf.Max(s_backstabTime(c), t - st.BackstabAge);
            if (ai != null)
            {
                s_sinceHurt(ai) = early ? st.SinceHurt : Mathf.Min(s_sinceHurt(ai), st.SinceHurt);
                if (early && !string.IsNullOrEmpty(st.Charge) && s_charging(ai) == null) s_charging(ai) = st.Charge;   // lets ChargeStop clear the anim bool the old owner set
            }
            if (ai is MonsterAI monster && (st.Flags & FlagMonster) != 0)
            {
                bool targetKept = false;
                if (s_target(monster) == null && !st.Target.IsNone() && ZNetScene.instance != null)
                {
                    GameObject go = ZNetScene.instance.FindInstance(st.Target);
                    Character target = go != null ? go.GetComponent<Character>() : null;
                    if (target != null && !target.IsDead() && !c.IsTamed() && BaseAI.IsEnemy(c, target)
                        && Vector3.Distance(target.transform.position, c.transform.position) <= AdoptMaxMeters)
                    {
                        s_target(monster) = target;
                        targetKept = true;
                    }
                }
                if (early || targetKept)
                {
                    s_lastKnown(monster) = st.LastKnownPos;
                    s_beenAt(monster) = st.BeenAtLastPos;
                    s_sinceAttacking(monster) = st.SinceAttacking;
                    s_sinceSensed(monster) = st.SinceSensed;
                }
                if (early) s_updateTargetTimer(monster) = st.UpdateTargetTimer;
            }
            if (c is Humanoid humanoid && (st.Flags & FlagHumanoid) != 0)
            {
                s_sinceLastAttack(humanoid) = early ? st.SinceLastAttack : Mathf.Min(s_sinceLastAttack(humanoid), st.SinceLastAttack);
                if (st.WeaponHash != 0)
                {
                    List<ItemDrop.ItemData> items = humanoid.GetInventory()?.GetAllItems();
                    if (items != null)
                        for (int i = 0; i < items.Count; i++)
                        {
                            ItemDrop.ItemData item = items[i];
                            if (item?.m_shared == null || string.IsNullOrEmpty(item.m_shared.m_name) || item.m_shared.m_name.GetStableHashCode() != st.WeaponHash) continue;
                            item.m_lastAttackTime = Mathf.Max(item.m_lastAttackTime, t - st.WeaponAge);   // never lets it attack sooner than the old owner would
                            break;
                        }
                }
            }
            if (early) s_stateAppliedEarly++; else s_stateAppliedLate++;
            s_dirty = true;
            if (LoggerOptions.DebugEnabled && s_stateAppliedEarly + s_stateAppliedLate <= 3)
                LoggerOptions.LogDebug($"[FightHandoff] state applied to {c.name} ({(early ? "before its first AI update" : "late, merged")}): stagger {st.StaggerDamage:F1}, push {st.Push.magnitude:F1}, "
                    + $"target {(st.Target.IsNone() ? "none" : st.Target.ToString())}, last attack {st.SinceLastAttack:F2} s ago, charge '{st.Charge}'");
        }

        private static void Reject(string why)
        {
            s_stateRejected++;
            s_lastReject = why;
        }

        // ------------------------------------------------------------------ first AI update as owner

        [HarmonyPatch(typeof(BaseAI), "UpdateAI"), HarmonyPrefix]
        static void BeforeAi(BaseAI __instance)
        {
            if (s_gained.Count == 0) return;
            try
            {
                ZNetView view = s_aiView(__instance);
                if ((object)view == null) return;
                ZDO zdo = view.GetZDO();
                if (zdo == null || !zdo.IsOwner() || !s_gained.TryGetValue(zdo.m_uid, out Edge g) || g.AiRan) return;
                g.AiRan = true;
                s_gained[zdo.m_uid] = g;
                Character c = __instance.GetComponent<Character>();
                if (c == null) return;
                double now = Time.realtimeSinceStartupAsDouble;
                if (s_parkedCount > 0)
                    for (int i = 0; i < s_parkedCount; i++)
                    {
                        if (s_parked[i].State.Id != zdo.m_uid) continue;
                        Parked p = s_parked[i];
                        s_parked[i] = s_parked[--s_parkedCount];
                        s_parked[s_parkedCount] = default;
                        if (p.Sender != g.Peer) Reject("parked state not from the previous owner");
                        else if (now - p.At > StateWaitSeconds) s_stateExpired++;
                        else if (CarryOn()) Apply(c, __instance, p.State, early: true);
                        break;
                    }
                if (s_heldCount > 0) FlushHeld(c, zdo.m_uid, g.Peer, now);
            }
            catch (Exception ex) { Warn("UpdateAI", ex); }
        }

        // ------------------------------------------------------------------ N8: render-only ease

        private static void StartEase(Character c, double now)
        {
            if (!EaseOn()) return;
            for (int i = 0; i < s_easeCount; i++)
                if (s_eases[i].C == c) { s_eases[i].Until = now + EaseWindowSeconds; return; }
            if (s_easeCount >= MaxEases) { s_easeFull++; return; }
            GameObject visual = s_visual(c);
            if (visual == null || c.IsDead()) return;
            Transform root = c.transform;
            s_eases[s_easeCount++] = new Ease
            {
                C = c, Root = root, Visual = visual.transform, BaseLocal = visual.transform.localPosition,
                LastRoot = root.position, Offset = Vector3.zero, Until = now + EaseWindowSeconds, Moved = false,
            };
            s_easeStarted++;
        }

        private static void RemoveEase(Character c)
        {
            for (int i = 0; i < s_easeCount; i++)
                if (s_eases[i].C == c) { Restore(ref s_eases[i]); RemoveEaseAt(i); return; }
        }

        private static void RemoveEaseAt(int i)
        {
            s_eases[i] = s_eases[--s_easeCount];
            s_eases[s_easeCount] = default;
        }

        private static void Restore(ref Ease e)
        {
            if (e.Moved && e.Visual != null) e.Visual.localPosition = e.BaseLocal;
            e.Moved = false;
        }

        // Once a frame, after every ZSyncTransform and anim event ran (MonoUpdaters.LateUpdate), before rendering.
        [HarmonyPatch(typeof(MonoUpdaters), "LateUpdate"), HarmonyPostfix]
        static void AfterLateUpdate()
        {
            if (s_easeCount == 0 && !s_dirty && s_lost.Count == 0 && s_gained.Count == 0 && s_heldCount == 0 && s_parkedCount == 0) return;
            try
            {
                double now = Time.realtimeSinceStartupAsDouble;
                if (s_easeCount > 0) StepEases(now, Time.deltaTime);
                if (now >= s_nextPrune)
                {
                    s_nextPrune = now + PruneEverySeconds;
                    PruneEdges(s_lost, now, WindowSeconds);
                    PruneEdges(s_gained, now, WindowSeconds);
                    PruneHeld(now);
                    PruneParked(now);
                }
                if (s_dirty && now >= s_nextReport) Report(now);
            }
            catch (Exception ex) { Warn("LateUpdate", ex); }
        }

        private static void StepEases(double now, float dt)
        {
            if (dt <= 0f) return;
            float decay = Mathf.Exp(-dt / EaseTauSeconds);
            for (int i = s_easeCount - 1; i >= 0; i--)
            {
                ref Ease e = ref s_eases[i];
                if (e.C == null || e.Visual == null || e.Root == null) { RemoveEaseAt(i); continue; }   // destroyed: nothing to restore
                ZNetView view = s_charView(e.C);
                ZDO zdo = (object)view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo == null || zdo.IsOwner() || e.C.IsDead()) { Restore(ref e); RemoveEaseAt(i); continue; }

                Vector3 root = e.Root.position;
                Vector3 step = root - e.LastRoot;
                e.LastRoot = root;
                if (now <= e.Until)
                {
                    // Only the part of this frame's move that the synced velocity does not explain is a pop; normal motion passes 1:1.
                    Vector3 residual = step - zdo.GetVec3(ZDOVars.s_velHash, Vector3.zero) * dt;
                    float r = residual.magnitude;
                    if (r >= TeleportMeters) { e.Offset = Vector3.zero; s_easeTeleports++; }
                    else if (r > PopMeters) { e.Offset -= residual; s_easePops++; if (r > s_maxPop) s_maxPop = r; s_dirty = true; }
                }
                e.Offset *= decay;
                if (e.Offset.sqrMagnitude > TeleportMeters * TeleportMeters) e.Offset = e.Offset.normalized * TeleportMeters;
                if (e.Offset.sqrMagnitude < 1e-6f)
                {
                    if (now > e.Until) { Restore(ref e); RemoveEaseAt(i); s_easeDone++; continue; }
                    if (e.Moved) { e.Visual.localPosition = e.BaseLocal; e.Moved = false; }
                    continue;
                }
                e.Visual.localPosition = e.BaseLocal + e.Root.InverseTransformVector(e.Offset);
                e.Moved = true;
            }
        }

        // ------------------------------------------------------------------ housekeeping

        private static void PruneEdges(Dictionary<ZDOID, Edge> edges, double now, double maxAge)
        {
            if (edges.Count == 0) return;
            s_stale.Clear();
            foreach (var entry in edges)
                if (now - entry.Value.At > maxAge) s_stale.Add(entry.Key);
            for (int i = 0; i < s_stale.Count; i++) edges.Remove(s_stale[i]);
        }

        private static void PruneHeld(double now)
        {
            for (int i = 0; i < s_heldCount; )
            {
                if (now - s_held[i].At <= HoldSeconds) { i++; continue; }
                s_held[i] = s_held[--s_heldCount];
                s_held[s_heldCount] = default;
                s_heldExpired++;
                s_dirty = true;
            }
        }

        private static void PruneParked(double now)
        {
            for (int i = 0; i < s_parkedCount; )
            {
                if (now - s_parked[i].At <= StateWaitSeconds) { i++; continue; }
                s_parked[i] = s_parked[--s_parkedCount];
                s_parked[s_parkedCount] = default;
                s_stateExpired++;
                s_dirty = true;
            }
        }

        private static void Report(double now)
        {
            s_nextReport = now + ReportSeconds;
            s_dirty = false;
            if (!LoggerOptions.DebugEnabled) { ResetCounters(); return; }
            var sb = new StringBuilder(512);
            sb.Append("[FightHandoff] last 10 s: owner changes seen: lost ").Append(s_lostEdges).Append(", gained ").Append(s_gainedEdges)
              .Append(", watched ").Append(s_observerEdges).Append('.');
            if (ForwardOn())
                sb.Append(" Hits: forwarded damage ").Append(s_fwdDamage).Append(", stagger ").Append(s_fwdStagger).Append(", heal ").Append(s_fwdHeal)
                  .Append("; not forwarded twice ").Append(s_fwdLoopSkipped).Append("; deduped at owner ").Append(s_dedupedAtOwner)
                  .Append("; held before the change arrived ").Append(s_held_n).Append(" (applied ").Append(s_heldApplied).Append(", expired ").Append(s_heldExpired)
                  .Append(", wrong sender ").Append(s_heldWrongSender).Append(", table full ").Append(s_heldFull).Append(").");
            if (CarryOn())
                sb.Append(" State: sent ").Append(s_stateSent).Append(" (idle, not sent ").Append(s_stateSkippedIdle).Append(", rate cap ").Append(s_stateRateCapped)
                  .Append("); applied before first AI update ").Append(s_stateAppliedEarly).Append(", late (merged) ").Append(s_stateAppliedLate)
                  .Append("; parked ").Append(s_stateParked).Append(", expired ").Append(s_stateExpired).Append(", rejected ").Append(s_stateRejected)
                  .Append(s_lastReject != null ? " (last: " + s_lastReject + ")" : "").Append('.');
            if (EaseOn())
                sb.Append(" Ease: started ").Append(s_easeStarted).Append(", pops eased ").Append(s_easePops).Append(" (largest ").Append(s_maxPop.ToString("F2"))
                  .Append(" m), teleports left alone ").Append(s_easeTeleports).Append(", finished ").Append(s_easeDone).Append(", table full ").Append(s_easeFull)
                  .Append(", active ").Append(s_easeCount).Append('.');
            LoggerOptions.LogDebug(sb.ToString());
            ResetCounters();
        }

        private static void ResetCounters()
        {
            s_fwdDamage = s_fwdStagger = s_fwdHeal = s_fwdLoopSkipped = s_dedupedAtOwner = s_held_n = s_heldFull = s_heldApplied = s_heldExpired = s_heldWrongSender = 0;
            s_stateSent = s_stateSkippedIdle = s_stateRateCapped = s_stateAppliedEarly = s_stateAppliedLate = s_stateParked = s_stateRejected = s_stateExpired = 0;
            s_lostEdges = s_gainedEdges = s_observerEdges = 0;
            s_easeStarted = s_easePops = s_easeTeleports = s_easeDone = s_easeFull = 0;
            s_lastReject = null;
            s_maxPop = 0f;
        }

        private static void Warn(string where, Exception ex)
        {
            float now = Time.realtimeSinceStartup;
            if (now < s_nextWarn) return;
            s_nextWarn = now + 60f;
            LoggerOptions.LogWarning($"[FightHandoff] {where} threw {ex.GetType().Name}: {ex.Message}; the game's own handling is untouched. Further warnings muted for 60 s.");
        }
    }
}
