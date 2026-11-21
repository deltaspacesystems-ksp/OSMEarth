using System;
using System.Collections.Generic;
using OSMRoads;

namespace OSMView
{
    /// <summary>
    ///   osmview sag
    ///
    /// Sprawdza kompensacje ugiecia siatki terenu (SagMath) na modelu tego, co widac
    /// w grze: teren rysowany jako siatka czworokatow, liniowo interpolowana miedzy
    /// wierzcholkami, z rozmiarem czworokata rosnacym z odlegloscia od kamery
    /// (drzewo czworkowe, potegi dwojki, ~odleglosc/16). Droga lezy na DOKLADNEJ
    /// wysokosci terenu plus zapas; "utopiona" znaczy, ze siatka terenu wystaje ponad
    /// jezdnie. Test liczy to dla setek wierzcholkow drog w kilku odleglosciach.
    ///
    /// Teren zawiera skladnik drobniejszy niz siatka 23 m z TerrainGrid (jak prawdziwy
    /// PQS), ktorego kompensacja nie moze znac - to sprawdza, czy podloga wkleslosci
    /// (MinConcavity) go pokrywa.
    /// </summary>
    internal static class SagTest
    {
        private const double R = 6371010.0;
        private const double Lat0 = 50.93, Lon0 = 22.23;

        private static double Terrain(double x, double z)
        {
            double lat, lon;
            Geo.FromLocalMeters(x, z, Lat0, Lon0, R, out lat, out lon);
            return TerrainGrid.TrueHeight(lat, lon)
                 + 0.5 * Math.Sin(x / 9.0) * Math.Sin(z / 11.0)     // drobny relief < 23 m
                 + 0.35 * Math.Sin((x + z) / 6.5);
        }

        /// <summary>Wysokosc WIDOCZNEJ siatki terenu w punkcie: rozmiar czworokata z odleglosci od kamery.</summary>
        private static double Mesh(double x, double z, double camX, double camY, double camZ, double span)
        {
            double h = Terrain(x, z);
            double d = Math.Sqrt((x - camX) * (x - camX) + (h - camY) * (h - camY) + (z - camZ) * (z - camZ));
            double q = 2.0;
            while (q * 2.0 <= Math.Max(2.0, d / span) * 1.4142 && q < 2048.0) q *= 2.0;   // najblizsza potega dwojki

            double x0 = Math.Floor(x / q) * q, z0 = Math.Floor(z / q) * q;
            double u = (x - x0) / q, v = (z - z0) / q;
            double ha = Terrain(x0, z0), hb = Terrain(x0 + q, z0);
            double hc = Terrain(x0, z0 + q), hd = Terrain(x0 + q, z0 + q);
            return u >= v ? ha + u * (hb - ha) + v * (hd - hb)
                          : ha + u * (hd - hc) + v * (hc - ha);
        }

