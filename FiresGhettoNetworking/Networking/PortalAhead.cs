// [ghost] FGN 1.6.72 ([lead] 11-08; [generator]'s study N4 / [seasons]' SteadyFrame B rows PortalInterest / Prefetch). Our own code.
// Behaviour seen in SteadyFrame 1.2.2 (the client names a destination, the host pushes its buildings); none of its code is used, and the
// rule is the opposite on the point that matters: the client never names a place. It names the portal it stands at; the server checks it
// is really there and resolves the other end itself.
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// "Send Portal Destination Early" (key, default OFF until the A/B; read live on both sides: the client asks, the server sends).
    ///
    /// CLIENT: while the local player stands within 30 m of a connected portal (vanilla's own 0.5 s TeleportWorld.UpdatePortal tick), it tells
    /// the server which PORTAL (its ZDOID) it is at, on the direct server ZRpc, at most once a second and only to a server on our FGN
    /// (FgnVersionGate), refreshed every 2 s while it stays there.
    /// SERVER (dedicated or host): the ask is accepted only from that connection's own peer, at most 1 per second per peer, only for a ZDO of a
    /// portal prefab within 30 m (+5 m slack) of that peer's live character position, and only if that portal is connected; the server reads
    /// the other end from its own ZDO. For the next 8 s it treats that destination as the peer's second reference point: after vanilla built the
    /// peer's normal sync list (its own area, ownership hand-overs, everything vanilla sends first), the floors, walls and terrain edits
    /// (ZDO type Terrain, then Solid) in the destination's 3 x 3 zones that the peer does not have yet are APPENDED, at most 128 per list. They
    /// go out only in the send budget the peer's own area leaves free (vanilla sends a list front to back until the budget is used), so the
    /// player's own surroundings are never delayed. No m_forceSend (no front inserts), no client-chosen position, bounded per-peer state
    /// (cleared when the peer leaves). On arrival the floors are already there, which is what lets FF's "Portal Arrival Ends When The
    /// Destination Is Ready" release early on a client.
    /// Info once on first use (each side); Debug proof every 30 s while anything happened.
    ///
    /// [wishbone] arrival (study N36): "Send Portal Destination On Use" (key, default OFF, both sides). The same ask, on its own RPC
    /// (FGN_PortalUse), sent once by the client from TeleportWorld.Teleport when vanilla actually started the trip (the player is still at the
    /// portal for vanilla's 2 s fade, Player.cs:4488, so the server's 30 m check holds). Server: same validation, at most one per vanilla
    /// teleport cooldown (2 s, Player.cs:4464) per peer, held up to vanilla's 15 s fallback. Both kinds now stop as soon as the player's live
    /// 3 x 3 overlaps the destination's (it arrived: vanilla's list covers it; appending too would list ZDOs twice in one package).
    /// Appends ride inside FGN's SendScheduler budget (the scheduler calls vanilla SendZDOs -> CreateSyncList) and only after SendLater's
    /// compaction; no m_forceSend, no front inserts.
    /// </summary>
    [HarmonyPatch]
    public static class PortalAhead
    {
        public static ConfigEntry<bool> ConfigEnabled;
        public static ConfigEntry<bool> ConfigOnUse;   // [wishbone] arrival (N36)

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("04 - Networking", "Send Portal Destination Early", false,
                Cfg.Adv("While a player stands at a connected portal, the server starts sending the floors and buildings at its other end in spare bandwidth, so arriving is faster. Needs this on the server and the player. Off until measured."));
            ConfigOnUse = config.Bind("04 - Networking", "Send Portal Destination On Use", false,
                Cfg.Adv("When a player steps into a connected portal, the server starts sending the floors and buildings at its other end at once, in spare bandwidth, during the loading screen. Needs this on the server and the player. Off until measured."));
        }

        private static bool On => ConfigEnabled != null && ConfigEnabled.Value;
        private static bool UseOn => ConfigOnUse != null && ConfigOnUse.Value;

        private const string AskRpc = "FGN_PortalAhead";
        private const string UseRpc = "FGN_PortalUse";   // [wishbone] arrival: same payload (the portal's ZDOID), same validation
        private const float NearMetres = 30f, NearSlackMetres = 5f, AskEverySeconds = 2f, HoldSeconds = 8f, MinAskGapSeconds = 1f;
        private const float UseHoldSeconds = 15f;      // vanilla's own teleport fallback (Player.UpdateTeleport, Player.cs:4508): no trip waits longer
        private const float MinUseGapSeconds = 2f;     // vanilla's teleport cooldown (Player.TeleportTo, Player.cs:4464): no player can use portals faster
        private const int MaxAppendPerList = 128;

        // ------------------------------------------------------------------ client: ask at portal use ([wishbone] arrival, N36)
        private static float s_useAskAt = -100f, s_lastPortalUseAt = -100f;

        /// <summary>True when the trip that started at tripStart (unscaled time) began at a portal (TeleportWorld.Teleport).</summary>
        internal static bool TripWasPortal(float tripStart) => Mathf.Abs(s_lastPortalUseAt - tripStart) < 0.5f;
        /// <summary>True when an early-send ask went to the server for the trip that started at tripStart.</summary>
        internal static bool UseAskSentFor(float tripStart) => Mathf.Abs(s_useAskAt - tripStart) < 0.5f;

        [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Teleport)), HarmonyPrefix]
        private static void OnPortalUsePrefix(Player player, out bool __state)
        {
            __state = player != null && player == Player.m_localPlayer && !player.IsTeleporting();
        }

        [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Teleport)), HarmonyPostfix]
        private static void OnPortalUse(TeleportWorld __instance, Player player, bool __state)
        {
            if (!__state || __instance == null || player == null || !player.IsTeleporting()) return;   // vanilla refused (blocked, items, no target)
            float now = Time.unscaledTime;
            s_lastPortalUseAt = now;   // for ArrivalGate's census, key on or off
            if (!UseOn || ZNet.instance == null || ZNet.instance.IsServer()) return;   // hosting: everything is local already
            var view = __instance.GetComponent<ZNetView>();
            var zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null || zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal).IsNone()) return;
            var serverPeer = ZNet.instance.GetServerPeer();
            if (serverPeer == null || !FgnVersionGate.IsMatched(serverPeer)) return;   // a server without our FGN has no handler: send nothing
            var rpc = ZNet.instance.GetServerRPC();
            if (rpc == null) return;
            rpc.Invoke(UseRpc, zdo.m_uid);
            s_useAskAt = now;
            s_asksSent++;
            Proof();
        }

        // ------------------------------------------------------------------ client
        private static ZDOID s_askedPortal = ZDOID.None;
        private static float s_nextAsk;
        private static bool s_clientSaid;
        private static int s_asksSent;

        [HarmonyPatch(typeof(TeleportWorld), "UpdatePortal"), HarmonyPostfix]
        private static void OnPortalTick(TeleportWorld __instance)
        {
            if (!On || __instance == null || ZNet.instance == null || ZNet.instance.IsServer()) return;
            var player = Player.m_localPlayer;
            if (player == null || player.IsTeleporting()) return;
            if ((player.transform.position - __instance.transform.position).sqrMagnitude > NearMetres * NearMetres) return;
            var view = __instance.GetComponent<ZNetView>();
            var zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null || zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal).IsNone()) return;
            float now = Time.unscaledTime;
            if (zdo.m_uid == s_askedPortal && now < s_nextAsk) return;
            if (zdo.m_uid != s_askedPortal && now < s_nextAsk - AskEverySeconds + MinAskGapSeconds) return;   // a different portal: still at most 1/s
            var serverPeer = ZNet.instance.GetServerPeer();
            if (serverPeer == null || !FgnVersionGate.IsMatched(serverPeer)) return;   // a server without our FGN has no handler
            var rpc = ZNet.instance.GetServerRPC();
            if (rpc == null) return;
            rpc.Invoke(AskRpc, zdo.m_uid);
            s_askedPortal = zdo.m_uid;
            s_nextAsk = now + AskEverySeconds;
            s_asksSent++;
            if (!s_clientSaid) { s_clientSaid = true; LoggerOptions.LogInfo("[PortalAhead] asked the server to start sending this portal's destination while you stand here."); }
            Proof();
        }

        // ------------------------------------------------------------------ server
        private struct Ahead { public Vector3 Target; public float Until; public float LastAsk; public float LastUse; public bool FromUse; public ZDOID Portal; }
        private static readonly Dictionary<long, Ahead> s_ahead = new Dictionary<long, Ahead>();
        private static readonly List<long> s_gone = new List<long>();
        private static readonly HashSet<int> s_portalPrefabs = new HashSet<int>();
        private static int s_portalScanned;
        private static readonly List<ZDO> s_scratch = new List<ZDO>();
        private static readonly List<ZDO> s_terrain = new List<ZDO>(), s_solid = new List<ZDO>();
        private static bool s_serverSaid;
        private static int s_accepted, s_refused, s_appended, s_useAccepted, s_arrived;
        private static float s_nextProof, s_nextPrune;

        [HarmonyPatch(typeof(ZNet), "OnNewConnection"), HarmonyPostfix]
        private static void OnNewConnection(ZNet __instance, ZNetPeer peer)
        {
            if (__instance == null || !__instance.IsServer() || peer?.m_rpc == null) return;
            peer.m_rpc.Register<ZDOID>(AskRpc, OnAsk);   // registered always (cheap); the key is read on each ask
            peer.m_rpc.Register<ZDOID>(UseRpc, OnUse);   // [wishbone] arrival: likewise
        }

        private static void OnAsk(ZRpc rpc, ZDOID portalId) => Accept(rpc, portalId, false);
        private static void OnUse(ZRpc rpc, ZDOID portalId) => Accept(rpc, portalId, true);

        private static void Accept(ZRpc rpc, ZDOID portalId, bool fromUse)
        {
            if (!(fromUse ? UseOn : On) || ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null) return;
            var peer = ZNet.instance.GetPeer(rpc);   // the connection itself: a client can only ask for itself
            if (peer == null || !peer.IsReady()) return;
            float now = Time.unscaledTime;
            bool known = s_ahead.TryGetValue(peer.m_uid, out var have);
            if (known && !fromUse && now - have.LastAsk < MinAskGapSeconds) { s_refused++; return; }   // stand asks: 1 per second per peer
            if (known && fromUse && now - have.LastUse < MinUseGapSeconds) { s_refused++; return; }    // use asks: 1 per vanilla teleport cooldown
            ScanPortalPrefabs();
            var portal = ZDOMan.instance.GetZDO(portalId);
            if (portal == null || !s_portalPrefabs.Contains(portal.GetPrefab())) { s_refused++; return; }
            Vector3 at = SyncListRefPosPatches.LiveRefPos(peer);
            float reach = NearMetres + NearSlackMetres;
            if ((at - portal.GetPosition()).sqrMagnitude > reach * reach) { s_refused++; return; }   // not standing at that portal
            var other = ZDOMan.instance.GetZDO(portal.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal));
            if (other == null) { s_refused++; return; }
            Vector3 target = other.GetPosition();
            Vector2s tz = ZoneSystem.GetZone(target), pz = ZoneSystem.GetZone(at);
            if (Mathf.Abs(tz.x - pz.x) <= 2 && Mathf.Abs(tz.y - pz.y) <= 2) { s_refused++; return; }   // the two 3 x 3 areas overlap: vanilla sends it anyway (and nothing is listed twice)
            if (!fromUse && known && have.FromUse && now <= have.Until) { s_refused++; return; }   // a running use hold stays as it is
            s_ahead[peer.m_uid] = fromUse
                ? new Ahead { Target = target, Until = now + UseHoldSeconds, LastAsk = known ? have.LastAsk : -100f, LastUse = now, FromUse = true, Portal = portalId }
                : new Ahead { Target = target, Until = now + HoldSeconds, LastAsk = now, LastUse = known ? have.LastUse : -100f, FromUse = false, Portal = portalId };
            s_accepted++;
            if (fromUse) s_useAccepted++;
            if (!s_serverSaid)
            {
                s_serverSaid = true;
                LoggerOptions.LogInfo($"[PortalAhead] first early send: {(string.IsNullOrEmpty(peer.m_playerName) ? peer.m_uid.ToString() : peer.m_playerName)} stands at a portal; its destination's floors and buildings go out in spare bandwidth.");
            }
        }

        [HarmonyPatch(typeof(ZDOMan), "CreateSyncList"), HarmonyPostfix]
        private static void AfterSyncList(ZDOMan.ZDOPeer peer, List<ZDO> toSync)
        {
            if (s_ahead.Count == 0 || peer == null || peer.m_peer == null || toSync == null) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            float now = Time.unscaledTime;
            if (now >= s_nextPrune) Prune(now);
            if (!s_ahead.TryGetValue(peer.m_peer.m_uid, out var ahead)) return;
            if (!(ahead.FromUse ? UseOn : On) || now > ahead.Until) { s_ahead.Remove(peer.m_peer.m_uid); return; }
            // [wishbone] arrival: once the player's own 3 x 3 overlaps the destination's (it arrived), vanilla's own list covers it; appending
            // as well would list the same ZDOs twice in one package (vanilla's SendZDOs does not de-duplicate, ZDOMan.cs:845-863).
            Vector2s tz = ZoneSystem.GetZone(ahead.Target), pz = ZoneSystem.GetZone(SyncListRefPosPatches.LiveRefPos(peer.m_peer));
            if (Mathf.Abs(tz.x - pz.x) <= 2 && Mathf.Abs(tz.y - pz.y) <= 2) { s_ahead.Remove(peer.m_peer.m_uid); s_arrived++; return; }

            s_scratch.Clear(); s_terrain.Clear(); s_solid.Clear();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(ahead.Target), new SimulationDistance(1, 0), s_scratch);
            for (int i = 0; i < s_scratch.Count; i++)
            {
                var z = s_scratch[i];
                if (z.Type == ZDO.ObjectType.Terrain) { if (peer.ShouldSend(z)) s_terrain.Add(z); }
                else if (z.Type == ZDO.ObjectType.Solid) { if (peer.ShouldSend(z)) s_solid.Add(z); }
            }
            int added = 0;
            for (int i = 0; i < s_terrain.Count && added < MaxAppendPerList; i++, added++) toSync.Add(s_terrain[i]);   // terrain edits first: the ground
            for (int i = 0; i < s_solid.Count && added < MaxAppendPerList; i++, added++) toSync.Add(s_solid[i]);       // then floors and walls
            s_appended += added;
            if (added == 0 && s_terrain.Count == 0 && s_solid.Count == 0) s_ahead.Remove(peer.m_peer.m_uid);   // everything there is already sent
            Proof();
        }

        private static void Prune(float now)
        {
            s_nextPrune = now + 5f;
            s_gone.Clear();
            foreach (var kv in s_ahead)
                if (now > kv.Value.Until || ZNet.instance.GetPeer(kv.Key) == null) s_gone.Add(kv.Key);   // expired, or the peer left
            foreach (var id in s_gone) s_ahead.Remove(id);
        }

        private static void ScanPortalPrefabs()
        {
            var list = ZNetScene.instance != null ? ZNetScene.instance.m_prefabs : null;
            if (list == null || list.Count <= s_portalScanned) return;
            for (int i = s_portalScanned; i < list.Count; i++)
            {
                var prefab = list[i];
                if (prefab != null && prefab.GetComponentInChildren<TeleportWorld>(true) != null) s_portalPrefabs.Add(prefab.name.GetStableHashCode());
            }
            s_portalScanned = list.Count;
        }

        internal static void OnShutdown()
        {
            s_ahead.Clear();
            s_portalPrefabs.Clear();
            s_portalScanned = 0;
            s_askedPortal = ZDOID.None;
            s_useAskAt = -100f; s_lastPortalUseAt = -100f;
        }

        private static void Proof()
        {
            float now = Time.unscaledTime;
            if (now < s_nextProof) return;
            s_nextProof = now + 30f;
            if (s_accepted + s_refused + s_appended + s_asksSent == 0 || !LoggerOptions.DebugEnabled) return;
            LoggerOptions.LogDebug($"[PortalAhead] last 30 s: asks sent {s_asksSent}; asks accepted {s_accepted} (of them at portal use {s_useAccepted}), refused {s_refused} (rate, not at a portal, not connected, same area, use hold running); objects sent early {s_appended}; stopped on arrival {s_arrived}; players being served {s_ahead.Count}");
            s_asksSent = s_accepted = s_refused = s_appended = s_useAccepted = s_arrived = 0;
        }
    }
}
