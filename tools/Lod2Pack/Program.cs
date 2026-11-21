using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using OSMRoads;

namespace Lod2Pack
{
    /// <summary>
    /// lod2pack &lt;katalog z .gml (rekurencyjnie)&gt; &lt;katalog wyjsciowy&gt;
    ///
    /// Modele budynkow 3D GUGiK (CityGML 2.0, LoD2, EPSG:2180 + PL-KRON86) -&gt;
    /// pliki b3d_*.bin dla moda. Sciany i dachy sa triangulowane tutaj, raz,
    /// zeby w grze zostalo tylko przeliczenie wspolrzednych.
    /// </summary>
    internal static class Program
    {
        private static int buildings, skipped, tris, fanned;

        private static int Main(string[] args)
        {
            if (args.Length >= 3 && args[0] == "color") return ColorCommand.Run(args);
            if (args.Length < 2) { Console.WriteLine("lod2pack <gml-dir> <out-dir>   |   lod2pack color <b3d-dir> <tile-cache> [--dry]"); return 1; }
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var cells = new Dictionary<long, List<Lod2Building>>();
            string[] files = Directory.GetFiles(args[0], "*.gml", SearchOption.AllDirectories);
            int fi = 0;
            foreach (string f in files)
            {
                foreach (Lod2Building b in ReadGml(f))
                {
                    int iy, ix;
                    PackFormat.CellOf(b.Lat, b.Lon, out iy, out ix);
                    long k = PackFormat.CellKey(iy, ix);
                    List<Lod2Building> l;
                    if (!cells.TryGetValue(k, out l)) cells[k] = l = new List<Lod2Building>();
                    l.Add(b);
                }
                if (++fi % 25 == 0) Console.WriteLine("  {0}/{1} plikow, {2:N0} budynkow, {3:F0} s", fi, files.Length, buildings, sw.Elapsed.TotalSeconds);
            }

            Directory.CreateDirectory(args[1]);
            // grupowanie komorek w pliki per kwadrat 1 st.
            var bySquare = new Dictionary<string, List<KeyValuePair<long, List<Lod2Building>>>>();
            foreach (var kv in cells)
            {
                Lod2Building first = kv.Value[0];
                string name = Lod2Format.SquareOf(first.Lat, first.Lon);
                List<KeyValuePair<long, List<Lod2Building>>> l;
                if (!bySquare.TryGetValue(name, out l)) bySquare[name] = l = new List<KeyValuePair<long, List<Lod2Building>>>();
                l.Add(kv);
            }
            foreach (var sq in bySquare) WriteSquare(Path.Combine(args[1], sq.Key), sq.Value);

            Console.WriteLine("gotowe: {0:N0} budynkow, {1:N0} trojkatow, pominietych {2:N0}, {3} komorek, {4:F0} s",
                              buildings, tris, skipped, cells.Count, sw.Elapsed.TotalSeconds);
            return 0;
        }

