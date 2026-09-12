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
    /// Polls Discord for reactions on posted snapshot messages and uploads what was asked for: the full
    /// BepInEx log (EmojiLog), the errors and warnings report (EmojiErrors), or the client mod list and its
    /// diff against the server (EmojiMods). Anything over the webhook attachment limit is split into
    /// sequential parts.
    /// </summary>
    public sealed class ReactionPoller : MonoBehaviour
    {
        private const float PollIntervalSeconds = 30f;
        private const float RateLimitBackoffSeconds = 120f;

        private static int _consecutiveRateLimits = 0;
        private static float _lastRateLimitTime = 0f;

        private Func<string> _botTokenResolver;
        private Func<string> _webhookUrlResolver;
        private Func<string> _clientLogsRootResolver;
        private Func<string> _webhookNameResolver;

        private readonly HashSet<string> _fulfilled = new HashSet<string>(StringComparer.Ordinal);

        private string _botUserId;
        private bool _botUserIdResolved;

        // The three emoji we poll for, pre-encoded for the REST URL.
        private static readonly string[] _emojiRaw = {
            DiscordWebhookConsumer.EmojiLog,
            DiscordWebhookConsumer.EmojiErrors,
            DiscordWebhookConsumer.EmojiMods,
            DiscordWebhookConsumer.EmojiDisconnect,
        };
        private static readonly string[] _emojiEncoded = _emojiRaw
            .Select(e => Uri.EscapeDataString(e)).ToArray();

        public static ReactionPoller Create(
            Func<string> botTokenResolver,
            Func<string> webhookUrlResolver,
            Func<string> clientLogsRootResolver,
            Func<string> webhookNameResolver = null)
        {
            var host = new GameObject("ClientLogRelay_ReactionPoller");
            DontDestroyOnLoad(host);
            var poller = host.AddComponent<ReactionPoller>();
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
                    if (timeSinceLimit < RateLimitBackoffSeconds)
                    {
                        float waitTime = RateLimitBackoffSeconds - timeSinceLimit;
                        Debug.Log($"[ClientLogRelay] ReactionPoller: Rate limited, backing off for {waitTime:F0}s (attempt {_consecutiveRateLimits})");
                        yield return new WaitForSeconds(waitTime);
                    }
                }

                yield return new WaitForSeconds(PollIntervalSeconds);

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

                foreach (var context in contexts)
                {
                    if (string.IsNullOrEmpty(context.ChannelId) || string.IsNullOrEmpty(context.MessageId))
                        continue;

                    // Poll each emoji independently with delays between requests
                    bool hitRateLimit = false;
                    for (int i = 0; i < _emojiRaw.Length; i++)
                    {
                        bool wasRateLimited = false;
                        yield return PollReaction(botToken, context, _emojiRaw[i], _emojiEncoded[i], (limited) => wasRateLimited = limited);

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

                bool succeeded = req.result == UnityWebRequest.Result.Success;
                if (succeeded && req.downloadHandler != null)
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
        private IEnumerator PollReaction(string botToken, LogRequestContext context, string emojiRaw, string emojiEncoded, System.Action<bool> rateLimitCallback = null)
        {
            string url = $"https://discord.com/api/v10/channels/{context.ChannelId}/messages/{context.MessageId}/reactions/{emojiEncoded}";
            using (var req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("Authorization", $"Bot {botToken}");
                req.timeout = 15;
                yield return req.SendWebRequest();

                bool succeeded = req.result == UnityWebRequest.Result.Success;
                if (!succeeded)
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
                    string key = $"{context.MessageId}:{userId}:{emojiRaw}";
                    if (_fulfilled.Contains(key))
                        continue;

                    _fulfilled.Add(key);
                    string userName = userToken["username"]?.ToString() ?? userId;
                    Debug.Log($"[ClientLogRelay] ReactionPoller: '{userName}' reacted {emojiRaw} on {context.PlayerName} ({context.PlatformId})");

                    yield return Fulfill(context, emojiRaw, userName, userId);
                }
            }
        }

        // ================================================================
        //  Dispatch by emoji
        // ================================================================
        private IEnumerator Fulfill(LogRequestContext context, string emoji, string requestedByName, string requestedById)
        {
            string logsRoot = _clientLogsRootResolver?.Invoke();
            if (string.IsNullOrEmpty(logsRoot)) yield break;

            string folder = BuildPlayerFolder(logsRoot, context);

            if (emoji == DiscordWebhookConsumer.EmojiLog)
                yield return FulfillFullLog(context, folder, requestedByName);
            else if (emoji == DiscordWebhookConsumer.EmojiErrors)
                yield return FulfillErrorsWarnings(context, folder, requestedByName);
            else if (emoji == DiscordWebhookConsumer.EmojiMods)
                yield return FulfillMods(context, folder, requestedByName);
            else if (emoji == DiscordWebhookConsumer.EmojiDisconnect)
                FulfillDisconnect(context, requestedByName);
        }

        // ================================================================
        //  ??  Full log
        // ================================================================
        private IEnumerator FulfillFullLog(LogRequestContext context, string folder, string requestedByName)
        {
            string logPath = Path.Combine(folder, "LogOutput.log");
            byte[] logBytes = SafeReadFile(logPath);
            if (logBytes == null || logBytes.Length == 0)
            {
                Debug.LogWarning($"[ClientLogRelay] ReactionPoller: no cached log for {context.PlatformId} at '{logPath}'");
                yield break;
            }

            string stamp = context.CapturedUtc.ToString("yyyyMMdd_HHmmss");
            string safeId = SafePlatformId(context);

            // Chunk if necessary.
            var chunks = ChunkBytes(logBytes, DiscordPayload.MaxAttachmentBytes);
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
                    .AddField("\uD83D\uDC64 Player",   context.PlayerName,           true)
                    .AddField("\uD83D\uDD94 Steam ID", context.PlatformId ?? "?",    true)
                    .AddField("\uD83D\uDCC4 Size",     DiscordPayload.FormatBytes(chunks[i].Length), true)
                    .SetFooter($"Requested by {requestedByName}");

                var files = new List<MinimalWebhookPoster.Attachment>
                {
                    new MinimalWebhookPoster.Attachment(fileName, chunks[i], "text/plain")
                };

                yield return PostAndWait(embed, files);
            }

            Debug.Log($"[ClientLogRelay] ReactionPoller: posted full log for {context.PlatformId} " +
                      $"({logBytes.Length} bytes, {chunks.Count} part(s)), requested by {requestedByName}");
        }

        private IEnumerator FulfillErrorsWarnings(LogRequestContext context, string folder, string requestedByName)
        {
            string path = Path.Combine(folder, "errors_warnings.txt");
            byte[] bytes = SafeReadFile(path);
            if (bytes == null || bytes.Length == 0)
            {
                Debug.LogWarning($"[ClientLogRelay] ReactionPoller: no errors_warnings.txt for {context.PlatformId}");
                yield break;
            }

            string stamp = context.CapturedUtc.ToString("yyyyMMdd_HHmmss");
            string safeId = SafePlatformId(context);

            var embed = new MinimalWebhookPoster.Embed()
                .SetTitle("\u26D4 Errors + Warnings \u2014 Requested")
                .SetColor(15548997)
                .AddField("\uD83D\uDC64 Player",   context.PlayerName,        true)
                .AddField("\uD83D\uDD94 Steam ID", context.PlatformId ?? "?", true)
                .AddField("\uD83D\uDCC4 Size",     DiscordPayload.FormatBytes(bytes.Length), true)
                .SetFooter($"Requested by {requestedByName}");

            var files = new List<MinimalWebhookPoster.Attachment>
            {
                new MinimalWebhookPoster.Attachment($"errors_warnings_{safeId}_{stamp}.txt", bytes, "text/plain")
            };

            yield return PostAndWait(embed, files);
            Debug.Log($"[ClientLogRelay] ReactionPoller: posted errors/warnings for {context.PlatformId}, requested by {requestedByName}");
        }

        // ================================================================
        //  ??  Mod list + diff
        // ================================================================
        private IEnumerator FulfillMods(LogRequestContext context, string folder, string requestedByName)
        {
            string stamp = context.CapturedUtc.ToString("yyyyMMdd_HHmmss");
            string safeId = SafePlatformId(context);

            byte[] modListBytes = SafeReadFile(Path.Combine(folder, "modlist.txt"));
            byte[] modDiffBytes = SafeReadFile(Path.Combine(folder, "mod_diff.txt"));

            bool hasModList = modListBytes != null && modListBytes.Length > 0;
            bool hasDiff = modDiffBytes != null && modDiffBytes.Length > 0;
            if (!hasModList && !hasDiff)
            {
                Debug.LogWarning($"[ClientLogRelay] ReactionPoller: no mod artifacts for {context.PlatformId}");
                yield break;
            }

            var embed = new MinimalWebhookPoster.Embed()
                .SetTitle("\uD83E\uDDE9 Mod Lists \u2014 Requested")
                .SetColor(5763719)
                .AddField("\uD83D\uDC64 Player",   context.PlayerName,        true)
                .AddField("\uD83D\uDD94 Steam ID", context.PlatformId ?? "?", true)
                .SetFooter($"Requested by {requestedByName}");

            var files = new List<MinimalWebhookPoster.Attachment>();
            if (hasModList)
                files.Add(new MinimalWebhookPoster.Attachment($"modlist_{safeId}_{stamp}.txt", modListBytes, "text/plain"));
            if (hasDiff)
                files.Add(new MinimalWebhookPoster.Attachment($"mod_diff_{safeId}_{stamp}.txt", modDiffBytes, "text/plain"));

            yield return PostAndWait(embed, files);
            Debug.Log($"[ClientLogRelay] ReactionPoller: posted mod artifacts for {context.PlatformId}, requested by {requestedByName}");
        }

        // ================================================================
        //  ??  Disconnect capture
        // ================================================================
        private void FulfillDisconnect(LogRequestContext context, string requestedByName)
        {
            // Just register the player - the actual log post happens when they disconnect
            // (handled by the Harmony patch in ClientLogRelayIntegration).
            DisconnectLogRegistry.Register(context.PlatformId, context.PlayerName, requestedByName);
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

        private static string BuildPlayerFolder(string logsRoot, LogRequestContext context)
        {
            string safePid = (context.PlatformId ?? "unknown").Replace(":", "_").Replace("/", "_").Replace("\\", "_");
            string safeName = context.PlayerName ?? "unknown";
            if (!string.IsNullOrEmpty(safeName))
            {
                var invalid = Path.GetInvalidFileNameChars();
                safeName = string.Concat(safeName.Split(invalid));
            }
            return Path.Combine(logsRoot, $"{safeName}_{safePid}");
        }

        private static string SafePlatformId(LogRequestContext context)
            => (context.PlatformId ?? "unknown").Replace(":", "_").Replace("/", "_").Replace("\\", "_");

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
    }
}
