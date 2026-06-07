using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using VerdantsAscent.Modules.ClientLogRelay.Consumers;
using VerdantsAscent.Modules.ClientLogRelay.Webhook;

namespace VerdantsAscent.Modules.ClientLogRelay.Interactions
{
    /// <summary>
    /// Periodically polls Discord for new reactions on registered snapshot messages and
    /// posts the requested artifact(s) to the webhook.
    ///
    /// Three reaction types are supported:
    /// <list type="bullet">
    /// <item>?? (<see cref="DiscordWebhookConsumer.EMOJI_LOG"/>)    — full BepInEx log</item>
    /// <item>? (<see cref="DiscordWebhookConsumer.EMOJI_ERRORS"/>) — errors + warnings report</item>
    /// <item>?? (<see cref="DiscordWebhookConsumer.EMOJI_MODS"/>)   — client mod list + diff</item>
    /// </list>
    ///
    /// Large files (&gt; 8 MB) are automatically split into sequential chunks so they stay
    /// within Discord's webhook upload limit for non-boosted servers.
    /// </summary>
    public sealed class ReactionPoller : MonoBehaviour
    {
        private const float POLL_INTERVAL_SECONDS = 30f; // Reduced from 15s to avoid rate limits
        private const float RATE_LIMIT_BACKOFF_SECONDS = 120f; // Increased to 2 minutes

        private static int _consecutiveRateLimits = 0;
        private static float _lastRateLimitTime = 0f;

        /// <summary>
        /// Discord webhook file-size ceiling. Non-boosted servers cap at 8 MB; level-2
        /// boost raises it to 50 MB. We use 8 MB minus a small margin for the multipart
        /// envelope so the upload never fails on a vanilla Discord server.
        /// </summary>
        private const int MAX_ATTACHMENT_BYTES = 8 * 1024 * 1024 - 64 * 1024; // ~7.94 MB

        private Func<string> _botTokenResolver;
        private Func<string> _webhookUrlResolver;
        private Func<string> _clientLogsRootResolver;
        private Func<string> _webhookNameResolver;

        private readonly HashSet<string> _fulfilled = new HashSet<string>(StringComparer.Ordinal);

        private string _botUserId;
        private bool _botUserIdResolved;

        // The three emoji we poll for, pre-encoded for the REST URL.
        private static readonly string[] _emojiRaw = {
            DiscordWebhookConsumer.EMOJI_LOG,
            DiscordWebhookConsumer.EMOJI_ERRORS,
            DiscordWebhookConsumer.EMOJI_MODS,
            DiscordWebhookConsumer.EMOJI_DISCONNECT,
        };
        private static readonly string[] _emojiEncoded = _emojiRaw
            .Select(e => Uri.EscapeDataString(e)).ToArray();

        public static ReactionPoller Create(
            Func<string> botTokenResolver,
            Func<string> webhookUrlResolver,
            Func<string> clientLogsRootResolver,
            Func<string> webhookNameResolver = null)
        {
            var go = new GameObject("ClientLogRelay_ReactionPoller");
            DontDestroyOnLoad(go);
            var poller = go.AddComponent<ReactionPoller>();
            poller._botTokenResolver = botTokenResolver;
            poller._webhookUrlResolver = webhookUrlResolver;
            poller._clientLogsRootResolver = clientLogsRootResolver;
            poller._webhookNameResolver = webhookNameResolver;
            return poller;
        }

        private void Start()
        {
            StartCoroutine(PollLoop());
        }

