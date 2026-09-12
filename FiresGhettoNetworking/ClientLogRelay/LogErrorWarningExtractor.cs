using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VerdantsAscent.Modules.ClientLogRelay
{
    /// <summary>
    /// Distills a BepInEx LogOutput.log buffer into an errors-and-warnings report: a per-source breakdown
    /// grouped by the [BracketedTag] or PrefixName: that produced each line, followed by the full
    /// chronological list. No dependencies beyond the framework.
    /// </summary>
    public static class LogErrorWarningExtractor
    {
        private const string SectionRule = "# ----------------------------------------------------------";

        public static readonly List<string> BenignPatterns = new List<string>
        {
            "Failed to find expected binary shader data",
            "The texture is not suitable to be used as a single mip level texture",
            "The AssetBundle",
            "audio clip could not be loaded",
        };

        public struct Result
        {
            public string Report;
            public int ErrorCount;
            public int WarningCount;
            public int BenignSkipped;
            public int DuplicatesCollapsed;
        }

        public static Result Extract(byte[] logBytes, string playerName, string platformId)
        {
            var result = new Result();

            if (logBytes == null || logBytes.Length == 0)
            {
                result.Report = BuildHeader(playerName, platformId, 0, 0, 0, 0)
                    + "# (no client log available)\n";
                return result;
            }

            // --- Phase 1: collect unique lines, dedup, count ---
            var messageCounts  = new Dictionary<string, int>(StringComparer.Ordinal);
            var orderedKeys    = new List<string>();
            var keyToEmitIdx   = new Dictionary<string, int>(StringComparer.Ordinal);
            var emittedLines   = new List<string>();
            // Track whether each emitted line is an error (true) or warning (false).
            var emittedIsError = new List<bool>();

            try
            {
                string text = Encoding.UTF8.GetString(logBytes);
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.None);

                bool inStackTrace = false;
                string currentKey = null;
                bool currentIsError = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrEmpty(line)) { inStackTrace = false; currentKey = null; continue; }

                    bool isError = line.IndexOf("[Error", StringComparison.OrdinalIgnoreCase) >= 0
                                   || line.IndexOf("Exception:", StringComparison.Ordinal) >= 0
                                   || line.IndexOf("NullReferenceException", StringComparison.Ordinal) >= 0
                                   || line.IndexOf("ArgumentException", StringComparison.Ordinal) >= 0
                                   || line.IndexOf("InvalidOperationException", StringComparison.Ordinal) >= 0;
                    bool isWarning = !isError && line.IndexOf("[Warning", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (!isError && !isWarning)
                    {
                        if (inStackTrace && currentKey != null && IsStackTraceContinuation(line))
                        {
                            emittedLines.Add(line);
                            emittedIsError.Add(currentIsError);
                        }
                        else
                        {
                            inStackTrace = false;
                            currentKey = null;
                        }
                        continue;
                    }

                    if (IsBenign(line))
                    {
                        result.BenignSkipped++;
                        inStackTrace = false;
                        currentKey = null;
                        continue;
                    }

                    string key = NormalizeForDedup(line);

                    if (messageCounts.TryGetValue(key, out int prev))
                    {
                        messageCounts[key] = prev + 1;
                        result.DuplicatesCollapsed++;
                        inStackTrace = false;
                        currentKey = null;
                        continue;
                    }

                    messageCounts[key] = 1;
                    orderedKeys.Add(key);
                    keyToEmitIdx[key] = emittedLines.Count;
                    emittedLines.Add(line);
                    emittedIsError.Add(isError);

                    if (isError) { result.ErrorCount++; inStackTrace = true; currentIsError = true; }
                    else         { result.WarningCount++; inStackTrace = false; currentIsError = false; }
                    currentKey = key;
                }

                // Stamp repeat counts into the emitted lines.
                foreach (var k in orderedKeys)
                {
                    int count = messageCounts[k];
                    if (count > 1)
                    {
                        int idx = keyToEmitIdx[k];
                        emittedLines[idx] = emittedLines[idx] + $"   [repeated {count} times]";
                    }
                }
            }
            catch (Exception ex)
            {
                emittedLines.Add($"# Parser failure: {ex.Message}");
                emittedIsError.Add(true);
            }

            // --- Phase 2: group lines by source tag ---
            var grouped = GroupBySource(emittedLines, emittedIsError);

            // --- Phase 3: build report ---
            var sb = new StringBuilder();
            sb.Append(BuildHeader(playerName, platformId,
                result.ErrorCount, result.WarningCount,
                result.BenignSkipped, result.DuplicatesCollapsed));

            // Section 1: Per-source breakdown
            if (grouped.Count > 0)
            {
                sb.AppendLine(SectionRule);
                sb.AppendLine("#  PER-SOURCE BREAKDOWN");
                sb.AppendLine(SectionRule);
                sb.AppendLine();

                foreach (var group in grouped.OrderByDescending(g => g.Errors).ThenByDescending(g => g.Warnings))
                {
                    string label = group.Source;
                    sb.AppendLine($"## {label}  ({group.Errors} error{(group.Errors == 1 ? "" : "s")}, {group.Warnings} warning{(group.Warnings == 1 ? "" : "s")})");

                    if (group.ErrorLines.Count > 0)
                    {
                        sb.AppendLine("  [ERRORS]");
                        foreach (var line in group.ErrorLines)
                            sb.Append("    ").AppendLine(line);
                    }

                    if (group.WarningLines.Count > 0)
                    {
                        sb.AppendLine("  [WARNINGS]");
                        foreach (var line in group.WarningLines)
                            sb.Append("    ").AppendLine(line);
                    }

                    sb.AppendLine();
                }
            }

            // Section 2: Full chronological list
            sb.AppendLine(SectionRule);
            sb.AppendLine("#  FULL CHRONOLOGICAL LIST");
            sb.AppendLine(SectionRule);
            sb.AppendLine();

            foreach (var emitted in emittedLines)
                sb.AppendLine(emitted);

            result.Report = sb.ToString();
            return result;
        }

        // ================================================================
        //  Source grouping
        // ================================================================

        private sealed class SourceGroup
        {
            public string Source;
            public int Errors;
            public int Warnings;
            public List<string> ErrorLines   = new List<string>();
            public List<string> WarningLines = new List<string>();
        }

        /// <summary>
        /// Groups emitted lines by their log source tag. Extracts:
        /// <list type="bullet">
        /// <item><c>[BracketedTag]</c> after the log-level prefix</item>
        /// <item><c>PrefixName:</c> at the start of the message body</item>
        /// <item>BepInEx embedded source in the level bracket: <c>[Warning:SourceName]</c></item>
        /// </list>
        /// Lines that can't be attributed go under "(General / Unity)".
        /// </summary>
        private static List<SourceGroup> GroupBySource(List<string> lines, List<bool> isError)
        {
            var map = new Dictionary<string, SourceGroup>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];

                // Skip stack-trace continuation lines (they belong to the previous entry).
                if (IsStackTraceContinuation(line))
                    continue;

                string tag = ExtractSourceTag(line);
                if (string.IsNullOrEmpty(tag))
                    tag = "(General / Unity)";

                if (!map.TryGetValue(tag, out var group))
                {
                    group = new SourceGroup { Source = tag };
                    map[tag] = group;
                }

                // Trim the raw line for the grouped view - strip the BepInEx prefix to
                // keep it concise. e.g.:
                //   [Error  : Unity Log] VerdantsAscentPieces: marble...
                // becomes:
                //   VerdantsAscentPieces: marble...
                string trimmed = TrimBepInExPrefix(line);

                if (isError[i]) { group.Errors++; group.ErrorLines.Add(trimmed); }
                else            { group.Warnings++; group.WarningLines.Add(trimmed); }
            }

            return map.Values.ToList();
        }

        /// <summary>
        /// Extracts a human-readable source tag from a BepInEx log line.
        /// </summary>
        private static string ExtractSourceTag(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;

            // Find the first bracket block: [Error  : Unity Log] or [Warning:Server Devcommands]
            int firstOpen = line.IndexOf('[');
            if (firstOpen < 0) return null;
            int firstClose = line.IndexOf(']', firstOpen + 1);
            if (firstClose < 0) return null;

            string firstBracket = line.Substring(firstOpen + 1, firstClose - firstOpen - 1).Trim();

            // Check for embedded source: "[Warning:Server Devcommands]"
            int colon = firstBracket.IndexOf(':');
            if (colon >= 0 && colon < firstBracket.Length - 1)
            {
                string afterColon = firstBracket.Substring(colon + 1).Trim();
                if (afterColon.Length >= 3 && !string.Equals(afterColon, "Unity Log", StringComparison.OrdinalIgnoreCase))
                    return afterColon;
            }

            // Look for a second bracketed tag: [Warning: Unity Log] [QuestManager] ...
            string rest = line.Substring(firstClose + 1);
            int secondOpen = rest.IndexOf('[');
            if (secondOpen >= 0)
            {
                int secondClose = rest.IndexOf(']', secondOpen + 1);
                if (secondClose > secondOpen)
                {
                    string secondTag = rest.Substring(secondOpen + 1, secondClose - secondOpen - 1).Trim();
                    // Skip embedded timestamps like [04/20/2026 01:55:16]
                    // Skip dedup suffixes like [repeated 2 times]
                    if (secondTag.Length >= 3
                        && !char.IsDigit(secondTag[0])
                        && !secondTag.StartsWith("repeated ", StringComparison.OrdinalIgnoreCase))
                        return secondTag;
                }
            }

            // Look for "PrefixName: " at the start of message body
            string body = rest.TrimStart();
            int colonIdx = body.IndexOf(": ", StringComparison.Ordinal);
            if (colonIdx > 0 && colonIdx <= 60)
            {
                string candidate = body.Substring(0, colonIdx).Trim();
                if (candidate.Length >= 3 && candidate.IndexOf(' ') < 0)
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Strips the BepInEx log-level prefix for the grouped view, leaving just the
        /// message body. e.g. <c>[Error  : Unity Log] Foo: bar</c> -> <c>Foo: bar</c>.
        /// </summary>
        private static string TrimBepInExPrefix(string line)
        {
            int close = line.IndexOf("] ", StringComparison.Ordinal);
            if (close >= 0 && close + 2 < line.Length)
                return line.Substring(close + 2);
            return line;
        }

        // ================================================================
        //  Header + helpers (unchanged)
        // ================================================================

        private static string BuildHeader(string playerName, string platformId,
            int errorCount, int warningCount, int benignSkipped, int duplicatesCollapsed)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Client Errors + Warnings Report");
            sb.AppendLine($"# Player:   {playerName ?? "unknown"}");
            sb.AppendLine($"# SteamID:  {platformId ?? "unknown"}");
            sb.AppendLine($"# Captured: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"# Errors:   {errorCount}");
            sb.AppendLine($"# Warnings: {warningCount}");
            if (benignSkipped > 0)
                sb.AppendLine($"# Benign:   {benignSkipped} (filtered \u2014 known-harmless patterns)");
            if (duplicatesCollapsed > 0)
                sb.AppendLine($"# Deduped:  {duplicatesCollapsed} (repeated duplicates collapsed)");
            sb.AppendLine();
            return sb.ToString();
        }

        private static bool IsBenign(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            for (int i = 0; i < BenignPatterns.Count; i++)
            {
                if (line.IndexOf(BenignPatterns[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool IsStackTraceContinuation(string line)
        {
            return line.StartsWith("  at ", StringComparison.Ordinal)
                || line.StartsWith("Stack trace:", StringComparison.Ordinal)
                || line.StartsWith("UnityEngine.", StringComparison.Ordinal)
                || line.StartsWith("System.", StringComparison.Ordinal)
                || line.StartsWith("\t", StringComparison.Ordinal)
                || line.StartsWith("(wrapper ", StringComparison.Ordinal);
        }

        private static string NormalizeForDedup(string line)
        {
            if (string.IsNullOrEmpty(line)) return line ?? string.Empty;

            int close = line.IndexOf("] ", StringComparison.Ordinal);
            string body = close >= 0 && close + 2 <= line.Length
                ? line.Substring(close + 2)
                : line;

            if (body.Length > 2 && body[0] == '[')
            {
                int ts = body.IndexOf("] ", StringComparison.Ordinal);
                if (ts > 0 && ts < 30) body = body.Substring(ts + 2);
            }

            return body.Trim();
        }
    }
}
