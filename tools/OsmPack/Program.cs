using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using OsmSharp;
using OsmSharp.Streams;
using OSMRoads;

namespace OsmPack
{
    /// <summary>
    /// Tnie .osm.pbf na paczki dla moda: nazwa.bin + nazwa.idx + nazwa.json.
    ///
    /// Dwie rzeczy decyduja o szybkosci:
    ///
    /// 1. JEDEN PRZELOT przez plik. Pliki .pbf sa posortowane wezly -> drogi ->
    ///    relacje, wiec zanim dojda drogi, mamy juz komplet potrzebnych wezlow.
    ///    Drugi przebieg byl zwyklym marnotrawstwem.
    ///
    /// 2. WIELE REGIONOW NARAZ. Skan 88 GB kosztuje tyle samo dla jednego wycinka
    ///    co dla dwudziestu, wiec jedno przejscie produkuje wszystkie paczki.
    ///    Inaczej planeta razy dwadziescia regionow to tygodnie, a nie noc.
    ///
    /// Wezly filtrujemy sumą bboxow od razu przy wczytywaniu, wiec pamiec zalezy
    /// od wielkosci wycinkow, a nie od wielkosci pliku wejsciowego.
    /// </summary>
    internal static class Program
    {
        private sealed class PendingWay
        {
            public long Id;
            public byte Kind;
            public string S1;
            public string RoofShape;
            public string Name;
            public float Height = float.NaN;
            public int Levels = -1;
            public sbyte Area = -1;
            public long[] Nodes;
        }

        private sealed class PendingPlace
        {
            public long Id;
            public string Name;
            public string Kind;
            public int Lat, Lon;
        }

        private sealed class Region
        {
            public string Name;
            public double S, W, N, E;
            public readonly Dictionary<long, List<PendingWay>> Cells =
                new Dictionary<long, List<PendingWay>>();
            public readonly Dictionary<long, List<PendingPlace>> PlaceCells =
                new Dictionary<long, List<PendingPlace>>();
            public int Kept;

            public bool Contains(double lat, double lon)
            {
                return lat >= S && lat <= N && lon >= W && lon <= E;
            }
        }

        /// <summary>Zapas wokol bboxa, zeby droga wychodzaca poza wycinek nadal
        /// miala komplet wezlow i nie wypadla w calosci.</summary>
        private const double MarginDeg = 0.25;

        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("uzycie:");
                Console.WriteLine("  osmpack <plik.osm.pbf> <katalog>                       - caly plik");
                Console.WriteLine("  osmpack <plik.osm.pbf> <katalog> S,W,N,E [nazwa]       - jeden wycinek");
                Console.WriteLine("  osmpack <plik.osm.pbf> <katalog> --regions lista.txt   - wiele naraz");
                Console.WriteLine();
                Console.WriteLine("lista.txt: jedna linia na region, '#' to komentarz");
                Console.WriteLine("  ksc       27.5,-81.5,29.0,-80.0");
                Console.WriteLine("  krakow    49.8,19.6,50.3,20.3");
                return 1;
            }

            string input = args[0];
            string outDir = args[1];
            var regions = new List<Region>();
            bool wholeFile = false;

            if (args.Length >= 4 && args[2] == "--regions")
            {
                if (!LoadRegions(args[3], regions)) return 1;
            }
            else if (args.Length >= 3 && args[2].Contains(","))
            {
                string[] p = args[2].Split(',');
                if (p.Length != 4) { Console.WriteLine("zly bbox"); return 1; }
                regions.Add(new Region
                {
                    Name = args.Length >= 4 ? args[3] : "region",
                    S = D(p[0]), W = D(p[1]), N = D(p[2]), E = D(p[3])
                });
            }
            else
            {
                // Tryb "caly plik". Nazwa moze przyjsc jako args[2] albo args[3],
                // bo add-region.sh podaje 0 w miejscu bboxa - wczesniej byla tu
                // zaszyta na sztywno i kazdy region wychodzil jako "osm".
                wholeFile = true;
                string wholeName = "osm";
                if (args.Length >= 4 && !string.IsNullOrEmpty(args[3])) wholeName = args[3];
                else if (args.Length >= 3 && args[2] != "0" && !args[2].Contains(",")) wholeName = args[2];
                regions.Add(new Region { Name = wholeName, S = -90, W = -180, N = 90, E = 180 });
            }