        // ================================================================
        //  Main loop
        // ================================================================
        private IEnumerator PollLoop()
        {
            yield return new WaitForSeconds(30f);

            while (true)
            {
                // Check if we're in rate limit backoff
                if (_consecutiveRateLimits > 0)
                {
                    float timeSinceLimit = Time.realtimeSinceStartup - _lastRateLimitTime;
                    if (timeSinceLimit < RATE_LIMIT_BACKOFF_SECONDS)
                    {
                        float waitTime = RATE_LIMIT_BACKOFF_SECONDS - timeSinceLimit;
                        Debug.Log($"[ClientLogRelay] ReactionPoller: Rate limited, backing off for {waitTime:F0}s (attempt {_consecutiveRateLimits})");
                        yield return new WaitForSeconds(waitTime);
                    }
                }

                yield return new WaitForSeconds(POLL_INTERVAL_SECONDS);

                string botToken = _botTokenResolver?.Invoke();
                if (string.IsNullOrEmpty(botToken))
                    continue;

                if (!_botUserIdResolved)
                {
                    yield return ResolveBotUserId(botToken);
                    _botUserIdResolved = true;
                }

                var contexts = LogRequestRegistry.Snapshot();
                if (contexts == null || contexts.Length == 0)
                    continue;

                // Poll all messages, but with delays to avoid rate limits
                // With 2s between messages and 0.5s between emojis:
                // 10 messages = ~20 seconds to poll all (within 30s cycle)
                int polledCount = 0;

                foreach (var ctx in contexts)
                {
                    if (string.IsNullOrEmpty(ctx.ChannelId) || string.IsNullOrEmpty(ctx.MessageId))
                        continue;

                    // Poll each emoji independently with delays between requests
                    bool hitRateLimit = false;
                    for (int i = 0; i < _emojiRaw.Length; i++)
                    {
                        bool wasRateLimited = false;
                        yield return PollReaction(botToken, ctx, _emojiRaw[i], _emojiEncoded[i], (limited) => wasRateLimited = limited);

                        if (wasRateLimited)
                        {
                            hitRateLimit = true;
                            Debug.Log($"[ClientLogRelay] ReactionPoller: Hit rate limit after {polledCount} messages, pausing for this cycle");
                            break; // Stop polling this message
                        }

                        // Small delay between emoji polls to spread out requests
                        if (i < _emojiRaw.Length - 1)
                        {
                            yield return new WaitForSeconds(0.5f);
                        }
                    }

                    if (hitRateLimit)
                        break; // Stop polling entirely this cycle

                    polledCount++;

                    // Longer delay between messages to avoid bursts
                    // 2 seconds = can poll ~10 messages in a 30s cycle
                    yield return new WaitForSeconds(2f);
                }

                if (polledCount > 0)
                {
                    Debug.Log($"[ClientLogRelay] ReactionPoller: Polled {polledCount} message(s) this cycle");
                }
            }
        }

