using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Modules.ClientLogRelay
{
    /// <summary>
    /// Central fan-out hub for client login artifacts.
    ///
    /// Contract:
    ///  1. The wire transport (VAngarde anti-cheat challenge, or any other RPC) constructs
    ///     a <see cref="ClientLogArtifacts"/> and calls <see cref="ReportArtifacts"/>.
    ///  2. The relay parses errors/warnings once into the artifact, then invokes every
    ///     registered <see cref="IClientLogConsumer"/>.
    ///  3. Each consumer decides independently what to do — write to disk, post to Discord,
    ///     stash in memory, etc.
    ///
    /// Registration is process-global and idempotent (same ConsumerId cannot be registered
    /// twice). All public methods are thread-safe under a simple lock.
    /// </summary>
    public static class ClientLogRelay
    {
        private static readonly object _lock = new object();
        private static readonly List<IClientLogConsumer> _consumers = new List<IClientLogConsumer>();

        /// <summary>
        /// True when at least one consumer is registered. Wire-transport layers should check
        /// this before doing the work of soliciting a log from the client — no consumers,
        /// no point.
        /// </summary>
        public static bool HasConsumers
        {
            get { lock (_lock) { return _consumers.Count > 0; } }
        }

        /// <summary>
        /// Registers a consumer. Returns true if added, false if a consumer with the same
        /// <see cref="IClientLogConsumer.ConsumerId"/> is already present.
        /// </summary>
        public static bool RegisterConsumer(IClientLogConsumer consumer)
        {
            if (consumer == null || string.IsNullOrEmpty(consumer.ConsumerId)) return false;
            lock (_lock)
            {
                for (int i = 0; i < _consumers.Count; i++)
                {
                    if (string.Equals(_consumers[i].ConsumerId, consumer.ConsumerId, StringComparison.Ordinal))
                        return false;
                }
                _consumers.Add(consumer);
                Debug.Log($"[ClientLogRelay] Registered consumer: {consumer.ConsumerId} (total: {_consumers.Count})");
                return true;
            }
        }

        /// <summary>
        /// Unregisters a consumer by id. Returns true if removed.
        /// </summary>
        public static bool UnregisterConsumer(string consumerId)
        {
            if (string.IsNullOrEmpty(consumerId)) return false;
            lock (_lock)
            {
                for (int i = 0; i < _consumers.Count; i++)
                {
                    if (string.Equals(_consumers[i].ConsumerId, consumerId, StringComparison.Ordinal))
                    {
                        _consumers.RemoveAt(i);
                        Debug.Log($"[ClientLogRelay] Unregistered consumer: {consumerId} (total: {_consumers.Count})");
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Called by the wire-transport layer once the client's log + mod list bytes have
        /// been received, verified, and decoded. The relay:
        ///  1. Runs <see cref="LogErrorWarningExtractor"/> over the log bytes once.
        ///  2. Populates the artifact's <c>ErrorsWarningsReport</c> + counts.
        ///  3. Fans out to every registered consumer.
        ///
        /// Consumer exceptions are caught and logged; one failing consumer does not affect
        /// the others.
        /// </summary>
        public static void ReportArtifacts(ClientLogArtifacts artifacts)
        {
            if (artifacts == null) return;

            // Parse once for all consumers
            try
            {
                var extraction = LogErrorWarningExtractor.Extract(
                    artifacts.LogBytes, artifacts.PlayerName, artifacts.PlatformId);
                artifacts.ErrorsWarningsReport = extraction.Report;
                artifacts.ErrorCount           = extraction.ErrorCount;
                artifacts.WarningCount         = extraction.WarningCount;
                artifacts.BenignSkipped        = extraction.BenignSkipped;
                artifacts.DuplicatesCollapsed  = extraction.DuplicatesCollapsed;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay] Extraction failed for {artifacts.PlatformId}: {ex.Message}");
                artifacts.ErrorsWarningsReport = $"# Extraction error: {ex.Message}";
            }

            // Compute mod diff if the server-side mod list was provided
            if (artifacts.ServerMods != null && artifacts.ServerMods.Count > 0)
            {
                try
                {
                    artifacts.ModDiff = ModListDiff.Compute(
                        artifacts.ModList, artifacts.ServerMods,
                        artifacts.PlayerName, artifacts.PlatformId,
                        artifacts.BrandLabel, artifacts.CapturedUtc);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ClientLogRelay] ModListDiff failed for {artifacts.PlatformId}: {ex.Message}");
                }
            }

            // Snapshot consumers inside the lock, invoke outside so a slow consumer
            // cannot stall other ReportArtifacts calls.
            IClientLogConsumer[] snapshot;
            lock (_lock)
            {
                if (_consumers.Count == 0)
                {
                    Debug.Log($"[ClientLogRelay] No consumers; dropping artifacts for {artifacts.PlatformId}");
                    return;
                }
                snapshot = _consumers.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                var c = snapshot[i];
                try
                {
                    c.OnClientArtifacts(artifacts);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ClientLogRelay] Consumer '{c.ConsumerId}' threw: {ex.Message}");
                }
            }
        }

        // ?????????????????????????????????????????????????????????
        //  Cross-mod login-snapshot ownership
        // ?????????????????????????????????????????????????????????
        //  Multiple mods that drop in this folder may each want to POST a Discord embed
        //  on every client login. To avoid double-posting we coordinate through an
        //  AppDomain-level slot keyed by a non-namespaced string constant ? so two mods
        //  built against different renamed copies of this module still see the same
        //  ownership state.
        //
        //  Contract:
        //    ? Every mod that wants to own the login-snapshot channel calls
        //      TryClaimLoginSnapshotOwnership("MyMod.Login", priority) in its plugin init.
        //    ? Highest priority wins. Ties go to the first caller.
        //    ? A consumer checks IsLoginSnapshotOwner(ownerId) in its EnabledGate and
        //      no-ops when the answer is false.
        //    ? If NO mod claims, every consumer stays enabled (backwards-compatible).

        /// <summary>
        /// Fully-qualified string literal; intentionally NOT derived from a namespace so
        /// that two mods holding independent copies of this module still share state.
        /// </summary>
        private const string LoginOwnerKey         = "FiresMods.ClientLogRelay.LoginSnapshotOwner";
        private const string LoginOwnerPriorityKey = "FiresMods.ClientLogRelay.LoginSnapshotOwnerPriority";

        /// <summary>
        /// Tries to take (or keep) ownership of the "post Discord snapshot on login"
        /// channel.
        /// </summary>
        /// <param name="ownerId">Caller's stable id (e.g. <c>"FiresGhettoNetworkMod.Login"</c>).
        /// Used only for diagnostics and self-identification.</param>
        /// <param name="priority">Higher values win. Default 0. Sibling mods that should
        /// take precedence over a baseline mod should claim with a higher number.</param>
        /// <returns>true if <paramref name="ownerId"/> is (now) the owner, false if some
        /// other caller holds a higher-priority claim.</returns>
        public static bool TryClaimLoginSnapshotOwnership(string ownerId, int priority = 0)
        {
            if (string.IsNullOrEmpty(ownerId)) return false;
            lock (_lock)
            {
                string currentOwner     = AppDomain.CurrentDomain.GetData(LoginOwnerKey) as string;
                object currentPrioObj   = AppDomain.CurrentDomain.GetData(LoginOwnerPriorityKey);
                int    currentPriority  = currentPrioObj is int cp ? cp : int.MinValue;

                if (string.IsNullOrEmpty(currentOwner))
                {
                    AppDomain.CurrentDomain.SetData(LoginOwnerKey, ownerId);
                    AppDomain.CurrentDomain.SetData(LoginOwnerPriorityKey, priority);
                    Debug.Log($"[ClientLogRelay] Login snapshot owner: '{ownerId}' (priority {priority})");
                    return true;
                }

                if (string.Equals(currentOwner, ownerId, StringComparison.Ordinal))
                {
                    // Same owner re-claiming; bump priority if higher.
                    if (priority > currentPriority)
                        AppDomain.CurrentDomain.SetData(LoginOwnerPriorityKey, priority);
                    return true;
                }

                if (priority > currentPriority)
                {
                    Debug.Log($"[ClientLogRelay] Login snapshot owner changed: " +
                              $"'{currentOwner}' (priority {currentPriority}) ? '{ownerId}' (priority {priority})");
                    AppDomain.CurrentDomain.SetData(LoginOwnerKey, ownerId);
                    AppDomain.CurrentDomain.SetData(LoginOwnerPriorityKey, priority);
                    return true;
                }

                // Another claim wins.
                Debug.Log($"[ClientLogRelay] Login snapshot claim declined for '{ownerId}' " +
                          $"(priority {priority}); current owner '{currentOwner}' (priority {currentPriority})");
                return false;
            }
        }

        /// <summary>
        /// True if <paramref name="ownerId"/> is the current owner, OR if no mod has ever
        /// claimed ownership (so we default to "allowed" when nobody bothered to opt in).
        /// This is the call consumers make inside their <c>EnabledGate</c>.
        /// </summary>
        public static bool IsLoginSnapshotOwner(string ownerId)
        {
            if (string.IsNullOrEmpty(ownerId)) return false;
            string currentOwner = AppDomain.CurrentDomain.GetData(LoginOwnerKey) as string;
            if (string.IsNullOrEmpty(currentOwner)) return true; // nobody claimed ? everyone allowed
            return string.Equals(currentOwner, ownerId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns the current login-snapshot owner id, or null if none has been claimed.
        /// Exposed for diagnostics / console commands.
        /// </summary>
        public static string GetLoginSnapshotOwner()
        {
            return AppDomain.CurrentDomain.GetData(LoginOwnerKey) as string;
        }

        // ?????????????????????????????????????????????????????????
        //  Log-request reaction dispatch
        // ?????????????????????????????????????????????????????????
        //  The host mod plugs in a single ILogRequestHandler. When it spots a recognised
        //  reaction on a previously-posted snapshot message (via whatever bot / gateway /
        //  polling it has available) it calls TryDispatchLogRequest and the relay:
        //    1. Resolves the message id through LogRequestRegistry.
        //    2. Authorises the reacting user via the handler.
        //    3. Invokes the handler with the resolved context.
        //
        //  The relay never talks to Discord on its own here ? it just routes.

        private static Interactions.ILogRequestHandler _logRequestHandler;

        /// <summary>
        /// Installs (or replaces) the <see cref="Interactions.ILogRequestHandler"/> used to
        /// service reaction-driven full-log requests. Pass <c>null</c> to remove.
        /// </summary>
        public static void RegisterLogRequestHandler(Interactions.ILogRequestHandler handler)
        {
            lock (_lock)
            {
                _logRequestHandler = handler;
                Debug.Log(handler != null
                    ? $"[ClientLogRelay] LogRequestHandler installed: {handler.GetType().FullName}"
                    : "[ClientLogRelay] LogRequestHandler cleared");
            }
        }

        /// <summary>
        /// True when both a handler and at least one registry entry exist. Cheap pre-check
        /// host pollers can use to skip reaction parsing altogether when there's nothing
        /// to dispatch to.
        /// </summary>
        public static bool HasLogRequestHandler
        {
            get { lock (_lock) { return _logRequestHandler != null; } }
        }

        /// <summary>
        /// Main entry point for reaction dispatch. Called by the host mod's bot listener
        /// (or equivalent) once it has identified an incoming emoji-reaction event.
        /// Returns true if the event was resolved to a known message AND an authorised
        /// user, false otherwise. Not an error ? an unknown message id just means the
        /// reaction is on something this module didn't post.
        /// </summary>
        /// <param name="messageId">Discord message snowflake id the reaction was added to.</param>
        /// <param name="discordUserId">Author of the reaction.</param>
        /// <param name="emoji">Raw emoji string / name the bot saw. Forwarded unchanged to
        /// the handler so handlers can enforce an emoji allowlist if they want.</param>
        public static bool TryDispatchLogRequest(string messageId, string discordUserId, string emoji)
        {
            Interactions.ILogRequestHandler handler;
            lock (_lock) { handler = _logRequestHandler; }
            if (handler == null) return false;

            if (!Interactions.LogRequestRegistry.TryGet(messageId, out var ctx))
                return false;

            bool authorised;
            try { authorised = handler.IsAuthorized(discordUserId); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay] ILogRequestHandler.IsAuthorized threw: {ex.Message}");
                return false;
            }

            if (!authorised)
            {
                Debug.Log($"[ClientLogRelay] Log request from unauthorised Discord user '{discordUserId}' on message {messageId}; ignoring");
                return false;
            }

            try
            {
                handler.HandleRequest(ctx, discordUserId, emoji);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay] ILogRequestHandler.HandleRequest threw: {ex.Message}");
                return false;
            }
        }
    }
}
