using System;
using UnityEngine;

namespace VerdantsAscent.Modules.ClientLogRelay.Consumers
{
    /// <summary>
    /// Stock consumer that persists artifacts to disk via
    /// <see cref="ClientLogArtifactWriter"/>. Pass a function that returns the root directory
    /// each call so live config changes are picked up automatically.
    /// </summary>
    public sealed class DiskConsumer : IClientLogConsumer
    {
        private readonly Func<string> _rootDirResolver;
        private readonly Func<bool>   _enabledGate;

        public string ConsumerId { get; }

        /// <param name="consumerId">Stable id, e.g. <c>"MyMod.Disk"</c>.</param>
        /// <param name="rootDirResolver">Returns the root "ClientLogs" directory. Per-client
        /// subfolders are created automatically.</param>
        /// <param name="enabledGate">Optional live gate; if null, consumer is always on.</param>
        public DiskConsumer(string consumerId, Func<string> rootDirResolver,
            Func<bool> enabledGate = null)
        {
            ConsumerId = consumerId ?? throw new ArgumentNullException(nameof(consumerId));
            _rootDirResolver = rootDirResolver ?? throw new ArgumentNullException(nameof(rootDirResolver));
            _enabledGate = enabledGate;
        }

        public void OnClientArtifacts(ClientLogArtifacts artifacts)
        {
            if (_enabledGate != null && !_enabledGate()) return;

            string root;
            try { root = _rootDirResolver(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay:{ConsumerId}] Root resolver threw: {ex.Message}");
                return;
            }

            if (string.IsNullOrEmpty(root))
            {
                Debug.LogWarning($"[ClientLogRelay:{ConsumerId}] Root directory is empty; skipping");
                return;
            }

            string folder = ClientLogArtifactWriter.Write(root, artifacts);
            if (folder != null)
            {
                Debug.Log($"[ClientLogRelay:{ConsumerId}] Wrote artifacts for {artifacts.PlatformId} -> {folder}");
            }
        }
    }
}
