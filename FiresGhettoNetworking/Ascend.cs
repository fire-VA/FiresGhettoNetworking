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
        public const string PluginVersion = "1.5.73";

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

        // A type that can't load on this game build (Steam-only types on the Microsoft
        // Store build) fails the JIT of the method that names it BEFORE any line of it runs, so a check inside Awake can't help. The
        // body lives in AwakeCore (never inlined) and a load failure is caught and logged here instead of silently killing FGN.
        private void Awake()
        {
            try { AwakeCore(); }
            catch (Exception ex) when (ex is TypeLoadException || ex is MissingMemberException || ex is System.IO.FileNotFoundException)
            {
                Logger.LogError($"FiresGhettoNetworkMod could not finish loading on this game build ({ex.GetType().Name}: {ex.Message}). " +
                                "Please report it with this log.");
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void AwakeCore()
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
            FgnVersionGate.InitConfig(Config);
            OwnerSafeRevisions.InitConfig(Config);
            PickupClaimGuard.InitConfig(Config);
            Api.FgnApi.InitConfig(Config);
            DropStaleConfigKeys();   // after every key is bound, so moved values carry over
            try { Config.Save(); } catch (Exception ex) { Logger.LogWarning($"Failed to save the tidied config: {ex.Message}"); }

            if (!SteamSide.Present)
                LoggerOptions.LogMessage("Steam networking not present: FGN's Steam features off, the rest on");

            TryPatchAll(typeof(CompressionGroup));
            TryPatchAll(typeof(NetworkingRatesGroup));
            if (SteamSide.Present) TryPatchAll(typeof(NetworkingRatesGroup.SteamConnectPatch));   // Steam-only (the Xbox build has no ZSteamSocket)
            TryPatchAll(typeof(DedicatedServerGroup));
            if (SteamSide.Present) TryPatchAll(typeof(DedicatedServerGroup.SteamPatches));   // Steam-only (the Xbox build has no ZSteamSocket)
            TryPatchAll(typeof(PlayFabJoinCodeGuard));   // crossplay join-code check (null lobby entry), servers and hosts
            TryPatchAll(typeof(ServerSafePick));   // a server-owned Pickable no longer throws in RPC_Pick (no local player)
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
            if (SteamSide.Present) TryPatchAll(typeof(CloseWithoutSleep));   // Steam-only (the Xbox build has no ZSteamSocket)
            PlayFabZlibWorker.Apply();

            TryPatchAll(typeof(BulkTransferGatePatches));

            TryPatchAll(typeof(BigZdoDiagnostic));

            TryPatchAll(typeof(FireplaceFuelTicks));
            // OwnerSync's prefix attaches only while one of its features is on (ZSyncHookGate)
            ZSyncHookGate.Init(TransformWriteRate.ConfigEnabled, TransformWriteRate.ConfigPrefabs, TransformWriteRate.ConfigWritesPerSecond, RemoteMotion.ConfigSendStamps);

            ZdoWireWriter.Initialize();
            TryPatchAll(typeof(ZdoWireWriter));

            if (ConfigEnableZDODelta.Value)
            {
                TryPatchAll(typeof(ZDODeltaPatches));
                LoggerOptions.LogInfo("ZDO delta compression enabled.");
            }

            TryPatchAll(typeof(DisarmOverloadTest));
            if (SteamSide.Present) TryPatchAll(typeof(DisarmOverloadTest.SteamPatch));   // Steam-only (the Xbox build has no ZSteamSocket)

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

            if (SteamSide.Present) TryPatchAll(typeof(KeepaliveFirst));   // Steam-only (the Xbox build has no ZSteamSocket)

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

            TryPatchAll(typeof(ZSyncHookGate));   // the handoff prefix is attached by its session gate (off for a solo host), not here

            TryPatchAll(typeof(OwnedPositionEcho));
            TryPatchAll(typeof(OwnerSafeRevisions));
            TryPatchAll(typeof(PickupClaimGuard));   // a full player never claims a drop it cannot carry
            TryPatchAll(typeof(PendingServerKeys));
            OwnerSafeRevisions.MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;

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

            // Always attached: it must run on clients too, not only with the server-only MonsterAIPatches.
            try
            {
                TryPatchAll(typeof(SpawnHeightmapGuard));
            }
            catch (System.Exception ex)
            {
                LoggerOptions.LogWarning($"[Spawn] heightmap guard could not be attached; vanilla behaviour is unchanged. {ex.Message}");
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
                    ZSyncHookGate.Refresh("Server-Side Simulation started");   // the kill-plane rescue needs OwnerSync
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

            TmlGuard.Capture();   // our own settings, before any later plugin's Awake
            StartCoroutine(TmlGuard.Run(this));
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
            TryPatchAll(typeof(SssQuietPass));   // quiet-pass counter for the [SSS] line
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
                    yield return new WaitForSecondsRealtime(JoinAddressPollSeconds);
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
        private const float JoinAddressPollSeconds = 0.5f;
        private const int DefaultServerPort = 2456;

        private static bool TryGetJoinAddress(out string address)
        {
            address = null;
            int port = ServerPort();
            if (port <= 0) return false;
            // The Xbox build has no ZSteamSocket: the Steam game-server read is JIT'd only when Steam is there.
            return SteamSide.Present && TryGetSteamJoinAddress(port, out address);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]   // never inlined into a non-Steam caller
        private static bool TryGetSteamJoinAddress(int port, out string address)
        {
            address = null;
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
            return DefaultServerPort;
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
        /// (which would leave every peer running a half-initialised FGN).
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
        /// not reject it and reset the player to the default. A no-op once the file holds a number.</summary>
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

        /// <summary>
        /// Removes keys FGN no longer reads from the cfg, so the file
        /// shows only live settings: the old Selective key whose dash an earlier save garbled, two retired [12 - Advanced] keys, and
        /// the diagnostics' old "10 - Diagnostics" section (it clashed with "10 - Server Authority"; now "14 - Diagnostics").
        /// Only these named leftovers go; anything else another tool wrote is kept.
        /// </summary>
        private void DropStaleConfigKeys()
        {
            try
            {
                var prop = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (!(prop?.GetValue(Config) is System.Collections.Generic.Dictionary<ConfigDefinition, string> orphans)) return;
                var drop = new System.Collections.Generic.List<ConfigDefinition>();
                foreach (ConfigDefinition d in orphans.Keys)
                {
                    bool garbled = d.Section == "10 - Server Authority" && d.Key.IndexOf('â') >= 0;   // "â€”" = an em dash saved twice
                    bool retired = d.Section == "12 - Advanced" && (d.Key == "Fix Boat Damage From Server Time Sync" || d.Key == "Max Active ZDOs");
                    bool moved = d.Section == "10 - Diagnostics";
                    if (garbled || retired || moved) drop.Add(d);
                }
                foreach (ConfigDefinition d in drop)
                {
                    // a moved key: the player's old value goes into the new key once, then the old line is removed
                    var target = new ConfigDefinition("14 - Diagnostics", d.Key);
                    if (d.Section == "10 - Diagnostics" && Config.ContainsKey(target) && Config[target] is ConfigEntryBase moved)
                    {
                        string old = orphans[d];
                        if (old != moved.GetSerializedValue())
                        {
                            moved.SetSerializedValue(old);
                            Logger.LogInfo($"[Config] '{d.Key}' moved from [10 - Diagnostics] to [14 - Diagnostics]; your value '{old}' was kept.");
                        }
                    }
                    orphans.Remove(d);
                }
                if (drop.Count > 0) Logger.LogInfo($"[Config] removed {drop.Count} setting(s) FGN no longer uses from the cfg file.");
            }
            catch (Exception ex) { Logger.LogWarning($"[Config] could not tidy old settings: {ex.Message}"); }
        }

        private void BindConfigs()
        {
            // Config UI rules: only the basics a player or server admin needs show without "Show Advanced" (Cfg.Basic);
            // everything else is Cfg.Adv. Descriptions are one short line. Section and key names are unchanged so saved values stay.
            ConfigLogLevel = Config.Bind(
                "01 - General",
                "Log Level",
                LogLevel.Message,
                Cfg.Basic("How much FGN writes to the log. Message is normal; Info and Debug are for troubleshooting."));

            // ConfigManager sections render in first-Bind() order. The priming
            // binds + Init calls below set sections 02..08 to match the "NN - "
            // prefix. Bind each key once: a second Bind of the same key only
            // returns the same entry, and two copies drift apart when edited.
            ConfigZoneLoadBatchSize = Config.Bind(
                "02 - Client Performance",
                "Zone Load Batch Size",
                1,
                Cfg.Adv("How many objects load per frame when an area loads in (1 = vanilla). Higher = faster, bigger hitches.",
                    new AcceptableValueRange<int>(1, 8)));

            PlayerPositionSyncPatches.Init(Config);

            ConfigEnableCompression = Config.Bind(
                "04 - Networking",
                "Enable Compression",
                true,
                Cfg.Basic("Compresses network traffic. Works when both server and player run FGN."));

            ConfigUpdateRate = Config.Bind(
                "04 - Networking",
                "ZDO Send Rate",
                UpdateRateOptions._100,
                Cfg.Adv("How often world updates are sent (100% = vanilla). Auto-Tune overwrites this while on."));

            ConfigSendRateMin = Config.Bind(
                "05 - Networking - Steamworks",
                "Send Rate Min",
                MigrateLegacyKb("05 - Networking - Steamworks", "Send Rate Min", DefaultSendRateMinKb),
                Cfg.Adv("Lowest Steam send rate in KB/s. Auto-Tune overwrites this while on.",
                new AcceptableValueRange<int>(SendRateKbLow, SendRateKbHigh)));

            ConfigSendRateMax = Config.Bind(
                "05 - Networking - Steamworks",
                "Send Rate Max",
                MigrateLegacyKb("05 - Networking - Steamworks", "Send Rate Max", DefaultSendRateMaxKb),
                Cfg.Adv("Highest Steam send rate in KB/s (your upload ceiling). Auto-Tune overwrites this while on.",
                new AcceptableValueRange<int>(SendRateKbLow, SendRateKbHigh)));

            ConfigAdaptiveUpload = Config.Bind(
                "04 - Networking",
                "Adaptive Upload",
                true,
                Cfg.Adv("Backs your send rate off when your connection struggles instead of flooding it. Steam only."));

            ConfigHyperBoost = Config.Bind(
                "05 - Networking - Steamworks",
                "HYPERBOOST",
                false,
                Cfg.Adv("Max-throughput mode for very fast links and benchmarks. Leave off for normal play."));

            AutoTuneConfig.Init(Config);

            ConfigQueueSize = Config.Bind(
                "04 - Networking",
                "Queue Size",
                QueueSizeOptions._32KB,
                Cfg.Adv("Biggest batch of world updates sent to one player at once. Auto-Tune overwrites this while on."));

            ConfigForceCrossplay = Config.Bind(
                "09 - Dedicated Server",
                "Force Crossplay",
                ForceCrossplayOptions.vanilla,
                Cfg.Basic("Server backend: vanilla follows -crossplay, steamworks = Steam only, playfab = crossplay. Restart."));

            ConfigPlayerLimit = Config.Bind(
                "09 - Dedicated Server",
                "Player Limit",
                10,
                Cfg.Basic("Max players on a dedicated server. Restart to apply.", new AcceptableValueRange<int>(1, 999)));

            ConfigAdvertisedPlayerLimit = Config.Bind(
                "09 - Dedicated Server",
                "Advertised Player Limit",
                0,
                Cfg.Basic("Player cap shown in server lists (0 = same as Player Limit). Restart to apply.",
                    new AcceptableValueRange<int>(0, 9999)));

            // ---- Client-side perf knobs ----
            // Zone Load Batch Size is bound once, at the top of this method, where it primes section 02's position.

            ConfigZPackageReceiveBufferSize = Config.Bind(
                "02 - Client Performance",
                "ZPackage Receive Buffer Bytes",
                256 * 1024,
                Cfg.Adv("Steam receive buffer in bytes when Auto-Tune is off (raised to at least 2 MB).",
                    new AcceptableValueRange<int>(64 * 1024, 4 * 1024 * 1024)));

            // ---- Time-sliced instantiation ----
            ConfigEnableTimeSliceInstantiation = Config.Bind(
                "02 - Client Performance",
                "Enable Time-Slice Instantiation",
                false,
                Cfg.Adv("Spreads loading a busy area over several frames instead of one big hitch."));

            ConfigInstantiationBudgetMs = Config.Bind(
                "02 - Client Performance",
                "Instantiation Budget Ms",
                3,
                Cfg.Adv("Milliseconds per frame spent loading objects with time-slicing. Lower = smoother, higher = faster.",
                    new AcceptableValueRange<int>(1, 16)));

            ConfigMaxInstancesPerFrame = Config.Bind(
                "02 - Client Performance",
                "Max Instances Per Frame",
                100,
                Cfg.Adv("Most objects loaded in one frame with time-slicing.",
                    new AcceptableValueRange<int>(10, 500)));

            ConfigSafetyFallbackEnabled = Config.Bind(
                "02 - Client Performance",
                "Safety Fallback Enabled",
                true,
                Cfg.Adv("Loads faster when a huge backlog is waiting, like after teleporting into a big base."));

            ConfigSafetyFallbackThreshold = Config.Bind(
                "02 - Client Performance",
                "Safety Fallback Threshold",
                5000,
                Cfg.Adv("Backlog size at which the safety fallback kicks in.",
                    new AcceptableValueRange<int>(100, 50000)));

            ConfigEnableRpcRouter = Config.Bind(
                "10 - Server Authority",
                "Enable RPC Router",
                true,
                Cfg.Adv("Server relays hits and effects instead of everyone sending to everyone. Restart."));

            ConfigEnableShipFixes = Config.Bind(
                "11 - Ship Fixes",
                "Enable Universal Ship Fixes",
                true,
                Cfg.Adv("Keeps a server-run ship's speed and rudder as the helmsman set them. Restart."));

            ConfigEnableServerSideShipSimulation = Config.Bind(
                "11 - Ship Fixes",
                "Server-Side Ship Simulation",
                false,
                Cfg.Adv("Lets the server run ship physics too (with Selective ownership). Restart."));

            ConfigEnableRpcAoI = Config.Bind(
                "10 - Server Authority",
                "Enable RPC Area-of-Interest",
                true,
                Cfg.Adv("Only sends object messages and damage numbers to players close enough to see them."));

            ConfigRpcAoIRadius = Config.Bind(
                "10 - Server Authority",
                "RPC AoI Radius",
                256f,
                Cfg.Adv("Meters around a player for damage numbers and similar messages.",
                    new AcceptableValueRange<float>(64f, 1024f)));

            ConfigClientMaxDestroysPerFrame = Config.Bind(
                "02 - Client Performance",
                "Max Destroys Per Frame",
                200,
                Cfg.Adv("Most objects removed per frame when leaving an area, avoids freezes (0 = vanilla).",
                    new AcceptableValueRange<int>(0, 5000)));

            ConfigDediFellOutRescueLayers = Config.Bind(
                "10 - Server Authority",
                "Dedi Fell-Out Rescue Layers",
                "Default,static_solid,Default_small,piece,terrain,vehicle",
                Cfg.Adv("Layers that count as solid ground when rescuing things that fell out of the world."));

            ConfigFixGroundSnapThroughFloors = Config.Bind(
                "12 - Advanced",
                "Fix Ground Snap Through Floors",
                true,
                Cfg.Adv("Stops creatures and tombstones popping up through floors built below ground."));

            ConfigShowAILODInServerStatus = Config.Bind(
                "01 - General",
                "Show AILOD in ServerStatus",
                true,
                Cfg.Adv("Adds AI LOD stats to the server status log line."));

            ConfigDiagnosticIntervalSec = Config.Bind(
                "01 - General",
                "Diagnostic Rollup Interval (sec)",
                60f,
                Cfg.Adv("Seconds between server status log lines.",
                    new AcceptableValueRange<float>(10f, 3600f)));

            ConfigEnableBulkTransferBoost = Config.Bind(
                "04 - Networking",
                "Enable Bulk Transfer Queue Boost",
                true,
                Cfg.Adv("Speeds up ServerSync config syncing on join and stops it kicking slow players. Restart."));

            ConfigBulkTransferBudgetPercent = Config.Bind(
                "04 - Networking",
                "Bulk Transfer Budget Percent",
                40,
                Cfg.Adv("Share of the send buffer ServerSync mods may use with the boost on.",
                    new AcceptableValueRange<int>(10, 80)));

            ConfigEnableServerAuthority = Config.Bind(
                "10 - Server Authority",
                "Enable Server-Side Simulation",
                false,
                Cfg.Basic("Dedicated server runs the world around every player so it can run creatures and ships. Restart."));

            ConfigServerInstantiationBudgetMs = Config.Bind(
                "10 - Server Authority",
                "Server Instantiation Budget Ms",
                8,
                Cfg.Adv("Milliseconds per frame the server spends loading objects near players (0 = vanilla).",
                    new AcceptableValueRange<int>(0, 50)));

            ConfigServerPassHz = Config.Bind(
                "10 - Server Authority",
                "Server Object Pass Hz",
                10,
                Cfg.Adv("How often per second the server refreshes objects around players (0 = every frame).",
                    new AcceptableValueRange<int>(0, 30)));

            ConfigEnableServerOwnership = Config.Bind(
                "10 - Server Authority",
                "Enable Server ZDO Ownership Transfer (EXPERIMENTAL)",
                false,
                Cfg.Adv("Server takes over almost everything near players. Can break pickups; prefer Selective. Restart."));

            ConfigEnableServerOwnershipSelective = Config.Bind(
                "10 - Server Authority",
                "Enable Server ZDO Ownership Transfer — Selective (EXPERIMENTAL)",
                false,
                Cfg.Adv("Server runs wild creatures near players; everything else stays with players. Restart."));

            ConfigExtendedZoneRadius = Config.Bind(
                "10 - Server Authority",
                "Extended Zone Radius",
                0,
                Cfg.Adv("Extra zone rings the server loads around each player (0 = vanilla). Costs server CPU and RAM.",
                    new AcceptableValueRange<int>(0, 3)));

            // ---- Predictive zone pre-streaming ----
            ConfigEnablePredictiveZoneStreaming = Config.Bind(
                "10 - Server Authority",
                "Enable Predictive Zone Streaming",
                true,
                Cfg.Adv("Server loads the area ahead of a moving player so it is ready when they arrive."));

            ConfigPredictionLookaheadSec = Config.Bind(
                "10 - Server Authority",
                "Prediction Lookahead Sec",
                3.0f,
                Cfg.Adv("Seconds of travel ahead the prediction looks.",
                    new AcceptableValueRange<float>(0.5f, 10f)));

            ConfigPredictionMinVelocity = Config.Bind(
                "10 - Server Authority",
                "Prediction Min Velocity",
                2.0f,
                Cfg.Adv("Speed in m/s below which prediction is not used.",
                    new AcceptableValueRange<float>(0.5f, 20f)));

            ConfigPredictionMaxLookaheadZones = Config.Bind(
                "10 - Server Authority",
                "Prediction Max Lookahead Zones",
                9,
                Cfg.Adv("Furthest ahead the prediction may reach, in zones.",
                    new AcceptableValueRange<int>(1, 25)));

            // ZDO Throttling (server-only bandwidth optimization)
            ConfigEnableZDOThrottling = Config.Bind(
                "10 - Server Authority",
                "Enable ZDO Throttling",
                true,
                Cfg.Adv("On a backed-up connection, far loose objects are sent after nearer ones."));

            ConfigZDOThrottleDistance = Config.Bind(
                "10 - Server Authority",
                "ZDO Throttle Distance",
                500f,
                Cfg.Adv("Meters beyond which loose objects are sent last (0 = off).",
                    new AcceptableValueRange<float>(0f, 1000f)));

            // AI LOD Throttling (server-only CPU optimization)
            ConfigEnableAILOD = Config.Bind(
                "10 - Server Authority",
                "Enable AI LOD Throttling",
                true,
                Cfg.Adv("Updates creatures far from every player less often to save server CPU."));

            ConfigAILODNearDistance = Config.Bind(
                "10 - Server Authority",
                "AI LOD Near Distance",
                100f,
                Cfg.Adv("Stats only: distance counted as near in the status log.", new AcceptableValueRange<float>(50f, 200f)));

            ConfigAILODFarDistance = Config.Bind(
                "10 - Server Authority",
                "AI LOD Far Distance",
                300f,
                Cfg.Adv("Meters from the nearest player beyond which creatures update less often.", new AcceptableValueRange<float>(200f, 600f)));

            ConfigAILODThrottleFactor = Config.Bind(
                "10 - Server Authority",
                "AI LOD Throttle Factor",
                0.5f,
                Cfg.Adv("Share of updates far creatures still get (0.5 = half).", new AcceptableValueRange<float>(0.25f, 0.75f)));

            // Adaptive gate — the optimizations above only run when a peer's send queue
            // is actually backing up. Healthy server = vanilla behaviour (smoother).
            ConfigEnableAdaptiveThrottling = Config.Bind(
                "10 - Server Authority",
                "Adaptive Throttling",
                true,
                Cfg.Adv("Only throttles while a player's connection is backing up; otherwise plays like vanilla."));

            ConfigSendCongestionThresholdPct = Config.Bind(
                "10 - Server Authority",
                "Congestion Threshold",
                50,
                Cfg.Adv("How full (percent) a send window must be to count as backed up.",
                    new AcceptableValueRange<int>(10, 100)));

            ConfigEnableSendHeartbeatLog = Config.Bind(
                "12 - Advanced",
                "Log Send Queue Heartbeat",
                false,
                Cfg.Adv("Debug log: each player's send queue health every 10 s."));

            ConfigEnableBootPatchVerification = Config.Bind(
                "12 - Advanced",
                "Enable Boot Patch Verification",
                false,
                Cfg.Adv("Debug log: lists other mods' patches on methods FGN relies on at startup."));

            ConfigEnableLargeZdoDiagnostics = Config.Bind(
                "12 - Advanced",
                "Enable Large ZDO Diagnostics",
                false,
                Cfg.Adv("Debug log: names the object behind vanilla's 'Writing a lot of data' warning."));

            ConfigEnableZDODelta = Config.Bind(
                "12 - Advanced",
                "Enable ZDO Delta Compression",
                true,
                Cfg.Adv("Only resends values that changed, saving lots of bandwidth. Restart."));

            ConfigAllocationFreeZdoWrites = Config.Bind(
                "12 - Advanced",
                "Allocation-free ZDO Writes",
                true,
                Cfg.Adv("Writes network data without garbage, cutting stutter. Falls back to vanilla if anything differs."));

            ConfigEnableWNTServerOptimization = Config.Bind(
                "12 - Advanced",
                "Enable WearNTear Server Optimization",
                true,
                Cfg.Adv("Server skips wear updates on fully invulnerable build pieces. Restart."));

            ConfigEnableInvulnerableSupportSkip = Config.Bind(
                "12 - Advanced",
                "Enable Invulnerable Support Skip",
                true,
                Cfg.Adv("Skips support checks on fully invulnerable pieces; big CPU save in large bases."));

            ConfigEnableInstanceOrphanPrune = Config.Bind(
                "12 - Advanced",
                "Enable Instance Orphan Prune",
                true,
                Cfg.Adv("Cleans up broken object entries left by other mods instead of erroring."));

            ConfigFixTeleportGhosts = Config.Bind(
                "12 - Advanced",
                "Fix Teleport Ghost Players",
                true,
                Cfg.Adv("Fixes players staying visible, frozen, after they teleport away."));

            ConfigFixSlowSleep = Config.Bind(
                "12 - Advanced",
                "Fix Slow Sleep On Busy Servers",
                true,
                Cfg.Adv("Morning arrives on time when sleeping on a laggy server."));

            ConfigKeepaliveFirst = Config.Bind(
                "12 - Advanced",
                "Keep Busy Connections Alive",
                true,
                Cfg.Adv("Stops big transfers, like a large base loading, from timing players out."));

            ConfigKeepWorldClockAtRealTime = Config.Bind(
                "12 - Advanced",
                "Keep World Clock At Real Time",
                true,
                Cfg.Adv("Stops the world clock falling behind and jumping back on a laggy server."));

            ConfigSmoothServerClockCorrections = Config.Bind(
                "12 - Advanced",
                "Smooth Server Clock Corrections",
                true,
                Cfg.Adv("Eases in clock corrections so waves, ships and timers do not jerk."));

            ConfigEnableCapeCrashDiagnostics = Config.Bind(
                "01 - General",
                "Enable Cape Crash Diagnostics",
                false,
                Cfg.Adv("Debug log for crashes during cape setup. Very noisy. Restart."));

            ConfigEnableFallThroughDiagnostics = Config.Bind(
                "01 - General",
                "Enable Fall-Through Diagnostics",
                false,
                Cfg.Adv("Debug log for items and tombstones sinking through floors. Costs performance. Restart."));

            // === CONFIG CHANGE LOGGING ===
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
        // Added last (periodic diagnostics are debug logs) so the stored names and order of the others stay.
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

    /// <summary>Tags Configuration Manager and Fires Core read by field name (reflection): Browsable, IsAdvanced, Order.</summary>
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? Browsable;
        public bool? IsAdvanced;
        public int? Order;
    }

    /// <summary>Config descriptions: Basic = shown by default, Adv = only with "Show Advanced", Hidden = never shown (still saved).</summary>
    internal static class Cfg
    {
        internal static ConfigDescription Basic(string text, AcceptableValueBase range = null) => new ConfigDescription(text, range);
        internal static ConfigDescription Adv(string text, AcceptableValueBase range = null)
            => new ConfigDescription(text, range, new ConfigurationManagerAttributes { IsAdvanced = true });
        internal static ConfigDescription Hidden(string text, AcceptableValueBase range = null)
            => new ConfigDescription(text, range, new ConfigurationManagerAttributes { Browsable = false });
    }
}
