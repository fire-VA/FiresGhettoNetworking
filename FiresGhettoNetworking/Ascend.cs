using BepInEx;
using BepInEx.Configuration;
using FiresGhettoNetworkMod.AutoTune;
using HarmonyLib;
using System;
using System.Collections;
using System.ComponentModel;
using System.Reflection;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(ValheimCommunityPatchCompat.PluginGuid, BepInDependency.DependencyFlags.SoftDependency)]

    public class FiresGhettoNetworkMod : BaseUnityPlugin
    {
        public const string PluginGUID = "com.Fire.FiresGhettoNetworkMod";
        public const string PluginName = "FiresGhettoNetworkMod";
        public const string PluginVersion = "1.4.15";
        internal static Harmony Harmony { get; private set; }

        // Static reference so non-MonoBehaviour subsystems (AutoTuneProbe coroutine, etc.)
        // can call StartCoroutine via the plugin instance.
        public static FiresGhettoNetworkMod Instance { get; private set; }

        // BepInEx log source — banner emitters route through this (not Debug.Log)
        // so banner lines don't stdout-echo a raw white console duplicate.
        public static BepInEx.Logging.ManualLogSource Log;

        public static ConfigEntry<LogLevel> ConfigLogLevel;
        public static ConfigEntry<bool> ConfigEnableCompression;
        public static ConfigEntry<UpdateRateOptions> ConfigUpdateRate;
        public static ConfigEntry<SendRateMinOptions> ConfigSendRateMin;
        public static ConfigEntry<SendRateMaxOptions> ConfigSendRateMax;
        public static ConfigEntry<QueueSizeOptions> ConfigQueueSize;
        public static ConfigEntry<ForceCrossplayOptions> ConfigForceCrossplay;
        public static ConfigEntry<int> ConfigPlayerLimit;
        public static ConfigEntry<int> ConfigAdvertisedPlayerLimit;
        public static ConfigEntry<bool> ConfigEnableShipFixes;
        public static ConfigEntry<bool> ConfigEnableServerSideShipSimulation;
        public static ConfigEntry<int> ConfigExtendedZoneRadius;
        public static ConfigEntry<bool> ConfigEnableZDOThrottling;
        public static ConfigEntry<float> ConfigZDOThrottleDistance;
        public static ConfigEntry<bool> ConfigEnableAILOD;
        public static ConfigEntry<float> ConfigAILODNearDistance;
        public static ConfigEntry<float> ConfigAILODFarDistance;
        public static ConfigEntry<float> ConfigAILODThrottleFactor;
        public static ConfigEntry<bool> ConfigEnableAdaptiveThrottling;
        public static ConfigEntry<int> ConfigSendCongestionThresholdPct;
        public static ConfigEntry<bool> ConfigEnableSendHeartbeatLog;
        public static ConfigEntry<bool> ConfigEnableFallThroughDiagnostics;
        public static ConfigEntry<int> ConfigZoneLoadBatchSize;
        public static ConfigEntry<int> ConfigZPackageReceiveBufferSize;
        public static ConfigEntry<bool>  ConfigEnableTimeSliceInstantiation;
        public static ConfigEntry<int>   ConfigInstantiationBudgetMs;
        public static ConfigEntry<int>   ConfigMaxInstancesPerFrame;
        public static ConfigEntry<bool>  ConfigSafetyFallbackEnabled;
        public static ConfigEntry<int>   ConfigSafetyFallbackThreshold;
        public static ConfigEntry<bool>  ConfigEnablePredictiveZoneStreaming;
        public static ConfigEntry<float> ConfigPredictionLookaheadSec;
        public static ConfigEntry<float> ConfigPredictionMinVelocity;
        public static ConfigEntry<int>   ConfigPredictionMaxLookaheadZones;
        public static ConfigEntry<bool> ConfigEnableInvulnerableSupportSkip;
        public static ConfigEntry<bool> ConfigEnableInstanceOrphanPrune;
        public static ConfigEntry<bool> ConfigFixTeleportGhosts;
        public static ConfigEntry<bool> ConfigFixSlowSleep;
        public static ConfigEntry<bool> ConfigFixBoatDamageFromTimeSync;
        public static ConfigEntry<bool> ConfigFixGroundSnapThroughFloors;
        public static ConfigEntry<bool> ConfigEnableRpcRouter;
        public static ConfigEntry<bool> ConfigEnableRpcAoI;
        public static ConfigEntry<float> ConfigRpcAoIRadius;
        public static ConfigEntry<bool> ConfigEnableZDODelta;
        public static ConfigEntry<bool> ConfigEnableWNTServerOptimization;
        public static ConfigEntry<bool> ConfigEnableServerAuthority;
        public static ConfigEntry<bool> ConfigEnableServerOwnership;
        public static ConfigEntry<bool> ConfigEnableServerOwnershipSelective;
        public static ConfigEntry<bool> ConfigShowAILODInServerStatus;
        public static ConfigEntry<bool> ConfigEnableBootPatchVerification;
        public static ConfigEntry<bool> ConfigEnableLargeZdoDiagnostics;
        public static ConfigEntry<int> ConfigClientMaxDestroysPerFrame;
        public static ConfigEntry<string> ConfigDediFellOutRescueLayers;
        public static ConfigEntry<float> ConfigDiagnosticIntervalSec;
        public static ConfigEntry<bool> ConfigEnableBulkTransferBoost;
        public static ConfigEntry<int> ConfigBulkTransferBudgetPercent;
        public static ConfigEntry<bool> ConfigHyperBoost;


        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Harmony = new Harmony(PluginGUID);

            // BIG obnoxious "loading" banner — fires FIRST before any
            // other Debug.Log so it sits at the top of the FGN-related
            // console output as the load announcement. The smaller
            // antenna+signal-bar banner fires at the end of Awake to
            // bookend load with a "we're ready" confirmation. See
            // VAGhettoBanner for the two-banner rationale + content.
            try { VAGhettoBanner.PrintBig(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] VAGhettoBanner.PrintBig failed: {ex.Message}");
            }

            // Install the color patch BEFORE PatchAll runs on other
            // patch classes, so subsequent FGN logs (those that route
            // through Debug.Log with our [FiresGhetto] tag) get colored
            // from the very first line. The patch's own FAT-detection
            // guard bails if FiresAdminTerrain is loaded — see
            // FiresLogColorPatch class header for the dedup rationale.
            try { FiresLogColorPatch.Install(Harmony); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] FiresLogColorPatch.Install failed: {ex.Message}");
            }

            BindConfigs();

            try { Config.Save(); }
            catch (Exception ex) { Logger.LogWarning($"Failed to save config file immediately: {ex.Message}"); }

            LoggerOptions.Init(Logger);

            // Audit every AutoTune tier preset against the vanilla floor now that the logger exists
            // (it runs AFTER Init for exactly that reason); LogError on any sub-vanilla value so a
            // regression is caught at load, not as a field complaint.
            TierPresets.ValidateVanillaFloors();

            ServerClientUtils.Detect(Logger);

            bool isDedicated = ServerClientUtils.IsDedicatedServerDetected;

            // Visible at default log level so deployment side is obvious in logs.
            if (isDedicated)
            {
                LoggerOptions.LogMessage($"{PluginName} v{PluginVersion} — Running on DEDICATED SERVER, enabling server-side features.");
            }
            else
            {
                LoggerOptions.LogMessage($"{PluginName} v{PluginVersion} — Running on CLIENT or SINGLE-PLAYER/LISTEN SERVER, only client-safe features will be applied.");
            }

            ValheimCommunityPatchCompat.Detect();

            // Always registered: harmless on clients, or needed before anything else.
            InvokeStaticInitByTypeName("FiresGhettoNetworkMod.CompressionGroup", "InitConfig", new object[] { Config });
            InvokeStaticInitByTypeName("FiresGhettoNetworkMod.NetworkingRatesGroup", "Init", new object[] { Config });
            InvokeStaticInitByTypeName("FiresGhettoNetworkMod.DedicatedServerGroup", "Init", new object[] { Config });
            SendQueueHeadroomMonitor.InitConfig(Config);
            AdaptiveSendRate.InitConfig(Config);

            Harmony.PatchAll(typeof(CompressionGroup));
            Harmony.PatchAll(typeof(NetworkingRatesGroup));
            Harmony.PatchAll(typeof(DedicatedServerGroup));

            Harmony.PatchAll(typeof(SendZDOsHeartbeatDiagnostic));

            if (ConfigEnableFallThroughDiagnostics.Value)
            {
                Harmony.PatchAll(typeof(FallThroughProbe));
                Harmony.PatchAll(typeof(PieceTypeAudit));
            }

            Harmony.PatchAll(typeof(ServerDisconnectDiagnostics));

            Harmony.PatchAll(typeof(BulkTransferGatePatches));

            Harmony.PatchAll(typeof(BigZdoDiagnostic));

            Harmony.PatchAll(typeof(DisarmOverloadTest));

            Harmony.PatchAll(typeof(CompressionRoundTripTest));

            Harmony.PatchAll(typeof(SocketStressTests));

            Harmony.PatchAll(typeof(SendQueueHeadroomMonitor));

            Harmony.PatchAll(typeof(AdaptiveSendRate));

            Harmony.PatchAll(typeof(ZdoFloodTest));

            WackyDatabaseCompatibilityPatch.Init(Harmony);



            Harmony.PatchAll(typeof(PlayerPositionSyncPatches));

            Harmony.PatchAll(typeof(WearNTearClientSupportPatches));

            Harmony.PatchAll(typeof(ClientCleanupThrottle));

            Harmony.PatchAll(typeof(GroundSnapPatches));

            Harmony.PatchAll(typeof(OwnershipHandoffPatches));

            try
            {
                TeleportGhostFix.Init();
                Harmony.PatchAll(typeof(TeleportGhostFix));
            }
            catch (System.Exception ex)
            {
                LoggerOptions.LogWarning($"[TeleportGhostFix] could not be attached; vanilla behaviour is unchanged. {ex.Message}");
            }

            Harmony.PatchAll(typeof(SleepTimeSkipFix));

            if (!isDedicated)
                Harmony.PatchAll(typeof(WaveClockSmoothing));

            // Auto-tune: probe on clients, self-tune on servers.
            Harmony.PatchAll(typeof(AutoTuneProbeHooks));
            Harmony.PatchAll(typeof(ZoneLoadPatches));
            ServerAutoTune.InitServerSide();


            // ClientLogRelay is excluded from the csproj until it ships as its own mod; its sources remain on disk.
            if (isDedicated)
            {
                ApplyServerTrafficPatches();

                bool simulationActive = false;
                if (!ConfigEnableServerAuthority.Value)
                {
                    LoggerOptions.LogInfo("Server-side simulation disabled via ConfigEnableServerAuthority = false.");
                }
                else if (ValheimCommunityPatchCompat.SchedulesSceneObjects)
                {
                    LoggerOptions.LogWarning(
                        "Server-Side Simulation is OFF for this session: ValheimCommunityPatch replaces the server's object "
                        + "create/destroy pass with its own, built around world origin, and destroys every object FGN creates "
                        + "for players. Remove ValheimCommunityPatch from the server to use Server-Side Simulation, or turn "
                        + "'Enable Server-Side Simulation' off to silence this warning.");
                }
                else
                {
                    ApplyServerSideSimulationPatches();
                    simulationActive = true;
                }

                EmitServerFeatureSummary(simulationActive);
            }
            else
            {
                LoggerOptions.LogInfo("Server-side features skipped — not running on a dedicated server.");
            }

            StartCoroutine(EmitCompactBannerWhenZNetReady());
        }

        /// <summary>Dedicated-server traffic shaping. None of it needs Server-Side Simulation; each feature follows its own toggle.</summary>
        private static void ApplyServerTrafficPatches()
        {
            Harmony.PatchAll(typeof(ZDOThrottlingPatches));
            Harmony.PatchAll(typeof(AILODPatches));

            if (ConfigEnableRpcRouter.Value)
            {
                Harmony.PatchAll(typeof(RpcRouterPatches));
                DamageTextHandler.Register();
                HealthChangedHandler.Register();
                WNTHealthChangedHandler.Register();
                SetTargetHandler.Register();
                AddNoiseHandler.Register();
                TriggerAnimationHandler.Register();
                TriggerOnDeathHandler.Register();
                TalkerSayHandler.Register();
                SpawnedZoneHandler.Register();
                VAGhettoLoadSummary.EmitRpcRouter(
                    handlersRegistered: RoutedRpcManager.HandlerCount,
                    aoiRadius: ConfigRpcAoIRadius?.Value ?? 256f,
                    aoiEnabled: ConfigEnableRpcAoI?.Value ?? false);
                if (VAGhettoLoadSummary.VerboseEnabled)
                    LoggerOptions.LogInfo("RPC Router enabled — handlers: " + string.Join(", ", RoutedRpcManager.HandlerMethodNames) + ".");
            }

            if (ConfigEnableZDODelta.Value)
            {
                Harmony.PatchAll(typeof(ZDODeltaPatches));
                LoggerOptions.LogInfo("ZDO delta compression enabled.");
            }

            if (ConfigEnableWNTServerOptimization.Value)
            {
                Harmony.PatchAll(typeof(WearNTearServerPatches));
                LoggerOptions.LogInfo("WearNTear server optimization enabled.");
            }
        }

        /// <summary>The server instantiating, owning and driving the world around every peer.</summary>
        private static void ApplyServerSideSimulationPatches()
        {
            if (ConfigEnableShipFixes.Value)
                Harmony.PatchAll(typeof(ShipFixesGroup));

            Harmony.PatchAll(typeof(ServerShipSimulationPatches));

            Harmony.PatchAll(typeof(ZDOMemoryManager));

            Harmony.PatchAll(typeof(ServerAuthorityPatches));
            Harmony.PatchAll(typeof(ServerStabilityPatches));
            Harmony.PatchAll(typeof(MonsterAIPatches));

            if (ConfigEnableServerOwnershipSelective.Value)
            {
                if (ConfigEnableServerOwnership.Value)
                {
                    LoggerOptions.LogWarning(
                        "Both 'Server ZDO Ownership Transfer' flags are ENABLED — selective (V3) takes precedence; broad (V2) is being ignored. Disable one to silence this warning.");
                }
                Harmony.PatchAll(typeof(ServerOwnershipPatchesV3));
                LoggerOptions.LogMessage(
                    "Server ZDO ownership (V3 SELECTIVE) ENABLED — Character/Ship only; drops/voxel/interactables/carts stay peer-owned.");
            }
            else if (ConfigEnableServerOwnership.Value)
            {
                Harmony.PatchAll(typeof(ServerOwnershipPatches));
                LoggerOptions.LogMessage(
                    "Server ZDO ownership (V2 BROAD SSS-exact) ENABLED — every persistent ZDO in any peer's active area will be claimed by the server.");
                if (!ConfigEnableServerSideShipSimulation.Value)
                    LoggerOptions.LogWarning(
                        "V2 BROAD claims SHIPS as well, regardless of 'Server-Side Ship Simulation' being off — it is a "
                        + "deliberate verbatim port with no per-prefab exclusions. A server-owned hull runs its own physics, "
                        + "and ImpactEffect only fires for the owner, so boats can take phantom damage on calm water. "
                        + "Use the Selective (V3) toggle instead if your players sail; it honours that setting.");
            }
            else
            {
                LoggerOptions.LogInfo(
                    "Server ZDO ownership transfer disabled (both V2 and V3 flags = false). Vanilla peer ownership in effect.");
            }

            if (ConfigEnableBootPatchVerification.Value)
            {
                DumpPatchInfo(typeof(BaseAI),       "UpdateAI");
                DumpPatchInfo(typeof(BaseAI),       "Awake");
                DumpPatchInfo(typeof(MonsterAI),    "UpdateAI");
                DumpPatchInfo(typeof(Character),    "CustomFixedUpdate");
                DumpPatchInfo(typeof(MonoUpdaters), "FixedUpdate");
                DumpPatchInfo(typeof(ZNetScene),    "CreateDestroyObjects");
                DumpPatchInfo(typeof(ZNetScene),    "CreateObject");
                DumpPatchInfo(typeof(ZoneSystem),   "IsActiveAreaLoaded");
                DumpPatchInfo(typeof(SpawnSystem),  "UpdateSpawning");
                DumpPatchInfo(typeof(ShieldDomeImageEffect), "Awake");
                DumpPatchInfo(typeof(Heightmap),    "RebuildRenderMesh");
            }

            if (VAGhettoLoadSummary.VerboseEnabled)
                LoggerOptions.LogInfo("All server-side simulation patches enabled.");
        }

        private static void EmitServerFeatureSummary(bool simulationActive)
        {
            string ownershipMode =
                ConfigEnableServerOwnershipSelective.Value ? "V3 selective"
                : ConfigEnableServerOwnership.Value ? "V2 broad"
                : "vanilla peer";
            VAGhettoLoadSummary.EmitServerAuthority(
                simulation: simulationActive,
                ownership: ownershipMode,
                zdoDelta: ConfigEnableZDODelta?.Value ?? false,
                zdoThrottle: ConfigEnableZDOThrottling?.Value ?? false,
                aiLod: ConfigEnableAILOD?.Value ?? false,
                wntOpt: ConfigEnableWNTServerOptimization?.Value ?? false);
        }

        // Waits for ZNetScene + ObjectDB to be live (same readiness
        // signal FAP uses in VaPieces.WaitForZNetReady), then emits
        // the compact loaded banner. Failure is non-fatal — falls
        // back to the plain "loaded" log so the load event is still
        // recorded in the file log.
        private IEnumerator EmitCompactBannerWhenZNetReady()
        {
            while (ZNetScene.instance == null
                   || ZNetScene.instance.m_prefabs == null
                   || ZNetScene.instance.m_prefabs.Count == 0)
            {
                yield return null;
            }
            yield return new WaitForEndOfFrame();

            try { VAGhettoBanner.Print(); }
            catch (Exception ex)
            {
                Logger.LogInfo($"{PluginName} v{PluginVersion} loaded. (banner failed: {ex.Message})");
            }
        }

        private void OnApplicationQuit()
        {
            // Previously called ServerHeartbeat.OnServerStop() from the
            // ClientLogRelay module.  That module is excluded from the build
            // until it's spun out as its own standalone mod; nothing to do here
            // for now.
        }

        // Compatibility patch for WackyDatabase — safely skips SnapshotItem for broken/null items
        [HarmonyPatch]
        public static class WackyDatabaseCompatibilityPatch
        {
            public static void Init(Harmony harmony)
            {
                // Safely detect if WackyDatabase is present
                Type functionsType = Type.GetType("wackydatabase.Util.Functions, WackysDatabase");
                if (functionsType == null)
                {
                    LoggerOptions.LogInfo("WackyDatabase not detected — skipping compatibility patch.");
                    return;
                }

                MethodInfo snapshotMethod = functionsType.GetMethod("SnapshotItem", BindingFlags.Static | BindingFlags.Public);
                if (snapshotMethod == null)
                {
                    LoggerOptions.LogWarning("WackyDatabase detected but SnapshotItem method not found — patch skipped.");
                    return;
                }

                // Apply the prefix patch
                harmony.Patch(
                    original: snapshotMethod,
                    prefix: new HarmonyMethod(typeof(WackyDatabaseCompatibilityPatch), nameof(SnapshotItem_Prefix))
                );

                LoggerOptions.LogInfo("WackyDatabase compatibility patch applied — will skip snapshots for invalid/broken clones.");
            }

            // Prefix for SnapshotItem(ItemDrop item, ...)
            [HarmonyPrefix]
            public static bool SnapshotItem_Prefix(ref ItemDrop item) // Use ref to allow null check + early exit
            {
                // First and most important: null item
                if (item == null)
                {
                    LoggerOptions.LogWarning("WDB: Skipping snapshot for null ItemDrop (likely broken clone).");
                    return false; // Skip original
                }

                // Second: item has no valid gameObject (common when cloneFrom prefab is missing)
                if (item.gameObject == null)
                {
                    LoggerOptions.LogWarning($"WDB: Skipping snapshot for {item.name} — gameObject is null (missing prefab from removed mod).");
                    return false;
                }

                // Third: no renderable components (prevents NRE in bounds calculation and rendering)
                bool hasRenderer = item.GetComponentsInChildren<Renderer>(true).Length > 0;
                bool hasMesh = item.GetComponentsInChildren<MeshFilter>(true).Length > 0;

                if (!hasRenderer && !hasMesh)
                {
                    LoggerOptions.LogWarning($"WDB: Skipping snapshot for {item.name} — no renderers or meshes (broken model).");
                    return false;
                }

                // All good — allow original method to run
                return true;
            }
        }


        

        private void TryPatchAll(Type type)
        {
            if (type == null)
            {
                Logger.LogError("Tried to patch a null type!");
                return;
            }
            Harmony.PatchAll(type);
        }

        // ============================================================
        // POST-PATCHALL VERIFICATION
        //
        // Logs every Harmony patch attached to (type, methodName) right
        // now — owner / patch-method full name / priority. Lets us prove
        // attachment from boot logs without waiting for the method to
        // actually fire at runtime.
        // ============================================================
        private static void DumpPatchInfo(Type type, string methodName)
        {
            var mi = AccessTools.Method(type, methodName);
            if (mi == null)
            {
                LoggerOptions.LogMessage($"[PatchVerify] {type.FullName}.{methodName} → method NOT FOUND by AccessTools.");
                return;
            }
            var info = HarmonyLib.Harmony.GetPatchInfo(mi);
            if (info == null)
            {
                LoggerOptions.LogMessage($"[PatchVerify] {type.FullName}.{methodName} → NO patches attached (info=null).");
                return;
            }
            int total = (info.Prefixes?.Count ?? 0)
                      + (info.Postfixes?.Count ?? 0)
                      + (info.Transpilers?.Count ?? 0)
                      + (info.Finalizers?.Count ?? 0);
            LoggerOptions.LogMessage($"[PatchVerify] {type.FullName}.{methodName} → total={total} (prefixes={info.Prefixes?.Count ?? 0}, postfixes={info.Postfixes?.Count ?? 0}, transpilers={info.Transpilers?.Count ?? 0}, finalizers={info.Finalizers?.Count ?? 0}).");
            void DumpList(string kind, System.Collections.Generic.IEnumerable<HarmonyLib.Patch> ps)
            {
                if (ps == null) return;
                foreach (var p in ps)
                    LoggerOptions.LogMessage($"[PatchVerify]   [{kind}] owner='{p.owner}' method={p.PatchMethod.DeclaringType?.FullName}.{p.PatchMethod.Name} prio={p.priority}");
            }
            DumpList("Prefix",     info.Prefixes);
            DumpList("Postfix",    info.Postfixes);
            DumpList("Transpiler", info.Transpilers);
            DumpList("Finalizer",  info.Finalizers);
        }

        private void InvokeStaticInitByTypeName(string typeName, string methodName, object[] args)
        {
            try
            {
                var type = Type.GetType(typeName);
                if (type == null)
                {
                    Logger.LogWarning($"Type {typeName} not found; skipping {methodName}.");
                    return;
                }
                var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null)
                {
                    Logger.LogWarning($"Method {methodName} not found on {typeName}.");
                    return;
                }
                method.Invoke(null, args);
            }
            catch (TypeLoadException tle)
            {
                Logger.LogError($"TypeLoadException while invoking {typeName}.{methodName}: {tle}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Exception while invoking {typeName}.{methodName}: {ex}");
            }
        }

        private void BindConfigs()
        {
            ConfigLogLevel = Config.Bind(
                "01 - General",
                "Log Level",
                LogLevel.Message,
                "Controls verbosity in BepInEx log.");

            // ConfigManager sections render in first-Bind() order. The priming
            // binds + Init calls below set sections 02..08 to match the "NN - "
            // prefix; later duplicate Binds in this method return the same entry.
            ConfigZoneLoadBatchSize = Config.Bind(
                "02 - Client Performance",
                "Zone Load Batch Size",
                1,
                new ConfigDescription(
                    "How aggressively the client consumes incoming zone-stream backlog per frame.\n" +
                    "1 = vanilla (one CreateObjects pass per frame, capped by Valheim).\n" +
                    "2 = double the per-frame cap (faster zone load, bigger frame hitches).\n" +
                    "4 = quadruple (zone-cross stutter masking on capable machines).\n" +
                    "Auto-Tune may override this on the client based on measured frame time.\n" +
                    "CLIENT-ONLY — no effect on server.",
                    new AcceptableValueRange<int>(1, 8)));

            PlayerPositionSyncPatches.Init(Config);

            ConfigEnableCompression = Config.Bind(
                "04 - Networking",
                "Enable Compression",
                true,
                "Enable ZSTD network compression (highly recommended).");

            ConfigUpdateRate = Config.Bind(
                "04 - Networking",
                "ZDO Send Rate",
                UpdateRateOptions._100,
                "How often the server SENDS ZDO updates to clients. This is a NETWORK send-cadence\n" +
                "setting ONLY — it does NOT change the world/game tick, day length, smelter/cook timers,\n" +
                "cooldowns, or any simulation speed. Higher = other players/creatures look smoother to\n" +
                "you, at the cost of more bandwidth.\n" +
                "100% (20 sends/sec) matches vanilla and is the safe default. 150% (30 sends/sec) can look\n" +
                "smoother on high-pop servers with bandwidth headroom; lower it if bandwidth is tight.\n" +
                "SERVER-ONLY.");

            ConfigSendRateMin = Config.Bind(
                "05 - Networking - Steamworks",
                "Send Rate Min",
                SendRateMinOptions._512KB,
                "Minimum send rate Steam will attempt. Steam's adapter has a sticky-down quirk — peers " +
                "that back off toward this value tend to stay there. Keep it well above unplayable.");

            ConfigSendRateMax = Config.Bind(
                "05 - Networking - Steamworks",
                "Send Rate Max",
                SendRateMaxOptions._2048KB,
                "Maximum send rate Steam will attempt. This is 'permission to burst' — Steam still ramps " +
                "adaptively between Min and Max, this just removes the artificial ceiling.");

            ConfigHyperBoost = Config.Bind(
                "05 - Networking - Steamworks",
                "HYPERBOOST",
                false,
                "MAX-THROUGHPUT MODE. Overrides Auto-Tune AND the Send Rate Min/Max above, lifting Steam's\n" +
                "send rate, send buffer, and recv buffer / per-message ceiling to their proven unlocked\n" +
                "maximums — the same lifts fgn_socketramp uses to reach ~40 MB/s, versus the ~8 MB/s the\n" +
                "High tier caps everyday traffic at. Applies LIVE the instant you toggle it (no reconnect).\n" +
                "For a server->client transfer, set it on BOTH sides: the server lifts its outbound, the\n" +
                "client lifts its inbound. The recv side needs FiresSteamworksPatcher installed.\n" +
                "This trades Steam's conservative congestion ceiling for raw headroom and lets the buffers\n" +
                "grow large under load, so leave it OFF for normal play and ON for high-bandwidth links or\n" +
                "benchmarking.");

            AutoTuneConfig.Init(Config);

            ConfigQueueSize = Config.Bind(
                "04 - Networking",
                "Queue Size",
                QueueSizeOptions._32KB,
                "Send queue size. Higher helps high-player servers.");

            ConfigForceCrossplay = Config.Bind(
                "09 - Dedicated Server",
                "Force Crossplay",
                ForceCrossplayOptions.vanilla,
                "Requires restart. Selects the networking backend for a DEDICATED SERVER.\n" +
                "vanilla = respect the command-line -crossplay flag (DEFAULT — does NOT change how your server connects).\n" +
                "steamworks = force Steam-only; DISABLES crossplay. Best performance for an all-Steam playerbase, " +
                "but Xbox / Game Pass / PlayStation players cannot join.\n" +
                "playfab = force crossplay ENABLED (PlayFab matchmaking) regardless of the -crossplay flag.");

            ConfigPlayerLimit = Config.Bind(
                "09 - Dedicated Server",
                "Player Limit",
                10,
                new ConfigDescription("Max players on dedicated server. Requires restart.", new AcceptableValueRange<int>(1, 999)));

            ConfigAdvertisedPlayerLimit = Config.Bind(
                "09 - Dedicated Server",
                "Advertised Player Limit",
                0,
                new ConfigDescription(
                    "Max players advertised to matchmaking (Steam server browser → BattleMetrics, PlayFab session/Party). " +
                    "Independent of the actual in-game limit set by 'Player Limit' — useful when an operator wants their " +
                    "server listed as '/500' for marketing while running a real 30-slot cap. " +
                    "0 = mirror 'Player Limit' (advertised matches reality). Requires restart.",
                    new AcceptableValueRange<int>(0, 9999)));

            // ---- Client-side perf knobs (formerly stubbed; wired up by AutoTune patches) ----
            ConfigZoneLoadBatchSize = Config.Bind(
                "02 - Client Performance",
                "Zone Load Batch Size",
                1,
                new ConfigDescription(
                    "How aggressively the client consumes incoming zone-stream backlog per frame.\n" +
                    "1 = vanilla (one CreateObjects pass per frame, capped by Valheim).\n" +
                    "2 = double the per-frame cap (faster zone load, bigger frame hitches).\n" +
                    "4 = quadruple (zone-cross stutter masking on capable machines).\n" +
                    "Auto-Tune may override this on the client based on measured frame time.\n" +
                    "CLIENT-ONLY — no effect on server.",
                    new AcceptableValueRange<int>(1, 8)));

            ConfigZPackageReceiveBufferSize = Config.Bind(
                "02 - Client Performance",
                "ZPackage Receive Buffer Bytes",
                256 * 1024,
                new ConfigDescription(
                    "Steam recv-buffer size for inbound network packages. Bigger = fewer dropped packets\n" +
                    "if the client briefly stalls (GC pause, disk hitch), but uses more RAM.\n" +
                    "Auto-Tune may override this on the client based on measured tier.",
                    new AcceptableValueRange<int>(64 * 1024, 4 * 1024 * 1024)));

            // ---- Time-sliced instantiation (Workstream A) ----
            ConfigEnableTimeSliceInstantiation = Config.Bind(
                "02 - Client Performance",
                "Enable Time-Slice Instantiation",
                false,
                "Replace vanilla's fixed 10/100 per-frame ZDO instantiation cap with a per-frame ms\n" +
                "budget that drains incoming objects across multiple ticks. Eliminates the big spike\n" +
                "when crossing into a heavy zone. Disable to fall back to the cap-bump transpiler\n" +
                "(set 'Zone Load Batch Size' to control its multiplier).\n" +
                "OFF by default: instantiating this fast can spawn a creature/item the instant its ZDO\n" +
                "arrives — before the structure it rests on, when that ZDO lags a tick behind — which can\n" +
                "let tames slip locked pens or drop items through floors on zone load. Opt in for the\n" +
                "smoother zone crossings if your world doesn't hit that.\n" +
                "CLIENT-ONLY — no effect on dedicated server.");

            ConfigInstantiationBudgetMs = Config.Bind(
                "02 - Client Performance",
                "Instantiation Budget Ms",
                3,
                new ConfigDescription(
                    "Per-frame millisecond budget for ZDO → GameObject instantiation when time-slicing\n" +
                    "is enabled. Lower = smoother frame times during zone load (longer ramp-in); higher\n" +
                    "= zone loads finish faster (bigger spikes). 3 ms keeps 60-fps clients comfortable.\n" +
                    "Auto-Tune overrides this with tier-specific values when active.",
                    new AcceptableValueRange<int>(1, 16)));

            ConfigMaxInstancesPerFrame = Config.Bind(
                "02 - Client Performance",
                "Max Instances Per Frame",
                100,
                new ConfigDescription(
                    "Hard ceiling on instantiations per frame regardless of how much budget remains.\n" +
                    "Belt-and-suspenders against a runaway pending list.\n" +
                    "Auto-Tune overrides this with tier-specific values when active.",
                    new AcceptableValueRange<int>(10, 500)));

            ConfigSafetyFallbackEnabled = Config.Bind(
                "02 - Client Performance",
                "Safety Fallback Enabled",
                true,
                "When the pending instantiation list grows past 'Safety Fallback Threshold' items\n" +
                "(teleport into megabase, freshly-loaded zones), widen the per-frame budget so we\n" +
                "don't bleed across many seconds. Capped at 16 ms so we never burn an entire frame.");

            ConfigSafetyFallbackThreshold = Config.Bind(
                "02 - Client Performance",
                "Safety Fallback Threshold",
                5000,
                new ConfigDescription(
                    "Pending-ZDO count at which the safety fallback widens the instantiation budget.",
                    new AcceptableValueRange<int>(100, 50000)));

            ConfigEnableRpcRouter = Config.Bind(
                "10 - Server Authority",
                "Enable RPC Router",
                true,
                "Server-side RPC filtering \u2014 drops unnecessary DamageText/HealthChanged RPCs before routing\n" +
                "and prevents SetTarget exploits (targeting players/tamed creatures).\n" +
                "Saves bandwidth and hardens server security.\n" +
                "SERVER-ONLY \u2014 no effect on client.");

            ConfigEnableShipFixes = Config.Bind(
                "11 - Ship Fixes",
                "Enable Universal Ship Fixes",
                true,
                "Apply permanent autopilot + jitter fixes to ALL ships.");

            ConfigEnableServerSideShipSimulation = Config.Bind(
                "11 - Ship Fixes",
                "Server-Side Ship Simulation",
                false,
                "Server authoritatively simulates ship physics.\n" +
                "Disabled by default \u2014 enable manually if you want the server to drive ship physics.\n" +
                "\n" +
                "This also gates whether Selective (V3) ZDO ownership claims ships. Ownership IS simulation:\n" +
                "whoever owns a hull runs its Rigidbody, and vanilla only applies impact damage on the owner,\n" +
                "so a server that owns an empty boat can collide it against its own streaming colliders and\n" +
                "damage it on flat water. Left OFF, ships stay peer-owned and the sailing client simulates\n" +
                "them exactly as in vanilla, while creatures still move to the server.\n" +
                "NOTE: the BROAD (V2) ownership toggle ignores this and claims ships regardless.");

            ConfigEnableRpcAoI = Config.Bind(
                "10 - Server Authority",
                "Enable RPC Area-of-Interest",
                true,
                "When enabled, broadcast RPCs targeting a specific ZDO are only forwarded to peers\n" +
                "within the configured radius of that ZDO. Massively reduces bandwidth on busy servers.\n" +
                "RPCs without a target ZDO (global RPCs) are always broadcast to all peers.\n" +
                "Requires Enable RPC Router = true. SERVER-ONLY.");

            ConfigRpcAoIRadius = Config.Bind(
                "10 - Server Authority",
                "RPC AoI Radius",
                256f,
                new ConfigDescription(
                    "Distance (meters) from the target ZDO within which peers will receive the RPC.\n" +
                    "Vanilla active area is ~7 zones × 64m = ~448m. Default 256m covers nearby players.\n" +
                    "Set higher if players report missing interactions at distance.",
                    new AcceptableValueRange<float>(64f, 1024f)));

            ConfigClientMaxDestroysPerFrame = Config.Bind(
                "02 - Client Performance",
                "Max Destroys Per Frame",
                200,
                new ConfigDescription(
                    "Maximum ZNetScene instances the client destroys per CreateDestroyObjects pass.\n" +
                    "Vanilla destroys every out-of-area instance in a single frame, producing a\n" +
                    "multi-second hitch when leaving a heavily-loaded zone (e.g. ~150k instances\n" +
                    "on a megabase). The throttle defers the surplus to subsequent frames so the\n" +
                    "departure cost spreads out smoothly.\n" +
                    "0 = no throttle (vanilla single-frame destruction).\n" +
                    "200 = ~12s to clear a 150k-instance backlog at 60 fps, with each frame's\n" +
                    "destroy cost roughly equal to instantiating 200 objects.\n" +
                    "CLIENT-ONLY — no effect on dedicated server.",
                    new AcceptableValueRange<int>(0, 5000)));

            ConfigDediFellOutRescueLayers = Config.Bind(
                "10 - Server Authority",
                "Dedi Fell-Out Rescue Layers",
                "Default,static_solid,Default_small,piece,terrain,vehicle",
                new ConfigDescription(
                    "Comma-separated Unity layer names the dedi raycasts against when rescuing a mob\n" +
                    "that fell below the kill plane (y < -5000). Default mirrors vanilla BaseAI's\n" +
                    "solid-ray mask so any vanilla collider catches the mob. If your modlist ships\n" +
                    "custom terrain on a non-vanilla layer (RPGmaker overlay, etc.) and you see mobs\n" +
                    "permanently parked instead of recovering, add that layer name here. The cache\n" +
                    "refreshes when this value changes; no restart needed.",
                    null));

            ConfigFixGroundSnapThroughFloors = Config.Bind(
                "12 - Advanced",
                "Fix Ground Snap Through Floors",
                true,
                new ConfigDescription(
                    "Vanilla decides whether something has fallen out of the world by comparing it to the\n" +
                    "HEIGHTMAP, which cannot see build pieces. Digging moves the heightmap, so pits and mines\n" +
                    "are fine, but anything resting on a piece BELOW the heightmap is not: a tame penned on a\n" +
                    "cellar floor under a mound, or a gravestone in that cellar, reads as under the world and\n" +
                    "is teleported up to the dirt, through the floor that was holding it. With this on, that\n" +
                    "one check asks what is actually underneath first (see 'Dedi Fell-Out Rescue Layers' for\n" +
                    "the surfaces that count) and only falls back to the heightmap when nothing is there.\n" +
                    "Affects Character.UnderWorldCheck and TombStone.PositionCheck. Costs a single raycast,\n" +
                    "and only in the moment vanilla was about to teleport something.",
                    null));

            ConfigShowAILODInServerStatus = Config.Bind(
                "01 - General",
                "Show AILOD in ServerStatus",
                true,
                "When ON, appends the AILOD throttle's per-window stats (mobs examined,\n" +
                "near/mid/far decision counts, nearest-peer distance range) to the\n" +
                "consolidated [ServerStatus] line. Useful for tuning the near/far gates.\n" +
                "Turn OFF once tuning is validated and you want a quieter log.\n" +
                "Has no effect when Enable AI LOD Throttling is OFF — there's nothing to report.");

            ConfigDiagnosticIntervalSec = Config.Bind(
                "01 - General",
                "Diagnostic Rollup Interval (sec)",
                60f,
                new ConfigDescription(
                    "How often the consolidated [ServerStatus] line is logged on a dedicated server.\n" +
                    "Summarises MonoUpdaters tick rate, AI list sizes, BaseAI/MonsterAI tick counts,\n" +
                    "ZNetScene instantiation, our CreateDestroyObjects / IsActiveAreaLoaded gate\n" +
                    "behavior, and ServerOwnership transfer/release churn in a single line.\n" +
                    "Lower = more visibility, more log spam. Higher = quieter. Change takes effect on the next window.",
                    new AcceptableValueRange<float>(10f, 3600f)));

            ConfigEnableBulkTransferBoost = Config.Bind(
                "04 - Networking",
                "Enable Bulk Transfer Queue Boost",
                true,
                new ConfigDescription(
                    "When ON, reflectively patches the 20 KB GetSendQueueSize gate inside every loaded "
                    + "ServerSync.ConfigSync (AzuEPI, Marketplace, EpicLoot, Wizardry, EW Data, etc.) AND "
                    + "ServerCharacters.Shared copy so their fragment loops keep up with our raised ZDOMan cap.\n"
                    + "Replaces the old BulkTransferGuard which suppressed ZDOMan.SendZDOs during bulk bursts "
                    + "and incidentally starved player position ZDOs (the source of 'players flying / teleporting / "
                    + "hits from across the map' complaints).\n"
                    + "Disable as a kill switch if you suspect the scan is causing freezes or false-positive patching.\n"
                    + "Both sides — applies on client and dedicated server.",
                    null));

            ConfigBulkTransferBudgetPercent = Config.Bind(
                "04 - Networking",
                "Bulk Transfer Budget Percent",
                40,
                new ConfigDescription(
                    "Caps how much of the Steam per-connection send buffer the raised ServerSync gates may "
                    + "collectively claim, so many ServerSync mods (Azu/EpicLoot/Marketplace/EW/etc.) can't "
                    + "stack their raised gates and overflow the buffer (the cause of the heavy-area peer "
                    + "disconnects). The per-mod gate is computed as min(Queue Size, (buffer * this% / "
                    + "ServerSync-mod-count)), floored at the vanilla 20 KB so it never throttles tighter than "
                    + "stock. The buffer is 512 KB by default, or the larger value set when FiresSteamworksPatcher "
                    + "is installed. 40% leaves headroom for ZDO + RPC traffic. Lower if you still see heavy-area "
                    + "disconnects; raise if config syncs feel slow on join. Only matters when Bulk Transfer Queue "
                    + "Boost is ON.",
                    new AcceptableValueRange<int>(10, 80)));

            ConfigEnableServerAuthority = Config.Bind(
                "10 - Server Authority",
                "Enable Server-Side Simulation",
                false,
                new ConfigDescription(
                    "Makes the server fully authoritative over zones, ZDO ownership, monster AI, events, etc. (does NOT override your existing ship fixes).\n" +
                    "\n" +
                    "The RPC router and RPC area-of-interest, ZDO delta compression, ZDO throttling, AI LOD and the\n" +
                    "WearNTear server optimization do not need this: each follows its own toggle either way.\n" +
                    "\n" +
                    "Disabled by default \u2014 enable manually on your DEDICATED SERVER if desired.\n" +
                    "\n" +
                    "WARNING: THIS IS A SERVER-ONLY FEATURE!\n" +
                    "Enabling this on a CLIENT will cause INFINITE LOADING SCREEN.\n" +
                    "The mod automatically disables it on clients regardless of this setting.",
                    null));

            ConfigEnableServerOwnership = Config.Bind(
                "10 - Server Authority",
                "Enable Server ZDO Ownership Transfer (EXPERIMENTAL)",
                false,
                new ConfigDescription(
                    "EXPERIMENTAL — defaults OFF. Direct port of the original Serverside Simulations mod's\n" +
                    "ZDOMan.ReleaseNearbyZDOS prefix. When enabled, the server takes ownership of EVERY\n" +
                    "persistent ZDO in any peer's active area (mobs, ships, terrain, structures, items, doors,\n" +
                    "the whole world). Peers no longer own anything.\n" +
                    "\n" +
                    "WHEN TO ENABLE: you've validated the rest of your modlist plays nicely with broad\n" +
                    "server ownership and you want maximum server-side simulation (offloads client CPU,\n" +
                    "flips the bandwidth pattern so server send dominates).\n" +
                    "\n" +
                    "WHEN TO LEAVE OFF: you're running a heavy modpack, you've seen mob freezes or\n" +
                    "interaction issues after a previous attempt, or you don't know yet. Leaving this off\n" +
                    "preserves vanilla peer ownership — every other server-authority feature (zones,\n" +
                    "spawning, raids, throttling) still works without it.\n" +
                    "\n" +
                    "REQUIRES: ConfigEnableServerAuthority = true AND running on a dedicated server.\n" +
                    "TOGGLE IS INDEPENDENT — flip this without touching ConfigEnableServerAuthority.",
                    null));

            ConfigEnableServerOwnershipSelective = Config.Bind(
                "10 - Server Authority",
                "Enable Server ZDO Ownership Transfer — Selective (EXPERIMENTAL)",
                false,
                new ConfigDescription(
                    "EXPERIMENTAL — defaults OFF. Selective variant of the broad ownership transfer above.\n" +
                    "Only Character (non-Player) and Ship prefabs are claimed by the server. Drops,\n" +
                    "containers, doors, signs, workstations, pickables, beds, traders, wards, voxel\n" +
                    "terrain, built structures, and carts all stay under vanilla peer ownership.\n" +
                    "\n" +
                    "RATIONALE: broad SSS-style ownership (the toggle above) exposes interaction-RPC\n" +
                    "race conditions — `removedrops` failing for mob-dropped items, voxel mining/flattening\n" +
                    "breaking intermittently under load, carts shaking/sinking when parked. Selective scope\n" +
                    "avoids all three by keeping interactables on the vanilla peer-owned path.\n" +
                    "\n" +
                    "MUTUALLY EXCLUSIVE with the broad toggle above. If both are true, this selective\n" +
                    "variant takes precedence (the safer choice — drops/voxel/etc. stay working).\n" +
                    "\n" +
                    "REQUIRES: ConfigEnableServerAuthority = true AND running on a dedicated server.",
                    null));

            ConfigExtendedZoneRadius = Config.Bind(
                "10 - Server Authority",
                "Extended Zone Radius",
                1,
                new ConfigDescription(
                    "Additional zone layers the server pre-loads around players for smoother zone transitions.\n" +
                    "0 = vanilla (no extra pre-load)\n" +
                    "1 = +1 layer (recommended, ~7x7 zones total)\n" +
                    "2 = +2 layers (~9x9 zones)\n" +
                    "3 = +3 layers (~11x11 zones)\n" +
                    "\n" +
                    "Higher values reduce stutter when crossing zone borders but increase server CPU/RAM usage.\n" +
                    "SERVER-ONLY — clients ignore this setting.",
                    new AcceptableValueRange<int>(0, 3)));

            // ---- Predictive zone pre-streaming (Workstream C) ----
            ConfigEnablePredictiveZoneStreaming = Config.Bind(
                "10 - Server Authority",
                "Enable Predictive Zone Streaming",
                true,
                "Bias each peer's active-area center forward along their velocity vector so the\n" +
                "server starts loading zones BEFORE the peer crosses the boundary. Composes with\n" +
                "'Extended Zone Radius' — the symmetric ring still expands, the center just slides\n" +
                "forward. By the time the peer arrives, the predicted zones are already loaded.\n" +
                "SERVER-ONLY — clients ignore this setting.");

            ConfigPredictionLookaheadSec = Config.Bind(
                "10 - Server Authority",
                "Prediction Lookahead Sec",
                3.0f,
                new ConfigDescription(
                    "How many seconds ahead the server projects each peer's position when deciding\n" +
                    "which zones to pre-load. 3 s gives one zone of headroom at running speed.",
                    new AcceptableValueRange<float>(0.5f, 10f)));

            ConfigPredictionMinVelocity = Config.Bind(
                "10 - Server Authority",
                "Prediction Min Velocity",
                2.0f,
                new ConfigDescription(
                    "Minimum smoothed speed (m/s) below which prediction is suppressed and the peer's\n" +
                    "real position is used. Stops idle / slow-walking peers from triggering pre-load.",
                    new AcceptableValueRange<float>(0.5f, 20f)));

            ConfigPredictionMaxLookaheadZones = Config.Bind(
                "10 - Server Authority",
                "Prediction Max Lookahead Zones",
                9,
                new ConfigDescription(
                    "Hard cap on prediction distance, expressed in zones (64 m each). Stops a\n" +
                    "teleporting / glitching peer from subscribing to zones across the world.",
                    new AcceptableValueRange<int>(1, 25)));

            // NEW: ZDO Throttling (server-only bandwidth optimization)
            ConfigEnableZDOThrottling = Config.Bind(
                "10 - Server Authority",
                "Enable ZDO Throttling",
                true,
                "Reduce update frequency for distant ZDOs (creatures/structures far away) to save bandwidth.\n" +
                "SERVER-ONLY — no effect on client.");

            ConfigZDOThrottleDistance = Config.Bind(
                "10 - Server Authority",
                "ZDO Throttle Distance",
                500f,
                new ConfigDescription(
                    "Distance (meters) beyond which ZDOs are throttled (lower update rate).\n" +
                    "0 = disable throttling.\n" +
                    "Recommended: 400-600m.",
                    new AcceptableValueRange<float>(0f, 1000f)));

            // NEW: AI LOD Throttling (server-only CPU optimization)
            ConfigEnableAILOD = Config.Bind(
                "10 - Server Authority",
                "Enable AI LOD Throttling",
                true,
                "Reduce FixedUpdate frequency for distant AI (saves server CPU).\n" +
                "Nearby AI stays full speed for smooth combat.\n" +
                "SERVER-ONLY — no effect on client.");

            ConfigAILODNearDistance = Config.Bind(
                "10 - Server Authority",
                "AI LOD Near Distance",
                100f,
                new ConfigDescription("Full-speed AI within this range (meters).", new AcceptableValueRange<float>(50f, 200f)));

            ConfigAILODFarDistance = Config.Bind(
                "10 - Server Authority",
                "AI LOD Far Distance",
                300f,
                new ConfigDescription("Beyond this distance, AI is throttled (meters).", new AcceptableValueRange<float>(200f, 600f)));

            ConfigAILODThrottleFactor = Config.Bind(
                "10 - Server Authority",
                "AI LOD Throttle Factor",
                0.5f,
                new ConfigDescription("Update multiplier for throttled AI (0.5 = half speed, 0.25 = quarter). Lower = more savings.", new AcceptableValueRange<float>(0.25f, 0.75f)));

            // Adaptive gate — the optimizations above only run when a peer's send queue
            // is actually backing up. Healthy server = vanilla behaviour (smoother).
            ConfigEnableAdaptiveThrottling = Config.Bind(
                "10 - Server Authority",
                "Adaptive Throttling",
                true,
                "Only engage FGN's send-side optimizations (distant-ZDO throttling, player\n" +
                "boost, AI LOD) when a peer's send queue is actually backing up. On a server\n" +
                "with bandwidth to spare, FGN leaves vanilla update order untouched — leaner\n" +
                "and lower-latency. Turn OFF to force the optimizations on at all times.\n" +
                "SERVER-ONLY.");

            ConfigSendCongestionThresholdPct = Config.Bind(
                "10 - Server Authority",
                "Congestion Threshold",
                50,
                new ConfigDescription(
                    "How full a peer's send queue must get (percent of cap) before Adaptive\n" +
                    "Throttling engages the optimizations. Lower = engages sooner.\n" +
                    "SERVER-ONLY.",
                    new AcceptableValueRange<int>(10, 100)));

            ConfigEnableSendHeartbeatLog = Config.Bind(
                "12 - Advanced",
                "Log Send Queue Heartbeat",
                false,
                "Diagnostic: log each peer's send-queue health every 10s on a dedicated\n" +
                "server. Useful when investigating lag, but writes ~1 line per player per\n" +
                "10s to the log. Leave OFF for normal play. SERVER-ONLY.");

            ZDOMemoryManager.ConfigMaxZDOs = Config.Bind(
                "12 - Advanced",
                "Max Active ZDOs",
                500000,
                new ConfigDescription(
                    "If the number of active ZDOs exceeds this value, the mod will force cleanup of orphan non-persistent ZDOs and run garbage collection.\n" +
                    "Set to 0 to disable. Useful on very long-running servers with high entity counts.\n" +
                    "Default: 500000 (vanilla rarely goes above ~200k).",
                    new AcceptableValueRange<int>(0, 1000000)));

            ConfigEnableBootPatchVerification = Config.Bind(
                "12 - Advanced",
                "Enable Boot Patch Verification",
                false,
                new ConfigDescription(
                    "OFF by default. When ON, logs every Harmony patch attached to the AI tick,\n" +
                    "instantiation, and zone-gate methods this mod cares about — useful when\n" +
                    "troubleshooting mod-conflict scenarios (another mod's transpiler stomping our\n" +
                    "prefix, etc.) or when bringing up a new feature.\n" +
                    "\n" +
                    "The runtime [ServerStatus] rollup is the ongoing health indicator; this toggle\n" +
                    "is only useful when you suspect Harmony itself didn't attach something.",
                    null));

            ConfigEnableLargeZdoDiagnostics = Config.Bind(
                "12 - Advanced",
                "Enable Large ZDO Diagnostics",
                false,
                new ConfigDescription(
                    "OFF by default. When ON, adds enriched [BigZdoDiag] log lines next to vanilla's\n" +
                    "existing 'Writing a lot of data; X items, is not optimal' warning so you can see\n" +
                    "which ZDO is bloating its extra-data buckets (uid, prefab name, world position,\n" +
                    "owner peer id, bucket type and count).\n" +
                    "\n" +
                    "NOTE: the underlying warning is emitted by vanilla Valheim, not by this mod.\n" +
                    "This toggle only controls whether we ATTACH context to it. Turn this on when you\n" +
                    "see the vanilla warning and want to track down the offending entity; leave it off\n" +
                    "otherwise so the log stays quiet.",
                    null));

            ConfigEnableZDODelta = Config.Bind(
                "12 - Advanced",
                "Enable ZDO Delta Compression",
                true,
                "On re-syncs, only send ZDO fields that changed since last send to each peer.\n" +
                "Initial sync always sends full ZDO state. Re-syncs only send the diff.\n" +
                "Significant bandwidth reduction for high-field ZDOs (creatures, players) where\n" +
                "only 1-2 fields change per tick (e.g. health, position).\n" +
                "SERVER-ONLY — no effect on client.");

            ConfigEnableWNTServerOptimization = Config.Bind(
                "12 - Advanced",
                "Enable WearNTear Server Optimization",
                true,
                "Skips the wear and support update for building pieces whose damage modifiers are all\n" +
                "Immune or Ignore (Infinity Hammer, admin-flagged pieces), on the server that owns them.\n" +
                "Every other piece updates exactly as in vanilla.\n" +
                "SERVER-ONLY — no effect on client.");

            ConfigEnableInvulnerableSupportSkip = Config.Bind(
                "12 - Advanced",
                "Enable Invulnerable Support Skip",
                true,
                "CLIENT-side counterpart to the WearNTear server optimization. Short-circuits the\n" +
                "expensive WearNTear.UpdateSupport call (Physics.OverlapBoxNonAlloc per piece) for\n" +
                "pieces whose damage modifiers are all Immune/Ignore — e.g. Infinity Hammer pieces.\n" +
                "Pins m_support at the material's max value so neighbouring mortal pieces still\n" +
                "see full support when querying. Massive steady-state CPU saving in megabases\n" +
                "dominated by invulnerable pieces.");

            ConfigEnableInstanceOrphanPrune = Config.Bind(
                "12 - Advanced",
                "Enable Instance Orphan Prune",
                true,
                "SERVER-ONLY defensive cleanup. Before vanilla ZNetScene.RemoveObjects walks\n" +
                "m_instances on the dedicated server, scan for entries whose ZNetView is\n" +
                "Unity-destroyed OR whose view.GetZDO() returns null, and remove just the dict\n" +
                "entry (the GameObject is left alone). These are exactly the entries that would\n" +
                "NRE vanilla RemoveObjects, so we're only purging things vanilla can't handle.\n" +
                "Triggered by mods that block WearNTear.RPC_Remove on the server while ZDOMan\n" +
                "still reaps the ZDO via a separate path — e.g. TargetPortalProtection's\n" +
                "Player.m_localPlayer-based permission check on a headless dedi when ZDO\n" +
                "ownership has been moved to the server (FGN's Server-Side Simulation).\n" +
                "\n" +
                "Every prune is logged at warning level (rate-limited to once per 5s) and the\n" +
                "per-window count appears in the [ServerStatus] CDO segment. If you see this\n" +
                "firing repeatedly on a healthy server, another mod is mismanaging ZNetScene\n" +
                "state and should be investigated. Disable as a kill switch if it ever causes\n" +
                "trouble (you'd then see the original NRE caught by the existing fallback).");

            ConfigFixTeleportGhosts = Config.Bind(
                "12 - Advanced",
                "Fix Teleport Ghost Players",
                true,
                "Fixes a Valheim 1.0 bug. When something jumps out of a player's area in one step\n" +
                "(a portal, a teleport command), the server tests the position it is LEAVING, never tells\n" +
                "that player, and they keep seeing the traveller frozen where they left until it next\n" +
                "crosses a zone line. Re-runs vanilla's own check once the new position has landed.\n" +
                "Stands down on its own when the game or another mod already fixes it, e.g.\n" +
                "ValheimCommunityPatch's 'Fix Teleport Ghost Players' (deferred to while that is on).\n" +
                "SERVER / LISTEN-HOST ONLY. No effect on a connecting client.");

            ConfigFixSlowSleep = Config.Bind(
                "12 - Advanced",
                "Fix Slow Sleep On Busy Servers",
                true,
                "Vanilla moves the sleep time skip forward by one physics step per rendered frame, so the\n" +
                "skip meant to last 12 seconds takes longer the lower the server's frame rate. A busy server\n" +
                "can keep everyone in bed for a minute or more. With this on, the skip follows real time and\n" +
                "morning arrives after about 12 seconds however busy the server is.\n" +
                "SERVER / LISTEN-HOST ONLY. No effect on a connecting client.");

            ConfigFixBoatDamageFromTimeSync = Config.Bind(
                "12 - Advanced",
                "Fix Boat Damage From Server Time Sync",
                true,
                "Every 2 seconds the server corrects each player's clock, and vanilla applies the correction at once.\n" +
                "Waves are worked out from that clock, so every correction moves the water under a ship in a single\n" +
                "physics step, and a boat with players aboard takes the jump as slamming into the water and loses hull\n" +
                "health. The more a connection's timing wobbles, the bigger and more frequent the hits. With this on,\n" +
                "waves follow a clock that eases each correction in over a few seconds, so the water never jumps.\n" +
                "Corrections of 5 seconds or more (sleeping, reconnecting) still apply at once, as in vanilla.\n" +
                "CLIENT-SIDE. A ship is damaged by the game of the player who owns it, normally someone aboard,\n" +
                "so every player who sails needs this on. No effect on a dedicated server.");

            ConfigEnableFallThroughDiagnostics = Config.Bind(
                "01 - General",
                "Enable Fall-Through Diagnostics",
                false,
                "Verbose, opt-in diagnostics for investigating items and tombstones sinking through\n" +
                "structures. Off by default. When on, two probes run: a per-spawn probe that raycasts\n" +
                "under each dropped item/tombstone and logs the ones at risk (and any that actually\n" +
                "fall), and a one-shot startup audit that names build pieces left non-Solid (the load\n" +
                "order that lets an item spawn before its support). The per-spawn probe adds real cost\n" +
                "and log volume on a busy server, so leave this OFF for normal play and the live read —\n" +
                "turn it on only to investigate a suspected fall-through. These probes only observe and\n" +
                "log; they do not change any physics.");

            // === CONFIG CHANGE LOGGING (fixed for generic types) ===
            var allConfigs = new ConfigEntryBase[]
            {
        ConfigLogLevel,
        ConfigEnableCompression,
        ConfigUpdateRate,
        ConfigSendRateMin,
        ConfigSendRateMax,
        ConfigQueueSize,
        ConfigForceCrossplay,
        ConfigPlayerLimit,
        ConfigAdvertisedPlayerLimit,
        ConfigEnableShipFixes,
        ConfigEnableServerSideShipSimulation,
        ConfigEnableRpcRouter,
        ConfigEnableRpcAoI,
        ConfigRpcAoIRadius,
        ConfigEnableServerAuthority,
        ConfigExtendedZoneRadius,
        ConfigEnableZDOThrottling,
        ConfigZDOThrottleDistance,
        ConfigEnableAILOD,
        ConfigAILODNearDistance,
        ConfigAILODFarDistance,
        ConfigAILODThrottleFactor,
        ZDOMemoryManager.ConfigMaxZDOs,
        ConfigEnableZDODelta,
        ConfigShowAILODInServerStatus,
        ConfigEnableBootPatchVerification,
        ConfigEnableLargeZdoDiagnostics,
        ConfigEnableWNTServerOptimization,
        ConfigZoneLoadBatchSize,
        ConfigZPackageReceiveBufferSize,
        ConfigEnableTimeSliceInstantiation,
        ConfigInstantiationBudgetMs,
        ConfigMaxInstancesPerFrame,
        ConfigSafetyFallbackEnabled,
        ConfigSafetyFallbackThreshold,
        ConfigEnablePredictiveZoneStreaming,
        ConfigPredictionLookaheadSec,
        ConfigPredictionMinVelocity,
        ConfigPredictionMaxLookaheadZones,
        ConfigEnableInvulnerableSupportSkip,
        ConfigEnableInstanceOrphanPrune,
        ConfigFixTeleportGhosts,
        ConfigFixSlowSleep,
        ConfigFixBoatDamageFromTimeSync,
        ConfigEnableFallThroughDiagnostics,
        ConfigEnableBulkTransferBoost,
        ConfigBulkTransferBudgetPercent,
        ConfigEnableServerOwnership,
        ConfigEnableServerOwnershipSelective
            };
            // Note: AutoTune.* configs are bound later (in Awake, after BindConfigs returns),
            // so they don't get change-logger hooks — their own log lines cover that.

            foreach (var baseCfg in allConfigs)
            {
                var cfgType = baseCfg.GetType();
                var settingChanged = cfgType.GetEvent("SettingChanged");
                if (settingChanged != null)
                {
                    var handler = new EventHandler((sender, __) =>
                    {
                        string side = (ZNet.instance != null && ZNet.instance.IsServer()) ? "SERVER" : "CLIENT";
                        var cfg = (ConfigEntryBase)sender;
                        LoggerOptions.LogInfo($"[{side}] Config changed: {cfg.Definition.Section} → {cfg.Definition.Key} = {cfg.BoxedValue}");
                    });
                    settingChanged.AddEventHandler(baseCfg, handler);
                }
            }

            // Force default to false on clients
            if (ZNet.instance != null && !ZNet.instance.IsServer())
            {
                ConfigEnableServerAuthority.Value = false;
            }
        }
    }

    // ====================== ALL ENUMS DEFINED HERE ======================
    public enum LogLevel
    {
        [Description("Errors/Warnings only")]
        Warning,
        [Description("Errors/Warnings/Messages [default]")]
        Message,
        [Description("Everything including Info")]
        Info
    }

    public enum UpdateRateOptions
    {
        [Description("150% - 30 network sends/sec [smoother, more bandwidth]")]
        _150,
        [Description("100% - 20 network sends/sec [default, vanilla]")]
        _100,
        [Description("75% - 15 network sends/sec")]
        _75,
        [Description("50% - 10 network sends/sec")]
        _50
    }

    public enum SendRateMinOptions
    {
        [Description("1024 KB/s | 8 Mbit/s")]
        _1024KB,
        [Description("768 KB/s | 6 Mbit/s")]
        _768KB,
        [Description("512 KB/s | 4 Mbit/s")]
        _512KB,
        [Description("256 KB/s | 2 Mbit/s [default]")]
        _256KB,
        [Description("150 KB/s | 1.2 Mbit/s [vanilla]")]
        _150KB
    }

    public enum SendRateMaxOptions
    {
        [Description("8192 KB/s | 64 Mbit/s")]
        _8192KB,
        [Description("4096 KB/s | 32 Mbit/s")]
        _4096KB,
        [Description("2048 KB/s | 16 Mbit/s")]
        _2048KB,
        [Description("1024 KB/s | 8 Mbit/s")]
        _1024KB,
        [Description("768 KB/s | 6 Mbit/s")]
        _768KB,
        [Description("512 KB/s | 4 Mbit/s [default]")]
        _512KB,
        [Description("256 KB/s | 2 Mbit/s")]
        _256KB,
        [Description("150 KB/s | 1.2 Mbit/s [vanilla]")]
        _150KB
    }

    public enum QueueSizeOptions
    {
        [Description("80 KB")]
        _80KB,
        [Description("64 KB")]
        _64KB,
        [Description("48 KB")]
        _48KB,
        [Description("32 KB [default]")]
        _32KB,
        [Description("Vanilla (~10 KB)")]
        _vanilla
    }

    public enum ForceCrossplayOptions
    {
        [Description("Vanilla behaviour - respect -crossplay flag [default]")]
        vanilla,
        [Description("Force crossplay ENABLED (use PlayFab backend)")]
        playfab,
        [Description("Force crossplay DISABLED (use Steamworks backend)")]
        steamworks
    }
}