        public static int Run(string[] args)
        {
            // siatka terenu kafla: 150 wezlow na 3.8 km, ~25 m - jak w grze (64 na 1.5 km)
            double dLat = 1900.0 / (R * Geo.DegToRad), dLon = dLat / Math.Cos(Lat0 * Geo.DegToRad);
            BBox box;
            box.South = Lat0 - dLat; box.North = Lat0 + dLat; box.West = Lon0 - dLon; box.East = Lon0 + dLon;
            TerrainGrid grid = TerrainGrid.Create(box, 150, 0.0);
            grid.Fill((la, lo) =>
            {
                double e, n; Geo.ToLocalMeters(la, lo, Lat0, Lon0, R, out e, out n);
                return Terrain(e, n);
            });

            // drogi: 80 lamanych o lagodnym skrecie, wierzcholek co 6 m, plus krawedzie jezdni +-3 m
            var rnd = new Random(11);
            var pts = new List<double[]>();
            for (int r = 0; r < 80; r++)
            {
                double ang = rnd.NextDouble() * Math.PI * 2, rad = Math.Sqrt(rnd.NextDouble()) * 1500.0;
                double x = Math.Cos(ang) * rad, z = Math.Sin(ang) * rad;
                double hd = rnd.NextDouble() * Math.PI * 2;
                for (int i = 0; i < 250; i++)
                {
                    hd += (rnd.NextDouble() - 0.5) * 0.04;
                    x += Math.Cos(hd) * 6.0; z += Math.Sin(hd) * 6.0;
                    if (Math.Abs(x) > 1800 || Math.Abs(z) > 1800) break;
                    foreach (double side in new[] { -3.0, 0.0, 3.0 })
                        pts.Add(new[] { x - Math.Sin(hd) * side, z + Math.Cos(hd) * side });
                }
            }
            Console.WriteLine("teren testowy: fale 450 m - 8 km + drobny relief < 23 m; {0:N0} wierzcholkow drog", pts.Count);

            double[] bins = { 150, 400, 800, 1600 };
            string[] binName = { "<150 m", "150-400 m", "400-800 m", "800-1600 m" };
            int worstFails = 0;

            foreach (double camH in new[] { 2.0, 250.0 })
            {
                Console.WriteLine();
                Console.WriteLine("=== kamera {0} m nad terenem ===", camH);
                double camY = Terrain(0, 0) + camH;

                // 1 = stary stały zapas 0.3; 2 = 0.4 (nowy domyslny) bez kompensacji; potem kompensacja x1, x2
                var variants = new (string name, float baseLift, float k)[]
                {
                    ("stary: 0.30 m stale        ", 0.30f, 0f),
                    ("0.40 m stale               ", 0.40f, 0f),
                    ("0.40 + kompensacja x1      ", 0.40f, 1f),
                    ("0.40 + kompensacja x2      ", 0.40f, 2f),
                };

                Console.WriteLine("  odleglosc   wariant                       utopione  >30 cm   wisi >1 m  srednio wisi");
                for (int b = 0; b < bins.Length; b++)
                {
                    double lo = b == 0 ? 0 : bins[b - 1], hi = bins[b];
                    var inBin = pts.FindAll(p => { double dd = Math.Sqrt(p[0] * p[0] + p[1] * p[1]); return dd >= lo && dd < hi; });
                    if (inBin.Count < 50) continue;

                    foreach (var vr in variants)
                    {
                        int buried = 0, deep = 0, high = 0; double sumUp = 0; int nUp = 0;
                        foreach (double[] p in inBin)
                        {
                            double h = Terrain(p[0], p[1]);
                            double d3 = Math.Sqrt(p[0] * p[0] + (h - camY) * (h - camY) + p[1] * p[1]);
                            double lift = vr.baseLift;
                            if (vr.k > 0f)
                            {
                                double la, lo2; Geo.FromLocalMeters(p[0], p[1], Lat0, Lon0, R, out la, out lo2);
                                lift += SagMath.SagLift(d3, SagMath.Concavity(grid, la, lo2, R), vr.k);
                            }
                            double m = Mesh(p[0], p[1], 0, camY, 0, SagMath.SagSpan);
                            double over = m - (h + lift);            // > 0: siatka terenu wystaje ponad jezdnie
                            if (over > 0.03) buried++;
                            if (over > 0.30) deep++;
                            double up = (h + lift) - m;              // > 0: jezdnia nad widoczna siatka
                            if (up > 1.0) high++;
                            if (up > 0) { sumUp += up; nUp++; }
                        }
                        double n = inBin.Count;
                        Console.WriteLine("  {0,-11} {1}  {2,6:P1}  {3,6:P1}   {4,6:P1}    {5,5:F2} m",
                                          binName[b], vr.name, buried / n, deep / n, high / n, nUp > 0 ? sumUp / nUp : 0.0);
                        if (vr.k == 1f && b >= 1 && b <= 2 && camH == 2.0 && deep / n > 0.10) worstFails++;
                    }
                    Console.WriteLine();
                }
            }

            Console.WriteLine(worstFails == 0
                ? "wynik: kompensacja x1 utrzymuje utopione >30 cm ponizej 10% w srodkowych odleglosciach"
                : "wynik: kompensacja x1 NIE wystarcza w srodkowych odleglosciach - zwieksz domyslna wartosc");
            return 0;
        }
    }
}
