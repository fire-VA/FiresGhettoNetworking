using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using VerdantsAscent.Modules.ClientLogRelay.Webhook;

namespace VerdantsAscent.Modules.ClientLogRelay.Consumers
{
    /// <summary>
    /// Stock consumer that posts a Discord embed snapshot each time a client logs in.
    ///
    /// By default only the summary embed is posted - no file attachments. Three reaction
    /// buttons are pre-added by the bot so admins can request specific artifacts on demand:
    /// <list type="bullet">
    /// <item>?? <c>:envelope_with_arrow:</c> - full BepInEx log</item>
    /// <item>? <c>:no_entry:</c> - errors + warnings report</item>
    /// <item>?? <c>:jigsaw:</c> - client mod list + client-vs-server diff</item>
    /// </list>
    /// </summary>
    public sealed class DiscordWebhookConsumer : IClientLogConsumer
    {
        // Reaction emoji constants - single source of truth for consumer + poller.
        public const string EmojiLog    = "\uD83D\uDCE9"; // ?? :envelope_with_arrow:
        public const string EmojiErrors      = "\u26D4";        // :no_entry:
        public const string EmojiMods        = "\uD83E\uDDE9"; // ?? :jigsaw:
        public const string EmojiDisconnect  = "\u267B\uFE0F"; // ?? :recycle:
        public const string EmojiRestart     = "\uD83D\uDD04"; // ?? :arrows_counterclockwise:

        public string ConsumerId { get; }

        public Func<bool>   EnabledGate;
        public Func<string> WebhookUrl;
        public Func<string> WebhookName;
        public Func<string> BrandLabel;
        public Func<string> AvatarUrl;
        public Func<bool>   AttachFullLog;
        public Func<bool>   AttachModList;
        public Func<bool>   AttachErrorsWarnings;
        public Func<bool>   OnlyIfErrorsOrWarnings;
        public Func<string> ServerName;

        /// <summary>
        /// When true, file attachments are withheld and three reaction buttons are
        /// pre-added so admins can request artifacts on demand.
        /// </summary>
        public Func<bool>   EnableLogRequestReaction;

        /// <summary>
        /// (Optional) Discord bot token. Required for pre-adding reactions and for the
        /// <see cref="Interactions.ReactionPoller"/> to detect admin clicks.
        /// </summary>
        public Func<string> BotToken;

        public DiscordWebhookConsumer(string consumerId)
        {
            ConsumerId = consumerId ?? throw new ArgumentNullException(nameof(consumerId));
        }

