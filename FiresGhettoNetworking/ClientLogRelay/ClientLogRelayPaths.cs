using System;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Modules.ClientLogRelay
{
    /// <summary>
    /// Conventional filesystem paths used by the stock <see cref="Consumers.DiskConsumer"/>.
    /// A mod that drops in this folder can pass the delegate
    /// <c>() =&gt; ClientLogRelayPaths.GetDefaultClientLogsDir("MyMod")</c> as the
    /// <c>rootDirResolver</c> and get the same layout every consumer in this ecosystem uses:
    ///
    /// <code>
    ///   {BepInEx config}/{ModId}/ClientLogs/
    ///     {SafePlayerName}_{SafePlatformId}/
    ///       LogOutput.log
    ///       modlist.txt
    ///       errors_warnings.txt
    /// </code>
    ///
    /// The helper creates the directory on first access so the consumer does not have to.
    /// Dependencies: BepInEx only.
    /// </summary>
    public static class ClientLogRelayPaths
    {
        /// <summary>
        /// Default folder name appended under <c>{ConfigPath}/{ModId}</c>. Kept as a public
        /// const so downstream tooling can reference it without magic strings.
        /// </summary>
        public const string DefaultFolderName = "ClientLogs";

        /// <summary>
        /// Returns <c>{ConfigPath}/{modId}/ClientLogs</c> and ensures the directory exists.
        /// <paramref name="modId"/> should be a filesystem-safe identifier (typically the
        /// BepInEx plugin GUID or the conventional mod short name); it is not sanitised
        /// further here.
        /// </summary>
        public static string GetDefaultClientLogsDir(string modId)
        {
            if (string.IsNullOrEmpty(modId))
                throw new ArgumentException("modId must be provided", nameof(modId));

            string dir = Path.Combine(BepInEx.Paths.ConfigPath, modId, DefaultFolderName);
            TryEnsureDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Returns a subdirectory under the default client-logs root, creating it on first
        /// access. Example: <c>GetDefaultClientLogsDir("MyMod", "archived")</c> ?
        /// <c>{ConfigPath}/MyMod/ClientLogs/archived</c>.
        /// </summary>
        public static string GetDefaultClientLogsDir(string modId, string subfolder)
        {
            string root = GetDefaultClientLogsDir(modId);
            if (string.IsNullOrEmpty(subfolder)) return root;

            string dir = Path.Combine(root, subfolder);
            TryEnsureDirectory(dir);
            return dir;
        }

        private static void TryEnsureDirectory(string dir)
        {
            try
            {
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay] Failed to create directory '{dir}': {ex.Message}");
            }
        }
    }
}
