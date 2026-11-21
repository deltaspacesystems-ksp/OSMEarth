using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OSMRoads;

namespace PlacesIndex
{
    /// <summary>
    ///   placesindex &lt;katalog paczek&gt; [--threads N]
    ///
    /// Przechodzi wszystkie komorki wszystkich paczek i zbiera punkty place.
    /// Zapisuje w katalogu paczek:
    ///   places.idx          - tylko city/town calego swiata (mala, laduje sie w calosci),
    ///   pl_&lt;kwadrat&gt;.idx    - WSZYSTKIE miejsca (city/town/suburb/village/hamlet) jednego
    ///                         kwadratu 10x10 st., tak jak paczka p_&lt;kwadrat&gt;. Wyszukiwarka
    ///                         doczytuje je dla kwadratow wokol widoku.
    /// Paczki czyta kazda swoim strumieniem, wiec po sieci idzie jeden plik naraz na watek.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 1) { Console.WriteLine("placesindex <katalog paczek> [--threads N]"); return 1; }
            string dir = args[0];
            int threads = 6;
            for (int i = 1; i + 1 < args.Length; i++) if (args[i] == "--threads") threads = int.Parse(args[i + 1]);

            string[] idxs = Directory.GetFiles(dir, "p_*.idx");
            Array.Sort(idxs, StringComparer.Ordinal);
            Console.WriteLine("{0} paczek, {1} watkow", idxs.Length, threads);

            var all = new List<WorldPlaces.Place>();
            var seen = new HashSet<long>();
            long cells = 0, bytes = 0, objects = 0;
            int done = 0;
            var sw = Stopwatch.StartNew();
            var gate = new object();

            Parallel.ForEach(idxs, new ParallelOptions { MaxDegreeOfParallelism = threads }, idx =>
            {
                var found = new List<KeyValuePair<long, WorldPlaces.Place>>();
                var roads = new List<RoadRec>();
                long c = 0, b = 0, o = 0;
                try { ScanPack(idx, found, roads, ref c, ref b, ref o); }
                catch (Exception e) { Console.WriteLine("  {0}: {1}", Path.GetFileName(idx), e.Message); }

                // Plik tego kwadratu: wszystkie rangi, najwazniejsze pierwsze.
                try { WriteSquare(dir, idx, found); }
                catch (Exception e) { Console.WriteLine("  pl_ dla {0}: {1}", Path.GetFileName(idx), e.Message); }

                // zestaw przegladowy dr\u00f3g (ro_<kwadrat>.idx) - z tego samego przebiegu po paczce
                try { WriteOverview(dir, idx, roads); }
                catch (Exception e) { Console.WriteLine("  ro_ dla {0}: {1}", Path.GetFileName(idx), e.Message); }

                lock (gate)
                {
                    foreach (var kv in found)
                        if (kv.Value.Rank <= 1 && seen.Add(kv.Key)) all.Add(kv.Value);
                    cells += c; bytes += b; objects += o; done++;
                    if (done % 10 == 0 || done == idxs.Length)
                        Console.WriteLine("  {0}/{1} paczek  {2:N0} komorek  {3:N1} GB  {4:N0} obiektow  {5:N0} miast  {6:N0} s",
                                          done, idxs.Length, cells, bytes / 1073741824.0, objects, all.Count, sw.Elapsed.TotalSeconds);
                }
            });

