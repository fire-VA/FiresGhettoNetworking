using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using FiresGhettoNetworkMod.AutoTune;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Per-peer adaptive send-rate controller (AIMD). Replaces Steam's own send-rate adapter — which has a
    /// documented sticky-down quirk that parks peers near SendRateMin — with a closed loop driven by the
    /// real per-connection signals: outbound send-queue depth + measured delivered throughput.
    ///
    /// Per peer, each tick:
    ///   * delivering >= 80% of the current rate WITH a clear send-queue -> RAISE the rate (the cap limits us,
    ///     not the link). The clear-queue gate stops us from ratcheting the pin up INTO a backlog.
    ///   * queue stuck >= high water for SustainTicks AND delivery not keeping up -> BACK OFF toward the real
    ///     delivered rate (the link is the limit). A single spike never triggers.
    ///   * otherwise hold.
    ///
    /// The rate is PINNED (SendRateMin == SendRateMax) per connection so Steam has no window to drift inside;
    /// this loop is the sole authority and each client converges to ITS OWN link capacity. The pin STARTS at
    /// the Auto-Tune/manual send rate (the optimistic baseline) but its floor is the tier's SendRateMin, so a
    /// peer whose real link is below the baseline can be eased DOWN to its true capacity rather than stranded
    /// with a permanently full queue. Ceiling = the HYPERBOOST rate. SERVER-SIDE only; defers to HYPERBOOST
    /// (already pinned to the API ceiling) and to the socket stress tests, and skips PlayFab/crossplay peers.
    /// </summary>
    [HarmonyPatch]
    public static class AdaptiveSendRate
    {
        public static ConfigEntry<bool> ConfigEnabled;

        // Raised by the FGN socket stress tests while they drive the per-connection rate by hand, so the
        // controller doesn't fight their override mid-test.
        public static bool Suspend;

        private const float TickSeconds       = 1.5f;
        private const float HighWaterFraction = 0.70f;   // queue >= 70% of the send buffer = pressure
        private const int   SustainTicks      = 3;       // consecutive pressured ticks before backing off
        private const float RampUpFactor      = 1.25f;   // rate increase per healthy tick
        private const float BackOffFactor     = 0.60f;   // max rate decrease per back-off
        private const float BackOffHeadroom   = 1.10f;   // never settle below 1.1x what we're actually delivering
        private const float UseFraction       = 0.80f;   // must deliver >= 80% of the current rate to count as "using" it
        private const int   MinStepBytes      = 1024 * 1024;

        private sealed class PeerState
        {
            public uint Conn;          // resolved once, then reused (no per-tick reflection)
            public int  Target;
            public int  PressureTicks;
        }

        private static readonly Dictionary<long, PeerState> s_state = new Dictionary<long, PeerState>();
        private static readonly HashSet<long> s_seen = new HashSet<long>();
        private static readonly List<long> s_stale = new List<long>();
        private static bool s_running;
        private static bool s_wasBoosting;
        private static Coroutine s_loop;

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("06 - Auto-Tune", "Adaptive Send Rate", true,
                "Server-side closed-loop send-rate controller. Each peer starts at the Auto-Tune/manual send "
                + "rate and ramps UP toward the HYPERBOOST ceiling while it's delivering near that rate with a "
                + "clear send-queue, easing back toward the real measured throughput (down to the tier's "
                + "SendRateMin) only when the queue stays congested AND delivery can't keep up for several "
                + "ticks. Pins SendRateMin = SendRateMax per connection so Steam's own sticky-down adapter "
                + "can't park the peer near the floor — each client converges to its own real link capacity. "
                + "Defers to HYPERBOOST while that's on. SERVER-SIDE only.");
            ConfigEnabled.SettingChanged += (_, __) => MaybeStart();
        }

        [HarmonyPatch(typeof(ZNet), "Start"), HarmonyPostfix]
        static void OnZNetStart() => MaybeStart();

        // The coroutine host (FiresGhettoNetworkMod.Instance) outlives ZNet, so without these the loop
        // survives a world teardown suspended on its yield and can double-start or stick the next session off.
        [HarmonyPatch(typeof(ZNet), "Shutdown"), HarmonyPostfix]
        static void OnZNetShutdown() => Stop();

        [HarmonyPatch(typeof(ZNet), "OnDestroy"), HarmonyPostfix]
        static void OnZNetDestroy() => Stop();

        private static void MaybeStart()
        {
            if (s_running) return;
            if (ConfigEnabled == null || !ConfigEnabled.Value) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (FiresGhettoNetworkMod.Instance == null) return;
            s_loop = FiresGhettoNetworkMod.Instance.StartCoroutine(ControlLoop());
        }

        private static void Stop()
        {
            if (s_loop != null && FiresGhettoNetworkMod.Instance != null)
                FiresGhettoNetworkMod.Instance.StopCoroutine(s_loop);
            s_loop = null;
            s_running = false;
            s_wasBoosting = false;
            s_state.Clear();
        }

        private static IEnumerator ControlLoop()
        {
            s_running = true;
            s_wasBoosting = false;
            s_state.Clear();
            var wait = new WaitForSeconds(TickSeconds);
            LoggerOptions.LogMessage($"[AdaptiveRate] controller ON (tick {TickSeconds:F1}s, high-water "
                + $"{HighWaterFraction * 100f:F0}% over {SustainTicks} ticks, up x{RampUpFactor:F2}, down x{BackOffFactor:F2}).");

            while (ZNet.instance != null && ZNet.instance.IsServer() && ConfigEnabled != null && ConfigEnabled.Value)
            {
                if (EffectiveConfig.HyperBoost())
                {
                    // HyperBoost owns the rate; our pins are stale -> drop them so we re-pin cleanly when it ends.
                    if (!s_wasBoosting) { s_state.Clear(); s_wasBoosting = true; }
                }
                else
                {
                    // HyperBoost's toggle-off re-applies the tier's (unequal) Min/Max, UN-pinning every
                    // connection. Clearing here makes Tick() re-create + re-pin each peer this same tick.
                    if (s_wasBoosting) { s_state.Clear(); s_wasBoosting = false; }
                    if (!Suspend)
                    {
                        try { Tick(); }
                        catch (System.Exception e) { LoggerOptions.LogWarning($"[AdaptiveRate] tick failed: {e.Message}"); }
                    }
                }
                yield return wait;
            }

            s_running = false;
            s_loop = null;
            s_state.Clear();
            LoggerOptions.LogMessage("[AdaptiveRate] controller OFF.");
        }

        private static void Tick()
        {
            var peers = ZNet.instance.GetPeers();
            if (peers == null) return;

            int buffer = EffectiveConfig.SteamSendBufferBytes();
            int highWater = (int)(buffer * HighWaterFraction);
            int baseline = EffectiveConfig.SteamSendRateMax();   // optimistic high start (the configured baseline)
            int minRate = EffectiveConfig.SteamSendRateMin();    // link-safe down-room floor (tier Min)
            int ceiling = EffectiveConfig.HyperBoostSendRateMaxBytes;
            if (minRate > baseline) minRate = baseline;          // never invert if a manual config sets Min > Max

            s_seen.Clear();
            for (int i = 0; i < peers.Count; i++)
            {
                var peer = peers[i];
                if (peer == null || peer.m_socket == null) continue;

                PeerState st;
                if (!s_state.TryGetValue(peer.m_uid, out st))
                {
                    uint h = NetworkingRatesGroup.GetConnectionHandle(peer);
                    if (h == 0u) continue;   // PlayFab / crossplay — no per-connection control
                    st = new PeerState { Conn = h, Target = baseline };
                    s_state[peer.m_uid] = st;
                    s_seen.Add(peer.m_uid);
                    NetworkingRatesGroup.SetConnectionRatePinned(h, st.Target, raising: true);
                    continue;
                }
                s_seen.Add(peer.m_uid);

                if (st.Target < minRate) st.Target = minRate;   // floor at the link-safe minimum, NOT the baseline

                int queue = peer.m_socket.GetSendQueueSize();
                if (queue < 0) continue;

                float delivered = NetworkStats.PeerSendBytesPerSec(peer.m_socket);

                if (delivered >= st.Target * UseFraction && queue < highWater)
                {
                    // Delivering near our cap with queue headroom -> the cap limits us, not the link -> raise it.
                    st.PressureTicks = 0;
                    if (st.Target < ceiling)
                    {
                        long next = (long)(st.Target * RampUpFactor);
                        if (next <= st.Target) next = st.Target + MinStepBytes;
                        if (next > ceiling) next = ceiling;
                        st.Target = (int)next;
                        NetworkingRatesGroup.SetConnectionRatePinned(st.Conn, st.Target, raising: true);
                    }
                }
                else if (queue >= highWater)
                {
                    // Data waiting but delivery stuck below our cap -> the LINK is the limit. Sustained only.
                    if (++st.PressureTicks >= SustainTicks)
                    {
                        st.PressureTicks = 0;
                        int settleAboveLink = Mathf.Max(minRate, (int)(delivered * BackOffHeadroom));
                        int gradualStep = Mathf.Max(minRate, (int)(st.Target * BackOffFactor));
                        int next = Mathf.Max(settleAboveLink, gradualStep);
                        if (next < st.Target)
                        {
                            st.Target = next;
                            NetworkingRatesGroup.SetConnectionRatePinned(st.Conn, st.Target, raising: false);
                        }
                    }
                }
                else
                {
                    st.PressureTicks = 0;   // idle / comfortable headroom — hold
                }
            }

            if (s_state.Count > s_seen.Count)
            {
                s_stale.Clear();
                foreach (var kv in s_state) if (!s_seen.Contains(kv.Key)) s_stale.Add(kv.Key);
                for (int i = 0; i < s_stale.Count; i++) s_state.Remove(s_stale[i]);
            }
        }
    }
}
