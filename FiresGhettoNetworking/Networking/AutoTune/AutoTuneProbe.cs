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
        public const string RpcPing        = "FiresGhetto.AutoTune.Ping";
        public const string RpcPong        = "FiresGhetto.AutoTune.Pong";
        public const string RpcBandwidthRequest      = "FiresGhetto.AutoTune.BwReq";
        public const string RpcBandwidthResponse     = "FiresGhetto.AutoTune.BwResp";
        public const string RpcTierReport = "FiresGhetto.AutoTune.TierReport";

        private const int BwPayloadMinBytes = 1024;
        private const int BwPayloadMaxBytes = 256 * 1024;
        private const int DefaultBwPayloadBytes = 128 * 1024;
        private const int PayloadPatternMultiplier = 1103515245;
        private const int PayloadPatternIncrement = 12345;
        private const int PayloadPatternShift = 8;

        private const float DefaultPlayerArrivalTimeoutSeconds = 180f;
        private const float DefaultPingTimeoutSeconds = 5f;
        private const float FrameSampleSeconds = 5f;
        private const int BandwidthSampleCount = 3;
        private const float BandwidthTimeoutFactor = 1.5f;
        private const float MinBandwidthTimeoutSeconds = 2f;

        private const int HighTierCores = 8;
        private const int HighTierRamMb = 16 * 1024;
        private const int MediumTierCores = 4;
        private const int MediumTierRamMb = 8 * 1024;

        private const int HighTierMedianPingMs = 60;
        private const int HighTierP95PingMs = 100;
        private const int HighTierJitterMs = 20;
        private const int MediumTierMedianPingMs = 120;
        private const int MediumTierP95PingMs = 200;
        private const int MediumTierJitterMs = 50;

        private const float HighTierMedianFrameMs = 16.7f;
        private const float HighTierP95FrameMs = 25f;
        private const float MediumTierMedianFrameMs = 33.4f;
        private const float MediumTierP95FrameMs = 50f;

        private const float HighTierKbPerSec = 500f;
        private const float MediumTierKbPerSec = 200f;

        private const int PromotionObservations = 2;
        private const int DemotionObservations = 3;

        private const int FrameSampleCapacity = 300;
        private const float BandwidthSampleGapSeconds = 0.5f;
        private const float LatencyWarmupSettleSeconds = 0.3f;
        private const float LatencyPingGapSeconds = 0.2f;

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
        private static Tier        _latencyTier;
        private static int         _latencyMedianMs;
        private static int         _latencyP95Ms;
        private static int         _latencyJitterMs;
        private static List<long>  _latencyRawSamplesMs = new List<long>();
        private static bool        _latencyProbeAborted;

        // Cached at initial probe so rolling monitor doesn't have to re-sample fps/cpu.
        // Hardware doesn't change mid-session and FPS is stable enough on the same scene.
        private static Tier _sessionMachineTier = Tier.Medium;
        private static string _sessionServerKey = string.Empty;
        private static string _sessionHwHash = string.Empty;

        // Rolling monitor state — last N tier observations
        private static readonly Queue<Tier> _rollingTiers = new Queue<Tier>();
        private static bool _rollingMonitorActive;

        // Pending tier change awaiting its consecutive-observation streak.
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
                peer.m_rpc.Register<int>(RpcPing, OnRpcPing);
                peer.m_rpc.Register<int, int>(RpcBandwidthRequest, OnRpcBwReq);

                // Client-side handlers
                peer.m_rpc.Register<int>(RpcPong, OnRpcPong);
                peer.m_rpc.Register<int, ZPackage>(RpcBandwidthResponse, OnRpcBwResp);

                // Server-side: receive client tier reports (handled by ServerAutoTune)
                peer.m_rpc.Register<int, int>(RpcTierReport, ServerAutoTune.OnTierReport);
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
            try { rpc.Invoke(RpcPong, seq); }
            catch (Exception ex) { LoggerOptions.LogWarning($"[AutoTune] Ping echo failed: {ex.Message}"); }
        }

        private static void OnRpcBwReq(ZRpc rpc, int seq, int requestedBytes)
        {
            // Anti-abuse: never allocate more than the cap, whatever the client asked for.
            int safeBytes = Mathf.Clamp(requestedBytes, BwPayloadMinBytes, BwPayloadMaxBytes);

            try
            {
                ZPackage pkg = new ZPackage();
                // Varied pattern, so the compression layer cannot squash the sample and flatter the link.
                byte[] payload = new byte[safeBytes];
                for (int i = 0; i < safeBytes; i++)
                {
                    payload[i] = (byte)((i * PayloadPatternMultiplier + PayloadPatternIncrement) >> PayloadPatternShift);
                }
                pkg.Write(payload);

                rpc.Invoke(RpcBandwidthResponse, seq, pkg);
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

                CapeCrashDiagnostics.Log($"AutoTune bandwidth response {seq}: {actualBytes} bytes after {sw.ElapsedMilliseconds} ms");
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
            // Heavy modpacks push the real spawn a minute or more past connect, and probing before it
            // measures world-init traffic rather than the link. The timeout is only a safety valve.
            float arrivalTimeout = AutoTuneConfig.PlayerArrivalTimeoutSeconds?.Value ?? DefaultPlayerArrivalTimeoutSeconds;
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
                    CapeCrashDiagnostics.Log($"AutoTune using cached tier {cached.Tier}");
                    ApplyClientTier(cached.Tier);
                    SendTierReport(serverPeer, cached.Tier, cached.PingMedianMs);
                    _probeCompletedThisSession = true;
                    _probeRunning = false;
                    VAGhettoLoadSummary.EmitAutoTune("cached", cached.Tier.ToString());

                    // Rolling monitor still kicks in on cached hits — the cached tier
                    // could be stale (network conditions changed since last session)
                    // and the monitor will revalidate on its first re-probe interval.
                    // Use cpuTier as a conservative machine ceiling; we skip the fps
                    // sample on cache hit to keep the path quick.
                    _sessionServerKey   = serverKey;
                    _sessionHwHash      = hwHash;
                    _sessionMachineTier = cpuTier;
                    if (FiresGhettoNetworkMod.Instance != null)
                        _rollingMonitorHandle = FiresGhettoNetworkMod.Instance.StartCoroutine(RunRollingMonitor(serverPeer));

                    yield break;
                }
            }

            LoggerOptions.LogInfo($"[AutoTune] Starting probe — cores={cores}, RAM={ramMb}MB, GPU={gpu}");
            CapeCrashDiagnostics.Log("AutoTune probe starting");

            // Stash session-scoped state so the rolling monitor can reuse it
            // without re-detecting hardware or re-resolving the cache key.
            _sessionServerKey = serverKey;
            _sessionHwHash    = hwHash;

            // ---- 4. Latency probe ----
            yield return DoLatencyProbe(serverPeer);

            if (_latencyProbeAborted)
            {
                LoggerOptions.LogInfo($"[AutoTune] Initial latency probe aborted (raw=[{string.Join(",", _latencyRawSamplesMs)}]) — defaulting to LOW tier, skipping bandwidth probe.");
                Finalize(serverKey, hwHash, Tier.Low, _latencyMedianMs, serverPeer);
                yield break;
            }

            int  pingMedian = _latencyMedianMs;
            Tier netTier    = _latencyTier;

            LoggerOptions.LogInfo($"[AutoTune] Latency: raw=[{string.Join(",", _latencyRawSamplesMs)}] trimmed→ median={pingMedian}ms p95={_latencyP95Ms}ms iqrJitter={_latencyJitterMs}ms → {netTier}");

            // ---- 5. Frame-time sample (5s passive) ----
            Tier fpsTier = Tier.Medium;
            {
                List<float> frameMs = new List<float>(FrameSampleCapacity);
                float collected = 0f;
                while (collected < FrameSampleSeconds)
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
                    LoggerOptions.LogInfo($"[AutoTune] Frame time: median={fpsMedian:0.0}ms p95={fpsP95:0.0}ms → {fpsTier}");
                }
            }

            // ---- 6. Bandwidth probe (gated) ----
            // Only run if latency tier is MED or HIGH. LOW already proved fragile.
            // Bandwidth confirms rather than decides: the sample is bounded by the server's send cap, so
            // it can only pull the result one step below min(machine, latency).
            float bwKbPerSec = -1f;
            bool bwProbeCompleted = false;

            if (netTier != Tier.Low)
            {
                // Peak of several samples: one alone can land while the server is busy with another peer.
                int payloadBytes = AutoTuneConfig.ProbeBandwidthPayloadBytes?.Value ?? DefaultBwPayloadBytes;
                float bwTimeout = Mathf.Max(MinBandwidthTimeoutSeconds, (AutoTuneConfig.ProbePingTimeoutSeconds?.Value ?? DefaultPingTimeoutSeconds) * BandwidthTimeoutFactor);
                int sampleCount = BandwidthSampleCount;
                List<float> bwSamples = new List<float>(sampleCount);
                int timeouts = 0;

                for (int s = 0; s < sampleCount; s++)
                {
                    if (serverPeer == null || serverPeer.m_rpc == null) break;

                    int seq = NextSeq();
                    Stopwatch sw = Stopwatch.StartNew();
                    _bwInflight[seq] = sw;
                    _bwRequested[seq] = payloadBytes;

                    CapeCrashDiagnostics.Log($"AutoTune bandwidth sample {s + 1}/{sampleCount}: requesting {payloadBytes} bytes");
                    bool sent = TryInvoke(serverPeer, RpcBandwidthRequest, seq, payloadBytes);
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
                    if (s < sampleCount - 1) yield return new WaitForSeconds(BandwidthSampleGapSeconds);
                }

                if (bwSamples.Count > 0)
                {
                    // Peak (max) wins — this is the LEAST contended sample we got, and
                    // therefore the closest estimate of the link's actual capacity.
                    float peak = bwSamples[0];
                    foreach (var v in bwSamples) if (v > peak) peak = v;
                    bwKbPerSec = peak;
                    bwProbeCompleted = true;
                    LoggerOptions.LogInfo($"[AutoTune] Bandwidth: samples=[{string.Join(",", bwSamples.ConvertAll(v => v.ToString("0")))}] KB/s, peak={bwKbPerSec:0} KB/s, timeouts={timeouts}");
                }
                else if (timeouts > 0)
                {
                    LoggerOptions.LogInfo($"[AutoTune] Bandwidth probe: all {timeouts} samples timed out — treated as bandwidth-bad signal.");
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

            LoggerOptions.LogInfo($"[AutoTune] Final tier: machine={machineTier} (cpu={cpuTier} fps={fpsTier}) latency={latencyTier} bw={(bwProbeCompleted ? bwKbPerSec.ToString("0") + "KB/s" : "n/a")} → {finalTier}");

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

        // Clamped decrement through the enum values rather than assuming the int layout.
        private static Tier StepTowardLow(Tier tier, int steps)
        {
            int target = (int)tier - System.Math.Max(0, steps);
            int floor = (int)Tier.Low;
            return (Tier)System.Math.Max(floor, target);
        }

        private static Tier ScoreBwTier(float kbPerSec)
        {
            if (kbPerSec >= HighTierKbPerSec) return Tier.High;
            if (kbPerSec >= MediumTierKbPerSec) return Tier.Medium;
            return Tier.Low;
        }

        // ============================================================
        //  Latency probe (reusable for initial + rolling re-probes)
        // ============================================================

        /// <summary>
        /// Warmup ping plus N timed pings into the _latency* fields, shared by the initial probe and the
        /// rolling monitor. The probe shares Valheim's socket, so samples stuck behind a real payload skew
        /// jitter and p95 even on a fast link: the warmup clears slow-start and drains the outbound queue,
        /// the worst sample is trimmed, jitter is the IQR rather than the range, and a LOW abort needs two
        /// bad pings instead of one straggler.
        /// </summary>
        private static IEnumerator DoLatencyProbe(ZNetPeer serverPeer)
        {
            int   pingCount      = AutoTuneConfig.ProbePingCount?.Value ?? 10;
            float pingTimeoutSec = AutoTuneConfig.ProbePingTimeoutSeconds?.Value ?? 5f;
            int   pingAbortMs    = AutoTuneConfig.ProbePingAbortMs?.Value ?? 2000;

            _latencyProbeAborted = false;
            _latencyRawSamplesMs   = new List<long>(pingCount);

            // Warmup ping — discarded result, just primes the path.
            if (serverPeer != null && serverPeer.m_rpc != null)
            {
                int warmSeq = NextSeq();
                _pingInflight[warmSeq] = Stopwatch.StartNew();
                if (TryInvoke(serverPeer, RpcPing, warmSeq))
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
                yield return new WaitForSeconds(LatencyWarmupSettleSeconds);
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

                if (!TryInvoke(serverPeer, RpcPing, seq))
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
                _latencyRawSamplesMs.Add(sample);

                if (sample >= pingAbortMs) badSamples++;

                if (badSamples >= 2)
                {
                    _latencyProbeAborted = true;
                    _latencyTier    = Tier.Low;
                    _latencyMedianMs = (int)ComputeMedian(_latencyRawSamplesMs);
                    _latencyP95Ms    = pingAbortMs;
                    _latencyJitterMs = pingAbortMs;
                    yield break;
                }

                yield return new WaitForSeconds(LatencyPingGapSeconds);
            }

            if (_latencyRawSamplesMs.Count == 0)
            {
                _latencyProbeAborted  = true;
                _latencyTier     = Tier.Low;
                _latencyMedianMs = 0;
                _latencyP95Ms    = 0;
                _latencyJitterMs = 0;
                yield break;
            }

            // Outlier trimming — drop worst sample (10% @ 10 pings).
            List<long> trimmed = TrimWorst(_latencyRawSamplesMs, dropCount: _latencyRawSamplesMs.Count >= 5 ? 1 : 0);

            _latencyMedianMs = (int)ComputeMedian(trimmed);
            _latencyP95Ms    = (int)ComputeP95(trimmed);
            _latencyJitterMs = ComputeIqrJitter(trimmed);
            _latencyTier     = ScoreNetTier(_latencyMedianMs, _latencyP95Ms, _latencyJitterMs);
        }

        // ============================================================
        //  Rolling monitor (continuous re-probe over the session)
        // ============================================================

        /// <summary>
        /// Re-probes latency on an interval once the initial probe settles, taking the mode of a rolling
        /// buffer and only applying it after PromotionObservations (or DemotionObservations) consecutive
        /// agreeing cycles. Mode alone oscillates when latency sits on a tier threshold; demotion is stricter
        /// because losing performance to one bad probe costs more than briefly punching above weight.
        /// Latency only, since FPS is captured once and a re-run bandwidth stage hits the same bottleneck.
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
                // we read _latencyProbeAborted (which is set to true on legitimate aborts too).
                if (_probeAborted || ZNet.instance == null) { _rollingMonitorActive = false; yield break; }

                if (_latencyProbeAborted)
                {
                    LoggerOptions.LogMessage($"[AutoTune] Rolling re-probe aborted (raw=[{string.Join(",", _latencyRawSamplesMs)}]); leaving tier at {AutoTuneState.ClientTier}.");
                    continue;
                }

                // Combine fresh latency tier with stable machine tier using existing rule
                Tier observedFinal = ComputeFinalTier(_sessionMachineTier, _latencyTier, /*bw*/ -1f, /*bwProbeCompleted*/ false);

                _rollingTiers.Enqueue(observedFinal);
                while (_rollingTiers.Count > windowSize) _rollingTiers.Dequeue();

                Tier consensus = ComputeMode(_rollingTiers);
                Tier currentApplied = AutoTuneState.ClientTier;

                if (consensus != currentApplied)
                {
                    // Demotion needs more consecutive observations than promotion: giving up performance
                    // to one bad probe costs more than briefly punching above weight.
                    if (_pendingRollingTier == consensus)
                    {
                        _pendingRollingObservations++;
                    }
                    else
                    {
                        _pendingRollingTier = consensus;
                        _pendingRollingObservations = 1;
                    }

                    int required = (int)consensus > (int)currentApplied ? PromotionObservations : DemotionObservations;

                    if (_pendingRollingObservations >= required)
                    {
                        LoggerOptions.LogMessage($"[AutoTune] Rolling tier change: {currentApplied} → {consensus} ({_pendingRollingObservations} consecutive obs; this probe: latency={_latencyTier} median={_latencyMedianMs}ms; window=[{string.Join(",", _rollingTiers)}])");
                        AutoTuneState.SetClient(consensus, _latencyMedianMs);
                        ApplyClientTier(consensus);
                        try { AutoTuneCache.Save(_sessionServerKey, consensus, _latencyMedianMs, _sessionHwHash); }
                        catch (Exception ex) { LoggerOptions.LogWarning($"[AutoTune] Cache save skipped ({ex.GetType().Name}: {ex.Message})."); }
                        SendTierReport(serverPeer, consensus, _latencyMedianMs);
                        // Reset streak — applied, no longer pending.
                        _pendingRollingObservations = 0;
                    }
                    else
                    {
                        LoggerOptions.LogInfo($"[AutoTune] Rolling tier change pending: {currentApplied} → {consensus} (obs {_pendingRollingObservations}/{required}; this probe: latency={_latencyTier} median={_latencyMedianMs}ms; window=[{string.Join(",", _rollingTiers)}])");
                    }
                }
                else
                {
                    // Consensus matches applied — any pending change in flight is invalidated
                    // (the boundary jitter swung back our way). Reset streak to currentApplied.
                    _pendingRollingTier = currentApplied;
                    _pendingRollingObservations = 0;
                    LoggerOptions.LogInfo($"[AutoTune] Rolling re-probe stable at {consensus} (this probe: latency={_latencyTier} median={_latencyMedianMs}ms iqrJitter={_latencyJitterMs}ms; window=[{string.Join(",", _rollingTiers)}])");
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
            VAGhettoLoadSummary.EmitAutoTune("probed", tier.ToString());
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
            CapeCrashDiagnostics.Log($"AutoTune applying client tier {tier}: send rates");
            try { NetworkingRatesGroup.ApplySendRates(); }
            catch (Exception ex) { LoggerOptions.LogWarning($"[AutoTune] ApplySendRates failed: {ex.Message}"); }

            // Steam send buffer (per-connection outbound) — main lever for k_EResultLimitExceeded
            CapeCrashDiagnostics.Log($"AutoTune applying client tier {tier}: send buffer");
            try { NetworkingRatesGroup.ApplySendBufferSize(); }
            catch (Exception ex) { LoggerOptions.LogWarning($"[AutoTune] ApplySendBufferSize failed: {ex.Message}"); }

            // Steam recv buffer — set via the same reflection helper (no-op on Valheim's older Steamworks build)
            CapeCrashDiagnostics.Log($"AutoTune applying client tier {tier}: recv buffer {EffectiveConfig.SteamRecvBufferBytes()} bytes");
            try { NetworkingRatesGroup.ApplyRecvBufferSize(); }
            catch (Exception ex) { LoggerOptions.LogWarning($"[AutoTune] ApplyRecvBufferSize failed: {ex.Message}"); }

            // Steam per-message ceiling — must rise alongside the recv buffer or
            // large reliable messages still hit Steam's 512 KB default cap and get
            // rejected with "Reliable message size too large".
            CapeCrashDiagnostics.Log($"AutoTune applying client tier {tier}: recv max message {EffectiveConfig.SteamRecvMaxMessageBytes()} bytes");
            try { NetworkingRatesGroup.ApplyRecvMaxMessageSize(); }
            catch (Exception ex) { LoggerOptions.LogWarning($"[AutoTune] ApplyRecvMaxMessageSize failed: {ex.Message}"); }

            CapeCrashDiagnostics.Log($"AutoTune client tier {tier} applied");
            LoggerOptions.LogInfo($"[AutoTune] Applied client tier {tier}");
        }

        private static void SendTierReport(ZNetPeer serverPeer, Tier tier, int pingMedianMs)
        {
            if (serverPeer == null || serverPeer.m_rpc == null) return;
            try
            {
                serverPeer.m_rpc.Invoke(RpcTierReport, (int)tier, pingMedianMs);
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
            if (cores >= HighTierCores && ramMb >= HighTierRamMb) return Tier.High;
            if (cores >= MediumTierCores && ramMb >= MediumTierRamMb) return Tier.Medium;
            return Tier.Low;
        }

        private static Tier ScoreNetTier(int medianMs, int p95Ms, int jitterMs)
        {
            if (medianMs <= HighTierMedianPingMs && p95Ms <= HighTierP95PingMs && jitterMs <= HighTierJitterMs) return Tier.High;
            if (medianMs <= MediumTierMedianPingMs && p95Ms <= MediumTierP95PingMs && jitterMs <= MediumTierJitterMs) return Tier.Medium;
            return Tier.Low;
        }

        private static Tier ScoreFpsTier(float medianMs, float p95Ms)
        {
            if (medianMs <= HighTierMedianFrameMs && p95Ms <= HighTierP95FrameMs) return Tier.High;
            if (medianMs <= MediumTierMedianFrameMs && p95Ms <= MediumTierP95FrameMs) return Tier.Medium;
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
            int current = _nextSeq;
            _nextSeq = unchecked(_nextSeq + 1);
            if (_nextSeq <= 0) _nextSeq = 1;
            return current;
        }

        private static string MakeHwHash(int cores, int ramMb, string gpu)
        {
            // Cheap stable hash — we only care about "did the box change", not crypto strength.
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + cores;
                hash = hash * 31 + ramMb;
                hash = hash * 31 + (gpu ?? "").GetHashCode();
                return hash.ToString("X8");
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
