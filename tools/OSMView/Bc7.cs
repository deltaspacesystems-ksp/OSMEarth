using System;

namespace OSMView
{
    /// <summary>
    /// Programowy dekoder BC7 - tylko do testera. W grze dane kafli satelity ida
    /// na karte bez rozpakowywania (Texture2D BC7 + LoadRawTextureData), a tu
    /// trzeba je zobaczyc, zeby sprawdzic orientacje scian i dopasowanie do drog.
    /// Wynik: RGBA, wiersz 0 = pierwszy wiersz blokow w danych.
    /// </summary>
    internal static class Bc7
    {
        private struct Mode
        {
            public int Subsets, PartBits, RotBits, SelBits, ColorBits, AlphaBits, EndPBits, SharedPBits, IndexBits, Index2Bits;
            public Mode(int ns, int pb, int rb, int isb, int cb, int ab, int epb, int spb, int ib, int ib2)
            { Subsets = ns; PartBits = pb; RotBits = rb; SelBits = isb; ColorBits = cb; AlphaBits = ab; EndPBits = epb; SharedPBits = spb; IndexBits = ib; Index2Bits = ib2; }
        }

        private static readonly Mode[] Modes =
        {
            new Mode(3, 4, 0, 0, 4, 0, 1, 0, 3, 0),
            new Mode(2, 6, 0, 0, 6, 0, 0, 1, 3, 0),
            new Mode(3, 6, 0, 0, 5, 0, 0, 0, 2, 0),
            new Mode(2, 6, 0, 0, 7, 0, 1, 0, 2, 0),
            new Mode(1, 0, 2, 1, 5, 6, 0, 0, 2, 3),
            new Mode(1, 0, 2, 0, 7, 8, 0, 0, 2, 2),
            new Mode(1, 0, 0, 0, 7, 7, 1, 0, 4, 0),
            new Mode(2, 6, 0, 0, 5, 5, 1, 0, 2, 0),
        };

        private static readonly int[] W2 = { 0, 21, 43, 64 };
        private static readonly int[] W3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
        private static readonly int[] W4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

        private static readonly string P2 =
            "0011001100110011" + "0001000100010001" + "0111011101110111" + "0001001100110111" +
            "0000000100010011" + "0011011101111111" + "0001001101111111" + "0000000100110111" +
            "0000000000010011" + "0011011111111111" + "0000000101111111" + "0000000000010111" +
            "0001011111111111" + "0000000011111111" + "0000111111111111" + "0000000000001111" +
            "0000100011101111" + "0111000100000000" + "0000000010001110" + "0111001100010000" +
            "0011000100000000" + "0000100011001110" + "0000000010001100" + "0111001100110001" +
            "0011000100010000" + "0000100010001100" + "0110011001100110" + "0011011001101100" +
            "0001011111101000" + "0000111111110000" + "0111000110001110" + "0011100110011100" +
            "0101010101010101" + "0000111100001111" + "0101101001011010" + "0011001111001100" +
            "0011110000111100" + "0101010110101010" + "0110100101101001" + "0101101010100101" +
            "0111001111001110" + "0001001111001000" + "0011001001001100" + "0011101111011100" +
            "0110100110010110" + "0011110011000011" + "0110011010011001" + "0000011001100000" +
            "0100111001000000" + "0010011100100000" + "0000001001110010" + "0000010011100100" +
            "0110110010010011" + "0011011011001001" + "0110001110011100" + "0011100111000110" +
            "0110110011001001" + "0110001100111001" + "0111111010000001" + "0001100011100111" +
            "0000111100110011" + "0011001111110000" + "0010001011101110" + "0100010001110111";

        private static readonly string P3 =
            "0011001102212222" + "0001001122112221" + "0000200122112211" + "0222002200110111" +
            "0000000011221122" + "0011001100220022" + "0022002211111111" + "0011001122112211" +
            "0000000011112222" + "0000111111112222" + "0000111122222222" + "0012001200120012" +
            "0112011201120112" + "0122012201220122" + "0011011211221222" + "0011200122002220" +
            "0001001101121122" + "0111001120012200" + "0000112211221122" + "0022002200221111" +
            "0111011102220222" + "0001000122212221" + "0000001101220122" + "0000110022102210" +
            "0122012200110000" + "0012001211222222" + "0110122112210110" + "0000011012211221" +
            "0022110211020022" + "0110011020022222" + "0011012201220011" + "0000200022112221" +
            "0000000211221222" + "0222002200120011" + "0011001200220222" + "0120012001200120" +
            "0000111122220000" + "0120120120120120" + "0120201212010120" + "0011220011220011" +
            "0011112222000011" + "0101010122222222" + "0000000021212121" + "0022112200221122" +
            "0022001100220011" + "0220122102201221" + "0101222222220101" + "0000212121212121" +
            "0101010101012222" + "0222011102220111" + "0002111200021112" + "0000211221122112" +
            "0222011101110222" + "0002111211120002" + "0110011001102222" + "0000000021122112" +
            "0110011022222222" + "0022001100110022" + "0022112211220022" + "0000000000002112" +
            "0002000100020001" + "0222122202221222" + "0101222222222222" + "0111201122012220";

