// [ghost] FGN 1.6.69 ([lead] / [fgn] split 11-08; study Tools\STUDY_sections\other_NRF_VPE_STL.md items 3, 5 and 6). Our own code.
// Behaviour learned from DeathMonger SpreadTheLoad 0.3.0 (no license: all rights reserved, so only its observable behaviour was used,
// none of its code). Each rule here is narrower than theirs: ships only on the helm event, creatures only inside CreatureOwnership's
// own idle / hold / fight guard, and an in-use veto for every FGN hand-off.
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// (A) Server hands a ship to its helmsman when the ship's owner is a client without our FGN (study item 3).
    ///     HelmOwnership's own hand-over runs on the OWNING game, so a vanilla (or other-FGN) owner keeps the ship and the helmsman steers
    ///     through it with a round trip of lag. The server sees the helm request pass through it (routed "RequestControl", target = the
    ///     ship's owner); it remembers that request (bounded, 10 s), and once the owner's game has granted the helm (the ship ZDO's user is the
    ///     requester's player id) while that owner is still a different, non-FGN client, the server gives the ship to the requester, once.
    ///     Event-driven: nothing runs while no request is pending (no world sweep). Only ship prefabs, only to the player at the helm, who must
    ///     be aboard (character within 15 m of the ship), never from the server or from an FGN owner (their own path does it better: state
    ///     push + steering pass-on). Key ON (a fix that acts only where FGN's own hand-over cannot).
    /// (B) Creatures go to the player touching them (study item 5): inside CreatureOwnership's balancer, as a third reason after "fighting"
    ///     and before "lower ping": a player whose character stands within 2 m (flat) of an idle creature, while its owner's character is
    ///     more than 5 m away (2 / 5 m = hysteresis). It only runs where that balancer already decided a move is allowed (not fighting, held
    ///     30 s, untamed, not ridden or possessed, both sides on our FGN, owner-pushed hand-off), so it adds no new way to move anything.
    ///     Key OFF (behaviour change); needs "Balance Creature Ownership" on.
    /// (C) Never move an object someone is using (study item 6): one GetInt read; a ZDO whose InUse is 1 (an open chest, an occupied station)
    ///     is never handed by FGN's balancer or by an add-on's FgnApi hand-off. Always on (a guard, no key).
    /// Info once on first use; Debug proof every 30 s while anything happened.
    /// </summary>
    public static class GhostOwner169
    {
        public static ConfigEntry<bool> ConfigHelmFallback;
        public static ConfigEntry<bool> ConfigTouchOwner;

        public static void InitConfig(ConfigFile config)
        {
            ConfigHelmFallback = config.Bind("11 - Ship Fixes", "Server Hands Ship To Helmsman For Unmodded Owners", true,
                Cfg.Adv("When the player who owns a ship has no FGN, the server gives the ship to whoever takes its helm, so steering never lags. Ships whose owner runs FGN hand themselves over already."));
            ConfigTouchOwner = config.Bind("10 - Server Authority", "Creatures Go To The Player Touching Them", false,
                Cfg.Adv("With 'Balance Creature Ownership' on: an idle creature a player stands right next to (2 m) goes to that player when its owner is further away (5 m). Fights and the 30 s hold still keep it where it is."));
        }

        internal static void OnShutdown()
        {
            s_requests.Clear();
            s_shipPrefabs.Clear();
            s_shipScanned = 0;
        }

        // ---------------------------------------------------------------- (C) in-use veto
        internal static bool InUse(ZDO zdo) => zdo != null && zdo.GetInt(ZDOVars.s_inUse) == 1;

        // ---------------------------------------------------------------- (A) helm fallback
        private static readonly int s_requestControlHash = "RequestControl".GetStableHashCode();
        private const double RequestSeconds = 10.0;
        private const int MaxRequests = 64;
        private const float AboardMetres = 15f;
        private const double TickSeconds = 0.25;

        private struct Request { public long Requester; public double At; }
        private static readonly Dictionary<ZDOID, Request> s_requests = new Dictionary<ZDOID, Request>();
        private static readonly List<ZDOID> s_done = new List<ZDOID>();
        private static readonly HashSet<int> s_shipPrefabs = new HashSet<int>();
        private static int s_shipScanned;
        private static double s_nextTick, s_nextProof;
        private static bool s_helmSaid, s_touchSaid;
        private static int s_seen, s_handed, s_ownerHasFgn, s_expired, s_notAboard;

        /// <summary>From RoutedRpcManager.ProcessRoutedRPC (server), next to CreatureOwnership.ObserveRoutedRpc: remember helm requests.</summary>
        internal static void ObserveRoutedRpc(ZRoutedRpc.RoutedRPCData data)
        {
            if (data == null || data.m_methodHash != s_requestControlHash || data.m_targetZDO.IsNone()) return;
            if (ConfigHelmFallback == null || !ConfigHelmFallback.Value || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            long requester = data.m_senderPeerID;
            if (requester == 0L || requester == ZDOMan.GetSessionID()) return;
            if (s_requests.Count >= MaxRequests) DropOldest();
            s_requests[data.m_targetZDO] = new Request { Requester = requester, At = Time.realtimeSinceStartupAsDouble };
            s_seen++;
        }

        /// <summary>From CreatureOwnership.Balance (server, every ZDOMan update), next to HelmOwnership.ReturnEmptyShipsToServer.</summary>
        internal static void HelmFallbackTick()
        {
            if (s_requests.Count == 0) { Proof(); return; }
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < s_nextTick) return;
            s_nextTick = now + TickSeconds;
            if (ZDOMan.instance == null || ZNet.instance == null || ConfigHelmFallback == null || !ConfigHelmFallback.Value) { s_requests.Clear(); return; }
            ScanShipPrefabs();
            s_done.Clear();
            foreach (var entry in s_requests)
            {
                if (now - entry.Value.At > RequestSeconds) { s_done.Add(entry.Key); s_expired++; continue; }
                string outcome = TryHand(entry.Key, entry.Value.Requester);
                if (outcome != null) s_done.Add(entry.Key);
            }
            foreach (var id in s_done) s_requests.Remove(id);
            Proof();
        }

        // null = keep waiting (the owner has not granted the helm yet); otherwise the request is finished.
        private static string TryHand(ZDOID id, long requester)
        {
            var zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null || !s_shipPrefabs.Contains(zdo.GetPrefab())) return "not a ship";
            long owner = zdo.GetOwner();
            if (owner == requester) return "already the helmsman's";
            long server = ZDOMan.GetSessionID();
            if (owner == 0L || owner == server) return "server or no owner";   // not this rule's case
            var ownerPeer = ZNet.instance.GetPeer(owner);
            if (ownerPeer == null) return "owner gone";
            if (FgnVersionGate.IsMatched(ownerPeer)) { s_ownerHasFgn++; return "owner runs FGN"; }   // HelmOwnership hands it over on that game
            var requesterPeer = ZNet.instance.GetPeer(requester);
            if (requesterPeer == null || !requesterPeer.IsReady()) return "requester gone";
            long user = zdo.GetLong(ZDOVars.s_user);
            if (user == 0L) return null;   // the owner's game has not answered yet
            var character = ZDOMan.instance.GetZDO(requesterPeer.m_characterID);
            if (character == null || character.GetLong(ZDOVars.s_playerID) != user) return "someone else has the helm";
            if ((character.GetPosition() - zdo.GetPosition()).sqrMagnitude > AboardMetres * AboardMetres) { s_notAboard++; return "helmsman not aboard"; }

            ZDOMan.instance.ForceSendZDO(id);
            zdo.SetOwner(requester);
            s_handed++;
            string who = string.IsNullOrEmpty(requesterPeer.m_playerName) ? requester.ToString() : requesterPeer.m_playerName;
            if (!s_helmSaid)
            {
                s_helmSaid = true;
                LoggerOptions.LogInfo($"[Helm] first server hand-over: the {PrefabName(zdo)} went to {who}, who took its helm; its owner had no FGN to hand it over itself.");
            }
            else LoggerOptions.LogDebug($"[Helm] server handed the {PrefabName(zdo)} to {who} at its helm (owner {owner} has no FGN).");
            return "handed";
        }

        private static void DropOldest()
        {
            ZDOID oldest = ZDOID.None; double at = double.MaxValue;
            foreach (var entry in s_requests) if (entry.Value.At < at) { at = entry.Value.At; oldest = entry.Key; }
            if (!oldest.IsNone()) s_requests.Remove(oldest);
        }

        // ships another mod registers late are picked up: only the new tail of ZNetScene.m_prefabs is looked at when it grows
        private static void ScanShipPrefabs()
        {
            var list = ZNetScene.instance != null ? ZNetScene.instance.m_prefabs : null;
            if (list == null || list.Count <= s_shipScanned) return;
            for (int i = s_shipScanned; i < list.Count; i++)
            {
                var prefab = list[i];
                if (prefab != null && prefab.GetComponent<Ship>() != null) s_shipPrefabs.Add(prefab.name.GetStableHashCode());
            }
            s_shipScanned = list.Count;
        }

        private static string PrefabName(ZDO zdo)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            return prefab != null ? prefab.name : "ship";
        }

        // ---------------------------------------------------------------- (B) touch rule
        private const float TouchMetres = 2f, OwnerAwayMetres = 5f;
        private static int s_touchMoves;

        /// <summary>
        /// From CreatureOwnership.TryHandOffCreature, after its own guards passed (idle, held 30 s, 2+ players near): the candidate whose character
        /// stands within 2 m (flat) of the creature while the owner's character is more than 5 m away; null = no toucher (or the key is off).
        /// Only peers on our FGN (the hand-off is FGN's own RPC).
        /// </summary>
        internal static ZNetPeer Toucher(ZDO creature, ZNetPeer ownerPeer, List<ZNetPeer> candidates)
        {
            if (ConfigTouchOwner == null || !ConfigTouchOwner.Value || creature == null || ownerPeer == null || ZDOMan.instance == null) return null;
            if (InUse(creature)) return null;
            Vector3 at = creature.GetPosition();
            var ownerChar = ZDOMan.instance.GetZDO(ownerPeer.m_characterID);
            if (ownerChar != null && FlatSqr(ownerChar.GetPosition(), at) <= OwnerAwayMetres * OwnerAwayMetres) return null;
            ZNetPeer best = null; float bestSqr = TouchMetres * TouchMetres;
            for (int i = 0; i < candidates.Count; i++)
            {
                var peer = candidates[i];
                if (peer == null || peer == ownerPeer || !FgnVersionGate.IsMatched(peer)) continue;
                var ch = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (ch == null) continue;
                float d = FlatSqr(ch.GetPosition(), at);
                if (d <= bestSqr) { bestSqr = d; best = peer; }
            }
            if (best != null)
            {
                s_touchMoves++;
                if (!s_touchSaid) { s_touchSaid = true; LoggerOptions.LogInfo($"[CreatureOwnership] first touch hand-off: a creature goes to {(string.IsNullOrEmpty(best.m_playerName) ? best.m_uid.ToString() : best.m_playerName)}, who stands next to it."); }
            }
            return best;
        }

        private static float FlatSqr(Vector3 a, Vector3 b) { float x = a.x - b.x, z = a.z - b.z; return x * x + z * z; }

        // ---------------------------------------------------------------- proof
        private static void Proof()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < s_nextProof) return;
            s_nextProof = now + 30.0;
            if (s_seen + s_handed + s_touchMoves == 0 || !LoggerOptions.DebugEnabled) return;
            LoggerOptions.LogDebug($"[GhostOwner] last 30 s: helm requests seen {s_seen}, ships handed by the server {s_handed}, left to their FGN owner {s_ownerHasFgn}, helmsman not aboard {s_notAboard}, expired {s_expired}; creatures picked for touch {s_touchMoves}");
            s_seen = s_handed = s_ownerHasFgn = s_notAboard = s_expired = s_touchMoves = 0;
        }
    }
}
