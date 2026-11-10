// Idea from [generator]'s SpreadTheLoad study (Tools\STUDY_sections\other_NRF_VPE_STL.md 3.5). SpreadTheLoad has no licence: behaviour only;
// this is our own design and code.
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.69 ([fgn], Fire 11-08). "Judge Peers By Update Gaps" (dedicated server): ping does not show a struggling client (16 fps at 24 ms
    /// ping looks healthy). A client's game sends its owned objects in a steady stream; when its frames stall, that stream pauses. The server
    /// timestamps each ZDO data packet per peer and counts a STALL when a pause of 0.3-5 s breaks a stream that was steady just before (the
    /// median of its last 8 gaps under 0.15 s, so an idle player who simply has nothing to send is never counted), and the server's own frame
    /// was not the slow one. 6 stalls inside 60 s flags the peer; 300 s without a stall clears it; never in the first 60 s after it joined
    /// (loading), and never the last unflagged player. The only reader is FGN's creature balancer (CreatureOwnership): a flagged peer is not
    /// handed idle creatures, and idle creatures it owns may go to a healthy nearby player even without the ping margin. Same fight guard and
    /// 30 s hold as before. Bounded: 64 timestamps per peer, peers pruned when they leave. Zero allocation per packet. Info on flag / clear.
    /// </summary>
    [HarmonyPatch]
    internal static class PeerStalls
    {
        internal static BepInEx.Configuration.ConfigEntry<bool> ConfigEnabled;
        private const float MinGap = 0.3f, MaxGap = 5f, SteadyGap = 0.15f, WindowS = 60f, ClearS = 300f, JoinGraceS = 60f, ServerSlowS = 0.25f;
        private const int StallsToFlag = 6, Ring = 64, Recent = 8;

        private sealed class State
        {
            public readonly float[] Stalls = new float[Ring]; public int StallHead, StallCount;
            public readonly float[] Gaps = new float[Recent]; public int GapHead, GapCount;
            public float Last = -1f, FirstSeen, LastStall = -1f; public bool Flagged; public string Name;
        }
        private static readonly Dictionary<long, State> s_peers = new Dictionary<long, State>();
        private static readonly Dictionary<ZRpc, long> s_rpcUid = new Dictionary<ZRpc, long>();
        private static readonly float[] s_sort = new float[Recent];
        private static readonly List<long> s_gone = new List<long>();
        private static float s_nextPrune;
        private static int s_stalls;

        internal static void InitConfig(BepInEx.Configuration.ConfigFile config)
        {
            ConfigEnabled = config.Bind("10 - Server Authority", "Judge Peers By Update Gaps", false,
                "Dedicated server: a player whose game keeps pausing (low frame rate, stalls) is spotted from gaps in what it sends, not only from " +
                "ping, and is not given extra creatures to run (with Balance Creature Ownership on). Off until the A/B.");
        }

        private static bool Active => ConfigEnabled != null && ConfigEnabled.Value && ZNet.instance != null && ZNet.instance.IsDedicated();

        /// <summary>For CreatureOwnership: true while this peer is flagged as stalling.</summary>
        internal static bool IsStalling(long uid) => Active && s_peers.TryGetValue(uid, out State s) && s.Flagged;

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData"), HarmonyPostfix]
        private static void OnData(ZRpc rpc)
        {
            if (rpc == null || !Active) return;
            float now = Time.realtimeSinceStartup;
            if (!s_rpcUid.TryGetValue(rpc, out long uid))
            {
                ZNetPeer p = ZNet.instance.GetPeer(rpc);
                if (p == null) return;
                uid = p.m_uid; s_rpcUid[rpc] = uid;
            }
            if (!s_peers.TryGetValue(uid, out State st))
            {
                st = new State { FirstSeen = now, Name = ZNet.instance.GetPeer(uid)?.m_playerName ?? uid.ToString() };
                s_peers[uid] = st;
            }
            float gap = st.Last < 0f ? 0f : now - st.Last;
            st.Last = now;
            if (gap <= 0f) return;
            bool steady = st.GapCount >= Recent && Median(st) < SteadyGap;
            if (gap >= MinGap && gap <= MaxGap && steady && now - st.FirstSeen > JoinGraceS && Time.unscaledDeltaTime < ServerSlowS)
            {
                st.Stalls[st.StallHead] = now; st.StallHead = (st.StallHead + 1) % Ring; if (st.StallCount < Ring) st.StallCount++;
                st.LastStall = now; s_stalls++;
                if (!st.Flagged && CountSince(st, now - WindowS) >= StallsToFlag && AnotherHealthy(uid))
                {
                    st.Flagged = true;
                    LoggerOptions.LogInfo($"[PeerStalls] {st.Name} flagged as struggling ({StallsToFlag}+ update pauses in {WindowS:0} s); idle creatures are not handed to them");
                }
            }
            if (gap < MaxGap) { st.Gaps[st.GapHead] = gap; st.GapHead = (st.GapHead + 1) % Recent; if (st.GapCount < Recent) st.GapCount++; }
            if (st.Flagged && st.LastStall > 0f && now - st.LastStall > ClearS)
            {
                st.Flagged = false;
                LoggerOptions.LogInfo($"[PeerStalls] {st.Name} no longer flagged ({ClearS:0} s without an update pause)");
            }
            if (now >= s_nextPrune) Prune(now);
        }

        private static float Median(State st)
        {
            Array.Copy(st.Gaps, s_sort, Recent);
            Array.Sort(s_sort);
            return s_sort[Recent / 2];
        }

        private static int CountSince(State st, float since)
        {
            int n = 0;
            for (int i = 0; i < st.StallCount; i++) if (st.Stalls[i] >= since) n++;
            return n;
        }

        private static bool AnotherHealthy(long uid)
        {
            foreach (ZNetPeer p in ZNet.instance.GetPeers())
                if (p != null && p.m_uid != uid && p.IsReady() && !(s_peers.TryGetValue(p.m_uid, out State s) && s.Flagged)) return true;
            return false;
        }

        private static void Prune(float now)
        {
            s_nextPrune = now + 10f;
            s_gone.Clear();
            foreach (var kv in s_peers) if (ZNet.instance.GetPeer(kv.Key) == null) s_gone.Add(kv.Key);
            foreach (long uid in s_gone) s_peers.Remove(uid);
            if (s_gone.Count > 0 || s_rpcUid.Count > s_peers.Count + 8)
            {
                var dead = new List<ZRpc>();
                foreach (var kv in s_rpcUid) if (!s_peers.ContainsKey(kv.Value) || ZNet.instance.GetPeer(kv.Value) == null) dead.Add(kv.Key);
                foreach (var r in dead) s_rpcUid.Remove(r);
            }
            if (LoggerOptions.DebugEnabled && s_stalls > 0)
            {
                int flagged = 0; foreach (var kv in s_peers) if (kv.Value.Flagged) flagged++;
                LoggerOptions.LogDebug($"[PeerStalls] last 10 s: {s_stalls} update pause(s) counted; {flagged} of {s_peers.Count} peer(s) flagged");
            }
            s_stalls = 0;
        }

        internal static void OnShutdown() { s_peers.Clear(); s_rpcUid.Clear(); }
    }
}