        /// <summary>Scala z istniejacym plikiem (kolejne powiaty dopisuja sie do tego samego kwadratu).</summary>
        private static void WriteSquare(string path, List<KeyValuePair<long, List<Lod2Building>>> cells)
        {
            var all = new Dictionary<long, List<Lod2Building>>();
            if (File.Exists(path))
                foreach (var kv in ReadSquare(path)) all[kv.Key] = kv.Value;
            foreach (var kv in cells)
            {
                List<Lod2Building> l;
                if (!all.TryGetValue(kv.Key, out l)) all[kv.Key] = l = new List<Lod2Building>();
                l.AddRange(kv.Value);
            }

            var blocks = new List<KeyValuePair<long, byte[]>>();
            var raws = new List<int>();
            foreach (var kv in all.OrderBy(k => k.Key))
            {
                var ms = new MemoryStream();
                using (var w = new BinaryWriter(ms))
                {
                    PackFormat.WriteVarInt(w, kv.Value.Count);
                    foreach (Lod2Building b in kv.Value) Lod2Format.Write(w, b);
                }
                byte[] raw = ms.ToArray();
                raws.Add(raw.Length);
                blocks.Add(new KeyValuePair<long, byte[]>(kv.Key, PackFormat.Deflate(raw)));
            }

            string tmp = path + ".part";
            using (var w = new BinaryWriter(File.Create(tmp)))
            {
                w.Write(Lod2Format.Magic); w.Write(Lod2Format.Version); w.Write(blocks.Count);
                long off = 12 + blocks.Count * 24L;
                for (int i = 0; i < blocks.Count; i++)
                {
                    long k = blocks[i].Key;
                    w.Write((int)(k >> 32)); w.Write((int)k);
                    w.Write(off); w.Write(blocks[i].Value.Length); w.Write(raws[i]);
                    off += blocks[i].Value.Length;
                }
                foreach (var b in blocks) w.Write(b.Value);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            Console.WriteLine("  {0}: {1} komorek, {2:N1} MB", Path.GetFileName(path), blocks.Count, new FileInfo(path).Length / 1048576.0);
        }

        public static IEnumerable<KeyValuePair<long, List<Lod2Building>>> ReadSquarePublic(string path) { return ReadSquare(path); }

        /// <summary>Nadpisuje plik tymi komorkami (bez scalania ze stara zawartoscia).</summary>
        public static void WriteSquareReplace(string path, List<KeyValuePair<long, List<Lod2Building>>> cells)
        {
            if (File.Exists(path)) File.Delete(path);
            WriteSquare(path, cells);
        }

        private static IEnumerable<KeyValuePair<long, List<Lod2Building>>> ReadSquare(string path)
        {
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (r.ReadUInt32() != Lod2Format.Magic) yield break;
                r.ReadInt32();
                int n = r.ReadInt32();
                var idx = new List<long[]>();
                for (int i = 0; i < n; i++)
                    idx.Add(new long[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt64(), r.ReadInt32(), r.ReadInt32() });
                foreach (long[] e in idx)
                {
                    r.BaseStream.Seek(e[2], SeekOrigin.Begin);
                    byte[] raw = PackFormat.Inflate(r.ReadBytes((int)e[3]), (int)e[4]);
                    var list = new List<Lod2Building>();
                    using (var br = new BinaryReader(new MemoryStream(raw)))
                    {
                        int c = (int)PackFormat.ReadVarInt(br);
                        for (int i = 0; i < c; i++) list.Add(Lod2Format.Read(br));
                    }
                    yield return new KeyValuePair<long, List<Lod2Building>>(PackFormat.CellKey((int)e[0], (int)e[1]), list);
                }
            }
        }

        // ------------------------------------------------------------------
        //  CityGML
        // ------------------------------------------------------------------

        private sealed class Surf { public string Kind; public List<double[]> Ring = new List<double[]>(); }

        private static IEnumerable<Lod2Building> ReadGml(string path)
        {
            var settings = new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true, DtdProcessing = DtdProcessing.Ignore };
            using (XmlReader x = XmlReader.Create(path, settings))
            {
                while (x.Read())
                {
                    if (x.NodeType != XmlNodeType.Element || x.LocalName != "Building") continue;
                    Lod2Building b = null;
                    using (XmlReader sub = x.ReadSubtree())
                    {
                        try { b = ParseBuilding(sub); }
                        catch (Exception e) { skipped++; Console.WriteLine("  blad budynku w " + Path.GetFileName(path) + ": " + e.Message); }
                    }
                    if (b != null) { buildings++; yield return b; } else skipped++;
                }
            }
        }