            // Suma bboxow z zapasem - tylko takie wezly trzymamy w pamieci.
            double mS = 90, mN = -90, mW = 180, mE = -180;
            foreach (Region r in regions)
            {
                if (r.S - MarginDeg < mS) mS = r.S - MarginDeg;
                if (r.N + MarginDeg > mN) mN = r.N + MarginDeg;
                if (r.W - MarginDeg < mW) mW = r.W - MarginDeg;
                if (r.E + MarginDeg > mE) mE = r.E + MarginDeg;
            }
            if (wholeFile) { mS = -90; mN = 90; mW = -180; mE = 180; }

            Directory.CreateDirectory(outDir);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            Console.WriteLine("wejscie  : " + input + string.Format(" ({0:N1} GB)",
                              new FileInfo(input).Length / 1073741824.0));
            Console.WriteLine("regionow : " + regions.Count);
            foreach (Region r in regions)
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-14} {1},{2},{3},{4}", r.Name, r.S, r.W, r.N, r.E));
            Console.WriteLine();

            // --- JEDEN PRZELOT ---
            var coords = new Dictionary<long, KeyValuePair<int, int>>();
            var touched = new HashSet<long>();
            long seen = 0, nodes = 0;
            int kept = 0, dropped = 0, places = 0;
            bool waysStarted = false, warnedOrder = false;

            Console.WriteLine("skanuje ...");
            using (var fs = File.OpenRead(input))
            {
                foreach (OsmGeo o in new PBFOsmStreamSource(fs))
                {
                    if (++seen % 20000000 == 0)
                        Console.WriteLine(string.Format(
                            "  ...{0} mln obiektow | wezlow {1:N0} | zachowanych {2:N0} | {3:F0} min",
                            seen / 1000000, coords.Count, kept, sw.Elapsed.TotalMinutes));

                    if (o.Type == OsmGeoType.Node)
                    {
                        // Pliki .pbf sa posortowane; gdyby nie byly, wezel po drogach
                        // znaczy, ze cześć geometrii przepadnie - lepiej o tym wiedziec.
                        if (waysStarted && !warnedOrder)
                        {
                            warnedOrder = true;
                            Console.WriteLine("  UWAGA: wezly po drogach - plik nieposortowany,"
                                            + " czesc geometrii moze wypasc");
                        }

                        var n = (Node)o;
                        if (!n.Id.HasValue || !n.Latitude.HasValue || !n.Longitude.HasValue) continue;

                        double la = n.Latitude.Value, lo = n.Longitude.Value;

                        // Suma bboxow to tylko SZYBKI ODSIEW. Przy regionach
                        // rozrzuconych po swiecie (Kourou i Bajkonur) prostokat
                        // opisany na nich obejmuje pol planety, wiec sam w sobie
                        // niczego nie filtruje - trzeba sprawdzic KAZDY region z osobna.
                        if (la < mS || la > mN || lo < mW || lo > mE) continue;

                        bool nodeWanted = false;
                        for (int ri = 0; ri < regions.Count; ri++)
                        {
                            Region rr = regions[ri];
                            if (la >= rr.S - MarginDeg && la <= rr.N + MarginDeg &&
                                lo >= rr.W - MarginDeg && lo <= rr.E + MarginDeg)
                            { nodeWanted = true; break; }
                        }
                        if (!nodeWanted) continue;

                        int qLat = (int)Math.Round(la * PackFormat.CoordScale);
                        int qLon = (int)Math.Round(lo * PackFormat.CoordScale);
                        coords[n.Id.Value] = new KeyValuePair<int, int>(qLat, qLon);
                        nodes++;

                        PendingPlace pp = MakePlace(n, qLat, qLon);
                        if (pp != null)
                        {
                            int pi, pj;
                            PackFormat.CellOf(la, lo, out pi, out pj);
                            long pk = PackFormat.CellKey(pi, pj);
                            foreach (Region r in regions)
                            {
                                if (!r.Contains(la, lo)) continue;
                                List<PendingPlace> pl;
                                if (!r.PlaceCells.TryGetValue(pk, out pl))
                                    r.PlaceCells[pk] = pl = new List<PendingPlace>();
                                pl.Add(pp);
                                places++;
                            }
                        }
                        continue;
                    }

                    if (o.Type != OsmGeoType.Way) continue;
                    waysStarted = true;

                    var w = (Way)o;
                    if (w.Nodes == null || w.Nodes.Length < 2 || w.Tags == null) continue;

                    PendingWay pw = Classify(w);
                    if (pw == null) continue;

                    // Wszystkie wezly musza byc znane - inaczej geometria bylaby
                    // dziurawa. Od tego jest zapas MarginDeg.
                    touched.Clear();
                    bool ok = true;
                    double wS = 90, wN = -90, wW = 180, wE = -180;

                    foreach (long nid in pw.Nodes)
                    {
                        KeyValuePair<int, int> c;
                        if (!coords.TryGetValue(nid, out c)) { ok = false; break; }

                        double lat = c.Key / PackFormat.CoordScale;
                        double lon = c.Value / PackFormat.CoordScale;
                        if (lat < wS) wS = lat; if (lat > wN) wN = lat;
                        if (lon < wW) wW = lon; if (lon > wE) wE = lon;

                        int iLat, iLon;
                        PackFormat.CellOf(lat, lon, out iLat, out iLon);
                        touched.Add(PackFormat.CellKey(iLat, iLon));
                    }

                    if (!ok) { dropped++; continue; }

                    bool used = false;
                    foreach (Region r in regions)
                    {
                        // przeciecie prostokatow: obiekt dotyka regionu?
                        if (wN < r.S || wS > r.N || wE < r.W || wW > r.E) continue;

                        foreach (long key in touched)
                        {
                            List<PendingWay> list;
                            if (!r.Cells.TryGetValue(key, out list))
                                r.Cells[key] = list = new List<PendingWay>();
                            list.Add(pw);
                        }
                        r.Kept++;
                        used = true;
                    }

                    if (used) kept++; else dropped++;
                }
            }

            Console.WriteLine(string.Format(
                "  {0:N0} wezlow, {1:N0} obiektow, {2:N0} miejsc, pominieto {3:N0}",
                nodes, kept, places, dropped));
            Console.WriteLine();

            foreach (Region r in regions)
                Write(outDir, r, coords);

            sw.Stop();
            Console.WriteLine();
            Console.WriteLine(string.Format("GOTOWE w {0:F1} min", sw.Elapsed.TotalMinutes));
            return 0;
        }

        private static bool LoadRegions(string path, List<Region> into)
        {
            if (!File.Exists(path)) { Console.WriteLine("brak pliku: " + path); return false; }

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) { Console.WriteLine("pomijam: " + line); continue; }

                string[] b = parts[parts.Length - 1].Split(',');
                if (b.Length != 4) { Console.WriteLine("pomijam: " + line); continue; }

                into.Add(new Region
                {
                    Name = parts[0],
                    S = D(b[0]), W = D(b[1]), N = D(b[2]), E = D(b[3])
                });
            }

            if (into.Count == 0) { Console.WriteLine("lista regionow pusta"); return false; }
            return true;
        }

        private static void Write(string outDir, Region reg,
                                  Dictionary<long, KeyValuePair<int, int>> coords)
        {
            string binPath = Path.Combine(outDir, reg.Name + ".bin");
            string idxPath = Path.Combine(outDir, reg.Name + ".idx");
            string jsonPath = Path.Combine(outDir, reg.Name + ".json");

            var index = new List<long[]>(reg.Cells.Count);
            var strTable = new List<string>();
            var strIndex = new Dictionary<string, int>();

            Func<string, int> intern = delegate (string s)
            {
                if (string.IsNullOrEmpty(s)) return -1;
                int ix;
                if (strIndex.TryGetValue(s, out ix)) return ix;
                ix = strTable.Count; strTable.Add(s); strIndex[s] = ix; return ix;
            };

            var allKeys = new HashSet<long>(reg.Cells.Keys);
            allKeys.UnionWith(reg.PlaceCells.Keys);

            double minLat = 90, maxLat = -90, minLon = 180, maxLon = -180;

            using (var bs = File.Create(binPath, 1 << 20))
            using (var bw = new BinaryWriter(bs))
            {
                foreach (long ck in allKeys)
                {
                    List<PendingWay> cellWays;
                    List<PendingPlace> cellPlaces;
                    reg.Cells.TryGetValue(ck, out cellWays);
                    reg.PlaceCells.TryGetValue(ck, out cellPlaces);

                    int nWays = cellWays == null ? 0 : cellWays.Count;
                    int nPlaces = cellPlaces == null ? 0 : cellPlaces.Count;

                    var cell = new MemoryStream();
                    var cw = new BinaryWriter(cell);
                    PackFormat.WriteVarInt(cw, nWays + nPlaces);

                    for (int i = 0; i < nWays; i++)
                    {
                        PendingWay pw = cellWays[i];
                        cw.Write(pw.Kind);
                        PackFormat.WriteVarInt(cw, pw.Id);

                        if (pw.Kind == PackFormat.KindRoad)
                        {
                            PackFormat.WriteVarInt(cw, intern(pw.S1));
                        }
                        else if (pw.Kind == PackFormat.KindBuilding)
                        {
                            PackFormat.WriteVarInt(cw, intern(pw.S1));
                            cw.Write(pw.Height);
                            PackFormat.WriteVarInt(cw, pw.Levels);
                            PackFormat.WriteVarInt(cw, intern(pw.RoofShape));
                        }
                        else
                        {
                            cw.Write(pw.Area);
                        }

                        PackFormat.WriteVarInt(cw, intern(pw.Name));
                        PackFormat.WriteVarInt(cw, pw.Nodes.Length);

                        long pLat = 0, pLon = 0;
                        foreach (long nid in pw.Nodes)
                        {
                            KeyValuePair<int, int> c = coords[nid];
                            PackFormat.WriteVarInt(cw, c.Key - pLat);
                            PackFormat.WriteVarInt(cw, c.Value - pLon);
                            pLat = c.Key; pLon = c.Value;

                            double dla = c.Key / PackFormat.CoordScale;
                            double dlo = c.Value / PackFormat.CoordScale;
                            if (dla < minLat) minLat = dla;
                            if (dla > maxLat) maxLat = dla;
                            if (dlo < minLon) minLon = dlo;
                            if (dlo > maxLon) maxLon = dlo;
                        }
                    }

                    for (int i = 0; i < nPlaces; i++)
                    {
                        PendingPlace p = cellPlaces[i];
                        cw.Write(PackFormat.KindPlace);
                        PackFormat.WriteVarInt(cw, p.Id);
                        PackFormat.WriteVarInt(cw, intern(p.Name));
                        PackFormat.WriteVarInt(cw, intern(p.Kind));
                        PackFormat.WriteVarInt(cw, p.Lat);
                        PackFormat.WriteVarInt(cw, p.Lon);
                    }

                    cw.Flush();
                    byte[] raw = cell.ToArray();
                    byte[] packed = PackFormat.Deflate(raw);

                    long start = bs.Position;
                    bw.Write(packed);
                    bw.Flush();
                    index.Add(new long[] { ck, start, packed.Length, raw.Length });
                }
            }

            using (var fs = File.Create(idxPath))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(PackFormat.Magic);
                w.Write(PackFormat.Version);
                w.Write(PackFormat.CellDeg);

                w.Write(strTable.Count);
                foreach (string s in strTable) w.Write(s);

                w.Write(index.Count);
                foreach (long[] e in index)
                {
                    w.Write(e[0]); w.Write(e[1]); w.Write((int)e[2]); w.Write((int)e[3]);
                }
            }

            if (minLat > maxLat) { minLat = reg.S; maxLat = reg.N; minLon = reg.W; maxLon = reg.E; }

            var bi = new FileInfo(binPath);
            var ii = new FileInfo(idxPath);
            File.WriteAllText(jsonPath, string.Format(CultureInfo.InvariantCulture,
                "{{\n  \"name\": \"{0}\",\n  \"south\": {1:F5},\n  \"west\": {2:F5},\n" +
                "  \"north\": {3:F5},\n  \"east\": {4:F5},\n  \"cells\": {5},\n" +
                "  \"bytes\": {6},\n  \"built\": \"{7:yyyy-MM-dd}\"\n}}\n",
                reg.Name, minLat, minLon, maxLat, maxLon,
                index.Count, bi.Length + ii.Length, DateTime.UtcNow));

            Console.WriteLine(string.Format(
                "  {0,-14} {1,7:N1} MB + {2,5:N1} MB idx   {3,6:N0} komorek   {4,8:N0} obiektow",
                reg.Name, bi.Length / 1048576.0, ii.Length / 1048576.0, index.Count, reg.Kept));
        }

        private static double D(string s)
        {
            return double.Parse(s.Trim(), CultureInfo.InvariantCulture);
        }

        private static PendingPlace MakePlace(Node n, int qLat, int qLon)
        {
            if (n.Tags == null || !n.Id.HasValue) return null;

            string kind;
            if (!n.Tags.TryGetValue("place", out kind)) return null;

            switch (kind)
            {
                case "city": case "town": case "village":
                case "hamlet": case "suburb": break;
                default: return null;
            }

            string name;
            if (!n.Tags.TryGetValue("name", out name) || string.IsNullOrEmpty(name)) return null;

            return new PendingPlace
            {
                Id = n.Id.Value,
                Name = name,
                Kind = kind,
                Lat = qLat,
                Lon = qLon
            };
        }

        private static PendingWay Classify(Way w)
        {
            string highway = Get(w, "highway");
            if (highway != null && IsWantedRoad(highway))
                return new PendingWay
                {
                    Id = w.Id ?? 0,
                    Kind = PackFormat.KindRoad,
                    S1 = highway,
                    Name = Get(w, "name"),
                    Nodes = w.Nodes
                };

            string building = Get(w, "building");
            if (building != null && building != "no")
                return new PendingWay
                {
                    Id = w.Id ?? 0,
                    Kind = PackFormat.KindBuilding,
                    S1 = building,
                    RoofShape = Get(w, "roof:shape"),
                    Height = ParseMeters(Get(w, "height")),
                    Levels = ParseInt(Get(w, "building:levels")),
                    Name = Get(w, "name"),
                    Nodes = w.Nodes
                };

            string[] areaKeys = { "landuse", "natural", "leisure" };
            foreach (string key in areaKeys)
            {
                string v = Get(w, key);
                if (v == null) continue;
                AreaKind kind = AreaKinds.From(key, v);
                if (kind != AreaKind.None)
                    return new PendingWay
                    {
                        Id = w.Id ?? 0,
                        Kind = PackFormat.KindArea,
                        Area = (sbyte)kind,
                        Name = Get(w, "name"),
                        Nodes = w.Nodes
                    };
            }

            return null;
        }

        private static bool IsWantedRoad(string v)
        {
            switch (v)
            {
                case "motorway": case "motorway_link":
                case "trunk": case "trunk_link":
                case "primary": case "primary_link":
                case "secondary": case "secondary_link":
                case "tertiary": case "tertiary_link":
                case "residential": case "unclassified":
                    return true;
                default: return false;
            }
        }

        private static string Get(Way w, string key)
        {
            string v;
            return w.Tags != null && w.Tags.TryGetValue(key, out v) ? v : null;
        }

        private static float ParseMeters(string v)
        {
            if (string.IsNullOrEmpty(v)) return float.NaN;
            int end = 0;
            while (end < v.Length && (char.IsDigit(v[end]) || v[end] == '.' || v[end] == '-')) end++;
            float f;
            return end > 0 && float.TryParse(v.Substring(0, end), NumberStyles.Float,
                                             CultureInfo.InvariantCulture, out f)
                   ? f : float.NaN;
        }

        private static int ParseInt(string v)
        {
            int i;
            return int.TryParse(v, out i) ? i : -1;
        }
    }
}
