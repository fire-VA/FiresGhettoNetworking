using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace FiresGhettoNetworkMod.AutoTune
{
    /// <summary>
    /// Server-side counterpart to AutoTuneProbe. Two responsibilities:
    ///   1. Score the host machine's CPU/RAM at startup, optionally APPLY the tier
    ///      preset (if Enable Server Auto-Tune is on), and/or LOG a suggestion
    ///      (if Log Server Suggestions is on).
    ///   2. Receive tier reports from connected clients and periodically log a
    ///      summary so the admin can see whether their settings actually match
    ///      the audience joining the server.
    ///
    /// Never auto-applies anything based on client reports — those are admin decisions.
    /// </summary>
    public static class ServerAutoTune
    {
        private const int ClientReportWindow = 20;
        private static readonly Queue<ClientReport> _clientReports = new Queue<ClientReport>();
        private static DateTime _lastSummaryLogged = DateTime.MinValue;
        private static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(15);

        // Per-peer "AutoTune probe has completed for this peer" flag, populated when
        // RPC_TIER_REPORT arrives and cleared on disconnect. Other mods can poll this
        // (via reflection — soft dependency) to gate their own heavy server→client
        // pushes until the AutoTune probe is done. See FiresRPGmaker's
        // GhettoNetworkingCoexistence helper for the consuming side.
        private static readonly HashSet<long> _peersWithTierReported = new HashSet<long>();

        private struct ClientReport
        {
            public Tier Tier;
            public int  PingMedianMs;
            public DateTime ReceivedUtc;
        }

        /// <summary>
        /// Cross-mod query: has this peer's client-side AutoTune probe finished and
        /// sent its tier report? Other mods that wrap heavy server→client pushes
        /// (e.g. config-file syncs, ZDO-restore scans) can poll this in a coroutine
        /// to wait until the probe is no longer competing for bandwidth/CPU. Returns
        /// false if the client doesn't have AutoTune enabled, the probe is still
        /// running, the probe aborted, or the peer has since disconnected.
        /// </summary>
        public static bool HasPeerReportedTier(long peerUid)
        {
            return peerUid != 0L && _peersWithTierReported.Contains(peerUid);
        }

        /// <summary>
        /// Drop tracking for a peer that has disconnected. Called from
        /// <see cref="AutoTuneProbeHooks.ZNet_Disconnect_Postfix"/> so a reconnecting
        /// peer's new session starts from a clean slate.
        /// </summary>
        public static void ClearPeerReportedTier(long peerUid)
        {
            if (peerUid == 0L) return;
            _peersWithTierReported.Remove(peerUid);
        }

        /// <summary>
        /// Called once on dedicated server startup, after configs are bound.
        /// </summary>
        public static void InitServerSide()
        {
            if (!ServerClientUtils.IsDedicatedServerDetected) return;

            int cores = Mathf.Max(1, SystemInfo.processorCount);
            int ramMb = Mathf.Max(0, SystemInfo.systemMemorySize);
            Tier tier = ScoreServerTier(cores, ramMb);

            bool autoApply = AutoTuneConfig.EnableServerAutoTune?.Value ?? false;
            bool logHint   = AutoTuneConfig.LogServerSuggestions?.Value  ?? true;

            if (autoApply)
            {
                AutoTuneState.SetServer(tier);
                LoggerOptions.LogMessage($"[AutoTune] SERVER auto-tune APPLIED: tier={tier} (cores={cores}, RAM={ramMb}MB).");
            }
            else if (logHint)
            {
                LogSelfTuneSuggestion(tier, cores, ramMb);
            }
        }

        // A rented/containerised host (zap-hosting, Pterodactyl, etc.) usually
        // exposes the PHYSICAL machine's /proc to the container, so SystemInfo
        // reports the host's full core count and RAM — not the container's
        // actual allocation. A 64-core / ~1 TB report is a dedicated game-server
        // allocation essentially never; it's the host bleeding through. Trusting
        // it makes the server self-apply HIGH and overcommit queue/buffers past
        // what the container can deliver. When the numbers are implausibly large
        // for a real game-server slice, treat the detection as unreliable and cap
        // the auto-applied tier at Medium.
        private const int ImplausibleCoreCount = 32;
        private const int ImplausibleRamMb = 128 * 1024;

        private static Tier ScoreServerTier(int cores, int ramMb)
        {
            if (cores > ImplausibleCoreCount || ramMb > ImplausibleRamMb)
            {
                LoggerOptions.LogWarning(
                    $"[AutoTune] Detected {cores} cores / {ramMb}MB RAM — implausibly large for a dedicated "
                    + "game-server allocation, almost certainly a container reporting the HOST's specs. "
                    + "Capping auto-tune tier at Medium to avoid overcommitting queue/buffers beyond what "
                    + "the container can actually deliver. Set 'Enable Server Auto-Tune' = false and tune "
                    + "manually if you know your real allocation.");
                return Tier.Medium;
            }
            if (cores >= 8 && ramMb >= 16 * 1024) return Tier.High;
            if (cores >= 4 && ramMb >= 8  * 1024) return Tier.Medium;
            return Tier.Low;
        }

        private static void LogSelfTuneSuggestion(Tier tier, int cores, int ramMb)
        {
            TierPreset preset = TierPresets.For(tier);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"[AutoTune] Server self-tune: {tier} (cores={cores}, RAM={ramMb}MB). 'Enable Server Auto-Tune' is OFF — settings unchanged. Suggested values:");
            AppendDelta(sb, "Update Rate",            FiresGhettoNetworkMod.ConfigUpdateRate.Value.ToString(),     preset.UpdateRate.ToString());
            AppendDelta(sb, "Queue Size",             FiresGhettoNetworkMod.ConfigQueueSize.Value.ToString(),      preset.QueueSize.ToString());
            AppendDelta(sb, "ZDO Throttle Distance",  $"{FiresGhettoNetworkMod.ConfigZDOThrottleDistance.Value:0}m", $"{preset.ZDOThrottleDistance:0}m");
            AppendDelta(sb, "AI LOD Near Distance",   $"{FiresGhettoNetworkMod.ConfigAILODNearDistance.Value:0}m",   $"{preset.AILODNearDistance:0}m");
            AppendDelta(sb, "AI LOD Far Distance",    $"{FiresGhettoNetworkMod.ConfigAILODFarDistance.Value:0}m",    $"{preset.AILODFarDistance:0}m");
            AppendDelta(sb, "AI LOD Throttle Factor", $"{FiresGhettoNetworkMod.ConfigAILODThrottleFactor.Value:0.00}", $"{preset.AILODThrottleFactor:0.00}");
            AppendDelta(sb, "RPC AoI Radius",         $"{FiresGhettoNetworkMod.ConfigRpcAoIRadius.Value:0}m",       $"{preset.RpcAoIRadius:0}m");
            AppendDelta(sb, "Extended Zone Radius",   FiresGhettoNetworkMod.ConfigExtendedZoneRadius.Value.ToString(), preset.ExtendedZoneRadius.ToString());
            sb.Append("To apply: set 'Enable Server Auto-Tune' = true OR copy individual values into the config above.");
            LoggerOptions.LogMessage(sb.ToString());
        }

        private static void AppendDelta(StringBuilder sb, string label, string current, string suggested)
        {
            if (current == suggested)
            {
                sb.AppendLine($"  {label,-26} {current}  (already matches)");
            }
            else
            {
                sb.AppendLine($"  {label,-26} {current}  →  {suggested}");
            }
        }

        // ============================================================
        //  Client tier-report ingestion
        // ============================================================

        /// <summary>
        /// RPC handler called by AutoTuneProbe.SendTierReport on each connected client.
        /// Server-side only — on a client this fires for nothing because clients never
        /// receive TierReport.
        /// </summary>
        public static void OnTierReport(ZRpc rpc, int tierInt, int pingMedianMs)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            Tier reported;
            if (Enum.IsDefined(typeof(Tier), tierInt))
                reported = (Tier)tierInt;
            else
                reported = Tier.Low;

            _clientReports.Enqueue(new ClientReport
            {
                Tier         = reported,
                PingMedianMs = pingMedianMs,
                ReceivedUtc  = DateTime.UtcNow,
            });
            while (_clientReports.Count > ClientReportWindow) _clientReports.Dequeue();

            // Mark this peer's AutoTune as complete so other mods can stop waiting
            // and push their heavy server→client data. Resolved from the ZRpc that
            // carried the report — works on dedicated server and listen-host alike.
            long peerUid = 0L;
            try { peerUid = ZNet.instance.GetPeer(rpc)?.m_uid ?? 0L; }
            catch { /* peer lookup failed — fall through, consumers will timeout */ }
            if (peerUid != 0L) _peersWithTierReported.Add(peerUid);

            LoggerOptions.LogInfo($"[AutoTune] Client reported tier={reported} ping={pingMedianMs}ms peer={peerUid} (rolling window: {_clientReports.Count}/{ClientReportWindow})");

            // Log a summary on a cadence — too chatty otherwise on a busy server
            if (DateTime.UtcNow - _lastSummaryLogged >= SummaryInterval)
            {
                LogClientSummary();
                _lastSummaryLogged = DateTime.UtcNow;
            }
        }

        private static void LogClientSummary()
        {
            if (!(AutoTuneConfig.LogServerSuggestions?.Value ?? true)) return;
            if (_clientReports.Count == 0) return;

            int low = 0, med = 0, high = 0;
            long totalPing = 0;
            foreach (var r in _clientReports)
            {
                switch (r.Tier)
                {
                    case Tier.Low:    low++;  break;
                    case Tier.Medium: med++;  break;
                    case Tier.High:   high++; break;
                }
                totalPing += r.PingMedianMs;
            }

            Tier median = MedianTier(low, med, high);
            int avgPing = (int)(totalPing / Math.Max(1, _clientReports.Count));

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"[AutoTune] Client tier distribution (last {_clientReports.Count}): HIGH={high} MED={med} LOW={low}, avgPing={avgPing}ms");
            sb.AppendLine($"  Median client tier: {median}");

            if (!(AutoTuneConfig.EnableServerAutoTune?.Value ?? false))
            {
                TierPreset preset = TierPresets.For(median);
                sb.AppendLine("  If you'd like the server tuned for the median client, suggested values:");
                AppendDelta(sb, "Update Rate",            FiresGhettoNetworkMod.ConfigUpdateRate.Value.ToString(),     preset.UpdateRate.ToString());
                AppendDelta(sb, "Queue Size",             FiresGhettoNetworkMod.ConfigQueueSize.Value.ToString(),      preset.QueueSize.ToString());
                AppendDelta(sb, "ZDO Throttle Distance",  $"{FiresGhettoNetworkMod.ConfigZDOThrottleDistance.Value:0}m", $"{preset.ZDOThrottleDistance:0}m");
                AppendDelta(sb, "AI LOD Far Distance",    $"{FiresGhettoNetworkMod.ConfigAILODFarDistance.Value:0}m",    $"{preset.AILODFarDistance:0}m");
                AppendDelta(sb, "RPC AoI Radius",         $"{FiresGhettoNetworkMod.ConfigRpcAoIRadius.Value:0}m",       $"{preset.RpcAoIRadius:0}m");
                AppendDelta(sb, "Extended Zone Radius",   FiresGhettoNetworkMod.ConfigExtendedZoneRadius.Value.ToString(), preset.ExtendedZoneRadius.ToString());
            }

            LoggerOptions.LogMessage(sb.ToString());
        }

        private static Tier MedianTier(int low, int med, int high)
        {
            int total = low + med + high;
            if (total == 0) return Tier.Medium;
            int half = total / 2;
            int running = 0;
            running += low;    if (running > half) return Tier.Low;
            running += med;    if (running > half) return Tier.Medium;
            return Tier.High;
        }
    }
}
