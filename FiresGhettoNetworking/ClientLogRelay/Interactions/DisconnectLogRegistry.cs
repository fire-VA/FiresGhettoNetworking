using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Modules.ClientLogRelay.Interactions
{
    /// <summary>
    /// Tracks which players an admin has requested "send log on disconnect" for via the
    /// ?? reaction. When a tracked player disconnects, the server reads the latest cached
    /// log from disk and posts it to the webhook.
    ///
    /// Entries are keyed by platform ID and include enough context to build the webhook
    /// post without needing the original <see cref="LogRequestContext"/>.
    /// </summary>
    public static class DisconnectLogRegistry
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Entry> _entries
            = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public sealed class Entry
        {
            public string PlatformId;
            public string PlayerName;
            public DateTime RegisteredUtc;
            public string RequestedByName;
        }

        /// <summary>Registers a player for disconnect-log capture. Overwrites if already present.</summary>
        public static void Register(string platformId, string playerName, string requestedByName)
        {
            if (string.IsNullOrEmpty(platformId)) return;
            lock (_lock)
            {
                _entries[platformId] = new Entry
                {
                    PlatformId = platformId,
                    PlayerName = playerName ?? "unknown",
                    RegisteredUtc = DateTime.UtcNow,
                    RequestedByName = requestedByName ?? "unknown",
                };
                Debug.Log($"[ClientLogRelay] DisconnectLogRegistry: registered '{playerName}' ({platformId}) for disconnect capture, requested by {requestedByName}");
            }
        }

        /// <summary>
        /// Removes and returns the entry for <paramref name="platformId"/> if present.
        /// Returns null if the player was not registered.
        /// </summary>
        public static Entry TakeIfRegistered(string platformId)
        {
            if (string.IsNullOrEmpty(platformId)) return null;
            lock (_lock)
            {
                if (_entries.TryGetValue(platformId, out var entry))
                {
                    _entries.Remove(platformId);
                    return entry;
                }
                return null;
            }
        }

        public static bool IsRegistered(string platformId)
        {
            if (string.IsNullOrEmpty(platformId)) return false;
            lock (_lock) { return _entries.ContainsKey(platformId); }
        }

        public static int Count
        {
            get { lock (_lock) { return _entries.Count; } }
        }
    }
}
