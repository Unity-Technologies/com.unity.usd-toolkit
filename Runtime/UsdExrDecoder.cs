using System;
using System.IO;
using System.IO.Compression;
using Unity.USDToolkit.Native;

namespace Unity.USDToolkit
{
    /// <summary>
    /// Decodes OpenEXR images to 8-bit RGBA on a worker thread (touches no Unity API).
    /// </summary>
    /// <remarks>
    /// Texture pipelines write their maps as EXR: ALab carries 6,834 of them and not one other
    /// image on its render materials, so without this the whole stage imported flat. Covered:
    /// scanline and tiled single-part images with NONE, RLE, ZIPS or ZIP compression and HALF,
    /// FLOAT or UINT channels -- which is what ALab (tiled 64x64, mipmapped, ZIP, HALF) and
    /// typical DCC output use. Of a mipmapped or ripmapped file only the full-resolution level is
    /// read; Unity builds its own chain. Deep and multi-part files, and PIZ/PXR24/B44/DWA
    /// compression, return false with a reason; nothing else can read them at run time either,
    /// so the texture is reported rather than guessed.
    ///
    /// The pixels are linear floating point. They are clamped to [0, 1] and quantised to 8 bits
    /// -- encoded to sRGB when the slot reads them that way, so the texture samples back to the
    /// same linear value -- and from there take the path every PNG takes: mips, block
    /// compression, upload. What is lost is precision above 8 bits and anything outside [0, 1];
    /// what is kept is the one code path and the one memory budget.
    ///
    /// Every size and offset read from the file is checked against the buffer and against
    /// UsdImageLimits before it drives an allocation, for the same reason UsdPngDecoder does.
    /// </remarks>
    internal static class UsdExrDecoder
    {
        private const int CompressionNone = 0;
        private const int CompressionRle = 1;
        private const int CompressionZips = 2;
        private const int CompressionZip = 3;

        // Same limits as the native decoder (UsdImageCodecs.cpp). RGBA and a few auxiliary layers
        // is all a texture carries; thousands of channels only make every buffer enormous.
        private const int MaxChannels = 64;

        // The most bytes `compressed` bytes can expand to: deflate about 1032:1, an RLE run two
        // bytes for at most 128. A chunk, or an image, claiming more is refused before anything
        // is allocated for it -- a 200-byte file declaring a 16384x16384 tile had gigabytes
        // allocated before any data was read.
        private static long MaxExpansion(int compression, long compressed)
        {
            switch (compression)
            {
                case CompressionNone: return compressed;
                case CompressionRle: return compressed * 64 + 128;
                default: return compressed * 1032 + 1032;
            }
        }

        // Refuses a chunk whose bytes cannot expand to the pixels it claims, then grows the two
        // working buffers to hold it.
        private static bool ChunkFits(int compression, int dataSize, int expected, ref byte[] chunk,
                                      ref byte[] scratch, out string reason)
        {
            reason = null;
            if (expected < 0 || (dataSize != expected && expected > MaxExpansion(compression, dataSize)))
            {
                reason = "OpenEXR chunk claims more pixels than its data can hold";
                return false;
            }

            if (chunk.Length < expected)
            {
                chunk = new byte[expected];
                scratch = new byte[expected];
            }

            return true;
        }

        private const int PixelUint = 0;
        private const int PixelHalf = 1;
        private const int PixelFloat = 2;

        private const int LevelsOne = 0;
        private const int LevelsMipmap = 1;
        private const int LevelsRipmap = 2;

        // Half bits -> byte, once per process for each colour space. 64K entries each.
        private static readonly byte[] HalfToLinearByte = BuildHalfTable(false);
        private static readonly byte[] HalfToSrgbByte = BuildHalfTable(true);

        private struct Channel
        {
            public string Name;
            public int PixelType;
            public int BytesPerSample;
            public int Target; // 0 R, 1 G, 2 B, 3 A, -1 unused
        }

