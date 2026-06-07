using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VerdantsAscent.Modules.ClientLogRelay
{
    /// <summary>
    /// Pure, dependency-free helper that produces a side-by-side diff between the client's
    /// and the server's BepInEx plugin lists. The output is a summary + a GUID-sorted list
    /// suitable for a .txt attachment or Discord upload.
    /// </summary>
    public static class ModListDiff
    {
        public sealed class VersionMismatch
        {
            public string Guid;
            public string ClientVersion;
            public string ServerVersion;
        }

        public sealed class Result
        {
            public List<string>           ClientOnly       = new List<string>();
            public List<string>           ServerOnly       = new List<string>();
            public List<VersionMismatch>  VersionMismatches = new List<VersionMismatch>();
            public int                    SharedMatching;
            public string                 Report; // rendered .txt-ready text
        }

        public static Result Compute(
            IReadOnlyDictionary<string, string> clientMods,
            IReadOnlyDictionary<string, string> serverMods,
            string playerName,
            string platformId,
            string brandLabel,
            DateTime capturedUtc)
        {
            var r = new Result();
            if (clientMods == null) clientMods = new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);
            if (serverMods == null) serverMods = new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);

            // Normalise key comparison: case-insensitive on GUID.
            var client = new Dictionary<string, string>(clientMods.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in clientMods) client[kv.Key] = kv.Value;
            var server = new Dictionary<string, string>(serverMods.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in serverMods) server[kv.Key] = kv.Value;

            foreach (var kv in client)
            {
                if (!server.TryGetValue(kv.Key, out var srvVer))
                {
                    r.ClientOnly.Add(kv.Key);
                    continue;
                }
                if (!string.Equals(kv.Value ?? string.Empty, srvVer ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    r.VersionMismatches.Add(new VersionMismatch
                    {
                        Guid = kv.Key,
                        ClientVersion = kv.Value ?? string.Empty,
                        ServerVersion = srvVer ?? string.Empty,
                    });
                }
                else
                {
                    r.SharedMatching++;
                }
            }
            foreach (var kv in server)
            {
                if (!client.ContainsKey(kv.Key)) r.ServerOnly.Add(kv.Key);
            }

            r.ClientOnly.Sort(StringComparer.OrdinalIgnoreCase);
            r.ServerOnly.Sort(StringComparer.OrdinalIgnoreCase);
            r.VersionMismatches = r.VersionMismatches
                .OrderBy(v => v.Guid, StringComparer.OrdinalIgnoreCase)
                .ToList();

            r.Report = Render(r, client, server, playerName, platformId, brandLabel, capturedUtc);
            return r;
        }

        private static string Render(Result r,
            IReadOnlyDictionary<string, string> client,
            IReadOnlyDictionary<string, string> server,
            string playerName, string platformId, string brandLabel, DateTime capturedUtc)
        {
            var sb = new StringBuilder();
            string label = string.IsNullOrEmpty(brandLabel) ? "ClientLogRelay" : brandLabel;
            sb.AppendLine($"# {label} — Client vs Server Mod List Diff");
            sb.AppendLine($"# Player:          {playerName ?? "unknown"}");
            sb.AppendLine($"# PlatformID:      {platformId ?? "unknown"}");
            sb.AppendLine($"# Captured:        {capturedUtc:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"# Client mods:     {client.Count}");
            sb.AppendLine($"# Server mods:     {server.Count}");
            sb.AppendLine($"# Shared matching: {r.SharedMatching}");
            sb.AppendLine($"# Client-only:     {r.ClientOnly.Count}");
            sb.AppendLine($"# Server-only:     {r.ServerOnly.Count}");
            sb.AppendLine($"# Version mismatches: {r.VersionMismatches.Count}");
            sb.AppendLine();

            if (r.VersionMismatches.Count > 0)
            {
                sb.AppendLine("## Version mismatches (client ? server)");
                foreach (var m in r.VersionMismatches)
                    sb.AppendLine($"  {m.Guid,-55} client={m.ClientVersion}  server={m.ServerVersion}");
                sb.AppendLine();
            }

            if (r.ClientOnly.Count > 0)
            {
                sb.AppendLine("## Client-only (installed on client, missing on server)");
                foreach (var g in r.ClientOnly)
                    sb.AppendLine($"  {g,-55} version={SafeLookup(client, g)}");
                sb.AppendLine();
            }

            if (r.ServerOnly.Count > 0)
            {
                sb.AppendLine("## Server-only (installed on server, missing on client)");
                foreach (var g in r.ServerOnly)
                    sb.AppendLine($"  {g,-55} version={SafeLookup(server, g)}");
                sb.AppendLine();
            }

            sb.AppendLine("## All client mods");
            foreach (var kv in client.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append("  C ").Append(kv.Key).Append('=').AppendLine(kv.Value);
            sb.AppendLine();

            sb.AppendLine("## All server mods");
            foreach (var kv in server.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append("  S ").Append(kv.Key).Append('=').AppendLine(kv.Value);

            return sb.ToString();
        }

        private static string SafeLookup(IReadOnlyDictionary<string, string> d, string key)
        {
            if (d == null) return string.Empty;
            return d.TryGetValue(key, out var v) ? (v ?? string.Empty) : string.Empty;
        }
    }
}
