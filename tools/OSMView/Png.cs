using System;
using System.IO;
using System.IO.Compression;

namespace OSMView
{
    /// <summary>Minimalny zapis PNG (RGB, 8 bit) - bez System.Drawing i bez paczek.</summary>
    internal static class Png
    {
        public static void Save(string path, byte[] rgb, int w, int h)
        {
            using (var fs = File.Create(path))
            {
                fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);

                var ihdr = new byte[13];
                BE(ihdr, 0, w); BE(ihdr, 4, h);
                ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
                Chunk(fs, "IHDR", ihdr);

                byte[] raw = new byte[(w * 3 + 1) * h];
                for (int y = 0; y < h; y++)
                {
                    int row = y * (w * 3 + 1);
                    raw[row] = 0;
                    Buffer.BlockCopy(rgb, y * w * 3, raw, row + 1, w * 3);
                }

                byte[] z;
                using (var ms = new MemoryStream())
                {
                    using (var zs = new ZLibStream(ms, CompressionLevel.Fastest, true))
                        zs.Write(raw, 0, raw.Length);
                    z = ms.ToArray();
                }
                Chunk(fs, "IDAT", z);
                Chunk(fs, "IEND", new byte[0]);
            }
        }

        private static void BE(byte[] b, int o, int v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; BE(len, 0, data.Length);
            s.Write(len, 0, 4);
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc(t, 0xFFFFFFFFu);
            crc = Crc(data, crc) ^ 0xFFFFFFFFu;
            var c = new byte[4]; BE(c, 0, (int)crc);
            s.Write(c, 0, 4);
        }

        private static uint[] table;
        private static uint Crc(byte[] d, uint c)
        {
            if (table == null)
            {
                table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint k = n;
                    for (int i = 0; i < 8; i++) k = (k & 1) != 0 ? 0xEDB88320u ^ (k >> 1) : k >> 1;
                    table[n] = k;
                }
            }
            foreach (byte b in d) c = table[(c ^ b) & 0xFF] ^ (c >> 8);
            return c;
        }
    }
}
