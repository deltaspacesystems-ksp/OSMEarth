using System;
using System.Collections.Generic;
using System.Globalization;
using OSMRoads;
using UnityEngine;

namespace OSMView
{
    /// <summary>
    /// Sprawdzenie triangulacji liczbami, nie na oko.
    ///
    ///   osmview poly &lt;lat&gt; &lt;lon&gt;
    ///
    /// Dla kazdego obszaru z paczki: suma pol trojkatow / pole wielokata.
    /// Poprawna triangulacja prostego wielokata daje dokladnie 1. Mniej niz 1
    /// to dziury, wiecej - zakladki. Liczone w dwoch wariantach: na pelnym
    /// obrysie (jak landuse 3D) i na punktach przerzedzonych do pikseli mapy.
    /// </summary>
    internal static class PolygonTest
    {
        public static int Run(string[] args)
        {
            double lat = double.Parse(args[1], CultureInfo.InvariantCulture);
            double lon = double.Parse(args[2], CultureInfo.InvariantCulture);
            LocalStore.Reload();

            var box = new BBox { South = lat - 0.05, North = lat + 0.05, West = lon - 0.07, East = lon + 0.07 };
            var ways = LocalStore.Read(box);

            foreach (float mpp in new[] { 0f, 8f, 64f })
                Measure(ways, lat, lon, mpp);
            return 0;
        }

        private static void Measure(List<OsmWay> ways, double lat0, double lon0, float mpp)
        {
            int total = 0, perfect = 0, holes = 0, overlaps = 0, failed = 0;
            double worst = 1, areaSum = 0, triSum = 0;
            double mLat = 6371010.0 * Geo.DegToRad, mLon = mLat * Math.Cos(lat0 * Geo.DegToRad);
            var ring = new List<Vector2>();

            foreach (OsmWay w in ways)
            {
                if (!w.IsArea || w.Points.Count < 4) continue;
                ring.Clear();
                Vector2 last = new Vector2(float.NaN, float.NaN);
                for (int i = 0; i < w.Points.Count; i++)
                {
                    var p = new Vector2((float)((w.Points[i].Lon - lon0) * mLon), (float)((w.Points[i].Lat - lat0) * mLat));
                    if (mpp > 0f)
                    {
                        // przerzedzenie jak MapRenderer.Project (1.4 px), ostatni punkt zawsze
                        p = new Vector2(p.x / mpp, -p.y / mpp);
                        if (i > 0 && i < w.Points.Count - 1 && (p - last).sqrMagnitude < 1.4f * 1.4f) continue;
                    }
                    ring.Add(p);
                    last = p;
                }
                if (ring.Count < 3) continue;

                double area = Math.Abs(Shoelace(ring));
                if (area < 1e-3) continue;
                total++;

                List<int> t = Polygon.Triangulate(ring);
                if (t == null) { failed++; holes++; continue; }

                double sum = 0;
                for (int i = 0; i + 2 < t.Count; i += 3)
                {
                    Vector2 a = ring[t[i]], b = ring[t[i + 1]], c = ring[t[i + 2]];
                    sum += Math.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) * 0.5;
                }
                double ratio = sum / area;
                areaSum += area; triSum += sum;
                if (Math.Abs(ratio - 1) < 0.005) perfect++;
                else if (ratio < 1) holes++;
                else overlaps++;
                if (Math.Abs(ratio - 1) > Math.Abs(worst - 1)) worst = ratio;
            }

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,-22} obszarow {1,6}  dokladnie {2,6} ({3:P2})  dziury {4,4}  zakladki {5,4}  nieudane {6,3}  suma pol {7:F4}  najgorszy {8:F3}",
                mpp == 0f ? "pelny obrys (3D)" : string.Format("mapa {0} m/px", mpp),
                total, perfect, perfect / (double)Math.Max(1, total), holes, overlaps, failed,
                triSum / Math.Max(1e-9, areaSum), worst));
        }

        private static double Shoelace(List<Vector2> p)
        {
            double s = 0;
            for (int i = 0, n = p.Count; i < n; i++)
            {
                Vector2 a = p[i], b = p[(i + 1) % n];
                s += (double)a.x * b.y - (double)b.x * a.y;
            }
            return s * 0.5;
        }
    }
}