        private sealed class Header
        {
            public Channel[] Channels;
            public int Compression = -1;
            public int XMin, YMin, XMax = -1, YMax = -1;
            public bool HaveDataWindow;
            public bool Tiled;
            public int TileWidth, TileHeight, LevelMode, RoundingMode;
            // In 64-bit: XMax - XMin + 1 overflows int for a window like [int.MaxValue, int.MinValue + 98]
            // and wrapped to a plausible width. Width and Height are only read after the check.
            public long Width64 => (long)XMax - XMin + 1;
            public long Height64 => (long)YMax - YMin + 1;
            public int Width => (int)Width64;
            public int Height => (int)Height64;
        }

        public static bool IsExr(byte[] data)
        {
            return data != null && data.Length >= 8 &&
                   data[0] == 0x76 && data[1] == 0x2f && data[2] == 0x31 && data[3] == 0x01;
        }

        /// <summary>
        /// On success, rgba is width*height*4 bytes in Unity layout (row 0 = bottom), the same
        /// orientation UsdPngDecoder produces.
        /// </summary>
        public static bool TryDecode(byte[] data, bool srgb, out int width, out int height, out byte[] rgba,
                                     out string reason)
        {
            width = 0;
            height = 0;
            rgba = null;
            reason = null;

            if (!IsExr(data))
            {
                reason = "not an OpenEXR file";
                return false;
            }

            // The native reader is the same decoder in C++ with the platform's zlib; on ALab it
            // is the difference between a 70-second import and one Isaac Sim's size. It hands
            // back here only when its zlib could not be opened.
            if (UsdNative.SupportsImageCodecs)
            {
                if (UsdNative.TryDecodeExr(data, srgb, out width, out height, out rgba, out reason, out bool fallback))
                {
                    return true;
                }

                if (!fallback)
                {
                    width = 0;
                    height = 0;
                    rgba = null;
                    return false;
                }
            }

            // The version word: the low byte is the format version, the bits above it are flags.
            int version = data[4];
            int flags = data[5];
            if (version != 2)
            {
                reason = $"OpenEXR version {version} is not supported";
                return false;
            }

            bool tiledFlag = (flags & 0x02) != 0;
            bool longNames = (flags & 0x04) != 0;
            bool deep = (flags & 0x08) != 0;
            bool multiPart = (flags & 0x10) != 0;
            if (deep || multiPart)
            {
                reason = deep ? "deep OpenEXR images are not supported" : "multi-part OpenEXR files are not supported";
                return false;
            }

            int pos = 8;
            if (!ReadHeader(data, ref pos, longNames ? 255 : 31, out Header header, out reason))
            {
                return false;
            }

            if (tiledFlag != header.Tiled)
            {
                reason = "OpenEXR tiled flag and tiles attribute disagree";
                return false;
            }

            long w = header.Width64;
            long h = header.Height64;
            if (w < 1 || h < 1 || w > UsdImageLimits.MaxDimension || h > UsdImageLimits.MaxDimension ||
                !UsdImageLimits.IsWithin((int)w, (int)h))
            {
                reason = $"OpenEXR dataWindow claims {w}x{h}, outside the limits of 1-{UsdImageLimits.MaxDimension}px a side and {UsdImageLimits.MaxPixelCount} pixels";
                return false;
            }

            switch (header.Compression)
            {
                case CompressionNone:
                case CompressionRle:
                case CompressionZips:
                case CompressionZip:
                    break;
                default:
                    reason = $"OpenEXR compression {CompressionName(header.Compression)} is not supported (NONE, RLE, ZIPS and ZIP are)";
                    return false;
            }

            if (header.Channels.Length > MaxChannels)
            {
                reason = $"OpenEXR has {header.Channels.Length} channels, over the limit of {MaxChannels}";
                return false;
            }

            // The image has to fit in what is left of the file once expanded, or nothing is
            // allocated for it (see MaxExpansion).
            long pixelBytes = BytesPerRow(header.Channels, 1);
            long remaining = data.Length - (long)pos;
            if (pixelBytes * w * h > MaxExpansion(header.Compression, remaining))
            {
                reason = $"OpenEXR claims {w}x{h} pixels, more than its {remaining} bytes of data can hold";
                return false;
            }

            width = (int)w;
            height = (int)h;
            rgba = new byte[(long)width * height * 4];

            // Channels the output does not take are skipped, so an alpha-less image still fills
            // alpha with 255.
            bool hasAlpha = false;
            foreach (Channel channel in header.Channels)
            {
                hasAlpha |= channel.Target == 3;
            }

            if (!hasAlpha)
            {
                for (long i = 3; i < rgba.LongLength; i += 4)
                {
                    rgba[i] = 255;
                }
            }

            bool ok = header.Tiled
                ? DecodeTiles(data, pos, header, srgb, rgba, out reason)
                : DecodeScanlines(data, pos, header, srgb, rgba, out reason);
            if (!ok)
            {
                rgba = null;
                return false;
            }

            // A greyscale image (one colour channel) is replicated so that any slot can read it.
            if (IsSingleColourChannel(header.Channels))
            {
                for (long i = 0; i < rgba.LongLength; i += 4)
                {
                    rgba[i + 1] = rgba[i];
                    rgba[i + 2] = rgba[i];
                }
            }

            return true;
        }

