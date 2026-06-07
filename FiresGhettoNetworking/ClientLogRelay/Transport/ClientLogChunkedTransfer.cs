using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Modules.ClientLogRelay.Transport
{
    /// <summary>
    /// Manages reassembly of chunked client log transfers on the server side.
    /// Large logs are split into 200KB chunks on the client and sent via multiple RPCs.
    /// </summary>
    public static class ClientLogChunkedTransfer
    {
        private class TransferState
        {
            public long PeerId;
            public Dictionary<int, byte[]> Chunks = new Dictionary<int, byte[]>();
            public int TotalChunks;
            public float LastChunkTime;
            public Dictionary<string, string> ModList;
            public string SteamId;
        }

        private static readonly Dictionary<long, TransferState> _activeTransfers = new Dictionary<long, TransferState>();
        private const float TRANSFER_TIMEOUT = 60f;

        public class TransferResult
        {
            public byte[] LogBytes;
            public Dictionary<string, string> ModList;
            public string SteamId;
        }

        /// <summary>
        /// Stores metadata (mod list, steam ID) for a peer before chunks arrive.
        /// NEW PROTOCOL: Metadata is sent separately to avoid bloating chunks.
        /// </summary>
        public static void StoreMetadata(long peerId, int totalChunks, Dictionary<string, string> modList, string steamId)
        {
            if (!_activeTransfers.TryGetValue(peerId, out var state))
            {
                state = new TransferState
                {
                    PeerId = peerId,
                    TotalChunks = totalChunks,
                    LastChunkTime = Time.realtimeSinceStartup,
                    ModList = modList,
                    SteamId = steamId
                };
                _activeTransfers[peerId] = state;
                Debug.Log($"[ClientLogChunkedTransfer] Stored metadata for peer {peerId}: {modList?.Count ?? 0} mods, expecting {totalChunks} chunks");
            }
            else
            {
                // Update existing state with metadata
                state.ModList = modList;
                state.SteamId = steamId;
                state.TotalChunks = totalChunks;
                state.LastChunkTime = Time.realtimeSinceStartup;
                Debug.Log($"[ClientLogChunkedTransfer] Updated metadata for peer {peerId}: {modList?.Count ?? 0} mods");
            }
        }

        /// <summary>
        /// Receives a chunk from a client. If this completes the transfer, returns the full log bytes and metadata.
        /// Otherwise returns null.
        /// </summary>
        public static TransferResult ReceiveChunk(long peerId, int chunkIndex, int totalChunks, byte[] chunkData,
            Dictionary<string, string> modList = null, string steamId = null)
        {
            if (!_activeTransfers.TryGetValue(peerId, out var state))
            {
                state = new TransferState
                {
                    PeerId = peerId,
                    TotalChunks = totalChunks,
                    LastChunkTime = Time.realtimeSinceStartup
                };
                _activeTransfers[peerId] = state;
            }

            // Update metadata if provided (for backward compatibility with old protocol)
            // In new protocol, metadata comes via StoreMetadata() before chunks
            if (modList != null || steamId != null)
            {
                if (modList != null) state.ModList = modList;
                if (steamId != null) state.SteamId = steamId;
            }

            // Store chunk
            state.Chunks[chunkIndex] = chunkData;
            state.LastChunkTime = Time.realtimeSinceStartup;

            Debug.Log($"[ClientLogChunkedTransfer] Received chunk {chunkIndex + 1}/{totalChunks} from peer {peerId} ({chunkData.Length} bytes)");

            // Check if complete
            if (state.Chunks.Count == totalChunks)
            {
                Debug.Log($"[ClientLogChunkedTransfer] Transfer complete for peer {peerId} - reassembling {totalChunks} chunks");

                // Reassemble in order
                int totalSize = state.Chunks.Values.Sum(c => c.Length);
                byte[] fullLog = new byte[totalSize];
                int offset = 0;

                for (int i = 0; i < totalChunks; i++)
                {
                    if (!state.Chunks.TryGetValue(i, out var chunk))
                    {
                        Debug.LogWarning($"[ClientLogChunkedTransfer] Missing chunk {i} for peer {peerId} - transfer corrupt!");
                        _activeTransfers.Remove(peerId);
                        return null;
                    }

                    Array.Copy(chunk, 0, fullLog, offset, chunk.Length);
                    offset += chunk.Length;
                }

                // Capture metadata before removing the transfer
                var result = new TransferResult
                {
                    LogBytes = fullLog,
                    ModList = state.ModList,
                    SteamId = state.SteamId
                };

                _activeTransfers.Remove(peerId);
                Debug.Log($"[ClientLogChunkedTransfer] Reassembled {totalSize} bytes from {totalChunks} chunks for peer {peerId}");
                return result;
            }

            return null; // Not complete yet
        }

        /// <summary>
        /// Gets metadata (mod list, steam ID) for an active transfer.
        /// </summary>
        public static bool TryGetMetadata(long peerId, out Dictionary<string, string> modList, out string steamId)
        {
            if (_activeTransfers.TryGetValue(peerId, out var state))
            {
                modList = state.ModList;
                steamId = state.SteamId;
                return true;
            }

            modList = null;
            steamId = null;
            return false;
        }

        /// <summary>
        /// Cleans up transfers that haven't received a chunk in over 60 seconds.
        /// Should be called periodically (e.g. every 30s).
        /// </summary>
        public static void CleanupTimedOutTransfers()
        {
            float now = Time.realtimeSinceStartup;
            var timedOut = _activeTransfers
                .Where(kvp => (now - kvp.Value.LastChunkTime) > TRANSFER_TIMEOUT)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var peerId in timedOut)
            {
                Debug.LogWarning($"[ClientLogChunkedTransfer] Transfer from peer {peerId} timed out after {TRANSFER_TIMEOUT}s - cleaning up");
                _activeTransfers.Remove(peerId);
            }
        }

        /// <summary>
        /// Cancels an active transfer (e.g. when client disconnects).
        /// </summary>
        public static void CancelTransfer(long peerId)
        {
            if (_activeTransfers.Remove(peerId))
            {
                Debug.Log($"[ClientLogChunkedTransfer] Cancelled transfer for peer {peerId}");
            }
        }

        /// <summary>
        /// Receives an update chunk (periodic repush). Similar to ReceiveChunk but simpler
        /// since updates don't include mod lists (server already has them from initial upload).
        /// </summary>
        public static TransferResult ReceiveUpdateChunk(long peerId, int chunkIndex, int totalChunks, byte[] chunkData, string steamId = null)
        {
            // Reuse the same transfer state system, but use a different key to avoid conflicts
            // with ongoing initial uploads
            long updateKey = peerId + 1000000000L; // Offset to avoid collision

            if (!_activeTransfers.TryGetValue(updateKey, out var state))
            {
                state = new TransferState
                {
                    PeerId = updateKey,
                    TotalChunks = totalChunks,
                    LastChunkTime = Time.realtimeSinceStartup,
                    SteamId = steamId
                };
                _activeTransfers[updateKey] = state;
            }

            // Update timing
            state.LastChunkTime = Time.realtimeSinceStartup;

            // Store chunk
            state.Chunks[chunkIndex] = chunkData;
            Debug.Log($"[ClientLogChunkedTransfer] Update chunk {chunkIndex + 1}/{totalChunks} received from peer {peerId} ({chunkData.Length} bytes)");

            // Check if complete
            if (state.Chunks.Count == totalChunks)
            {
                // Reassemble
                int totalSize = state.Chunks.Values.Sum(c => c.Length);
                byte[] fullLog = new byte[totalSize];
                int offset = 0;

                for (int i = 0; i < totalChunks; i++)
                {
                    if (!state.Chunks.TryGetValue(i, out var chunk))
                    {
                        Debug.LogWarning($"[ClientLogChunkedTransfer] Missing update chunk {i} for peer {peerId}");
                        _activeTransfers.Remove(updateKey);
                        return null;
                    }

                    Array.Copy(chunk, 0, fullLog, offset, chunk.Length);
                    offset += chunk.Length;
                }

                var result = new TransferResult
                {
                    LogBytes = fullLog,
                    ModList = null, // Updates don't include mod lists
                    SteamId = state.SteamId
                };

                _activeTransfers.Remove(updateKey);
                Debug.Log($"[ClientLogChunkedTransfer] Reassembled update: {totalSize} bytes from {totalChunks} chunks for peer {peerId}");
                return result;
            }

            return null; // Not complete yet
        }
    }
}
