using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Modules.ClientLogRelay
{
    /// <summary>
    /// Writes a ClientLogArtifacts bundle to disk as LogOutput.log, modlist.txt and errors_warnings.txt under
    /// a per-client folder, overwriting on each call. The caller owns the target directory, so any mod can
    /// reuse it. Returns the folder's absolute path, or null on failure.
    /// </summary>
    public static class ClientLogArtifactWriter
    {
        public static string Write(string targetDir, ClientLogArtifacts artifacts)
        {
            if (artifacts == null) return null;
            if (string.IsNullOrEmpty(targetDir)) return null;

            try
            {
                string folder = Path.Combine(targetDir, artifacts.DefaultFolderName);
                if (!Directory.Exists(folder))
                    Directory.CreateDirectory(folder);

                // 1. Full BepInEx log
                if (artifacts.LogBytes != null && artifacts.LogBytes.Length > 0)
                {
                    File.WriteAllBytes(Path.Combine(folder, "LogOutput.log"), artifacts.LogBytes);
                }

                // 2. Mod list
                if (artifacts.ModList != null && artifacts.ModList.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("# Client BepInEx Mod List");
                    sb.AppendLine($"# Player:    {artifacts.PlayerName}");
                    sb.AppendLine($"# SteamID:   {artifacts.PlatformId}");
                    sb.AppendLine($"# Captured:  {artifacts.CapturedUtc:yyyy-MM-dd HH:mm:ss} UTC");
                    sb.AppendLine($"# Mod Count: {artifacts.ModList.Count}");
                    sb.AppendLine("# Format:    GUID=Version");
                    sb.AppendLine();
                    foreach (var kv in artifacts.ModList.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                        sb.Append(kv.Key).Append('=').AppendLine(kv.Value);
                    File.WriteAllText(Path.Combine(folder, "modlist.txt"), sb.ToString());
                }

                // 3. Errors + warnings report (already pre-computed by the relay)
                if (!string.IsNullOrEmpty(artifacts.ErrorsWarningsReport))
                {
                    File.WriteAllText(Path.Combine(folder, "errors_warnings.txt"),
                        artifacts.ErrorsWarningsReport);
                }

                // 4. Mod diff report (client vs server)
                if (artifacts.ModDiff != null && !string.IsNullOrEmpty(artifacts.ModDiff.Report))
                {
                    File.WriteAllText(Path.Combine(folder, "mod_diff.txt"),
                        artifacts.ModDiff.Report);
                }

                return folder;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay] Failed to write artifacts for {artifacts.PlatformId}: {ex.Message}");
                return null;
            }
        }
    }
}
