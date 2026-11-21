using System;
using System.Globalization;
using OSMRoads;

namespace OSMView
{
    /// <summary>osmview find &lt;tekst&gt; &lt;lat&gt; &lt;lon&gt; [km] - obiekty z nazwa zawierajaca tekst
    /// i najblizszy model LoD2 dla kazdego budynku.</summary>
    internal static class FindTest
    {
        public static int Run(string[] args)
        {
            string text = args[1].ToLowerInvariant();
            double lat = double.Parse(args[2], CultureInfo.InvariantCulture);
            double lon = double.Parse(args[3], CultureInfo.InvariantCulture);
            double km = args.Length > 4 ? double.Parse(args[4], CultureInfo.InvariantCulture) : 5;
            LocalStore.Reload();
            BBox box = Geo.BoxAround(lat, lon, km * 1000, 6371010);
            var ways = LocalStore.Read(box);
            var lod2 = Lod2Store.Read(box);
            Console.WriteLine("obiektow {0}, LoD2 {1}", ways.Count, lod2 == null ? 0 : lod2.Count);
            foreach (OsmWay w in ways)
            {
                if (w.Name == null || !w.Name.ToLowerInvariant().Contains(text)) continue;
                double cla = 0, clo = 0;
                foreach (GeoPoint p in w.Points) { cla += p.Lat; clo += p.Lon; }
                cla /= w.Points.Count; clo /= w.Points.Count;
                Console.WriteLine("{0} bld={1} area={2} h={3} lvl={4} roof={5} pts={6} @ {7:F5},{8:F5}",
                    w.Name, w.Building, w.Area, w.HeightM, w.Levels, w.RoofShape, w.Points.Count, cla, clo);
                if (lod2 == null) continue;
                Lod2Building best = null; double bd = 1e9;
                foreach (Lod2Building b in lod2)
                {
                    double d = Math.Sqrt(Math.Pow((b.Lat - cla) * 111000, 2) + Math.Pow((b.Lon - clo) * 71000, 2));
                    if (d < bd) { bd = d; best = b; }
                }
                if (best != null)
                {
                    float top = 0; foreach (float h in best.VH) top = Math.Max(top, h);
                    Console.WriteLine("   najblizszy LoD2: {0:F0} m, dach {1}, wys {2:F1} m, trojkatow {3}+{4}",
                        bd, best.RoofType, top, best.Walls.Length / 3, best.Roofs.Length / 3);
                }
            }
            return 0;
        }
    }
}