        // ---- header ----

        private static bool ReadHeader(byte[] data, ref int pos, int maxName, out Header header, out string reason)
        {
            header = new Header();
            reason = null;
            while (true)
            {
                if (!ReadString(data, ref pos, maxName + 1, out string name))
                {
                    reason = "malformed OpenEXR header";
                    return false;
                }

                if (name.Length == 0)
                {
                    break; // end of header
                }

                if (!ReadString(data, ref pos, maxName + 1, out string _) || pos + 4 > data.Length)
                {
                    reason = "malformed OpenEXR header";
                    return false;
                }

                int size = ReadInt32(data, pos);
                pos += 4;
                if (size < 0 || (long)pos + size > data.Length)
                {
                    reason = "malformed OpenEXR header";
                    return false;
                }

                switch (name)
                {
                    case "channels":
                        if (!ReadChannels(data, pos, size, maxName, out header.Channels))
                        {
                            reason = "malformed OpenEXR channel list";
                            return false;
                        }

                        break;
                    case "compression":
                        header.Compression = size >= 1 ? data[pos] : -1;
                        break;
                    case "dataWindow":
                        if (size != 16)
                        {
                            reason = "malformed OpenEXR dataWindow";
                            return false;
                        }

                        header.XMin = ReadInt32(data, pos);
                        header.YMin = ReadInt32(data, pos + 4);
                        header.XMax = ReadInt32(data, pos + 8);
                        header.YMax = ReadInt32(data, pos + 12);
                        header.HaveDataWindow = true;
                        break;
                    case "tiles":
                        if (size != 9)
                        {
                            reason = "malformed OpenEXR tiles attribute";
                            return false;
                        }

                        header.Tiled = true;
                        header.TileWidth = ReadInt32(data, pos);
                        header.TileHeight = ReadInt32(data, pos + 4);
                        header.LevelMode = data[pos + 8] & 0x0F;
                        header.RoundingMode = data[pos + 8] >> 4;
                        break;
                }

                pos += size;
            }

            if (header.Channels == null || header.Channels.Length == 0 || !header.HaveDataWindow ||
                header.Compression < 0)
            {
                reason = "OpenEXR header lacks channels, dataWindow or compression";
                return false;
            }

            if (header.Tiled && (header.TileWidth < 1 || header.TileHeight < 1 ||
                                 header.TileWidth > UsdImageLimits.MaxDimension ||
                                 header.TileHeight > UsdImageLimits.MaxDimension ||
                                 header.LevelMode < LevelsOne || header.LevelMode > LevelsRipmap))
            {
                reason = "malformed OpenEXR tiles attribute";
                return false;
            }

            return true;
        }

