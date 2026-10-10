using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// A server-side write to an object a CLIENT owns must not outrank the owner. Vanilla resolves every ZDO by data revision alone
    /// (ZDOMan.RPC_ZDOData: a higher revision replaces position and fields, an equal or lower one is dropped). A world script that
    /// writes on the server to a client-owned object (Expand World Prefabs rules: data injected into players, Skull1 trackers,
    /// signs; each Set bumps the revision and EWP adds 100 on top) pushes the server's revision far past what the owner has, so:
    ///   - the server's copy, with the position and fields it last heard from the owner, replaces the owner's own copy: across a
    ///     teleport that puts the local player's ZDO back in its old sector and the instance cleanup destroys the player
    ///     (2026-10-02 Enigma, launch M 03:41: server rev 9226 vs owner 9025, the player lost one frame later);
    ///   - every change the owner made after its last send (sign text, a tracker's state) is dropped by the server, whose revision
    ///     is higher, and then reverted on the owner by the server's copy.
    /// So, on the server, a write that did not come from the owner is held to ONE revision above what the server and that owner
    /// last exchanged (vanilla's per-peer record, ZDOMan.ZDOPeer.m_zdos, set on every send to and receive from the peer). If the
    /// owner has changed nothing since, it takes the server's write (its revision is one higher); if it has, the owner's own copy is
    /// already at least that revision, so the server's copy is dropped there, and the owner's next send, a higher revision, carries
    /// on as vanilla. Vanilla clients merge this by vanilla's own rule; nothing new goes on the wire. Server only.
    /// Left as vanilla: if the owner writes once and the server writes in the same window, both carry the same revision and
    /// vanilla keeps whichever the receiver had (the server keeps its own); far rarer than the hundreds-ahead jump this removes.
    /// </summary>
    [HarmonyPatch]
    internal static class OwnerSafeRevisions
    {
        public static ConfigEntry<bool> ConfigEnabled;

        // Expand World Prefabs writes from Valheim's world-save thread (ServerSideData.BeforeSave/AfterSave on the save clones); those are
        // not owner traffic, and these sets are main-thread only, so anything off the main thread is ignored.
        internal static int MainThreadId = -1;
        internal static bool OnMainThread => System.Threading.Thread.CurrentThread.ManagedThreadId == MainThreadId;

        private static bool s_receiving;
        private static bool s_adjusting;
        private static int s_doneFrame = -1;
        private static readonly HashSet<ZDO> s_written = new HashSet<ZDO>();
        private static readonly Dictionary<long, ZDOMan.ZDOPeer> s_ownerPeers = new Dictionary<long, ZDOMan.ZDOPeer>();
        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDOMan.ZDOPeer>> s_peers = AccessTools.FieldRefAccess<ZDOMan, List<ZDOMan.ZDOPeer>>("m_peers");
        private static long s_held, s_heldRevisions;
        private static float s_nextLog;

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("13 - Compatibility", "Owner-Safe Server Writes", true,
                "Server only. When a world script (e.g. Expand World Prefabs) writes to an object a player owns, keep that write only one\n" +
                "revision ahead of the player instead of far ahead, so it cannot overwrite the player's newer position or changes\n" +
                "(a teleporting player lost, sign text reverted). Players without FGN merge it by vanilla's own rule.");
        }

        private static bool Active => ConfigEnabled != null && ConfigEnabled.Value && ZNet.instance != null && ZNet.instance.IsServer();

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData"), HarmonyPrefix]
        private static void Receive_Prefix() => s_receiving = true;

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData"), HarmonyFinalizer]
        private static void Receive_Finalizer() => s_receiving = false;

        // A write made on this server (not a receive, not our own correction) to an object some client owns.
        [HarmonyPatch(typeof(ZDO), "IncreaseDataRevision"), HarmonyPostfix]
        private static void OnIncrease(ZDO __instance) => Note(__instance);

        // EWP also bumps with "DataRevision += 100" directly; the setter may be inlined into its caller, but its Sets went through
        // IncreaseDataRevision first, so the object is noted either way and the jump is undone before the next send.
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.DataRevision), MethodType.Setter), HarmonyPostfix]
        private static void OnSet(ZDO __instance) => Note(__instance);

        private static void Note(ZDO zdo)
        {
            if (s_receiving || s_adjusting || !OnMainThread || !Active) return;
            long owner = zdo.GetOwner();
            if (owner == 0L || owner == ZDOMan.GetSessionID()) return;
            s_written.Add(zdo);
        }

        // Before ZDOMan sends to any peer (first of the SendZDOs prefixes, so the delta encoder and writer see the held revision);
        // the work runs once per frame.
        [HarmonyPatch(typeof(ZDOMan), "SendZDOs"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Send_Prefix() => BeforeSend();

        internal static void BeforeSend()
        {
            if (s_written.Count == 0 || Time.frameCount == s_doneFrame) return;
            s_doneFrame = Time.frameCount;
            if (!Active || ZDOMan.instance == null) { s_written.Clear(); return; }
            List<ZDOMan.ZDOPeer> peers = s_peers(ZDOMan.instance);
            s_ownerPeers.Clear();
            foreach (ZDOMan.ZDOPeer p in peers) if (p?.m_peer != null) s_ownerPeers[p.m_peer.m_uid] = p;
            s_adjusting = true;
            try
            {
                foreach (ZDO zdo in s_written)
                {
                    if (zdo == null || ZDOMan.instance.GetZDO(zdo.m_uid) != zdo) continue;   // pooled: destroyed or reused since
                    long owner = zdo.GetOwner();
                    if (!s_ownerPeers.TryGetValue(owner, out ZDOMan.ZDOPeer ownerPeer)) continue;   // owner gone or the server
                    if (!ownerPeer.m_zdos.TryGetValue(zdo.m_uid, out var agreed)) continue;         // never exchanged: nothing to protect
                    uint target = agreed.m_dataRevision + 1;
                    // never below what another peer was already sent, or that peer would stop getting this object
                    foreach (ZDOMan.ZDOPeer p in peers)
                        if (p != ownerPeer && p != null && p.m_zdos.TryGetValue(zdo.m_uid, out var other) && other.m_dataRevision + 1 > target)
                            target = other.m_dataRevision + 1;
                    if (zdo.DataRevision <= target) continue;
                    s_heldRevisions += zdo.DataRevision - target;
                    zdo.DataRevision = target;
                    s_held++;
                }
            }
            finally
            {
                s_adjusting = false;
                s_written.Clear();
            }
            if (s_held > 0 && Time.unscaledTime >= s_nextLog)
            {
                s_nextLog = Time.unscaledTime + 60f;
                LoggerOptions.LogDebug($"[OwnerSafe] {s_held} server write(s) to player-owned objects held to one revision above the owner "
                    + $"({s_heldRevisions} revisions not jumped) so far");
            }
        }

        internal static void Clear()
        {
            s_written.Clear();
            s_ownerPeers.Clear();
        }
    }
}