        // ================================================================
        //  Bot identity
        // ================================================================
        private IEnumerator ResolveBotUserId(string botToken)
        {
            using (var req = UnityWebRequest.Get("https://discord.com/api/v10/users/@me"))
            {
                req.SetRequestHeader("Authorization", $"Bot {botToken}");
                req.timeout = 15;
                yield return req.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                bool ok = req.result == UnityWebRequest.Result.Success;
#else
                bool ok = !req.isNetworkError && !req.isHttpError;
#endif
                if (ok && req.downloadHandler != null)
                {
                    try
                    {
                        var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(req.downloadHandler.text);
                        if (obj != null && obj.TryGetValue("id", out var idObj) && idObj != null)
                        {
                            _botUserId = idObj.ToString();
                            Debug.Log($"[ClientLogRelay] ReactionPoller resolved bot user id: {_botUserId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ClientLogRelay] ReactionPoller /users/@me parse failed: {ex.Message}");
                    }
                }
                else
                {
                    Debug.LogWarning($"[ClientLogRelay] ReactionPoller /users/@me failed ({req.responseCode}): {req.error}");
                }
            }
        }

        // ================================================================
        //  Poll a single emoji on a single message
        // ================================================================
        private IEnumerator PollReaction(string botToken, LogRequestContext ctx, string emojiRaw, string emojiEncoded, System.Action<bool> rateLimitCallback = null)
        {
            string url = $"https://discord.com/api/v10/channels/{ctx.ChannelId}/messages/{ctx.MessageId}/reactions/{emojiEncoded}";
            using (var req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("Authorization", $"Bot {botToken}");
                req.timeout = 15;
                yield return req.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                bool ok = req.result == UnityWebRequest.Result.Success;
#else
                bool ok = !req.isNetworkError && !req.isHttpError;
#endif
                if (!ok)
                {
                    if (req.responseCode == 429)
                    {
                        // Rate limited - notify caller and track it
                        _consecutiveRateLimits++;
                        _lastRateLimitTime = Time.realtimeSinceStartup;
                        rateLimitCallback?.Invoke(true);
                        Debug.LogWarning($"[ClientLogRelay] ReactionPoller rate limited (429) - backing off (consecutive: {_consecutiveRateLimits})");
                    }
                    else if (req.responseCode != 404)
                    {
                        Debug.LogWarning($"[ClientLogRelay] ReactionPoller GET reactions failed ({req.responseCode}): {req.error}");
                    }
                    yield break;
                }

                // Success - reset rate limit counter
                if (_consecutiveRateLimits > 0)
                {
                    Debug.Log($"[ClientLogRelay] ReactionPoller: Rate limit cleared after {_consecutiveRateLimits} consecutive 429s");
                    _consecutiveRateLimits = 0;
                }
                rateLimitCallback?.Invoke(false);

                JArray users;
                try { users = JArray.Parse(req.downloadHandler.text); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ClientLogRelay] ReactionPoller parse failed: {ex.Message}");
                    yield break;
                }

                foreach (var userToken in users)
                {
                    string userId = userToken["id"]?.ToString();
                    if (string.IsNullOrEmpty(userId)) continue;

                    // Skip the bot's own reaction.
                    if (!string.IsNullOrEmpty(_botUserId) && userId == _botUserId)
                        continue;

                    // Unique key per message + user + emoji so clicking two different
                    // reactions on the same message both get fulfilled.
                    string key = $"{ctx.MessageId}:{userId}:{emojiRaw}";
                    if (_fulfilled.Contains(key))
                        continue;

                    _fulfilled.Add(key);
                    string userName = userToken["username"]?.ToString() ?? userId;
                    Debug.Log($"[ClientLogRelay] ReactionPoller: '{userName}' reacted {emojiRaw} on {ctx.PlayerName} ({ctx.PlatformId})");

                    yield return Fulfill(ctx, emojiRaw, userName, userId);
                }
            }
        }

        // ================================================================
        //  Dispatch by emoji
        // ================================================================
        private IEnumerator Fulfill(LogRequestContext ctx, string emoji, string reqByName, string reqById)
        {
            string logsRoot = _clientLogsRootResolver?.Invoke();
            if (string.IsNullOrEmpty(logsRoot)) yield break;

            string folder = BuildPlayerFolder(logsRoot, ctx);

            if (emoji == DiscordWebhookConsumer.EMOJI_LOG)
                yield return FulfillFullLog(ctx, folder, reqByName);
            else if (emoji == DiscordWebhookConsumer.EMOJI_ERRORS)
                yield return FulfillErrorsWarnings(ctx, folder, reqByName);
            else if (emoji == DiscordWebhookConsumer.EMOJI_MODS)
                yield return FulfillMods(ctx, folder, reqByName);
            else if (emoji == DiscordWebhookConsumer.EMOJI_DISCONNECT)
                FulfillDisconnect(ctx, reqByName);
        }

