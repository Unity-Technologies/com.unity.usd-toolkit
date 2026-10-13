using System;

namespace Unity.USDToolkit
{
    /// <summary>
    /// Compresses decoded RGBA into BC1 or BC3 blocks, with a mip chain, on whatever thread calls
    /// it.
    /// </summary>
    /// <remarks>
    /// <see cref="UnityEngine.Texture2D.Compress"/> does this too, and better, but only on the main
    /// thread -- which on a stage with 150 textures was 22 seconds of a 38-second import, more than
    /// the USD composition and all the geometry together. Compressing here instead puts the work
    /// beside the decoding, which already runs on worker threads and is already where the pixels
    /// are.
    ///
    /// The encoder fits each 4x4 block to the range its pixels occupy: two endpoints at the extremes
    /// and two interpolated between them for colour, eight levels for alpha. It is the standard
    /// range fit, which is what a fast encoder does and roughly what Texture2D.Compress produces at
    /// its low-quality setting. A slower search would place the endpoints better; for albedo,
    /// normal and roughness maps the difference is not where the import's problems are.
    ///
    /// Opaque images get BC1 rather than BC3, which Texture2D.Compress does not choose on their
    /// behalf -- it produced DXT5 for all 116 compressible textures of that stage. BC1 is half
    /// again, so this is smaller as well as faster.
    ///
    /// Mip levels are box filters of the level above, generated before compression so that each is
    /// compressed from its own pixels rather than from compressed ones.
    /// </remarks>
    internal static class UsdBlockCompressor
    {
        /// Bytes per 4x4 block.
        private const int Bc1BlockBytes = 8;
        private const int Bc3BlockBytes = 16;

        /// <summary>
        /// Encodes <paramref name="rgba"/> and every mip level below it.
        /// </summary>
        /// <param name="alpha">
        /// True to encode BC3, which carries alpha; false for BC1, which does not. The caller
        /// decides, because whether an image's alpha means anything is a property of how the
        /// material uses it, not of the pixels.
        /// </param>
        /// <param name="mipCount">How many levels the returned buffer holds.</param>
        /// <returns>
        /// The levels back to back, largest first, as Unity's LoadRawTextureData expects; or null
        /// if the size cannot be block-compressed.
        /// </returns>
        public static byte[] Encode(byte[] rgba, int width, int height, bool alpha, bool srgb,
                                    out int mipCount)
        {
            mipCount = 0;
            if (rgba == null || width < 4 || height < 4 || (width & 3) != 0 || (height & 3) != 0)
            {
                return null;
            }

            // The same encoder in native code (API 11): same blocks, same mip averaging, a
            // fraction of the time. The managed one below is what an older payload gets.
            if (Native.UsdNative.SupportsImageCodecs)
            {
                byte[] native = Native.UsdNative.BlockCompress(rgba, width, height, alpha, srgb, out mipCount);
                if (native != null)
                {
                    return native;
                }

                mipCount = 0;
            }

            int blockBytes = alpha ? Bc3BlockBytes : Bc1BlockBytes;

            // The chain runs to 1x1, which is what Unity expects of a complete one. Levels below
            // four pixels a side are still a whole block each -- a block is the unit the format
            // stores, whatever fraction of it carries image -- so the count is a ceiling, not a
            // division. Getting this wrong by one block is not a quality problem but a size
            // mismatch that LoadRawTextureData rejects outright.
            int levels = 1;
            for (int w = width, h = height; w > 1 || h > 1; w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
            {
                levels++;
            }

            int total = 0;
            for (int level = 0, w = width, h = height; level < levels;
                 level++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
            {
                total += Blocks(w) * Blocks(h) * blockBytes;
            }

            var output = new byte[total];
            byte[] source = rgba;
            int sourceWidth = width;
            int sourceHeight = height;
            int cursor = 0;

            for (int level = 0; level < levels; level++)
            {
                if (level > 0)
                {
                    source = Downsample(source, sourceWidth, sourceHeight, srgb);
                    sourceWidth = Math.Max(1, sourceWidth >> 1);
                    sourceHeight = Math.Max(1, sourceHeight >> 1);
                }

                cursor += EncodeLevel(source, sourceWidth, sourceHeight, alpha, output, cursor);
            }

            mipCount = levels;
            return output;
        }

        // How many 4x4 blocks a dimension needs: a 3-pixel side still occupies one.
        private static int Blocks(int size)
        {
            return Math.Max(1, (size + 3) >> 2);
        }

        /// <summary>
        /// True when every pixel is fully opaque, so the image can take BC1 and half the memory.
        /// </summary>
        public static bool IsOpaque(byte[] rgba)
        {
            if (rgba == null)
            {
                return true;
            }

            for (int i = 3; i < rgba.Length; i += 4)
            {
                if (rgba[i] != 255)
                {
                    return false;
                }
            }

            return true;
        }

        private static int EncodeLevel(byte[] rgba, int width, int height, bool alpha,
                                       byte[] output, int offset)
        {
            int blocksX = Blocks(width);
            int blocksY = Blocks(height);
            int blockBytes = alpha ? Bc3BlockBytes : Bc1BlockBytes;
            var block = new byte[64];

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    // Gather the 4x4 texels, clamping at the edges. A block that hangs off the
                    // image -- which every level below four pixels does -- repeats its last row
                    // and column rather than reading past the buffer or encoding black.
                    for (int row = 0; row < 4; row++)
                    {
                        int y = Math.Min((by << 2) + row, height - 1);
                        for (int column = 0; column < 4; column++)
                        {
                            int x = Math.Min((bx << 2) + column, width - 1);
                            int from = ((y * width) + x) << 2;
                            int to = (row << 4) + (column << 2);
                            block[to] = rgba[from];
                            block[to + 1] = rgba[from + 1];
                            block[to + 2] = rgba[from + 2];
                            block[to + 3] = rgba[from + 3];
                        }
                    }

                    int at = offset + ((by * blocksX) + bx) * blockBytes;
                    if (alpha)
                    {
                        EncodeAlphaBlock(block, output, at);
                        EncodeColourBlock(block, output, at + 8);
                    }
                    else
                    {
                        EncodeColourBlock(block, output, at);
                    }
                }
            }

            return blocksX * blocksY * blockBytes;
        }

