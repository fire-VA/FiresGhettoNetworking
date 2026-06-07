using System;
using System.Collections.Generic;

namespace VerdantsAscent.Modules.ClientLogRelay
{
    /// <summary>
    /// Data transfer object passed to every <see cref="IClientLogConsumer"/> when a client has
    /// finished logging in and their BepInEx log + mod list have been received on the server.
    ///
    /// Construction is done by whichever wire-transport layer owns the challenge/response
    /// protocol (e.g. the VAngarde anti-cheat's challenge handler). After the bytes arrive,
    /// the transport hands the populated DTO to <see cref="ClientLogRelay.ReportArtifacts"/>
    /// and the relay does parsing, persistence, and fan-out.
    ///
    /// All fields are read-only after construction — consumers must not mutate the byte arrays
    /// or dictionaries they receive. This keeps the fan-out safe when multiple consumers run
    /// concurrently on the main thread.
    /// </summary>
    public sealed class ClientLogArtifacts
    {
        public string PlatformId   { get; }
        public string PlayerName   { get; }
        public byte[] LogBytes     { get; }
        public IReadOnlyDictionary<string, string> ModList { get; }
        public IReadOnlyDictionary<string, string> ServerMods { get; }
        public string BrandLabel   { get; }
        public DateTime CapturedUtc { get; }

        /// <summary>
        /// Populated by <see cref="ClientLogRelay.ReportArtifacts"/> after parsing the log.
        /// Consumers receive the pre-computed report; they do not need to parse themselves.
        /// </summary>
        public string ErrorsWarningsReport { get; internal set; }

        public int ErrorCount   { get; internal set; }
        public int WarningCount { get; internal set; }
        public int BenignSkipped { get; internal set; }
        public int DuplicatesCollapsed { get; internal set; }

        /// <summary>
        /// Populated by <see cref="ClientLogRelay.ReportArtifacts"/> when <see cref="ServerMods"/> is non-null.
        /// </summary>
        public ModListDiff.Result ModDiff { get; internal set; }

        public ClientLogArtifacts(string platformId, string playerName,
            byte[] logBytes, IReadOnlyDictionary<string, string> modList,
            IReadOnlyDictionary<string, string> serverMods = null,
            string brandLabel = null)
        {
            PlatformId = platformId ?? string.Empty;
            PlayerName = string.IsNullOrEmpty(playerName) ? "unknown" : playerName;
            LogBytes   = logBytes   ?? Array.Empty<byte>();
            ModList    = modList    ?? new Dictionary<string, string>(0);
            ServerMods = serverMods;
            BrandLabel = brandLabel;
            CapturedUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Filesystem-safe version of the platform ID (no colons or slashes).
        /// </summary>
        public string SafePlatformId
            => (PlatformId ?? "unknown").Replace(":", "_").Replace("/", "_").Replace("\\", "_");

        /// <summary>
        /// Filesystem-safe version of the player name.
        /// </summary>
        public string SafePlayerName
        {
            get
            {
                if (string.IsNullOrEmpty(PlayerName)) return "unknown";
                var invalid = System.IO.Path.GetInvalidFileNameChars();
                return string.Concat(PlayerName.Split(invalid));
            }
        }

        /// <summary>
        /// Default subdirectory name used by <see cref="Consumers.DiskConsumer"/> for this
        /// client. Format: <c>{SafePlayerName}_{SafePlatformId}</c>.
        /// </summary>
        public string DefaultFolderName => $"{SafePlayerName}_{SafePlatformId}";
    }
}