        // ================================================================
        //  ??  Full log
        // ================================================================
        private IEnumerator FulfillFullLog(LogRequestContext ctx, string folder, string reqByName)
        {
            string logPath = Path.Combine(folder, "LogOutput.log");
            byte[] logBytes = SafeReadFile(logPath);
            if (logBytes == null || logBytes.Length == 0)
            {
                Debug.LogWarning($"[ClientLogRelay] ReactionPoller: no cached log for {ctx.PlatformId} at '{logPath}'");
                yield break;
            }

            string stamp = ctx.CapturedUtc.ToString("yyyyMMdd_HHmmss");
            string safeId = SafePlatformId(ctx);

            // Chunk if necessary.
            var chunks = ChunkBytes(logBytes, MAX_ATTACHMENT_BYTES);
            for (int i = 0; i < chunks.Count; i++)
            {
                string suffix = chunks.Count == 1 ? "" : $"_part{i + 1}of{chunks.Count}";
                string fileName = $"client_log_{safeId}_{stamp}{suffix}.log";
                string title = chunks.Count == 1
                    ? "\uD83D\uDCE9 Full Log \u2014 Requested"
                    : $"\uD83D\uDCE9 Full Log \u2014 Part {i + 1}/{chunks.Count}";

                var embed = new MinimalWebhookPoster.Embed()
                    .SetTitle(title)
                    .SetColor(3447003)
                    .AddField("\uD83D\uDC64 Player",   ctx.PlayerName,           true)
                    .AddField("\uD83D\uDD94 Steam ID", ctx.PlatformId ?? "?",    true)
                    .AddField("\uD83D\uDCC4 Size",     FormatBytes(chunks[i].Length), true)
                    .SetFooter($"Requested by {reqByName}");

                var files = new List<MinimalWebhookPoster.Attachment>
                {
                    new MinimalWebhookPoster.Attachment(fileName, chunks[i], "text/plain")
                };

                yield return PostAndWait(embed, files);
            }

            Debug.Log($"[ClientLogRelay] ReactionPoller: posted full log for {ctx.PlatformId} " +
                      $"({logBytes.Length} bytes, {chunks.Count} part(s)), requested by {reqByName}");
        }

        // ================================================================
        //  ?  Errors + warnings
        // ================================================================
        private IEnumerator FulfillErrorsWarnings(LogRequestContext ctx, string folder, string reqByName)
        {
            string path = Path.Combine(folder, "errors_warnings.txt");
            byte[] bytes = SafeReadFile(path);
            if (bytes == null || bytes.Length == 0)
            {
                Debug.LogWarning($"[ClientLogRelay] ReactionPoller: no errors_warnings.txt for {ctx.PlatformId}");
                yield break;
            }

            string stamp = ctx.CapturedUtc.ToString("yyyyMMdd_HHmmss");
            string safeId = SafePlatformId(ctx);

            var embed = new MinimalWebhookPoster.Embed()
                .SetTitle("\u26D4 Errors + Warnings \u2014 Requested")
                .SetColor(15548997)
                .AddField("\uD83D\uDC64 Player",   ctx.PlayerName,        true)
                .AddField("\uD83D\uDD94 Steam ID", ctx.PlatformId ?? "?", true)
                .AddField("\uD83D\uDCC4 Size",     FormatBytes(bytes.Length), true)
                .SetFooter($"Requested by {reqByName}");

            var files = new List<MinimalWebhookPoster.Attachment>
            {
                new MinimalWebhookPoster.Attachment($"errors_warnings_{safeId}_{stamp}.txt", bytes, "text/plain")
            };

            yield return PostAndWait(embed, files);
            Debug.Log($"[ClientLogRelay] ReactionPoller: posted errors/warnings for {ctx.PlatformId}, requested by {reqByName}");
        }

