using System;
using System.Collections.Generic;
using System.IO;
using OSMRoads;

namespace PlanetPack
{
    /// <summary>
    /// Linie-czlonkowie multipoligonow w jednym przebiegu.
    ///
    /// W trakcie przebiegu kazda taka linia (z rozwiazanymi wspolrzednymi) idzie do
    /// pliku relcap.bin - nie do RAM, bo magazyn wezlow zajmuje wtedy prawie caly
    /// budzet. Po przebiegu, gdy wezly sa juz zwolnione, plik wraca do pamieci,
    /// linie sklejaja sie w pierscienie po wspolnych wezlach koncowych i kazdy
    /// zamkniety pierscien trafia do zrzutu jak zwykly obszar (dziury jako Clearing).
    /// </summary>
    internal sealed class RelCapture
    {
        private readonly long[] members;          // posortowane, unikalne
        private readonly string path;
        private readonly object lk = new object();
        private BinaryWriter w;
        private long captured;

        public RelCapture(List<RelInfo> rels, string path)
        {
            var all = new List<long>();
            foreach (RelInfo r in rels) all.AddRange(r.Ways);
            all.Sort();
            int n = 0;
            for (int i = 0; i < all.Count; i++)
                if (i == 0 || all[i] != all[i - 1]) all[n++] = all[i];
            members = new long[n];
            all.CopyTo(0, members, 0, n);
            this.path = path;
            w = new BinaryWriter(new BufferedStream(File.Create(path), 1 << 20));
        }

        public int MemberCount { get { return members.Length; } }
        public long Captured { get { return captured; } }

        public bool IsMember(long id) { return Array.BinarySearch(members, id) >= 0; }

        public void Capture(long id, long first, long last, int n, int[] lat, int[] lon)
        {
            lock (lk)
            {
                w.Write(id); w.Write(first); w.Write(last); w.Write(n);
                for (int i = 0; i < n; i++) { w.Write(lat[i]); w.Write(lon[i]); }
                captured++;
            }
        }

        private sealed class Geom
        {
            public long A, B;
            public int[] Lat, Lon;
        }

        /// <summary>Sklada pierscienie wszystkich relacji z przechwyconych linii i zapisuje je do zrzutu.</summary>
        public void Assemble(List<RelInfo> rels, Spill spill, BatchSpec spec, PassStats st, Action<string> log)
        {
            lock (lk) { w.Flush(); w.Dispose(); w = null; }

            var geoms = new Dictionary<long, Geom>();
            using (var br = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 20)))
            {
                long len = br.BaseStream.Length;
                while (br.BaseStream.Position < len)
                {
                    long id = br.ReadInt64();
                    var g = new Geom { A = br.ReadInt64(), B = br.ReadInt64() };
                    int n = br.ReadInt32();
                    g.Lat = new int[n]; g.Lon = new int[n];
                    for (int i = 0; i < n; i++) { g.Lat[i] = br.ReadInt32(); g.Lon[i] = br.ReadInt32(); }
                    geoms[id] = g;
                }
            }

