using System;

namespace Unity.USDToolkit
{
    // OpenEXR PIZ decompression: a Huffman-coded, Haar-wavelet-transformed image of 16-bit
    // values, with a bitmap that maps the values actually used onto a dense range. Lossless.
    // A port of the reference decoder (OpenEXR ImfPizCompressor / ImfHuf / ImfWav, BSD-3), kept
    // close to it so the two can be compared line by line. Every read is bounds-checked: a chunk
    // that would run past its data or past the pixels it claims fails instead of throwing.
    internal static class UsdExrPiz
    {
        private const int HufEncBits = 16;
        private const int HufDecBits = 14;
        private const int HufEncSize = (1 << HufEncBits) + 1;
        private const int HufDecSize = 1 << HufDecBits;
        private const int HufDecMask = HufDecSize - 1;
        private const int ShortZeroCodeRun = 59;
        private const int LongZeroCodeRun = 63;
        private const int ShortestLongRun = 2 + LongZeroCodeRun - ShortZeroCodeRun;
        private const int UShortRange = 1 << 16;
        private const int BitmapSize = UShortRange >> 3;

        // channelSizes: 16-bit units per sample of each channel, in file order (half 1, float or
        // uint 2). The output is the chunk as an uncompressed one would hold it: for each line,
        // each channel's `width` samples.
        public static bool Decompress(byte[] data, int at, int dataSize, byte[] output, int expected,
                                      int[] channelSizes, int width, int lines, out string reason)
        {
            reason = null;
            int end = at + dataSize;
            if (dataSize < 4 || end > data.Length || (expected & 1) != 0)
            {
                reason = "PIZ chunk too short";
                return false;
            }

            var bitmap = new byte[BitmapSize];
            int minNonZero = data[at] | (data[at + 1] << 8);
            int maxNonZero = data[at + 2] | (data[at + 3] << 8);
            at += 4;
            if (maxNonZero >= BitmapSize)
            {
                reason = "PIZ bitmap out of range";
                return false;
            }

            if (minNonZero <= maxNonZero)
            {
                int count = maxNonZero - minNonZero + 1;
                if (at + count > end)
                {
                    reason = "PIZ bitmap runs past its chunk";
                    return false;
                }

                Buffer.BlockCopy(data, at, bitmap, minNonZero, count);
                at += count;
            }

            var lut = new ushort[UShortRange];
            ushort maxValue = ReverseLutFromBitmap(bitmap, lut);

            if (at + 4 > end)
            {
                reason = "PIZ length runs past its chunk";
                return false;
            }

            int length = data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) | (data[at + 3] << 24);
            at += 4;
            if (length < 0 || at + length > end)
            {
                reason = "PIZ Huffman data runs past its chunk";
                return false;
            }

            int count16 = expected / 2;
            var buffer = new ushort[count16];
            if (!HufUncompress(data, at, length, buffer, count16, out reason))
            {
                return false;
            }

            // Wavelet decode, channel by channel; each channel's block is width*lines*size values.
            long total = 0;
            foreach (int size in channelSizes)
            {
                total += (long)width * lines * size;
            }

            if (total != count16)
            {
                reason = "PIZ chunk size does not match its channels";
                return false;
            }

            int start = 0;
            var starts = new int[channelSizes.Length];
            for (int c = 0; c < channelSizes.Length; c++)
            {
                starts[c] = start;
                int size = channelSizes[c];
                for (int j = 0; j < size; j++)
                {
                    Wav2Decode(buffer, start + j, width, size, lines, width * size, maxValue);
                }

                start += width * lines * size;
            }

            for (int i = 0; i < count16; i++)
            {
                buffer[i] = lut[buffer[i]];
            }

            // Interleave back to lines: line y holds each channel's row in turn.
            int outAt = 0;
            var cursor = (int[])starts.Clone();
            for (int y = 0; y < lines; y++)
            {
                for (int c = 0; c < channelSizes.Length; c++)
                {
                    int n = width * channelSizes[c];
                    for (int i = 0; i < n; i++)
                    {
                        ushort v = buffer[cursor[c] + i];
                        output[outAt++] = (byte)v;
                        output[outAt++] = (byte)(v >> 8);
                    }

                    cursor[c] += n;
                }
            }