        public void OnClientArtifacts(ClientLogArtifacts artifacts)
        {
            if (EnabledGate != null && !EnabledGate()) return;

            string url = WebhookUrl?.Invoke();
            if (!MinimalWebhookPoster.IsValidWebhookUrl(url))
                return;

            if (OnlyIfErrorsOrWarnings != null && OnlyIfErrorsOrWarnings()
                && artifacts.ErrorCount == 0 && artifacts.WarningCount == 0)
                return;

            int color = artifacts.ErrorCount > 0   ? 15548997   // red
                      : artifacts.WarningCount > 0 ? 15105570   // orange
                      : 5763719;                                // green

            string stamp = artifacts.CapturedUtc.ToString("yyyyMMdd_HHmmss");
            string safeId = artifacts.SafePlatformId;
            string brand = BrandLabel?.Invoke();
            bool hasServerMods = artifacts.ServerMods != null && artifacts.ServerMods.Count > 0;
            bool hasDiff = artifacts.ModDiff != null;
            bool enableReaction = EnableLogRequestReaction != null && EnableLogRequestReaction();

            // --- Build embed ---
            string title = hasServerMods
                ? (string.IsNullOrEmpty(brand) ? "\uD83E\uDEB5 Client Login Snapshot" : $"\uD83E\uDEB5 {brand} \u2014 Client Login Snapshot")
                : "\uD83E\uDEB5 Client Login \u2014 Log Snapshot";

            var embed = new MinimalWebhookPoster.Embed()
                .SetTitle(title)
                .SetColor(color)
                .AddField("\uD83D\uDC64 Player",      artifacts.PlayerName,                         true)
                .AddField("\uD83D\uDD94 Steam ID",    artifacts.PlatformId ?? "Unknown",            true)
                .AddField("\uD83E\uDDE9 Client Mods", (artifacts.ModList?.Count ?? 0).ToString(),   true);

            if (hasServerMods)
                embed.AddField("\uD83E\uDDE9 Server Mods", artifacts.ServerMods.Count.ToString(), true);

            embed.AddField("\uD83D\uDD34 Errors",   artifacts.ErrorCount.ToString(),              true)
                .AddField("\u26A0\uFE0F Warnings", artifacts.WarningCount.ToString(),            true);

            var modsWithIssues = FindModsWithIssues(artifacts);
            if (modsWithIssues.Count > 0)
            {
                embed.AddField("\uD83D\uDCAC Mods w/ Issues", modsWithIssues.Count.ToString(), true);
            }

            embed.AddField("\uD83D\uDCC4 Log Size", DiscordPayload.FormatBytes(artifacts.LogBytes?.Length ?? 0), true);

            if (modsWithIssues.Count > 0)
            {
                const int MaxShown = 5;
                var sb = new StringBuilder();
                for (int i = 0; i < modsWithIssues.Count && i < MaxShown; i++)
                {
                    var hit = modsWithIssues[i];
                    string shortName = hit.Guid;
                    int lastDot = hit.Guid.LastIndexOf('.');
                    if (lastDot >= 0 && lastDot < hit.Guid.Length - 1)
                        shortName = hit.Guid.Substring(lastDot + 1);
                    sb.AppendLine($"\u2022 {shortName} ({hit.Hits} mention{(hit.Hits == 1 ? "" : "s")})");
                }
                if (modsWithIssues.Count > MaxShown)
                    sb.AppendLine($"*({modsWithIssues.Count - MaxShown} more)*");
                embed.AddField("\u26A0\uFE0F Problem Mods", sb.ToString().TrimEnd(), false);
            }

            if (hasDiff)
            {
                var diff = artifacts.ModDiff;
                string diffSummary = $"client-only: {diff.ClientOnly.Count} server-only: {diff.ServerOnly.Count} mismatched: {diff.VersionMismatches.Count}";
                embed.AddField("\uD83D\uDD04 Mod Diff (C vs S)", diffSummary, false);

                if (diff.VersionMismatches.Count > 0)
                {
                    int clientBehind = 0;
                    int serverBehind = 0;
                    foreach (var m in diff.VersionMismatches)
                    {
                        int cmp = CompareVersions(m.ClientVersion, m.ServerVersion);
                        if (cmp < 0) clientBehind++;
                        else if (cmp > 0) serverBehind++;
                        else { clientBehind++; serverBehind++; } // unparseable - flag both
                    }
                    string updateSummary = $"\u2B06\uFE0F Client: {clientBehind} \u2502 \u2B06\uFE0F Server: {serverBehind}";
                    embed.AddField("\uD83D\uDD27 Needs Update", updateSummary, false);
                }
            }

            // --- Footer ---
            string serverName = ServerName?.Invoke();
            string footerLabel = string.IsNullOrEmpty(brand) ? "ClientLogRelay" : brand;
            string footerText;
            if (enableReaction)
            {
                string hint = $"{EmojiLog} Log \u2502 {EmojiErrors} Errors \u2502 {EmojiMods} Mods \u2502 {EmojiDisconnect} Disconnect";
                footerText = string.IsNullOrEmpty(serverName)
                    ? hint
                    : $"{footerLabel} \u2014 {serverName}\n{hint}";
            }
            else if (!string.IsNullOrEmpty(serverName))
            {
                footerText = $"{footerLabel} \u2014 {serverName}";
            }
            else
            {
                footerText = footerLabel;
            }
            if (!string.IsNullOrEmpty(footerText)) embed.SetFooter(footerText);

            // --- Attachments (only when reaction mode is OFF) ---
            var files = new List<MinimalWebhookPoster.Attachment>();

            if (!enableReaction)
            {
                if ((AttachFullLog == null || AttachFullLog())
                    && artifacts.LogBytes != null && artifacts.LogBytes.Length > 0)
                {
                    files.Add(new MinimalWebhookPoster.Attachment(
                        $"client_log_{safeId}_{stamp}.log",
                        artifacts.LogBytes, "text/plain"));
                }

                if ((AttachModList == null || AttachModList())
                    && artifacts.ModList != null && artifacts.ModList.Count > 0)
                {
                    files.Add(new MinimalWebhookPoster.Attachment(
                        $"modlist_{safeId}_{stamp}.txt", BuildModListText(artifacts)));
                }

                if ((AttachErrorsWarnings == null || AttachErrorsWarnings())
                    && !string.IsNullOrEmpty(artifacts.ErrorsWarningsReport))
                {
                    files.Add(new MinimalWebhookPoster.Attachment(
                        $"errors_warnings_{safeId}_{stamp}.txt",
                        artifacts.ErrorsWarningsReport));
                }

                if (artifacts.ModDiff != null && !string.IsNullOrEmpty(artifacts.ModDiff.Report))
                {
                    files.Add(new MinimalWebhookPoster.Attachment(
                        $"mod_diff_{safeId}_{stamp}.txt",
                        artifacts.ModDiff.Report));
                }
            }

            // --- Post ---
            string botToken = BotToken?.Invoke();

            MinimalWebhookPoster.Post(url, embed, files,
                WebhookName?.Invoke(), AvatarUrl?.Invoke(),
                onPosted: (messageId, channelId) =>
                {
                    if (enableReaction && !string.IsNullOrEmpty(messageId))
                    {
                        Interactions.LogRequestRegistry.Register(
                            new Interactions.LogRequestContext(
                                messageId:   messageId,
                                platformId:  artifacts.PlatformId,
                                playerName:  artifacts.PlayerName,
                                capturedUtc: artifacts.CapturedUtc,
                                consumerId:  ConsumerId,
                                channelId:   channelId));

                        // Pre-add all three reaction buttons sequentially so
                        // Discord doesn't rate-limit and drop any.
                        if (!string.IsNullOrEmpty(botToken) && !string.IsNullOrEmpty(channelId))
                        {
                            MinimalWebhookPoster.AddReactions(botToken, channelId, messageId,
                                EmojiLog, EmojiErrors, EmojiMods, EmojiDisconnect);

                            // Clean up old client log messages (keep only the 10 most recent)
                            MinimalWebhookPoster.StartCleanupOldClientLogs(botToken, channelId, keepCount: 10);
                        }
                    }
                });

            Debug.Log($"[ClientLogRelay:{ConsumerId}] Posted login snapshot for {artifacts.PlatformId} " +
                      $"({files.Count} attachment(s), err={artifacts.ErrorCount} warn={artifacts.WarningCount})");
        }