        // BC1's colour half: two RGB565 endpoints and a two-bit index per texel into the four
        // colours they imply. The endpoints are the corners of the block's bounding box in RGB,
        // which is where a range fit puts them.
        private static void EncodeColourBlock(byte[] block, byte[] output, int at)
        {
            int minR = 255, minG = 255, minB = 255;
            int maxR = 0, maxG = 0, maxB = 0;
            for (int i = 0; i < 64; i += 4)
            {
                int r = block[i], g = block[i + 1], b = block[i + 2];
                if (r < minR) { minR = r; }
                if (g < minG) { minG = g; }
                if (b < minB) { minB = b; }
                if (r > maxR) { maxR = r; }
                if (g > maxG) { maxG = g; }
                if (b > maxB) { maxB = b; }
            }

            // Pull the box in slightly. The extremes are single texels and placing the endpoints
            // exactly on them spends both of the palette's fixed colours on outliers; an inset of
            // a sixteenth of the range is the usual compromise.
            int insetR = (maxR - minR) >> 4;
            int insetG = (maxG - minG) >> 4;
            int insetB = (maxB - minB) >> 4;
            minR = Math.Min(minR + insetR, 255);
            minG = Math.Min(minG + insetG, 255);
            minB = Math.Min(minB + insetB, 255);
            maxR = Math.Max(maxR - insetR, 0);
            maxG = Math.Max(maxG - insetG, 0);
            maxB = Math.Max(maxB - insetB, 0);

            int high = Pack565(maxR, maxG, maxB);
            int low = Pack565(minR, minG, minB);

            // BC1 reads the four-colour palette when the first endpoint is the larger. Equal
            // endpoints mean a flat block, where the order does not matter but the rule still
            // must be satisfied.
            if (high < low)
            {
                int swap = high;
                high = low;
                low = swap;
            }

            Unpack565(high, out int hr, out int hg, out int hb);
            Unpack565(low, out int lr, out int lg, out int lb);

            // Palette: the two endpoints and two thirds between them.
            Span4 paletteR = new Span4(hr, lr);
            Span4 paletteG = new Span4(hg, lg);
            Span4 paletteB = new Span4(hb, lb);

            uint indices = 0;
            for (int i = 0, texel = 0; i < 64; i += 4, texel++)
            {
                int r = block[i], g = block[i + 1], b = block[i + 2];
                int best = 0;
                int bestDistance = int.MaxValue;
                for (int c = 0; c < 4; c++)
                {
                    int dr = r - paletteR[c];
                    int dg = g - paletteG[c];
                    int db = b - paletteB[c];
                    int distance = (dr * dr) + (dg * dg) + (db * db);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = c;
                    }
                }

                indices |= (uint)best << (texel << 1);
            }

