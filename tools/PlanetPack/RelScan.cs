using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OSMRoads;

namespace PlanetPack
{
    /// <summary>Multipoligon z pliku: atrybuty obszaru/budynku i linie-czlonkowie.</summary>
    internal sealed class RelInfo
    {
        public long Id;
        public RecAttrs Attrs;
        public long[] Ways;
        public bool[] Inner;
    }

    /// <summary>
    /// Relacje type=multipolygon (lasy, laki, parki, budynki z dziedzincem...).
    ///
    /// Duze lasy w OSM to prawie zawsze multipoligony: sam obrys jest pocięty na
    /// dziesiatki linii BEZ tagow, a "landuse=forest" wisi na relacji. Stary
    /// konwerter relacji nie czytal wcale, wiec takie lasy znikaly w calosci.
    ///
    /// W posortowanym .osm.pbf relacje sa na samym koncu pliku, a przebiegi potrzebuja
    /// ich PRZED drogami. Szukamy wiec poczatku relacji bisekcja po offsetach pliku
    /// (naglowek bloba "OSMData" da sie znalezc w dowolnym miejscu) i czytamy tylko
    /// ogon - kilka procent pliku zamiast calosci. Wynik idzie do rels.bin.
    /// </summary>
    internal static class RelScan
    {
        private static readonly byte[] Sig = { 0x0A, 0x07, (byte)'O', (byte)'S', (byte)'M', (byte)'D', (byte)'a', (byte)'t', (byte)'a' };

        public static List<RelInfo> LoadOrRun(string input, string cache, string stamp, Action<string> log)
        {
            List<RelInfo> r = Load(cache, stamp);
            if (r != null) { log(string.Format("  relacje: z pliku rels.bin ({0:N0})", r.Count)); return r; }
            r = Run(input, log);
            Save(cache, stamp, r);
            return r;
        }

        // ------------------------------------------------------------------

        private static List<RelInfo> Run(string input, Action<string> log)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var fs = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                long len = fs.Length;
                var ctx = new DecodeContext();

                // bisekcja: najwczesniejszy blob z relacjami
                long lo = 0, hi = len;
                long found = -1;
                while (hi - lo > (8L << 20))
                {
                    long mid = lo + (hi - lo) / 2;
                    long at = NextBlob(fs, mid);
                    if (at < 0) { hi = mid; continue; }
                    int kind = BlobKind(fs, at, ctx);       // 0 wezly, 1 drogi, 2 relacje, -1 inne
                    if (kind >= 2) { hi = mid; found = at; }
                    else lo = mid;
                }
                long start = NextBlob(fs, lo);
                if (start < 0) start = found;
                if (start < 0) { log("  relacje: nie znaleziono bloku relacji"); return new List<RelInfo>(); }
                log(string.Format("  relacje: od {0:N1} GB z {1:N1} GB", start / 1073741824.0, len / 1073741824.0));

