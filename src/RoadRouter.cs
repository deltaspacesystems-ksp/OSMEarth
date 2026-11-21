using System;
using System.Collections.Generic;
using System.Globalization;

namespace OSMRoads
{
    public enum Maneuver { Depart, Straight, SlightLeft, Left, SharpLeft, SlightRight, Right, SharpRight, UTurn, Arrive }

    /// <summary>Jeden manewr na trasie: gdzie (indeks punktu i metr trasy), co i na jaka droge.</summary>
    public class RouteStep
    {
        public int Index;
        public double AtM;
        public Maneuver Turn;
        public float Angle;          // kat skretu, + = w prawo
        public string Road;
        public bool Junction;
    }

    public class NavRoute
    {
        public List<GeoPoint> Points = new List<GeoPoint>();
        public double[] Cum;                 // metr trasy w kazdym punkcie (metry gry)
        public float[] SpeedMs;              // szacowana predkosc na odcinku i -> i+1
        public List<RouteStep> Steps = new List<RouteStep>();
        public double LengthM, TimeS;
        public double BodyRadius;
        public string Error;
        public int GraphNodes;
        public double BuildMs;
    }

    public enum RouteProfile { Car = 0, Offroad = 1 }

    /// <summary>
    /// Trasa po sieci drog OSM z paczek.
    ///
    /// Graf powstaje w locie: drogi lacza sie tam, gdzie maja wspolny punkt (OSM tak
    /// zapisuje skrzyzowania). Dane czytamy tylko z korytarza miedzy startem a celem,
    /// a przy dluzszych trasach w srodku korytarza zostaja same wazniejsze drogi -
    /// pelna gestosc tylko przy koncach, gdzie trzeba wyjechac z osiedla.
    ///
    /// Paczki nie maja jednokierunkowych ani zakazow skretu, wiec trasa jest
    /// "dla lazika": najkrotsza w czasie po klasach drog, bez przepisow.
    /// </summary>
    public static class RoadRouter
    {
        public const double MaxSpanDeg = 7.0;
        private const double EndRadiusDeg = 0.06;

        public static float SpeedKmh(string hw, RouteProfile p)
        {
            switch (hw)
            {
                case "motorway": return 110f;
                case "trunk": return 90f;
                case "motorway_link": case "trunk_link": return 60f;
                case "primary": return 70f;
                case "secondary": return 60f;
                case "tertiary": return 50f;
                case "primary_link": case "secondary_link": case "tertiary_link": return 40f;
                case "unclassified": case "residential": case "road": return 35f;
                case "living_street": return 15f;
                case "service": return 18f;
                case "track": return p == RouteProfile.Offroad ? 25f : 12f;
                case "busway": return 30f;
                case "construction": case "proposed": case "platform": case "raceway": case "steps": case "elevator": return 0f;
                case "footway": case "path": case "cycleway": case "pedestrian": case "bridleway": case "corridor":
                    return p == RouteProfile.Offroad ? 10f : 0f;
                default: return p == RouteProfile.Offroad ? 8f : 0f;
            }
        }

        private static bool IsTrunkNet(string hw)
        {
            switch (hw)
            {
                case "motorway": case "motorway_link": case "trunk": case "trunk_link":
                case "primary": case "primary_link": case "secondary": case "secondary_link":
                case "tertiary": case "tertiary_link":
                    return true;
                default: return false;
            }
        }

        // ------------------------------------------------------------------
        //  Graf
        // ------------------------------------------------------------------

        private sealed class Graph
        {
            public readonly Dictionary<long, int> Index = new Dictionary<long, int>();
            public readonly List<double> Lat = new List<double>(), Lon = new List<double>();
            public readonly List<int> Head = new List<int>();
            // krawedzie: jednokierunkowe wpisy, kazdy odcinek drogi daje dwa
            public readonly List<int> To = new List<int>(), Next = new List<int>(), Way = new List<int>();
            public readonly List<float> Len = new List<float>(), Cost = new List<float>();
            public readonly List<OsmWay> Ways = new List<OsmWay>();
            public readonly HashSet<long> Seg = new HashSet<long>();

            public int Node(GeoPoint g)
            {
                long ky = (long)Math.Round(g.Lat * 1e7), kx = (long)Math.Round(g.Lon * 1e7);
                long key = (ky << 32) ^ (kx & 0xFFFFFFFFL);
                int id;
                if (Index.TryGetValue(key, out id)) return id;
                id = Lat.Count;
                Index[key] = id;
                Lat.Add(g.Lat); Lon.Add(g.Lon); Head.Add(-1);
                return id;
            }

