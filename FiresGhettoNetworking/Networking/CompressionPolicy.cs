using System;
using System.Collections.Generic;

namespace FiresGhettoNetworkMod
{
    internal enum SendOutcome
    {
        Deflated,
        Segmented,
        AllGzip,
        DidNotShrink,
        Small,
        SkippedRpc,
        Escaped,
        EncodeFailed,
        NotCompressed
    }

    /// <summary>
    /// Decides what goes on the wire for each outgoing packet: a compressed frame when compressing is agreed and pays, the
    /// packet in a stored frame when it happens to begin with the frame magic and the peer reads frames, otherwise the packet.
    /// RPCs whose packets keep failing to shrink stop being deflated for a while.
    /// </summary>
    internal sealed class CompressionPolicy
    {
        // Measured on the game's zlib: below this Valheim's RPCs save a few bytes at most once the frame header is paid for.
        public const int MinCompressBytes = 512;

        // An RPC whose packets fail to shrink this many times in a row is not deflated for a while, the pause doubling each
        // time up to the cap; one packet that compresses clears it. Mod config sync, already deflated by ServerSync or Jotunn,
        // is the typical case.
        public const int FailuresBeforeSkipping = 3;
        public const int FirstSkipPackets = 32;
        public const int MaxSkipPackets = 1024;
        public const int MaxTrackedRpcs = 512;

        // RoutedRPC packets: outer method hash, package length, message id, sender, target, target ZDO (long + uint), then the
        // routed method hash.
        private const int RoutedMethodHashOffset = 44;

        private sealed class RpcHistory
        {
            public int FailuresInARow;
            public int SkipPackets;
            public int SkipRemaining;
        }

        private readonly int _zdoDataHash;
        private readonly int _routedRpcHash;
        private readonly Action<Exception> _onEncodeFailure;
        private readonly List<PacketFrame.Region> _regions = new List<PacketFrame.Region>();
        private readonly Dictionary<int, RpcHistory> _rpcHistory = new Dictionary<int, RpcHistory>();

        public CompressionPolicy(int zdoDataHash, int routedRpcHash, Action<Exception> onEncodeFailure)
        {
            _zdoDataHash = zdoDataHash;
            _routedRpcHash = routedRpcHash;
            _onEncodeFailure = onEncodeFailure;
        }

        public int TrackedRpcs => _rpcHistory.Count;

        public void Reset() => _rpcHistory.Clear();

        public byte[] FrameForPeer(byte[] packet, bool compress, bool peerReadsFrames, out SendOutcome outcome)
        {
            outcome = SendOutcome.NotCompressed;
            if (!peerReadsFrames || packet.Length < PacketFrame.HeaderBytes) return packet;

            byte[] frame = compress ? TryCompress(packet, out outcome) : null;
            if (frame != null) return frame;
            if (!PacketFrame.StartsWithMagic(packet)) return packet;

            outcome = SendOutcome.Escaped;
            return PacketFrame.EncodeStored(packet);
        }

        private byte[] TryCompress(byte[] packet, out SendOutcome outcome)
        {
            if (packet.Length < MinCompressBytes)
            {
                outcome = SendOutcome.Small;
                return null;
            }

            int rpc = LearningKey(packet);
            if (IsSkipping(rpc))
            {
                outcome = SendOutcome.SkippedRpc;
                return null;
            }

            PacketFrame.EncodeResult result;
            byte[] frame;
            try
            {
                result = PacketFrame.Encode(packet, _regions, out frame);
            }
            catch (Exception ex)
            {
                _onEncodeFailure?.Invoke(ex);
                outcome = SendOutcome.EncodeFailed;
                return null;
            }

            Learn(rpc, result);
            switch (result)
            {
                case PacketFrame.EncodeResult.Deflated: outcome = SendOutcome.Deflated; break;
                case PacketFrame.EncodeResult.Segmented: outcome = SendOutcome.Segmented; break;
                case PacketFrame.EncodeResult.NothingToCompress: outcome = SendOutcome.AllGzip; break;
                default: outcome = SendOutcome.DidNotShrink; break;
            }
            return frame;
        }

        // Which RPC a packet carries, for remembering RPCs that do not compress. ZDO packets mix every kind of object, so they
        // are never skipped as a whole; their gzip data is kept out of the deflate stream instead.
        private int LearningKey(byte[] packet)
        {
            int method = PacketFrame.ReadInt(packet, 0);
            if (method == _zdoDataHash) return 0;
            if (method == _routedRpcHash) return packet.Length >= RoutedMethodHashOffset + 4 ? PacketFrame.ReadInt(packet, RoutedMethodHashOffset) : 0;
            return method;
        }

        private bool IsSkipping(int rpc)
        {
            if (rpc == 0 || !_rpcHistory.TryGetValue(rpc, out RpcHistory history) || history.SkipRemaining <= 0) return false;
            history.SkipRemaining--;
            return true;
        }

        private void Learn(int rpc, PacketFrame.EncodeResult result)
        {
            if (rpc == 0 || result == PacketFrame.EncodeResult.NothingToCompress) return;

            _rpcHistory.TryGetValue(rpc, out RpcHistory history);
            if (result != PacketFrame.EncodeResult.DidNotShrink)
            {
                if (history != null)
                {
                    history.FailuresInARow = 0;
                    history.SkipPackets = 0;
                }
                return;
            }

            if (history == null)
            {
                if (_rpcHistory.Count >= MaxTrackedRpcs) _rpcHistory.Clear();
                history = new RpcHistory();
                _rpcHistory[rpc] = history;
            }

            if (++history.FailuresInARow < FailuresBeforeSkipping) return;
            history.FailuresInARow = 0;
            history.SkipPackets = history.SkipPackets == 0 ? FirstSkipPackets : Math.Min(history.SkipPackets * 2, MaxSkipPackets);
            history.SkipRemaining = history.SkipPackets;
        }
    }
}
