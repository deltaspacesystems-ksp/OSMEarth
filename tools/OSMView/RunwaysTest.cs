using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using OSMRoads;

namespace OSMView
{
    /// <summary>
    /// osmview runways &lt;south&gt; &lt;west&gt; &lt;north&gt; &lt;east&gt; &lt;plik.csv&gt; - pasy startowe z paczek
    /// (aeroway=runway) do listy dla generatora configow Kerbal Konstructs w modzie.
    /// Kolumny: nazwa;lat1;lon1;lat2;lon2;szerokosc_m;nawierzchnia
    /// </summary>
    internal static class RunwaysTest
    {
        public static int Run(string[] args)
        {
            var ci = CultureInfo.InvariantCulture;
            double s = double.Parse(args[1], ci), w = double.Parse(args[2], ci), n = double.Parse(args[3], ci), e = double.Parse(args[4], ci);
            LocalStore.Reload();
            var sb = new StringBuilder("# name;lat1;lon1;lat2;lon2;width_m;surface\n");
            int count = 0;
            var seen = new HashSet<string>();
            for (double la = s; la < n; la += 0.5)
                for (double lo = w; lo < e; lo += 0.5)
                {
                    var box = new BBox { South = la, West = lo, North = Math.Min(n, la + 0.5), East = Math.Min(e, lo + 0.5) };
                    var places = new List<OsmPlace>();
                    List<OsmWay> ways = LocalStore.Read(box, places, false);
                    foreach (OsmWay r in ways)
                    {
                        if (!r.IsRoad || r.Highway != "runway" || r.Points.Count < 2) continue;
                        GeoPoint a = r.Points[0], b = r.Points[r.Points.Count - 1];
                        double mLat = (a.Lat + b.Lat) / 2, mLon = (a.Lon + b.Lon) / 2;
                        if (mLat < box.South || mLat >= box.North || mLon < box.West || mLon >= box.East) continue;
                        double lenM = Dist(a.Lat, a.Lon, b.Lat, b.Lon);
                        if (lenM < 250) continue;          // pasy dla modeli, lądowiska szybowcowe itp. zostaja
                        string key = Math.Round(mLat, 4) + "," + Math.Round(mLon, 4);
                        if (!seen.Add(key)) continue;

                        // nazwa: najblizsza miejscowosc + oznaczenie pasa z kursu
                        string town = "Poland";
                        double best = 30000;
                        foreach (OsmPlace p in PlacesAround(mLat, mLon))
                        {
                            if (p.Kind != "city" && p.Kind != "town" && p.Kind != "village") continue;
                            double d = Dist(p.Lat, p.Lon, mLat, mLon) * (p.Kind == "village" ? 2.5 : 1.0);
                            if (d < best) { best = d; town = p.Name; }
                        }
                        double hdg = Bearing(a.Lat, a.Lon, b.Lat, b.Lon);
                        int d1 = (int)Math.Round(hdg / 10.0); if (d1 == 0) d1 = 36; if (d1 > 36) d1 -= 36;
                        int d2 = d1 > 18 ? d1 - 18 : d1 + 18;
                        string des = Math.Min(d1, d2).ToString("00") + "/" + Math.Max(d1, d2).ToString("00");
                        string name = (r.Name != null && r.Name.Length > 0 && r.Name.Length < 40 ? r.Name + " " : "") + town + " RWY " + des;
                        float wm = float.IsNaN(r.WidthM) ? 30f : r.WidthM;
                        sb.AppendFormat(ci, "{0};{1:F7};{2:F7};{3:F7};{4:F7};{5:F0};{6}\n", name.Replace(";", ","),
                                        a.Lat, a.Lon, b.Lat, b.Lon, wm, r.Surface ?? "");
                        count++;
                    }
                }
            File.WriteAllText(args[5], sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine("pasow: " + count);
            return 0;
        }

        private static List<OsmPlace> PlacesAround(double lat, double lon)
        {
            var places = new List<OsmPlace>();
            LocalStore.Read(Around(lat, lon, 0.25), places, true);
            return places;
        }

        private static BBox Around(double lat, double lon, double d)
        {
            return new BBox { South = lat - d, North = lat + d, West = lon - d * 1.5, East = lon + d * 1.5 };
        }

        private static double Dist(double la1, double lo1, double la2, double lo2)
        {
            double k = Math.PI / 180, x = (lo2 - lo1) * k * Math.Cos((la1 + la2) * 0.5 * k), y = (la2 - la1) * k;
            return Math.Sqrt(x * x + y * y) * 6371010;
        }

        private static double Bearing(double la1, double lo1, double la2, double lo2)
        {
            double k = Math.PI / 180, x = (lo2 - lo1) * Math.Cos((la1 + la2) * 0.5 * k), y = la2 - la1;
            double h = Math.Atan2(x, y) / k;
            return h < 0 ? h + 360 : h;
        }
    }
}