        private static Lod2Building ParseBuilding(XmlReader x)
        {
            var surfs = new List<Surf>();
            ushort roofType = 0;
            Surf cur = null;
            // ReadElementContentAsString i Skip zostawiaja czytnik JUZ na nastepnym wezle -
            // wtedy nie wolno wolac Read(), bo przeskoczylby co drugi punkt.
            bool advance = true;
            while (advance ? x.Read() : !x.EOF)
            {
                advance = true;
                if (x.NodeType == XmlNodeType.Element)
                {
                    string ln = x.LocalName;
                    if (ln == "roofType") { ushort.TryParse(x.ReadElementContentAsString(), out roofType); advance = false; continue; }
                    if (ln == "GroundSurface" || ln == "WallSurface" || ln == "RoofSurface" || ln == "OuterFloorSurface" || ln == "ClosureSurface")
                        cur = new Surf { Kind = ln };
                    if (ln == "Polygon" && cur != null && cur.Ring.Count > 0)
                    {
                        // kolejny wielokat tej samej powierzchni - osobna pozycja
                        surfs.Add(cur);
                        cur = new Surf { Kind = cur.Kind };
                    }
                    if (ln == "interior") { x.Skip(); advance = false; continue; }            // otwory w scianach - pomijamy
                    if ((ln == "pos" || ln == "posList") && cur != null)
                    {
                        string t = x.ReadElementContentAsString();
                        string[] p = t.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i + 2 < p.Length; i += 3)
                            cur.Ring.Add(new[] { double.Parse(p[i], CultureInfo.InvariantCulture),
                                                 double.Parse(p[i + 1], CultureInfo.InvariantCulture),
                                                 double.Parse(p[i + 2], CultureInfo.InvariantCulture) });
                        advance = false;
                        continue;
                    }
                }
                else if (x.NodeType == XmlNodeType.EndElement)
                {
                    string ln = x.LocalName;
                    if ((ln == "GroundSurface" || ln == "WallSurface" || ln == "RoofSurface" || ln == "OuterFloorSurface" || ln == "ClosureSurface") && cur != null)
                    {
                        if (cur.Ring.Count >= 3) surfs.Add(cur);
                        cur = null;
                    }
                }
            }
            return Build(surfs, roofType);
        }

        private static Lod2Building Build(List<Surf> surfs, ushort roofType)
        {
            List<Surf> ground = surfs.Where(s => s.Kind == "GroundSurface").ToList();
            if (ground.Count == 0) return null;

            double baseZ = double.MaxValue;
            foreach (Surf s in surfs) foreach (double[] p in s.Ring) baseZ = Math.Min(baseZ, p[2]);

            // kotwica: srodek najwiekszej podstawy
            Surf gmain = ground.OrderByDescending(s => Math.Abs(Area2D(s.Ring))).First();
            double ce = gmain.Ring.Average(p => p[0]), cn = gmain.Ring.Average(p => p[1]);
            var b = new Lod2Building { RoofType = roofType };
            Puwg1992.ToLatLon(ce, cn, out b.Lat, out b.Lon);

            // wierzcholki bez duplikatow (GML powtarza te same punkty w kazdej scianie)
            var index = new Dictionary<long, int>();
            var vE = new List<double>(); var vN = new List<double>(); var vZ = new List<double>();
            Func<double[], int> vid = p =>
            {
                long k = ((long)Math.Round(p[0] * 100) * 73856093L) ^ ((long)Math.Round(p[1] * 100) * 19349663L) ^ ((long)Math.Round(p[2] * 100) * 83492791L);
                int id;
                if (index.TryGetValue(k, out id)) return id;
                id = vE.Count; index[k] = id;
                vE.Add(p[0]); vN.Add(p[1]); vZ.Add(p[2]);
                return id;
            };

            var walls = new List<int>(); var roofs = new List<int>();
            foreach (Surf s in surfs)
            {
                if (s.Kind == "GroundSurface" || s.Kind == "ClosureSurface") continue;
                List<double[]> ring = Clean(s.Ring);
                if (ring.Count < 3) continue;
                List<int> t = Triangulate(ring);
                if (t == null) continue;
                List<int> into = s.Kind == "RoofSurface" || s.Kind == "OuterFloorSurface" ? roofs : walls;
                foreach (int i in t) into.Add(vid(ring[i]));
            }
            if (walls.Count + roofs.Count == 0) return null;

            int n = vE.Count;
            b.VLat = new double[n]; b.VLon = new double[n]; b.VH = new float[n];
            for (int i = 0; i < n; i++)
            {
                Puwg1992.ToLatLon(vE[i], vN[i], out b.VLat[i], out b.VLon[i]);
                b.VH[i] = (float)(vZ[i] - baseZ);
            }
            b.Walls = walls.ToArray();
            b.Roofs = roofs.ToArray();
            tris += (walls.Count + roofs.Count) / 3;

            List<double[]> foot = Clean(gmain.Ring);
            b.FootLat = new double[foot.Count]; b.FootLon = new double[foot.Count];
            for (int i = 0; i < foot.Count; i++) Puwg1992.ToLatLon(foot[i][0], foot[i][1], out b.FootLat[i], out b.FootLon[i]);
            return b;
        }

        /// <summary>Bez zamykajacego punktu i bez punktow blizszych niz 1 cm.</summary>
        private static List<double[]> Clean(List<double[]> ring)
        {
            var o = new List<double[]>();
            foreach (double[] p in ring)
            {
                if (o.Count > 0 && Dist(o[o.Count - 1], p) < 0.01) continue;
                o.Add(p);
            }
            while (o.Count > 1 && Dist(o[0], o[o.Count - 1]) < 0.01) o.RemoveAt(o.Count - 1);
            return o;
        }

        private static double Dist(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static double Area2D(List<double[]> r)
        {
            double a = 0;
            for (int i = 0, j = r.Count - 1; i < r.Count; j = i++) a += r[j][0] * r[i][1] - r[i][0] * r[j][1];
            return a * 0.5;
        }

        /// <summary>Ear clipping plaskiego wielokata 3D: rzut na plaszczyzne, w ktorej
        /// normalna ma najwieksza skladowa. Kolejnosc wierzcholkow zachowana (normalna jak w GML).</summary>
        private static List<int> Triangulate(List<double[]> ring)
        {
            int n = ring.Count;
            // normalna Newella
            double nx = 0, ny = 0, nz = 0;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double[] a = ring[j], b = ring[i];
                nx += (a[1] - b[1]) * (a[2] + b[2]);
                ny += (a[2] - b[2]) * (a[0] + b[0]);
                nz += (a[0] - b[0]) * (a[1] + b[1]);
            }
            int ax = Math.Abs(nx) >= Math.Abs(ny) && Math.Abs(nx) >= Math.Abs(nz) ? 0 : Math.Abs(ny) >= Math.Abs(nz) ? 1 : 2;
            int u = ax == 0 ? 1 : 0, v = ax == 2 ? 1 : 2;
            double sign = ax == 0 ? nx : ax == 1 ? ny : nz;
            if (Math.Abs(sign) < 1e-9) return null;
            // przy rzucie wzdluz osi Y uklad (Z, X) jest prawoskretny - (X, Z) odwraca orientacje
            if (ax == 1) sign = -sign;

            var pts = new double[n, 2];
            for (int i = 0; i < n; i++) { pts[i, 0] = ring[i][u]; pts[i, 1] = ring[i][v]; }
            var idx = Enumerable.Range(0, n).ToList();
            var outp = new List<int>();
            int guard = 0;
            while (idx.Count > 3 && guard++ < n * n)
            {
                bool cut = false;
                for (int k = 0; k < idx.Count; k++)
                {
                    int i0 = idx[(k + idx.Count - 1) % idx.Count], i1 = idx[k], i2 = idx[(k + 1) % idx.Count];
                    double cr = Cross(pts, i0, i1, i2);
                    if (cr * sign <= 1e-12) continue;                 // wklesly rog
                    bool inside = false;
                    foreach (int q in idx)
                    {
                        if (q == i0 || q == i1 || q == i2) continue;
                        if (InTri(pts, q, i0, i1, i2, sign)) { inside = true; break; }
                    }
                    if (inside) continue;
                    outp.Add(i0); outp.Add(i1); outp.Add(i2);
                    idx.RemoveAt(k);
                    cut = true;
                    break;
                }
                if (!cut) break;
            }
            if (idx.Count == 3) { outp.Add(idx[0]); outp.Add(idx[1]); outp.Add(idx[2]); }
            else if (idx.Count > 3)
            {
                // nie dalo sie domknac (wielokat lekko nieplaski / samoprzeciecie) - reszta wachlarzem,
                // zeby w scianie nie zostala dziura
                fanned++;
                for (int k = 1; k + 1 < idx.Count; k++) { outp.Add(idx[0]); outp.Add(idx[k]); outp.Add(idx[k + 1]); }
            }
            return outp.Count >= 3 ? outp : null;
        }

        private static double Cross(double[,] p, int a, int b, int c)
        {
            return (p[b, 0] - p[a, 0]) * (p[c, 1] - p[a, 1]) - (p[b, 1] - p[a, 1]) * (p[c, 0] - p[a, 0]);
        }

        private static bool InTri(double[,] p, int q, int a, int b, int c, double sign)
        {
            double d1 = Cross(p, a, b, q) * sign, d2 = Cross(p, b, c, q) * sign, d3 = Cross(p, c, a, q) * sign;
            return d1 >= 0 && d2 >= 0 && d3 >= 0;
        }
    }
}