        private static readonly int[] A2 =
        {
            15,15,15,15,15,15,15,15, 15,15,15,15,15,15,15,15,
            15, 2, 8, 2, 2, 8, 8,15,  2, 8, 2, 2, 8, 8, 2, 2,
            15,15, 6, 8, 2, 8,15,15,  2, 8, 2, 2, 2,15,15, 6,
             6, 2, 6, 8,15,15, 2, 2, 15,15,15,15,15, 2, 2,15
        };
        private static readonly int[] A3a =
        {
             3, 3,15,15, 8, 3,15,15,  8, 8, 6, 6, 6, 5, 3, 3,
             3, 3, 8,15, 3, 3, 6,10,  5, 8, 8, 6, 8, 5,15,15,
             8,15, 3, 5, 6,10, 8,15, 15, 3,15, 5,15,15,15,15,
             3,15, 5, 5, 5, 8, 5,10,  5,10, 8,13,15,12, 3, 3
        };
        private static readonly int[] A3b =
        {
            15, 8, 8, 3,15,15, 3, 8, 15,15,15,15,15,15,15, 8,
            15, 8,15, 3,15, 8,15, 8,  3,15, 6,10,15,15,10, 8,
            15, 3,15,10,10, 8, 9,10,  6,15, 8,15, 3, 6, 6, 8,
            15, 3,15,15,15,15,15,15, 15,15,15,15, 3,15,15, 8
        };

        /// <summary>Kotwice musza lezec w swoim podzbiorze - to wylapuje pomylke przy przepisywaniu tablic.</summary>
        public static string SelfCheck()
        {
            int bad = 0;
            for (int p = 0; p < 64; p++)
            {
                if (P2[p * 16] != '0' || P2[p * 16 + A2[p]] != '1') bad++;
                if (P3[p * 16] != '0' || P3[p * 16 + A3a[p]] != '1' || P3[p * 16 + A3b[p]] != '2') bad++;
            }
            return bad == 0 ? "tablice BC7 spojne" : bad + " niespojnych partycji BC7";
        }

        public static byte[] Decode(byte[] data, int width, int height)
        {
            var outp = new byte[width * height * 4];
            int bw = (width + 3) / 4, bh = (height + 3) / 4;
            var px = new byte[16 * 4];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    DecodeBlock(data, (by * bw + bx) * 16, px);
                    for (int j = 0; j < 4; j++)
                        for (int i = 0; i < 4; i++)
                        {
                            int x = bx * 4 + i, y = by * 4 + j;
                            if (x >= width || y >= height) continue;
                            Buffer.BlockCopy(px, (j * 4 + i) * 4, outp, (y * width + x) * 4, 4);
                        }
                }
            return outp;
        }

