using System;
using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Modules.ClientLogRelay;
using VerdantsAscent.Modules.ClientLogRelay.Consumers;
using VerdantsAscent.Modules.ClientLogRelay.Interactions;
using VerdantsAscent.Modules.ClientLogRelay.Transport;
using VerdantsAscent.Modules.ClientLogRelay.Webhook;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Wires the drop-in <see cref="ClientLogRelay"/> module into this mod using the
    /// push-model prescribed by <c>ClientLogRelay/README.md</c>: the client sends an
    /// unsolicited snapshot of its <c>LogOutput.log</c> + plugin list to the server
    /// shortly after the peer-info handshake completes. There is no request/response
    /// dance � one RPC name, one direction.
    ///
    /// On a dedicated server:
    ///   1. Registers the Disk + Discord consumers with <see cref="ClientLogRelay"/>.
    ///   2. Registers a routed RPC "VAG_SubmitClientLog" that deserialises the payload,
    ///      builds a <see cref="ClientLogArtifacts"/>, and hands it to
    ///      <see cref="ClientLogRelay.ReportArtifacts"/>.
    ///
    /// On a client:
    ///   1. After <c>ZNet.RPC_PeerInfo</c> confirms the connection, a coroutine waits a
    ///      few seconds for BepInEx-side init to settle, then uses
    ///      <see cref="ClientLogCollector.TryCollect"/> to snapshot
    ///      <c>LogOutput.log</c> + <c>Chainloader.PluginInfos</c>, and pushes them to the
    ///      server via <see cref="ZRoutedRpc.InvokeRoutedRPC(long, string, object[])"/>
    ///      targeting <see cref="ZRoutedRpc.GetServerPeerID"/>.
    ///
    /// Webhook URLs and toggles are bound under the [Client Log Relay] / [Server Status]
    /// config sections. <see cref="PrimaryWebhookUrl"/> is the server admin's Discord;
    /// <see cref="SecondaryWebhookUrl"/> is an optional second destination (e.g. remote
    /// diagnostics) that receives a copy of every login snapshot. Both default to empty �
    /// no Discord traffic happens until an admin opts in.
    /// </summary>
    [HarmonyPatch]
    internal static class ClientLogRelayIntegration
    {
        // Subfolder under {BepInEx config}/ used by ClientLogRelayPaths. The relay will
        // produce {ConfigPath}/VAGhettoNetworking/ClientLogs/{player_platform}/{files}.
        private const string MOD_ID = "VAGhettoNetworking";

        // Short display label used in Discord embeds, file headers, and log breadcrumbs
        // in place of the generic "ClientLogRelay" string.
        private const string PLUGIN_BRAND = "FiresGhettoNetworking";

        // Single routed RPC name. Kept short to save ZPackage bytes.
        private const string RPC_SUBMIT = "VAG_SubmitClientLog";
        private const string RPC_UPDATE = "VAG_UpdateClientLog";

        // How long the client waits after peer-info completion before reading LogOutput.log
        // and pushing it. Gives BepInEx / Jotunn / other mods a moment to finish their own
        // connect-time logging so the snapshot captures as much as possible.
        private const float CLIENT_PUSH_DELAY_SECONDS = 15f;

        // How often the client re-pushes its log to keep the server's disk copy current.
        // This runs silently � no Discord post, just a disk overwrite.
        private const float CLIENT_REPUSH_INTERVAL_SECONDS = 15f * 60f; // 15 minutes

        // === Anti-spam debounce (server side) ===
        private const float GLOBAL_MIN_POST_INTERVAL_SECONDS = 10f;

        // ====================== CONFIG ENTRIES ======================
        private const string CFG_SECTION = "Client Log Relay";

        public static ConfigEntry<bool>   ConfigEnableLogRelay;
        public static ConfigEntry<bool>   ConfigAutoSendLog;
        public static ConfigEntry<string> ConfigWebhookUrl;
        public static ConfigEntry<string> ConfigSecondaryWebhookUrl;
        public static ConfigEntry<string> ConfigBotToken;

        // Server Status / Heartbeat
        public static ConfigEntry<string> ConfigStatusWebhookUrl;
        public static ConfigEntry<string> ConfigStatusChannelId;

        private static void BindConfigs(ConfigFile config)
        {
            ConfigEnableLogRelay = config.Bind(
                CFG_SECTION,
                "Enable Client Log Relay",
                true,
                "Master toggle. When disabled the server will not collect or post client logs.");

            ConfigAutoSendLog = config.Bind(
                CFG_SECTION,
                "Auto Send Full Log",
                false,
                "When true the full BepInEx log is attached to the initial Discord snapshot.\n" +
                "When false (default) only the summary, mod list, and error report are posted;\n" +
                "admins can click the reaction on the snapshot message to request the full log.");

            ConfigWebhookUrl = config.Bind(
                CFG_SECTION,
                "Discord Webhook URL",
                string.Empty,
                "Primary Discord webhook URL for posting client-login snapshots.\n" +
                "This is your own server's Discord channel. Leave empty to disable\n" +
                "Discord posting entirely (disk capture still runs).");

            ConfigSecondaryWebhookUrl = config.Bind(
                CFG_SECTION,
                "Secondary Discord Webhook URL",
                string.Empty,
                "Optional secondary Discord webhook URL that receives a copy of every\n" +
                "login snapshot in addition to the primary. Useful for forwarding logs\n" +
                "to a remote diagnostics channel (e.g. the mod author's Discord) so the\n" +
                "server owner can opt in to remote support without granting any access.\n" +
                "Reaction-based on-demand log requests are NOT relayed here � those run\n" +
                "against the primary channel only. Leave empty to disable.");

            ConfigBotToken = config.Bind(
                CFG_SECTION,
                "Discord Bot Token",
                string.Empty,
                "(Optional) Discord bot token. When provided, the bot will pre-react on each\n" +
                "snapshot message with the \ud83d\udce9 emoji so admins can simply click it\n" +
                "to request the full log. Leave empty to disable auto-reaction.");

            ConfigStatusWebhookUrl = config.Bind(
                "Server Status",
                "Status Webhook URL",
                string.Empty,
                "Discord webhook URL for the server status channel.\n" +
                "This is where the live-updating \u2018Server Online\u2019 heartbeat message\n" +
                "and the \uD83D\uDD04 Restart / \u26D4 Stop control panel reactions are posted.\n" +
                "Leave empty to disable the server status poster.");

            ConfigStatusChannelId = config.Bind(
                "Server Status",
                "Status Channel ID",
                string.Empty,
                "Discord channel ID for the server status channel.\n" +
                "Required for the bot to edit the heartbeat message and poll reactions.\n" +
                "Right-click the channel in Discord \u2192 Copy Channel ID.\n" +
                "If empty, the channel will be auto-detected from the webhook post response.");
        }

        /// <summary>Server admin's own Discord webhook. Empty disables Discord posting.</summary>
        private static string PrimaryWebhookUrl
            => ConfigWebhookUrl?.Value ?? string.Empty;

        /// <summary>Optional remote-diagnostics webhook. Empty means single-destination.</summary>
        private static string SecondaryWebhookUrl
            => ConfigSecondaryWebhookUrl?.Value ?? string.Empty;

        private static readonly HashSet<long> _postedPeers = new HashSet<long>();
        private static readonly object _debounceLock = new object();
        private static DateTime _lastPostUtc = DateTime.MinValue;

        // Client-side: set once per ZNet session so we only push one snapshot per connect.
        // Reset whenever a fresh ZNet is awakened (i.e. on reconnect / menu round-trip).
        private static bool _clientPushedThisSession;

        private static bool _initialized;
        private static bool _isServerSide;

        public static void InitServer(ConfigFile config)
        {
            if (_initialized) return;
            _initialized = true;
            _isServerSide = true;

            BindConfigs(config);

            if (ConfigEnableLogRelay != null && !ConfigEnableLogRelay.Value)
            {
                LoggerOptions.LogInfo("[ClientLogRelay] Disabled by config � skipping initialisation.");
                return;
            }

            // Register each consumer independently so a failure in one cannot take the
            // other down. Server-only deployments simply end up with an empty ClientLogs
            // folder if no clients ever push, which is harmless.
            TryRun("RegisterDiskConsumer",    RegisterDiskConsumer);
            TryRun("RegisterDiscordConsumer", RegisterDiscordConsumer);
            TryRun("StartReactionPoller",     StartReactionPoller);
            TryRun("StartServerStatusPoster", StartServerStatusPoster);
            LoggerOptions.LogInfo("[ClientLogRelay] Server-side integration initialised.");
        }

        public static void InitClient(ConfigFile config)
        {
            if (_initialized) return;
            _initialized = true;
            _isServerSide = false;

            BindConfigs(config);
            LoggerOptions.LogInfo("[ClientLogRelay] Client-side integration initialised (will push log on connect).");
        }

        /// <summary>
        /// Safe wrapper around a named init step. Any exception is caught and logged so
        /// that one failed consumer/registration never aborts the rest of the module or
        /// the plugin's <c>Awake</c>.
        /// </summary>
        private static void TryRun(string label, Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[ClientLogRelay] {label} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Root directory for persisted client-log artifacts:
        /// <c>{BepInEx config}/VAGhettoNetworking/ClientLogs/</c>. The path helper creates
        /// it on first access, so we don't need to pre-create it manually.
        /// </summary>
        public static string ClientLogsRoot
            => ClientLogRelayPaths.GetDefaultClientLogsDir(MOD_ID);

        private static void RegisterDiskConsumer()
        {
            var disk = new DiskConsumer(
                consumerId:       "VAGhetto.Disk",
                rootDirResolver:  () => ClientLogRelayPaths.GetDefaultClientLogsDir(MOD_ID),
                enabledGate:      () => true);

            ClientLogRelay.RegisterConsumer(disk);
            LoggerOptions.LogInfo($"[ClientLogRelay] Disk consumer will persist artifacts under: {ClientLogsRoot}");
        }

        private static void RegisterDiscordConsumer()
        {
            bool ReactionEnabled() => !ConfigAutoSendLog.Value;

            // Primary: server admin's own Discord. Carries the interactive features
            // (reaction-based on-demand full-log requests, bot pre-react).
            var primary = new DiscordWebhookConsumer("VAGhetto.Discord.Primary")
            {
                EnabledGate              = () => ConfigEnableLogRelay.Value
                                                 && !string.IsNullOrEmpty(PrimaryWebhookUrl),
                WebhookUrl               = () => PrimaryWebhookUrl,
                WebhookName              = () => $"{PLUGIN_BRAND} Relay",
                BrandLabel               = () => PLUGIN_BRAND,
                AttachFullLog            = () => ConfigAutoSendLog.Value,
                AttachModList            = () => true,
                AttachErrorsWarnings     = () => true,
                OnlyIfErrorsOrWarnings   = () => false,
                EnableLogRequestReaction = ReactionEnabled,
                BotToken                 = () => ConfigBotToken?.Value,
                ServerName               = () => ZNet.instance != null ? ZNet.instance.GetWorldName() : "Unknown",
            };
            ClientLogRelay.RegisterConsumer(primary);

            // Secondary: optional remote-diagnostics destination. Receives the same
            // login snapshot fire-and-forget � no reaction wiring (the poller can only
            // watch one channel) and no bot token (bot is configured for the primary's
            // guild). Silently no-ops while the secondary URL is empty.
            var secondary = new DiscordWebhookConsumer("VAGhetto.Discord.Secondary")
            {
                EnabledGate              = () => ConfigEnableLogRelay.Value
                                                 && !string.IsNullOrEmpty(SecondaryWebhookUrl),
                WebhookUrl               = () => SecondaryWebhookUrl,
                WebhookName              = () => $"{PLUGIN_BRAND} Relay",
                BrandLabel               = () => PLUGIN_BRAND,
                AttachFullLog            = () => ConfigAutoSendLog.Value,
                AttachModList            = () => true,
                AttachErrorsWarnings     = () => true,
                OnlyIfErrorsOrWarnings   = () => false,
                EnableLogRequestReaction = () => false,
                BotToken                 = () => null,
                ServerName               = () => ZNet.instance != null ? ZNet.instance.GetWorldName() : "Unknown",
            };
            ClientLogRelay.RegisterConsumer(secondary);
        }

        private static void StartReactionPoller()
        {
            string token = ConfigBotToken?.Value;
            if (string.IsNullOrEmpty(token))
            {
                LoggerOptions.LogInfo("[ClientLogRelay] No bot token configured � reaction poller disabled.");
                return;
            }

            if (string.IsNullOrEmpty(PrimaryWebhookUrl))
            {
                LoggerOptions.LogInfo("[ClientLogRelay] No primary webhook URL configured \u2014 reaction poller disabled.");
                return;
            }

            ReactionPoller.Create(
                botTokenResolver:       () => ConfigBotToken?.Value,
                webhookUrlResolver:     () => PrimaryWebhookUrl,
                clientLogsRootResolver: () => ClientLogsRoot,
                webhookNameResolver:    () => $"{PLUGIN_BRAND} Relay");

            LoggerOptions.LogInfo("[ClientLogRelay] Reaction poller started \u2014 polling every 15s for log requests.");
        }

        private static void StartServerStatusPoster()
        {
            string token = ConfigBotToken?.Value;
            string statusUrl = ConfigStatusWebhookUrl?.Value;

            if (string.IsNullOrEmpty(statusUrl))
            {
                LoggerOptions.LogInfo("[ClientLogRelay] No status webhook URL configured \u2014 server status poster disabled.");
                return;
            }

            if (string.IsNullOrEmpty(token))
            {
                LoggerOptions.LogInfo("[ClientLogRelay] No bot token configured \u2014 server status poster disabled (bot token required for heartbeat edits + reaction polling).");
                return;
            }

            // Don't call ServerHeartbeat.OnServerStart here - wait for ZNet to be ready
            LoggerOptions.LogInfo("[ClientLogRelay] Server heartbeat configured - will start when ZNet is ready.");
        }

        // ====================================================================
        // Harmony entry points � register server RPC once ZNet is up; on the client,
        // reset the per-session push-dedupe flag so a reconnect can push again.
        // Both postfixes are guarded so any failure is swallowed locally and cannot
        // propagate into Valheim's ZNet code.
        // ====================================================================
        [HarmonyPatch(typeof(ZNet), "Awake")]
        [HarmonyPostfix]
        public static void ZNet_Awake_Postfix()
        {
            try
            {
                if (ZRoutedRpc.instance == null)
                {
                    LoggerOptions.LogWarning("[ClientLogRelay] ZNet.Awake postfix fired but ZRoutedRpc.instance is null \u2014 RPC registration skipped.");
                    return;
                }

                if (_isServerSide)
                {
                    ZRoutedRpc.instance.Register<ZPackage>(RPC_SUBMIT, OnServerReceiveLog);
                    ZRoutedRpc.instance.Register<ZPackage>(RPC_UPDATE, OnServerReceiveLogUpdate);
                    LoggerOptions.LogInfo($"[ClientLogRelay] Registered server-side handlers for '{RPC_SUBMIT}' + '{RPC_UPDATE}'.");

                    // Start periodic cleanup of timed-out chunked transfers
                    ZNet.instance.StartCoroutine(PeriodicTransferCleanup());

                    // Start ServerHeartbeat now that ZNet is ready
                    string token = ConfigBotToken?.Value;
                    string statusUrl = ConfigStatusWebhookUrl?.Value;
                    if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(statusUrl))
                    {
                        ServerHeartbeat.OnServerStart(
                            botToken:          token,
                            statusWebhookUrl:  statusUrl,
                            channelIdOverride: ConfigStatusChannelId?.Value,
                            webhookName:       $"{PLUGIN_BRAND} Relay",
                            brandLabel:        PLUGIN_BRAND);
                        LoggerOptions.LogInfo("[ClientLogRelay] Server heartbeat + control panel started.");
                    }
                }
                else
                {
                    // Fresh ZNet session on the client � allow a new push.
                    _clientPushedThisSession = false;
                    LoggerOptions.LogInfo("[ClientLogRelay] Client-side ZNet awake \u2014 push-on-connect armed.");
                }
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[ClientLogRelay] ZNet.Awake postfix threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Client-side only: ZNet.RPC_PeerInfo fires here when the server has sent us its
        // peer info, i.e. the handshake completed successfully. Schedule the push. The
        // server's own RPC_PeerInfo invocation also runs this postfix, but _isServerSide
        // gates it off so server-only deployments simply do nothing here.
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        [HarmonyPostfix]
        public static void ZNet_RPC_PeerInfo_Postfix(ZNet __instance, ZRpc rpc)
        {
            try
            {
                if (_isServerSide || __instance == null || rpc == null) return;
                if (_clientPushedThisSession)
                {
                    LoggerOptions.LogInfo("[ClientLogRelay] PeerInfo postfix fired but client already pushed this session \u2014 skipping.");
                    return;
                }

                _clientPushedThisSession = true;
                LoggerOptions.LogInfo($"[ClientLogRelay] PeerInfo handshake complete \u2014 scheduling log push in {CLIENT_PUSH_DELAY_SECONDS:F0}s.");
                __instance.StartCoroutine(DelayedClientPush());
                __instance.StartCoroutine(PeriodicClientRepush());
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[ClientLogRelay] ZNet.RPC_PeerInfo postfix threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static IEnumerator DelayedClientPush()
        {
            yield return new WaitForSeconds(CLIENT_PUSH_DELAY_SECONDS);

            if (ZNet.instance == null)
            {
                LoggerOptions.LogInfo("[ClientLogRelay] ZNet.instance gone before push \u2014 cancelling.");
                yield break;
            }

            if (ZRoutedRpc.instance == null)
            {
                LoggerOptions.LogWarning("[ClientLogRelay] ZRoutedRpc.instance null at push time \u2014 cancelling.");
                yield break;
            }

            long serverId = ZRoutedRpc.instance.GetServerPeerID();
            if (serverId == 0L)
            {
                LoggerOptions.LogWarning("[ClientLogRelay] ServerPeerID is 0 at push time \u2014 connection no longer valid; cancelling.");
                yield break;
            }

            PushLogToServer(serverId);
        }

        private static void PushLogToServer(long serverId)
        {
            try
            {
                byte[] logBytes;
                Dictionary<string, string> modList;
                ClientLogCollector.TryCollect(out logBytes, out modList);
                if (modList == null) modList = new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);

                // Steamworks.NET version can differ between clients; isolate so a bad
                // assembly bind there cannot abort the whole push.
                string steamId = string.Empty;
                try { steamId = GetOwnSteamId() ?? string.Empty; }
                catch (Exception sidEx)
                {
                    LoggerOptions.LogInfo($"[ClientLogRelay] SteamID unavailable ({sidEx.GetType().Name}: {sidEx.Message}); pushing without it.");
                }

                logBytes = logBytes ?? new byte[0];
                int logSize = logBytes.Length;

                // REDESIGNED CHUNKING SYSTEM:
                // Instead of jamming everything into the first chunk, we send metadata separately.
                // This keeps ALL chunks under the 512KB Steam limit.
                //
                // Protocol:
                // 1. Send metadata message (mod list, steam ID, total chunks)
                // 2. Send N pure data chunks (ONLY log bytes, no metadata)
                //
                // Benefits:
                // - No chunk needs to carry heavy metadata
                // - All chunks can be same size (simpler logic)
                // - Much safer margins (no 200KB mod list overhead)

                const int CHUNK_SIZE = 400 * 1024;  // 400KB per chunk

                int totalChunks = logSize == 0 ? 1 : (logSize + CHUNK_SIZE - 1) / CHUNK_SIZE;

                LoggerOptions.LogInfo($"[ClientLogRelay] Pushing log to server: {logSize}B in {totalChunks} chunk(s) + metadata, mods={modList.Count}");

                // STEP 1: Send metadata separately (mod list, steam ID)
                {
                    ZPackage metaPayload = new ZPackage();
                    metaPayload.Write(-1);  // Special marker: this is metadata, not a chunk
                    metaPayload.Write(totalChunks);
                    metaPayload.Write(modList.Count);
                    foreach (var kv in modList)
                    {
                        metaPayload.Write(kv.Key ?? string.Empty);
                        metaPayload.Write(kv.Value ?? string.Empty);
                    }
                    metaPayload.Write(steamId ?? string.Empty);

                    byte[] metaData = metaPayload.GetArray();
                    if (metaData != null && metaData.Length > 420000)
                    {
                        LoggerOptions.LogWarning($"[ClientLogRelay] ?? Metadata ZPackage is {metaData.Length}B - very large mod list!");
                    }

                    ZRoutedRpc.instance.InvokeRoutedRPC(serverId, RPC_SUBMIT, metaPayload);
                    LoggerOptions.LogInfo($"[ClientLogRelay]  ? Sent metadata ({metaData?.Length ?? 0}B total, {modList.Count} mods)");
                }

                // STEP 2: Send pure data chunks (NO metadata, just log bytes)
                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    ZPackage payload = new ZPackage();
                    payload.Write(chunkIndex);
                    payload.Write(totalChunks);

                    int offset = chunkIndex * CHUNK_SIZE;
                    int size = Math.Min(CHUNK_SIZE, logSize - offset);
                    byte[] chunk = new byte[size];
                    if (size > 0)
                    {
                        Array.Copy(logBytes, offset, chunk, 0, size);
                    }
                    payload.Write(chunk);

                    // Safety check
                    byte[] packageData = payload.GetArray();
                    if (packageData != null && packageData.Length > 420000)
                    {
                        LoggerOptions.LogWarning($"[ClientLogRelay] ?? CRITICAL: Chunk {chunkIndex + 1} ZPackage is {packageData.Length}B!");
                    }

                    ZRoutedRpc.instance.InvokeRoutedRPC(serverId, RPC_SUBMIT, payload);

                    if (totalChunks > 1)
                    {
                        LoggerOptions.LogInfo($"[ClientLogRelay]  ? Sent chunk {chunkIndex + 1}/{totalChunks} ({size}B data, {packageData?.Length ?? 0}B total)");
                    }
                }

                LoggerOptions.LogInfo($"[ClientLogRelay] ? Push complete: {logSize}B in {totalChunks} chunk(s)");
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[ClientLogRelay] Failed to push log to server: {ex.Message}");
            }
        }

        /// <summary>
        /// Coroutine that re-pushes the client log every 15 minutes via <see cref="RPC_UPDATE"/>
        /// so the server's cached disk copy stays current throughout the session.
        /// </summary>
        private static IEnumerator PeriodicClientRepush()
        {
            while (true)
            {
                yield return new WaitForSeconds(CLIENT_REPUSH_INTERVAL_SECONDS);

                // Skip this iteration if not connected, but keep trying
                if (ZNet.instance == null || ZRoutedRpc.instance == null)
                {
                    LoggerOptions.LogInfo("[ClientLogRelay] Repush skipped: ZNet or ZRoutedRpc is null (will retry next interval)");
                    continue;  // ? Skip this iteration, keep looping
                }

                long serverId = ZRoutedRpc.instance.GetServerPeerID();
                if (serverId == 0L)
                {
                    LoggerOptions.LogInfo("[ClientLogRelay] Repush skipped: not connected to server (will retry next interval)");
                    continue;  // ? Skip this iteration, keep looping
                }

                RepushLogToServer(serverId);
            }
        }

        /// <summary>
        /// Server-side coroutine that periodically cleans up timed-out chunked transfers.
        /// </summary>
        private static IEnumerator PeriodicTransferCleanup()
        {
            while (true)
            {
                yield return new WaitForSeconds(30f);
                ClientLogChunkedTransfer.CleanupTimedOutTransfers();
            }
        }

        private static void RepushLogToServer(long serverId)
        {
            try
            {
                byte[] logBytes;
                Dictionary<string, string> modList;
                ClientLogCollector.TryCollect(out logBytes, out modList);

                string steamId = string.Empty;
                try { steamId = GetOwnSteamId() ?? string.Empty; }
                catch { /* Steamworks not available */ }

                // CRITICAL FIX: Repush was sending entire log in one ZPackage!
                // After 1 hour, logs are 600KB+ and exceed Steam's 512KB limit.
                // Now we use the SAME chunking system as initial upload.

                logBytes = logBytes ?? new byte[0];
                int logSize = logBytes.Length;

                // If log is small enough, send in single message (backward compat)
                if (logSize <= 400 * 1024)
                {
                    ZPackage payload = new ZPackage();
                    payload.Write(logBytes);
                    payload.Write(steamId);

                    ZRoutedRpc.instance.InvokeRoutedRPC(serverId, RPC_UPDATE, payload);
                    LoggerOptions.LogInfo($"[ClientLogRelay] ? Re-pushed '{RPC_UPDATE}' to server: {logSize}B (single message)");
                }
                else
                {
                    // Large log - use chunking (same as initial upload but to RPC_UPDATE)
                    // NOTE: We don't send mod list in repush (server already has it from initial upload)
                    const int CHUNK_SIZE = 400 * 1024;
                    int totalChunks = (logSize + CHUNK_SIZE - 1) / CHUNK_SIZE;

                    LoggerOptions.LogInfo($"[ClientLogRelay] Re-pushing large log to server: {logSize}B in {totalChunks} chunk(s)");

                    for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                    {
                        ZPackage payload = new ZPackage();
                        payload.Write(chunkIndex);
                        payload.Write(totalChunks);

                        int offset = chunkIndex * CHUNK_SIZE;
                        int size = Math.Min(CHUNK_SIZE, logSize - offset);
                        byte[] chunk = new byte[size];
                        if (size > 0)
                        {
                            Array.Copy(logBytes, offset, chunk, 0, size);
                        }
                        payload.Write(chunk);
                        payload.Write(steamId);

                        ZRoutedRpc.instance.InvokeRoutedRPC(serverId, RPC_UPDATE, payload);

                        if (totalChunks > 1)
                        {
                            LoggerOptions.LogInfo($"[ClientLogRelay]  ? Re-pushed chunk {chunkIndex + 1}/{totalChunks} ({size}B)");
                        }
                    }

                    LoggerOptions.LogInfo($"[ClientLogRelay] ? Re-push complete: {logSize}B in {totalChunks} chunk(s)");
                }
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[ClientLogRelay] Failed to re-push log: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears all server-side debounce state so every currently connected peer is
        /// eligible for a fresh post. Intended for manual dev-console invocation.
        /// </summary>
        public static void ResetDebounce()
        {
            lock (_debounceLock)
            {
                int p = _postedPeers.Count;
                _postedPeers.Clear();
                _lastPostUtc = DateTime.MinValue;
                LoggerOptions.LogInfo($"[ClientLogRelay] Debounce reset (cleared {p} posted peers).");
            }
        }

        // ====================================================================
        // Server side: receive log from a client and hand off to the relay.
        // ====================================================================
        private static void OnServerReceiveLog(long sender, ZPackage pkg)
        {
            try
            {
                if (pkg == null)
                {
                    LoggerOptions.LogWarning($"[ClientLogRelay] Submit from peer {sender} had a null payload.");
                    return;
                }

                // Read chunk header
                int chunkIndex = pkg.ReadInt();
                int totalChunks = pkg.ReadInt();

                // NEW PROTOCOL: chunkIndex = -1 means this is metadata
                if (chunkIndex == -1)
                {
                    // This is the metadata message (mod list + steam ID)
                    int modCount = pkg.ReadInt();
                    if (modCount < 0) modCount = 0;
                    Dictionary<string, string> metaModList = new Dictionary<string, string>(modCount, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < modCount; i++)
                    {
                        string guid = pkg.ReadString();
                        string ver = pkg.ReadString();
                        if (!string.IsNullOrEmpty(guid)) metaModList[guid] = ver ?? string.Empty;
                    }
                    string metaSteamId = null;
                    try { metaSteamId = pkg.ReadString(); } catch { }

                    // Store metadata for this peer (will be used when chunks arrive)
                    ClientLogChunkedTransfer.StoreMetadata(sender, totalChunks, metaModList, metaSteamId);

                    LoggerOptions.LogInfo($"[ClientLogRelay] ? Received metadata from peer {sender}: {metaModList.Count} mods, expecting {totalChunks} chunk(s)");
                    return;
                }

                // This is a regular data chunk
                byte[] chunkData = pkg.ReadByteArray();

                LoggerOptions.LogInfo($"[ClientLogRelay] ? Received chunk {chunkIndex + 1}/{totalChunks} from peer {sender} ({chunkData.Length}B)");

                // Process chunk through chunked transfer system (no metadata in chunks anymore)
                var result = ClientLogChunkedTransfer.ReceiveChunk(
                    sender, chunkIndex, totalChunks, chunkData, null, null);

                // If not complete yet, return and wait for more chunks
                if (result == null)
                {
                    return;
                }

                // Transfer complete - extract data from result
                byte[] fullLog = result.LogBytes;
                Dictionary<string, string> modList = result.ModList;
                string reportedSteamId = result.SteamId;
                if (modList == null) modList = new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);

                LoggerOptions.LogInfo($"[ClientLogRelay] ? Complete log received from peer {sender}: {fullLog.Length}B, {modList.Count} mods");

                string playerName = GetPeerPlayerName(sender);
                string platformId = !string.IsNullOrEmpty(reportedSteamId)
                    ? reportedSteamId
                    : sender.ToString();

                // Debounce layer 1: per-peer once per session.
                lock (_debounceLock)
                {
                    if (!_postedPeers.Add(sender))
                    {
                        LoggerOptions.LogInfo($"[ClientLogRelay] Dropping duplicate submission from '{playerName}' ({platformId}) \u2014 already posted this session.");
                        return;
                    }

                    // Debounce layer 2: global minimum interval between any two posts.
                    TimeSpan sinceLast = DateTime.UtcNow - _lastPostUtc;
                    if (sinceLast.TotalSeconds < GLOBAL_MIN_POST_INTERVAL_SECONDS)
                    {
                        // Re-allow this peer so a later, non-throttled submission isn't swallowed.
                        _postedPeers.Remove(sender);
                        LoggerOptions.LogInfo($"[ClientLogRelay] Rate-limiting post from '{playerName}' ({platformId}); {sinceLast.TotalSeconds:F1}s since last post (floor {GLOBAL_MIN_POST_INTERVAL_SECONDS:F0}s).");
                        return;
                    }

                    _lastPostUtc = DateTime.UtcNow;
                }

                // Snapshot the server's own plugin list so the consumer can produce a
                // client-vs-server diff alongside the per-player mod list. Safe on the
                // dedicated server � ClientLogCollector just walks Chainloader.PluginInfos.
                Dictionary<string, string> serverMods = null;
                try { serverMods = ClientLogCollector.BuildLocalModList(); }
                catch (Exception ex)
                {
                    LoggerOptions.LogInfo($"[ClientLogRelay] Could not enumerate server-side mod list: {ex.GetType().Name}: {ex.Message}");
                }

                var artifacts = new ClientLogArtifacts(
                    platformId, playerName, fullLog, modList,
                    serverMods, PLUGIN_BRAND);
                ClientLogRelay.ReportArtifacts(artifacts);

                // Track peer?identity for disconnect hook.
                lock (_debounceLock)
                {
                    _peerToPlatformId[sender] = platformId;
                    _peerToPlayerName[sender] = playerName;
                }

                LoggerOptions.LogInfo($"[ClientLogRelay] Processed log from '{playerName}' ({platformId}): {fullLog.Length} bytes, {modList.Count} client mods, {(serverMods != null ? serverMods.Count : 0)} server mods.");
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[ClientLogRelay] Failed to process submitted log: {ex.Message}");
            }
        }

        // ====================================================================
        // Server side: silent log update (periodic re-push from client).
        // Overwrites ALL disk artifacts (log, errors/warnings, etc.) so that
        // on-demand reactions and disconnect capture always read the latest data.
        // No Discord post, no debounce check.
        // NEW: Now supports chunked updates for large logs!
        // ====================================================================
        private static void OnServerReceiveLogUpdate(long sender, ZPackage pkg)
        {
            try
            {
                if (pkg == null) return;

                // Check if this is a chunked update (new protocol) or single message (legacy)
                // We peek at the first int to see if it's a chunk index
                pkg.SetPos(0); // Reset to start
                int firstInt = pkg.ReadInt();
                pkg.SetPos(0); // Reset again for actual reading

                byte[] log;
                string reportedSteamId = null;

                // If firstInt is 0-999, it's probably a chunk index (chunked protocol)
                // If it's > 100000, it's probably the byte array length (legacy protocol)
                bool isChunked = firstInt >= 0 && firstInt < 1000;

                if (isChunked)
                {
                    // NEW CHUNKED PROTOCOL
                    int chunkIndex = pkg.ReadInt();
                    int totalChunks = pkg.ReadInt();
                    byte[] chunkData = pkg.ReadByteArray();
                    try { reportedSteamId = pkg.ReadString(); } catch { }

                    LoggerOptions.LogInfo($"[ClientLogRelay] ? Received update chunk {chunkIndex + 1}/{totalChunks} from peer {sender} ({chunkData.Length}B)");

                    // Use chunked transfer system for updates too
                    var result = ClientLogChunkedTransfer.ReceiveUpdateChunk(
                        sender, chunkIndex, totalChunks, chunkData, reportedSteamId);

                    // If not complete yet, return and wait for more chunks
                    if (result == null)
                    {
                        return;
                    }

                    log = result.LogBytes;
                    reportedSteamId = result.SteamId;
                    LoggerOptions.LogInfo($"[ClientLogRelay] ? Complete update received from peer {sender}: {log.Length}B");
                }
                else
                {
                    // LEGACY SINGLE-MESSAGE PROTOCOL (backward compat)
                    log = pkg.ReadByteArray();
                    try { reportedSteamId = pkg.ReadString(); } catch { }
                }

                string playerName = GetPeerPlayerName(sender);
                string platformId = !string.IsNullOrEmpty(reportedSteamId)
                    ? reportedSteamId
                    : sender.ToString();

                if (log == null || log.Length == 0)
                {
                    LoggerOptions.LogInfo($"[ClientLogRelay] Update from '{playerName}' ({platformId}) had empty log � skipping.");
                    return;
                }

                string logsRoot = ClientLogsRoot;
                if (!string.IsNullOrEmpty(logsRoot))
                {
                    // Re-read the existing mod list from the previously saved modlist.txt
                    // so we can rebuild the full artifact set without the client re-sending it.
                    var modList = new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);
                    string safeName = playerName ?? "unknown";
                    var invalid = System.IO.Path.GetInvalidFileNameChars();
                    safeName = string.Concat(safeName.Split(invalid));
                    string safePid = (platformId ?? "unknown").Replace(":", "_").Replace("/", "_").Replace("\\", "_");
                    string existingModListPath = System.IO.Path.Combine(logsRoot, $"{safeName}_{safePid}", "modlist.txt");
                    if (System.IO.File.Exists(existingModListPath))
                    {
                        try
                        {
                            foreach (var line in System.IO.File.ReadAllLines(existingModListPath))
                            {
                                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                                int eq = line.IndexOf('=');
                                if (eq > 0)
                                    modList[line.Substring(0, eq)] = line.Substring(eq + 1);
                            }
                        }
                        catch { /* best-effort */ }
                    }

                    // Build artifacts and run the extractor to regenerate errors_warnings.txt
                    Dictionary<string, string> serverMods = null;
                    try { serverMods = ClientLogCollector.BuildLocalModList(); }
                    catch { /* not critical for an update */ }

                    var artifacts = new ClientLogArtifacts(
                        platformId, playerName, log, modList,
                        serverMods, PLUGIN_BRAND);

                    // Run the extraction so errors/warnings report is populated.
                    try
                    {
                        var extraction = LogErrorWarningExtractor.Extract(log, playerName, platformId);
                        artifacts.ErrorsWarningsReport = extraction.Report;
                        artifacts.ErrorCount = extraction.ErrorCount;
                        artifacts.WarningCount = extraction.WarningCount;
                    }
                    catch { /* non-fatal */ }

                    // Compute mod diff if server mods are available.
                    if (serverMods != null && serverMods.Count > 0 && modList.Count > 0)
                    {
                        try
                        {
                            artifacts.ModDiff = ModListDiff.Compute(
                                modList, serverMods, playerName, platformId,
                                PLUGIN_BRAND, artifacts.CapturedUtc);
                        }
                        catch { /* non-fatal */ }
                    }

                    // Write all artifact files (log, modlist, errors_warnings, mod_diff).
                    ClientLogArtifactWriter.Write(logsRoot, artifacts);
                }

                // Track the mapping from peer UID ? platformId for disconnect lookup.
                lock (_debounceLock)
                {
                    _peerToPlatformId[sender] = platformId;
                    _peerToPlayerName[sender] = playerName;
                }

                LoggerOptions.LogInfo($"[ClientLogRelay] \u2190 Updated all cached artifacts for '{playerName}' ({platformId}): {log.Length} bytes.");
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[ClientLogRelay] Failed to process log update: {ex.Message}");
            }
        }

        // Peer UID ? platformId mapping so the disconnect hook knows who just left.
        // Populated by both OnServerReceiveLog and OnServerReceiveLogUpdate.
        private static readonly Dictionary<long, string> _peerToPlatformId
            = new Dictionary<long, string>();
        private static readonly Dictionary<long, string> _peerToPlayerName
            = new Dictionary<long, string>();

        // ====================================================================
        // Server side: detect peer disconnect and post log if registered.
        // ====================================================================
        [HarmonyPatch(typeof(ZNet), "Disconnect")]
        [HarmonyPrefix]
        public static void ZNet_Disconnect_Prefix(ZNetPeer peer)
        {
            try
            {
                if (!_isServerSide || peer == null) return;

                // Cancel any pending chunked transfer from this peer
                ClientLogChunkedTransfer.CancelTransfer(peer.m_uid);

                string platformId;
                string playerName;
                lock (_debounceLock)
                {
                    _peerToPlatformId.TryGetValue(peer.m_uid, out platformId);
                    _peerToPlayerName.TryGetValue(peer.m_uid, out playerName);
                    _peerToPlatformId.Remove(peer.m_uid);
                    _peerToPlayerName.Remove(peer.m_uid);
                    _postedPeers.Remove(peer.m_uid); // allow re-post on next connect
                }

                if (string.IsNullOrEmpty(platformId))
                    platformId = peer.m_uid.ToString();
                if (string.IsNullOrEmpty(playerName))
                    playerName = string.IsNullOrEmpty(peer.m_playerName) ? "unknown" : peer.m_playerName;

                var entry = DisconnectLogRegistry.TakeIfRegistered(platformId);
                if (entry == null) return;

                LoggerOptions.LogInfo($"[ClientLogRelay] Player '{playerName}' ({platformId}) disconnected � posting session log (requested by {entry.RequestedByName}).");

                // CRITICAL: Don't block the disconnect process with file I/O and HTTP requests!
                // Schedule the log posting as a coroutine to run after disconnect completes.
                if (ZNet.instance != null)
                {
                    ZNet.instance.StartCoroutine(PostDisconnectLogAsync(platformId, playerName, entry.RequestedByName));
                }
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[ClientLogRelay] Disconnect hook threw: {ex.Message}");
            }
        }

        private static IEnumerator PostDisconnectLogAsync(string platformId, string playerName, string requestedByName)
        {
            // Small delay to ensure disconnect cleanup is complete
            yield return new WaitForSeconds(1f);

            try
            {
                PostDisconnectLog(platformId, playerName, requestedByName);
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[ClientLogRelay] PostDisconnectLogAsync threw: {ex.Message}");
            }
        }

        private static void PostDisconnectLog(string platformId, string playerName, string requestedByName)
        {
            try
            {
                string logsRoot = ClientLogsRoot;
                if (string.IsNullOrEmpty(logsRoot)) return;

                string safeName = playerName ?? "unknown";
                var invalid = System.IO.Path.GetInvalidFileNameChars();
                safeName = string.Concat(safeName.Split(invalid));
                string safePid = (platformId ?? "unknown").Replace(":", "_").Replace("/", "_").Replace("\\", "_");
                string logPath = System.IO.Path.Combine(logsRoot, $"{safeName}_{safePid}", "LogOutput.log");

                if (!System.IO.File.Exists(logPath))
                {
                    LoggerOptions.LogWarning($"[ClientLogRelay] No cached log at '{logPath}' for disconnect post.");
                    return;
                }

                byte[] logBytes = System.IO.File.ReadAllBytes(logPath);
                if (logBytes.Length == 0) return;

                var targetUrls = new System.Collections.Generic.List<string>(2);
                foreach (var candidate in new[] { PrimaryWebhookUrl, SecondaryWebhookUrl })
                {
                    if (!string.IsNullOrEmpty(candidate)
                        && MinimalWebhookPoster.IsValidWebhookUrl(candidate))
                    {
                        targetUrls.Add(candidate);
                    }
                }
                if (targetUrls.Count == 0) return;

                string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

                var embed = new MinimalWebhookPoster.Embed()
                    .SetTitle("\u267B\uFE0F Session Log \u2014 Player Disconnected")
                    .SetColor(3066993) // green
                    .AddField("\uD83D\uDC64 Player",   playerName,        true)
                    .AddField("\uD83D\uDD94 Steam ID", platformId ?? "?", true)
                    .AddField("\uD83D\uDCC4 Log Size", FormatBytes(logBytes.Length), true)
                    .SetFooter($"Requested by {requestedByName}");

                // Chunk if large (same 8 MB limit as the reaction poller)
                const int MAX_BYTES = 8 * 1024 * 1024 - 64 * 1024;
                if (logBytes.Length <= MAX_BYTES)
                {
                    var files = new System.Collections.Generic.List<MinimalWebhookPoster.Attachment>
                    {
                        new MinimalWebhookPoster.Attachment($"session_log_{safePid}_{stamp}.log", logBytes, "text/plain")
                    };
                    foreach (var url in targetUrls)
                        MinimalWebhookPoster.Post(url, embed, files, $"{PLUGIN_BRAND} Relay", null);
                }
                else
                {
                    int partNum = 0;
                    int offset = 0;
                    while (offset < logBytes.Length)
                    {
                        partNum++;
                        int len = Math.Min(MAX_BYTES, logBytes.Length - offset);
                        var chunk = new byte[len];
                        Buffer.BlockCopy(logBytes, offset, chunk, 0, len);
                        offset += len;

                        int totalParts = (logBytes.Length + MAX_BYTES - 1) / MAX_BYTES;
                        var partEmbed = new MinimalWebhookPoster.Embed()
                            .SetTitle($"\u267B\uFE0F Session Log \u2014 Part {partNum}/{totalParts}")
                            .SetColor(3066993)
                            .AddField("\uD83D\uDC64 Player", playerName, true)
                            .AddField("\uD83D\uDCC4 Size", FormatBytes(chunk.Length), true)
                            .SetFooter($"Requested by {requestedByName}");

                        var files = new System.Collections.Generic.List<MinimalWebhookPoster.Attachment>
                        {
                            new MinimalWebhookPoster.Attachment($"session_log_{safePid}_{stamp}_part{partNum}.log", chunk, "text/plain")
                        };
                        foreach (var url in targetUrls)
                            MinimalWebhookPoster.Post(url, partEmbed, files, $"{PLUGIN_BRAND} Relay", null);
                    }
                }

                LoggerOptions.LogInfo($"[ClientLogRelay] Posted disconnect session log for '{playerName}' ({platformId}), {logBytes.Length} bytes.");
            }
            catch (Exception ex)
            {
                LoggerOptions.LogWarning($"[ClientLogRelay] Failed to post disconnect log: {ex.Message}");
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return $"{v:0.##} {units[u]}";
        }

        // ====================================================================
        // Helpers
        // ====================================================================
        private static string GetPeerPlayerName(long uid)
        {
            if (ZNet.instance == null) return "unknown";
            foreach (ZNetPeer p in ZNet.instance.GetPeers())
            {
                if (p != null && p.m_uid == uid)
                    return string.IsNullOrEmpty(p.m_playerName) ? "unknown" : p.m_playerName;
            }
            return "unknown";
        }

        /// <summary>
        /// Returns the local user's SteamID64 as a string, or empty if Steamworks isn't
        /// available (e.g. Crossplay/Xbox, non-Steam launches, server-role calls, or a
        /// Steamworks.NET version mismatch between profiles). Uses reflection so this
        /// method can be JIT-compiled even when Steamworks.NET fails to bind.
        /// </summary>
        private static string GetOwnSteamId()
        {
            try
            {
                Type steamApi = Type.GetType("Steamworks.SteamAPI, Steamworks.NET")
                                ?? AccessTools.TypeByName("Steamworks.SteamAPI");
                Type steamUser = Type.GetType("Steamworks.SteamUser, Steamworks.NET")
                                 ?? AccessTools.TypeByName("Steamworks.SteamUser");
                if (steamApi == null || steamUser == null) return string.Empty;

                var isRunning = steamApi.GetMethod("IsSteamRunning",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (isRunning == null) return string.Empty;
                object running = isRunning.Invoke(null, null);
                if (!(running is bool b) || !b) return string.Empty;

                var getId = steamUser.GetMethod("GetSteamID",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (getId == null) return string.Empty;
                object idObj = getId.Invoke(null, null);
                if (idObj == null) return string.Empty;

                // CSteamID is a struct; prefer the m_SteamID field, fall back to ToString().
                var field = idObj.GetType().GetField("m_SteamID",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (field != null)
                {
                    object val = field.GetValue(idObj);
                    if (val != null) return val.ToString();
                }
                return idObj.ToString();
            }
            catch { /* Steamworks not available / version mismatch */ }
            return string.Empty;
        }
    }
}