        // The channel list: name, pixel type, pLinear, reserved, xSampling, ySampling, repeated,
        // then an empty name. Channels are named "R" or "layer.R"; the part after the last dot
        // decides the output channel. "Y" (luminance) is read as grey.
        private static bool ReadChannels(byte[] data, int pos, int size, int maxName, out Channel[] channels)
        {
            channels = null;
            int end = pos + size;
            var list = new System.Collections.Generic.List<Channel>();
            while (pos < end)
            {
                if (!ReadString(data, ref pos, maxName + 1, out string name))
                {
                    return false;
                }

                if (name.Length == 0)
                {
                    break;
                }

                if (pos + 16 > end)
                {
                    return false;
                }

                int pixelType = ReadInt32(data, pos);
                int xSampling = ReadInt32(data, pos + 8);
                int ySampling = ReadInt32(data, pos + 12);
                pos += 16;
                if (pixelType < PixelUint || pixelType > PixelFloat || xSampling != 1 || ySampling != 1)
                {
                    return false; // subsampled channels are a chroma layout no texture uses
                }

                string leaf = name;
                int dot = name.LastIndexOf('.');
                if (dot >= 0 && dot + 1 < name.Length)
                {
                    leaf = name.Substring(dot + 1);
                }

                int target;
                switch (leaf.ToUpperInvariant())
                {
                    case "R": target = 0; break;
                    case "G": target = 1; break;
                    case "B": target = 2; break;
                    case "A": target = 3; break;
                    case "Y": target = 0; break;
                    default: target = -1; break;
                }

                list.Add(new Channel
                {
                    Name = name,
                    PixelType = pixelType,
                    BytesPerSample = pixelType == PixelHalf ? 2 : 4,
                    Target = target,
                });
            }

            if (list.Count == 0)
            {
                return false;
            }

            // A file with a single channel of some other name (e.g. "Z", or "roughness") is a
            // greyscale map; read it into red and let the caller replicate it.
            bool anyMapped = false;
            foreach (Channel channel in list)
            {
                anyMapped |= channel.Target >= 0;
            }

            if (!anyMapped && list.Count == 1)
            {
                Channel only = list[0];
                only.Target = 0;
                list[0] = only;
            }

            channels = list.ToArray();
            return true;
        }

        private static bool IsSingleColourChannel(Channel[] channels)
        {
            int colour = 0;
            foreach (Channel channel in channels)
            {
                if (channel.Target >= 0 && channel.Target < 3)
                {
                    colour++;
                }
            }

            return colour == 1;
        }

        // ---- scanline images ----

        private static bool DecodeScanlines(byte[] data, int pos, Header header, bool srgb, byte[] rgba,
                                            out string reason)
        {
            reason = null;
            int width = header.Width, height = header.Height;
            int linesPerChunk = header.Compression == CompressionZip ? 16 : 1;
            long bytesPerLine = BytesPerRow(header.Channels, width);
            if (bytesPerLine <= 0 || bytesPerLine * Math.Min(linesPerChunk, height) > int.MaxValue)
            {
                reason = "OpenEXR scanline too large";
                return false;
            }

            int chunkCount = (height + linesPerChunk - 1) / linesPerChunk;
            if (!ReadOffsets(data, pos, chunkCount, out long[] offsets))
            {
                reason = "OpenEXR offset table runs past the end of the file";
                return false;
            }

            // Grown to the largest chunk that passes ChunkFits, not sized from the header up front.
            var chunk = Array.Empty<byte>();
            var scratch = Array.Empty<byte>();
            for (int c = 0; c < chunkCount; c++)
            {
                int at = (int)offsets[c];
                int y = ReadInt32(data, at);
                int dataSize = ReadInt32(data, at + 4);
                at += 8;
                if (y < header.YMin || y > header.YMax || dataSize < 0 || (long)at + dataSize > data.Length)
                {
                    reason = "malformed OpenEXR chunk";
                    return false;
                }

                int lines = Math.Min(linesPerChunk, header.YMax - y + 1);
                int expected = (int)(bytesPerLine * lines);
                if (!ChunkFits(header.Compression, dataSize, expected, ref chunk, ref scratch, out reason))
                {
                    return false;
                }

                if (!Unpack(data, at, dataSize, expected, header.Compression, chunk, scratch, out reason))
                {
                    return false;
                }

                UnpackRows(chunk, header, srgb, rgba, 0, y - header.YMin, width, lines);
            }

            return true;
        }