            output[at] = (byte)(high & 0xFF);
            output[at + 1] = (byte)(high >> 8);
            output[at + 2] = (byte)(low & 0xFF);
            output[at + 3] = (byte)(low >> 8);
            output[at + 4] = (byte)(indices & 0xFF);
            output[at + 5] = (byte)((indices >> 8) & 0xFF);
            output[at + 6] = (byte)((indices >> 16) & 0xFF);
            output[at + 7] = (byte)((indices >> 24) & 0xFF);
        }

        // BC3's alpha half: two endpoints and a three-bit index per texel into the eight values
        // they imply.
        private static void EncodeAlphaBlock(byte[] block, byte[] output, int at)
        {
            int min = 255, max = 0;
            for (int i = 3; i < 64; i += 4)
            {
                int a = block[i];
                if (a < min) { min = a; }
                if (a > max) { max = a; }
            }

            output[at] = (byte)max;
            output[at + 1] = (byte)min;

            // A block of one alpha value, which an image with any opaque region has a great many
            // of. There is no range to divide by and nothing to interpolate: index 0 selects the
            // first endpoint, so leaving every index at zero reproduces the block exactly.
            if (max == min)
            {
                for (int byteIndex = 0; byteIndex < 6; byteIndex++)
                {
                    output[at + 2 + byteIndex] = 0;
                }

                return;
            }

            ulong indices = 0;
            int range = max - min;
            for (int i = 3, texel = 0; i < 64; i += 4, texel++)
            {
                // Position within the range, as one of eight steps: 0 is max, 1 is min, and 2..7
                // divide the span between them.
                int step = ((block[i] - min) * 7 + (range >> 1)) / range;
                int index = step == 7 ? 0 : (step == 0 ? 1 : 8 - step);
                indices |= (ulong)(uint)index << (texel * 3);
            }

            for (int byteIndex = 0; byteIndex < 6; byteIndex++)
            {
                output[at + 2 + byteIndex] = (byte)((indices >> (byteIndex * 8)) & 0xFF);
            }
        }

        // The four colours BC1 implies for one channel's pair of endpoints.
        private readonly struct Span4
        {
            private readonly int high;
            private readonly int low;

            public Span4(int high, int low)
            {
                this.high = high;
                this.low = low;
            }

            public int this[int index]
            {
                get
                {
                    switch (index)
                    {
                        case 0: return high;
                        case 1: return low;
                        case 2: return ((2 * high) + low) / 3;
                        default: return (high + (2 * low)) / 3;
                    }
                }
            }
        }

