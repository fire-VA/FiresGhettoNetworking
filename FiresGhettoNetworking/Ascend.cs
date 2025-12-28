using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.ComponentModel;
using System.Reflection;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInProcess("valheim.exe")]
    [BepInProcess("valheim_server.exe")]
    public class FiresGhettoNetworkMod : BaseUnityPlugin
    {
        public const string PluginGUID = "com.Fire.FiresGhettoNetworkMod";
        public const string PluginName = "FiresGhettoNetworkMod";
        public const string PluginVersion = "1.1.0";
        internal static Harmony Harmony { get; private set; }

        // ====================== CONFIG ENTRIES ======================
        public static ConfigEntry<LogLevel> ConfigLogLevel;
        public static ConfigEntry<bool> ConfigEnableCompression;
        public static ConfigEntry<UpdateRateOptions> ConfigUpdateRate;
        public static ConfigEntry<SendRateMinOptions> ConfigSendRateMin;
        public static ConfigEntry<SendRateMaxOptions> ConfigSendRateMax;
        public static ConfigEntry<QueueSizeOptions> ConfigQueueSize;
        public static ConfigEntry<ForceCrossplayOptions> ConfigForceCrossplay;
        public static ConfigEntry<int> ConfigPlayerLimit;
        public static ConfigEntry<bool> ConfigEnableShipFixes;
        public static ConfigEntry<bool> ConfigEnableServerSideShipSimulation;
        public static ConfigEntry<int> ConfigExtendedZoneRadius;

        // NEW: ZDO Throttling configs
        public static ConfigEntry<bool> ConfigEnableZDOThrottling;
        public static ConfigEntry<float> ConfigZDOThrottleDistance;

        // NEW: AI LOD Throttling configs (server-only CPU optimization)
        public static ConfigEntry<bool> ConfigEnableAILOD;
        public static ConfigEntry<float> ConfigAILODNearDistance;
        public static ConfigEntry<float> ConfigAILODFarDistance;
        public static ConfigEntry<float> ConfigAILODThrottleFactor;

        // NEW: Client-side zone loading smoothness configs
        public static ConfigEntry<int> ConfigZoneLoadBatchSize;
        public static ConfigEntry<int> ConfigZPackageReceiveBufferSize;

        // Server-authority toggle — now server-only with warning
        public static ConfigEntry<bool> ConfigEnableServerAuthority;

        private static bool _dummyRpcRegistered = false;
        private bool _delayedInitDone = false;

        private void Awake()
        {
            Harmony = new Harmony(PluginGUID);

            BindConfigs();
            try { Config.Save(); }
            catch (Exception ex) { Logger.LogWarning($"Failed to save config file immediately: {ex.Message}"); }

            LoggerOptions.Init(Logger);

            // Detect dedicated server
            ServerClientUtils.Detect();
            bool isDedicated = ServerClientUtils.IsDedicatedServerDetected;
            LoggerOptions.LogInfo(isDedicated
                ? "Running on DEDICATED SERVER — disabling unsafe client-only features."
                : "Running on CLIENT — all features enabled.");

            // === ALWAYS initialize these groups (servers need rates and queue fixes too) ===
            SafeInvokeInit("FiresGhettoNetworkMod.CompressionGroup", "InitConfig", new object[] { Config });
            SafeInvokeInit("FiresGhettoNetworkMod.NetworkingRatesGroup", "Init", new object[] { Config });
            SafeInvokeInit("FiresGhettoNetworkMod.DedicatedServerGroup", "Init", new object[] { Config });

            // === NEW: Initialize Player Position Sync (reduces floaty movement) ===
            PlayerPositionSyncPatches.Init(Config);

            // Patch core networking features (always safe and needed early)
            Harmony.PatchAll(typeof(CompressionGroup));
            Harmony.PatchAll(typeof(NetworkingRatesGroup));
            Harmony.PatchAll(typeof(DedicatedServerGroup));
            Harmony.PatchAll(typeof(ShipFixesGroup));
            Harmony.PatchAll(typeof(ZDOMemoryManager));
            Harmony.PatchAll(typeof(PlayerPositionSyncPatches));

            // Apply WackyDatabase compatibility patch (safe, no timing issues)
            WackyDatabaseCompatibilityPatch.Init(Harmony);

            // Server-authority patches — apply immediately (server-only, guarded inside the classes)
            bool isServer = ZNet.instance != null && ZNet.instance.IsServer();
            if (isServer && ConfigEnableServerAuthority.Value)
            {
                Harmony.PatchAll(typeof(ServerAuthorityPatches));
                Harmony.PatchAll(typeof(MonsterAIPatches));
                Harmony.PatchAll(typeof(ZDOThrottlingPatches));
                Harmony.PatchAll(typeof(AILODPatches));
                LoggerOptions.LogInfo("Server-authority patches enabled (zone loading, ZDO ownership, AI, events, ZDO throttling, AI LOD, etc.)");
            }
            else if (!isServer)
            {
                LoggerOptions.LogInfo("Server-authority patches ignored on client (server-only feature).");
            }
            else
            {
                LoggerOptions.LogInfo("Server-authority patches disabled via config.");
            }

            // Register dummy RPC (safe to do early — ZRoutedRpc becomes available very quickly)
            StartCoroutine(RegisterDummyRpcWhenReady());

            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded.");
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

        private void SafeInvokeInit(string typeName, string methodName, object[] args)
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
                "General",
                "Log Level",
                LogLevel.Message,
                "Controls verbosity in BepInEx log.");

            ConfigEnableCompression = Config.Bind(
                "Networking",
                "Enable Compression",
                true,
                "Enable ZSTD network compression (highly recommended).");

            ConfigUpdateRate = Config.Bind(
                "Networking",
                "Update Rate",
                UpdateRateOptions._100,
                "Server update frequency. Lower = less bandwidth.");

            ConfigSendRateMin = Config.Bind(
                "Networking.Steamworks",
                "Send Rate Min",
                SendRateMinOptions._256KB,
                "Minimum send rate Steam will attempt.");

            ConfigSendRateMax = Config.Bind(
                "Networking.Steamworks",
                "Send Rate Max",
                SendRateMaxOptions._1024KB,
                "Maximum send rate Steam will attempt.");

            ConfigQueueSize = Config.Bind(
                "Networking",
                "Queue Size",
                QueueSizeOptions._32KB,
                "Send queue size. Higher helps high-player servers.");

            // Default to Steamworks only (crossplay OFF)
            ConfigForceCrossplay = Config.Bind(
                "Dedicated Server",
                "Force Crossplay",
                ForceCrossplayOptions.steamworks,
                "Requires restart.\n" +
                "steamworks = Force crossplay DISABLED (Steam friends only)\n" +
                "playfab = Force crossplay ENABLED (PlayFab matchmaking)\n" +
                "vanilla = Respect command-line -crossplay flag (default Valheim behavior)");

            ConfigPlayerLimit = Config.Bind(
                "Dedicated Server",
                "Player Limit",
                10,
                new ConfigDescription("Max players on dedicated server. Requires restart.", new AcceptableValueRange<int>(1, 127)));

            ConfigEnableShipFixes = Config.Bind(
                "Ship Fixes",
                "Enable Universal Ship Fixes",
                true,
                "Apply permanent autopilot + jitter fixes to ALL ships.");

            ConfigEnableServerSideShipSimulation = Config.Bind(
                "Ship Fixes",
                "Server-Side Ship Simulation",
                true,
                "Server authoritatively simulates ship physics.");

            ConfigEnableServerAuthority = Config.Bind(
                "Server Authority",
                "Enable Server-Side Simulation",
                true,
                new ConfigDescription(
                    "Makes the server fully authoritative over zones, ZDO ownership, monster AI, events, etc. (does NOT override your existing ship fixes).\n" +
                    "\n" +
                    "WARNING: THIS IS A SERVER-ONLY FEATURE!\n" +
                    "Enabling this on a CLIENT will cause INFINITE LOADING SCREEN.\n" +
                    "The mod automatically disables it on clients regardless of this setting.",
                    null));

            ConfigExtendedZoneRadius = Config.Bind(
                "Server Authority",
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

            // NEW: ZDO Throttling (server-only bandwidth optimization)
            ConfigEnableZDOThrottling = Config.Bind(
                "Server Authority",
                "Enable ZDO Throttling",
                true,
                "Reduce update frequency for distant ZDOs (creatures/structures far away) to save bandwidth.\n" +
                "SERVER-ONLY — no effect on client.");

            ConfigZDOThrottleDistance = Config.Bind(
                "Server Authority",
                "ZDO Throttle Distance",
                500f,
                new ConfigDescription(
                    "Distance (meters) beyond which ZDOs are throttled (lower update rate).\n" +
                    "0 = disable throttling.\n" +
                    "Recommended: 400-600m.",
                    new AcceptableValueRange<float>(0f, 1000f)));

            // NEW: AI LOD Throttling (server-only CPU optimization)
            ConfigEnableAILOD = Config.Bind(
                "Server Authority",
                "Enable AI LOD Throttling",
                true,
                "Reduce FixedUpdate frequency for distant AI (saves server CPU).\n" +
                "Nearby AI stays full speed for smooth combat.\n" +
                "SERVER-ONLY — no effect on client.");

            ConfigAILODNearDistance = Config.Bind(
                "Server Authority",
                "AI LOD Near Distance",
                100f,
                new ConfigDescription("Full-speed AI within this range (meters).", new AcceptableValueRange<float>(50f, 200f)));

            ConfigAILODFarDistance = Config.Bind(
                "Server Authority",
                "AI LOD Far Distance",
                300f,
                new ConfigDescription("Beyond this distance, AI is throttled (meters).", new AcceptableValueRange<float>(200f, 600f)));

            ConfigAILODThrottleFactor = Config.Bind(
                "Server Authority",
                "AI LOD Throttle Factor",
                0.5f,
                new ConfigDescription("Update multiplier for throttled AI (0.5 = half speed, 0.25 = quarter). Lower = more savings.", new AcceptableValueRange<float>(0.25f, 0.75f)));

            ZDOMemoryManager.ConfigMaxZDOs = Config.Bind(
                "Advanced",
                "Max Active ZDOs",
                500000,
                new ConfigDescription(
                    "If the number of active ZDOs exceeds this value, the mod will force cleanup of orphan non-persistent ZDOs and run garbage collection.\n" +
                    "Set to 0 to disable. Useful on very long-running servers with high entity counts.\n" +
                    "Default: 500000 (vanilla rarely goes above ~200k).",
                    new AcceptableValueRange<int>(0, 1000000)));

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
        ConfigEnableShipFixes,
        ConfigEnableServerSideShipSimulation,
        ConfigEnableServerAuthority,
        ConfigExtendedZoneRadius,
        ConfigEnableZDOThrottling,
        ConfigZDOThrottleDistance,
        ConfigEnableAILOD,
        ConfigAILODNearDistance,
        ConfigAILODFarDistance,
        ConfigAILODThrottleFactor,
        ZDOMemoryManager.ConfigMaxZDOs
            };

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

        private void Start()
        {
            StartCoroutine(RegisterDummyRpcWhenReady());
        }

        

        private IEnumerator RegisterDummyRpcWhenReady()
        {
            while (ZRoutedRpc.instance == null)
                yield return null;

            if (_dummyRpcRegistered)
            {
                Logger.LogInfo("Dummy ForceUpdateZDO RPC already registered — skipping.");
                yield break;
            }

            ZRoutedRpc.instance.Register("ForceUpdateZDO", (Action<long>)((sender) => { }));
            _dummyRpcRegistered = true;
            Logger.LogInfo("Dummy ForceUpdateZDO RPC registered.");
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
        [Description("100% - 20 updates/sec [default]")]
        _100,
        [Description("75% - 15 updates/sec")]
        _75,
        [Description("50% - 10 updates/sec")]
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
        [Description("1024 KB/s | 8 Mbit/s [default]")]
        _1024KB,
        [Description("768 KB/s | 6 Mbit/s")]
        _768KB,
        [Description("512 KB/s | 4 Mbit/s")]
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