            return true;
        }

        private static ushort ReverseLutFromBitmap(byte[] bitmap, ushort[] lut)
        {
            int k = 0;
            for (int i = 0; i < UShortRange; i++)
            {
                if (i == 0 || (bitmap[i >> 3] & (1 << (i & 7))) != 0)
                {
                    lut[k++] = (ushort)i;
                }
            }

            int n = k - 1;
            while (k < UShortRange)
            {
                lut[k++] = 0;
            }

            return (ushort)n;
        }

        // ---- wavelet ----

        private static void Wdec14(ushort l, ushort h, out ushort a, out ushort b)
        {
            short ls = (short)l, hs = (short)h;
            int hi = hs;
            int ai = ls + (hi & 1) + (hi >> 1);
            a = (ushort)(short)ai;
            b = (ushort)(short)(ai - hi);
        }

        private const int ModMask = (1 << 16) - 1;
        private const int AOffset = 1 << 15;

        private static void Wdec16(ushort l, ushort h, out ushort a, out ushort b)
        {
            int m = l, d = h;
            int bb = (m - (d >> 1)) & ModMask;
            int aa = (d + bb - AOffset) & ModMask;
            b = (ushort)bb;
            a = (ushort)aa;
        }

        private static void Wav2Decode(ushort[] v, int origin, int nx, int ox, int ny, int oy, ushort mx)
        {
            bool w14 = mx < (1 << 14);
            int n = nx > ny ? ny : nx;
            int p = 1;
            while (p <= n)
            {
                p <<= 1;
            }

            p >>= 1;
            int p2 = p;
            p >>= 1;
            while (p >= 1)
            {
                int py = origin;
                int ey = origin + oy * (ny - p2);
                int oy1 = oy * p, oy2 = oy * p2, ox1 = ox * p, ox2 = ox * p2;
                ushort i00, i01, i10, i11;
                for (; py <= ey; py += oy2)
                {
                    int px = py;
                    int ex = py + ox * (nx - p2);
                    for (; px <= ex; px += ox2)
                    {
                        int p01 = px + ox1, p10 = px + oy1, p11 = p10 + ox1;
                        if (w14)
                        {
                            Wdec14(v[px], v[p10], out i00, out i10);
                            Wdec14(v[p01], v[p11], out i01, out i11);
                            Wdec14(i00, i01, out v[px], out v[p01]);
                            Wdec14(i10, i11, out v[p10], out v[p11]);
                        }
                        else
                        {
                            Wdec16(v[px], v[p10], out i00, out i10);
                            Wdec16(v[p01], v[p11], out i01, out i11);
                            Wdec16(i00, i01, out v[px], out v[p01]);
                            Wdec16(i10, i11, out v[p10], out v[p11]);
                        }
                    }

                    if ((nx & p) != 0)
                    {
                        int p10 = px + oy1;
                        if (w14)
                        {
                            Wdec14(v[px], v[p10], out i00, out v[p10]);
                        }
                        else
                        {
                            Wdec16(v[px], v[p10], out i00, out v[p10]);
                        }

                        v[px] = i00;
                    }
                }

                if ((ny & p) != 0)
                {
                    int px = py;
                    int ex = py + ox * (nx - p2);
                    for (; px <= ex; px += ox2)
                    {
                        int p01 = px + ox1;
                        if (w14)
                        {
                            Wdec14(v[px], v[p01], out i00, out v[p01]);
                        }
                        else
                        {
                            Wdec16(v[px], v[p01], out i00, out v[p01]);
                        }

                        v[px] = i00;
                    }
                }

                p2 = p;
                p >>= 1;
            }
        }

        // ---- Huffman ----

        private struct HufDec
        {
            public int Len;
            public int Lit;
            public int[] P;
        }