        // ---- tiled images ----

        // Only level (0, 0) is read: a mipmapped file's other levels are smaller copies Unity
        // regenerates. Its tiles are the first entries of the offset table in every level mode.
        private static bool DecodeTiles(byte[] data, int pos, Header header, bool srgb, byte[] rgba,
                                        out string reason)
        {
            reason = null;
            int width = header.Width, height = header.Height;
            int tilesX = (width + header.TileWidth - 1) / header.TileWidth;
            int tilesY = (height + header.TileHeight - 1) / header.TileHeight;
            long tileCount = (long)tilesX * tilesY;
            if (tileCount > int.MaxValue)
            {
                reason = "OpenEXR tile count too large";
                return false;
            }

            if (!ReadOffsets(data, pos, (int)tileCount, out long[] offsets))
            {
                reason = "OpenEXR offset table runs past the end of the file";
                return false;
            }

            // A tile never holds more than the image: a 1x1 image may declare 16384x16384 tiles.
            long bytesPerTile = BytesPerRow(header.Channels, Math.Min(header.TileWidth, width)) *
                                Math.Min(header.TileHeight, height);
            if (bytesPerTile <= 0 || bytesPerTile > int.MaxValue)
            {
                reason = "OpenEXR tile too large";
                return false;
            }

            var chunk = Array.Empty<byte>();
            var scratch = Array.Empty<byte>();
            for (int c = 0; c < tileCount; c++)
            {
                int at = (int)offsets[c];
                if ((long)at + 20 > data.Length)
                {
                    reason = "malformed OpenEXR tile";
                    return false;
                }

                int dx = ReadInt32(data, at);
                int dy = ReadInt32(data, at + 4);
                int lx = ReadInt32(data, at + 8);
                int ly = ReadInt32(data, at + 12);
                int dataSize = ReadInt32(data, at + 16);
                at += 20;
                if (lx != 0 || ly != 0)
                {
                    continue; // a coarser level, which this does not need
                }

                if (dx < 0 || dy < 0 || dx >= tilesX || dy >= tilesY || dataSize < 0 ||
                    (long)at + dataSize > data.Length)
                {
                    reason = "malformed OpenEXR tile";
                    return false;
                }

                int x0 = dx * header.TileWidth;
                int y0 = dy * header.TileHeight;
                int tileW = Math.Min(header.TileWidth, width - x0);
                int tileH = Math.Min(header.TileHeight, height - y0);
                int expected = (int)(BytesPerRow(header.Channels, tileW) * tileH);
                if (!ChunkFits(header.Compression, dataSize, expected, ref chunk, ref scratch, out reason))
                {
                    return false;
                }

                if (!Unpack(data, at, dataSize, expected, header.Compression, chunk, scratch, out reason))
                {
                    return false;
                }

                UnpackRows(chunk, header, srgb, rgba, x0, y0, tileW, tileH);
            }

            return true;
        }

        // ---- shared ----

        private static long BytesPerRow(Channel[] channels, int samples)
        {
            long bytes = 0;
            foreach (Channel channel in channels)
            {
                bytes += (long)channel.BytesPerSample * samples;
            }

            return bytes;
        }