        private static void DecodeBlock(byte[] d, int o, byte[] px)
        {
            int modeIdx = 0;
            while (modeIdx < 8 && (d[o] >> modeIdx & 1) == 0) modeIdx++;
            if (modeIdx == 8) { Array.Clear(px, 0, px.Length); return; }
            Mode m = Modes[modeIdx];
            int bit = modeIdx + 1;

            int partition = Bits(d, o, ref bit, m.PartBits);
            int rotation = Bits(d, o, ref bit, m.RotBits);
            int sel = Bits(d, o, ref bit, m.SelBits);

            int ns = m.Subsets;
            var ep = new int[ns * 2, 4];
            for (int c = 0; c < 3; c++)
                for (int e = 0; e < ns * 2; e++) ep[e, c] = Bits(d, o, ref bit, m.ColorBits);
            for (int e = 0; e < ns * 2; e++) ep[e, 3] = m.AlphaBits > 0 ? Bits(d, o, ref bit, m.AlphaBits) : 255;

            int cb = m.ColorBits, ab = m.AlphaBits;
            if (m.EndPBits > 0)
            {
                for (int e = 0; e < ns * 2; e++)
                {
                    int p = Bits(d, o, ref bit, 1);
                    for (int c = 0; c < 3; c++) ep[e, c] = ep[e, c] << 1 | p;
                    if (ab > 0) ep[e, 3] = ep[e, 3] << 1 | p;
                }
                cb++; if (ab > 0) ab++;
            }
            else if (m.SharedPBits > 0)
            {
                for (int s = 0; s < ns; s++)
                {
                    int p = Bits(d, o, ref bit, 1);
                    for (int e = s * 2; e < s * 2 + 2; e++)
                    {
                        for (int c = 0; c < 3; c++) ep[e, c] = ep[e, c] << 1 | p;
                        if (ab > 0) ep[e, 3] = ep[e, 3] << 1 | p;
                    }
                }
                cb++; if (ab > 0) ab++;
            }
            for (int e = 0; e < ns * 2; e++)
            {
                for (int c = 0; c < 3; c++) ep[e, c] = Expand(ep[e, c], cb);
                ep[e, 3] = ab > 0 ? Expand(ep[e, 3], ab) : 255;
            }

            var subsetOf = new int[16];
            for (int i = 0; i < 16; i++)
                subsetOf[i] = ns == 1 ? 0 : (ns == 2 ? P2[partition * 16 + i] - '0' : P3[partition * 16 + i] - '0');

            var idx1 = new int[16];
            var idx2 = new int[16];
            for (int i = 0; i < 16; i++)
            {
                int n = m.IndexBits;
                if (IsAnchor(i, ns, partition)) n--;
                idx1[i] = Bits(d, o, ref bit, n);
            }
            if (m.Index2Bits > 0)
                for (int i = 0; i < 16; i++)
                    idx2[i] = Bits(d, o, ref bit, i == 0 ? m.Index2Bits - 1 : m.Index2Bits);

            for (int i = 0; i < 16; i++)
            {
                int s = subsetOf[i];
                int e0 = s * 2, e1 = s * 2 + 1;
                int r, g, b, a;
                if (m.Index2Bits == 0)
                {
                    int w = Weight(m.IndexBits, idx1[i]);
                    r = Lerp(ep[e0, 0], ep[e1, 0], w); g = Lerp(ep[e0, 1], ep[e1, 1], w);
                    b = Lerp(ep[e0, 2], ep[e1, 2], w); a = Lerp(ep[e0, 3], ep[e1, 3], w);
                }
                else
                {
                    int wc, wa;
                    if (sel == 0) { wc = Weight(m.IndexBits, idx1[i]); wa = Weight(m.Index2Bits, idx2[i]); }
                    else { wc = Weight(m.Index2Bits, idx2[i]); wa = Weight(m.IndexBits, idx1[i]); }
                    r = Lerp(ep[e0, 0], ep[e1, 0], wc); g = Lerp(ep[e0, 1], ep[e1, 1], wc);
                    b = Lerp(ep[e0, 2], ep[e1, 2], wc); a = Lerp(ep[e0, 3], ep[e1, 3], wa);
                }
                switch (rotation)
                {
                    case 1: { int t = a; a = r; r = t; break; }
                    case 2: { int t = a; a = g; g = t; break; }
                    case 3: { int t = a; a = b; b = t; break; }
                }
                px[i * 4] = (byte)r; px[i * 4 + 1] = (byte)g; px[i * 4 + 2] = (byte)b; px[i * 4 + 3] = (byte)a;
            }
        }

        private static bool IsAnchor(int i, int ns, int p)
        {
            if (i == 0) return true;
            if (ns == 2) return i == A2[p];
            if (ns == 3) return i == A3a[p] || i == A3b[p];
            return false;
        }

        private static int Weight(int bits, int i) { return bits == 2 ? W2[i] : (bits == 3 ? W3[i] : W4[i]); }
        private static int Lerp(int a, int b, int w) { return ((64 - w) * a + w * b + 32) >> 6; }
        private static int Expand(int v, int bits) { v <<= 8 - bits; return v | (v >> bits); }

        private static int Bits(byte[] d, int o, ref int bit, int n)
        {
            int v = 0;
            for (int k = 0; k < n; k++, bit++)
                v |= (d[o + (bit >> 3)] >> (bit & 7) & 1) << k;
            return v;
        }
    }
}
