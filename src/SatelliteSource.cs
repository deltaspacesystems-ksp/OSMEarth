using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Zdjecia satelitarne do planera - z plikow, ktore gra juz ma na dysku:
    ///
    ///   - Sol-Textures: cala planeta w piramidzie poziomow 0..7 (ok. 300 m/px na L7),
    ///     GameData/Sol-Textures/PluginData/.../03_Earth/Terrain/Level_N/canonical.color.LN.*
    ///   - Mirage: pamiec podreczna zdjec Sentinel-2 (EOX) sciaganych w locie tam,
    ///     gdzie sie latalo, poziomy 7..12 (do ok. 10 m/px),
    ///     GameData/Mirage/PluginData/WebTiles/Earth/web.color.*
    ///
    /// Oba to ten sam format Mirage: .idx (naglowek 23 B + wpisy 22 B) i .bin, w ktorym
    /// pod offsetem lezy 24-bajtowy naglowek kafla, a za nim dane BC7 264x264
    /// (256 px + 4 px ramki z kazdej strony), surowe albo skompresowane LZ4.
    ///
    /// Kafle leza na SZESCIANIE: klucz = sciana &lt;&lt; 60 | poziom &lt;&lt; 51 | Morton(x, y).
    /// Geometria scian przepisana 1:1 z Mirage (MirageCubeMath) - inaczej obraz
    /// rozjechalby sie z drogami.
    /// </summary>
    public sealed class SatelliteSource
    {
        public const int TilePx = 264, BorderPx = 4, InnerPx = 256;
        public const int RawBytes = 66 * 66 * 16;          // BC7: 16 bajtow na blok 4x4
        private const int TileHeaderBytes = 24;
        private const int FormatBC7 = 25;

        private sealed class Archive
        {
            public string Bin, Idx;
            public DateTime IdxTime;
            public Dictionary<ulong, long> Offsets = new Dictionary<ulong, long>();
            public Dictionary<ulong, int> Lengths = new Dictionary<ulong, int>();
            public Dictionary<ulong, byte> Codecs = new Dictionary<ulong, byte>();
            public FileStream Stream;
            public bool Live;           // Mirage dopisuje w trakcie gry - indeks trzeba odswiezac
            public float Checked;
        }

        private readonly List<Archive> canonical = new List<Archive>();   // indeks = poziom
        private Archive web;
        private readonly object gate = new object();

        public string Body { get; private set; }
        public int MaxCanonicalLevel { get { return canonical.Count - 1; } }
        public int MaxWebLevel { get; private set; }
        public int WebTiles { get { return web != null ? web.Offsets.Count : 0; } }
        public bool Available { get { return canonical.Count > 0 || WebTiles > 0; } }
        public string Report { get; private set; }

        // --- mala pamiec ostatnio czytanych kafli: sasiednie kafle mapy dziela kafle szescianu ---
        private const int CacheTiles = 48;
        private readonly Dictionary<ulong, byte[]> cache = new Dictionary<ulong, byte[]>();
        private readonly LinkedList<ulong> lru = new LinkedList<ulong>();

        private static readonly Dictionary<string, SatelliteSource> byBody = new Dictionary<string, SatelliteSource>();

        public static SatelliteSource For(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            lock (byBody)
            {
                SatelliteSource s;
                if (!byBody.TryGetValue(body, out s))
                {
                    s = new SatelliteSource(body);
                    byBody[body] = s;
                }
                return s;
            }
        }

        private SatelliteSource(string body)
        {
            Body = body;
            try { Open(); }
            catch (Exception e) { Report = "blad: " + e.Message; Debug.LogWarning("[OSMRoads] satelita " + body + ": " + e); }
        }

        private void Open()
        {
            string gameData = Path.Combine(KSPUtil.ApplicationRootPath, "GameData");

            // --- Sol-Textures: szukamy katalogu "<cokolwiek>_Earth" z Terrain/Level_0 ---
            string terrain = null;
            string solData = Path.Combine(gameData, "Sol-Textures/PluginData");
            if (Directory.Exists(solData))
            {
                foreach (string d in Directory.GetDirectories(solData, "*", SearchOption.AllDirectories))
                {
                    string leaf = Path.GetFileName(d);
                    if (leaf != Body && !leaf.EndsWith("_" + Body)) continue;
                    string t = Path.Combine(d, "Terrain");
                    if (Directory.Exists(Path.Combine(t, "Level_0"))) { terrain = t; break; }
                }
            }
            if (terrain != null)
            {
                for (int l = 0; l < 16; l++)
                {
                    string b = Path.Combine(terrain, string.Format("Level_{0}/canonical.color.L{0}", l));
                    if (!File.Exists(b + ".idx") || !File.Exists(b + ".bin")) break;
                    var a = new Archive { Bin = b + ".bin", Idx = b + ".idx" };
                    LoadIndex(a);
                    canonical.Add(a);
                }
            }

            string w = Path.Combine(gameData, "Mirage/PluginData/WebTiles/" + Body + "/web.color");
            if (File.Exists(w + ".idx") && File.Exists(w + ".bin"))
            {
                web = new Archive { Bin = w + ".bin", Idx = w + ".idx", Live = true };
                LoadIndex(web);
            }

            Report = string.Format("SOL poziomy 0-{0}, Mirage {1} kafli do poziomu {2}",
                                   MaxCanonicalLevel, WebTiles, MaxWebLevel);
        }

        private void LoadIndex(Archive a)
        {
            var offs = new Dictionary<ulong, long>();
            var lens = new Dictionary<ulong, int>();
            var codecs = new Dictionary<ulong, byte>();
            int maxLevel = 0;
            DateTime t = File.GetLastWriteTimeUtc(a.Idx);

            // Mirage moze akurat przepisywac indeks - czytamy go calego do pamieci naraz.
            byte[] b;
            using (var fs = new FileStream(a.Idx, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                b = new byte[fs.Length];
                int got = 0;
                while (got < b.Length) { int n = fs.Read(b, got, b.Length - got); if (n <= 0) break; got += n; }
            }
            if (b.Length < 23 || BitConverter.ToUInt32(b, 0) != 0x3149544D) throw new IOException("zly naglowek " + a.Idx);
            int count = BitConverter.ToInt32(b, 11);
            for (int i = 0; i < count; i++)
            {
                int p = 23 + 22 * i;
                if (p + 22 > b.Length) break;
                if (b[p + 21] != FormatBC7) continue;
                ulong key = BitConverter.ToUInt64(b, p);
                offs[key] = (long)BitConverter.ToUInt64(b, p + 8);
                lens[key] = (int)BitConverter.ToUInt32(b, p + 16);
                codecs[key] = b[p + 20];
                int lv = (int)((key >> 51) & 511);
                if (lv > maxLevel) maxLevel = lv;
            }

            lock (gate)
            {
                a.Offsets = offs; a.Lengths = lens; a.Codecs = codecs;
                a.IdxTime = t;
                if (a == web) MaxWebLevel = maxLevel;
            }
        }

        /// <summary>Mirage dopisuje nowe kafle w trakcie lotu. Indeks sprawdzamy co kilka sekund.</summary>
        public void RefreshLive()
        {
            Archive a = web;
            if (a == null) return;
            float now = Time.realtimeSinceStartup;
            if (now - a.Checked < 5f) return;
            a.Checked = now;
            try
            {
                if (File.GetLastWriteTimeUtc(a.Idx) != a.IdxTime) LoadIndex(a);
            }
            catch (Exception e) { Debug.LogWarning("[OSMRoads] indeks Mirage: " + e.Message); }
        }

        public static ulong Key(int face, int level, int x, int y)
        {
            return ((ulong)face << 60) | ((ulong)level << 51) | Part1By1((uint)x) | (Part1By1((uint)y) << 1);
        }

        private static ulong Part1By1(uint v)
        {
            ulong x = v & 0x1FFFF;
            x = (x | (x << 16)) & 0x0000FFFF0000FFFFUL;
            x = (x | (x << 8)) & 0x00FF00FF00FF00FFUL;
            x = (x | (x << 4)) & 0x0F0F0F0F0F0F0F0FUL;
            x = (x | (x << 2)) & 0x3333333333333333UL;
            x = (x | (x << 1)) & 0x5555555555555555UL;
            return x;
        }

        public bool Has(int face, int level, int x, int y)
        {
            ulong k = Key(face, level, x, y);
            lock (gate)
            {
                if (web != null && web.Offsets.ContainsKey(k)) return true;
                return level < canonical.Count && canonical[level].Offsets.ContainsKey(k);
            }
        }

        /// <summary>Dane BC7 kafla (264x264, wiersz 0 = dol tekstury) albo null.
        /// Najpierw Mirage (ostrzejszy i swiezszy), potem Sol.</summary>
        public byte[] Read(int face, int level, int x, int y)
        {
            ulong k = Key(face, level, x, y);
            lock (gate)
            {
                byte[] hit;
                if (cache.TryGetValue(k, out hit))
                {
                    lru.Remove(k); lru.AddFirst(k);
                    return hit;
                }
            }

            byte[] data = null;
            if (web != null) data = ReadFrom(web, k);
            if (data == null && level < canonical.Count) data = ReadFrom(canonical[level], k);
            if (data == null) return null;

            lock (gate)
            {
                if (!cache.ContainsKey(k))
                {
                    cache[k] = data;
                    lru.AddFirst(k);
                    while (lru.Count > CacheTiles) { cache.Remove(lru.Last.Value); lru.RemoveLast(); }
                }
            }
            return data;
        }

        private byte[] ReadFrom(Archive a, ulong key)
        {
            long off; int len; byte codec;
            lock (gate)
            {
                if (!a.Offsets.TryGetValue(key, out off)) return null;
                len = a.Lengths[key];
                codec = a.Codecs[key];
            }
            if (codec > 1 || len <= 0 || len > RawBytes + 1024) return null;

            try
            {
                byte[] payload = new byte[len];
                byte[] head = new byte[TileHeaderBytes];
                lock (a)
                {
                    if (a.Stream == null)
                        a.Stream = new FileStream(a.Bin, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                    a.Stream.Seek(off, SeekOrigin.Begin);
                    ReadExactly(a.Stream, head);
                    ReadExactly(a.Stream, payload);
                }
                // Naglowek kafla powtarza klucz - chroni przed indeksem nieaktualnym
                // wzgledem pliku, ktory Mirage wlasnie kompaktuje.
                if (BitConverter.ToUInt64(head, 0) != key) { a.Checked = -100f; return null; }

                if (codec == 0) return len == RawBytes ? payload : null;
                byte[] raw = new byte[RawBytes];
                return Lz4.DecodeBlock(payload, raw) == RawBytes ? raw : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[OSMRoads] kafel satelity: " + e.Message);
                lock (a) { if (a.Stream != null) { a.Stream.Dispose(); a.Stream = null; } }
                return null;
            }
        }

        private static void ReadExactly(Stream s, byte[] buf)
        {
            int got = 0;
            while (got < buf.Length)
            {
                int n = s.Read(buf, got, buf.Length - got);
                if (n <= 0) throw new EndOfStreamException();
                got += n;
            }
        }

        // ------------------------------------------------------------------
        //  Wybor kafli pod prostokat mapy
        // ------------------------------------------------------------------

        /// <summary>Poziom szescianu, na ktorym teksel ma mniej wiecej mpp metrow.</summary>
        public static int LevelFor(double metersPerPixel, double bodyRadius, int bias)
        {
            double faceM = bodyRadius * Math.PI * 0.5;
            double l = Math.Log(faceM / (InnerPx * Math.Max(0.01, metersPerPixel)), 2.0);
            return Math.Max(0, (int)Math.Round(l) + bias);
        }

        public struct Patch
        {
            public int Face, Level, X, Y;
            public byte[] Data;
        }

        /// <summary>
        /// Kafle potrzebne do pokrycia prostokata, z danymi. Gdzie brak kafla na
        /// zadanym poziomie, bierze najblizszego przodka - posortowane od
        /// najgrubszych, zeby ostrzejsze rysowaly sie na wierzchu.
        /// </summary>
        public List<Patch> Collect(BBox box, int level, int maxLevel)
        {
            RefreshLiveSafe();
            level = Math.Min(level, maxLevel);
            var want = new HashSet<ulong>();
            const int S = 9;
            for (int j = 0; j < S; j++)
            {
                double lat = box.South + (box.North - box.South) * j / (S - 1);
                for (int i = 0; i < S; i++)
                {
                    double lon = box.West + (box.East - box.West) * i / (S - 1);
                    int f, x, y;
                    CubeMath.LatLonToTile(lat, lon, level, out f, out x, out y);
                    int n = 1 << level;
                    // sasiedzi tej samej sciany: waski skrawek kafla miedzy probkami
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= n || yy >= n) continue;
                            want.Add(Key(f, level, xx, yy));
                        }
                }
            }

            var chosen = new Dictionary<ulong, Patch>();
            foreach (ulong k in want)
            {
                int f = (int)(k >> 60);
                ulong morton = k & ((1UL << 34) - 1);        // bez bitow sciany i poziomu
                int x = (int)Compact1By1(morton), y = (int)Compact1By1(morton >> 1);
                for (int l = level; l >= 0; l--, x >>= 1, y >>= 1)
                {
                    ulong kk = Key(f, l, x, y);
                    if (chosen.ContainsKey(kk)) break;
                    if (!Has(f, l, x, y)) continue;
                    byte[] d = Read(f, l, x, y);
                    if (d == null) continue;
                    chosen[kk] = new Patch { Face = f, Level = l, X = x, Y = y, Data = d };
                    break;
                }
            }

            var list = new List<Patch>(chosen.Values);
            list.Sort((a, b) => a.Level.CompareTo(b.Level));
            return list;
        }

        private void RefreshLiveSafe()
        {
            try { RefreshLive(); } catch { }
        }

        private static ulong Compact1By1(ulong v)
        {
            v &= 0x5555555555555555UL;
            v = (v | (v >> 1)) & 0x3333333333333333UL;
            v = (v | (v >> 2)) & 0x0F0F0F0F0F0F0F0FUL;
            v = (v | (v >> 4)) & 0x00FF00FF00FF00FFUL;
            v = (v | (v >> 8)) & 0x0000FFFF0000FFFFUL;
            v = (v | (v >> 16)) & 0x00000000FFFFFFFFUL;
            return v;
        }
    }

    /// <summary>Geometria szescianu Mirage (MirageCubeMath) - osie scian i obroty UV.</summary>
    public static class CubeMath
    {
        private static readonly double[][] Axis =
        {
            new[] { 1.0, 0, 0 }, new[] { -1.0, 0, 0 }, new[] { 0, 1.0, 0 },
            new[] { 0, -1.0, 0 }, new[] { 0, 0, 1.0 }, new[] { 0, 0, -1.0 }
        };
        private static readonly double[][] U =
        {
            new[] { 0, 1.0, 0 }, new[] { 0, -1.0, 0 }, new[] { -1.0, 0, 0 },
            new[] { -1.0, 0, 0 }, new[] { -1.0, 0, 0 }, new[] { -1.0, 0, 0 }
        };
        private static readonly double[][] V =
        {
            new[] { 0, 0, -1.0 }, new[] { 0, 0, -1.0 }, new[] { 0, 0, -1.0 },
            new[] { 0, 0, 1.0 }, new[] { 0, 1.0, 0 }, new[] { 0, -1.0, 0 }
        };

        public static void LatLonToDir(double lat, double lon, out double x, out double y, out double z)
        {
            double la = lat * Math.PI / 180.0, lo = lon * Math.PI / 180.0;
            double c = Math.Cos(la);
            x = c * Math.Cos(lo);
            y = Math.Sin(la);
            z = c * Math.Sin(lo);
        }

        public static void DirToLatLon(double x, double y, double z, out double lat, out double lon)
        {
            double len = Math.Sqrt(x * x + y * y + z * z);
            lat = Math.Asin(Math.Max(-1.0, Math.Min(1.0, y / len))) * 180.0 / Math.PI;
            lon = Math.Atan2(z, x) * 180.0 / Math.PI;
        }

        /// <summary>Kierunek -> sciana i SKORYGOWANE uv w [0,1] (te, ktore ida na kafle).</summary>
        public static void DirToFaceUV(double x, double y, double z, out int face, out double cu, out double cv)
        {
            double ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z);
            if (ax >= ay && ax >= az) face = x >= 0 ? 0 : 1;
            else if (ay >= az) face = y >= 0 ? 2 : 3;
            else face = z >= 0 ? 4 : 5;

            double[] a = Axis[face], u = U[face], v = V[face];
            double d = x * a[0] + y * a[1] + z * a[2];
            double uu = ((x * u[0] + y * u[1] + z * u[2]) / d + 1.0) * 0.5;
            double vv = ((x * v[0] + y * v[1] + z * v[2]) / d + 1.0) * 0.5;
            Correct(face, uu, vv, out cu, out cv);
        }

        /// <summary>Sciana + skorygowane uv -> kierunek (nieznormalizowany).</summary>
        public static void FaceUVToDir(int face, double cu, double cv, out double x, out double y, out double z)
        {
            double uu, vv;
            Uncorrect(face, cu, cv, out uu, out vv);
            double su = 2.0 * uu - 1.0, sv = 2.0 * vv - 1.0;
            double[] a = Axis[face], u = U[face], v = V[face];
            x = a[0] + su * u[0] + sv * v[0];
            y = a[1] + su * u[1] + sv * v[1];
            z = a[2] + su * u[2] + sv * v[2];
        }

        public static void LatLonToTile(double lat, double lon, int level, out int face, out int tx, out int ty)
        {
            double x, y, z, cu, cv;
            LatLonToDir(lat, lon, out x, out y, out z);
            DirToFaceUV(x, y, z, out face, out cu, out cv);
            int n = 1 << level;
            tx = Math.Max(0, Math.Min(n - 1, (int)Math.Floor(cu * n)));
            ty = Math.Max(0, Math.Min(n - 1, (int)Math.Floor(cv * n)));
        }

        private static void Correct(int face, double u, double v, out double cu, out double cv)
        {
            switch (face)
            {
                case 0: cu = v; cv = 1.0 - u; break;
                case 1: cu = 1.0 - v; cv = u; break;
                case 2: case 3: case 4: cu = 1.0 - u; cv = 1.0 - v; break;
                default: cu = u; cv = v; break;
            }
        }

        private static void Uncorrect(int face, double cu, double cv, out double u, out double v)
        {
            switch (face)
            {
                case 0: u = 1.0 - cv; v = cu; break;
                case 1: u = cv; v = 1.0 - cu; break;
                case 2: case 3: case 4: u = 1.0 - cu; v = 1.0 - cv; break;
                default: u = cu; v = cv; break;
            }
        }
    }

    /// <summary>Dekompresja bloku LZ4 (bez ramki) - tak zapisuje Sol-Textures.</summary>
    public static class Lz4
    {
        public static int DecodeBlock(byte[] src, byte[] dst)
        {
            int ip = 0, op = 0, end = src.Length;
            while (ip < end)
            {
                int token = src[ip++];
                int lit = token >> 4;
                if (lit == 15) { int b; do { b = src[ip++]; lit += b; } while (b == 255); }
                if (op + lit > dst.Length || ip + lit > end) return -1;
                Buffer.BlockCopy(src, ip, dst, op, lit);
                ip += lit; op += lit;
                if (ip >= end) break;                        // ostatnia sekwencja: same literaly

                int offset = src[ip] | (src[ip + 1] << 8);
                ip += 2;
                if (offset == 0 || offset > op) return -1;
                int ml = token & 15;
                if (ml == 15) { int b; do { b = src[ip++]; ml += b; } while (b == 255); }
                ml += 4;
                if (op + ml > dst.Length) return -1;
                int from = op - offset;
                for (int i = 0; i < ml; i++) dst[op++] = dst[from++];   // zachodzace kopie - bajt po bajcie
            }
            return op;
        }
    }
}
