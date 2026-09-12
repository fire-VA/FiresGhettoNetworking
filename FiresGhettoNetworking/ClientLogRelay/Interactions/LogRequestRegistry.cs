using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Modules.ClientLogRelay.Interactions
{
    /// <summary>
    /// Process-global cache of <see cref="LogRequestContext"/> entries keyed by Discord
    /// message id. Populated by <see cref="Consumers.DiscordWebhookConsumer"/> after a
    /// successful webhook POST; read by the host mod's bot listener when it detects a
    /// user reaction on a previously-posted snapshot message.
    ///
    /// Entries expire after <see cref="DefaultTtl"/> to keep the cache bounded across
    /// long uptimes. Thread-safe.
    /// </summary>
    public static class LogRequestRegistry
    {
        /// <summary>How long entries are retained before being eligible for eviction.</summary>
        public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

        private static readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly object _lock = new object();
        private static DateTime _lastJanitorUtc = DateTime.UtcNow;

        private struct Entry
        {
            public LogRequestContext Context;
            public DateTime          ExpiresUtc;
        }

        /// <summary>
        /// Remember a newly-posted snapshot message so a later reaction on it can be
        /// resolved back to the player. No-ops if <paramref name="context"/> or its message id
        /// is null/empty.
        /// </summary>
        public static void Register(LogRequestContext context, TimeSpan? ttl = null)
        {
            if (context == null || string.IsNullOrEmpty(context.MessageId)) return;
            var expires = DateTime.UtcNow + (ttl ?? DefaultTtl);

            lock (_lock)
            {
                _entries[context.MessageId] = new Entry { Context = context, ExpiresUtc = expires };
                RunJanitorIfDue();
            }
        }

        /// <summary>
        /// Resolve a message id back to its <see cref="LogRequestContext"/>. Expired or
        /// unknown ids return <c>false</c> with <paramref name="context"/> set to null.
        /// </summary>
        public static bool TryGet(string messageId, out LogRequestContext context)
        {
            context = null;
            if (string.IsNullOrEmpty(messageId)) return false;

            lock (_lock)
            {
                if (!_entries.TryGetValue(messageId, out var entry)) return false;
                if (entry.ExpiresUtc < DateTime.UtcNow)
                {
                    _entries.Remove(messageId);
                    return false;
                }
                context = entry.Context;
                return true;
            }
        }

        /// <summary>Drops an entry explicitly (e.g. after the request has been fulfilled).</summary>
        public static void Forget(string messageId)
        {
            if (string.IsNullOrEmpty(messageId)) return;
            lock (_lock) { _entries.Remove(messageId); }
        }

        /// <summary>Clears all entries. Mainly for tests and plugin teardown.</summary>
        public static void Clear()
        {
            lock (_lock) { _entries.Clear(); }
        }

        /// <summary>Current live entry count (for diagnostics).</summary>
        public static int Count
        {
            get { lock (_lock) { return _entries.Count; } }
        }

        /// <summary>
        /// Non-expired snapshot of every currently-registered context. The returned array
        /// is a copy, so callers can iterate freely without holding the lock. Used by host
        /// bot listeners to decide which message ids to poll for reactions.
        /// </summary>
        public static LogRequestContext[] Snapshot()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var alive = new List<LogRequestContext>(_entries.Count);
                foreach (var kv in _entries)
                {
                    if (kv.Value.ExpiresUtc >= now) alive.Add(kv.Value.Context);
                }
                return alive.ToArray();
            }
        }

        // Sweep expired entries at most once per minute. Cheap enough to inline on the
        // Register path so we don't need a dedicated coroutine.
        private static void RunJanitorIfDue()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastJanitorUtc) < TimeSpan.FromMinutes(1)) return;
            _lastJanitorUtc = now;

            List<string> expired = null;
            foreach (var kv in _entries)
            {
                if (kv.Value.ExpiresUtc < now)
                {
                    if (expired == null) expired = new List<string>();
                    expired.Add(kv.Key);
                }
            }
            if (expired != null)
            {
                for (int i = 0; i < expired.Count; i++) _entries.Remove(expired[i]);
                if (expired.Count > 0)
                    Debug.Log($"[ClientLogRelay] LogRequestRegistry swept {expired.Count} expired entr(ies)");
            }
        }
    }
}
