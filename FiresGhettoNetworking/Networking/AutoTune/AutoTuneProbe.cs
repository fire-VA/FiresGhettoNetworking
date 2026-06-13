using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FiresGhettoNetworkMod.AutoTune
{
    /// <summary>
    /// Client-side performance probe. Runs on first login to a server (or every login when
    /// configured to retune), measures hardware + latency + jitter + frame time + (optionally)
    /// downlink bandwidth, picks a LOW/MED/HIGH tier, applies it, and reports it back to
    /// the server for the admin's aggregate-suggestion view.
    ///
    /// Hard rule: the probe must NEVER kick a fragile client off. Every stage has a timeout
    /// that defaults to LOW tier rather than throwing. The bandwidth stage is gated behind
    /// a passing latency stage so clients with bad routes don't get hit with extra traffic.
    /// </summary>
    public static class AutoTuneProbe
    {
        public const string RPC_PING        = "FiresGhetto.AutoTune.Ping";
        public const string RPC_PONG        = "FiresGhetto.AutoTune.Pong";
        public const string RPC_BW_REQ      = "FiresGhetto.AutoTune.BwReq";
        public const string RPC_BW_RESP     = "FiresGhetto.AutoTune.BwResp";
        public const string RPC_TIER_REPORT = "FiresGhetto.AutoTune.TierReport";

        // Outstanding probe RPCs the client is waiting for. Keyed by seq id.
        private static readonly Dictionary<int, Stopwatch> _pingInflight = new Dictionary<int, Stopwatch>();
        private static readonly Dictionary<int, Stopwatch> _bwInflight   = new Dictionary<int, Stopwatch>();
        private static readonly Dictionary<int, int>       _bwRequested  = new Dictionary<int, int>();

        private static readonly Dictionary<int, long> _pingResultsMs = new Dictionary<int, long>();
        private static readonly Dictionary<int, BwResult> _bwResults = new Dictionary<int, BwResult>();

        private struct BwResult
        {
            public int  Bytes;
            public long Ms;
        }

        private static int _nextSeq = 1;
        private static bool _probeRunning;
        private static bool _probeCompletedThisSession;

        // Set true when Game.m_playerInitialSpawn fires — the canonical "player has
        // arrived in the world for the first time this session" event raised from
        // Game.UpdateRespawn right after SpawnPlayer returns and the "$text_player_arrived"
        // chat message broadcasts. Reset on peer disconnect. The probe coroutine waits
        // on this so a slow-loading modpack (where pre-spawn world download, chunk
        // preload, and dungeon spawn add up to 60s+) doesn't run the probe against a
        // player who isn't actually in the world yet.
        private static bool _playerArrivedInWorld;

        // Latency-probe result fields, written by DoLatencyProbe so multiple callers
        // (initial probe + rolling re-probes) can share the same coroutine.
        private static Tier        _latLastTier;
        private static int         _latLastMedianMs;
        private static int         _latLastP95Ms;
        private static int         _latLastJitterMs;
        private static List<long>  _latLastRawMs = new List<long>();
        private static bool        _latLastAborted;

        // Cached at initial probe so rolling monitor doesn't have to re-sample fps/cpu.
        // Hardware doesn't change mid-session and FPS is stable enough on the same scene.
        private static Tier _sessionMachineTier = Tier.Medium;
        private static bool _hasMachineTier;
        private static string _sessionServerKey = string.Empty;
        private static string _sessionHwHash = string.Empty;

        // Rolling monitor state — last N tier observations
        private static readonly Queue<Tier> _rollingTiers = new Queue<Tier>();
        private static bool _rollingMonitorActive;

        // Consecutive-observation hysteresis. The mode of a small rolling window flips
        // every cycle when latency hovers at the Low/Medium boundary (~120ms) — each
        // new sample lands on a different side of ScoreNetTier's threshold, the oldest
        // matching sample gets evicted, and the 3-2 mode follows on a 2-cycle lag.
        // To prevent boundary jitter we require the "different from currently-applied"
        // mode to persist for multiple consecutive cycles before actually flipping the
        // applied tier. Promotion (toward higher tier) clears after 2 cycles; demotion
        // (toward lower tier) needs 3 — biased to keep current tier as the config
        // comment on RollingMonitorWindow has always promised.
        private static Tier _pendingRollingTier;
        private static int  _pendingRollingObservations;

        // Live coroutine handles so OnPeerDisconnected can hard-stop in-flight work
        // (prior probe still running, rolling monitor mid-cycle) and a fresh re-join
        // gets a fully clean slate instead of racing against a zombie coroutine that
        // mutates shared state.
        private static Coroutine _probeCoroutineHandle;
        private static Coroutine _rollingMonitorHandle;

        // Abort flag for in-flight coroutines. Set when ZNet.Shutdown / OnDestroy /
        // Disconnect fires. StopCoroutine should already kill the coroutine on its
        // next yield, but Unity's MonoBehaviour-to-coroutine binding is sometimes
        // flaky after a scene transition (e.g. host MonoBehaviour gets temporarily
        // disabled mid-yield), so we ALSO check this flag inside ping loops to break
        // out cleanly if StopCoroutine missed. Reset on every fresh probe kickoff.
        private static bool _probeAborted;

        /// <summary>
        /// Called from a ZNet.OnNewConnection postfix once per peer. Registers the probe
        /// RPCs on this peer in BOTH directions:
        ///   - server-side: it can answer Ping and BwReq from the client.
        ///   - client-side: it can receive Pong and BwResp from the server, and TierReport from a client.
        /// On the client this also kicks off the probe coroutine the first time we connect
        /// to a server peer (and the probe hasn't already run this session).
        /// </summary>
        public static void OnPeerConnected(ZNetPeer peer)
        {
            if (peer == null || peer.m_rpc == null) return;

            try
            {
                // Server-side handlers (no harm registering on client too — they just won't fire there
                // for inbound Ping/BwReq because clients only see their server peer reply).
                peer.m_rpc.Register<int>(RPC_PING, OnRpcPing);
                peer.m_rpc.Register<int, int>(RPC_BW_REQ, OnRpcBwReq);

                // Client-side handlers
                peer.m_rpc.Register<int>(RPC_PONG, OnRpcPong);
                peer.m_rpc.Register<int, ZPackage>(RPC_BW_RESP, OnRpcBwResp);

                // Server-side: receive client tier reports (handled by ServerAutoTune)
                peer.m_rpc.Register<int, int>(RPC_TIER_REPORT, ServerAutoTune.OnTierReport);
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[AutoTune] Failed to register probe RPCs on peer: {ex.Message}");
                return;
            }

            // Server doesn't probe outwards. Only the client kicks off the probe when it sees
            // its server peer come up.
            if (ZNet.instance != null && ZNet.instance.IsServer()) return;

            if (_probeCompletedThisSession || _probeRunning) return;

            if (FiresGhettoNetworkMod.Instance == null) return;
            if (AutoTuneConfig.EnableClientAutoTune == null || !AutoTuneConfig.EnableClientAutoTune.Value) return;

            // Subscribe to the "player has arrived" event before kicking off the probe so
            // the coroutine can wait on the real spawn-complete moment instead of guessing
            // with a timer. Reflection-resolved (see SubscribePlayerArrival) because the
            // build references both assembly_valheim and Assembly-CSharp_publicized, and
            // direct `Game.m_playerInitialSpawn` produces a CS0229 ambiguity against the
            // duplicate `Game` types defined in both.
            _playerArrivedInWorld = false;
            SubscribePlayerArrival();

            // Clear the abort flag — a fresh server connect means a fresh session.
            // Disconnect handlers set this to true; we need it false so coroutine
            // self-checks don't immediately bail.
            _probeAborted = false;

            _probeCoroutineHandle = FiresGhettoNetworkMod.Instance.StartCoroutine(RunClientProbe(peer));
        }

        // ── Game.m_playerInitialSpawn reflection plumbing ───────────────────────────
        //
        // `Game` is defined in multiple referenced Valheim assemblies (vanilla +
        // publicized), so taking a direct compile-time reference to its static event
        // triggers a duplicate-symbol ambiguity. Reflection sidesteps that — we resolve
        // the event by name at runtime, against whichever loaded assembly happens to
        // define it. Cost is a one-time AppDomain scan; the subscribe/unsubscribe path
        // is cached after that.
        private static EventInfo _playerInitialSpawnEvent;
        private static Action _playerInitialSpawnHandler;
        private static bool _playerInitialSpawnEventResolved;

        private static EventInfo ResolvePlayerInitialSpawnEvent()
        {
            if (_playerInitialSpawnEventResolved) return _playerInitialSpawnEvent;
            _playerInitialSpawnEventResolved = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType("Game", throwOnError: false); }
                catch { continue; }
                if (t == null) continue;

                var evt = t.GetEvent("m_playerInitialSpawn", BindingFlags.Public | BindingFlags.Static);
                if (evt != null) { _playerInitialSpawnEvent = evt; return evt; }
            }

            LoggerOptions.LogWarning("[AutoTune] Could not resolve Game.m_playerInitialSpawn via reflection — arrival wait will fall back to its safety timeout.");
            return null;
        }

        private static void SubscribePlayerArrival()
        {
            var evt = ResolvePlayerInitialSpawnEvent();
            if (evt == null) return;
            if (_playerInitialSpawnHandler == null) _playerInitialSpawnHandler = OnPlayerInitialSpawn;

            try
            {
                // Remove-then-add keeps it idempotent across any OnPeerConnected re-entries.
                evt.RemoveEventHandler(null, _playerInitialSpawnHandler);
                evt.AddEventHandler(null, _playerInitialSpawnHandler);
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[AutoTune] Failed to subscribe to Game.m_playerInitialSpawn: {ex.Message}");
            }
        }

        private static void UnsubscribePlayerArrival()
        {
            var evt = _playerInitialSpawnEvent;
            if (evt == null || _playerInitialSpawnHandler == null) return;
            try { evt.RemoveEventHandler(null, _playerInitialSpawnHandler); }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Handler for <c>Game.m_playerInitialSpawn</c>. Fires once per world entry;
        /// flips the gate flag so the probe coroutine can advance past its arrival-wait
        /// loop. Unsubscribes itself so we don't accumulate references across reconnects.
        /// </summary>
        private static void OnPlayerInitialSpawn()
        {
            _playerArrivedInWorld = true;
            UnsubscribePlayerArrival();
            LoggerOptions.LogInfo("[AutoTune] Game.m_playerInitialSpawn fired — player is in the world.");
        }

        /// <summary>
        /// Reset the per-session flag when we disconnect so the probe can run again
        /// next time we join a different server.
        /// </summary>
        public static void OnPeerDisconnected()
        {
            // Trip the abort flag FIRST — any in-flight coroutine that yields between
            // here and the StopCoroutine call below will see this on its next yield
            // and bail itself out. Belt-and-suspenders against Unity occasionally
            // failing to terminate a coroutine cleanly across a scene transition.
            _probeAborted = true;

            // Hard-stop any probe / rolling-monitor coroutine still in flight from
            // the prior session. Without this they continue running on the global
            // FiresGhettoNetworkMod MonoBehaviour (which survives the disconnect)
            // and race against the next session's fresh probe — both writing to
            // the same shared state (_probeRunning, _probeCompletedThisSession,
            // _rollingTiers, etc.). The zombie usually loses the race but can
            // briefly poison the new probe's results.
            var host = FiresGhettoNetworkMod.Instance;
            if (host != null)
            {
                if (_probeCoroutineHandle != null)
                {
                    try { host.StopCoroutine(_probeCoroutineHandle); } catch { /* best-effort */ }
                    _probeCoroutineHandle = null;
                }
                if (_rollingMonitorHandle != null)
                {
                    try { host.StopCoroutine(_rollingMonitorHandle); } catch { /* best-effort */ }
                    _rollingMonitorHandle = null;
                }
            }

            _probeRunning = false;
            _probeCompletedThisSession = false;
            _rollingMonitorActive = false;
            _rollingTiers.Clear();
            _pendingRollingTier = Tier.Low;
            _pendingRollingObservations = 0;
            _pingInflight.Clear();
            _bwInflight.Clear();
            _bwRequested.Clear();
            _pingResultsMs.Clear();
            _bwResults.Clear();

            // Drop the arrival flag + handler so the next world entry starts from a clean
            // slate. Game.m_playerInitialSpawn is one-shot per Game instance, so a fresh
            // connection always needs a fresh subscription.
            _playerArrivedInWorld = false;
            UnsubscribePlayerArrival();
        }

        // ============================================================
        //  Server-side RPC handlers (echo work)
        // ============================================================

        private static void OnRpcPing(ZRpc rpc, int seq)
        {
            // Just echo back. Server-side cost is one packet; we don't even allocate.
            try { rpc.Invoke(RPC_PONG, seq); }
            catch (Exception ex) { LoggerOptions.LogWarning($"[AutoTune] Ping echo failed: {ex.Message}"); }
        }

        private static void OnRpcBwReq(ZRpc rpc, int seq, int requestedBytes)
        {
            // Cap requestedBytes server-side — never allocate more than 256KB per probe regardless
            // of what the client asked for. This is the anti-abuse gate (a malicious mod could
            // request gigabytes otherwise).
            int safeBytes = Mathf.Clamp(requestedBytes, 1024, 256 * 1024);

            try
            {
                ZPackage pkg = new ZPackage();
                // Pack a constant-byte payload. Compressors are present in this mod
                // (CompressionGroup uses zstd) and a stream of zeros compresses heavily,
                // which would defeat the bandwidth test. Use a varied pattern so zstd
                // can't squash it.
                byte[] payload = new byte[safeBytes];
                for (int i = 0; i < safeBytes; i++)
                {
                    payload[i] = (byte)((i * 1103515245 + 12345) >> 8);
                }
                pkg.Write(payload);

                rpc.Invoke(RPC_BW_RESP, seq, pkg);
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[AutoTune] Bandwidth echo failed: {ex.Message}");
            }
        }

        // ============================================================
        //  Client-side RPC handlers (record measurements)
        // ============================================================

        private static void OnRpcPong(ZRpc rpc, int seq)
        {
            Stopwatch sw;
            if (_pingInflight.TryGetValue(seq, out sw))
            {
                sw.Stop();
                _pingResultsMs[seq] = sw.ElapsedMilliseconds;
                _pingInflight.Remove(seq);
            }
        }

        private static void OnRpcBwResp(ZRpc rpc, int seq, ZPackage pkg)
        {
            Stopwatch sw;
            if (_bwInflight.TryGetValue(seq, out sw))
            {
                sw.Stop();
                int requested;
                _bwRequested.TryGetValue(seq, out requested);
                int actualBytes = 0;
                try
                {
                    byte[] arr = pkg?.GetArray();
                    actualBytes = arr?.Length ?? requested;
                }
                catch { actualBytes = requested; }

                _bwResults[seq] = new BwResult { Bytes = actualBytes, Ms = sw.ElapsedMilliseconds };
                _bwInflight.Remove(seq);
                _bwRequested.Remove(seq);
            }
        }

        // ============================================================
        //  The probe coroutine
        // ============================================================

        private static IEnumerator RunClientProbe(ZNetPeer serverPeer)
        {
            _probeRunning = true;

            // ---- 1. Wait for the player to actually arrive in the world ----
            //
            // Game.m_playerInitialSpawn fires once per world entry from Game.UpdateRespawn,
            // right after SpawnPlayer returns and the "$text_player_arrived" chat message
            // broadcasts. That is the precise moment the local player is in the world with
            // control of their character.
            //
            // Why this instead of a fixed timer from connection: heavy modpacks (Jotunn
            // prefab registration, BetterContinents cache loads, large world-data downloads,
            // dungeon spawning) routinely push spawn out to 60+ seconds after the peer
            // connects. A timer that fires before spawn measures latency on a wire that's
            // still being flooded with vanilla world-init traffic — the probe gets junk
            // samples and the heavy server pushes we're guarding fire mid-load.
            //
            // The timeout is a safety valve only — if the spawn fails entirely we
            // eventually proceed anyway rather than dangle the player at a degraded
            // default tier forever.
            float arrivalTimeout = AutoTuneConfig.PlayerArrivalTimeoutSeconds?.Value ?? 180f;
            float arrivalWait = 0f;
            while (!_playerArrivedInWorld && arrivalWait < arrivalTimeout)
            {
                // _probeAborted + ZNet.instance == null catch the main-menu logout case
                // that the per-peer check below misses: ZNetPeer.Dispose disposes m_rpc
                // but doesn't null serverPeer.m_rpc, so spinning here would otherwise
                // hold the coroutine alive until arrivalTimeout fires and then crash on
                // the cache lookup at main menu.
                if (_probeAborted || ZNet.instance == null) { ProbeAbort("aborted (disconnect/shutdown) while waiting for player arrival"); yield break; }
                if (serverPeer == null || serverPeer.m_rpc == null) { ProbeAbort("peer dropped while waiting for player arrival"); yield break; }
                yield return null;
                arrivalWait += Time.unscaledDeltaTime;
            }

            if (!_playerArrivedInWorld)
            {
                LoggerOptions.LogWarning($"[AutoTune] Game.m_playerInitialSpawn never fired within {arrivalTimeout:0}s — proceeding with probe anyway, results may be unreliable.");
            }
            else
            {
                LoggerOptions.LogInfo($"[AutoTune] Player arrived after {arrivalWait:0.0}s — settling before probe.");
            }

            // ---- 2. Post-arrival settle ----
            //
            // Short window after the player arrives, to let the immediate post-spawn burst
            // (inventory equip, ZDO zone-load for the spawn point, post-spawn mod work)
            // subside before we start sampling latency. ProbeStartDelaySeconds is the
            // user-tunable knob for this; 30s is the safe default.
            float spawnDelay = AutoTuneConfig.ProbeStartDelaySeconds?.Value ?? 10f;
            float waited = 0f;
            while (waited < spawnDelay)
            {
                if (_probeAborted || ZNet.instance == null) { ProbeAbort("aborted (disconnect/shutdown) during post-arrival settle"); yield break; }
                if (serverPeer == null || serverPeer.m_rpc == null) { ProbeAbort("peer dropped during post-arrival settle"); yield break; }
                yield return null;
                waited += Time.unscaledDeltaTime;
            }

            // ---- 2. Hardware fingerprint ----
            int cores  = Mathf.Max(1, SystemInfo.processorCount);
            int ramMb  = Mathf.Max(0, SystemInfo.systemMemorySize);
            string gpu = SystemInfo.graphicsDeviceName ?? "unknown";
            string hwHash = MakeHwHash(cores, ramMb, gpu);
            Tier cpuTier = ScoreCpuTier(cores, ramMb);

            // ---- 3. Cache lookup ----
            string serverKey = MakeServerKey(serverPeer);
            bool retuneEveryLogin = AutoTuneConfig.RetuneOnEveryLogin?.Value ?? false;
            if (!retuneEveryLogin)
            {
                // Wrap the cache lookup in defensive try/catch: AutoTuneCache uses
                // Newtonsoft.Json (typically brought in by Jotunn or other mods). If a
                // user's modset profile is missing Newtonsoft.Json the JIT throws
                // FileNotFoundException when AutoTuneCache.TryGet is first called —
                // and the catch INSIDE TryGet can't catch its own JIT failure, the
                // exception surfaces at the call site. Catching here means "treat as
                // no cache, run full probe" instead of crashing the coroutine and
                // leaving the tier stuck at the default.
                AutoTuneCache.CacheEntry cached = null;
                try
                {
                    cached = AutoTuneCache.TryGet(serverKey, hwHash);
                }
                catch (FileNotFoundException ex)
                {
                    LoggerOptions.LogWarning($"[AutoTune] Cache dep missing ({ex.FileName ?? "Newtonsoft.Json"}); running full probe instead of cached fast-path.");
                }
                catch (TypeLoadException ex)
                {
                    LoggerOptions.LogWarning($"[AutoTune] Cache type load failed ({ex.Message}); running full probe.");
                }
                catch (Exception ex)
                {
                    LoggerOptions.LogWarning($"[AutoTune] Cache lookup threw {ex.GetType().Name}: {ex.Message}; running full probe.");
                }
                if (cached != null)
                {
                    LoggerOptions.LogInfo($"[AutoTune] Using cached tier for {serverKey}: {cached.Tier} (probed {(int)(DateTime.UtcNow - cached.TimestampUtc).TotalDays}d ago)");
                    AutoTuneState.SetClient(cached.Tier, cached.PingMedianMs);
                    ApplyClientTier(cached.Tier);
                    SendTierReport(serverPeer, cached.Tier, cached.PingMedianMs);
                    _probeCompletedThisSession = true;
                    _probeRunning = false;

                    // Rolling monitor still kicks in on cached hits — the cached tier
                    // could be stale (network conditions changed since last session)
                    // and the monitor will revalidate on its first re-probe interval.
                    // Use cpuTier as a conservative machine ceiling; we skip the fps
                    // sample on cache hit to keep the path quick.
                    _sessionServerKey   = serverKey;
                    _sessionHwHash      = hwHash;
                    _sessionMachineTier = cpuTier;
                    _hasMachineTier     = true;
                    if (FiresGhettoNetworkMod.Instance != null)
                        _rollingMonitorHandle = FiresGhettoNetworkMod.Instance.StartCoroutine(RunRollingMonitor(serverPeer));

                    yield break;
                }
            }

            LoggerOptions.LogMessage($"[AutoTune] Starting probe — cores={cores}, RAM={ramMb}MB, GPU={gpu}");

            // Stash session-scoped state so the rolling monitor can reuse it
            // without re-detecting hardware or re-resolving the cache key.
            _sessionServerKey = serverKey;
            _sessionHwHash    = hwHash;

            // ---- 4. Latency probe ----
            yield return DoLatencyProbe(serverPeer);

            if (_latLastAborted)
            {
                LoggerOptions.LogMessage($"[AutoTune] Initial latency probe aborted (raw=[{string.Join(",", _latLastRawMs)}]) — defaulting to LOW tier, skipping bandwidth probe.");
                Finalize(serverKey, hwHash, Tier.Low, _latLastMedianMs, serverPeer);
                yield break;
            }

            int  pingMedian = _latLastMedianMs;
            Tier netTier    = _latLastTier;

            LoggerOptions.LogMessage($"[AutoTune] Latency: raw=[{string.Join(",", _latLastRawMs)}] trimmed→ median={pingMedian}ms p95={_latLastP95Ms}ms iqrJitter={_latLastJitterMs}ms → {netTier}");

            // ---- 5. Frame-time sample (5s passive) ----
            Tier fpsTier = Tier.Medium;
            {
                List<float> frameMs = new List<float>(300);
                float collected = 0f;
                while (collected < 5f)
                {
                    yield return null;
                    float dt = Time.unscaledDeltaTime;
                    if (dt > 0f && dt < 1f) frameMs.Add(dt * 1000f);
                    collected += dt;
                }

                if (frameMs.Count > 0)
                {
                    float fpsMedian = MedianFloat(frameMs);
                    float fpsP95    = P95Float(frameMs);
                    fpsTier         = ScoreFpsTier(fpsMedian, fpsP95);
                    LoggerOptions.LogMessage($"[AutoTune] Frame time: median={fpsMedian:0.0}ms p95={fpsP95:0.0}ms → {fpsTier}");
                }
            }

            // ---- 6. Bandwidth probe (gated) ----
            // Only run if latency tier is MED or HIGH. LOW already proved fragile.
            //
            // Two-axis tier scoring rationale:
            //   netTier above is purely LATENCY-derived. Bandwidth is CONFIRMING, not
            //   authoritative — because our 32KB sample to a single server is bounded
            //   by that server's send-rate cap, NOT the client's actual link capacity.
            //   A 1Gbit client on a server capped at 512KB/s will measure ~512KB/s and
            //   look like "Medium" when their machine could comfortably handle HIGH.
            //
            //   We split this into two signals:
            //     * machineTier (cpuTier + fpsTier minimum) — pure local hardware/render perf
            //     * linkTier (latency-only) — pure connection quality
            //   When BOTH come back HIGH, we trust them and stay HIGH regardless of bandwidth.
            //   When one is already imperfect, bandwidth gets a vote — but only one tier
            //   step down from min(machine, latency), never further. This keeps a strong
            //   client on a server-bottlenecked link from being downgraded to LOW.
            float bwKbPerSec = -1f;
            bool bwProbeCompleted = false;

            if (netTier != Tier.Low)
            {
                // Multi-sample bandwidth: take 3 samples and pick the PEAK. Single
                // samples get bounded by transient server load — the moment we send
                // BwReq, the server might be busy serving someone else's RPC and our
                // response gets queued behind. Taking 3 samples and picking the peak
                // catches the moment where the link IS unblocked, giving us a more
                // accurate ceiling on what this server-link combo can deliver.
                int payloadBytes = AutoTuneConfig.ProbeBandwidthPayloadBytes?.Value ?? (32 * 1024);
                float bwTimeout = Mathf.Max(2f, (AutoTuneConfig.ProbePingTimeoutSeconds?.Value ?? 5f) * 1.5f);
                int sampleCount = 3;
                List<float> bwSamples = new List<float>(sampleCount);
                int timeouts = 0;

                for (int s = 0; s < sampleCount; s++)
                {
                    if (serverPeer == null || serverPeer.m_rpc == null) break;

                    int seq = NextSeq();
                    Stopwatch sw = Stopwatch.StartNew();
                    _bwInflight[seq] = sw;
                    _bwRequested[seq] = payloadBytes;

                    bool sent = TryInvoke(serverPeer, RPC_BW_REQ, seq, payloadBytes);
                    if (!sent)
                    {
                        _bwInflight.Remove(seq);
                        _bwRequested.Remove(seq);
                        break;
                    }

                    float waitSec = 0f;
                    while (_bwInflight.ContainsKey(seq) && waitSec < bwTimeout)
                    {
                        yield return null;
                        waitSec += Time.unscaledDeltaTime;
                    }

                    if (_bwInflight.ContainsKey(seq))
                    {
                        _bwInflight.Remove(seq);
                        _bwRequested.Remove(seq);
                        timeouts++;
                    }
                    else if (_bwResults.TryGetValue(seq, out BwResult bwRes))
                    {
                        _bwResults.Remove(seq);
                        if (bwRes.Ms > 0)
                        {
                            float kbps = (bwRes.Bytes / 1024f) / (bwRes.Ms / 1000f);
                            bwSamples.Add(kbps);
                        }
                    }

                    // Brief gap between samples — let any queued traffic clear before we
                    // hit the server again. Without this the second/third sample can show
                    // back-pressure from our own previous sample.
                    if (s < sampleCount - 1) yield return new WaitForSeconds(0.5f);
                }

                if (bwSamples.Count > 0)
                {
                    // Peak (max) wins — this is the LEAST contended sample we got, and
                    // therefore the closest estimate of the link's actual capacity.
                    float peak = bwSamples[0];
                    foreach (var v in bwSamples) if (v > peak) peak = v;
                    bwKbPerSec = peak;
                    bwProbeCompleted = true;
                    LoggerOptions.LogMessage($"[AutoTune] Bandwidth: samples=[{string.Join(",", bwSamples.ConvertAll(v => v.ToString("0")))}] KB/s, peak={bwKbPerSec:0} KB/s, timeouts={timeouts}");
                }
                else if (timeouts > 0)
                {
                    LoggerOptions.LogMessage($"[AutoTune] Bandwidth probe: all {timeouts} samples timed out — treated as bandwidth-bad signal.");
                    bwKbPerSec = 0f;
                    bwProbeCompleted = true;
                }
            }

            // ---- 7. Final tier — two-axis combine ----
            Tier machineTier = MinTier(cpuTier, fpsTier);
            Tier latencyTier = netTier;     // already pure-latency, before any BW adjustment
            Tier finalTier   = ComputeFinalTier(machineTier, latencyTier, bwKbPerSec, bwProbeCompleted);

            // Stash for the rolling monitor — machine tier is fixed for the session.
            _sessionMachineTier = machineTier;
            _hasMachineTier     = true;

            LoggerOptions.LogMessage($"[AutoTune] Final tier: machine={machineTier} (cpu={cpuTier} fps={fpsTier}) latency={latencyTier} bw={(bwProbeCompleted ? bwKbPerSec.ToString("0") + "KB/s" : "n/a")} → {finalTier}");

            Finalize(serverKey, hwHash, finalTier, pingMedian, serverPeer);

            // Hand off to rolling monitor for the rest of the session
            if (FiresGhettoNetworkMod.Instance != null)
                _rollingMonitorHandle = FiresGhettoNetworkMod.Instance.StartCoroutine(RunRollingMonitor(serverPeer));
        }

        /// <summary>
        /// Capability-ceiling tier combine. The machine tier (min of cpu + fps) is
        /// the CEILING — a good connection can never push the client above what its
        /// hardware can actually deliver. Only a genuinely BAD link pulls the tier
        /// DOWN, and only by 'Link Downgrade Cap' steps (default 1), floored at Low.
        /// A strong PC on a 170ms link lands MED, not LOW; okay/medium ping costs
        /// nothing. Bandwidth is a confirming signal only: it can push the link tier
        /// down one step, never touch the machine tier. Replaces the old
        /// min(machine, latency) collapse that floored capable clients on a bad ping.
        /// </summary>
        private static Tier ComputeFinalTier(Tier machineTier, Tier latencyTier, float bwKbPerSec, bool bwProbeCompleted)
        {
            Tier linkTier = latencyTier;

            // Bandwidth confirms a constrained link by pushing the link tier down one
            // step (never up, never onto the machine tier). It takes BOTH mediocre ping
            // AND poor throughput to flag a link as bad — a clean-ping link with a fuzzy
            // bandwidth sample is not penalized.
            if (bwProbeCompleted)
            {
                Tier bwTier = ScoreBwTier(bwKbPerSec);
                if ((int)bwTier < (int)linkTier)
                    linkTier = StepTowardLow(linkTier, 1);
            }

            // Only a bad (Low) link triggers a downgrade. MED/HIGH link → machine tier stands.
            if (linkTier != Tier.Low)
                return machineTier;

            int cap = AutoTuneConfig.LinkDowngradeCap?.Value ?? 1;
            return StepTowardLow(machineTier, cap);
        }

        // Move a tier toward Low by 'steps', floored at Low. Tier is declared
        // Low=0 < Medium < High, so stepping toward Low is a clamped decrement —
        // but this routes through the enum values rather than assuming the int
        // layout, so it stays correct if the enum order ever changes.
        private static Tier StepTowardLow(Tier tier, int steps)
        {
            int v = (int)tier - System.Math.Max(0, steps);
            int floor = (int)Tier.Low;
            return (Tier)System.Math.Max(floor, v);
        }

        private static Tier ScoreBwTier(float kbPerSec)
        {
            if (kbPerSec >= 500f) return Tier.High;
            if (kbPerSec >= 200f) return Tier.Medium;
            return Tier.Low;
        }

        // ============================================================
        //  Latency probe (reusable for initial + rolling re-probes)
        // ============================================================

        /// <summary>
        /// Sends a warmup ping + N timed pings, populates the _latLast* result fields.
        /// Used by both the initial probe and the rolling monitor — the rolling monitor
        /// only needs latency, no need to re-run the bandwidth or fps stages.
        ///
        /// Why this is more involved than "send 5 pings, take stats":
        ///   The probe fires while Valheim's own traffic shares the same Steam socket.
        ///   A couple of samples come back contaminated by being stuck behind a big
        ///   payload, which shows up as high jitter and high p95 even on a 600Mbps/23ms
        ///   link. The stats need to be robust against that:
        ///     * 1 untracked warmup ping clears Steam TCP slow-start and lets the local
        ///       outbound queue drain a beat.
        ///     * Drop the single worst sample before computing stats (10% trim @ 10 pings).
        ///     * Jitter = IQR (q75 − q25), not max − min — outliers can't drag it.
        ///     * Abort to LOW only if 2+ samples exceed the bad-ping threshold; a single
        ///       straggler isn't a fragile-client signal.
        /// </summary>
        private static IEnumerator DoLatencyProbe(ZNetPeer serverPeer)
        {
            int   pingCount      = AutoTuneConfig.ProbePingCount?.Value ?? 10;
            float pingTimeoutSec = AutoTuneConfig.ProbePingTimeoutSeconds?.Value ?? 5f;
            int   pingAbortMs    = AutoTuneConfig.ProbePingAbortMs?.Value ?? 2000;

            _latLastAborted = false;
            _latLastRawMs   = new List<long>(pingCount);

            // Warmup ping — discarded result, just primes the path.
            if (serverPeer != null && serverPeer.m_rpc != null)
            {
                int warmSeq = NextSeq();
                _pingInflight[warmSeq] = Stopwatch.StartNew();
                if (TryInvoke(serverPeer, RPC_PING, warmSeq))
                {
                    float warmWait = 0f;
                    while (_pingInflight.ContainsKey(warmSeq) && warmWait < pingTimeoutSec)
                    {
                        yield return null;
                        warmWait += Time.unscaledDeltaTime;
                    }
                }
                _pingInflight.Remove(warmSeq);
                _pingResultsMs.Remove(warmSeq);
                yield return new WaitForSeconds(0.3f);
            }

            int badSamples = 0;
            for (int i = 0; i < pingCount; i++)
            {
                if (serverPeer == null || serverPeer.m_rpc == null) break;

                // Bail mid-probe if a disconnect/shutdown tripped the abort flag.
                // Without this check the probe runs all 10 pings against a dead
                // peer (each timing out at 5s) before yielding back to the rolling
                // loop's outer ZNet.instance check, spamming "Rolling re-probe
                // aborted (raw=[2000,2000,...])" lines after the user has already
                // logged out to main menu.
                if (_probeAborted || ZNet.instance == null) break;

                int seq = NextSeq();
                Stopwatch sw = Stopwatch.StartNew();
                _pingInflight[seq] = sw;

                if (!TryInvoke(serverPeer, RPC_PING, seq))
                {
                    _pingInflight.Remove(seq);
                    break;
                }

                float waitedSec = 0f;
                while (_pingInflight.ContainsKey(seq) && waitedSec < pingTimeoutSec)
                {
                    yield return null;
                    waitedSec += Time.unscaledDeltaTime;
                }

                long sample;
                if (_pingInflight.ContainsKey(seq))
                {
                    _pingInflight.Remove(seq);
                    sample = pingAbortMs;
                }
                else if (_pingResultsMs.TryGetValue(seq, out long rtt))
                {
                    _pingResultsMs.Remove(seq);
                    sample = rtt;
                }
                else
                {
                    continue;
                }
                _latLastRawMs.Add(sample);

                if (sample >= pingAbortMs) badSamples++;

                if (badSamples >= 2)
                {
                    _latLastAborted = true;
                    _latLastTier    = Tier.Low;
                    _latLastMedianMs = (int)ComputeMedian(_latLastRawMs);
                    _latLastP95Ms    = pingAbortMs;
                    _latLastJitterMs = pingAbortMs;
                    yield break;
                }

                yield return new WaitForSeconds(0.2f);
            }

            if (_latLastRawMs.Count == 0)
            {
                _latLastAborted  = true;
                _latLastTier     = Tier.Low;
                _latLastMedianMs = 0;
                _latLastP95Ms    = 0;
                _latLastJitterMs = 0;
                yield break;
            }

            // Outlier trimming — drop worst sample (10% @ 10 pings).
            List<long> trimmed = TrimWorst(_latLastRawMs, dropCount: _latLastRawMs.Count >= 5 ? 1 : 0);

            _latLastMedianMs = (int)ComputeMedian(trimmed);
            _latLastP95Ms    = (int)ComputeP95(trimmed);
            _latLastJitterMs = ComputeIqrJitter(trimmed);
            _latLastTier     = ScoreNetTier(_latLastMedianMs, _latLastP95Ms, _latLastJitterMs);
        }

        // ============================================================
        //  Rolling monitor (continuous re-probe over the session)
        // ============================================================

        /// <summary>
        /// Started after the initial probe finalizes. Re-probes latency every N minutes,
        /// pushes the resulting tier into a rolling buffer, computes a "consensus" tier
        /// (mode of the buffer), and gates the actual apply behind a consecutive-observation
        /// streak — same dissenting consensus must hold for 2 cycles (promotion toward a
        /// higher tier) or 3 cycles (demotion toward a lower tier) before flipping.
        ///
        /// Without that streak gate, mode-of-N alone oscillates when latency sits at the
        /// ScoreNetTier threshold (~120ms): each new sample lands on a different side, the
        /// oldest matching sample gets evicted, and a 3-2 mode flips every cycle on a
        /// 2-cycle lag. Demotion is stricter than promotion because giving up performance
        /// to a momentarily-bad probe costs more than briefly punching above weight.
        ///
        /// Skips the bandwidth + fps stages — those don't need re-checking continuously.
        /// FPS is captured at initial probe and held; bandwidth on the same server has the
        /// same server-bottleneck issue every time so retesting wouldn't tell us anything new.
        /// </summary>
        private static IEnumerator RunRollingMonitor(ZNetPeer serverPeer)
        {
            if (!(AutoTuneConfig.EnableRollingMonitor?.Value ?? true)) yield break;
            if (_rollingMonitorActive) yield break;
            _rollingMonitorActive = true;

            float intervalMin = AutoTuneConfig.RollingMonitorIntervalMinutes?.Value ?? 5f;
            int   windowSize  = AutoTuneConfig.RollingMonitorWindow?.Value ?? 5;
            float intervalSec = intervalMin * 60f;

            // Seed buffer with the initial tier so the first re-probe doesn't immediately
            // shift the consensus on a single observation.
            _rollingTiers.Clear();
            _rollingTiers.Enqueue(AutoTuneState.ClientTier);

            // Reset the consecutive-observation streak — fresh rolling session starts
            // aligned with the currently-applied tier (no pending change in flight).
            _pendingRollingTier = AutoTuneState.ClientTier;
            _pendingRollingObservations = 0;

            LoggerOptions.LogInfo($"[AutoTune] Rolling monitor started — re-probe every {intervalMin:0}min, window={windowSize}");

            while (_probeCompletedThisSession)
            {
                // Top-of-loop fast bail: if a disconnect/shutdown happened during the
                // last DoLatencyProbe and the abort flag was tripped, terminate the
                // rolling monitor now instead of running another sleep+probe cycle.
                if (_probeAborted || ZNet.instance == null) { _rollingMonitorActive = false; yield break; }

                // Sleep the interval, abort if peer drops or session ends.
                // ZNet.instance check covers main-menu logout: ZNet.Shutdown clears the
                // singleton even when the per-peer Disconnect hook misses (StopAll
                // calls ZNetPeer.Dispose which doesn't null serverPeer.m_rpc, so the
                // direct peer check below isn't enough on its own).
                float waited = 0f;
                while (waited < intervalSec)
                {
                    yield return new WaitForSeconds(1f);
                    waited += 1f;
                    if (_probeAborted)                                  { _rollingMonitorActive = false; yield break; }
                    if (ZNet.instance == null)                          { _rollingMonitorActive = false; yield break; }
                    if (serverPeer == null || serverPeer.m_rpc == null) { _rollingMonitorActive = false; yield break; }
                    if (!_probeCompletedThisSession)                    { _rollingMonitorActive = false; yield break; }
                }

                // One more check after the long sleep before kicking off the probe.
                if (_probeAborted || ZNet.instance == null) { _rollingMonitorActive = false; yield break; }

                yield return DoLatencyProbe(serverPeer);

                // Post-probe check: if abort was set DURING DoLatencyProbe, bail before
                // we read _latLastAborted (which is set to true on legitimate aborts too).
                if (_probeAborted || ZNet.instance == null) { _rollingMonitorActive = false; yield break; }

                if (_latLastAborted)
                {
                    LoggerOptions.LogMessage($"[AutoTune] Rolling re-probe aborted (raw=[{string.Join(",", _latLastRawMs)}]); leaving tier at {AutoTuneState.ClientTier}.");
                    continue;
                }

                // Combine fresh latency tier with stable machine tier using existing rule
                Tier observedFinal = ComputeFinalTier(_sessionMachineTier, _latLastTier, /*bw*/ -1f, /*bwProbeCompleted*/ false);

                _rollingTiers.Enqueue(observedFinal);
                while (_rollingTiers.Count > windowSize) _rollingTiers.Dequeue();

                Tier consensus = ComputeMode(_rollingTiers);
                Tier currentApplied = AutoTuneState.ClientTier;

                if (consensus != currentApplied)
                {
                    // Consecutive-observation hysteresis. Same dissenting consensus has to
                    // hold across N cycles before we actually flip the applied tier.
                    // Promotion = move toward a HIGHER tier (e.g. Low→Medium, Medium→High);
                    // demotion = move toward a LOWER tier. Demotion is stricter (3 vs 2)
                    // because giving up performance is more costly than briefly punching
                    // above weight on a momentarily-good probe.
                    if (_pendingRollingTier == consensus)
                    {
                        _pendingRollingObservations++;
                    }
                    else
                    {
                        _pendingRollingTier = consensus;
                        _pendingRollingObservations = 1;
                    }

                    int required = (int)consensus > (int)currentApplied ? 2 : 3;

                    if (_pendingRollingObservations >= required)
                    {
                        LoggerOptions.LogMessage($"[AutoTune] Rolling tier change: {currentApplied} → {consensus} ({_pendingRollingObservations} consecutive obs; this probe: latency={_latLastTier} median={_latLastMedianMs}ms; window=[{string.Join(",", _rollingTiers)}])");
                        AutoTuneState.SetClient(consensus, _latLastMedianMs);
                        ApplyClientTier(consensus);
                        try { AutoTuneCache.Save(_sessionServerKey, consensus, _latLastMedianMs, _sessionHwHash); }
                        catch (Exception ex) { LoggerOptions.LogWarning($"[AutoTune] Cache save skipped ({ex.GetType().Name}: {ex.Message})."); }
                        SendTierReport(serverPeer, consensus, _latLastMedianMs);
                        // Reset streak — applied, no longer pending.
                        _pendingRollingObservations = 0;
                    }
                    else
                    {
                        LoggerOptions.LogInfo($"[AutoTune] Rolling tier change pending: {currentApplied} → {consensus} (obs {_pendingRollingObservations}/{required}; this probe: latency={_latLastTier} median={_latLastMedianMs}ms; window=[{string.Join(",", _rollingTiers)}])");
                    }
                }
                else
                {
                    // Consensus matches applied — any pending change in flight is invalidated
                    // (the boundary jitter swung back our way). Reset streak to currentApplied.
                    _pendingRollingTier = currentApplied;
                    _pendingRollingObservations = 0;
                    LoggerOptions.LogInfo($"[AutoTune] Rolling re-probe stable at {consensus} (this probe: latency={_latLastTier} median={_latLastMedianMs}ms iqrJitter={_latLastJitterMs}ms; window=[{string.Join(",", _rollingTiers)}])");
                }
            }

            _rollingMonitorActive = false;
        }

        /// <summary>
        /// Mode (most-common element) of a small queue of tiers. Ties are broken by
        /// preferring the CURRENT applied tier — keeps the system stable rather than
        /// oscillating between two equally-supported observations. If the current tier
        /// isn't part of the tie, falls back to preferring the lower (safer) tier.
        /// </summary>
        private static Tier ComputeMode(IEnumerable<Tier> tiers)
        {
            int low = 0, med = 0, high = 0;
            foreach (var t in tiers)
            {
                if (t == Tier.Low) low++;
                else if (t == Tier.Medium) med++;
                else if (t == Tier.High) high++;
            }
            int max = Math.Max(low, Math.Max(med, high));

            bool lowMax  = low  == max;
            bool medMax  = med  == max;
            bool highMax = high == max;

            // Single max — that's the mode.
            if (lowMax  && !medMax && !highMax) return Tier.Low;
            if (medMax  && !lowMax && !highMax) return Tier.Medium;
            if (highMax && !lowMax && !medMax)  return Tier.High;

            // Tie — keep the current applied tier when it's among the tied set,
            // otherwise pick the lower (safer) tied tier.
            Tier current = AutoTuneState.HasClientResult ? AutoTuneState.ClientTier : Tier.Medium;
            if (current == Tier.Low    && lowMax)  return Tier.Low;
            if (current == Tier.Medium && medMax)  return Tier.Medium;
            if (current == Tier.High   && highMax) return Tier.High;

            if (lowMax)  return Tier.Low;
            if (medMax)  return Tier.Medium;
            return Tier.High;
        }

        private static void Finalize(string serverKey, string hwHash, Tier tier, int pingMedianMs, ZNetPeer serverPeer)
        {
            AutoTuneState.SetClient(tier, pingMedianMs);
            try { AutoTuneCache.Save(serverKey, tier, pingMedianMs, hwHash); }
            catch (Exception ex) { LoggerOptions.LogWarning($"[AutoTune] Cache save skipped ({ex.GetType().Name}: {ex.Message}); tier still applied in-memory."); }
            ApplyClientTier(tier);
            SendTierReport(serverPeer, tier, pingMedianMs);
            _probeCompletedThisSession = true;
            _probeRunning = false;
        }

        private static void ProbeAbort(string reason)
        {
            LoggerOptions.LogWarning($"[AutoTune] Probe aborted: {reason} — defaulting to LOW tier.");
            AutoTuneState.SetClient(Tier.Low, 0);
            ApplyClientTier(Tier.Low);
            _probeCompletedThisSession = true;
            _probeRunning = false;
        }

        private static void ApplyClientTier(Tier tier)
        {
            // Steam send rates — re-apply through the existing path so the Steamworks side
            // picks up the new values. NetworkRatesGroup.ApplySendRates() reads through
            // EffectiveConfig now, so this is automatic.
            try { NetworkingRatesGroup.ApplySendRates(); }
            catch (Exception ex) { LoggerOptions.LogWarning($"[AutoTune] ApplySendRates failed: {ex.Message}"); }

            // Steam send buffer (per-connection outbound) — main lever for k_EResultLimitExceeded
            try { NetworkingRatesGroup.ApplySendBufferSize(); }
            catch (Exception ex) { LoggerOptions.LogWarning($"[AutoTune] ApplySendBufferSize failed: {ex.Message}"); }

            // Steam recv buffer — set via the same reflection helper (no-op on Valheim's older Steamworks build)
            try { NetworkingRatesGroup.ApplyRecvBufferSize(); }
            catch (Exception ex) { LoggerOptions.LogWarning($"[AutoTune] ApplyRecvBufferSize failed: {ex.Message}"); }

            // Steam per-message ceiling — must rise alongside the recv buffer or
            // large reliable messages still hit Steam's 512 KB default cap and get
            // rejected with "Reliable message size too large".
            try { NetworkingRatesGroup.ApplyRecvMaxMessageSize(); }
            catch (Exception ex) { LoggerOptions.LogWarning($"[AutoTune] ApplyRecvMaxMessageSize failed: {ex.Message}"); }

            LoggerOptions.LogMessage($"[AutoTune] Applied client tier {tier}");
        }

        private static void SendTierReport(ZNetPeer serverPeer, Tier tier, int pingMedianMs)
        {
            if (serverPeer == null || serverPeer.m_rpc == null) return;
            try
            {
                serverPeer.m_rpc.Invoke(RPC_TIER_REPORT, (int)tier, pingMedianMs);
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[AutoTune] TierReport invoke failed: {ex.Message}");
            }
        }

        // ============================================================
        //  Scoring functions
        // ============================================================

        private static Tier ScoreCpuTier(int cores, int ramMb)
        {
            if (cores >= 8 && ramMb >= 16 * 1024) return Tier.High;
            if (cores >= 4 && ramMb >= 8  * 1024) return Tier.Medium;
            return Tier.Low;
        }

        private static Tier ScoreNetTier(int medianMs, int p95Ms, int jitterMs)
        {
            if (medianMs <= 60  && p95Ms <= 100 && jitterMs <= 20) return Tier.High;
            if (medianMs <= 120 && p95Ms <= 200 && jitterMs <= 50) return Tier.Medium;
            return Tier.Low;
        }

        private static Tier ScoreFpsTier(float medianMs, float p95Ms)
        {
            if (medianMs <= 16.7f && p95Ms <= 25f) return Tier.High;
            if (medianMs <= 33.4f && p95Ms <= 50f) return Tier.Medium;
            return Tier.Low;
        }

        private static Tier MinTier(Tier a, Tier b)
        {
            return (Tier)Mathf.Min((int)a, (int)b);
        }

        // ============================================================
        //  Math helpers
        // ============================================================

        private static double ComputeMedian(List<long> xs)
        {
            if (xs.Count == 0) return 0;
            var copy = new List<long>(xs);
            copy.Sort();
            int mid = copy.Count / 2;
            return (copy.Count % 2 == 0)
                ? (copy[mid - 1] + copy[mid]) * 0.5
                : copy[mid];
        }

        private static double ComputeP95(List<long> xs)
        {
            if (xs.Count == 0) return 0;
            var copy = new List<long>(xs);
            copy.Sort();
            int idx = Mathf.Clamp((int)Math.Ceiling(copy.Count * 0.95) - 1, 0, copy.Count - 1);
            return copy[idx];
        }

        private static int ComputeJitter(List<long> xs)
        {
            if (xs.Count < 2) return 0;
            long minV = xs[0], maxV = xs[0];
            foreach (var v in xs)
            {
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }
            return (int)(maxV - minV);
        }

        /// <summary>
        /// IQR-based jitter (q75 − q25). Robust against outliers — a single bad
        /// sample sitting in the top 25% can't drag this number up the way max-min
        /// jitter could. Falls back to max-min when sample count is too low for
        /// quartiles to mean anything (≤3 samples).
        /// </summary>
        private static int ComputeIqrJitter(List<long> xs)
        {
            if (xs.Count < 4) return ComputeJitter(xs);
            var sorted = new List<long>(xs);
            sorted.Sort();
            int q1Idx = sorted.Count / 4;
            int q3Idx = (sorted.Count * 3) / 4;
            if (q3Idx >= sorted.Count) q3Idx = sorted.Count - 1;
            return (int)(sorted[q3Idx] - sorted[q1Idx]);
        }

        /// <summary>
        /// Returns a copy of xs with the worst <paramref name="dropCount"/> samples
        /// removed (highest values). Used for outlier rejection before computing
        /// latency stats — transient queue contention generates high outliers, not
        /// low ones, so we only trim from the top.
        /// </summary>
        private static List<long> TrimWorst(List<long> xs, int dropCount)
        {
            if (dropCount <= 0 || xs.Count <= dropCount) return new List<long>(xs);
            var sorted = new List<long>(xs);
            sorted.Sort();
            return sorted.GetRange(0, sorted.Count - dropCount);
        }

        private static float MedianFloat(List<float> xs)
        {
            if (xs.Count == 0) return 0;
            var copy = new List<float>(xs);
            copy.Sort();
            int mid = copy.Count / 2;
            return (copy.Count % 2 == 0)
                ? (copy[mid - 1] + copy[mid]) * 0.5f
                : copy[mid];
        }

        private static float P95Float(List<float> xs)
        {
            if (xs.Count == 0) return 0;
            var copy = new List<float>(xs);
            copy.Sort();
            int idx = Mathf.Clamp((int)Math.Ceiling(copy.Count * 0.95) - 1, 0, copy.Count - 1);
            return copy[idx];
        }

        // ============================================================
        //  Misc helpers
        // ============================================================

        private static bool TryInvoke(ZNetPeer peer, string name, params object[] args)
        {
            try
            {
                peer.m_rpc.Invoke(name, args);
                return true;
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[AutoTune] RPC '{name}' invoke failed: {ex.Message}");
                return false;
            }
        }

        private static int NextSeq()
        {
            int s = _nextSeq;
            _nextSeq = unchecked(_nextSeq + 1);
            if (_nextSeq <= 0) _nextSeq = 1;
            return s;
        }

        private static string MakeHwHash(int cores, int ramMb, string gpu)
        {
            // Cheap stable hash — we only care about "did the box change", not crypto strength.
            unchecked
            {
                int h = 17;
                h = h * 31 + cores;
                h = h * 31 + ramMb;
                h = h * 31 + (gpu ?? "").GetHashCode();
                return h.ToString("X8");
            }
        }

        private static string MakeServerKey(ZNetPeer serverPeer)
        {
            try
            {
                if (serverPeer != null && serverPeer.m_socket != null)
                {
                    string ep = serverPeer.m_socket.GetEndPointString();
                    if (!string.IsNullOrEmpty(ep)) return ep;
                }
            }
            catch { /* fallthrough to fallback */ }

            // Fallback so something always saves — should rarely hit this branch.
            return "unknown-server";
        }
    }

    /// <summary>
    /// Harmony-bound hooks that wire AutoTuneProbe lifecycle into ZNet.
    /// Kept in a small dedicated class so the conditional Harmony.PatchAll target list
    /// in Ascend.cs stays readable.
    /// </summary>
    [HarmonyPatch]
    public static class AutoTuneProbeHooks
    {
        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        [HarmonyPostfix]
        public static void ZNet_OnNewConnection_Postfix(ZNetPeer peer)
        {
            AutoTuneProbe.OnPeerConnected(peer);
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
        [HarmonyPostfix]
        public static void ZNet_Disconnect_Postfix(ZNetPeer peer)
        {
            // Client-side: clear local probe state regardless of which peer disconnected
            // (on a client there's only ever the one server peer).
            AutoTuneProbe.OnPeerDisconnected();

            // Server-side: drop per-peer tier-report tracking so a reconnecting peer
            // is treated as fresh (its new session needs a new tier report before
            // dependent pushes fire).
            try { ServerAutoTune.ClearPeerReportedTier(peer?.m_uid ?? 0L); }
            catch { /* server-side bookkeeping is best-effort */ }
        }

        // Main-menu logout / world teardown path. ZNet.Shutdown() iterates m_peers and
        // calls ZNetPeer.Dispose() on each, but does NOT route through ZNet.Disconnect(peer)
        // — so the Disconnect postfix above never fires on a normal logout. Without this
        // additional hook the rolling monitor coroutine keeps running on the surviving
        // FiresGhettoNetworkMod MonoBehaviour, sending pings via a disposed ZRpc, and we
        // get a steady stream of "Rolling re-probe aborted (raw=[2000,2000]); leaving
        // tier at Low" log spam from the character-select screen onward.
        //
        // ZNetPeer.Dispose disposes the socket + rpc but does NOT null serverPeer.m_rpc,
        // so the coroutine's own "serverPeer.m_rpc == null" self-check doesn't catch this
        // either — has to be torn down externally.
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        [HarmonyPostfix]
        public static void ZNet_Shutdown_Postfix()
        {
            AutoTuneProbe.OnPeerDisconnected();
        }

        // OnDestroy is the absolute backstop — fires when the ZNet GameObject is destroyed
        // (scene transition out of the world, mod reload, application quit). Some logout
        // paths (e.g. error-driven disconnects, certain modded shutdowns) may skip
        // Shutdown but every path must eventually destroy ZNet. Idempotent with the
        // Shutdown hook above (OnPeerDisconnected just re-sets already-set flags).
        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        [HarmonyPostfix]
        public static void ZNet_OnDestroy_Postfix()
        {
            AutoTuneProbe.OnPeerDisconnected();
        }
    }
}