            public void Edge(int a, int b, float len, float cost, int way)
            {
                To.Add(b); Len.Add(len); Cost.Add(cost); Way.Add(way); Next.Add(Head[a]); Head[a] = To.Count - 1;
            }

            public int Degree(int n)
            {
                int d = 0;
                for (int e = Head[n]; e >= 0; e = Next[e]) d++;
                return d;
            }
        }

        public static double Dist(double lat0, double lon0, double lat1, double lon1, double r)
        {
            double p0 = lat0 * Geo.DegToRad, p1 = lat1 * Geo.DegToRad;
            double dp = p1 - p0, dl = (lon1 - lon0) * Geo.DegToRad;
            double a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p0) * Math.Cos(p1) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2.0 * r * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
        }

        public static double Bearing(double lat0, double lon0, double lat1, double lon1)
        {
            double p0 = lat0 * Geo.DegToRad, p1 = lat1 * Geo.DegToRad, dl = (lon1 - lon0) * Geo.DegToRad;
            double y = Math.Sin(dl) * Math.Cos(p1);
            double x = Math.Cos(p0) * Math.Sin(p1) - Math.Sin(p0) * Math.Cos(p1) * Math.Cos(dl);
            return (Math.Atan2(y, x) / Geo.DegToRad + 360.0) % 360.0;
        }