        private static bool ReadOffsets(byte[] data, int pos, int count, out long[] offsets)
        {
            offsets = null;
            if (pos + (long)count * 8 > data.Length)
            {
                return false;
            }

            offsets = new long[count];
            for (int i = 0; i < count; i++)
            {
                offsets[i] = ReadInt64(data, pos + i * 8);
                if (offsets[i] < 0 || offsets[i] + 8 > data.Length)
                {
                    return false;
                }
            }

            return true;
        }

        // Produces the `expected` uncompressed bytes of one chunk into `chunk`.
        private static bool Unpack(byte[] data, int at, int dataSize, int expected, int compression,
                                   byte[] chunk, byte[] scratch, out string reason)
        {
            reason = null;
            if (compression == CompressionNone || dataSize == expected)
            {
                // Stored raw: either the file is uncompressed or the codec did not help.
                if (dataSize != expected)
                {
                    reason = "OpenEXR chunk size does not match its pixels";
                    return false;
                }

                Buffer.BlockCopy(data, at, chunk, 0, expected);
                return true;
            }

            int produced = compression == CompressionRle
                ? DecodeRle(data, at, dataSize, scratch, expected)
                : Inflate(data, at, dataSize, scratch, expected);
            if (produced != expected)
            {
                reason = "OpenEXR chunk did not decompress to its pixels";
                return false;
            }

            // Both RLE and ZIP store the bytes predicted and interleaved; undo both.
            Reconstruct(scratch, expected, chunk);
            return true;
        }

        // Writes `rows` rows of `rowWidth` pixels, laid out channel-by-channel per row, into the
        // output at (x0, y0). Row 0 of the output is the bottom of the image.
        private static void UnpackRows(byte[] pixels, Header header, bool srgb, byte[] rgba,
                                       int x0, int y0, int rowWidth, int rows)
        {
            byte[] halfTable = srgb ? HalfToSrgbByte : HalfToLinearByte;
            int width = header.Width, height = header.Height;
            int lineAt = 0;
            for (int row = 0; row < rows; row++)
            {
                long rowBase = ((long)(height - 1 - (y0 + row)) * width + x0) * 4;
                foreach (Channel channel in header.Channels)
                {
                    if (channel.Target < 0)
                    {
                        lineAt += channel.BytesPerSample * rowWidth;
                        continue;
                    }

                    long outAt = rowBase + channel.Target;
                    switch (channel.PixelType)
                    {
                        case PixelHalf:
                            for (int x = 0; x < rowWidth; x++, outAt += 4, lineAt += 2)
                            {
                                rgba[outAt] = halfTable[pixels[lineAt] | (pixels[lineAt + 1] << 8)];
                            }

                            break;
                        case PixelFloat:
                            for (int x = 0; x < rowWidth; x++, outAt += 4, lineAt += 4)
                            {
                                rgba[outAt] = ToByte(BitConverter.ToSingle(pixels, lineAt), srgb);
                            }

                            break;
                        default: // UINT: integer data (ids, masks); scaled by its full range
                            for (int x = 0; x < rowWidth; x++, outAt += 4, lineAt += 4)
                            {
                                rgba[outAt] = (byte)(BitConverter.ToUInt32(pixels, lineAt) >> 24);
                            }

                            break;
                    }
                }
            }
        }

        // RLE: a signed count byte; negative means that many literal bytes follow, otherwise the
        // next byte repeats count + 1 times.
        private static int DecodeRle(byte[] data, int at, int size, byte[] output, int expected)
        {
            int end = at + size;
            int written = 0;
            while (at < end)
            {
                int count = (sbyte)data[at++];
                if (count < 0)
                {
                    int literal = -count;
                    if (at + literal > end || written + literal > expected)
                    {
                        return -1;
                    }

                    Buffer.BlockCopy(data, at, output, written, literal);
                    at += literal;
                    written += literal;
                }
                else
                {
                    int run = count + 1;
                    if (at >= end || written + run > expected)
                    {
                        return -1;
                    }

                    byte value = data[at++];
                    for (int i = 0; i < run; i++)
                    {
                        output[written++] = value;
                    }
                }
            }

            return written;
        }

