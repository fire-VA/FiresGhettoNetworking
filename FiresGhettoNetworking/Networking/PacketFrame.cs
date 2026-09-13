using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// The wire frame for FGN-compressed packets. A frame starts with the "FGD2" magic and carries the original packet's length
    /// and Adler-32, and is fully validated before use: Unity's zlib returns truncated or corrupted input as short or altered
    /// data without raising an error, so nothing short of these checks keeps a damaged packet away from Valheim.
    ///
    /// Layout, little-endian: magic (4), method (1), packet length (int32), Adler-32 of the packet (uint32), then by method
    ///   Stored:    the packet bytes.
    ///   Deflate:   a raw deflate stream of the packet.
    ///   Segmented: region count (int32); per region its offset and length in the packet (int32 each, ascending, non-overlapping);
    ///              deflate length (int32); a raw deflate stream of every packet byte outside the regions; the region bytes in order.
    ///
    /// Segmented frames keep data Valheim already compressed out of the deflate stream: Utils.Compress output written through
    /// ZPackage.Write(byte[]), which is how TerrainComp, LiquidVolume and MapTable data travel inside ZDO packets. Unity's Mono
    /// always runs zlib at level 6 whatever CompressionLevel is requested, so deflating that data again takes real time for no gain.
    /// </summary>
    internal static class PacketFrame
    {
        public const int Magic = 0x32444746;
        public const int HeaderBytes = 13;
        public const int MaxPacketBytes = 16 * 1024 * 1024;

        // Below this a gzip array stays inside the deflate stream: its region table entry would cost more than deflate loses on it.
        public const int MinStoredRegionBytes = 64;

        // Deflate only ever runs on bytes that can compress, so once it has run any real saving is worth sending; this margin
        // just keeps the receiver from inflating a frame for a handful of bytes.
        public const int MinSavedBytes = 32;

        private const byte MethodStored = 0;
        private const byte MethodDeflate = 1;
        private const byte MethodSegmented = 2;

        private const int RegionEntryBytes = 8;

        // Deflate cannot expand data by more than 1032:1, so a longer claimed length is not a real stream.
        private const long MaxDeflateRatio = 1032;

        private const int AdlerModulus = 65521;
        private const int AdlerBlockBytes = 5552;

        private static readonly byte[] s_endProbe = new byte[1];

        public enum EncodeResult
        {
            Deflated,
            Segmented,
            NothingToCompress,
            DidNotShrink
        }

        public enum DecodeResult
        {
            Decoded,
            NotAFrame,
            BadLength,
            BadRegions,
            BadDeflate,
            BadChecksum
        }

        public readonly struct Region
        {
            public readonly int Offset;
            public readonly int Length;

            public Region(int offset, int length)
            {
                Offset = offset;
                Length = length;
            }
        }

        public static bool StartsWithMagic(byte[] data) => data != null && data.Length >= 4 && ReadInt(data, 0) == Magic;

        /// <summary>The packet unchanged inside a frame, so a packet that happens to begin with the magic is never mistaken for one.</summary>
        public static byte[] EncodeStored(byte[] packet)
        {
            var frame = new byte[HeaderBytes + packet.Length];
            WriteHeader(frame, MethodStored, packet.Length, Adler32(packet, 0, packet.Length));
            Buffer.BlockCopy(packet, 0, frame, HeaderBytes, packet.Length);
            return frame;
        }

        /// <summary>
        /// Compresses the packet, leaving gzip regions out of the deflate stream. The frame is returned only when it is at least
        /// MinSavedBytes smaller than the packet. NothingToCompress means deflate never ran because too few bytes lie outside
        /// the gzip regions to make that possible; DidNotShrink means it ran and the result was not worth sending.
        /// </summary>
        public static EncodeResult Encode(byte[] packet, List<Region> regions, out byte[] frame)
        {
            frame = null;
            FindGzipRegions(packet, regions);
            bool segmented = regions.Count > 0;

            long storedBytes = 0;
            foreach (Region region in regions) storedBytes += region.Length;
            long overheadBytes = HeaderBytes + (segmented ? 8 + (long)regions.Count * RegionEntryBytes : 0);
            if (packet.Length - storedBytes <= MinSavedBytes + overheadBytes) return EncodeResult.NothingToCompress;
            int maxFrameBytes = packet.Length - MinSavedBytes;

            var output = new MemoryStream(packet.Length);
            output.SetLength(HeaderBytes);
            output.Position = HeaderBytes;

            if (!segmented)
            {
                using (var deflate = new DeflateStream(output, CompressionMode.Compress, true))
                    deflate.Write(packet, 0, packet.Length);
            }
            else
            {
                WriteInt(output, regions.Count);
                foreach (Region region in regions)
                {
                    WriteInt(output, region.Offset);
                    WriteInt(output, region.Length);
                }

                long deflateLengthAt = output.Position;
                WriteInt(output, 0);
                long deflateStart = output.Position;

                using (var deflate = new DeflateStream(output, CompressionMode.Compress, true))
                {
                    int cursor = 0;
                    foreach (Region region in regions)
                    {
                        if (region.Offset > cursor) deflate.Write(packet, cursor, region.Offset - cursor);
                        cursor = region.Offset + region.Length;
                    }
                    if (cursor < packet.Length) deflate.Write(packet, cursor, packet.Length - cursor);
                }

                WriteInt(output.GetBuffer(), (int)deflateLengthAt, (int)(output.Position - deflateStart));
                if (output.Length + storedBytes > maxFrameBytes) return EncodeResult.DidNotShrink;

                foreach (Region region in regions)
                    output.Write(packet, region.Offset, region.Length);
            }

            if (output.Length > maxFrameBytes) return EncodeResult.DidNotShrink;

            frame = output.ToArray();
            WriteHeader(frame, segmented ? MethodSegmented : MethodDeflate, packet.Length, Adler32(packet, 0, packet.Length));
            return segmented ? EncodeResult.Segmented : EncodeResult.Deflated;
        }

        public static DecodeResult TryDecode(byte[] frame, out byte[] packet)
        {
            packet = null;
            if (frame == null || frame.Length < HeaderBytes || ReadInt(frame, 0) != Magic) return DecodeResult.NotAFrame;

            byte method = frame[4];
            int length = ReadInt(frame, 5);
            uint checksum = (uint)ReadInt(frame, 9);
            if (length <= 0 || length > MaxPacketBytes) return DecodeResult.BadLength;

            byte[] output;
            switch (method)
            {
                case MethodStored:
                    if (frame.Length - HeaderBytes != length) return DecodeResult.BadLength;
                    output = new byte[length];
                    Buffer.BlockCopy(frame, HeaderBytes, output, 0, length);
                    break;

                case MethodDeflate:
                {
                    int deflateBytes = frame.Length - HeaderBytes;
                    if (length > deflateBytes * MaxDeflateRatio) return DecodeResult.BadLength;
                    output = new byte[length];
                    if (!InflateAround(frame, HeaderBytes, deflateBytes, output, Array.Empty<Region>())) return DecodeResult.BadDeflate;
                    break;
                }

                case MethodSegmented:
                {
                    int position = HeaderBytes;
                    if (frame.Length - position < 8) return DecodeResult.BadRegions;
                    int count = ReadInt(frame, position);
                    position += 4;
                    if (count <= 0 || (long)count * RegionEntryBytes > frame.Length - position - 4) return DecodeResult.BadRegions;

                    var regions = new Region[count];
                    long regionEnd = 0;
                    long storedBytes = 0;
                    for (int i = 0; i < count; i++)
                    {
                        int offset = ReadInt(frame, position);
                        int regionLength = ReadInt(frame, position + 4);
                        position += RegionEntryBytes;
                        if (offset < regionEnd || regionLength <= 0 || (long)offset + regionLength > length) return DecodeResult.BadRegions;
                        regions[i] = new Region(offset, regionLength);
                        regionEnd = (long)offset + regionLength;
                        storedBytes += regionLength;
                    }

                    int deflateBytes = ReadInt(frame, position);
                    position += 4;
                    long plainBytes = length - storedBytes;
                    if (deflateBytes < 0 || (long)position + deflateBytes + storedBytes != frame.Length) return DecodeResult.BadRegions;
                    if (plainBytes == 0 ? deflateBytes != 0 : plainBytes > deflateBytes * MaxDeflateRatio) return DecodeResult.BadLength;

                    output = new byte[length];
                    if (plainBytes > 0 && !InflateAround(frame, position, deflateBytes, output, regions)) return DecodeResult.BadDeflate;

                    int storedAt = position + deflateBytes;
                    foreach (Region region in regions)
                    {
                        Buffer.BlockCopy(frame, storedAt, output, region.Offset, region.Length);
                        storedAt += region.Length;
                    }
                    break;
                }

                default:
                    return DecodeResult.NotAFrame;
            }

            if (Adler32(output, 0, output.Length) != checksum) return DecodeResult.BadChecksum;
            packet = output;
            return DecodeResult.Decoded;
        }

        /// <summary>
        /// Finds gzip data as ZPackage.Write(byte[]) lays out Utils.Compress output: an int32 length directly followed by a gzip
        /// member header. A coincidental match only moves bytes from the deflate stream into a stored region, never changes them.
        /// </summary>
        public static void FindGzipRegions(byte[] packet, List<Region> regions)
        {
            regions.Clear();
            int last = packet.Length - MinStoredRegionBytes;
            int i = 4;
            while (i <= last)
            {
                if (packet[i] == 0x1F && packet[i + 1] == 0x8B && packet[i + 2] == 0x08 && (packet[i + 3] & 0xE0) == 0)
                {
                    int length = ReadInt(packet, i - 4);
                    if (length >= MinStoredRegionBytes && length <= packet.Length - i)
                    {
                        regions.Add(new Region(i, length));
                        i += length;
                        continue;
                    }
                }
                i++;
            }
        }

        public static uint Adler32(byte[] data, int offset, int count)
        {
            uint a = 1, b = 0;
            while (count > 0)
            {
                int block = count < AdlerBlockBytes ? count : AdlerBlockBytes;
                count -= block;
                while (block-- > 0)
                {
                    a += data[offset++];
                    b += a;
                }
                a %= AdlerModulus;
                b %= AdlerModulus;
            }
            return (b << 16) | a;
        }

        /// <summary>Inflates exactly the bytes outside the regions into output, and fails unless the stream ends right there.</summary>
        private static bool InflateAround(byte[] frame, int offset, int count, byte[] output, Region[] regions)
        {
            try
            {
                using (var input = new MemoryStream(frame, offset, count, false))
                using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
                {
                    int cursor = 0;
                    foreach (Region region in regions)
                    {
                        if (!ReadExactly(inflate, output, cursor, region.Offset - cursor)) return false;
                        cursor = region.Offset + region.Length;
                    }
                    if (!ReadExactly(inflate, output, cursor, output.Length - cursor)) return false;
                    return inflate.Read(s_endProbe, 0, 1) == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read <= 0) return false;
                offset += read;
                count -= read;
            }
            return true;
        }

        private static void WriteHeader(byte[] frame, byte method, int length, uint checksum)
        {
            WriteInt(frame, 0, Magic);
            frame[4] = method;
            WriteInt(frame, 5, length);
            WriteInt(frame, 9, (int)checksum);
        }

        public static int ReadInt(byte[] data, int offset) =>
            data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);

        private static void WriteInt(byte[] data, int offset, int value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteInt(Stream stream, int value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 24));
        }
    }
}
