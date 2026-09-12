namespace VerdantsAscent.Modules.ClientLogRelay.Interactions
{
    /// <summary>
    /// Contract the host mod implements to turn a recognised reaction into the actual
    /// "post the full log to Discord" action.
    ///
    /// The relay module deliberately knows nothing about:
    ///   - how the host discovers Discord reactions (bot poll, gateway, HTTP, ...)
    ///   - how the host authenticates the reacting user
    ///   - how the host fetches the cached log bytes for a given platform id
    ///
    /// Those are all mod-specific concerns. The module only owns the registry of which
    /// message ids map to which players, plus the dispatch plumbing.
    /// </summary>
    public interface ILogRequestHandler
    {
        /// <summary>
        /// Returns true if the Discord user identified by <paramref name="discordUserId"/>
        /// is allowed to request full-log uploads. Called once per incoming reaction event.
        /// Typically a membership check against an admin allowlist kept in a mod config.
        /// </summary>
        bool IsAuthorized(string discordUserId);

        /// <summary>
        /// Invoked when an authorised user reacted with the configured emoji on a
        /// previously-posted snapshot message. The handler is expected to locate the
        /// cached log bytes for <see cref="LogRequestContext.PlatformId"/> and POST them
        /// to the Discord webhook itself (the module doesn't touch the bytes).
        /// Exceptions are caught by the caller; implementations should still log their
        /// own errors for diagnosability.
        /// </summary>
        void HandleRequest(LogRequestContext context, string discordUserId, string emoji);
    }
}