        // zlib stream: a two-byte header, a raw deflate body, a four-byte Adler-32 trailer.
        private static int Inflate(byte[] data, int at, int size, byte[] output, int expected)
        {
            if (size < 6)
            {
                return -1;
            }

            using (var source = new MemoryStream(data, at + 2, size - 2, false))
            using (var inflater = new DeflateStream(source, CompressionMode.Decompress))
            {
                int total = 0;
                while (total < expected)
                {
                    int read = inflater.Read(output, total, expected - total);
                    if (read <= 0)
                    {
                        break;
                    }

                    total += read;
                }

                // Anything left means the chunk was larger than its pixels, which is malformed.
                if (total == expected && inflater.ReadByte() >= 0)
                {
                    return -1;
                }

                return total;
            }
        }

        // The predictor and interleave both ZIP and RLE apply before compressing: each byte was
        // stored as the difference from the previous (plus 128), and the result was split into
        // its even and odd bytes laid end to end.
        private static void Reconstruct(byte[] source, int count, byte[] output)
        {
            for (int i = 1; i < count; i++)
            {
                source[i] = (byte)(source[i - 1] + source[i] - 128);
            }

            int half = (count + 1) / 2;
            int first = 0, second = half, at = 0;
            while (at < count)
            {
                output[at++] = source[first++];
                if (at < count)
                {
                    output[at++] = source[second++];
                }
            }
        }

        private static bool ReadString(byte[] data, ref int pos, int maxLength, out string value)
        {
            value = null;
            int start = pos;
            while (pos < data.Length && data[pos] != 0)
            {
                if (pos - start > maxLength)
                {
                    return false;
                }

                pos++;
            }

            if (pos >= data.Length)
            {
                return false;
            }

            value = System.Text.Encoding.ASCII.GetString(data, start, pos - start);
            pos++; // the terminator
            return true;
        }

        private static int ReadInt32(byte[] d, int o)
        {
            return d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24);
        }

        private static long ReadInt64(byte[] d, int o)
        {
            return (long)(uint)ReadInt32(d, o) | ((long)ReadInt32(d, o + 4) << 32);
        }

        private static string CompressionName(int compression)
        {
            switch (compression)
            {
                case 4: return "PIZ";
                case 5: return "PXR24";
                case 6: return "B44";
                case 7: return "B44A";
                case 8: return "DWAA";
                case 9: return "DWAB";
                default: return compression.ToString();
            }
        }

        private static byte ToByte(float value, bool srgb)
        {
            if (float.IsNaN(value) || value <= 0f)
            {
                return 0;
            }

            if (value >= 1f)
            {
                return 255;
            }

            if (srgb)
            {
                value = value <= 0.0031308f
                    ? value * 12.92f
                    : 1.055f * (float)Math.Pow(value, 1.0 / 2.4) - 0.055f;
            }

            return (byte)Math.Round(value * 255f);
        }

        private static byte[] BuildHalfTable(bool srgb)
        {
            var table = new byte[65536];
            for (int bits = 0; bits < 65536; bits++)
            {
                table[bits] = ToByte(HalfToFloat((ushort)bits), srgb);
            }

            return table;
        }

        private static float HalfToFloat(ushort half)
        {
            int sign = (half >> 15) & 1;
            int exponent = (half >> 10) & 0x1F;
            int mantissa = half & 0x3FF;
            float value;
            if (exponent == 0)
            {
                value = mantissa * (1f / 1024f) * (1f / 16384f); // subnormal: 2^-14 * m/1024
            }
            else if (exponent == 31)
            {
                value = mantissa == 0 ? float.PositiveInfinity : float.NaN;
            }
            else
            {
                value = (1f + mantissa * (1f / 1024f)) * (float)Math.Pow(2, exponent - 15);
            }

            return sign != 0 ? -value : value;
        }
    }
}
