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
        public const string PluginVersion = "1.4.67";
        internal static Harmony Harmony { get; private set; }

        // Static reference so non-MonoBehaviour subsystems (AutoTuneProbe coroutine, etc.)
        // can call StartCoroutine via the plugin instance.
        public static FiresGhettoNetworkMod Instance { get; private set; }

        // BepInEx log source â€” banner emitters route through this (not Debug.Log)
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
        public static ConfigEntry<bool> ConfigFixBoatDamageFromTimeSync;
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

            // BIG obnoxious "loading" banner â€” fires FIRST before any
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
            // guard bails if FiresAdminTerrain is loaded â€” see
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
                LoggerOptions.LogMessage($"{PluginName} v{PluginVersion} â€” Running on DEDICATED SERVER, enabling server-side features.");
            }
            else
            {
                LoggerOptions.LogMessage($"{PluginName} v{PluginVersion} â€” Running on CLIENT or SINGLE-PLAYER/LISTEN SERVER, only client-safe features will be applied.");
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
            SectorChangeTracker.InitConfig(Config);
            FireplaceFuelTicks.InitConfig(Config);
            TransformWriteRate.InitConfig(Config);
            SyncListRefPosPatches.InitConfig(Config);

            Harmony.PatchAll(typeof(CompressionGroup));
            Harmony.PatchAll(typeof(NetworkingRatesGroup));
            Harmony.PatchAll(typeof(DedicatedServerGroup));
            Harmony.PatchAll(typeof(SyncListRefPosPatches));
            Harmony.PatchAll(typeof(UploadBreakdown));
            Harmony.PatchAll(typeof(DownloadBreakdown));

            Harmony.PatchAll(typeof(SendZDOsHeartbeatDiagnostic));

            if (ConfigEnableFallThroughDiagnostics.Value)
            {
                Harmony.PatchAll(typeof(FallThroughProbe));
                Harmony.PatchAll(typeof(PieceTypeAudit));
            }

            Harmony.PatchAll(typeof(ServerDisconnectDiagnostics));

            Harmony.PatchAll(typeof(BulkTransferGatePatches));

            Harmony.PatchAll(typeof(BigZdoDiagnostic));

            Harmony.PatchAll(typeof(FireplaceFuelTicks));
            Harmony.PatchAll(typeof(TransformWriteRate));

            ZdoWireWriter.Initialize();
            Harmony.PatchAll(typeof(ZdoWireWriter));

            if (ConfigEnableZDODelta.Value)
            {
                Harmony.PatchAll(typeof(ZDODeltaPatches));
                LoggerOptions.LogInfo("ZDO delta compression enabled.");
            }

            Harmony.PatchAll(typeof(DisarmOverloadTest));

            Harmony.PatchAll(typeof(CompressionRoundTripTest));

            Harmony.PatchAll(typeof(SocketStressTests));

            Harmony.PatchAll(typeof(SendQueueHeadroomMonitor));

            Harmony.PatchAll(typeof(LinkController));

            Harmony.PatchAll(typeof(SendScheduler));

            Harmony.PatchAll(typeof(ConnectionEcho));

            Harmony.PatchAll(typeof(CreatureOwnership));

            Harmony.PatchAll(typeof(HelmOwnership));

            Harmony.PatchAll(typeof(KeepaliveFirst));

            try
            {
                Harmony.PatchAll(typeof(RoundTripTrace));
            }
            catch (System.Exception ex)
            {
                LoggerOptions.LogWarning($"[RoundTrip] could not be attached; round trips are still timed, without the per-stage breakdown. {ex.Message}");
            }

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

            try
            {
                Harmony.PatchAll(typeof(TerrainCompInitRace));
            }
            catch (System.Exception ex)
            {
                LoggerOptions.LogWarning($"[TerrainComp] could not be attached; vanilla behaviour is unchanged. {ex.Message}");
            }

            Harmony.PatchAll(typeof(SleepTimeSkipFix));

            if (!isDedicated)
                Harmony.PatchAll(typeof(WaveClockSmoothing));

            if (!isDedicated && ConfigEnableCapeCrashDiagnostics.Value)
            {
                Harmony.PatchAll(typeof(CapeCrashDiagnostics));
                Harmony.PatchAll(typeof(MagicaColliderRegistrationDiagnostics));
                Harmony.PatchAll(typeof(MagicaClothLifecycleDiagnostics));
                LoggerOptions.LogWarning("[CapeDiag] Cape crash diagnostics are ON; turn 'Enable Cape Crash Diagnostics' off once the crash is found.");
            }

            // Auto-tune: probe on clients, self-tune on servers.
            Harmony.PatchAll(typeof(AutoTuneProbeHooks));
            Harmony.PatchAll(typeof(ZoneLoadPatches));
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
                LoggerOptions.LogInfo("Server-side features skipped â€” not running on a dedicated server.");
            }

            StartCoroutine(EmitCompactBannerWhenZNetReady());
        }

        /// <summary>Dedicated-server traffic shaping. None of it needs Server-Side Simulation; each feature follows its own toggle.</summary>
        private static void ApplyServerTrafficPatches()
        {
            Harmony.PatchAll(typeof(ServerFrameProfile));
            Harmony.PatchAll(typeof(SectorChangeTracker));
            Harmony.PatchAll(typeof(ZDOThrottlingPatches));
            Harmony.PatchAll(typeof(AILODPatches));

            if (ConfigEnableRpcRouter.Value || StationRouter.ConfigEnabled.Value)
            {
                Harmony.PatchAll(typeof(RpcRouterPatches));
                Harmony.PatchAll(typeof(StationRouter));
                DamageTextHandler.Register();
                VAGhettoLoadSummary.EmitRpcRouter(
                    handlersRegistered: RoutedRpcManager.HandlerCount,
                    aoiRadius: RoutedRpcManager.PositionRadius(),
                    aoiEnabled: RoutedRpcManager.FilteringEnabled());
                if (VAGhettoLoadSummary.VerboseEnabled)
                    LoggerOptions.LogInfo("RPC Router enabled â€” handlers: " + string.Join(", ", RoutedRpcManager.HandlerMethodNames) + ".");
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

            Harmony.PatchAll(typeof(ServerAuthorityPatches));
            Harmony.PatchAll(typeof(ServerStabilityPatches));
            Harmony.PatchAll(typeof(MonsterAIPatches));

            if (ConfigEnableServerOwnershipSelective.Value)
            {
                if (ConfigEnableServerOwnership.Value)
                {
                    LoggerOptions.LogWarning(
                        "Both 'Server ZDO Ownership Transfer' flags are ENABLED â€” selective (V3) takes precedence; broad (V2) is being ignored. Disable one to silence this warning.");
                }
                Harmony.PatchAll(typeof(ServerOwnershipPatchesV3));
                CreatureOwnership.ServerOwnsCreatures = true;
                HelmOwnership.ServerOwnsShips = ConfigEnableServerSideShipSimulation.Value;
                LoggerOptions.LogMessage(
                    "Server ZDO ownership (V3 SELECTIVE) ENABLED â€” Character/Ship only; drops/voxel/interactables/carts stay peer-owned.");
            }
            else if (ConfigEnableServerOwnership.Value)
            {
                Harmony.PatchAll(typeof(ServerOwnershipPatches));
                CreatureOwnership.ServerOwnsCreatures = true;
                HelmOwnership.ServerOwnsShips = true;
                LoggerOptions.LogMessage(
                    "Server ZDO ownership (V2 BROAD SSS-exact) ENABLED â€” every persistent ZDO in any peer's active area will be claimed by the server.");
                if (!ConfigEnableServerSideShipSimulation.Value)
                    LoggerOptions.LogWarning(
                        "V2 BROAD claims SHIPS as well, regardless of 'Server-Side Ship Simulation' being off â€” it is a "
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
        // the compact loaded banner. Failure is non-fatal â€” falls
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

        // Compatibility patch for WackyDatabase â€” safely skips SnapshotItem for broken/null items
        [HarmonyPatch]
        public static class WackyDatabaseCompatibilityPatch
        {
            public static void Init(Harmony harmony)
            {
                // Safely detect if WackyDatabase is present
                Type functionsType = Type.GetType("wackydatabase.Util.Functions, WackysDatabase");
                if (functionsType == null)
                {
                    LoggerOptions.LogInfo("WackyDatabase not detected â€” skipping compatibility patch.");
                    return;
                }

                MethodInfo snapshotMethod = functionsType.GetMethod("SnapshotItem", BindingFlags.Static | BindingFlags.Public);
                if (snapshotMethod == null)
                {
                    LoggerOptions.LogWarning("WackyDatabase detected but SnapshotItem method not found â€” patch skipped.");
                    return;
                }

                // Apply the prefix patch
                harmony.Patch(
                    original: snapshotMethod,
                    prefix: new HarmonyMethod(typeof(WackyDatabaseCompatibilityPatch), nameof(SnapshotItem_Prefix))
                );

                LoggerOptions.LogInfo("WackyDatabase compatibility patch applied â€” will skip snapshots for invalid/broken clones.");
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
                    LoggerOptions.LogWarning($"WDB: Skipping snapshot for {item.name} â€” gameObject is null (missing prefab from removed mod).");
                    return false;
                }

                // Third: no renderable components (prevents NRE in bounds calculation and rendering)
                bool hasRenderer = item.GetComponentsInChildren<Renderer>(true).Length > 0;
                bool hasMesh = item.GetComponentsInChildren<MeshFilter>(true).Length > 0;

                if (!hasRenderer && !hasMesh)
                {
                    LoggerOptions.LogWarning($"WDB: Skipping snapshot for {item.name} â€” no renderers or meshes (broken model).");
                    return false;
                }

                // All good â€” allow original method to run
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
        // now â€” owner / patch-method full name / priority. Lets us prove
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
                    "CLIENT-ONLY â€” no effect on server.",
                    new AcceptableValueRange<int>(1, 8)));

            PlayerPositionSyncPatches.Init(Config);

            ConfigEnableCompression = Config.Bind(
                "04 - Networking",
                "Enable Compression",
                true,
                "Enable Deflate network compression (highly recommended). It is negotiated per player, so it only\n" +
                "engages with players whose FGN uses the same compression format. Data Valheim already compresses\n" +
                "(terrain edits, tar pits, map tables) is sent as is rather than compressed again.");

            ConfigUpdateRate = Config.Bind(
                "04 - Networking",
                "ZDO Send Rate",
                UpdateRateOptions._100,
                "SET BY AUTO-TUNE when Auto-Tune is on: it measures your connection and writes the value here, so this\n" +
                "always shows what is actually running. Edits are replaced on the next tune. Turn Auto-Tune off\n" +
                "(06 - Auto-Tune) to set it yourself - it starts from the last value Auto-Tune chose.\n" +
                "How many times a second world updates are sent - to each player on a server, and this PC's OWN\n" +
                "updates on a client. A NETWORK cadence setting only: it does NOT change the world tick, day length,\n" +
                "smelter or cooking timers, cooldowns or any simulation speed.\n" +
                "100% (20/s) is vanilla. 150% (30/s) looks smoother where bandwidth and CPU allow.\n" +
                "75% / 50% cut how often your character's updates leave your PC - the fix for a thin UPLOAD, and\n" +
                "what Auto-Tune picks when it measures one. You look slightly less smooth to others and see no\n" +
                "difference yourself, and you stop rubber-banding for everyone else.");

            ConfigSendRateMin = Config.Bind(
                "05 - Networking - Steamworks",
                "Send Rate Min",
                MigrateLegacyKb("05 - Networking - Steamworks", "Send Rate Min", DefaultSendRateMinKb),
                new ConfigDescription(
                "In KB/s. " +
                "SET BY AUTO-TUNE when Auto-Tune is on: it measures your connection and writes the value here, so this\n" +
                "always shows what is actually running. Edits are replaced on the next tune. Turn Auto-Tune off\n" +
                "(06 - Auto-Tune) to set it yourself - it starts from the last value Auto-Tune chose.\n" +
                "The rate Steam HOLDS even while a connection struggles, and the floor Adaptive Upload / Adaptive\n" +
                "Send Rate back off to. Keep it well under your real upload: a Min at or near the line leaves no room\n" +
                "to back off, so a dip floods it. Auto-Tune keeps it at no more than half of Send Rate Max.",
                new AcceptableValueRange<int>(SendRateKbLow, SendRateKbHigh)));

            ConfigSendRateMax = Config.Bind(
                "05 - Networking - Steamworks",
                "Send Rate Max",
                MigrateLegacyKb("05 - Networking - Steamworks", "Send Rate Max", DefaultSendRateMaxKb),
                new ConfigDescription(
                "In KB/s. " +
                "SET BY AUTO-TUNE when Auto-Tune is on: it measures your connection and writes the value here, so this\n" +
                "always shows what is actually running. Edits are replaced on the next tune. Turn Auto-Tune off\n" +
                "(06 - Auto-Tune) to set it yourself - it starts from the last value Auto-Tune chose.\n" +
                "The ceiling on this PC's send rate - on a client, your UPLOAD ceiling. Adaptive Upload and Adaptive\n" +
                "Send Rate move the live rate between Min and Max, never outside it.\n" +
                "Auto-Tune sets it from your tier, or - when it measures that your upload is the limit - to your real\n" +
                "upload speed exactly, so no capacity is left unused. The live controller, not this ceiling, is what\n" +
                "keeps you under the line from moment to moment.",
                new AcceptableValueRange<int>(SendRateKbLow, SendRateKbHigh)));

            ConfigAdaptiveUpload = Config.Bind(
                "04 - Networking",
                "Adaptive Upload",
                true,
                "Keeps this PC's live send rate under what its connection can actually carry, continuously, within\n" +
                "Send Rate Min..Max. When more is being sent than gets through, it eases toward what gets through\n" +
                "instead of flooding the line. Catches changes the join-time measurement cannot - someone else in\n" +
                "the house starting an upload, say. Off = Steam's own rate adapter.\n" +
                "Steam connections only; a crossplay (PlayFab) link has no Steam figures to measure.");

            ConfigHyperBoost = Config.Bind(
                "05 - Networking - Steamworks",
                "HYPERBOOST",
                false,
                "MAX-THROUGHPUT MODE. Overrides Auto-Tune AND the Send Rate Min/Max above, lifting Steam's\n" +
                "send rate, send buffer, and recv buffer / per-message ceiling to their proven unlocked\n" +
                "maximums â€” the same lifts fgn_socketramp uses to reach ~40 MB/s, versus the ~8 MB/s the\n" +
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
                "SET BY AUTO-TUNE when Auto-Tune is on: it measures your connection and writes the value here, so this\n" +
                "always shows what is actually running. Edits are replaced on the next tune. Turn Auto-Tune off\n" +
                "(06 - Auto-Tune) to set it yourself - it starts from the last value Auto-Tune chose.\n" +
                "The largest single package of world updates sent to one player at a time, and the starting send window\n" +
                "for each player. With 'Adaptive Send Window' off it is also the fixed limit on data in flight to each\n" +
                "player. Crossplay players are sized by Crossplay In-Flight KB instead.");

            ConfigForceCrossplay = Config.Bind(
                "09 - Dedicated Server",
                "Force Crossplay",
                ForceCrossplayOptions.vanilla,
                "Requires restart. Selects the networking backend for a DEDICATED SERVER.\n" +
                "vanilla = respect the command-line -crossplay flag (DEFAULT â€” does NOT change how your server connects).\n" +
                "steamworks = force Steam-only; DISABLES crossplay. Best performance for an all-Steam playerbase, " +
                "but Xbox / Game Pass / PlayStation players cannot join.\n" +
                "playfab = force crossplay ENABLED (PlayFab matchmaking) regardless of the -crossplay flag. Players then join with\n" +
                "the join code the server prints.\n" +
                "On a client it only changes worlds you host. Joining a server by address ignores playfab, because Valheim already\n" +
                "joins over crossplay when it finds a crossplay server at that address. Leave clients on vanilla.");

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
                    "Max players advertised to matchmaking (Steam server browser â†’ BattleMetrics, PlayFab session/Party). " +
                    "Independent of the actual in-game limit set by 'Player Limit' â€” useful when an operator wants their " +
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
                    "CLIENT-ONLY â€” no effect on server.",
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
                "arrives â€” before the structure it rests on, when that ZDO lags a tick behind â€” which can\n" +
                "let tames slip locked pens or drop items through floors on zone load. Opt in for the\n" +
                "smoother zone crossings if your world doesn't hit that.\n" +
                "CLIENT-ONLY â€” no effect on dedicated server.");

            ConfigInstantiationBudgetMs = Config.Bind(
                "02 - Client Performance",
                "Instantiation Budget Ms",
                3,
                new ConfigDescription(
                    "Per-frame millisecond budget for ZDO â†’ GameObject instantiation when time-slicing\n" +
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
                "Server-side relay for the messages players broadcast. Vanilla sends every footstep, swing, damage number and\n" +
                "destroyed object to every player on the server. With 'Enable RPC Area-of-Interest' on, a message about an\n" +
                "object only goes to players the server has sent that object, and a damage number only to players near it.\n" +
                "Nothing is dropped that a player could have used. Requires restart.\n" +
                "DEDICATED SERVER ONLY.");

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
                "Relays a broadcast about an object only to players who have that object, destroyed objects only to players\n" +
                "who held them, and damage numbers only to players within 'RPC AoI Radius'. Each of those players would\n" +
                "discard the message anyway. Broadcasts with no object or position still go to everyone.\n" +
                "Requires Enable RPC Router = true. DEDICATED SERVER ONLY.");

            ConfigRpcAoIRadius = Config.Bind(
                "10 - Server Authority",
                "RPC AoI Radius",
                256f,
                new ConfigDescription(
                    "Distance in meters within which players are sent messages that carry only a position, such as damage\n" +
                    "numbers (each player shows those within 30 m of their camera). Auto-Tune sets it when it is on.",
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
                    "CLIENT-ONLY â€” no effect on dedicated server.",
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
                "Has no effect when Enable AI LOD Throttling is OFF â€” there's nothing to report.");

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
                    + "Both sides â€” applies on client and dedicated server.",
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
                    "EXPERIMENTAL â€” defaults OFF. Direct port of the original Serverside Simulations mod's\n" +
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
                    "preserves vanilla peer ownership â€” every other server-authority feature (zones,\n" +
                    "spawning, raids, throttling) still works without it.\n" +
                    "\n" +
                    "REQUIRES: ConfigEnableServerAuthority = true AND running on a dedicated server.\n" +
                    "TOGGLE IS INDEPENDENT â€” flip this without touching ConfigEnableServerAuthority.",
                    null));

            ConfigEnableServerOwnershipSelective = Config.Bind(
                "10 - Server Authority",
                "Enable Server ZDO Ownership Transfer â€” Selective (EXPERIMENTAL)",
                false,
                new ConfigDescription(
                    "EXPERIMENTAL â€” defaults OFF. Selective variant of the broad ownership transfer above.\n" +
                    "Only Character (non-Player) and Ship prefabs are claimed by the server. Drops,\n" +
                    "containers, doors, signs, workstations, pickables, beds, traders, wards, voxel\n" +
                    "terrain, built structures, and carts all stay under vanilla peer ownership.\n" +
                    "\n" +
                    "RATIONALE: broad SSS-style ownership (the toggle above) exposes interaction-RPC\n" +
                    "race conditions â€” `removedrops` failing for mob-dropped items, voxel mining/flattening\n" +
                    "breaking intermittently under load, carts shaking/sinking when parked. Selective scope\n" +
                    "avoids all three by keeping interactables on the vanilla peer-owned path.\n" +
                    "\n" +
                    "MUTUALLY EXCLUSIVE with the broad toggle above. If both are true, this selective\n" +
                    "variant takes precedence (the safer choice â€” drops/voxel/etc. stay working).\n" +
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
                    "SERVER-ONLY â€” clients ignore this setting.",
                    new AcceptableValueRange<int>(0, 3)));

            // ---- Predictive zone pre-streaming (Workstream C) ----
            ConfigEnablePredictiveZoneStreaming = Config.Bind(
                "10 - Server Authority",
                "Enable Predictive Zone Streaming",
                true,
                "Bias each peer's active-area center forward along their velocity vector so the\n" +
                "server starts loading zones BEFORE the peer crosses the boundary. Composes with\n" +
                "'Extended Zone Radius' â€” the symmetric ring still expands, the center just slides\n" +
                "forward. By the time the peer arrives, the predicted zones are already loaded.\n" +
                "SERVER-ONLY â€” clients ignore this setting.");

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
                "SERVER-ONLY â€” no effect on client.");

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
                "SERVER-ONLY â€” no effect on client.");

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

            // Adaptive gate â€” the optimizations above only run when a peer's send queue
            // is actually backing up. Healthy server = vanilla behaviour (smoother).
            ConfigEnableAdaptiveThrottling = Config.Bind(
                "10 - Server Authority",
                "Adaptive Throttling",
                true,
                "Only engage FGN's send-side optimizations (distant-ZDO throttling, player\n" +
                "boost, AI LOD) when a peer's send queue is actually backing up. On a server\n" +
                "with bandwidth to spare, FGN leaves vanilla update order untouched â€” leaner\n" +
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

            ConfigEnableBootPatchVerification = Config.Bind(
                "12 - Advanced",
                "Enable Boot Patch Verification",
                false,
                new ConfigDescription(
                    "OFF by default. When ON, logs every Harmony patch attached to the AI tick,\n" +
                    "instantiation, and zone-gate methods this mod cares about â€” useful when\n" +
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
                "Both sides: the server's updates to each player, and the objects a player owns\n" +
                "(wild companions, tamed creatures, boats) going up to the server.");

            ConfigAllocationFreeZdoWrites = Config.Bind(
                "12 - Advanced",
                "Allocation-free ZDO Writes",
                true,
                "ON by default. Writes every object FGN sends (ZDOs) and every nested package straight into the\n" +
                "outgoing package: the same bytes vanilla writes, without vanilla's temporary lists, closures and\n" +
                "array copies (a busy client sends about 2,000 objects a second). The first 2,000 objects of each\n" +
                "session are checked against vanilla's own bytes; any difference switches back to vanilla for the\n" +
                "session and logs which object. Turn OFF to compare with vanilla's writer. This machine only;\n" +
                "read live.");

            ConfigEnableWNTServerOptimization = Config.Bind(
                "12 - Advanced",
                "Enable WearNTear Server Optimization",
                true,
                "Skips the wear and support update for building pieces whose damage modifiers are all\n" +
                "Immune or Ignore (Infinity Hammer, admin-flagged pieces), on the server that owns them.\n" +
                "Every other piece updates exactly as in vanilla.\n" +
                "SERVER-ONLY â€” no effect on client.");

            ConfigEnableInvulnerableSupportSkip = Config.Bind(
                "12 - Advanced",
                "Enable Invulnerable Support Skip",
                true,
                "CLIENT-side counterpart to the WearNTear server optimization. Short-circuits the\n" +
                "expensive WearNTear.UpdateSupport call (Physics.OverlapBoxNonAlloc per piece) for\n" +
                "pieces whose damage modifiers are all Immune/Ignore â€” e.g. Infinity Hammer pieces.\n" +
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
                "still reaps the ZDO via a separate path â€” e.g. TargetPortalProtection's\n" +
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

            ConfigKeepaliveFirst = Config.Bind(
                "12 - Advanced",
                "Keep Busy Connections Alive",
                true,
                "Valheim disconnects a player whose keepalive goes unanswered for 30 seconds, and its keepalives\n" +
                "wait behind everything already queued to send. A large transfer, such as a big base loading in,\n" +
                "could time a player out while data was still arriving. With this on, keepalives go ahead of the\n" +
                "queue. Works on whichever side has it; install on server and clients to cover both directions.");

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

            ConfigEnableCapeCrashDiagnostics = Config.Bind(
                "01 - General",
                "Enable Cape Crash Diagnostics",
                false,
                "Temporary client diagnostic for the crash in cape cloth setup. Logs taking and leaving ship controls, shoulder\n" +
                "item changes, every cloth collider handed to MagicaCloth2, every cloth built or destroyed, a per-frame check of\n" +
                "each cape collider list, object creation bursts, data overwriting the local player, auto-tune probe steps, server\n" +
                "clock corrections, every Steam setting FGN applies and a 5 second heartbeat with memory and compression totals.\n" +
                "Each line is written before its step runs, so the last line before a crash names the step. Takes effect on the\n" +
                "next start. Leave OFF for normal play.\n" +
                "CLIENT-SIDE.");

            ConfigEnableFallThroughDiagnostics = Config.Bind(
                "01 - General",
                "Enable Fall-Through Diagnostics",
                false,
                "Verbose, opt-in diagnostics for investigating items and tombstones sinking through\n" +
                "structures. Off by default. When on, two probes run: a per-spawn probe that raycasts\n" +
                "under each dropped item/tombstone and logs the ones at risk (and any that actually\n" +
                "fall), and a one-shot startup audit that names build pieces left non-Solid (the load\n" +
                "order that lets an item spawn before its support). The per-spawn probe adds real cost\n" +
                "and log volume on a busy server, so leave this OFF for normal play and the live read â€”\n" +
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
        ConfigFixBoatDamageFromTimeSync,
        ConfigEnableFallThroughDiagnostics,
        ConfigEnableBulkTransferBoost,
        ConfigBulkTransferBudgetPercent,
        ConfigEnableServerOwnership,
        ConfigEnableServerOwnershipSelective
            };
            // Note: AutoTune.* configs are bound later (in Awake, after BindConfigs returns),
            // so they don't get change-logger hooks â€” their own log lines cover that.

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
