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
    [BepInDependency(ValheimPerformanceOptimizationsGuid, BepInDependency.DependencyFlags.SoftDependency)]

    public class FiresGhettoNetworkMod : BaseUnityPlugin
    {
        public const string PluginGUID = "com.Fire.FiresGhettoNetworkMod";
        public const string PluginName = "FiresGhettoNetworkMod";
        public const string PluginVersion = "1.5.28";

        // ValheimPerformanceOptimizations (ontrigger) replaces ZNetScene.CreateDestroyObjects (always skipping vanilla, around
        // ZNet's reference position) and ZDOMan.ReleaseNearbyZDOS, the two methods Server-Side Simulation and ZDO ownership
        // transfer replace on the server. Soft dependency = load order only, so it is in PluginInfos when Awake runs.
        public const string ValheimPerformanceOptimizationsGuid = "dev.ontrigger.vpo";
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
        // KB/s as a plain NUMBER, not a dropdown. Auto-Tune writes the exact measured value; a fixed
        // list forced it to snap down to the nearest option and threw away up to half a thin line.
        public static ConfigEntry<int> ConfigSendRateMin;
        public static ConfigEntry<int> ConfigSendRateMax;
        public const int DefaultSendRateMinKb = 512;
        public const int DefaultSendRateMaxKb = 2048;
        public const int SendRateKbLow  = 4;        // below this a line is unplayable anyway
        public const int SendRateKbHigh = 131072;   // 128 MB/s - above every Auto-Tune ceiling
        public static ConfigEntry<bool> ConfigAdaptiveUpload;
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
        public static ConfigEntry<bool> ConfigEnableCapeCrashDiagnostics;
        public static ConfigEntry<int> ConfigZoneLoadBatchSize;
        public static ConfigEntry<int> ConfigZPackageReceiveBufferSize;
        public static ConfigEntry<bool>  ConfigEnableTimeSliceInstantiation;
        public static ConfigEntry<int>   ConfigInstantiationBudgetMs;
        public static ConfigEntry<int>   ConfigMaxInstancesPerFrame;
        public static ConfigEntry<int>   ConfigServerInstantiationBudgetMs;
        public static ConfigEntry<int>   ConfigServerPassHz;
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
        public static ConfigEntry<bool> ConfigKeepaliveFirst;
        public static ConfigEntry<bool> ConfigKeepWorldClockAtRealTime;
        public static ConfigEntry<bool> ConfigSmoothServerClockCorrections;
        public static ConfigEntry<bool> ConfigFixGroundSnapThroughFloors;
        public static ConfigEntry<bool> ConfigEnableRpcRouter;
        public static ConfigEntry<bool> ConfigEnableRpcAoI;
        public static ConfigEntry<float> ConfigRpcAoIRadius;
        public static ConfigEntry<bool> ConfigEnableZDODelta;
        public static ConfigEntry<bool> ConfigAllocationFreeZdoWrites;
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
            LinkController.InitConfig(Config);
            SendScheduler.InitConfig(Config);
            StationRouter.InitConfig(Config);
            CreatureOwnership.InitConfig(Config);
            HelmOwnership.InitConfig(Config);
            TargetSync.InitConfig(Config);
            SectorChangeTracker.InitConfig(Config);
            FireplaceFuelTicks.InitConfig(Config);
            TransformWriteRate.InitConfig(Config);
            SyncListRefPosPatches.InitConfig(Config);
            JoinGrace.InitConfig(Config);
            PlayFabZlibWorker.InitConfig(Config);
            LagFairDodge.InitConfig(Config);
            LogoutHold.InitConfig(Config);
            RemoteMotion.InitConfig(Config);
            RemoteArrows.InitConfig(Config);

            TryPatchAll(typeof(CompressionGroup));
            TryPatchAll(typeof(NetworkingRatesGroup));
            TryPatchAll(typeof(DedicatedServerGroup));
            TryPatchAll(typeof(SyncListRefPosPatches));
            TryPatchAll(typeof(UploadBreakdown));
            TryPatchAll(typeof(DownloadBreakdown));

            TryPatchAll(typeof(SendZDOsHeartbeatDiagnostic));

            if (ConfigEnableFallThroughDiagnostics.Value)
            {
                TryPatchAll(typeof(FallThroughProbe));
                TryPatchAll(typeof(PieceTypeAudit));
            }

            TryPatchAll(typeof(ServerDisconnectDiagnostics));
            TryPatchAll(typeof(CloseWithoutSleep));
            PlayFabZlibWorker.Apply();

            TryPatchAll(typeof(BulkTransferGatePatches));

            TryPatchAll(typeof(BigZdoDiagnostic));

            TryPatchAll(typeof(FireplaceFuelTicks));
            TryPatchAll(typeof(TransformWriteRate));

            ZdoWireWriter.Initialize();
            TryPatchAll(typeof(ZdoWireWriter));

            if (ConfigEnableZDODelta.Value)
            {
                TryPatchAll(typeof(ZDODeltaPatches));
                LoggerOptions.LogInfo("ZDO delta compression enabled.");
            }

            TryPatchAll(typeof(DisarmOverloadTest));

            TryPatchAll(typeof(CompressionRoundTripTest));

            TryPatchAll(typeof(SocketStressTests));

            TryPatchAll(typeof(SendQueueHeadroomMonitor));

            TryPatchAll(typeof(LinkController));

            TryPatchAll(typeof(NetSim));

            TryPatchAll(typeof(LogoutHold));
            TryPatchAll(typeof(RemoteArrows));

            TryPatchAll(typeof(SendScheduler));

            TryPatchAll(typeof(ConnectionEcho));

            TryPatchAll(typeof(CreatureOwnership));

            TryPatchAll(typeof(HelmOwnership));

            TryPatchAll(typeof(TargetSync));

            TryPatchAll(typeof(KeepaliveFirst));

            TryPatchAll(typeof(JoinGrace));

            try
            {
                TryPatchAll(typeof(RoundTripTrace));
            }
            catch (System.Exception ex)
            {
                LoggerOptions.LogWarning($"[RoundTrip] could not be attached; round trips are still timed, without the per-stage breakdown. {ex.Message}");
            }

            TryPatchAll(typeof(ZdoFloodTest));

            WackyDatabaseCompatibilityPatch.Init(Harmony);



            TryPatchAll(typeof(PlayerPositionSyncPatches));

            TryPatchAll(typeof(WearNTearClientSupportPatches));

            TryPatchAll(typeof(ClientCleanupThrottle));

            TryPatchAll(typeof(GroundSnapPatches));

            TryPatchAll(typeof(OwnershipHandoffPatches));

            try
            {
                TeleportGhostFix.Init();
                TryPatchAll(typeof(TeleportGhostFix));
            }
            catch (System.Exception ex)
            {
                LoggerOptions.LogWarning($"[TeleportGhostFix] could not be attached; vanilla behaviour is unchanged. {ex.Message}");
            }

            try
            {
                TryPatchAll(typeof(TerrainCompInitRace));
            }
            catch (System.Exception ex)
            {
                LoggerOptions.LogWarning($"[TerrainComp] could not be attached; vanilla behaviour is unchanged. {ex.Message}");
            }

            TryPatchAll(typeof(SleepTimeSkipFix));

            if (!isDedicated)
                TryPatchAll(typeof(WorldClock));

            if (!isDedicated && ConfigEnableCapeCrashDiagnostics.Value)
            {
                TryPatchAll(typeof(CapeCrashDiagnostics));
                TryPatchAll(typeof(MagicaColliderRegistrationDiagnostics));
                TryPatchAll(typeof(MagicaClothLifecycleDiagnostics));
                LoggerOptions.LogWarning("[CapeDiag] Cape crash diagnostics are ON; turn 'Enable Cape Crash Diagnostics' off once the crash is found.");
            }

            // Auto-tune: probe on clients, self-tune on servers.
            TryPatchAll(typeof(AutoTuneProbeHooks));
            TryPatchAll(typeof(ZoneLoadPatches));
            ServerAutoTune.InitServerSide();

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
                else if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(ValheimPerformanceOptimizationsGuid))
                {
                    LoggerOptions.LogWarning(
                        "Server-Side Simulation (and ZDO ownership transfer) is OFF for this session: ValheimPerformanceOptimizations "
                        + "replaces the server's object create/destroy pass and its ownership pass with its own, built around the "
                        + "server's reference position, so it would undo what FGN does for players. Remove "
                        + "ValheimPerformanceOptimizations from the server to use Server-Side Simulation, or turn 'Enable Server-Side "
                        + "Simulation' off to silence this warning.");
                }
                else
                {
                    ApplyServerSideSimulationPatches();
                    ServerAuthorityPatches.SimulationActive = true;
                    simulationActive = true;
                }

                EmitServerFeatureSummary(simulationActive);
            }
            else
            {
                LoggerOptions.LogInfo("Server-side features skipped — not running on a dedicated server.");
            }

            foreach (Type hooks in FgnHooks.ReadAttachedFeatures())
                TryPatchAll(hooks);

            StartCoroutine(EmitCompactBannerWhenZNetReady());
        }

        /// <summary>Dedicated-server traffic shaping. None of it needs Server-Side Simulation; each feature follows its own toggle.</summary>
        private static void ApplyServerTrafficPatches()
        {
            TryPatchAll(typeof(ServerFrameProfile));
            TryPatchAll(typeof(SectorChangeTracker));
            TryPatchAll(typeof(ZDOThrottlingPatches));
            TryPatchAll(typeof(AILODPatches));

            // Attached whatever the toggles say, so 'Fix Lost Station Inserts' can be turned on live; the routed-RPC prefix
            // hands every message to vanilla while it and 'Enable RPC Router' are both off.
            TryPatchAll(typeof(RpcRouterPatches));
            TryPatchAll(typeof(StationRouter));
            DamageTextHandler.Register();

            if (ConfigEnableRpcRouter.Value || StationRouter.ConfigEnabled.Value)
            {
                VAGhettoLoadSummary.EmitRpcRouter(
                    handlersRegistered: RoutedRpcManager.HandlerCount,
                    aoiRadius: RoutedRpcManager.PositionRadius(),
                    aoiEnabled: RoutedRpcManager.FilteringEnabled());
                if (VAGhettoLoadSummary.VerboseEnabled)
                    LoggerOptions.LogInfo("RPC Router enabled — handlers: " + string.Join(", ", RoutedRpcManager.HandlerMethodNames) + ".");
            }

            if (ConfigEnableWNTServerOptimization.Value)
            {
                TryPatchAll(typeof(WearNTearServerPatches));
                LoggerOptions.LogInfo("WearNTear server optimization enabled.");
            }
        }

        /// <summary>The server instantiating, owning and driving the world around every peer.</summary>
        private static void ApplyServerSideSimulationPatches()
        {
            if (ConfigEnableShipFixes.Value)
                TryPatchAll(typeof(ShipFixesGroup));

            TryPatchAll(typeof(ServerShipSimulationPatches));

            TryPatchAll(typeof(ServerAuthorityPatches));
            TryPatchAll(typeof(ServerStabilityPatches));
            TryPatchAll(typeof(MonsterAIPatches));

            if (ConfigEnableServerOwnershipSelective.Value)
            {
                if (ConfigEnableServerOwnership.Value)
                {
                    LoggerOptions.LogWarning(
                        "Both 'Server ZDO Ownership Transfer' flags are ENABLED — selective (V3) takes precedence; broad (V2) is being ignored. Disable one to silence this warning.");
                }
                TryPatchAll(typeof(ServerOwnershipPatchesV3));
                CreatureOwnership.ServerOwnsCreatures = true;
                HelmOwnership.ServerOwnsShips = ConfigEnableServerSideShipSimulation.Value;
                LoggerOptions.LogMessage(
                    "Server ZDO ownership (V3 SELECTIVE) ENABLED — Character/Ship only; drops/voxel/interactables/carts stay peer-owned.");
            }
            else if (ConfigEnableServerOwnership.Value)
            {
                TryPatchAll(typeof(ServerOwnershipPatches));
                CreatureOwnership.ServerOwnsCreatures = true;
                HelmOwnership.ServerOwnsShips = true;
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

            // A dedicated server also prints the address players type into the join dialog, under the banner. Steam
            // reports the public IP shortly after the server logs on, so wait a little for it.
            string joinAddress = null;
            bool dedicated = ServerClientUtils.IsDedicatedServerDetected;
            if (dedicated)
            {
                float giveUpAt = Time.realtimeSinceStartup + JoinAddressWaitSeconds;
                while (!TryGetJoinAddress(out joinAddress) && Time.realtimeSinceStartup < giveUpAt)
                    yield return new WaitForSecondsRealtime(0.5f);
            }

            try { VAGhettoBanner.Print(); }
            catch (Exception ex)
            {
                Logger.LogInfo($"{PluginName} v{PluginVersion} loaded. (banner failed: {ex.Message})");
            }

            if (dedicated)
            {
                Logger.LogInfo(joinAddress != null
                    ? $"Join address: {joinAddress}"
                    : $"Join address: this server's public IP, port {ServerPort()} (Steam did not report the public IP within {JoinAddressWaitSeconds:0} s).");
            }
        }

        private const float JoinAddressWaitSeconds = 60f;

        private static bool TryGetJoinAddress(out string address)
        {
            address = null;
            int port = ServerPort();
            if (port <= 0) return false;
            try
            {
                if (!Steamworks.SteamGameServer.BLoggedOn()) return false;
                Steamworks.SteamIPAddress_t publicIp = Steamworks.SteamGameServer.GetPublicIP();
                if (!publicIp.IsSet()) return false;
                System.Net.IPAddress ip = publicIp.ToIPAddress();
                address = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{ip}:{port}";
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // FejdStartup reads -port (default 2456) and hands it to the Steam and PlayFab sockets. ZNet.GetHostPort is no
        // help here: on a Steam socket it only answers 1 for a host.
        private static int ServerPort()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-port", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int port) && port > 0)
                    return port;
            }
            return 2456;
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


        

        /// <summary>
        /// One patch class. Harmony compiles each patched method as it attaches, so a patch the game's IL can't take throws here;
        /// it is logged and that feature stays off, instead of the exception leaving Awake and skipping everything after it
        /// (1.4.81: the WorkerMain prefix threw, and every peer ran a half-initialised FGN).
        /// </summary>
        private static void TryPatchAll(Type type)
        {
            if (type == null)
            {
                Log?.LogError("Tried to patch a null type!");
                return;
            }
            try
            {
                Harmony.PatchAll(type);
                FgnHooks.NoteAttached(type);
            }
            catch (Exception ex)
            {
                Exception cause = ex.InnerException ?? ex;
                Log?.LogError($"[{PluginName}] couldn't attach {type.Name} ({cause.GetType().Name}: {cause.Message}); that feature is off, "
                    + "the rest of FGN loads normally.");
            }
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
                LoggerOptions.LogMessage($"[PatchVerify] {type.FullName}.{methodName} â†’ method NOT FOUND by AccessTools.");
                return;
            }
            var info = HarmonyLib.Harmony.GetPatchInfo(mi);
            if (info == null)
            {
                LoggerOptions.LogMessage($"[PatchVerify] {type.FullName}.{methodName} â†’ NO patches attached (info=null).");
                return;
            }
            int total = (info.Prefixes?.Count ?? 0)
                      + (info.Postfixes?.Count ?? 0)
                      + (info.Transpilers?.Count ?? 0)
                      + (info.Finalizers?.Count ?? 0);
            LoggerOptions.LogMessage($"[PatchVerify] {type.FullName}.{methodName} â†’ total={total} (prefixes={info.Prefixes?.Count ?? 0}, postfixes={info.Postfixes?.Count ?? 0}, transpilers={info.Transpilers?.Count ?? 0}, finalizers={info.Finalizers?.Count ?? 0}).");
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

        /// <summary>Carries a pre-KB/s "_2048KB" dropdown value across the type change, so BepInEx does
        /// not reject it and reset the player to the default. A no-op once the file holds a number.
        /// Plan: Docs/PLAN_UploadBudgetAndAutoTune.md</summary>
        private int MigrateLegacyKb(string section, string key, int defaultKb)
        {
            int legacyKb = -1;
            try
            {
                string path = Config.ConfigFilePath;
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                {
                    string current = null;
                    foreach (string raw in System.IO.File.ReadAllLines(path))
                    {
                        string line = raw.Trim();
                        if (line.StartsWith("[") && line.EndsWith("]")) { current = line.Substring(1, line.Length - 2).Trim(); continue; }
                        if (current != section || line.Length == 0 || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0 || line.Substring(0, eq).Trim() != key) continue;
                        var m = System.Text.RegularExpressions.Regex.Match(line.Substring(eq + 1).Trim(), @"^_(\d+)KB$");
                        if (m.Success) legacyKb = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    }
                }
            }
            catch (Exception ex) { Logger.LogWarning($"[Config] Could not read {key} for migration: {ex.Message}"); }

            if (legacyKb <= 0) return defaultKb;

            try
            {
                var prop = typeof(ConfigFile).GetProperty("OrphanedEntries",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (prop?.GetValue(Config) is System.Collections.Generic.Dictionary<ConfigDefinition, string> orphans)
                {
                    var def = new ConfigDefinition(section, key);
                    if (orphans.ContainsKey(def))
                        orphans[def] = legacyKb.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch { /* value still carried as the default below */ }

            Logger.LogInfo($"[Config] {key}: migrated from the old option _{legacyKb}KB to {legacyKb} KB/s.");
            return Mathf.Clamp(legacyKb, SendRateKbLow, SendRateKbHigh);
        }

        private void BindConfigs()
        {
            ConfigLogLevel = Config.Bind(
                "01 - General",
                "Log Level",
                LogLevel.Message,
                "How much FGN writes to the BepInEx log. Message is normal; Info adds detail for troubleshooting; Debug also adds the timed reports (traffic, server timings, ownership counts).");

            // ConfigManager sections render in first-Bind() order. The priming
            // binds + Init calls below set sections 02..08 to match the "NN - "
            // prefix. Bind each key once: a second Bind of the same key only
            // returns the same entry, and two copies drift apart when edited.
            ConfigZoneLoadBatchSize = Config.Bind(
                "02 - Client Performance",
                "Zone Load Batch Size",
                1,
                new ConfigDescription(
                    "Multiplies how many objects the game creates per frame while an area loads in (1 = vanilla).\n" +
                    "Higher loads areas faster but with bigger hitches. Used when 'Enable Time-Slice Instantiation' is off\n" +
                    "or behind the loading screen; Client Auto-Tune replaces it with its own value. Client only.",
                    new AcceptableValueRange<int>(1, 8)));

            PlayerPositionSyncPatches.Init(Config);

            ConfigEnableCompression = Config.Bind(
                "04 - Networking",
                "Enable Compression",
                true,
                "Compresses network traffic (recommended). It is agreed per connection, so it is only used with players and\n" +
                "servers that also run FGN with compression on. Install on both server and clients.");

            ConfigUpdateRate = Config.Bind(
                "04 - Networking",
                "ZDO Send Rate",
                UpdateRateOptions._100,
                "How often world updates are sent; 100% (20/s) is vanilla. Lower helps a slow upload, higher looks smoother if\n" +
                "bandwidth allows. While Auto-Tune is on it writes this value and your edits are replaced.");

            ConfigSendRateMin = Config.Bind(
                "05 - Networking - Steamworks",
                "Send Rate Min",
                MigrateLegacyKb("05 - Networking - Steamworks", "Send Rate Min", DefaultSendRateMinKb),
                new ConfigDescription(
                "Lowest Steam send rate in KB/s, held even when the connection struggles. Keep it well below your real upload\n" +
                "so there is room to back off. While Auto-Tune is on it writes this value and your edits are replaced.",
                new AcceptableValueRange<int>(SendRateKbLow, SendRateKbHigh)));

            ConfigSendRateMax = Config.Bind(
                "05 - Networking - Steamworks",
                "Send Rate Max",
                MigrateLegacyKb("05 - Networking - Steamworks", "Send Rate Max", DefaultSendRateMaxKb),
                new ConfigDescription(
                "Highest Steam send rate in KB/s for this PC (on a client, your upload ceiling). Adaptive Upload and Adaptive\n" +
                "Send Rate move the live rate between Min and Max. While Auto-Tune is on it writes this value and your edits are replaced.",
                new AcceptableValueRange<int>(SendRateKbLow, SendRateKbHigh)));

            ConfigAdaptiveUpload = Config.Bind(
                "04 - Networking",
                "Adaptive Upload",
                true,
                "Keeps your live send rate (between Send Rate Min and Max) under what your connection actually delivers, so a\n" +
                "busy line backs off instead of flooding. Off = Steam's own rate control. Steam connections only, not crossplay.");

            ConfigHyperBoost = Config.Bind(
                "05 - Networking - Steamworks",
                "HYPERBOOST",
                false,
                "Maximum-throughput mode: overrides Auto-Tune and Send Rate Min/Max and lifts Steam's send rate and buffers to\n" +
                "their limits, live. Set it on both server and client; the receive side needs FiresSteamworksPatcher.\n" +
                "Leave off for normal play; it is meant for very fast links and benchmarking.");

            AutoTuneConfig.Init(Config);

            ConfigQueueSize = Config.Bind(
                "04 - Networking",
                "Queue Size",
                QueueSizeOptions._32KB,
                "Largest batch of world updates sent to one player at a time, and each player's starting send window (the fixed\n" +
                "limit when 'Adaptive Send Window' is off). Crossplay players use 'Crossplay In-Flight KB' instead.\n" +
                "While Auto-Tune is on it writes this value and your edits are replaced.");

            ConfigForceCrossplay = Config.Bind(
                "09 - Dedicated Server",
                "Force Crossplay",
                ForceCrossplayOptions.vanilla,
                "Which network backend a dedicated server uses. vanilla = follow the -crossplay launch flag; steamworks = Steam only\n" +
                "(no Xbox / Game Pass / PlayStation players); playfab = crossplay on, players join with the printed join code.\n" +
                "Requires a restart. Leave clients on vanilla.");

            ConfigPlayerLimit = Config.Bind(
                "09 - Dedicated Server",
                "Player Limit",
                10,
                new ConfigDescription("Maximum players on a dedicated server. Ignored when the MaxPlayerCount mod is installed. Requires a restart.", new AcceptableValueRange<int>(1, 999)));

            ConfigAdvertisedPlayerLimit = Config.Bind(
                "09 - Dedicated Server",
                "Advertised Player Limit",
                0,
                new ConfigDescription(
                    "Player cap shown in server listings (Steam server browser, crossplay). Does not change the real limit set by " +
                    "'Player Limit'. 0 = show 'Player Limit'. Ignored when the MaxPlayerCount mod is installed. Requires a restart.",
                    new AcceptableValueRange<int>(0, 9999)));

            // ---- Client-side perf knobs (formerly stubbed; wired up by AutoTune patches) ----
            // Zone Load Batch Size is bound once, at the top of this method, where it primes section 02's position.

            ConfigZPackageReceiveBufferSize = Config.Bind(
                "02 - Client Performance",
                "ZPackage Receive Buffer Bytes",
                256 * 1024,
                new ConfigDescription(
                    "Steam receive buffer size in bytes, used when Auto-Tune is off. A bigger buffer rides out short stalls without\n" +
                    "losing data, at the cost of memory. Values below 2 MB (including the default) are raised to 2 MB.",
                    new AcceptableValueRange<int>(64 * 1024, 4 * 1024 * 1024)));

            // ---- Time-sliced instantiation (Workstream A) ----
            ConfigEnableTimeSliceInstantiation = Config.Bind(
                "02 - Client Performance",
                "Enable Time-Slice Instantiation",
                false,
                "Creates incoming objects within a time budget per frame instead of vanilla's fixed count, so walking into a busy\n" +
                "area spreads the work over several frames instead of one big hitch. Off = 'Zone Load Batch Size' is used.\n" +
                "Client only; the loading screen still uses vanilla.");

            ConfigInstantiationBudgetMs = Config.Bind(
                "02 - Client Performance",
                "Instantiation Budget Ms",
                3,
                new ConfigDescription(
                    "Milliseconds per frame spent creating objects when 'Enable Time-Slice Instantiation' is on. Lower = smoother\n" +
                    "frames while an area loads in; higher = it loads faster. Client Auto-Tune replaces it with its own value.",
                    new AcceptableValueRange<int>(1, 16)));

            ConfigMaxInstancesPerFrame = Config.Bind(
                "02 - Client Performance",
                "Max Instances Per Frame",
                100,
                new ConfigDescription(
                    "Most objects created in one frame under time-slicing, however much of the budget is left.\n" +
                    "Client Auto-Tune replaces it with its own value.",
                    new AcceptableValueRange<int>(10, 500)));

            ConfigSafetyFallbackEnabled = Config.Bind(
                "02 - Client Performance",
                "Safety Fallback Enabled",
                true,
                "Under time-slicing, triples the per-frame budget (up to 16 ms) while more than 'Safety Fallback Threshold'\n" +
                "objects are waiting, such as after teleporting into a large base. Client Auto-Tune replaces it with its own value.");

            ConfigSafetyFallbackThreshold = Config.Bind(
                "02 - Client Performance",
                "Safety Fallback Threshold",
                5000,
                new ConfigDescription(
                    "Number of objects waiting to be created at which 'Safety Fallback Enabled' widens the budget.\n" +
                    "Client Auto-Tune replaces it with its own value.",
                    new AcceptableValueRange<int>(100, 50000)));

            ConfigEnableRpcRouter = Config.Bind(
                "10 - Server Authority",
                "Enable RPC Router",
                true,
                "Lets the server relay the messages players broadcast (hits, effects, damage numbers) instead of vanilla sending\n" +
                "each one to everyone. Needed for 'Enable RPC Area-of-Interest'. Dedicated server only; requires a restart.");

            ConfigEnableShipFixes = Config.Bind(
                "11 - Ship Fixes",
                "Enable Universal Ship Fixes",
                true,
                "When the dedicated server simulates ships, keeps the speed and rudder the steering player set instead of\n" +
                "resetting them. Only applies with Server-Side Simulation on; requires a restart.");

            ConfigEnableServerSideShipSimulation = Config.Bind(
                "11 - Ship Fixes",
                "Server-Side Ship Simulation",
                false,
                "Lets Selective server ownership claim ships as well as creatures, so the dedicated server runs their physics.\n" +
                "Off = ships stay with the players as in vanilla. Broad ownership claims ships either way. Requires a restart.");

            ConfigEnableRpcAoI = Config.Bind(
                "10 - Server Authority",
                "Enable RPC Area-of-Interest",
                true,
                "Sends a message about an object only to players who have that object loaded, and damage numbers only to players\n" +
                "within 'RPC AoI Radius'; others would ignore them anyway. Needs 'Enable RPC Router'. Dedicated server only.");

            ConfigRpcAoIRadius = Config.Bind(
                "10 - Server Authority",
                "RPC AoI Radius",
                256f,
                new ConfigDescription(
                    "Meters around a player within which they are sent position-only messages such as damage numbers.\n" +
                    "Server Auto-Tune replaces it with its own value.",
                    new AcceptableValueRange<float>(64f, 1024f)));

            ConfigClientMaxDestroysPerFrame = Config.Bind(
                "02 - Client Performance",
                "Max Destroys Per Frame",
                200,
                new ConfigDescription(
                    "Most objects removed per frame when you leave an area. Vanilla removes them all in one frame, which can freeze\n" +
                    "the game for seconds after leaving a big base; this spreads it out. 0 = vanilla. Client only.",
                    new AcceptableValueRange<int>(0, 5000)));

            ConfigDediFellOutRescueLayers = Config.Bind(
                "10 - Server Authority",
                "Dedi Fell-Out Rescue Layers",
                "Default,static_solid,Default_small,piece,terrain,vehicle",
                new ConfigDescription(
                    "Comma-separated Unity layers that count as solid ground when FGN looks for a surface: rescuing a creature that\n" +
                    "fell out of the world on the server, and 'Fix Ground Snap Through Floors'. Add a modded terrain layer here if\n" +
                    "creatures fail to recover. Takes effect live.",
                    null));

            ConfigFixGroundSnapThroughFloors = Config.Bind(
                "12 - Advanced",
                "Fix Ground Snap Through Floors",
                true,
                new ConfigDescription(
                    "Stops vanilla teleporting creatures and tombstones up through a floor that sits below ground level (a cellar\n" +
                    "under a mound): before treating them as fallen out of the world, it checks for a real surface underneath.",
                    null));

            ConfigShowAILODInServerStatus = Config.Bind(
                "01 - General",
                "Show AILOD in ServerStatus",
                true,
                "Adds AI LOD throttling stats to the server's periodic [ServerStatus] log line. Turn off for a shorter log.");

            ConfigDiagnosticIntervalSec = Config.Bind(
                "01 - General",
                "Diagnostic Rollup Interval (sec)",
                60f,
                new ConfigDescription(
                    "Seconds between [ServerStatus] health lines in a dedicated server's log (written while Server-Side Simulation\n" +
                    "is on). Lower = more detail, more log.",
                    new AcceptableValueRange<float>(10f, 3600f)));

            ConfigEnableBulkTransferBoost = Config.Bind(
                "04 - Networking",
                "Enable Bulk Transfer Queue Boost",
                true,
                new ConfigDescription(
                    "Speeds up config syncing for mods built on ServerSync or ServerCharacters by raising their 20 KB send limit, and "
                    + "stops them dropping slow players after 30 seconds. Turn off if you suspect it conflicts with one of those mods. "
                    + "Server and client; requires a restart.",
                    null));

            ConfigBulkTransferBudgetPercent = Config.Bind(
                "04 - Networking",
                "Bulk Transfer Budget Percent",
                40,
                new ConfigDescription(
                    "Share of the Steam send buffer that all ServerSync mods together may use with 'Enable Bulk Transfer Queue Boost' "
                    + "on, so they cannot crowd out world updates. Lower it if players disconnect in busy areas; raise it if config "
                    + "sync on join is slow. Never goes below vanilla's 20 KB per mod.",
                    new AcceptableValueRange<int>(10, 80)));

            ConfigEnableServerAuthority = Config.Bind(
                "10 - Server Authority",
                "Enable Server-Side Simulation",
                false,
                new ConfigDescription(
                    "The server builds the world around every player so it can run creatures and ships there (see ownership below).\n" +
                    "Off with ValheimCommunityPatch or ValheimPerformanceOptimizations. Dedicated server; requires a restart.",
                    null));

            ConfigServerInstantiationBudgetMs = Config.Bind(
                "10 - Server Authority",
                "Server Instantiation Budget Ms",
                8,
                new ConfigDescription(
                    "With Server-Side Simulation on, milliseconds per frame the server may spend building objects around players,\n" +
                    "nearest first, so a player arriving in a dense base does not stall the server. 0 = vanilla (up to 100 objects a\n" +
                    "frame with no time limit). Dedicated server only.",
                    new AcceptableValueRange<int>(0, 50)));

            ConfigServerPassHz = Config.Bind(
                "10 - Server Authority",
                "Server Object Pass Hz",
                10,
                new ConfigDescription(
                    "With Server-Side Simulation on, how many times a second the server updates which objects exist around players\n" +
                    "(creating new ones, removing far ones). Fewer passes save server time in dense bases; objects appear or go up\n" +
                    "to one pass later. 0 = every frame. Dedicated server only.",
                    new AcceptableValueRange<int>(0, 30)));

            ConfigEnableServerOwnership = Config.Bind(
                "10 - Server Authority",
                "Enable Server ZDO Ownership Transfer (EXPERIMENTAL)",
                false,
                new ConfigDescription(
                    "The server takes over almost everything near players except carts and tames; can break pickup and terrain edits,\n" +
                    "so prefer Selective. Needs Server-Side Simulation; ignored when Selective is on. Dedicated server; restart.",
                    null));

            ConfigEnableServerOwnershipSelective = Config.Bind(
                "10 - Server Authority",
                "Enable Server ZDO Ownership Transfer — Selective (EXPERIMENTAL)",
                false,
                new ConfigDescription(
                    "The server runs wild creatures near players (and ships with 'Server-Side Ship Simulation'); everything else stays\n" +
                    "with players. Needs Server-Side Simulation; wins over the broad setting. Dedicated server; restart.",
                    null));

            ConfigExtendedZoneRadius = Config.Bind(
                "10 - Server Authority",
                "Extended Zone Radius",
                0,
                new ConfigDescription(
                    "With Server-Side Simulation on, extra rings of zones (64 m each) the server loads around every player. Higher\n" +
                    "means fewer hitches at zone borders but much more server CPU and memory per player. 0 = vanilla. Not used when\n" +
                    "the Render Limits mod is installed. Dedicated server only.",
                    new AcceptableValueRange<int>(0, 3)));

            // ---- Predictive zone pre-streaming (Workstream C) ----
            ConfigEnablePredictiveZoneStreaming = Config.Bind(
                "10 - Server Authority",
                "Enable Predictive Zone Streaming",
                true,
                "With Server-Side Simulation on, the server loads the area ahead of a moving player instead of around where they\n" +
                "stand, so it is ready when they arrive. Dedicated server only.");

            ConfigPredictionLookaheadSec = Config.Bind(
                "10 - Server Authority",
                "Prediction Lookahead Sec",
                3.0f,
                new ConfigDescription(
                    "How many seconds of travel ahead 'Enable Predictive Zone Streaming' looks.",
                    new AcceptableValueRange<float>(0.5f, 10f)));

            ConfigPredictionMinVelocity = Config.Bind(
                "10 - Server Authority",
                "Prediction Min Velocity",
                2.0f,
                new ConfigDescription(
                    "Speed in m/s below which a player's real position is used instead of a predicted one.",
                    new AcceptableValueRange<float>(0.5f, 20f)));

            ConfigPredictionMaxLookaheadZones = Config.Bind(
                "10 - Server Authority",
                "Prediction Max Lookahead Zones",
                9,
                new ConfigDescription(
                    "Furthest ahead the prediction may reach, in zones (64 m each), so a very fast or teleporting player does not\n" +
                    "make the server load distant areas.",
                    new AcceptableValueRange<int>(1, 25)));

            // NEW: ZDO Throttling (server-only bandwidth optimization)
            ConfigEnableZDOThrottling = Config.Bind(
                "10 - Server Authority",
                "Enable ZDO Throttling",
                true,
                "When a player's connection backs up, the server sends loose objects beyond 'ZDO Throttle Distance' after\n" +
                "everything nearer. Buildings and terrain are not delayed. Dedicated server only.");

            ConfigZDOThrottleDistance = Config.Bind(
                "10 - Server Authority",
                "ZDO Throttle Distance",
                500f,
                new ConfigDescription(
                    "Meters from a player beyond which loose objects are sent last when 'Enable ZDO Throttling' is on. 0 = off.\n" +
                    "Server Auto-Tune replaces it with its own value.",
                    new AcceptableValueRange<float>(0f, 1000f)));

            // NEW: AI LOD Throttling (server-only CPU optimization)
            ConfigEnableAILOD = Config.Bind(
                "10 - Server Authority",
                "Enable AI LOD Throttling",
                true,
                "Updates wild creatures beyond 'AI LOD Far Distance' from every player less often, to save server CPU. Closer\n" +
                "creatures, players and tames run at full rate. Dedicated server only.");

            ConfigAILODNearDistance = Config.Bind(
                "10 - Server Authority",
                "AI LOD Near Distance",
                100f,
                new ConfigDescription("Stats only: used to count near creatures in the [ServerStatus] line and changes no behaviour. Creatures\n" +
                    "inside 'AI LOD Far Distance' run at full rate. Server Auto-Tune replaces it with its own value.", new AcceptableValueRange<float>(50f, 200f)));

            ConfigAILODFarDistance = Config.Bind(
                "10 - Server Authority",
                "AI LOD Far Distance",
                300f,
                new ConfigDescription("Meters from the nearest player beyond which AI LOD slows creature updates. Server Auto-Tune replaces it\n" +
                    "with its own value.", new AcceptableValueRange<float>(200f, 600f)));

            ConfigAILODThrottleFactor = Config.Bind(
                "10 - Server Authority",
                "AI LOD Throttle Factor",
                0.5f,
                new ConfigDescription("Share of updates far creatures still get (0.5 = half, 0.25 = a quarter). Lower saves more CPU.\n" +
                    "Server Auto-Tune replaces it with its own value.", new AcceptableValueRange<float>(0.25f, 0.75f)));

            // Adaptive gate — the optimizations above only run when a peer's send queue
            // is actually backing up. Healthy server = vanilla behaviour (smoother).
            ConfigEnableAdaptiveThrottling = Config.Bind(
                "10 - Server Authority",
                "Adaptive Throttling",
                true,
                "Runs ZDO throttling, the player position boost and AI LOD only while some player's connection is backing up;\n" +
                "otherwise the server behaves as vanilla. Off = those run all the time. Dedicated server only.");

            ConfigSendCongestionThresholdPct = Config.Bind(
                "10 - Server Authority",
                "Congestion Threshold",
                50,
                new ConfigDescription(
                    "How full a player's send window must get, in percent, before 'Adaptive Throttling' counts the connection as\n" +
                    "backed up. Lower = kicks in sooner. Dedicated server only.",
                    new AcceptableValueRange<int>(10, 100)));

            ConfigEnableSendHeartbeatLog = Config.Bind(
                "12 - Advanced",
                "Log Send Queue Heartbeat",
                false,
                "Diagnostic: logs each player's send-queue health every 10 seconds (one line per player). Useful when\n" +
                "investigating lag; leave off for normal play. Dedicated server only.");

            ConfigEnableBootPatchVerification = Config.Bind(
                "12 - Advanced",
                "Enable Boot Patch Verification",
                false,
                new ConfigDescription(
                    "Diagnostic: at startup, logs every mod's patches on the AI, object-creation and zone-loading methods FGN relies on,\n" +
                    "to track down mod conflicts. Dedicated server with Server-Side Simulation on only.",
                    null));

            ConfigEnableLargeZdoDiagnostics = Config.Bind(
                "12 - Advanced",
                "Enable Large ZDO Diagnostics",
                false,
                new ConfigDescription(
                    "Diagnostic: when vanilla logs 'Writing a lot of data ... is not optimal', also logs which object caused it\n" +
                    "(prefab, position, owner). Turn on to track that warning down; leave off otherwise.",
                    null));

            ConfigEnableZDODelta = Config.Bind(
                "12 - Advanced",
                "Enable ZDO Delta Compression",
                true,
                "After an object's first full send, only the values that changed are sent again, which saves a lot of bandwidth\n" +
                "for creatures and players. Applies to server and client sends. Requires a restart.");

            ConfigAllocationFreeZdoWrites = Config.Bind(
                "12 - Advanced",
                "Allocation-free ZDO Writes",
                true,
                "Writes outgoing objects with the same bytes as vanilla but without its temporary memory use, cutting garbage\n" +
                "collection hitches. Checks itself against vanilla at the start of each session and falls back if anything differs.\n" +
                "Affects this machine only; takes effect live.");

            ConfigEnableWNTServerOptimization = Config.Bind(
                "12 - Advanced",
                "Enable WearNTear Server Optimization",
                true,
                "Skips the wear and support update for fully invulnerable building pieces (all damage Immune or Ignored, e.g.\n" +
                "Infinity Hammer pieces) that the server owns. Other pieces are unchanged. Dedicated server; requires a restart.");

            ConfigEnableInvulnerableSupportSkip = Config.Bind(
                "12 - Advanced",
                "Enable Invulnerable Support Skip",
                true,
                "Client counterpart of the WearNTear server optimization: skips the costly support check for fully invulnerable\n" +
                "pieces and keeps them at full support, so pieces resting on them are unaffected. Big CPU saving in large bases\n" +
                "built with such pieces.");

            ConfigEnableInstanceOrphanPrune = Config.Bind(
                "12 - Advanced",
                "Enable Instance Orphan Prune",
                true,
                "With Server-Side Simulation on, if removing objects hits a broken entry left behind by another mod, clears those\n" +
                "entries and retries instead of erroring. Each cleanup is logged; repeated ones point to a conflicting mod.\n" +
                "Dedicated server only.");

            ConfigFixTeleportGhosts = Config.Bind(
                "12 - Advanced",
                "Fix Teleport Ghost Players",
                true,
                "Fixes a vanilla bug where a player (or anything) that teleports away stays visible to others, frozen where it\n" +
                "left. Steps aside if the game or another mod (such as ValheimCommunityPatch) already fixes it. Server or host only.");

            ConfigFixSlowSleep = Config.Bind(
                "12 - Advanced",
                "Fix Slow Sleep On Busy Servers",
                true,
                "On a busy server with a low frame rate, vanilla's sleep skip can keep everyone in bed for a minute or more.\n" +
                "With this on, morning arrives on time however busy the server is. Server or host only.");

            ConfigKeepaliveFirst = Config.Bind(
                "12 - Advanced",
                "Keep Busy Connections Alive",
                true,
                "Sends keepalives ahead of queued data, so a large transfer (such as a big base loading in) cannot time a\n" +
                "player out while data is still arriving. Works on whichever side has it; best on server and clients.");

            ConfigKeepWorldClockAtRealTime = Config.Bind(
                "12 - Advanced",
                "Keep World Clock At Real Time",
                true,
                "When the server has very slow frames, its world clock falls behind real time and every player's clock jumps\n" +
                "back to match. With this on, the server adds the lost time back so the clock keeps real time. Dedicated server.");

            ConfigSmoothServerClockCorrections = Config.Bind(
                "12 - Advanced",
                "Smooth Server Clock Corrections",
                true,
                "Eases in the server's regular clock corrections instead of jumping, so waves, ships and timers do not visibly\n" +
                "jerk. Large changes (sleeping, joining, an admin setting the time) still apply at once. Client side, per player.");

            ConfigEnableCapeCrashDiagnostics = Config.Bind(
                "01 - General",
                "Enable Cape Crash Diagnostics",
                false,
                "Diagnostic for crashes during cape cloth setup: logs each step before it runs, so the last line before a crash\n" +
                "names it. Very verbose; leave off for normal play. Client only; requires a restart.");

            ConfigEnableFallThroughDiagnostics = Config.Bind(
                "01 - General",
                "Enable Fall-Through Diagnostics",
                false,
                "Diagnostic for items and tombstones sinking through floors: logs dropped items at risk or falling, and build\n" +
                "pieces that load too late to hold them. Only observes, but costs performance; leave off for normal play.\n" +
                "Requires a restart.");

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
        ConfigEnableZDODelta,
        ConfigAllocationFreeZdoWrites,
        ConfigShowAILODInServerStatus,
        ConfigEnableBootPatchVerification,
        ConfigEnableLargeZdoDiagnostics,
        ConfigEnableWNTServerOptimization,
        ConfigZoneLoadBatchSize,
        ConfigZPackageReceiveBufferSize,
        ConfigEnableTimeSliceInstantiation,
        ConfigInstantiationBudgetMs,
        ConfigMaxInstancesPerFrame,
        ConfigServerInstantiationBudgetMs,
        ConfigServerPassHz,
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
        ConfigKeepaliveFirst,
        ConfigKeepWorldClockAtRealTime,
        ConfigSmoothServerClockCorrections,
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
                        LoggerOptions.LogInfo($"[{side}] Config changed: {cfg.Definition.Section} â†’ {cfg.Definition.Key} = {cfg.BoxedValue}");
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
        Info,
        // Added last (1.5.28, Fire 2026-10-01: periodic diagnostics are debug logs) so the stored names and order of the others stay.
        [Description("Info plus the timed reports")]
        Debug
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