            // Najwazniejsze pierwsze: przy nakladaniu sie etykiet wygrywa to, co wczesniej.
            all.Sort((a, x) => a.Rank != x.Rank ? a.Rank.CompareTo(x.Rank) : string.CompareOrdinal(a.Name, x.Name));
            string outPath = Path.Combine(dir, WorldPlaces.FileName);
            WorldPlaces.Write(outPath, all);
            int cities = 0; foreach (var p in all) if (p.Rank == 0) cities++;
            Console.WriteLine("zapisano {0}: {1:N0} city, {2:N0} town, {3:N1} MB, {4:N0} s",
                              outPath, cities, all.Count - cities, new FileInfo(outPath).Length / 1048576.0, sw.Elapsed.TotalSeconds);
            return 0;
        }

        /// <summary>
        /// ro_&lt;kwadrat&gt;.idx: autostrady, ekspresowe, krajowe i wojewodzkie jednego kwadratu 10x10 st., uproszczone
        /// (Douglas-Peucker: 40 m dla klas 0-1, 30 m dla krajowych, 25 m dla wojewodzkich). Format: "ROV1", liczba drog,
        /// a dla kazdej: klasa (bajt), liczba punktow, pierwszy punkt bezwzglednie i dalej delty (zygzak+varint, 1e-5 st.).
        /// Czytane przez wtyczke, zeby w widoku swiata i przy duzych skalach byly widoczne drogi bez czytania calych paczek.
        /// </summary>
        private static void WriteOverview(string dir, string idxPath, List<RoadRec> roads)
        {
            string name = Path.GetFileNameWithoutExtension(idxPath);
            if (!name.StartsWith("p_")) return;
            string outPath = Path.Combine(dir, "ro_" + name.Substring(2) + ".idx");
            if (roads.Count == 0) { if (File.Exists(outPath)) File.Delete(outPath); return; }

            string tmp = outPath + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp)))
            {
                w.Write((byte)0x52); w.Write((byte)0x4F); w.Write((byte)0x56); w.Write((byte)0x31);       // "ROV1"
                w.Write(roads.Count);
                foreach (RoadRec r in roads)
                {
                    double tol = r.Cls <= 1 ? 36.0 : r.Cls == 2 ? 27.0 : 22.0;
                    List<int> s = Simplify(r.Pts, tol);
                    int n = s.Count / 2;
                    w.Write(r.Cls);
                    PackFormat.WriteVarInt(w, n);
                    long pl = 0, po = 0;
                    for (int i = 0; i < n; i++)
                    {
                        PackFormat.WriteVarInt(w, s[2 * i] - pl); PackFormat.WriteVarInt(w, s[2 * i + 1] - po);
                        pl = s[2 * i]; po = s[2 * i + 1];
                    }
                }
            }
            if (File.Exists(outPath)) File.Delete(outPath);
            File.Move(tmp, outPath);
        }

        /// <summary>Douglas-Peucker na tablicy lat/lon w 1e-5 st.; dlugosc geograficzna skalowana cosinusem szerokosci.</summary>
        private static List<int> Simplify(int[] pts, double tol)
        {
            int n = pts.Length / 2;
            var res = new List<int>();
            if (n <= 2) { res.AddRange(pts); return res; }
            double kx = Math.Cos(pts[n] * 1e-5 * Math.PI / 180.0);        // szerokosc srodkowego punktu
            var keep = new bool[n];
            keep[0] = keep[n - 1] = true;
            var stack = new Stack<int>();
            stack.Push(0); stack.Push(n - 1);
            while (stack.Count > 0)
            {
                int b = stack.Pop(), a = stack.Pop();
                if (b <= a + 1) continue;
                double ax = pts[2 * a + 1] * kx, ay = pts[2 * a], bx = pts[2 * b + 1] * kx, by = pts[2 * b];
                double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy, max = -1;
                int at = -1;
                for (int i = a + 1; i < b; i++)
                {
                    double px = pts[2 * i + 1] * kx, py = pts[2 * i], d;
                    if (len2 < 1e-9) d = Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
                    else
                    {
                        double u = Math.Max(0.0, Math.Min(1.0, ((px - ax) * dx + (py - ay) * dy) / len2));
                        double qx = ax + u * dx, qy = ay + u * dy;
                        d = Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
                    }
                    if (d > max) { max = d; at = i; }
                }
                if (max > tol) { keep[at] = true; stack.Push(a); stack.Push(at); stack.Push(at); stack.Push(b); }
            }
            for (int i = 0; i < n; i++) if (keep[i]) { res.Add(pts[2 * i]); res.Add(pts[2 * i + 1]); }
            return res;
        }

        private static void WriteSquare(string dir, string idxPath, List<KeyValuePair<long, WorldPlaces.Place>> found)
        {
            string name = Path.GetFileNameWithoutExtension(idxPath);            // p_n50e010
            if (!name.StartsWith("p_")) return;
            string outPath = Path.Combine(dir, "pl_" + name.Substring(2) + ".idx");

            var list = new List<WorldPlaces.Place>(found.Count);
            var seenIds = new HashSet<long>();
            foreach (var kv in found) if (seenIds.Add(kv.Key)) list.Add(kv.Value);
            list.Sort((a, x) => a.Rank != x.Rank ? a.Rank.CompareTo(x.Rank) : string.CompareOrdinal(a.Name, x.Name));

            if (list.Count == 0) { if (File.Exists(outPath)) File.Delete(outPath); return; }
            WorldPlaces.Write(outPath, list);
        }

        /// <summary>Droga z paczki do zestawu przeglado wego: klasa 0 autostrada, 1 ekspresowa, 2 krajowa, 3 wojewodzka.</summary>
        private sealed class RoadRec { public byte Cls; public int[] Pts; }      // wspolrzedne w 1e-5 stopnia, lat/lon na przemian

        private static sbyte ClassOf(string highway)
        {
            switch (highway)
            {
                case "motorway": return 0;
                case "trunk": return 1;
                case "primary": return 2;
                case "secondary": return 3;
                default: return -1;
            }
        }

        private static void ScanPack(string idxPath, List<KeyValuePair<long, WorldPlaces.Place>> found, List<RoadRec> roads,
                                     ref long cells, ref long bytes, ref long objects)
        {
            string[] strings;
            var entries = new List<long[]>();
            using (var r = new BinaryReader(File.OpenRead(idxPath)))
            {
                if (r.ReadUInt32() != PackFormat.Magic) throw new IOException("zly naglowek");
                if (r.ReadInt32() != PackFormat.Version) throw new IOException("inna wersja");
                r.ReadDouble();
                int nStr = r.ReadInt32();
                strings = new string[nStr];
                for (int i = 0; i < nStr; i++) strings[i] = r.ReadString();
                int count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                    entries.Add(new long[] { r.ReadInt64(), r.ReadInt64(), r.ReadInt32(), r.ReadInt32() });
            }
            // Po offsecie - czytanie po kolei, bez skakania glowica/po sieci.
            entries.Sort((a, b) => a[1].CompareTo(b[1]));

            // ranga miejsca dla kazdego indeksu tablicy stringow: -1 = to nie jest rodzaj miejsca
            var rankOf = new sbyte[strings.Length];
            bool any = false;
            for (int i = 0; i < strings.Length; i++)
            {
                int rk = WorldPlaces.RankOf(strings[i]);
                bool isKind = strings[i] == "city" || strings[i] == "town" || strings[i] == "suburb" ||
                              strings[i] == "village" || strings[i] == "hamlet";
                rankOf[i] = (sbyte)(isKind ? rk : -1);
                if (isKind) any = true;
            }
            var roadCls = new sbyte[strings.Length];
            for (int i = 0; i < strings.Length; i++) { roadCls[i] = ClassOf(strings[i]); if (roadCls[i] >= 0) any = true; }
            var seenRoads = new HashSet<long>();
            if (!any) return;       // w tej paczce nie ma ani jednego miejsca ani drogi z zestawu

            string binPath = Path.ChangeExtension(idxPath, ".bin");
            using (var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                byte[] packed = new byte[1 << 16];
                foreach (long[] e in entries)
                {
                    int len = (int)e[2];
                    if (packed.Length < len) packed = new byte[len];
                    fs.Seek(e[1], SeekOrigin.Begin);
                    int got = 0;
                    while (got < len) { int n = fs.Read(packed, got, len - got); if (n <= 0) throw new IOException("koniec pliku"); got += n; }
                    var slice = new byte[len];
                    Buffer.BlockCopy(packed, 0, slice, 0, len);
                    byte[] raw = PackFormat.Inflate(slice, (int)e[3]);
                    cells++; bytes += len;
                    objects += ScanCell(raw, strings, rankOf, roadCls, found, roads, seenRoads);
                }
            }
        }

        private static int ScanCell(byte[] raw, string[] strings, sbyte[] rankOf, sbyte[] roadCls,
                                    List<KeyValuePair<long, WorldPlaces.Place>> found, List<RoadRec> roads, HashSet<long> seenRoads)
        {
            int p = 0;
            int count = (int)V(raw, ref p);
            for (int i = 0; i < count; i++)
            {
                byte kind = raw[p++];
                long id = V(raw, ref p);
                if (kind == PackFormat.KindPlace)
                {
                    int name = (int)V(raw, ref p), k = (int)V(raw, ref p);
                    double lat = V(raw, ref p) / PackFormat.CoordScale, lon = V(raw, ref p) / PackFormat.CoordScale;
                    if (k >= 0 && k < rankOf.Length && rankOf[k] >= 0 &&
                        name >= 0 && name < strings.Length && !string.IsNullOrEmpty(strings[name]))
                        found.Add(new KeyValuePair<long, WorldPlaces.Place>(id, new WorldPlaces.Place
                        {
                            Lat = (float)lat, Lon = (float)lon, Rank = (byte)rankOf[k], Name = strings[name]
                        }));
                    continue;
                }
                // uklad pol jak LocalStore.ReadCell (format 4)
                if (kind == PackFormat.KindTree) { V(raw, ref p); V(raw, ref p); continue; }
                if (kind == PackFormat.KindRoad)
                {
                    int hw = (int)V(raw, ref p);                          // pierwsze pole drogi: napis highway
                    for (int j = 0; j < 7; j++) V(raw, ref p);
                    int np = (int)V(raw, ref p);
                    int cls = hw >= 0 && hw < roadCls.Length ? roadCls[hw] : -1;
                    if (cls >= 0 && seenRoads.Add(id))
                    {
                        var pts = new int[2 * np];
                        long la = 0, lo = 0;
                        for (int j = 0; j < np; j++)
                        {
                            la += V(raw, ref p); lo += V(raw, ref p);
                            pts[2 * j] = (int)Math.Round(la / (PackFormat.CoordScale / 1e5));
                            pts[2 * j + 1] = (int)Math.Round(lo / (PackFormat.CoordScale / 1e5));
                        }
                        roads.Add(new RoadRec { Cls = (byte)cls, Pts = pts });
                    }
                    else for (int j = 0; j < 2 * np; j++) V(raw, ref p);
                    continue;
                }
                else if (kind == PackFormat.KindBuilding) { V(raw, ref p); p += 4; for (int j = 0; j < 7; j++) V(raw, ref p); }
                else if (kind == PackFormat.KindBarrier) { V(raw, ref p); V(raw, ref p); }
                else { p += 1; V(raw, ref p); }
                int n = (int)V(raw, ref p);
                for (int j = 0; j < 2 * n; j++) V(raw, ref p);
            }
            return count;
        }

        /// <summary>Zygzak + varint, jak PackFormat.ReadVarInt, ale po tablicy.</summary>
        private static long V(byte[] b, ref int p)
        {
            ulong z = 0; int shift = 0;
            while (true)
            {
                byte x = b[p++];
                z |= (ulong)(x & 0x7F) << shift;
                if ((x & 0x80) == 0) break;
                shift += 7;
            }
            return (long)(z >> 1) ^ -(long)(z & 1);
        }
    }
}
