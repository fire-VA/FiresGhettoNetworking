// Idea from [generator]'s SpreadTheLoad study (Tools\STUDY_sections\other_NRF_VPE_STL.md 3.7). SpreadTheLoad has no licence: behaviour only;
// this is our own design and code, and it avoids their bug (theirs moved ANY non-Character target, including building pieces, carts and ships).
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.69 ([fgn], Fire 11-08). "Resources Go To Whoever Hits Them" (dedicated server): when a player chops a tree / log or mines a rock
    /// that ANOTHER player's game owns, every hit goes over the network to that owner and back, so hits feel late. A resource's whole state
    /// (health, mined areas) lives in its ZDO, so the server hands it to the hitter after the hit has landed: ForceSendZDO, then SetOwner (the
    /// way FGN's helm handoff does it), at most once per DwellSeconds per object.
    /// Only RESOURCES move: prefabs with TreeBase, TreeLog, MineRock or MineRock5, plus Destructible prefabs that are none of WearNTear (pieces,
    /// ships, carts), Character, Ship, Vagon, Container, Piece. Classified by ZDO prefab hash, once per hash. Never moved: a resource the server
    /// itself owns (server-side simulation keeps those), an unowned one (vanilla gives it to the nearest player anyway), or one whose hitter is
    /// not a connected peer. Observed in FGN's own routed-RPC pass (no new patch on RouteRPC). Bounded: 256 pending, 1024 dwell entries pruned
    /// by age. Info once on the first move; Debug per 30 s with counts.
    /// </summary>
    internal static class ResourceOwnership
    {
        internal static BepInEx.Configuration.ConfigEntry<bool> ConfigEnabled;
        private const float SettleSeconds = 0.4f, DwellSeconds = 5f;
        private const int MaxPending = 256, MaxDwell = 1024;
        private static readonly int s_damageHash = "RPC_Damage".GetStableHashCode();

        private struct Pending { public ZDOID Zdo; public long Hitter; public double At; }
        private static readonly Queue<Pending> s_pending = new Queue<Pending>();
        private static readonly Dictionary<ZDOID, double> s_movedAt = new Dictionary<ZDOID, double>();
        private static readonly Dictionary<int, bool> s_isResource = new Dictionary<int, bool>();
        private static readonly List<ZDOID> s_prune = new List<ZDOID>();
        private static int s_moved, s_skippedOwnerServer, s_skippedDwell, s_skippedNotResource, s_dropped;
        private static double s_nextLog;
        private static bool s_said;

        internal static void InitConfig(BepInEx.Configuration.ConfigFile config)
        {
            ConfigEnabled = config.Bind("10 - Server Authority", "Resources Go To Whoever Hits Them", false,
                "Dedicated server: a tree, log or rock that another player's game looks after is handed to the player chopping or mining it, so " +
                "the hits land at once instead of after a round trip. Only resources: building pieces, ships, carts, chests and creatures are never moved. Off until the A/B.");
        }

        private static bool Active => ConfigEnabled != null && ConfigEnabled.Value && ZNet.instance != null && ZNet.instance.IsDedicated();

        /// <summary>From RoutedRpcManager.ProcessRoutedRPC, every routed RPC the server relays.</summary>
        internal static void Observe(ZRoutedRpc.RoutedRPCData data)
        {
            if (data == null || data.m_methodHash != s_damageHash || data.m_targetZDO.IsNone() || !Active) return;
            if (s_pending.Count >= MaxPending) { s_pending.Dequeue(); s_dropped++; }   // the oldest swing goes, the newest stays
            s_pending.Enqueue(new Pending { Zdo = data.m_targetZDO, Hitter = data.m_senderPeerID, At = Time.realtimeSinceStartupAsDouble });
        }

        /// <summary>From FGN's ZDOMan.Update postfix.</summary>
        internal static void Tick(ZDOMan man)
        {
            if (s_pending.Count == 0 && s_movedAt.Count == 0) return;
            if (!Active || man == null) { s_pending.Clear(); return; }
            double now = Time.realtimeSinceStartupAsDouble;
            long server = ZDOMan.GetSessionID();
            while (s_pending.Count > 0 && now - s_pending.Peek().At >= SettleSeconds)
            {
                Pending p = s_pending.Dequeue();
                ZDO zdo = man.GetZDO(p.Zdo);
                if (zdo == null || !zdo.IsValid() || !zdo.Persistent) continue;
                if (!IsResource(zdo.GetPrefab())) { s_skippedNotResource++; continue; }
                if (GhostOwner169.InUse(zdo)) continue;   // [ghost]'s in-use veto (study item 6)
                long owner = zdo.GetOwner();
                if (owner == p.Hitter || owner == 0L) continue;
                if (owner == server) { s_skippedOwnerServer++; continue; }
                if (s_movedAt.TryGetValue(p.Zdo, out double at) && now - at < DwellSeconds) { s_skippedDwell++; continue; }
                ZNetPeer peer = ZNet.instance.GetPeer(p.Hitter);
                if (peer == null || !peer.IsReady()) continue;
                man.ForceSendZDO(zdo.m_uid);
                zdo.SetOwner(p.Hitter);
                s_movedAt[p.Zdo] = now; s_moved++;
                if (!s_said) { s_said = true; LoggerOptions.LogInfo($"[ResourceOwner] first resource handed to the player hitting it ({PrefabName(zdo)} to {peer.m_playerName})"); }
            }
            if (s_movedAt.Count > MaxDwell) Prune(now);
            if (now >= s_nextLog)
            {
                if (s_nextLog > 0 && LoggerOptions.DebugEnabled && (s_moved + s_skippedOwnerServer + s_skippedDwell + s_dropped) > 0)
                    LoggerOptions.LogDebug($"[ResourceOwner] last 30 s: {s_moved} resource(s) handed to the player hitting them; skipped {s_skippedOwnerServer} server-owned, {s_skippedDwell} moved in the last {DwellSeconds:0} s, {s_skippedNotResource} hit(s) on non-resources; {s_dropped} pending dropped (cap {MaxPending})");
                s_moved = s_skippedOwnerServer = s_skippedDwell = s_skippedNotResource = s_dropped = 0;
                s_nextLog = now + 30.0;
                if (s_movedAt.Count > 0) Prune(now);
            }
        }

        private static void Prune(double now)
        {
            s_prune.Clear();
            foreach (var kv in s_movedAt) if (now - kv.Value >= DwellSeconds) s_prune.Add(kv.Key);
            foreach (var id in s_prune) s_movedAt.Remove(id);
            s_prune.Clear();
        }

        private static bool IsResource(int hash)
        {
            if (s_isResource.TryGetValue(hash, out bool r)) return r;
            r = false;
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
            if (prefab != null)
            {
                bool excluded = prefab.GetComponent<WearNTear>() || prefab.GetComponent<Character>() || prefab.GetComponent<Ship>()
                                || prefab.GetComponent<Vagon>() || prefab.GetComponent<Container>() || prefab.GetComponent<Piece>();
                bool resource = prefab.GetComponent<TreeBase>() || prefab.GetComponent<TreeLog>() || prefab.GetComponent<MineRock>()
                                || prefab.GetComponent<MineRock5>() || prefab.GetComponent<Destructible>();
                r = resource && !excluded;
                s_isResource[hash] = r;   // bounded by the prefab count
            }
            return r;   // a prefab not loaded yet is asked again next time
        }

        private static string PrefabName(ZDO zdo)
        {
            try { GameObject p = ZNetScene.instance.GetPrefab(zdo.GetPrefab()); return p != null ? p.name : zdo.GetPrefab().ToString(); } catch { return "?"; }
        }

        internal static void OnShutdown() { s_pending.Clear(); s_movedAt.Clear(); s_isResource.Clear(); }
    }
}
