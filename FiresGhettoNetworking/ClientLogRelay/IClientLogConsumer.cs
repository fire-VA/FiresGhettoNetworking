namespace VerdantsAscent.Modules.ClientLogRelay
{
    /// <summary>
    /// Implemented by any code that wants to receive client login artifacts.
    /// Consumers register themselves with <see cref="ClientLogRelay.RegisterConsumer"/> at
    /// plugin startup and are fanned out to on every successful login.
    ///
    /// Implementations must be safe to call on the main thread. They should NOT block -
    /// network I/O must be dispatched asynchronously (e.g. coroutine or Task).
    /// </summary>
    public interface IClientLogConsumer
    {
        /// <summary>
        /// Stable identifier for this consumer, used in diagnostic logs and to prevent
        /// duplicate registrations. Convention: <c>"MyMod.DiskConsumer"</c>.
        /// </summary>
        string ConsumerId { get; }

        /// <summary>
        /// Called by the relay on the server, on the main thread, after a client has
        /// successfully completed the login challenge and their artifacts have been parsed.
        ///
        /// The <paramref name="artifacts"/> instance is shared across all consumers - do not
        /// mutate it.
        /// </summary>
        void OnClientArtifacts(ClientLogArtifacts artifacts);
    }
}
