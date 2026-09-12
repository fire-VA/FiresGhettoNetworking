using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    [HarmonyPatch]
    public static class MonsterAIPatches
    {
        // Valheim 1.0 added a groupSalt to SpawnSystem.UpdateSpawnList; it keys the spawn
        // grouping, so replicating vanilla's own values keeps our replacement pass identical
        // to the one it stands in for ("b_" biome spawners, "e_" event spawners).
        private const string BiomeSpawnerSalt = "b_";
        private const string EventSpawnerSalt = "e_";

        private const float SpawnZoneHalfExtentMeters = 32f;
        private const float SpawnZoneExtraExtentForEventDetection = 32f;
        private const float EventDiagnosticIntervalSec = 30f;

        private static readonly List<Player> _playersInZoneScratch = new List<Player>();
        private static readonly Dictionary<int, float> _eventDiagnosticLastLogTime = new Dictionary<int, float>();

        private static readonly System.Reflection.FieldInfo _spawnSystemHeightmapField = AccessTools.Field(typeof(SpawnSystem), "m_heightmap");
        private static readonly System.Reflection.FieldInfo _randomEventField    = AccessTools.Field(typeof(RandEventSystem), "m_randomEvent");
        private static readonly System.Reflection.FieldInfo _forcedEventField    = AccessTools.Field(typeof(RandEventSystem), "m_forcedEvent");
        private static readonly System.Reflection.FieldInfo _activeEventField    = AccessTools.Field(typeof(RandEventSystem), "m_activeEvent");
        private static readonly System.Reflection.MethodInfo _setActiveEventMethod = AccessTools.Method(typeof(RandEventSystem), "SetActiveEvent", new[] { typeof(RandomEvent), typeof(bool) });
        private static readonly System.Reflection.MethodInfo _isAnyPlayerInEventAreaMethod  = AccessTools.Method(typeof(RandEventSystem), "IsAnyPlayerInEventArea", new[] { typeof(RandomEvent) });


        [HarmonyPatch(typeof(BaseAI), "UpdateAI")]
        [HarmonyPrefix]
        public static bool BaseAI_UpdateAI_Prefix(BaseAI __instance)
        {
            ServerStatusDiagnostics.s_updateAi_examined++;

            if (__instance.m_nview == null)
            {
                ServerStatusDiagnostics.s_updateAi_bail_nviewNull++;
                return false;
            }
            if (!__instance.m_nview.IsValid())
            {
                ServerStatusDiagnostics.s_updateAi_bail_nviewInvalid++;
                return false;
            }
            if (__instance.m_nview.GetZDO() == null)
            {
                ServerStatusDiagnostics.s_updateAi_bail_zdoNull++;
                return false;
            }
            if (__instance.m_character == null)
            {
                ServerStatusDiagnostics.s_updateAi_bail_charNull++;
                return false;
            }

            if (__instance.m_nview.IsOwner())
                ServerStatusDiagnostics.s_updateAi_passThrough_isOwner++;
            else
                ServerStatusDiagnostics.s_updateAi_passThrough_notOwner++;

            return true;
        }

        [HarmonyPatch(typeof(MonoUpdaters), "FixedUpdate")]
        [HarmonyPostfix]
        public static void MonoUpdaters_FixedUpdate_RollupDriver_Postfix()
        {
            ServerStatusDiagnostics.TryEmit();
        }

        [HarmonyPatch(typeof(MonsterAI), "UpdateAI")]
        [HarmonyPrefix]
        public static void MonsterAI_UpdateAI_Counter_Prefix()
        {
            if (!ServerClientUtils.ZNetIsDedicated()) return;
            ServerStatusDiagnostics.s_monsterAi_examined++;
        }

        private const int MaxAwakeLogsPerSession = 10;
        private static readonly HashSet<string> _seenAwakeNames = new HashSet<string>();
        private static int _awakeLogsRemaining = MaxAwakeLogsPerSession;

        [HarmonyPatch(typeof(BaseAI), "Awake")]
        [HarmonyPostfix]
        public static void BaseAI_Awake_Diagnostic_Postfix(BaseAI __instance)
        {
            if (!ServerClientUtils.ZNetIsDedicated() || _awakeLogsRemaining <= 0) return;

            string name = __instance.gameObject != null ? __instance.gameObject.name : "<null-go>";
            if (!_seenAwakeNames.Add(name)) return;

            _awakeLogsRemaining--;
            LoggerOptions.LogMessage(
                $"[BaseAI.Awake] First-time mob '{name}' awoke server-side. "
                + $"Post-Awake BaseAI.Instances.Count={BaseAI.Instances.Count}, "
                + $"BaseAI.BaseAIInstances.Count={BaseAI.BaseAIInstances.Count}. "
                + $"(Remaining log budget: {_awakeLogsRemaining}.)");
        }

        [HarmonyPatch(typeof(ZNetScene), "CreateObject")]
        [HarmonyPostfix]
        public static void ZNetScene_CreateObject_Diagnostic_Postfix(GameObject __result)
        {
            if (!ServerClientUtils.ZNetIsDedicated()) return;
            ServerStatusDiagnostics.s_createObject_calls++;
            if (__result == null) ServerStatusDiagnostics.s_createObject_nullReturns++;
        }

        [HarmonyPatch(typeof(SpawnSystem), "UpdateSpawning")]
        [HarmonyPrefix]
        static bool UpdateSpawning_Prefix(SpawnSystem __instance, ZNetView ___m_nview, List<SpawnSystemList> ___m_spawnLists)
        {
            if (!ServerClientUtils.ZNetIsDedicated()) return true;
            if (___m_nview == null || !___m_nview.IsValid() || !___m_nview.IsOwner()) return false;

            bool shouldLogEventDiagnostic = ShouldLogEventDiagnosticForSpawnSystem(__instance,
                out RandomEvent activeEvent, out RandomEvent runningEvent);

            CollectPlayersInsideExpandedSpawnZone(__instance);
            if (_playersInZoneScratch.Count == 0)
            {
                if (shouldLogEventDiagnostic)
                    LogEventDiagnosticSkippedNoPlayers(__instance, activeEvent, runningEvent);
                return false;
            }

            PrimeVanillaTempNearPlayersFromScratch();
            if (!TryEnsureSpawnSystemHeightmap(__instance)) return false;

            DateTime time = ZNet.instance.GetTime();
            foreach (SpawnSystemList spawnList in ___m_spawnLists)
                if (spawnList?.m_spawners != null)
                    __instance.UpdateSpawnList(spawnList.m_spawners, time, false, BiomeSpawnerSalt);

            RunEventSpawners(__instance, time, shouldLogEventDiagnostic, activeEvent, runningEvent);
            return false;
        }

        private static bool ShouldLogEventDiagnosticForSpawnSystem(
            SpawnSystem ss, out RandomEvent activeEvent, out RandomEvent runningEvent)
        {
            activeEvent = RandEventSystem.instance != null ? _activeEventField.GetValue(RandEventSystem.instance) as RandomEvent : null;
            runningEvent = RandEventSystem.instance != null ? _randomEventField.GetValue(RandEventSystem.instance) as RandomEvent : null;
            if (activeEvent == null && runningEvent == null) return false;

            int id = ss.GetInstanceID();
            float now = Time.time;
            if (_eventDiagnosticLastLogTime.TryGetValue(id, out float last) && now - last < EventDiagnosticIntervalSec)
                return false;

            _eventDiagnosticLastLogTime[id] = now;
            return true;
        }

        private static void CollectPlayersInsideExpandedSpawnZone(SpawnSystem ss)
        {
            _playersInZoneScratch.Clear();
            foreach (Player player in Player.GetAllPlayers())
                if (player != null && IsPointInsideSpawnZone(ss, player.transform.position, SpawnZoneExtraExtentForEventDetection))
                    _playersInZoneScratch.Add(player);
        }

        private static bool IsPointInsideSpawnZone(SpawnSystem ss, Vector3 point, float extraExtent)
        {
            float halfExtent = SpawnZoneHalfExtentMeters + extraExtent;
            Vector3 c = ss.transform.position;
            return point.x >= c.x - halfExtent && point.x <= c.x + halfExtent
                && point.z >= c.z - halfExtent && point.z <= c.z + halfExtent;
        }

        private static void PrimeVanillaTempNearPlayersFromScratch()
        {
            SpawnSystem.m_tempNearPlayers.Clear();
            SpawnSystem.m_tempNearPlayers.AddRange(_playersInZoneScratch);
        }

        private static bool TryEnsureSpawnSystemHeightmap(SpawnSystem spawnSystem)
        {
            if (spawnSystem == null || _spawnSystemHeightmapField == null) return false;
            var heightmap = _spawnSystemHeightmapField.GetValue(spawnSystem) as Heightmap;
            if (heightmap != null) return true;
            heightmap = Heightmap.FindHeightmap(spawnSystem.transform.position);
            if (heightmap == null) return false;
            _spawnSystemHeightmapField.SetValue(spawnSystem, heightmap);
            return true;
        }

        private static void RunEventSpawners(SpawnSystem spawnSystem, DateTime time, bool shouldLog, RandomEvent activeEvent, RandomEvent runningEvent)
        {
            if (RandEventSystem.instance == null) return;

            List<SpawnSystem.SpawnData> currentSpawners = RandEventSystem.instance.GetCurrentSpawners();
            if (shouldLog)
                LogEventDiagnosticRanEventPath(spawnSystem, activeEvent, runningEvent, currentSpawners);
            if (currentSpawners != null)
                spawnSystem.UpdateSpawnList(currentSpawners, time, true, EventSpawnerSalt);
        }

        private static void LogEventDiagnosticSkippedNoPlayers(SpawnSystem ss, RandomEvent activeEvent, RandomEvent runningEvent)
        {
            Vector3 c = ss.transform.position;
            LoggerOptions.LogInfo(
                $"[EventDiag] SpawnSystem@({c.x:F0},{c.z:F0}) SKIPPED: no players in zone. " +
                $"ActiveEvent='{(activeEvent != null ? activeEvent.m_name : "null")}' " +
                $"RunningEvent='{(runningEvent != null ? runningEvent.m_name : "null")}' " +
                $"TotalPlayers={Player.GetAllPlayers().Count} Peers={ZNet.instance.GetConnectedPeers().Count}");
        }

        private static void LogEventDiagnosticRanEventPath(SpawnSystem ss, RandomEvent activeEvent, RandomEvent runningEvent, List<SpawnSystem.SpawnData> currentSpawners)
        {
            Vector3 c = ss.transform.position;
            int count = currentSpawners != null ? currentSpawners.Count : -1;
            LoggerOptions.LogInfo(
                $"[EventDiag] SpawnSystem@({c.x:F0},{c.z:F0}) RAN event path. " +
                $"ActiveEvent='{(activeEvent != null ? activeEvent.m_name : "null")}' " +
                $"RunningEvent='{(runningEvent != null ? runningEvent.m_name : "null")}' " +
                $"GetCurrentSpawners.Count={count} Players={_playersInZoneScratch.Count}");
        }

        // Hard ceiling (seconds) on how long a single FGN-driven random event may run on a
        // dedicated server before it is force-ended, regardless of its authored m_duration.
        // A raid that outlives any sane duration is a stuck event, not a design choice — this
        // guarantees the "raid ran for days and never ended" failure mode can never recur.
        private const float MaxEventWallClockSec = 3600f;

        [HarmonyPatch(typeof(RandEventSystem), "FixedUpdate")]
        [HarmonyPrefix]
        static void RandEventSystem_FixedUpdate_Prefix(RandEventSystem __instance)
        {
            if (!ServerClientUtils.ZNetIsDedicated()) return;
            if (_forcedEventField.GetValue(__instance) is RandomEvent) return;

            RandomEvent randomEvent = _randomEventField.GetValue(__instance) as RandomEvent;
            if (randomEvent == null) return;

            // A dedicated server has no local player to occupy the event origin, so vanilla's
            // pause-if-no-player-in-area logic freezes m_time the instant every player leaves
            // m_eventRange of the spawn point — and the event can then never reach m_duration to
            // end. Because FGN drives event spawns from live player proximity (UpdateSpawning_Prefix
            // / RunEventSpawners), not from the origin, a fled raid keeps spawning around the
            // players forever while its end-timer sits frozen. Forcing the running event to not
            // pause makes m_time advance on wall-clock so vanilla's own end path
            // (m_time > m_duration in RandEventSystem.FixedUpdate) fires normally.
            randomEvent.m_pauseIfNoPlayerInArea = false;

            // Absolute failsafe for events authored with no finite duration (m_duration <= 0) or a
            // pathologically large one: once the wall-clock run exceeds the ceiling, force-end via
            // the vanilla teardown so a stuck raid can never persist (nor survive across restarts,
            // since m_randomEvent is saved). Logged so the drop is visible, never silent.
            if (randomEvent.m_time > MaxEventWallClockSec)
            {
                LoggerOptions.LogWarning(
                    $"[EventDiag] Random event '{randomEvent.m_name}' exceeded {MaxEventWallClockSec:F0}s wall-clock " +
                    $"(m_time={randomEvent.m_time:F0}, m_duration={randomEvent.m_duration:F0}) — force-ending as a stuck-raid failsafe.");
                __instance.ResetRandomEvent();
                return;
            }

            bool anyPlayerInEventArea = (bool)_isAnyPlayerInEventAreaMethod.Invoke(__instance, new object[] { randomEvent });
            if (anyPlayerInEventArea)
                _activeEventField.SetValue(__instance, randomEvent);
        }

        [HarmonyPatch(typeof(RandEventSystem), "SetActiveEvent")]
        [HarmonyPrefix]
        static bool RandEventSystem_SetActiveEvent_Prefix(RandEventSystem __instance, RandomEvent ev)
        {
            if (!ServerClientUtils.ZNetIsDedicated()) return true;
            if (ev != null) return true;
            if (_forcedEventField.GetValue(__instance) is RandomEvent) return true;

            RandomEvent randomEvent = _randomEventField.GetValue(__instance) as RandomEvent;
            return randomEvent == null;
        }

        [HarmonyPatch(typeof(RandEventSystem), "FixedUpdate")]
        [HarmonyPostfix]
        static void RandEventSystem_FixedUpdate_Postfix(RandEventSystem __instance)
        {
            if (!ServerClientUtils.ZNetIsDedicated()) return;
            if (_forcedEventField.GetValue(__instance) is RandomEvent) return;

            RandomEvent randomEvent = _randomEventField.GetValue(__instance) as RandomEvent;

            if (randomEvent != null)
            {
                bool anyInArea = (bool)_isAnyPlayerInEventAreaMethod.Invoke(__instance, new object[] { randomEvent });
                _setActiveEventMethod.Invoke(__instance, new object[] { anyInArea ? randomEvent : null, false });
                return;
            }
            _setActiveEventMethod.Invoke(__instance, new object[] { null, false });
        }
    }
}