            var ctx = new DecodeContext();
            long rings = 0, holes = 0, relsUsed = 0;
            var outer = new List<Geom>();
            var inner = new List<Geom>();
            foreach (RelInfo r in rels)
            {
                outer.Clear(); inner.Clear();
                for (int k = 0; k < r.Ways.Length; k++)
                {
                    Geom g;
                    if (!geoms.TryGetValue(r.Ways[k], out g)) continue;
                    (r.Inner[k] ? inner : outer).Add(g);
                }
                if (outer.Count == 0) continue;

                int idx = 0;
                bool any = false;
                foreach (List<int[]> ring in Rings(outer))
                {
                    if (idx >= 32) break;
                    if (EmitRing(ctx, spill, spec, st, -(r.Id * 64 + idx), ring, r.Attrs)) { rings++; any = true; }
                    idx++;
                }
                if (!any) continue;
                relsUsed++;

                // dziury tylko tam, gdzie cos rosnie - polana w lesie, staw w parku
                if (r.Attrs.Kind == PackFormat.KindArea && AreaKinds.TreeSpacing((AreaKind)r.Attrs.Area) > 0f && inner.Count > 0)
                {
                    var hole = new RecAttrs { Kind = PackFormat.KindArea, Area = (sbyte)AreaKind.Clearing };
                    int hi = 0;
                    foreach (List<int[]> ring in Rings(inner))
                    {
                        if (hi >= 32) break;
                        if (EmitRing(ctx, spill, spec, st, -(r.Id * 64 + 32 + hi), ring, hole)) holes++;
                        hi++;
                    }
                }
            }
            log(string.Format("  multipoligony: linii {0:N0}, relacji {1:N0}, pierscieni {2:N0}, dziur {3:N0}",
                              geoms.Count, relsUsed, rings, holes));
            try { File.Delete(path); } catch { }
        }

        private static bool EmitRing(DecodeContext c, Spill spill, BatchSpec spec, PassStats st, long id,
                                     List<int[]> pts, RecAttrs a)
        {
            int n = pts.Count;
            if (n < 4) return false;
            if (c.PtLat.Length < n)
            {
                int cap = c.PtLat.Length;
                while (cap < n) cap *= 2;
                c.PtLat = new int[cap];
                c.PtLon = new int[cap];
                c.Refs = new long[cap];
            }
            for (int i = 0; i < n; i++) { c.PtLat[i] = pts[i][0]; c.PtLon[i] = pts[i][1]; }
            return BlockDecoder.Emit(c, spill, spec, st, id, n, a);
        }

        /// <summary>Sklejanie linii w zamkniete pierscienie po wezlach koncowych.
        /// Linie, z ktorych nie da sie zamknac pierscienia (czesc relacji poza przebiegiem), odpadaja.</summary>
        private static IEnumerable<List<int[]>> Rings(List<Geom> parts)
        {
            var used = new bool[parts.Count];
            var byEnd = new Dictionary<long, List<int>>();
            for (int i = 0; i < parts.Count; i++)
            {
                Add(byEnd, parts[i].A, i);
                Add(byEnd, parts[i].B, i);
            }

            for (int s = 0; s < parts.Count; s++)
            {
                if (used[s]) continue;
                used[s] = true;
                var ring = new List<int[]>();
                Append(ring, parts[s], false);
                long start = parts[s].A, end = parts[s].B;
                int guard = parts.Count + 1;
                while (end != start && guard-- > 0)
                {
                    List<int> cand;
                    int pick = -1;
                    bool rev = false;
                    if (byEnd.TryGetValue(end, out cand))
                        foreach (int k in cand)
                        {
                            if (used[k]) continue;
                            pick = k;
                            rev = parts[k].B == end && parts[k].A != end;
                            break;
                        }
                    if (pick < 0) break;
                    used[pick] = true;
                    Append(ring, parts[pick], rev);
                    end = rev ? parts[pick].A : parts[pick].B;
                }
                if (end == start && ring.Count >= 4) yield return ring;
            }
        }

        private static void Add(Dictionary<long, List<int>> d, long key, int i)
        {
            List<int> l;
            if (!d.TryGetValue(key, out l)) d[key] = l = new List<int>(2);
            l.Add(i);
        }

        private static void Append(List<int[]> ring, Geom g, bool reverse)
        {
            int n = g.Lat.Length;
            for (int k = 0; k < n; k++)
            {
                int i = reverse ? n - 1 - k : k;
                if (ring.Count > 0 && k == 0)
                {
                    int[] last = ring[ring.Count - 1];
                    if (last[0] == g.Lat[i] && last[1] == g.Lon[i]) continue;   // wspolny wezel
                }
                ring.Add(new[] { g.Lat[i], g.Lon[i] });
            }
        }
    }
}
