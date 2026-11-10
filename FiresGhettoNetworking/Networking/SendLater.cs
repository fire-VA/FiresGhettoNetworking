// [wishbone] sendeff drop (rows N25 / N27 / N28, round 2 study). Our own code, written against the vanilla 1.0.15 decompile only.
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// "Send Far Idle Changes Later" (N28, merged with N27's send half). SERVER side (dedicated or host); off by default.
    ///
    /// What vanilla already does (read in ZDO.cs / ZDOMan.cs 1.0.15):
    ///   - ZDO.Set* writes the value at once (ZDOExtraData), and only bumps DataRevision when the value really changed
    ///     (ZDOExtraData.Set returns false on an equal value). Readers always see the new value immediately.
    ///   - A burst of writes to one ZDO in one frame bumps the revision N times but is sent ONCE: SendZDOs runs per 50 ms
    ///     slice and ZDOPeer.ShouldSend only compares "revision now > revision this peer was sent". So "one send per frame
    ///     burst" (N27) is vanilla already; collapsing the bump counter itself saves no byte and would break code that watches
    ///     DataRevision (Container, Door, Sign, Turret, ItemDrop, TerrainComp, ZSyncTransform), so it is not done.
    ///   - ShouldSend is an exact per-peer check on data AND owner revision (N25): nothing a peer already holds is resent.
    ///
    /// What this adds: an object that a player already has, that is far from that player (default over 80 m), persistent, not
    /// Prioritized, not owned by that player, and whose prefab has no Character / Projectile / ZSyncTransform / ZSyncAnimation /
    /// Ship / Vagon / ItemDrop (so never a creature, player, projectile, moving body or fight object), and whose change is a
    /// DATA change only (an owner change is never held), is held out of that player's update list for at most
    /// "Send Later Max Delay Ms" (default 400 ms) from the first change seen. Every write inside that window rides one send.
    /// The value is never held back on this machine; only the copy to that player goes out later, and it always carries the
    /// newest revision (the object stays dirty for that player; vanilla rescans it every slice), so there is no lost final state.
    /// Forced sends (m_forceSend, RPC_RequestZDO), new objects, and objects coming near are never held.
    ///
    /// Not hooked: ShouldSend (on a client a false there drops the object from m_clientChangeQueue for good). The hold is a
    /// filter on the finished list in a CreateSyncList postfix, Priority.High: after vanilla's sort (and ZDOThrottlingPatches'
    /// / FightNet's sort postfixes) and AddForceSendZdos, before PortalAhead appends. Order of what remains is untouched.
    /// </summary>
    [HarmonyPatch]
    public static class SendLater
    {
        public static ConfigEntry<bool> ConfigEnabled;
        public static ConfigEntry<int> ConfigMaxDelayMs;
        public static ConfigEntry<float> ConfigMinDistance;

        private const int MinDelayMs = 100, MaxDelayMs = 1000;
        private const int MaxEntriesPerPeer = 8192;
        private const float SweepEvery = 2f, StaleAfter = 10f, ReportEvery = 10f;
        // Bytes one object costs in a ZDOData package before its fields: ZDOID 12 + owner rev 2 + data rev 4 + owner 8 +
        // position 12 + package length 4, plus at least flags 2 + prefab 4 of the body. A lower bound for one avoided send.
        private const int MinBytesPerSend = 48;

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("04 - Networking", "Send Far Idle Changes Later", false,
                Cfg.Adv("Server: changes to far, still objects (buildings, chests, doors, fires, terrain edits; never creatures, players, projectiles or anything moving) "
                    + "are sent to a player at most a fraction of a second later, so several changes go as one. The server itself always has the new value. Off until measured. Read at start."));
            ConfigMaxDelayMs = config.Bind("04 - Networking", "Send Later Max Delay Ms", 400,
                Cfg.Adv("Longest a far object's change waits before it is sent to a player.", new AcceptableValueRange<int>(MinDelayMs, MaxDelayMs)));
            ConfigMinDistance = config.Bind("04 - Networking", "Send Later Min Distance", 80f,
                Cfg.Adv("Only objects at least this many metres from the player are held.", new AcceptableValueRange<float>(40f, 300f)));
        }

        internal static bool KeyOnAtStart => ConfigEnabled != null && ConfigEnabled.Value;
        private static bool On => ConfigEnabled != null && ConfigEnabled.Value;

        private struct Hold
        {
            public float First;          // unscaled time the first held change was seen
            public uint LastSeenRev;     // newest revision seen while holding
            public uint ReleasedRev;     // revision let through at release
            public int WouldSends;       // sends vanilla would have made in the window
            public bool Released;
        }

        private static readonly Dictionary<long, Dictionary<ZDOID, Hold>> s_peers = new Dictionary<long, Dictionary<ZDOID, Hold>>();
        private static readonly Stack<Dictionary<ZDOID, Hold>> s_pool = new Stack<Dictionary<ZDOID, Hold>>();
        private static readonly Dictionary<int, bool> s_eligible = new Dictionary<int, bool>();
        private static readonly List<ZDOID> s_dropIds = new List<ZDOID>();
        private static readonly List<long> s_dropPeers = new List<long>();
        private static ZDOMan s_man;
        private static float s_nextSweep, s_nextReport;
        private static bool s_threwSaid;

        // census (per 10 s)
        private static long s_holdsStarted, s_heldObservations, s_released, s_releasedNear, s_sendsAvoided, s_tableFull;
        private static float s_worstDelay, s_delaySum;

        [HarmonyPatch(typeof(ZDOMan), "CreateSyncList"), HarmonyPostfix, HarmonyPriority(Priority.High)]
        private static void AfterSyncList(ZDOMan __instance, ZDOMan.ZDOPeer peer, List<ZDO> toSync)
        {
            if (!On) return;
            if (toSync == null || toSync.Count == 0 || peer?.m_peer == null) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            try
            {
                Filter(__instance, peer, toSync);
            }
            catch (System.Exception ex)
            {
                if (!s_threwSaid) { s_threwSaid = true; LoggerOptions.LogWarning($"[SendLater] threw, the list is sent as the game built it: {ex.Message}"); }
            }
        }

        private static void Filter(ZDOMan man, ZDOMan.ZDOPeer peer, List<ZDO> toSync)
        {
            if (man != s_man) { ClearAll(); s_man = man; }   // ZDOID hashes read the session's user table: never keep keys across sessions
            float now = Time.unscaledTime;
            if (now >= s_nextSweep) Sweep(now);
            if (now >= s_nextReport) Report(now);

            float maxDelay = Mathf.Clamp(ConfigMaxDelayMs.Value, MinDelayMs, MaxDelayMs) / 1000f;
            float minDist = Mathf.Max(40f, ConfigMinDistance.Value);
            float minDistSqr = minDist * minDist;
            Vector3 refPos = SyncListRefPosPatches.LiveRefPos(peer.m_peer);
            long uid = peer.m_peer.m_uid;

            if (!s_peers.TryGetValue(uid, out var map))
            {
                map = s_pool.Count > 0 ? s_pool.Pop() : new Dictionary<ZDOID, Hold>();
                s_peers[uid] = map;
            }

            int write = 0;
            int count = toSync.Count;
            for (int i = 0; i < count; i++)
            {
                ZDO z = toSync[i];
                if (z == null || Keep(z, peer, uid, map, refPos, minDistSqr, maxDelay, now)) toSync[write++] = z;
            }
            if (write < count) toSync.RemoveRange(write, count - write);
        }

        // true = goes out in this list (as vanilla); false = held for this peer.
        private static bool Keep(ZDO z, ZDOMan.ZDOPeer peer, long uid, Dictionary<ZDOID, Hold> map, Vector3 refPos, float minDistSqr, float maxDelay, float now)
        {
            ZDOID id = z.m_uid;
            if (!peer.m_zdos.TryGetValue(id, out var info)) return true;                       // peer does not have it: create now
            uint rev = z.DataRevision;
            bool dataOnly = z.OwnerRevision <= info.m_ownerRevision && rev > info.m_dataRevision;
            bool candidate = dataOnly
                && !peer.m_forceSend.Contains(id)
                && z.Persistent
                && z.Type != ZDO.ObjectType.Prioritized
                && z.GetOwner() != uid
                && (z.GetPosition() - refPos).sqrMagnitude >= minDistSqr
                && Eligible(z.GetPrefab());

            bool has = map.TryGetValue(id, out var h);
            if (!candidate)
            {
                if (has && !h.Released) { Release(ref h, rev, now); s_releasedNear++; map[id] = h; }
                return true;
            }

            if (has && h.Released)
            {
                if (info.m_dataRevision < h.ReleasedRev) return true;                          // released, not yet written (package full): never hold twice
                has = false;                                                                    // that release was sent: this is a new change
            }

            if (!has)
            {
                if (map.Count >= MaxEntriesPerPeer && !map.ContainsKey(id)) { s_tableFull++; return true; }
                map[id] = new Hold { First = now, LastSeenRev = rev, WouldSends = 1 };
                s_holdsStarted++;
                s_heldObservations++;
                return false;
            }

            if (rev > h.LastSeenRev) { h.WouldSends++; h.LastSeenRev = rev; }
            if (now - h.First >= maxDelay)
            {
                Release(ref h, rev, now);
                map[id] = h;
                return true;
            }
            map[id] = h;
            s_heldObservations++;
            return false;
        }

        private static void Release(ref Hold h, uint rev, float now)
        {
            if (rev > h.LastSeenRev) { h.WouldSends++; h.LastSeenRev = rev; }
            h.Released = true;
            h.ReleasedRev = rev;
            float delay = now - h.First;
            if (delay > s_worstDelay) s_worstDelay = delay;
            s_delaySum += delay;
            s_released++;
            if (h.WouldSends > 1) s_sendsAvoided += h.WouldSends - 1;
        }

        private static bool Eligible(int prefabHash)
        {
            if (s_eligible.TryGetValue(prefabHash, out bool ok)) return ok;
            if (ZNetScene.instance == null) return false;
            GameObject prefab = ZNetScene.instance.GetPrefab(prefabHash);
            ok = prefab != null
                && prefab.GetComponentInChildren<Character>(true) == null
                && prefab.GetComponentInChildren<Projectile>(true) == null
                && prefab.GetComponentInChildren<ZSyncTransform>(true) == null
                && prefab.GetComponentInChildren<ZSyncAnimation>(true) == null
                && prefab.GetComponentInChildren<Ship>(true) == null
                && prefab.GetComponentInChildren<Vagon>(true) == null
                && prefab.GetComponentInChildren<ItemDrop>(true) == null;
            s_eligible[prefabHash] = ok;   // one entry per prefab, bounded by the prefab list
            return ok;
        }

        private static void Sweep(float now)
        {
            s_nextSweep = now + SweepEvery;
            s_dropPeers.Clear();
            foreach (var kv in s_peers)
            {
                if (ZNet.instance.GetPeer(kv.Key) == null) { s_dropPeers.Add(kv.Key); continue; }
                var map = kv.Value;
                s_dropIds.Clear();
                foreach (var e in map)
                    if (now - e.Value.First > StaleAfter) s_dropIds.Add(e.Key);   // released long ago, or left the list (out of range / destroyed)
                for (int i = 0; i < s_dropIds.Count; i++) map.Remove(s_dropIds[i]);
            }
            s_dropIds.Clear();
            for (int i = 0; i < s_dropPeers.Count; i++)
            {
                if (s_peers.TryGetValue(s_dropPeers[i], out var map)) { map.Clear(); s_pool.Push(map); }
                s_peers.Remove(s_dropPeers[i]);
            }
            s_dropPeers.Clear();
        }

        private static void Report(float now)
        {
            s_nextReport = now + ReportEvery;
            if (s_holdsStarted == 0 && s_released == 0 && s_tableFull == 0) return;
            if (LoggerOptions.DebugEnabled)
            {
                int tracked = 0;
                foreach (var m in s_peers.Values) tracked += m.Count;
                float mean = s_released > 0 ? s_delaySum / s_released * 1000f : 0f;
                LoggerOptions.LogDebug($"[SendLater] last 10 s: holds started {s_holdsStarted}, held list slots {s_heldObservations}; released {s_released} "
                    + $"({s_releasedNear} early: came near, owner change or forced); sends avoided {s_sendsAvoided} (several changes in one send), "
                    + $"bytes saved at least {s_sendsAvoided * MinBytesPerSend / 1024f:0.0} KB; delay mean {mean:0} ms, worst {s_worstDelay * 1000f:0} ms "
                    + $"(cap {Mathf.Clamp(ConfigMaxDelayMs.Value, MinDelayMs, MaxDelayMs)} ms + scheduler); table full {s_tableFull}; tracked {tracked} for {s_peers.Count} player(s).");
            }
            s_holdsStarted = s_heldObservations = s_released = s_releasedNear = s_sendsAvoided = s_tableFull = 0;
            s_worstDelay = s_delaySum = 0f;
        }

        private static void ClearAll()
        {
            foreach (var m in s_peers.Values) { m.Clear(); s_pool.Push(m); }
            s_peers.Clear();
            s_eligible.Clear();
        }
    }
}
