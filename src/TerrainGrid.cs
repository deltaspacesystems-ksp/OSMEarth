using System;
using System.Collections;

namespace OSMRoads
{
    /// <summary>
    /// Wysokosci terenu spróbkowane z PQS na regularnej siatce, raz na kafel.
    ///
    /// Po co: PQS wolno wywolywac tylko z glownego watku, a to wlasnie probkowanie
    /// (dziesiatki tysiecy wywolan na kafel) bylo glownym zrodlem zwiech. Teraz
    /// probkujemy raz, rozlozone na kilka klatek, a watek roboczy tylko interpoluje
    /// dwuliniowo z gotowej tablicy double[] - to jest czysty odczyt, bezpieczny
    /// watkowo i bez zadnego kontaktu z Unity.
    ///
    /// Kosztem jest rozdzielczosc: przy siatce N x N na kafel 1500 m odstep wynosi
    /// 1500/(N-1) metrow, a teren miedzy wezlami jest wygladzony.
    /// </summary>
    public class TerrainGrid
    {
        public double South, West, DLat, DLon;
        public int N;
        public double Alt0;          // wysokosc w srodku kafla - punkt odniesienia mesha

        private double[] h;

        /// <summary>
        /// Margines: Overpass z "out geom;" zwraca CALA geometrie drog przecinajacych
        /// bbox, wiec wezly potrafia lezec sporo poza kaflem. Bez zapasu takie punkty
        /// dostawalyby wysokosc przyklejona do krawedzi siatki.
        /// </summary>
        public static TerrainGrid Create(BBox box, int n, double marginFraction)
        {
            double mLat = (box.North - box.South) * marginFraction;
            double mLon = (box.East - box.West) * marginFraction;

            var g = new TerrainGrid();
            g.N = Math.Max(4, n);
            g.South = box.South - mLat;
            g.West = box.West - mLon;
            g.DLat = ((box.North + mLat) - g.South) / (g.N - 1);
            g.DLon = ((box.East + mLon) - g.West) / (g.N - 1);
            g.h = new double[g.N * g.N];
            return g;
        }

        /// <summary>Probkowanie PQS - TYLKO glowny watek. Rozlozone na klatki,
        /// zeby nie zrobic z tego nowej zwiechy.</summary>
        public IEnumerator Sample(CelestialBody body, int rowsPerFrame)
        {
            if (body.pqsController == null) yield break;

            for (int j = 0; j < N; j++)
            {
                double lat = South + j * DLat;
                for (int i = 0; i < N; i++)
                {
                    double lon = West + i * DLon;
                    h[j * N + i] = RawAltitude(body, lat, lon);
                }

                if (rowsPerFrame > 0 && (j % rowsPerFrame) == rowsPerFrame - 1)
                    yield return null;
            }

            Alt0 = HeightAt(South + (N - 1) * DLat * 0.5, West + (N - 1) * DLon * 0.5);
        }

        public static double RawAltitude(CelestialBody body, double lat, double lon)
        {
            if (body.pqsController == null) return 0.0;
            double alt = body.pqsController.GetSurfaceHeight(body.GetRelSurfaceNVector(lat, lon))
                         - body.Radius;
            if (body.ocean && alt < 0.0) alt = 0.0;
            return alt;
        }

        /// <summary>Interpolacja dwuliniowa. Czysty odczyt - wolno wolac z watku roboczego.</summary>
        public double HeightAt(double lat, double lon)
        {
            double fx = (lon - West) / DLon;
            double fy = (lat - South) / DLat;

            if (fx < 0) fx = 0; else if (fx > N - 1) fx = N - 1;
            if (fy < 0) fy = 0; else if (fy > N - 1) fy = N - 1;

            int x0 = (int)fx, y0 = (int)fy;
            int x1 = x0 + 1 < N ? x0 + 1 : x0;
            int y1 = y0 + 1 < N ? y0 + 1 : y0;
            double tx = fx - x0, ty = fy - y0;

            double a = h[y0 * N + x0], b = h[y0 * N + x1];
            double c = h[y1 * N + x0], d = h[y1 * N + x1];

            return (a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty;
        }
    }
}
