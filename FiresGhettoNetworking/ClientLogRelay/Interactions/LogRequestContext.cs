using System;

namespace VerdantsAscent.Modules.ClientLogRelay.Interactions
{
    /// <summary>
    /// Snapshot of what a single Discord webhook message represents ? captured at the
    /// moment the snapshot is posted so the host mod can later resolve a reaction back to
    /// the player it came from.
    ///
    /// Intentionally plain data. No references to Valheim types, ZNet, ZPackage, or any
    /// runtime-specific APIs; this keeps the whole interaction layer copy-pasteable along
    /// with the rest of the module.
    /// </summary>
    public sealed class LogRequestContext
    {
        /// <summary>Discord message snowflake id returned from the webhook POST.</summary>
        public string MessageId  { get; }

        /// <summary>Discord channel id the snapshot was posted in.</summary>
        public string ChannelId  { get; }

        /// <summary>Platform id (Steam id or equivalent) of the player the snapshot is about.</summary>
        public string PlatformId { get; }

        /// <summary>Display name of the player the snapshot is about.</summary>
        public string PlayerName { get; }

        /// <summary>UTC timestamp the snapshot was captured (not when it was posted).</summary>
        public DateTime CapturedUtc { get; }

        /// <summary>Consumer id that posted the snapshot (for diagnostics).</summary>
        public string ConsumerId { get; }

        public LogRequestContext(string messageId, string platformId, string playerName,
            DateTime capturedUtc, string consumerId, string channelId = null)
        {
            MessageId   = messageId   ?? string.Empty;
            ChannelId   = channelId   ?? string.Empty;
            PlatformId  = platformId  ?? string.Empty;
            PlayerName  = playerName  ?? "unknown";
            CapturedUtc = capturedUtc;
            ConsumerId  = consumerId  ?? string.Empty;
        }
    }
}
