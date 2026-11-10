using System.Collections.Generic;
using System.Linq;

using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// The other half of OwnerSafeRevisions. Holding a server write on a client-owned object to one revision above the owner means the
    /// owner wins whenever it changed anything meanwhile, and the owner's whole copy then comes back over the server's. Vanilla's
    /// receive only adds and overwrites the fields in that copy, so a key the server added survives; a key the owner ALSO carries
    /// (an older value), or one the owner never received, does not reach both sides. So every value the server writes on a
    /// client-owned object is kept pending until the owner sends a copy that carries it. When the owner's copy arrives with that
    /// key at an older value, the server's value is kept in place of it as the copy is read (the owner's position and every other field
    /// stay the owner's), and the object is sent again now and then, one revision above the owner. Server only; vanilla clients merge it by
    /// vanilla's rule. (Outranking the owner by a large revision jump would also make server writes stick, at the cost of reverting
    /// the owner; this keeps the server's values without that.)
    /// </summary>
    [HarmonyPatch]
    internal static class PendingServerKeys
    {
        private const int MaxPendingObjects = 4096;
        private const float ResendEverySeconds = 2f;
        private const float LogIntervalSeconds = 60f;
        // 1.6.31: a pending key only ever cleared when the owner's copy carried its value, so an owner's later real edit (a sign to "B") was
        // replaced by the old server value and reverted, for as long as the server ran. A server write now outranks the owner for this long.
        private const float ExpireSeconds = 30f;
        private const float PruneEverySeconds = 5f;

        private sealed class Pending
        {
            public readonly Dictionary<(int type, int hash), object> Values = new Dictionary<(int, int), object>();
            public readonly Dictionary<(int type, int hash), float> WrittenAt = new Dictionary<(int, int), float>();
            public float LastReapply;
        }

        private static readonly Dictionary<ZDOID, Pending> s_pending = new Dictionary<ZDOID, Pending>();
        // keys and values carried by the owner's copy now being received, per object
        private static readonly Dictionary<(int type, int hash), object> s_received = new Dictionary<(int, int), object>();
        private static ZDOID s_receivingId = ZDOID.None;
        private static bool s_receiving, s_reapplying;
        private static long s_kept, s_acked;
        private static Pending s_receivingPending;
        private static float s_nextLog;

        private static bool Active => OwnerSafeRevisions.ConfigEnabled != null && OwnerSafeRevisions.ConfigEnabled.Value
                                      && ZNet.instance != null && ZNet.instance.IsServer();

        // ZDOExtraData.Set(ZDOID, int, T) for every type T (a value the server writes) and ZDOExtraData.Add (Deserialize's way in:
        // what the incoming copy carries). One typed hook per type: these run for every field write on the server, so no boxing
        // array per call; a value is boxed only for an object that is being tracked.
        [HarmonyPatch(typeof(ZDOExtraData), "Set", new[] { typeof(ZDOID), typeof(int), typeof(float) }), HarmonyPostfix]
        private static void SetF(ZDOID zid, int hash, float value, bool __result) { if (__result) Wrote(zid, 0, hash, value); }
        [HarmonyPatch(typeof(ZDOExtraData), "Set", new[] { typeof(ZDOID), typeof(int), typeof(Vector3) }), HarmonyPostfix]
        private static void SetV(ZDOID zid, int hash, Vector3 value, bool __result) { if (__result) Wrote(zid, 1, hash, value); }
        [HarmonyPatch(typeof(ZDOExtraData), "Set", new[] { typeof(ZDOID), typeof(int), typeof(Quaternion) }), HarmonyPostfix]
        private static void SetQ(ZDOID zid, int hash, Quaternion value, bool __result) { if (__result) Wrote(zid, 2, hash, value); }
        [HarmonyPatch(typeof(ZDOExtraData), "Set", new[] { typeof(ZDOID), typeof(int), typeof(int) }), HarmonyPostfix]
        private static void SetI(ZDOID zid, int hash, int value, bool __result) { if (__result) Wrote(zid, 3, hash, value); }
        [HarmonyPatch(typeof(ZDOExtraData), "Set", new[] { typeof(ZDOID), typeof(int), typeof(long) }), HarmonyPostfix]
        private static void SetL(ZDOID zid, int hash, long value, bool __result) { if (__result) Wrote(zid, 4, hash, value); }
        [HarmonyPatch(typeof(ZDOExtraData), "Set", new[] { typeof(ZDOID), typeof(int), typeof(string) }), HarmonyPostfix]
        private static void SetS(ZDOID zid, int hash, string value, bool __result) { if (__result) Wrote(zid, 5, hash, value); }
        [HarmonyPatch(typeof(ZDOExtraData), "Set", new[] { typeof(ZDOID), typeof(int), typeof(byte[]) }), HarmonyPostfix]
        private static void SetB(ZDOID zid, int hash, byte[] value, bool __result) { if (__result) Wrote(zid, 6, hash, value); }

        // PREFIXES, first of all, on Deserialize's way in. For a key the server has pending, a stale value in the owner's copy is
        // replaced by the server's value before anything (other mods' change detection included) sees it: the server copy
        // never flips back and forth, so no write-back and no "changed" events (no write storm). The value the owner carried is
        // still noted, for the ack.
        [HarmonyPatch(typeof(ZDOExtraData), "Add", new[] { typeof(ZDOID), typeof(int), typeof(float) }), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void AddF(ZDOID zid, int hash, ref float value) { if (zid == s_receivingId) Keep(0, hash, ref value); }
        [HarmonyPatch(typeof(ZDOExtraData), "Add", new[] { typeof(ZDOID), typeof(int), typeof(Vector3) }), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void AddV(ZDOID zid, int hash, ref Vector3 value) { if (zid == s_receivingId) Keep(1, hash, ref value); }
        [HarmonyPatch(typeof(ZDOExtraData), "Add", new[] { typeof(ZDOID), typeof(int), typeof(Quaternion) }), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void AddQ(ZDOID zid, int hash, ref Quaternion value) { if (zid == s_receivingId) Keep(2, hash, ref value); }
        [HarmonyPatch(typeof(ZDOExtraData), "Add", new[] { typeof(ZDOID), typeof(int), typeof(int) }), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void AddI(ZDOID zid, int hash, ref int value) { if (zid == s_receivingId) Keep(3, hash, ref value); }
        [HarmonyPatch(typeof(ZDOExtraData), "Add", new[] { typeof(ZDOID), typeof(int), typeof(long) }), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void AddL(ZDOID zid, int hash, ref long value) { if (zid == s_receivingId) Keep(4, hash, ref value); }
        [HarmonyPatch(typeof(ZDOExtraData), "Add", new[] { typeof(ZDOID), typeof(int), typeof(string) }), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void AddS(ZDOID zid, int hash, ref string value) { if (zid == s_receivingId) Keep(5, hash, ref value); }
        [HarmonyPatch(typeof(ZDOExtraData), "Add", new[] { typeof(ZDOID), typeof(int), typeof(byte[]) }), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void AddB(ZDOID zid, int hash, ref byte[] value) { if (zid == s_receivingId) Keep(6, hash, ref value); }

        private static void Keep<T>(int type, int hash, ref T value)
        {
            s_received[(type, hash)] = value;
            if (s_receivingPending != null && s_receivingPending.Values.TryGetValue((type, hash), out object mine) && mine is T server && !Same(value, server))
            {
                value = server;
                s_kept++;
            }
        }

        private static void Wrote<T>(ZDOID zid, int type, int hash, T value)
        {
            if (OwnerSafeRevisions.NoPeers || s_receiving || s_reapplying || !OwnerSafeRevisions.OnMainThread || !Active) return;   // 1.6.82: no peer first
            ZDO zdo = ZDOMan.instance?.GetZDO(zid);
            if (zdo == null) return;
            long owner = zdo.GetOwner();
            if (owner == 0L || owner == ZDOMan.GetSessionID()) return;
            if (!s_pending.TryGetValue(zid, out Pending p))
            {
                if (s_pending.Count >= MaxPendingObjects) PruneExpired();
                if (s_pending.Count >= MaxPendingObjects) return;
                s_pending[zid] = p = new Pending();
            }
            p.Values[(type, hash)] = value;
            p.WrittenAt[(type, hash)] = Time.unscaledTime;
        }

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData"), HarmonyPrefix]
        private static void Receive_Prefix() => s_receiving = true;

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData"), HarmonyFinalizer]
        private static void Receive_Finalizer() => s_receiving = false;

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize)), HarmonyPrefix]
        private static void Deserialize_Prefix(ZDO __instance)
        {
            s_received.Clear();
            s_receivingPending = null;
            s_receivingId = s_receiving && s_pending.Count > 0 && s_pending.TryGetValue(__instance.m_uid, out s_receivingPending) ? __instance.m_uid : ZDOID.None;
            if (s_receivingPending != null)
            {
                Expire(s_receivingPending);
                if (s_receivingPending.Values.Count == 0)
                {
                    s_pending.Remove(__instance.m_uid);
                    s_receivingPending = null;
                    s_receivingId = ZDOID.None;
                }
            }
        }

        private static readonly List<(int type, int hash)> s_expiredKeys = new List<(int, int)>();
        private static readonly List<ZDOID> s_emptied = new List<ZDOID>();
        private static long s_expired;
        private static float s_nextExpireLog, s_nextPrune;

        private static void Expire(Pending p)
        {
            float cutoff = Time.unscaledTime - ExpireSeconds;
            s_expiredKeys.Clear();
            foreach (var entry in p.WrittenAt) if (entry.Value < cutoff) s_expiredKeys.Add(entry.Key);
            if (s_expiredKeys.Count == 0) return;
            foreach (var key in s_expiredKeys) { p.Values.Remove(key); p.WrittenAt.Remove(key); }
            s_expired += s_expiredKeys.Count;
            if (Time.unscaledTime < s_nextExpireLog) return;
            s_nextExpireLog = Time.unscaledTime + LogIntervalSeconds;
            LoggerOptions.LogDebug($"[OwnerSafe] {s_expired} server write(s) the owner never carried within {ExpireSeconds:F0} s dropped so far: the owner's value stands from here on, a later edit of theirs is not reverted");
        }

        // at the object cap, objects whose owner never sends a copy would otherwise block tracking for good
        private static void PruneExpired()
        {
            if (Time.unscaledTime < s_nextPrune) return;
            s_nextPrune = Time.unscaledTime + PruneEverySeconds;
            s_emptied.Clear();
            foreach (var entry in s_pending)
            {
                Expire(entry.Value);
                if (entry.Value.Values.Count == 0) s_emptied.Add(entry.Key);
            }
            foreach (ZDOID id in s_emptied) s_pending.Remove(id);
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize)), HarmonyPostfix]
        private static void Deserialize_Postfix(ZDO __instance)
        {
            if (s_receivingId.IsNone() || __instance.m_uid != s_receivingId) return;
            s_receivingId = ZDOID.None;
            Pending p = s_receivingPending;
            s_receivingPending = null;
            if (p == null) return;
            // acked: the owner's copy carried the server's value; those keys are done
            foreach (var key in p.Values.Keys.ToList())
                if (s_received.TryGetValue(key, out object got) && Same(got, p.Values[key])) { p.Values.Remove(key); p.WrittenAt.Remove(key); }
            if (p.Values.Count == 0) { s_pending.Remove(__instance.m_uid); s_acked++; return; }
            // still missing on the owner: resend now and then (revision only, no field change: one above the owner, see
            // OwnerSafeRevisions). An owner still changing outruns it; one that has settled takes it and the next copy acks.
            if (Time.unscaledTime - p.LastReapply >= ResendEverySeconds)
            {
                p.LastReapply = Time.unscaledTime;
                __instance.IncreaseDataRevision();
            }
            if (Time.unscaledTime >= s_nextLog)
            {
                s_nextLog = Time.unscaledTime + LogIntervalSeconds;
                LoggerOptions.LogDebug($"[OwnerSafe] stale owner values kept off server-written keys: {s_kept}; keys confirmed by the owner: {s_acked}; objects pending: {s_pending.Count}");
            }
        }

        [HarmonyPatch(typeof(ZDOMan), "HandleDestroyedZDO"), HarmonyPostfix]
        private static void Destroyed(ZDOID uid) => s_pending.Remove(uid);

        internal static void Clear() { s_pending.Clear(); s_received.Clear(); }

        private static bool Same(object a, object b)
        {
            if (a is byte[] x && b is byte[] y) return x.SequenceEqual(y);
            return Equals(a, b);
        }
    }
}
