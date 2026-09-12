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

        public static int s_updateAi_examined;
        public static int s_updateAi_bail_nviewNull;
        public static int s_updateAi_bail_nviewInvalid;
        public static int s_updateAi_bail_zdoNull;
        public static int s_updateAi_bail_charNull;
        public static int s_updateAi_passThrough_isOwner;
        public static int s_updateAi_passThrough_notOwner;

        public static int s_monsterAi_examined;

        public static int s_createObject_calls;
        public static int s_createObject_nullReturns;

        public static int s_createDestroy_passes;
        public static int s_createDestroy_bail_noPeers;
        public static int s_createDestroy_bail_nre;
        public static long s_createDestroy_nearTotal;
        public static long s_createDestroy_distantTotal;
        public static long s_createDestroy_distinctNearTotal;
        public static long s_createDestroy_distinctDistantTotal;
        public static int s_createDestroy_areaReadyTrue;
        public static int s_createDestroy_areaReadyFalse;
        public static int s_createDestroy_orphansPruned;

        public static int s_activeAreaLoaded_calls;
        public static int s_activeAreaLoaded_resultTrue;
        public static int s_activeAreaLoaded_resultFalse;
        public static int s_activeAreaLoaded_minMissingZones = int.MaxValue;
        public static int s_activeAreaLoaded_maxMissingZones;
        public static int s_activeAreaLoaded_lastMissingZoneX;
        public static int s_activeAreaLoaded_lastMissingZoneY;

        public static int s_ownership_passes;
        public static int s_ownership_zdosProcessed;
        public static int s_ownership_transfersToServer;
        public static int s_ownership_releases;

        public static int s_aiLod_examined;
        public static int s_aiLod_playerOrTamed;
        public static int s_aiLod_decidedNear;
        public static int s_aiLod_decidedMidBand;
        public static int s_aiLod_decidedFarRan;
        public static int s_aiLod_decidedFarSkipped;
        public static int s_aiLod_peersLastSeen;
        public static float s_aiLod_minNearestDist = float.MaxValue;
        public static float s_aiLod_maxNearestDist;

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
              .Append(" | BaseAI.UpdateAI: ").Append(s_updateAi_examined).Append(" ex, owner=").Append(s_updateAi_passThrough_isOwner)
                  .Append(" notOwner=").Append(s_updateAi_passThrough_notOwner)
                  .Append(" bail(nv=").Append(s_updateAi_bail_nviewNull)
                  .Append(",inv=").Append(s_updateAi_bail_nviewInvalid)
                  .Append(",zdo=").Append(s_updateAi_bail_zdoNull)
                  .Append(",char=").Append(s_updateAi_bail_charNull).Append(")")
              .Append(" | MonsterAI.UpdateAI: ").Append(s_monsterAi_examined).Append(" ex")
              .Append(" | CreateObject: ").Append(s_createObject_calls - s_createObject_nullReturns).Append(" ok, ").Append(s_createObject_nullReturns).Append(" null")
              .Append(" | CreateDestroyObjects: ").Append(FormatCreateDestroyObjectsSegment())
              .Append(" | IsActiveAreaLoaded: ").Append(FormatIsActiveAreaLoadedSegment())
              .Append(" | ServerOwnership: ").Append(s_ownership_passes).Append(" passes, ")
                  .Append(s_ownership_zdosProcessed).Append(" zdos, ")
                  .Append(s_ownership_transfersToServer).Append(" toServer, ")
                  .Append(s_ownership_releases).Append(" released");

            if (FiresGhettoNetworkMod.ConfigShowAILODInServerStatus?.Value ?? true)
                AppendAILODSegment(sb);

            LoggerOptions.LogMessage(sb.ToString());
        }

        private static void ResetWindowCounters()
        {
            s_updateAi_examined = 0;
            s_updateAi_bail_nviewNull = 0;
            s_updateAi_bail_nviewInvalid = 0;
            s_updateAi_bail_zdoNull = 0;
            s_updateAi_bail_charNull = 0;
            s_updateAi_passThrough_isOwner = 0;
            s_updateAi_passThrough_notOwner = 0;

            s_monsterAi_examined = 0;

            s_createObject_calls = 0;
            s_createObject_nullReturns = 0;

            s_createDestroy_passes = 0;
            s_createDestroy_bail_noPeers = 0;
            s_createDestroy_bail_nre = 0;
            s_createDestroy_nearTotal = 0;
            s_createDestroy_distantTotal = 0;
            s_createDestroy_distinctNearTotal = 0;
            s_createDestroy_distinctDistantTotal = 0;
            s_createDestroy_areaReadyTrue = 0;
            s_createDestroy_areaReadyFalse = 0;
            s_createDestroy_orphansPruned = 0;

            s_activeAreaLoaded_calls = 0;
            s_activeAreaLoaded_resultTrue = 0;
            s_activeAreaLoaded_resultFalse = 0;
            s_activeAreaLoaded_minMissingZones = int.MaxValue;
            s_activeAreaLoaded_maxMissingZones = 0;
            s_activeAreaLoaded_lastMissingZoneX = 0;
            s_activeAreaLoaded_lastMissingZoneY = 0;

            s_ownership_passes = 0;
            s_ownership_zdosProcessed = 0;
            s_ownership_transfersToServer = 0;
            s_ownership_releases = 0;

            s_aiLod_examined = 0;
            s_aiLod_playerOrTamed = 0;
            s_aiLod_decidedNear = 0;
            s_aiLod_decidedMidBand = 0;
            s_aiLod_decidedFarRan = 0;
            s_aiLod_decidedFarSkipped = 0;
            s_aiLod_peersLastSeen = 0;
            s_aiLod_minNearestDist = float.MaxValue;
            s_aiLod_maxNearestDist = 0f;
        }

        private static string FormatCreateDestroyObjectsSegment()
        {
            if (s_createDestroy_passes + s_createDestroy_bail_noPeers + s_createDestroy_bail_nre == 0) return "noCalls";
            string orphanSeg = s_createDestroy_orphansPruned > 0 ? $", orphansPruned={s_createDestroy_orphansPruned}" : "";
            return $"{s_createDestroy_passes} passes (bail noPeers={s_createDestroy_bail_noPeers}, nre={s_createDestroy_bail_nre}), "
                 + $"near={FormatBigCount(s_createDestroy_nearTotal)} (distinct {FormatBigCount(s_createDestroy_distinctNearTotal)}), "
                 + $"distant={FormatBigCount(s_createDestroy_distantTotal)} (distinct {FormatBigCount(s_createDestroy_distinctDistantTotal)}), "
                 + $"gate true={s_createDestroy_areaReadyTrue} false={s_createDestroy_areaReadyFalse}"
                 + orphanSeg;
        }

        private static string FormatIsActiveAreaLoadedSegment()
        {
            if (s_activeAreaLoaded_calls == 0) return "noCalls";
            float pctTrue = 100f * s_activeAreaLoaded_resultTrue / s_activeAreaLoaded_calls;
            string missingDetail = s_activeAreaLoaded_resultFalse > 0
                ? $", missing[min={s_activeAreaLoaded_minMissingZones} max={s_activeAreaLoaded_maxMissingZones} lastMissCoord=({s_activeAreaLoaded_lastMissingZoneX},{s_activeAreaLoaded_lastMissingZoneY})]"
                : "";
            return $"{s_activeAreaLoaded_calls} calls, {pctTrue:F1}% true{missingDetail}";
        }

        private static void AppendAILODSegment(StringBuilder sb)
        {
            if (s_aiLod_examined == 0)
            {
                sb.Append(" | AILOD: idle");
                return;
            }

            string nearestRange = s_aiLod_minNearestDist < float.MaxValue
                ? $"{s_aiLod_minNearestDist:F0}-{s_aiLod_maxNearestDist:F0}m"
                : "n/a";
            float nearMeters = FiresGhettoNetworkMod.ConfigAILODNearDistance?.Value ?? DefaultAILODNearMeters;
            float farMeters  = FiresGhettoNetworkMod.ConfigAILODFarDistance?.Value  ?? DefaultAILODFarMeters;

            sb.Append(" | AILOD: ").Append(s_aiLod_examined).Append(" ex")
              .Append(" (skipTamed=").Append(s_aiLod_playerOrTamed).Append(")")
              .Append(", near=").Append(s_aiLod_decidedNear)
              .Append(" mid=").Append(s_aiLod_decidedMidBand)
              .Append(" farRan=").Append(s_aiLod_decidedFarRan)
              .Append(" farSkipped=").Append(s_aiLod_decidedFarSkipped)
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
