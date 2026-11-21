using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Odczyt budynkow 3D GUGiK (pliki b3d_*.bin) - te same katalogi i ten sam serwer
    /// co paczki OSM (LocalStore.FindShared), plik na kwadrat 1 st., w nim komorki 0.05 st.
    /// Wywolywane z watku TileJob.
    /// </summary>
    public static class Lod2Store
    {
        public static bool Enabled = true;

        private sealed class Square
        {
            public string Path;
            public Dictionary<long, long[]> Index;     // klucz komorki -> offset, dlugosc, surowa
            public float TriedAt;
        }

        private static readonly object gate = new object();
        private static readonly Dictionary<string, Square> squares = new Dictionary<string, Square>();
        private static readonly Dictionary<long, List<Lod2Building>> cellCache = new Dictionary<long, List<Lod2Building>>();
        private static readonly LinkedList<long> lru = new LinkedList<long>();
        private const int MaxCells = 48;

        public static int Loaded;

        public static List<Lod2Building> Read(BBox box)
        {
            var result = new List<Lod2Building>();
            if (!Enabled) return result;

            int y0, x0, y1, x1;
            PackFormat.CellOf(box.South, box.West, out y0, out x0);
            PackFormat.CellOf(box.North, box.East, out y1, out x1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double lat = (y + 0.5) * PackFormat.CellDeg, lon = (x + 0.5) * PackFormat.CellDeg;
                    Square sq = SquareFor(Lod2Format.SquareOf(lat, lon));
                    if (sq == null) continue;
                    List<Lod2Building> cell = Cell(sq, PackFormat.CellKey(y, x));
                    if (cell == null) continue;
                    foreach (Lod2Building b in cell)
                        if (b.Lat >= box.South && b.Lat <= box.North && b.Lon >= box.West && b.Lon <= box.East)
                            result.Add(b);
                }
            return result;
        }

        private static Square SquareFor(string name)
        {
            Square sq;
            lock (gate)
            {
                if (squares.TryGetValue(name, out sq))
                {
                    if (sq.Index != null) return sq;
                    if (Time.realtimeSinceStartup - sq.TriedAt < 300f) return null;   // brak pliku - nie pytamy co kafel
                }
            }

            sq = new Square { TriedAt = Time.realtimeSinceStartup };
            try
            {
                string path = LocalStore.FindShared(name);
                if (path != null)
                {
                    using (var r = new BinaryReader(File.OpenRead(path)))
                    {
                        if (r.ReadUInt32() == Lod2Format.Magic)
                        {
                            r.ReadInt32();
                            int n = r.ReadInt32();
                            var idx = new Dictionary<long, long[]>(n);
                            for (int i = 0; i < n; i++)
                            {
                                int iy = r.ReadInt32(), ix = r.ReadInt32();
                                idx[PackFormat.CellKey(iy, ix)] = new long[] { r.ReadInt64(), r.ReadInt32(), r.ReadInt32() };
                            }
                            sq.Index = idx;
                            sq.Path = path;
                            Debug.Log("[OSMRoads] 3D buildings: " + name + ", " + n + " cells");
                        }
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[OSMRoads] " + name + ": " + e.Message); }

            lock (gate) squares[name] = sq;
            return sq.Index != null ? sq : null;
        }

        private static List<Lod2Building> Cell(Square sq, long key)
        {
            long[] e;
            if (!sq.Index.TryGetValue(key, out e)) return null;
            lock (gate)
            {
                List<Lod2Building> hit;
                if (cellCache.TryGetValue(key, out hit)) { lru.Remove(key); lru.AddFirst(key); return hit; }
            }

            var list = new List<Lod2Building>();
            try
            {
                byte[] packed;
                using (var fs = File.OpenRead(sq.Path))
                {
                    fs.Seek(e[0], SeekOrigin.Begin);
                    packed = new byte[e[1]];
                    int got = 0;
                    while (got < packed.Length) { int k = fs.Read(packed, got, packed.Length - got); if (k <= 0) break; got += k; }
                }
                byte[] raw = PackFormat.Inflate(packed, (int)e[2]);
                using (var r = new BinaryReader(new MemoryStream(raw)))
                {
                    int c = (int)PackFormat.ReadVarInt(r);
                    for (int i = 0; i < c; i++) list.Add(Lod2Format.Read(r));
                }
            }
            catch (Exception ex) { Debug.LogWarning("[OSMRoads] 3D buildings cell: " + ex.Message); return null; }

            lock (gate)
            {
                cellCache[key] = list;
                lru.AddFirst(key);
                while (lru.Count > MaxCells) { cellCache.Remove(lru.Last.Value); lru.RemoveLast(); }
                Loaded += list.Count;
            }
            return list;
        }

        public static void Clear()
        {
            lock (gate) { squares.Clear(); cellCache.Clear(); lru.Clear(); }
        }
    }
}