        // ================================================================
        //  ??  Mod list + diff
        // ================================================================
        private IEnumerator FulfillMods(LogRequestContext ctx, string folder, string reqByName)
        {
            string stamp = ctx.CapturedUtc.ToString("yyyyMMdd_HHmmss");
            string safeId = SafePlatformId(ctx);

            byte[] modListBytes = SafeReadFile(Path.Combine(folder, "modlist.txt"));
            byte[] modDiffBytes = SafeReadFile(Path.Combine(folder, "mod_diff.txt"));

            bool hasModList = modListBytes != null && modListBytes.Length > 0;
            bool hasDiff = modDiffBytes != null && modDiffBytes.Length > 0;
            if (!hasModList && !hasDiff)
            {
                Debug.LogWarning($"[ClientLogRelay] ReactionPoller: no mod artifacts for {ctx.PlatformId}");
                yield break;
            }

            var embed = new MinimalWebhookPoster.Embed()
                .SetTitle("\uD83E\uDDE9 Mod Lists \u2014 Requested")
                .SetColor(5763719)
                .AddField("\uD83D\uDC64 Player",   ctx.PlayerName,        true)
                .AddField("\uD83D\uDD94 Steam ID", ctx.PlatformId ?? "?", true)
                .SetFooter($"Requested by {reqByName}");

            var files = new List<MinimalWebhookPoster.Attachment>();
            if (hasModList)
                files.Add(new MinimalWebhookPoster.Attachment($"modlist_{safeId}_{stamp}.txt", modListBytes, "text/plain"));
            if (hasDiff)
                files.Add(new MinimalWebhookPoster.Attachment($"mod_diff_{safeId}_{stamp}.txt", modDiffBytes, "text/plain"));

            yield return PostAndWait(embed, files);
            Debug.Log($"[ClientLogRelay] ReactionPoller: posted mod artifacts for {ctx.PlatformId}, requested by {reqByName}");
        }

        // ================================================================
        //  ??  Disconnect capture
        // ================================================================
        private void FulfillDisconnect(LogRequestContext ctx, string reqByName)
        {
            // Just register the player — the actual log post happens when they disconnect
            // (handled by the Harmony patch in ClientLogRelayIntegration).
            DisconnectLogRegistry.Register(ctx.PlatformId, ctx.PlayerName, reqByName);
        }

        // ================================================================
        // ================================================================
        //  Helpers
        // ================================================================
        private IEnumerator PostAndWait(MinimalWebhookPoster.Embed embed, List<MinimalWebhookPoster.Attachment> files)
        {
            string webhookUrl = _webhookUrlResolver?.Invoke();
            if (string.IsNullOrEmpty(webhookUrl) || !MinimalWebhookPoster.IsValidWebhookUrl(webhookUrl))
                yield break;

            MinimalWebhookPoster.Post(webhookUrl, embed, files,
                _webhookNameResolver?.Invoke(), null);

            // Small delay between consecutive posts to respect Discord rate limits.
            yield return new WaitForSeconds(1.5f);
        }

        private static string BuildPlayerFolder(string logsRoot, LogRequestContext ctx)
        {
            string safePid = (ctx.PlatformId ?? "unknown").Replace(":", "_").Replace("/", "_").Replace("\\", "_");
            string safeName = ctx.PlayerName ?? "unknown";
            if (!string.IsNullOrEmpty(safeName))
            {
                var invalid = Path.GetInvalidFileNameChars();
                safeName = string.Concat(safeName.Split(invalid));
            }
            return Path.Combine(logsRoot, $"{safeName}_{safePid}");
        }

        private static string SafePlatformId(LogRequestContext ctx)
            => (ctx.PlatformId ?? "unknown").Replace(":", "_").Replace("/", "_").Replace("\\", "_");

        private static byte[] SafeReadFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay] ReactionPoller: read failed '{path}': {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Splits <paramref name="data"/> into chunks of at most <paramref name="maxBytes"/>.
        /// Returns a single-element list when the data already fits.
        /// </summary>
        private static List<byte[]> ChunkBytes(byte[] data, int maxBytes)
        {
            if (data == null || data.Length == 0)
                return new List<byte[]>(0);

            if (data.Length <= maxBytes)
                return new List<byte[]>(1) { data };

            var chunks = new List<byte[]>();
            int offset = 0;
            while (offset < data.Length)
            {
                int len = Math.Min(maxBytes, data.Length - offset);
                var chunk = new byte[len];
                Buffer.BlockCopy(data, offset, chunk, 0, len);
                chunks.Add(chunk);
                offset += len;
            }
            return chunks;
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
    }
}