        // Halves both dimensions by averaging each 2x2 group, clamping where a dimension is odd
        // or already one. Mip chains reach odd sizes as soon as an image is not a power of two,
        // and 4:3 textures reach them almost immediately.
        private static byte[] Downsample(byte[] rgba, int width, int height, bool srgb)
        {
            int halfWidth = Math.Max(1, width >> 1);
            int halfHeight = Math.Max(1, height >> 1);
            var output = new byte[halfWidth * halfHeight * 4];
            for (int y = 0; y < halfHeight; y++)
            {
                int y0 = Math.Min(y << 1, height - 1);
                int y1 = Math.Min((y << 1) + 1, height - 1);
                for (int x = 0; x < halfWidth; x++)
                {
                    int x0 = Math.Min(x << 1, width - 1);
                    int x1 = Math.Min((x << 1) + 1, width - 1);
                    int a = ((y0 * width) + x0) << 2;
                    int b = ((y0 * width) + x1) << 2;
                    int c = ((y1 * width) + x0) << 2;
                    int d = ((y1 * width) + x1) << 2;
                    int to = ((y * halfWidth) + x) << 2;

                    // Colour is averaged in linear space when the image is sRGB. Averaging the
                    // encoded values instead overestimates -- the encoding is concave, so the
                    // mean of four encoded samples sits above the encoding of their mean -- and
                    // the result is a mip chain that grows brighter the further it is from the
                    // top. It is visible as a washed-out surface at any distance.
                    for (int channel = 0; channel < 3; channel++)
                    {
                        output[to + channel] = srgb
                            ? LinearToSrgb(
                                (SrgbToLinear[rgba[a + channel]] + SrgbToLinear[rgba[b + channel]] +
                                 SrgbToLinear[rgba[c + channel]] + SrgbToLinear[rgba[d + channel]]) * 0.25f)
                            : (byte)((rgba[a + channel] + rgba[b + channel] +
                                      rgba[c + channel] + rgba[d + channel] + 2) >> 2);
                    }

                    // Alpha is coverage, not colour, and is linear whatever the colour channels are.
                    output[to + 3] = (byte)((
                        rgba[a + 3] + rgba[b + 3] + rgba[c + 3] + rgba[d + 3] + 2) >> 2);
                }
            }

            return output;
        }

        // sRGB to linear for each of the 256 encoded values, and back again through a table over
        // the linear range. Both directions are called once per channel per texel of every mip
        // level, which is often enough that the pow they replace would show up in the timings.
        private static readonly float[] SrgbToLinear = BuildSrgbToLinear();
        private static readonly byte[] LinearToSrgbTable = BuildLinearToSrgb();

        private static float[] BuildSrgbToLinear()
        {
            var table = new float[256];
            for (int i = 0; i < 256; i++)
            {
                float value = i / 255f;
                table[i] = value <= 0.04045f
                    ? value / 12.92f
                    : (float)Math.Pow((value + 0.055f) / 1.055f, 2.4);
            }

            return table;
        }

        private static byte[] BuildLinearToSrgb()
        {
            // 4096 steps over the linear range: finer than the 256 values it maps back onto, so
            // the table is not what limits the result.
            var table = new byte[4096];
            for (int i = 0; i < table.Length; i++)
            {
                double value = i / (double)(table.Length - 1);
                double encoded = value <= 0.0031308
                    ? value * 12.92
                    : (1.055 * Math.Pow(value, 1.0 / 2.4)) - 0.055;
                table[i] = (byte)Math.Round(Math.Max(0.0, Math.Min(1.0, encoded)) * 255.0);
            }

            return table;
        }

        private static byte LinearToSrgb(float linear)
        {
            int index = (int)((linear * (LinearToSrgbTable.Length - 1)) + 0.5f);
            if (index < 0)
            {
                index = 0;
            }
            else if (index >= LinearToSrgbTable.Length)
            {
                index = LinearToSrgbTable.Length - 1;
            }

            return LinearToSrgbTable[index];
        }

        private static int Pack565(int r, int g, int b)
        {
            return ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);
        }

        private static void Unpack565(int packed, out int r, out int g, out int b)
        {
            // Replicate the high bits into the low ones, so that full-scale input comes back as
            // full scale rather than slightly short of it.
            int r5 = (packed >> 11) & 0x1F;
            int g6 = (packed >> 5) & 0x3F;
            int b5 = packed & 0x1F;
            r = (r5 << 3) | (r5 >> 2);
            g = (g6 << 2) | (g6 >> 4);
            b = (b5 << 3) | (b5 >> 2);
        }
    }
}
