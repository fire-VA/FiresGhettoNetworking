using System.Text;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    public static class ServerStatusDiagnostics
    {
        private const float IntervalClampMinSec = 10f;
        private const float IntervalClampMaxSec = 3600f;
        private const float DefaultIntervalSec = 60f;
        private const float DefaultAILODNearMeters = 100f;
        private const float DefaultAILODFarMeters = 300f;

        private static int s_lastSeenUpdateCount;

        public static int s_uai_examined;
        public static int s_uai_bail_nviewNull;
        public static int s_uai_bail_nviewInvalid;
        public static int s_uai_bail_zdoNull;
        public static int s_uai_bail_charNull;
        public static int s_uai_passThrough_isOwner;
        public static int s_uai_passThrough_notOwner;

        public static int s_mai_examined;

        public static int s_co_calls;
        public static int s_co_nullReturns;

        public static int s_cdo_passes;
        public static int s_cdo_bail_noPeers;
        public static int s_cdo_bail_nre;
        public static long s_cdo_nearTotal;
        public static long s_cdo_distantTotal;
        public static long s_cdo_distinctNearTotal;
        public static long s_cdo_distinctDistantTotal;
        public static int s_cdo_areaReadyTrue;
        public static int s_cdo_areaReadyFalse;
        public static int s_cdo_orphansPruned;

        public static int s_iaal_calls;
        public static int s_iaal_resultTrue;
        public static int s_iaal_resultFalse;
        public static int s_iaal_minMissingZones = int.MaxValue;
        public static int s_iaal_maxMissingZones;
        public static int s_iaal_lastMissingZoneX;
        public static int s_iaal_lastMissingZoneY;

        public static int s_so_passes;
        public static int s_so_zdosProcessed;
        public static int s_so_transfersToServer;
        public static int s_so_releases;

        public static int s_ailod_examined;
        public static int s_ailod_playerOrTamed;
        public static int s_ailod_decidedNear;
        public static int s_ailod_decidedMidBand;
        public static int s_ailod_decidedFarRan;
        public static int s_ailod_decidedFarSkipped;
        public static int s_ailod_peersLastSeen;
        public static float s_ailod_minNearestDist = float.MaxValue;
        public static float s_ailod_maxNearestDist;

        private static float s_nextEmitTime;
        private static float s_lastEmitTime;

        public static void TryEmit()
        {
            if (ZNet.instance == null || !ZNet.instance.IsDedicated()) return;

            float now = Time.realtimeSinceStartup;
            float interval = Mathf.Clamp(
                FiresGhettoNetworkMod.ConfigDiagnosticIntervalSec?.Value ?? DefaultIntervalSec,
                IntervalClampMinSec, IntervalClampMaxSec);

            if (s_nextEmitTime <= 0f)
            {
                SeedFirstWindow(now, interval);
                return;
            }
            if (now < s_nextEmitTime) return;

            EmitConsolidatedLine(now, now - s_lastEmitTime);
            ResetWindowCounters();
            s_lastEmitTime = now;
            s_nextEmitTime = now + interval;
        }

        private static void SeedFirstWindow(float now, float interval)
        {
            s_lastEmitTime = now;
            s_nextEmitTime = now + interval;
        }

        private static void EmitConsolidatedLine(float now, float windowSec)
        {
            int updateCount = MonoUpdaters.UpdateCount;
            int updateDelta = updateCount - s_lastSeenUpdateCount;
            s_lastSeenUpdateCount = updateCount;

            int aiList   = BaseAI.Instances?.Count        ?? -1;
            int baseList = BaseAI.BaseAIInstances?.Count  ?? -1;
            int charList = Character.Instances?.Count     ?? -1;
            int peerCount = ZNet.instance?.GetPeers()?.Count ?? 0;
            float windowDisplay = Mathf.Round(windowSec);

            var sb = new StringBuilder(640);
            sb.Append("[ServerStatus] +").Append(windowDisplay).Append("s")
              .Append(" | tick: ").Append(updateCount).Append(" (+").Append(updateDelta).Append(")")
              .Append(" | peers: ").Append(peerCount)
              .Append(" | ai lists: ").Append(aiList).Append(" BaseAI / ").Append(baseList).Append(" BaseAIInst / ").Append(charList).Append(" Char")
              .Append(" | BaseAI.UpdateAI: ").Append(s_uai_examined).Append(" ex, owner=").Append(s_uai_passThrough_isOwner)
                  .Append(" notOwner=").Append(s_uai_passThrough_notOwner)
                  .Append(" bail(nv=").Append(s_uai_bail_nviewNull)
                  .Append(",inv=").Append(s_uai_bail_nviewInvalid)
                  .Append(",zdo=").Append(s_uai_bail_zdoNull)
                  .Append(",char=").Append(s_uai_bail_charNull).Append(")")
              .Append(" | MonsterAI.UpdateAI: ").Append(s_mai_examined).Append(" ex")
              .Append(" | CreateObject: ").Append(s_co_calls - s_co_nullReturns).Append(" ok, ").Append(s_co_nullReturns).Append(" null")
              .Append(" | CreateDestroyObjects: ").Append(FormatCreateDestroyObjectsSegment())
              .Append(" | IsActiveAreaLoaded: ").Append(FormatIsActiveAreaLoadedSegment())
              .Append(" | ServerOwnership: ").Append(s_so_passes).Append(" passes, ")
                  .Append(s_so_zdosProcessed).Append(" zdos, ")
                  .Append(s_so_transfersToServer).Append(" toServer, ")
                  .Append(s_so_releases).Append(" released");

            if (FiresGhettoNetworkMod.ConfigShowAILODInServerStatus?.Value ?? true)
                AppendAILODSegment(sb);

            LoggerOptions.LogMessage(sb.ToString());
        }

        private static void ResetWindowCounters()
        {
            s_uai_examined = 0;
            s_uai_bail_nviewNull = 0;
            s_uai_bail_nviewInvalid = 0;
            s_uai_bail_zdoNull = 0;
            s_uai_bail_charNull = 0;
            s_uai_passThrough_isOwner = 0;
            s_uai_passThrough_notOwner = 0;

            s_mai_examined = 0;

            s_co_calls = 0;
            s_co_nullReturns = 0;

            s_cdo_passes = 0;
            s_cdo_bail_noPeers = 0;
            s_cdo_bail_nre = 0;
            s_cdo_nearTotal = 0;
            s_cdo_distantTotal = 0;
            s_cdo_distinctNearTotal = 0;
            s_cdo_distinctDistantTotal = 0;
            s_cdo_areaReadyTrue = 0;
            s_cdo_areaReadyFalse = 0;
            s_cdo_orphansPruned = 0;

            s_iaal_calls = 0;
            s_iaal_resultTrue = 0;
            s_iaal_resultFalse = 0;
            s_iaal_minMissingZones = int.MaxValue;
            s_iaal_maxMissingZones = 0;
            s_iaal_lastMissingZoneX = 0;
            s_iaal_lastMissingZoneY = 0;

            s_so_passes = 0;
            s_so_zdosProcessed = 0;
            s_so_transfersToServer = 0;
            s_so_releases = 0;

            s_ailod_examined = 0;
            s_ailod_playerOrTamed = 0;
            s_ailod_decidedNear = 0;
            s_ailod_decidedMidBand = 0;
            s_ailod_decidedFarRan = 0;
            s_ailod_decidedFarSkipped = 0;
            s_ailod_peersLastSeen = 0;
            s_ailod_minNearestDist = float.MaxValue;
            s_ailod_maxNearestDist = 0f;
        }

        private static string FormatCreateDestroyObjectsSegment()
        {
            if (s_cdo_passes + s_cdo_bail_noPeers + s_cdo_bail_nre == 0) return "noCalls";
            string orphanSeg = s_cdo_orphansPruned > 0 ? $", orphansPruned={s_cdo_orphansPruned}" : "";
            return $"{s_cdo_passes} passes (bail noPeers={s_cdo_bail_noPeers}, nre={s_cdo_bail_nre}), "
                 + $"near={FormatBigCount(s_cdo_nearTotal)} (distinct {FormatBigCount(s_cdo_distinctNearTotal)}), "
                 + $"distant={FormatBigCount(s_cdo_distantTotal)} (distinct {FormatBigCount(s_cdo_distinctDistantTotal)}), "
                 + $"gate true={s_cdo_areaReadyTrue} false={s_cdo_areaReadyFalse}"
                 + orphanSeg;
        }

        private static string FormatIsActiveAreaLoadedSegment()
        {
            if (s_iaal_calls == 0) return "noCalls";
            float pctTrue = 100f * s_iaal_resultTrue / s_iaal_calls;
            string missingDetail = s_iaal_resultFalse > 0
                ? $", missing[min={s_iaal_minMissingZones} max={s_iaal_maxMissingZones} lastMissCoord=({s_iaal_lastMissingZoneX},{s_iaal_lastMissingZoneY})]"
                : "";
            return $"{s_iaal_calls} calls, {pctTrue:F1}% true{missingDetail}";
        }

        private static void AppendAILODSegment(StringBuilder sb)
        {
            if (s_ailod_examined == 0)
            {
                sb.Append(" | AILOD: idle");
                return;
            }

            string nearestRange = s_ailod_minNearestDist < float.MaxValue
                ? $"{s_ailod_minNearestDist:F0}-{s_ailod_maxNearestDist:F0}m"
                : "n/a";
            float nearMeters = FiresGhettoNetworkMod.ConfigAILODNearDistance?.Value ?? DefaultAILODNearMeters;
            float farMeters  = FiresGhettoNetworkMod.ConfigAILODFarDistance?.Value  ?? DefaultAILODFarMeters;

            sb.Append(" | AILOD: ").Append(s_ailod_examined).Append(" ex")
              .Append(" (skipTamed=").Append(s_ailod_playerOrTamed).Append(")")
              .Append(", near=").Append(s_ailod_decidedNear)
              .Append(" mid=").Append(s_ailod_decidedMidBand)
              .Append(" farRan=").Append(s_ailod_decidedFarRan)
              .Append(" farSkipped=").Append(s_ailod_decidedFarSkipped)
              .Append(", nearestPeer ").Append(nearestRange)
              .Append(" (gate ").Append(nearMeters.ToString("F0")).Append("/").Append(farMeters.ToString("F0")).Append("m)");
        }

        private static string FormatBigCount(long n)
        {
            if (n >= 1_000_000) return (n / 1_000_000d).ToString("F2") + "M";
            if (n >= 1_000) return (n / 1_000d).ToString("F1") + "k";
            return n.ToString();
        }
    }
}