                var list = new List<RelInfo>();
                fs.Position = start;
                var len4 = new byte[4];
                long blocks = 0;
                while (fs.Read(len4, 0, 4) == 4)
                {
                    int hl = (len4[0] << 24) | (len4[1] << 16) | (len4[2] << 8) | len4[3];
                    var hdr = new byte[hl];
                    ReadExact(fs, hdr, hl);
                    string type; int dataSize;
                    ParseHeader(hdr, hl, out type, out dataSize);
                    byte[] data = ArrayPool<byte>.Shared.Rent(dataSize);
                    ReadExact(fs, data, dataSize);
                    if (type == "OSMData")
                    {
                        int rawLen;
                        byte[] raw = BlockDecoder.Inflate(data, dataSize, out rawLen);
                        BlockDecoder.DecodeRelations(raw, rawLen, ctx, list);
                        ArrayPool<byte>.Shared.Return(raw);
                        blocks++;
                    }
                    ArrayPool<byte>.Shared.Return(data);
                    if (type == "OSMData" && blocks % 200 == 0)
                        log(string.Format("  relacje: {0:F1}%  multipoligonow {1:N0}", 100.0 * fs.Position / len, list.Count));
                }
                log(string.Format("  relacje: {0:N0} multipoligonow z {1:N0} blokow, {2:F1} min",
                                  list.Count, blocks, sw.Elapsed.TotalMinutes));
                return list;
            }
        }

        /// <summary>Offset poczatku pierwszego bloba (pole dlugosci naglowka) od pozycji pos.</summary>
        private static long NextBlob(FileStream fs, long pos)
        {
            const int Win = 4 << 20;
            var buf = new byte[Win + 64];
            long p = Math.Max(0, pos);
            while (p < fs.Length)
            {
                fs.Position = p;
                int n = fs.Read(buf, 0, buf.Length);
                if (n < 16) return -1;
                for (int i = 4; i + Sig.Length <= n; i++)
                {
                    if (buf[i] != Sig[0] || buf[i + 1] != Sig[1]) continue;
                    bool ok = true;
                    for (int k = 2; k < Sig.Length; k++) if (buf[i + k] != Sig[k]) { ok = false; break; }
                    if (!ok) continue;
                    long at = p + i - 4;
                    if (Valid(fs, at)) return at;
                }
                p += Win;
            }
            return -1;
        }

        /// <summary>Czy pod offsetem zaczyna sie blob, po ktorym od razu zaczyna sie nastepny (albo koniec pliku).</summary>
        private static bool Valid(FileStream fs, long at)
        {
            long save = fs.Position;
            try
            {
                fs.Position = at;
                var len4 = new byte[4];
                if (fs.Read(len4, 0, 4) != 4) return false;
                int hl = (len4[0] << 24) | (len4[1] << 16) | (len4[2] << 8) | len4[3];
                if (hl <= 0 || hl > 64 * 1024) return false;
                var hdr = new byte[hl];
                ReadExact(fs, hdr, hl);
                string type; int ds;
                ParseHeader(hdr, hl, out type, out ds);
                if (type != "OSMData" || ds <= 0 || ds > 64 << 20) return false;
                long next = at + 4 + hl + ds;
                if (next == fs.Length) return true;
                if (next > fs.Length) return false;
                fs.Position = next;
                if (fs.Read(len4, 0, 4) != 4) return false;
                int hl2 = (len4[0] << 24) | (len4[1] << 16) | (len4[2] << 8) | len4[3];
                return hl2 > 0 && hl2 < 64 * 1024;
            }
            catch { return false; }
            finally { fs.Position = save; }
        }

        private static int BlobKind(FileStream fs, long at, DecodeContext ctx)
        {
            fs.Position = at;
            var len4 = new byte[4];
            fs.Read(len4, 0, 4);
            int hl = (len4[0] << 24) | (len4[1] << 16) | (len4[2] << 8) | len4[3];
            var hdr = new byte[hl];
            ReadExact(fs, hdr, hl);
            string type; int ds;
            ParseHeader(hdr, hl, out type, out ds);
            var data = new byte[ds];
            ReadExact(fs, data, ds);
            int rawLen;
            byte[] raw = BlockDecoder.Inflate(data, ds, out rawLen);
            BlockInfo info = BlockDecoder.ReadInfo(raw, rawLen, ctx);
            ArrayPool<byte>.Shared.Return(raw);
            if (info.HasRels) return 2;
            if (info.HasWays) return 1;
            if (info.HasNodes) return 0;
            return -1;
        }

        private static void ParseHeader(byte[] hdr, int hl, out string type, out int dataSize)
        {
            type = null; dataSize = -1;
            int p = 0;
            while (p < hl)
            {
                ulong tag = Proto.Varint(hdr, ref p);
                int f = (int)(tag >> 3), wire = (int)(tag & 7);
                if (f == 1 && wire == 2)
                {
                    int l = (int)Proto.Varint(hdr, ref p);
                    type = Encoding.ASCII.GetString(hdr, p, l);
                    p += l;
                }
                else if (f == 3 && wire == 0) dataSize = (int)Proto.Varint(hdr, ref p);
                else Proto.Skip(hdr, ref p, wire);
            }
        }

        private static void ReadExact(Stream s, byte[] buf, int count)
        {
            int done = 0;
            while (done < count)
            {
                int n = s.Read(buf, done, count - done);
                if (n <= 0) throw new EndOfStreamException();
                done += n;
            }
        }

        // ------------------------------------------------------------------

        private static List<RelInfo> Load(string path, string stamp)
        {
            if (!File.Exists(path)) return null;
            using (var br = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
            {
                if (br.ReadString() != stamp) return null;
                int n = br.ReadInt32();
                var list = new List<RelInfo>(n);
                for (int i = 0; i < n; i++)
                {
                    var r = new RelInfo { Id = br.ReadInt64(), Attrs = new RecAttrs() };
                    r.Attrs.Kind = br.ReadByte();
                    r.Attrs.Area = br.ReadSByte();
                    r.Attrs.S1 = Null(br.ReadString());
                    r.Attrs.Roof = Null(br.ReadString());
                    r.Attrs.Name = Null(br.ReadString());
                    r.Attrs.Height = br.ReadSingle();
                    r.Attrs.Levels = br.ReadInt32();
                    r.Attrs.BColour = Null(br.ReadString());
                    r.Attrs.RColour = Null(br.ReadString());
                    int m = br.ReadInt32();
                    r.Ways = new long[m];
                    r.Inner = new bool[m];
                    for (int k = 0; k < m; k++) { r.Ways[k] = br.ReadInt64(); r.Inner[k] = br.ReadBoolean(); }
                    list.Add(r);
                }
                return list;
            }
        }

        private static string Null(string s) { return s.Length == 0 ? null : s; }

        private static void Save(string path, string stamp, List<RelInfo> list)
        {
            string tmp = path + ".tmp";
            using (var bw = new BinaryWriter(File.Create(tmp), Encoding.UTF8))
            {
                bw.Write(stamp);
                bw.Write(list.Count);
                foreach (RelInfo r in list)
                {
                    bw.Write(r.Id);
                    bw.Write(r.Attrs.Kind);
                    bw.Write(r.Attrs.Area);
                    bw.Write(r.Attrs.S1 ?? "");
                    bw.Write(r.Attrs.Roof ?? "");
                    bw.Write(r.Attrs.Name ?? "");
                    bw.Write(r.Attrs.Height);
                    bw.Write(r.Attrs.Levels);
                    bw.Write(r.Attrs.BColour ?? "");
                    bw.Write(r.Attrs.RColour ?? "");
                    bw.Write(r.Ways.Length);
                    for (int k = 0; k < r.Ways.Length; k++) { bw.Write(r.Ways[k]); bw.Write(r.Inner[k]); }
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