        /// <summary>Wiersze komorek korytarza: dla kazdego pasa szerokosci jednej komorki
        /// przedzial dlugosci, ktory trzeba wczytac.</summary>
        private static List<BBox> Corridor(double lat0, double lon0, double lat1, double lon1, double halfDeg)
        {
            var rows = new SortedDictionary<int, double[]>();
            double cell = PackFormat.CellDeg;
            int n = Math.Max(2, (int)(Math.Max(Math.Abs(lat1 - lat0), Math.Abs(lon1 - lon0)) / (cell * 0.5)) + 2);
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n;
                double la = lat0 + (lat1 - lat0) * t, lo = lon0 + (lon1 - lon0) * t;
                double hl = halfDeg / Math.Max(0.15, Math.Cos(la * Geo.DegToRad));
                int r0 = (int)Math.Floor((la - halfDeg) / cell), r1 = (int)Math.Floor((la + halfDeg) / cell);
                for (int r = r0; r <= r1; r++)
                {
                    double[] iv;
                    if (!rows.TryGetValue(r, out iv)) rows[r] = iv = new[] { double.MaxValue, double.MinValue };
                    iv[0] = Math.Min(iv[0], lo - hl); iv[1] = Math.Max(iv[1], lo + hl);
                }
            }
            var boxes = new List<BBox>();
            foreach (KeyValuePair<int, double[]> kv in rows)
            {
                BBox b;
                b.South = Math.Max(-89.9, kv.Key * cell + 1e-7);
                b.North = Math.Min(89.9, (kv.Key + 1) * cell - 1e-7);
                b.West = kv.Value[0]; b.East = kv.Value[1];
                boxes.Add(b);
            }
            return boxes;
        }

        // ------------------------------------------------------------------
        //  Planowanie
        // ------------------------------------------------------------------

        public static NavRoute Plan(double lat0, double lon0, double lat1, double lon1, double bodyRadius,
                                    RouteProfile profile, Func<bool> cancelled, Action<string> progress)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var route = new NavRoute { BodyRadius = bodyRadius };
            if (cancelled == null) cancelled = () => false;
            if (progress == null) progress = s => { };

            // cel "za linia zmiany daty" - liczymy w ciaglych dlugosciach
            double dLon = lon1 - lon0;
            if (dLon > 180) lon1 -= 360; else if (dLon < -180) lon1 += 360;

            double spanDeg = Math.Max(Math.Abs(lat1 - lat0), Math.Abs(lon1 - lon0) * Math.Cos((lat0 + lat1) * 0.5 * Geo.DegToRad));
            if (spanDeg > MaxSpanDeg)
            {
                route.Error = "Destination too far for road routing (max ~" + (int)(MaxSpanDeg * 111) + " km on the map).";
                return route;
            }
            if (!LocalStore.Available) { route.Error = "No OSM data available."; return route; }

            bool sparse = spanDeg > 0.25;                 // ponad ok. 25 km: w srodku tylko wazne drogi
            double half = Math.Max(0.04, Math.Min(0.4, spanDeg * 0.10 + 0.03));

            var g = new Graph();
            List<BBox> boxes = Corridor(lat0, lon0, lat1, lon1, half);
            for (int bi = 0; bi < boxes.Count; bi++)
            {
                if (cancelled()) { route.Error = "Cancelled."; return route; }
                if ((bi & 7) == 0) progress(string.Format("Reading roads {0}%", bi * 100 / boxes.Count));

                List<OsmWay> ways;
                try { ways = LocalStore.Read(boxes[bi], null, true); }
                catch (Exception e) { route.Error = "Reading OSM data failed: " + e.Message; return route; }

                foreach (OsmWay w in ways)
                {
                    if (!w.IsRoad || w.Points.Count < 2) continue;
                    float kmh = SpeedKmh(w.Highway, profile);
                    if (kmh <= 0f) continue;
                    if (sparse && !IsTrunkNet(w.Highway) && !NearEnds(w, lat0, lon0, lat1, lon1)) continue;
                    AddWay(g, w, kmh, bodyRadius, profile == RouteProfile.Car);
                }
            }
            route.GraphNodes = g.Lat.Count;
            if (g.Lat.Count < 2) { route.Error = "No roads found between start and destination."; return route; }

            progress("Finding route");
            List<KeyValuePair<int, double>> starts = Nearest(g, lat0, lon0, bodyRadius, 6);
            List<KeyValuePair<int, double>> goals = Nearest(g, lat1, lon1, bodyRadius, 6);
            if (starts.Count == 0 || goals.Count == 0) { route.Error = "No road near the start or the destination."; return route; }

            List<int> edges = AStar(g, starts, goals, lat1, lon1, bodyRadius, cancelled);
            if (edges == null)
            {
                route.Error = cancelled() ? "Cancelled." : "No road connection found (the roads may not be connected in the data).";
                return route;
            }

            Build(g, edges, starts, route, bodyRadius);
            route.BuildMs = sw.Elapsed.TotalMilliseconds;
            return route;
        }

        private static bool NearEnds(OsmWay w, double lat0, double lon0, double lat1, double lon1)
        {
            double r = EndRadiusDeg;
            foreach (GeoPoint p in w.Points)
            {
                double c = Math.Cos(p.Lat * Geo.DegToRad);
                if (Math.Abs(p.Lat - lat0) < r && Math.Abs(p.Lon - lon0) * c < r) return true;
                if (Math.Abs(p.Lat - lat1) < r && Math.Abs(p.Lon - lon1) * c < r) return true;
            }
            return false;
        }

        private const float Closed = 1e9f;

        private static void AddWay(Graph g, OsmWay w, float kmh, double radius, bool oneway)
        {
            int wi = g.Ways.Count;
            g.Ways.Add(w);
            float ms = kmh / 3.6f;
            int prev = g.Node(w.Points[0]);
            for (int i = 1; i < w.Points.Count; i++)
            {
                int cur = g.Node(w.Points[i]);
                if (cur == prev) continue;
                // ta sama droga czytana z dwoch wierszy korytarza - odcinek tylko raz
                long sk = prev < cur ? ((long)prev << 32) | (uint)cur : ((long)cur << 32) | (uint)prev;
                if (g.Seg.Add(sk))
                {
                    float len = (float)Dist(g.Lat[prev], g.Lon[prev], g.Lat[cur], g.Lon[cur], radius);
                    float cost = len / ms;
                    // jednokierunkowe (samochod): krawedz pod prad istnieje (para dla FromOf), ale jest zamknieta
                    bool fwdOk = !oneway || (w.Flags & PackFormat.ROnewayRev) == 0;
                    bool revOk = !oneway || (w.Flags & PackFormat.ROneway) == 0;
                    g.Edge(prev, cur, len, fwdOk ? cost : Closed, wi);
                    g.Edge(cur, prev, len, revOk ? cost : Closed, wi);
                }
                prev = cur;
            }
        }

        /// <summary>Kilka najblizszych wezlow z roznych drog - jesli najblizsza droga jest
        /// odcieta od reszty (parking, brak polaczenia w danych), sprobuje sie nastepnej.</summary>
        private static List<KeyValuePair<int, double>> Nearest(Graph g, double lat, double lon, double radius, int k)
        {
            var best = new List<KeyValuePair<int, double>>();
            double c = Math.Cos(lat * Geo.DegToRad);
            double lim = EndRadiusDeg * 1.5;
            for (int i = 0; i < g.Lat.Count; i++)
            {
                double dy = g.Lat[i] - lat, dx = (g.Lon[i] - lon) * c;
                if (Math.Abs(dy) > lim || Math.Abs(dx) > lim) continue;
                double d = Math.Sqrt(dx * dx + dy * dy) * Geo.DegToRad * radius;
                best.Add(new KeyValuePair<int, double>(i, d));
            }
            best.Sort((a, b) => a.Value.CompareTo(b.Value));
            var outp = new List<KeyValuePair<int, double>>();
            var ways = new HashSet<int>();
            foreach (KeyValuePair<int, double> kv in best)
            {
                int wi = g.Head[kv.Key] >= 0 ? g.Way[g.Head[kv.Key]] : -1;
                if (!ways.Add(wi)) continue;
                outp.Add(kv);
                if (outp.Count >= k) break;
            }
            return outp;
        }

        // Dojazd do drogi "na przelaj" liczymy 3 m/s - blizsza droga wygrywa, ale nie za wszelka cene.
        private const double OffroadMs = 3.0;

        private static List<int> AStar(Graph g, List<KeyValuePair<int, double>> starts, List<KeyValuePair<int, double>> goals,
                                       double tLat, double tLon, double radius, Func<bool> cancelled)
        {
            int n = g.Lat.Count;
            var dist = new float[n];
            var via = new int[n];
            var closed = new bool[n];
            for (int i = 0; i < n; i++) { dist[i] = float.MaxValue; via[i] = -1; }

            var goalCost = new Dictionary<int, double>();
            foreach (KeyValuePair<int, double> kv in goals) goalCost[kv.Key] = kv.Value / OffroadMs;

            const double vmax = 110.0 / 3.6;
            var heap = new MinHeap();
            foreach (KeyValuePair<int, double> kv in starts)
            {
                float d0 = (float)(kv.Value / OffroadMs);
                if (d0 < dist[kv.Key]) { dist[kv.Key] = d0; via[kv.Key] = -2 - kv.Key; }
                heap.Push(d0 + Dist(g.Lat[kv.Key], g.Lon[kv.Key], tLat, tLon, radius) / vmax, kv.Key);
            }

            double bestTotal = double.MaxValue; int bestGoal = -1;
            int pops = 0;
            while (heap.Count > 0)
            {
                double f; int u;
                heap.Pop(out f, out u);
                if (closed[u]) continue;
                if (f >= bestTotal) break;
                closed[u] = true;
                if ((++pops & 0xFFFF) == 0 && cancelled()) return null;

                double gc;
                if (goalCost.TryGetValue(u, out gc) && dist[u] + gc < bestTotal) { bestTotal = dist[u] + gc; bestGoal = u; }

                for (int e = g.Head[u]; e >= 0; e = g.Next[e])
                {
                    int v = g.To[e];
                    if (closed[v]) continue;
                    if (g.Cost[e] >= Closed) continue;          // pod prad
                    float nd = dist[u] + g.Cost[e];
                    if (nd >= dist[v]) continue;
                    dist[v] = nd; via[v] = e;
                    heap.Push(nd + Dist(g.Lat[v], g.Lon[v], tLat, tLon, radius) / vmax, v);
                }
            }
            if (bestGoal < 0) return null;

            // odtworzenie: lista krawedzi od startu do celu
            var edges = new List<int>();
            int cur = bestGoal;
            while (via[cur] >= 0)
            {
                int e = via[cur];
                edges.Add(e);
                cur = FromOf(g, e, cur);
            }
            edges.Reverse();
            edges.Insert(0, -1 - cur);       // wezel startowy zakodowany jako pierwsza pozycja
            return edges;
        }

        /// <summary>Wezel, z ktorego wychodzi krawedz e prowadzaca do 'to'. Krawedzie powstaja
        /// parami (a->b, b->a), wiec druga z pary ma to samo 'to' co nasz poczatek.</summary>
        private static int FromOf(Graph g, int e, int to)
        {
            int twin = (e & 1) == 0 ? e + 1 : e - 1;
            return g.To[twin];
        }

        private static void Build(Graph g, List<int> edges, List<KeyValuePair<int, double>> starts, NavRoute r, double radius)
        {
            int start = -1 - edges[0];
            var nodes = new List<int> { start };
            var wayOf = new List<int>();                // droga odcinka i -> i+1
            for (int i = 1; i < edges.Count; i++) { nodes.Add(g.To[edges[i]]); wayOf.Add(g.Way[edges[i]]); }

            int n = nodes.Count;
            r.Cum = new double[n];
            r.SpeedMs = new float[Math.Max(1, n - 1)];
            for (int i = 0; i < n; i++)
            {
                r.Points.Add(new GeoPoint(g.Lat[nodes[i]], g.Lon[nodes[i]]));
                if (i > 0)
                {
                    int e = edges[i];
                    r.Cum[i] = r.Cum[i - 1] + g.Len[e];
                    r.SpeedMs[i - 1] = g.Len[e] / Math.Max(0.01f, g.Cost[e]);
                    r.TimeS += g.Cost[e];
                }
            }
            r.LengthM = r.Cum[n - 1];

            string firstRoad = wayOf.Count > 0 ? RoadName(g.Ways[wayOf[0]]) : null;
            r.Steps.Add(new RouteStep { Index = 0, AtM = 0, Turn = Maneuver.Depart, Road = firstRoad });

            for (int i = 1; i + 1 < n; i++)
            {
                OsmWay win = g.Ways[wayOf[i - 1]], wout = g.Ways[wayOf[i]];
                int deg = g.Degree(nodes[i]);
                bool junction = deg >= 3;
                if (!junction) continue;         // zakret tej samej drogi albo styk dwoch odcinkow - nic

                float ang = TurnAngle(r, i);
                string nin = RoadName(win), nout = RoadName(wout);
                bool renamed = nout != nin;
                bool turn = Math.Abs(ang) >= 28f;
                if (!turn && (!renamed || nout == null)) continue;

                var st = new RouteStep { Index = i, AtM = r.Cum[i], Angle = ang, Road = nout, Junction = junction, Turn = Classify(ang) };
                RouteStep last = r.Steps[r.Steps.Count - 1];
                // dwa manewry kilka metrow od siebie (skrzyzowanie z dwoch wezlow) - jeden
                if (last.Turn != Maneuver.Depart && st.AtM - last.AtM < 18.0)
                {
                    last.Angle += ang; last.Turn = Classify(last.Angle); last.Road = nout;
                    continue;
                }
                r.Steps.Add(st);
            }
            r.Steps.Add(new RouteStep { Index = n - 1, AtM = r.LengthM, Turn = Maneuver.Arrive });
            MergeSteps(r, firstRoad);
        }

        /// <summary>Manewry blizej niz 120 m od siebie to jedno skrzyzowanie (rondo, lacznik,
        /// rozjazd dwujezdniowej) - zostaje jeden, z wypadkowym katem i droga na wyjsciu.
        /// "Jedz prosto" na te sama droge, na ktorej sie jest, wypada calkiem.</summary>
        private static void MergeSteps(NavRoute r, string firstRoad)
        {
            var outp = new List<RouteStep> { r.Steps[0] };
            string road = firstRoad;
            int i = 1;
            while (i < r.Steps.Count - 1)
            {
                RouteStep a = r.Steps[i];
                int j = i;
                while (j + 1 < r.Steps.Count - 1 && r.Steps[j + 1].AtM - r.Steps[j].AtM < 120.0) j++;
                RouteStep b = r.Steps[j];

                var m = new RouteStep { Index = a.Index, AtM = a.AtM, Road = b.Road, Junction = true };
                m.Angle = j == i ? a.Angle : NetAngle(r, a.Index, b.Index);
                m.Turn = Classify(m.Angle);
                // rondo: wjazd i wyjazd na prosto daje kat ~0, ale nazwa ronda po drodze nic nie mowi
                if (j > i && m.Turn == Maneuver.Straight && b.Road == road) m.Road = road;

                bool keep = m.Turn != Maneuver.Straight || (m.Road != null && m.Road != road);
                if (keep) outp.Add(m);
                if (m.Road != null) road = m.Road;
                i = j + 1;
            }
            outp.Add(r.Steps[r.Steps.Count - 1]);
            r.Steps = outp;
        }

        private static float NetAngle(NavRoute r, int i0, int i1)
        {
            int a = i0, b = i1;
            while (a > 0 && r.Cum[i0] - r.Cum[a] < 25.0) a--;
            while (b < r.Points.Count - 1 && r.Cum[b] - r.Cum[i1] < 25.0) b++;
            if (a == i0 || b == i1) return 0f;
            GeoPoint pa = r.Points[a], p0 = r.Points[i0], p1 = r.Points[i1], pb = r.Points[b];
            double d = Bearing(p1.Lat, p1.Lon, pb.Lat, pb.Lon) - Bearing(pa.Lat, pa.Lon, p0.Lat, p0.Lon);
            while (d > 180) d -= 360;
            while (d < -180) d += 360;
            return (float)d;
        }

        public static string RoadName(OsmWay w)
        {
            if (!string.IsNullOrEmpty(w.Name)) return w.Name;
            return null;
        }

        /// <summary>Kat skretu w punkcie i: kierunek ~25 m przed i ~25 m za punktem
        /// (pojedyncze wezly OSM bywaja rozrzucone co metr).</summary>
        public static float TurnAngle(NavRoute r, int i)
        {
            int a = i, b = i;
            while (a > 0 && r.Cum[i] - r.Cum[a] < 25.0) a--;
            while (b < r.Points.Count - 1 && r.Cum[b] - r.Cum[i] < 25.0) b++;
            if (a == i || b == i) return 0f;
            GeoPoint pa = r.Points[a], pi = r.Points[i], pb = r.Points[b];
            double b0 = Bearing(pa.Lat, pa.Lon, pi.Lat, pi.Lon), b1 = Bearing(pi.Lat, pi.Lon, pb.Lat, pb.Lon);
            double d = b1 - b0;
            while (d > 180) d -= 360;
            while (d < -180) d += 360;
            return (float)d;
        }

        public static Maneuver Classify(float a)
        {
            float x = Math.Abs(a);
            if (x < 20f) return Maneuver.Straight;
            if (x > 160f) return Maneuver.UTurn;
            if (a > 0) return x < 50f ? Maneuver.SlightRight : x < 125f ? Maneuver.Right : Maneuver.SharpRight;
            return x < 50f ? Maneuver.SlightLeft : x < 125f ? Maneuver.Left : Maneuver.SharpLeft;
        }

        public static string Describe(RouteStep s)
        {
            string onto = s.Road != null ? " onto " + s.Road : "";
            switch (s.Turn)
            {
                case Maneuver.Depart: return "Head out" + (s.Road != null ? " on " + s.Road : "");
                case Maneuver.Straight: return "Continue" + onto;
                case Maneuver.SlightLeft: return "Bear left" + onto;
                case Maneuver.Left: return "Turn left" + onto;
                case Maneuver.SharpLeft: return "Sharp left" + onto;
                case Maneuver.SlightRight: return "Bear right" + onto;
                case Maneuver.Right: return "Turn right" + onto;
                case Maneuver.SharpRight: return "Sharp right" + onto;
                case Maneuver.UTurn: return "Make a U-turn" + onto;
                default: return "Arrive at destination";
            }
        }

        public static string FormatDistance(double m)
        {
            if (m < 1000) return string.Format(CultureInfo.InvariantCulture, "{0:0} m", Math.Round(m / 10.0) * 10.0);
            if (m < 10000) return string.Format(CultureInfo.InvariantCulture, "{0:0.0} km", m / 1000.0);
            return string.Format(CultureInfo.InvariantCulture, "{0:0} km", m / 1000.0);
        }

        public static string FormatTime(double s)
        {
            if (s < 60) return "<1 min";
            int m = (int)Math.Round(s / 60.0);
            return m < 60 ? m + " min" : (m / 60) + " h " + (m % 60).ToString("00") + " min";
        }

        // ------------------------------------------------------------------

        private sealed class MinHeap
        {
            private double[] k = new double[1024];
            private int[] v = new int[1024];
            public int Count;

            public void Push(double key, int val)
            {
                if (Count == k.Length) { Array.Resize(ref k, Count * 2); Array.Resize(ref v, Count * 2); }
                int i = Count++;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (k[p] <= key) break;
                    k[i] = k[p]; v[i] = v[p]; i = p;
                }
                k[i] = key; v[i] = val;
            }

            public void Pop(out double key, out int val)
            {
                key = k[0]; val = v[0];
                double lk = k[--Count]; int lv = v[Count];
                int i = 0;
                while (true)
                {
                    int c = 2 * i + 1;
                    if (c >= Count) break;
                    if (c + 1 < Count && k[c + 1] < k[c]) c++;
                    if (k[c] >= lk) break;
                    k[i] = k[c]; v[i] = v[c]; i = c;
                }
                k[i] = lk; v[i] = lv;
            }
        }
    }
}