        private static int ReadUInt(byte[] b, int at) => b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24);

        private static bool HufUncompress(byte[] data, int at, int nCompressed, ushort[] raw, int nRaw, out string reason)
        {
            reason = null;
            if (nCompressed == 0)
            {
                if (nRaw != 0)
                {
                    reason = "PIZ Huffman data empty";
                    return false;
                }

                return true;
            }

            if (nCompressed < 20)
            {
                reason = "PIZ Huffman header too short";
                return false;
            }

            int im = ReadUInt(data, at);
            int iM = ReadUInt(data, at + 4);
            int nBits = ReadUInt(data, at + 12);
            if (im < 0 || iM < 0 || im >= HufEncSize || iM >= HufEncSize || nBits < 0)
            {
                reason = "PIZ Huffman table range invalid";
                return false;
            }

            int ptr = at + 20;
            int end = at + nCompressed;
            var freq = new long[HufEncSize];
            if (!HufUnpackEncTable(data, ref ptr, end, im, iM, freq))
            {
                reason = "PIZ Huffman table corrupt";
                return false;
            }

            if ((long)nBits > 8L * (end - ptr))
            {
                reason = "PIZ Huffman bit count exceeds data";
                return false;
            }

            var hdec = new HufDec[HufDecSize];
            if (!HufBuildDecTable(freq, im, iM, hdec))
            {
                reason = "PIZ Huffman decode table invalid";
                return false;
            }

            if (!HufDecode(freq, hdec, data, ptr, end, nBits, iM, nRaw, raw))
            {
                reason = "PIZ Huffman data corrupt";
                return false;
            }

            return true;
        }

        private static bool GetBits(int nBits, ref long c, ref int lc, byte[] data, ref int ptr, int end, out int value)
        {
            while (lc < nBits)
            {
                if (ptr >= end)
                {
                    value = 0;
                    return false;
                }

                c = (c << 8) | data[ptr++];
                lc += 8;
            }

            lc -= nBits;
            value = (int)((c >> lc) & ((1L << nBits) - 1));
            return true;
        }

        private static bool HufUnpackEncTable(byte[] data, ref int ptr, int end, int im, int iM, long[] hcode)
        {
            long c = 0;
            int lc = 0;
            for (; im <= iM; im++)
            {
                if (!GetBits(6, ref c, ref lc, data, ref ptr, end, out int l))
                {
                    return false;
                }

                hcode[im] = l;
                if (l == LongZeroCodeRun)
                {
                    if (!GetBits(8, ref c, ref lc, data, ref ptr, end, out int run))
                    {
                        return false;
                    }

                    int zerun = run + ShortestLongRun;
                    if (im + zerun > iM + 1)
                    {
                        return false;
                    }

                    while (zerun-- > 0)
                    {
                        hcode[im++] = 0;
                    }

                    im--;
                }
                else if (l >= ShortZeroCodeRun)
                {
                    int zerun = l - ShortZeroCodeRun + 2;
                    if (im + zerun > iM + 1)
                    {
                        return false;
                    }

                    while (zerun-- > 0)
                    {
                        hcode[im++] = 0;
                    }

                    im--;
                }
            }

            HufCanonicalCodeTable(hcode);
            return true;
        }

        private static void HufCanonicalCodeTable(long[] hcode)
        {
            var n = new long[59];
            for (int i = 0; i < HufEncSize; i++)
            {
                n[hcode[i]] += 1;
            }

            long c = 0;
            for (int i = 58; i > 0; --i)
            {
                long nc = (c + n[i]) >> 1;
                n[i] = c;
                c = nc;
            }

            for (int i = 0; i < HufEncSize; i++)
            {
                int l = (int)hcode[i];
                if (l > 0)
                {
                    hcode[i] = l | (n[l]++ << 6);
                }
            }
        }

        private static int HufLength(long code) => (int)(code & 63);

        private static long HufCode(long code) => code >> 6;

        private static bool HufBuildDecTable(long[] hcode, int im, int iM, HufDec[] hdecod)
        {
            for (; im <= iM; im++)
            {
                long c = HufCode(hcode[im]);
                int l = HufLength(hcode[im]);
                if ((c >> l) != 0)
                {
                    return false;
                }

                if (l > HufDecBits)
                {
                    int index = (int)(c >> (l - HufDecBits));
                    if (hdecod[index].Len != 0)
                    {
                        return false;
                    }

                    hdecod[index].Lit++;
                    int[] old = hdecod[index].P;
                    var grown = new int[hdecod[index].Lit];
                    if (old != null)
                    {
                        Array.Copy(old, grown, old.Length);
                    }

                    grown[hdecod[index].Lit - 1] = im;
                    hdecod[index].P = grown;
                }
                else if (l != 0)
                {
                    int index = (int)(c << (HufDecBits - l));
                    for (int i = 1 << (HufDecBits - l); i > 0; i--, index++)
                    {
                        if (hdecod[index].Len != 0 || hdecod[index].P != null)
                        {
                            return false;
                        }

                        hdecod[index].Len = l;
                        hdecod[index].Lit = im;
                    }
                }
            }

            return true;
        }

        private static bool GetCode(int po, int rlc, ref long c, ref int lc, byte[] data, ref int ptr, int end,
                                    ushort[] output, ref int outb, int oe)
        {
            if (po == rlc)
            {
                if (lc < 8)
                {
                    if (ptr >= end)
                    {
                        return false;
                    }

                    c = (c << 8) | data[ptr++];
                    lc += 8;
                }

                lc -= 8;
                int cs = (int)((c >> lc) & 0xff);
                if (outb == 0 || outb + cs > oe)
                {
                    return false;
                }

                ushort s = output[outb - 1];
                while (cs-- > 0)
                {
                    output[outb++] = s;
                }
            }
            else if (outb < oe)
            {
                output[outb++] = (ushort)po;
            }
            else
            {
                return false;
            }

            return true;
        }

        private static bool HufDecode(long[] hcode, HufDec[] hdecod, byte[] data, int ptr, int end, int ni, int rlc,
                                      int no, ushort[] output)
        {
            long c = 0;
            int lc = 0;
            int outb = 0;
            int ie = ptr + (ni + 7) / 8;
            if (ie > end)
            {
                return false;
            }

            while (ptr < ie)
            {
                c = (c << 8) | data[ptr++];
                lc += 8;
                // Keep c from overflowing: only the low 64 bits matter and lc stays small.
                while (lc >= HufDecBits)
                {
                    HufDec pl = hdecod[(int)((c >> (lc - HufDecBits)) & HufDecMask)];
                    if (pl.Len != 0)
                    {
                        lc -= pl.Len;
                        if (!GetCode(pl.Lit, rlc, ref c, ref lc, data, ref ptr, ie, output, ref outb, no))
                        {
                            return false;
                        }
                    }
                    else
                    {
                        if (pl.P == null)
                        {
                            return false;
                        }

                        int j;
                        for (j = 0; j < pl.Lit; j++)
                        {
                            int l = HufLength(hcode[pl.P[j]]);
                            while (lc < l && ptr < ie)
                            {
                                c = (c << 8) | data[ptr++];
                                lc += 8;
                            }

                            if (lc >= l && HufCode(hcode[pl.P[j]]) == ((c >> (lc - l)) & ((1L << l) - 1)))
                            {
                                lc -= l;
                                if (!GetCode(pl.P[j], rlc, ref c, ref lc, data, ref ptr, ie, output, ref outb, no))
                                {
                                    return false;
                                }

                                break;
                            }
                        }

                        if (j == pl.Lit)
                        {
                            return false;
                        }
                    }
                }
            }

            int skip = (8 - ni) & 7;
            c >>= skip;
            lc -= skip;
            while (lc > 0)
            {
                HufDec pl = hdecod[(int)((c << (HufDecBits - lc)) & HufDecMask)];
                if (pl.Len == 0)
                {
                    return false;
                }

                lc -= pl.Len;
                if (!GetCode(pl.Lit, rlc, ref c, ref lc, data, ref ptr, ie, output, ref outb, no))
                {
                    return false;
                }
            }

            return outb == no;
        }
    }
}
