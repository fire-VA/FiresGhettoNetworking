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
    ///   * delivering >= 80% of the current rate -> RAISE: fast (x2 slow-start) until the first congestion
    ///     signal, then fine (x1.1 congestion-avoidance). NOT gated on queue depth — a saturating transfer
    ///     keeps the queue full on purpose; delivered-vs-cap distinguishes "cap-limited" (raise) from
    ///     "link-limited" (delivered drops below cap -> back off). Overshoot is safe: the transport gives a
    ///     stalled peer ~30s before it drops, far longer than the few ticks the back-off needs to re-settle.
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
        public static ConfigEntry<bool> ConfigLog;

        // Raised by the FGN socket stress tests while they drive the per-connection rate by hand, so the
        // controller doesn't fight their override mid-test.
        public static bool Suspend;

        private const float TickSeconds       = 1.5f;
        private const float HighWaterFraction = 0.70f;   // queue >= 70% of the send buffer = pressure
        private const int   SustainTicks      = 3;       // consecutive pressured ticks before backing off
        private const float SlowStartFactor   = 2.00f;   // fast climb (double/tick) until the first congestion signal
        private const float CongAvoidFactor   = 1.10f;   // fine steps after that, so the steady-state hunt stays small
        private const float BackOffFactor     = 0.60f;   // max rate decrease per back-off
        private const float BackOffHeadroom   = 1.10f;   // never settle below 1.1x what we're actually delivering
        private const float UseFraction       = 0.80f;   // must deliver >= 80% of the current rate to count as "using" it
        private const int   MinStepBytes      = 1024 * 1024;

        private sealed class PeerState
        {
            public uint Conn;          // resolved once, then reused (no per-tick reflection)
            public int  Target;
            public int  PressureTicks;
            public bool SlowStart;     // fast-climb phase until the first back-off, then fine congestion-avoidance
        }

        private static readonly Dictionary<long, PeerState> s_state = new Dictionary<long, PeerState>();
        private static readonly HashSet<long> s_seen = new HashSet<long>();
        private static readonly List<long> s_stale = new List<long>();
        private static bool s_running;
        private static bool s_wasBoosting;
        private static bool s_loggedConfig;
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
            ConfigLog = config.Bind("10 - Diagnostics", "Log Adaptive Send Rate Ticks", false,
                "Verbose per-tick diagnostics for the Adaptive Send Rate controller — each managed peer's cap, "
                + "delivered throughput, queue depth and the branch taken, every tick. Useful for tuning, noisy "
                + "for normal play. OFF by default. SERVER-SIDE only.");
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
            s_loggedConfig = false;
            s_state.Clear();
            var wait = new WaitForSeconds(TickSeconds);
            LoggerOptions.LogMessage($"[AdaptiveRate] controller ON (tick {TickSeconds:F1}s, high-water "
                + $"{HighWaterFraction * 100f:F0}% over {SustainTicks} ticks, up x{SlowStartFactor:F1}/x{CongAvoidFactor:F2}, down x{BackOffFactor:F2}).");

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

            if (!s_loggedConfig && ConfigLog != null && ConfigLog.Value)
            {
                s_loggedConfig = true;
                LoggerOptions.LogMessage($"[AdaptiveRate] effective: start={baseline / 1048576}MB floor={minRate / 1048576}MB "
                    + $"ceiling={ceiling / 1048576L}MB sendBuf={buffer / 1048576}MB highWater={highWater / 1048576}MB "
                    + $"(slowStart=x{SlowStartFactor:F1} congAvoid=x{CongAvoidFactor:F2} use={UseFraction:F2})");
            }

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
                    st = new PeerState { Conn = h, Target = baseline, SlowStart = true };
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
                int oldTarget = st.Target;
                string branch;
                bool? pinned = null;

                // delivered-vs-cap is the limiter (NOT queue depth — a saturating transfer keeps the queue full
                // on purpose). Near the cap => cap-limited => raise. Below the cap with a full queue =>
                // link-limited => back off. Over-pin is bounded to ~1.25x link and self-corrects.
                if (delivered >= st.Target * UseFraction)
                {
                    st.PressureTicks = 0;
                    if (st.Target < ceiling)
                    {
                        long next = (long)(st.Target * (st.SlowStart ? SlowStartFactor : CongAvoidFactor));
                        if (next <= st.Target) next = st.Target + MinStepBytes;   // always make forward progress
                        if (next > ceiling) next = ceiling;
                        st.Target = (int)next;
                        pinned = NetworkingRatesGroup.SetConnectionRatePinned(st.Conn, st.Target, raising: true);
                        branch = st.SlowStart ? "RAISE-fast" : "RAISE";
                    }
                    else branch = "RAISE-ceiling";
                }
                else if (queue >= highWater)
                {
                    branch = $"PRESSURE {st.PressureTicks + 1}/{SustainTicks}";
                    if (++st.PressureTicks >= SustainTicks)
                    {
                        st.PressureTicks = 0;
                        st.SlowStart = false;   // found the ceiling — fine congestion-avoidance from here on
                        int settleAboveLink = Mathf.Max(minRate, (int)(delivered * BackOffHeadroom));
                        int gradualStep = Mathf.Max(minRate, (int)(st.Target * BackOffFactor));
                        int next = Mathf.Max(settleAboveLink, gradualStep);
                        if (next < st.Target)
                        {
                            st.Target = next;
                            pinned = NetworkingRatesGroup.SetConnectionRatePinned(st.Conn, st.Target, raising: false);
                            branch = "BACKOFF";
                        }
                        else branch = "BACKOFF-floored";
                    }
                }
                else
                {
                    st.PressureTicks = 0;
                    branch = "HOLD";
                }

                if (ConfigLog != null && ConfigLog.Value)
                    LoggerOptions.LogMessage($"[AdaptiveRate] peer {peer.m_uid} {branch}: cap {oldTarget / 1048576}->{st.Target / 1048576} MB/s, "
                        + $"delivered {delivered / 1048576f:F1} MB/s ({(oldTarget > 0 ? delivered / oldTarget * 100f : 0f):F0}% of cap), "
                        + $"queue {queue / 1048576f:F1}/{highWater / 1048576} MB"
                        + (pinned.HasValue ? (pinned.Value ? " [pin ok]" : " [PIN FAILED]") : ""));
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
