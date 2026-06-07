using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace VerdantsAscent.Modules.ClientLogRelay.Transport
{
    /// <summary>
    /// Client-side helpers that assemble the raw inputs needed to build a
    /// <see cref="ClientLogArtifacts"/> on the server.
    ///
    /// The relay itself deliberately knows nothing about the wire — every mod picks its own
    /// RPC / protocol / compression story. This helper exists so that the <b>client</b> half
    /// of the pipeline (read <c>BepInEx/LogOutput.log</c>, enumerate <c>Chainloader.PluginInfos</c>)
    /// doesn't have to be re-derived inside every consumer. Drop it into the other mod,
    /// call <see cref="ReadLocalBepInExLog"/> + <see cref="BuildLocalModList"/>, serialise
    /// whichever way you want, and forward the bytes to the server.
    ///
    /// Dependencies: BepInEx only (same as the rest of this module). No ZNet, no ZPackage,
    /// no Valheim types.
    /// </summary>
    public static class ClientLogCollector
    {
        /// <summary>
        /// Reads the entirety of the client's <c>BepInEx/LogOutput.log</c> file with a
        /// shared-read file lock so the BepInEx log sink can continue writing while we
        /// snapshot. Returns null on any failure (missing file, IO error).
        /// </summary>
        public static byte[] ReadLocalBepInExLog()
        {
            try
            {
                string logPath = Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");
                if (!File.Exists(logPath)) return null;

                using (var fs = new FileStream(logPath, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite))
                {
                    long length = fs.Length;
                    if (length <= 0) return null;

                    byte[] data = new byte[length];
                    int offset = 0;
                    while (offset < data.Length)
                    {
                        int read = fs.Read(data, offset, data.Length - offset);
                        if (read <= 0) break;
                        offset += read;
                    }

                    if (offset == data.Length) return data;

                    byte[] trimmed = new byte[offset];
                    Array.Copy(data, 0, trimmed, 0, offset);
                    return trimmed;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay] ReadLocalBepInExLog failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Snapshots the currently loaded BepInEx plugin set into a <c>GUID=Version</c> map.
        /// Keys are compared case-insensitively so duplicate GUIDs (which should not happen
        /// in practice) collapse deterministically.
        /// </summary>
        public static Dictionary<string, string> BuildLocalModList()
        {
            var modList = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var plugins = BepInEx.Bootstrap.Chainloader.PluginInfos;
                if (plugins == null) return modList;

                foreach (var kvp in plugins)
                {
                    var meta = kvp.Value?.Metadata;
                    if (meta == null || string.IsNullOrEmpty(meta.GUID)) continue;
                    modList[meta.GUID] = meta.Version != null ? meta.Version.ToString() : "?";
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay] BuildLocalModList failed: {ex.Message}");
            }
            return modList;
        }

        /// <summary>
        /// One-shot convenience wrapper: reads the log bytes and mod list in one call and
        /// returns them via out parameters. Returns <c>true</c> if both pieces were obtained
        /// (log bytes may still be empty if the file was zero-length, but the method returns
        /// true as long as the mod list could be built).
        /// </summary>
        public static bool TryCollect(out byte[] logBytes, out Dictionary<string, string> modList)
        {
            logBytes = ReadLocalBepInExLog();
            modList  = BuildLocalModList();
            return modList != null;
        }

        /// <summary>
        /// Off-thread variant of <see cref="ReadLocalBepInExLog"/>. Schedules the file read
        /// on a <see cref="ThreadPool"/> worker so it never blocks the Unity main thread,
        /// then invokes <paramref name="onComplete"/> with the bytes (or <c>null</c> on
        /// failure) via <c>MainThreadDispatcher.Enqueue</c> so callers can safely touch
        /// Unity APIs from the callback.
        ///
        /// Preferred in hot paths like the anti-cheat challenge response where a
        /// multi-megabyte synchronous read would otherwise stall a login frame.
        /// </summary>
        /// <param name="onComplete">Invoked on the main thread with the log bytes (or null).
        /// Never null-checked ? caller must provide a handler.</param>
        public static void ReadLocalBepInExLogAsync(Action<byte[]> onComplete)
        {
            if (onComplete == null) return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                byte[] bytes = null;
                try
                {
                    bytes = ReadLocalBepInExLog();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ClientLogRelay] ReadLocalBepInExLogAsync worker failed: {ex.Message}");
                }

                byte[] result = bytes;
                try { onComplete(result); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ClientLogRelay] ReadLocalBepInExLogAsync callback threw: {ex.Message}");
                }
            });
        }
    }
}