        internal static string BuildModListText(ClientLogArtifacts artifacts)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Client Mod List \u2014 {artifacts.PlayerName} ({artifacts.PlatformId})");
            sb.AppendLine($"# Captured: {artifacts.CapturedUtc:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"# Count:    {artifacts.ModList.Count}");
            sb.AppendLine();
            foreach (var kv in artifacts.ModList.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append(kv.Key).Append('=').AppendLine(kv.Value);
            return sb.ToString();
        }

        /// <summary>
        /// Finds mods named in error and warning lines. Bracketed tags and colon-prefixed names are pulled
        /// out of the report first, then fuzzy-matched against each GUID and its segments (so
        /// com.Fire.verdantsascent_pieces matches the tag VerdantsAscentPieces); a word-boundary search of
        /// the raw text catches the rest. Returns (guid, hitCount) pairs sorted by GUID.
        /// </summary>
        private static List<ModIssueHit> FindModsWithIssues(ClientLogArtifacts artifacts)
        {
            var result = new List<ModIssueHit>();
            if (string.IsNullOrEmpty(artifacts.ErrorsWarningsReport) || artifacts.ModList == null)
                return result;

            string report = artifacts.ErrorsWarningsReport;

            // --- Pass 1: extract all tags from the report ---
            var tags = ExtractLogTags(report);

            // --- Pass 2: for each mod, try to match ---
            foreach (var kv in artifacts.ModList.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                string guid = kv.Key;
                if (string.IsNullOrEmpty(guid)) continue;

                int hits = 0;

                // Strategy A: word-boundary match of the full GUID (skip if < 4 chars)
                if (guid.Length >= 4)
                    hits += CountWordBoundaryHits(report, guid);

                // Strategy B: word-boundary match of each segment of the GUID that is >= 5 chars
                var segments = guid.Split('.');
                foreach (var seg in segments)
                {
                    if (seg.Length >= 5)
                        hits += CountWordBoundaryHits(report, seg);
                }

                // Strategy C: fuzzy-match extracted tags against normalised GUID/segments
                string normGuid = NormaliseForTagMatch(guid);
                foreach (var tag in tags)
                {
                    string normTag = NormaliseForTagMatch(tag);
                    if (normTag.Length < 4) continue;

                    // Check if any segment (normalised) matches the tag, or if the tag
                    // is contained in the normalised GUID or vice-versa.
                    if (normGuid.IndexOf(normTag, StringComparison.OrdinalIgnoreCase) >= 0
                        || normTag.IndexOf(normGuid, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        hits += tags.Count(t => t == tag); // count how many times this tag appears
                        continue;
                    }

                    foreach (var seg in segments)
                    {
                        string normSeg = NormaliseForTagMatch(seg);
                        if (normSeg.Length < 6) continue;
                        // Require the shorter string to be at least 60% of the longer
                        // to avoid "fire" matching "firesrpgmaker".
                        int shorter = Math.Min(normSeg.Length, normTag.Length);
                        int longer  = Math.Max(normSeg.Length, normTag.Length);
                        if (shorter * 100 / longer < 60) continue;

                        if (normSeg.IndexOf(normTag, StringComparison.OrdinalIgnoreCase) >= 0
                            || normTag.IndexOf(normSeg, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hits += tags.Count(t => t == tag);
                            break;
                        }
                    }
                }

                if (hits > 0)
                    result.Add(new ModIssueHit { Guid = guid, Hits = hits });
            }
            return result;
        }

        private struct ModIssueHit
        {
            public string Guid;
            public int Hits;
        }

        /// <summary>
        /// Extracts log-source tags from BepInEx error/warning lines. Captures:
        /// <list type="bullet">
        /// <item><c>[TagName]</c> - bracketed tags after the log-level prefix</item>
        /// <item><c>TagName:</c> - word followed by colon at the start of the message body</item>
        /// </list>
        /// Returns all found tags (including duplicates for counting).
        /// </summary>
        private static List<string> ExtractLogTags(string report)
        {
            var tags = new List<string>();
            var lines = report.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                // Skip header lines
                if (line.StartsWith("#")) continue;

                // Look for [Error ...] or [Warning ...] log-level prefix, then find tags after it.
                // Pattern 1: [BracketedTag] after the log-level block
                //   e.g. "[Warning:Server Devcommands]" or "[Warning: Unity Log] [QuestManager] ..."
                int searchFrom = 0;

                // Find all [Bracketed] sections. The first one is the log-level prefix;
                // subsequent ones are source tags.
                int bracketCount = 0;
                int idx = 0;
                while (idx < line.Length)
                {
                    int open = line.IndexOf('[', idx);
                    if (open < 0) break;
                    int close = line.IndexOf(']', open + 1);
                    if (close < 0) break;

                    string content = line.Substring(open + 1, close - open - 1).Trim();
                    bracketCount++;

                    if (bracketCount == 1)
                    {
                        // First bracket is log-level - but BepInEx sometimes embeds the
                        // source in the level bracket: "[Warning:Server Devcommands]"
                        int colon = content.IndexOf(':');
                        if (colon >= 0 && colon < content.Length - 1)
                        {
                            string afterColon = content.Substring(colon + 1).Trim();
                            if (afterColon.Length >= 3 && afterColon != "Unity Log")
                                tags.Add(afterColon);
                        }
                        searchFrom = close + 1;
                    }
                    else
                    {
                        // Subsequent brackets are source tags
                        if (content.Length >= 3)
                            tags.Add(content);
                    }

                    idx = close + 1;
                }

                // Pattern 2: "TagName:" at start of message body (after the bracket prefix)
                //   e.g. "[Error  : Unity Log] VerdantsAscentPieces: Sandstone_post01..."
                //   e.g. "[Warning: Unity Log] VABackpacks: Material on SmallBag..."
                if (searchFrom < line.Length)
                {
                    string body = line.Substring(searchFrom).TrimStart();
                    // Match: one or more word chars, then a colon, then a space
                    int colonIdx = body.IndexOf(": ", StringComparison.Ordinal);
                    if (colonIdx > 0 && colonIdx <= 60)
                    {
                        string candidate = body.Substring(0, colonIdx).Trim();
                        // Must be a single "word" (no spaces allowed, except none here)
                        if (candidate.Length >= 3 && candidate.IndexOf(' ') < 0)
                            tags.Add(candidate);
                    }
                }
            }
            return tags;
        }

        /// <summary>
        /// Normalises a string for fuzzy tag matching: strips underscores, hyphens, dots,
        /// and lowercases. This way <c>verdantsascent_pieces</c> can match
        /// <c>VerdantsAscentPieces</c>.
        /// </summary>
        private static string NormaliseForTagMatch(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '_' || c == '-' || c == '.') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Counts occurrences of <paramref name="needle"/> in <paramref name="haystack"/>
        /// where the match is surrounded by non-GUID characters (or string boundaries).
        /// </summary>
        private static int CountWordBoundaryHits(string haystack, string needle)
        {
            int count = 0;
            int startIdx = 0;
            while (startIdx <= haystack.Length - needle.Length)
            {
                int idx = haystack.IndexOf(needle, startIdx, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;

                bool leftOk  = idx == 0 || !IsGuidChar(haystack[idx - 1]);
                bool rightOk = (idx + needle.Length >= haystack.Length) || !IsGuidChar(haystack[idx + needle.Length]);

                if (leftOk && rightOk)
                    count++;

                startIdx = idx + needle.Length;
            }
            return count;
        }

        private static bool IsGuidChar(char c)
            => char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-';

        /// <summary>
        /// Compares two dot-separated version strings (e.g. "1.2.3" vs "1.3.0").
        /// Returns &lt; 0 if <paramref name="a"/> is older, &gt; 0 if newer, 0 if equal
        /// or if either string is unparseable.
        /// </summary>
        private static int CompareVersions(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return 0;
            var leftParts = left.Split('.');
            var rightParts = right.Split('.');
            int segments = Math.Max(leftParts.Length, rightParts.Length);
            for (int i = 0; i < segments; i++)
            {
                int leftValue = 0, rightValue = 0;
                if (i < leftParts.Length) int.TryParse(leftParts[i], out leftValue);
                if (i < rightParts.Length) int.TryParse(rightParts[i], out rightValue);
                if (leftValue != rightValue) return leftValue.CompareTo(rightValue);
            }
            return 0;
        }
    }
}
