using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Modules.ClientLogRelay
{
    /// <summary>
    /// Pure, dependency-free writer that persists a <see cref="ClientLogArtifacts"/> bundle to
    /// a target directory. Always writes three files (overwriting on each call), keyed to the
    /// client's default folder name:
    ///
    /// <list type="bullet">
    /// <item><c>{targetDir}/{folder}/LogOutput.log</c></item>
    /// <item><c>{targetDir}/{folder}/modlist.txt</c></item>
    /// <item><c>{targetDir}/{folder}/errors_warnings.txt</c></item>
    /// </list>
    ///
    /// Caller controls <c>targetDir</c> so the writer can be reused by any mod.
    /// Returns the absolute path to the per-client folder, or null on failure.
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
