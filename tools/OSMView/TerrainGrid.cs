using System;

namespace OSMRoads
{
    /// <summary>
    /// Offline'owy odpowiednik TerrainGrid z moda: ta sama siatka, ta sama
    /// interpolacja dwuliniowa, tylko wysokosci nie z PQS, a z funkcji terenu.
    ///
    /// To ma znaczenie: w grze kafel jest budowany z siatki 64x64 (wezel co
    /// ~23 m), a teren PQS jest liczony w pelnej rozdzielczosci. Tester robi
    /// dokladnie to samo - siatka dla builderow, prawdziwa funkcja dla renderu
    /// terenu - wiec rozjazd drog i ziemi widac tu tak samo jak w KSP.
    /// </summary>
    public class TerrainGrid
    {
        public double South, West, DLat, DLon;
        public int N;
        public double Alt0;

        private double[] h;

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

        /// <summary>Zastepuje Sample(CelestialBody): ten sam uklad wezlow.</summary>
        public void Fill(Func<double, double, double> height)
        {
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N; i++)
                    h[j * N + i] = height(South + j * DLat, West + i * DLon);
            Alt0 = HeightAt(South + (N - 1) * DLat * 0.5, West + (N - 1) * DLon * 0.5);
        }

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

        /// <summary>
        /// "Prawdziwy" teren testowy: pagorki ~8 km, faldy ~1.4 km i drobne garby
        /// ~450 m. Spadki 1.5-3%, jak na Wyzynie Lubelskiej. Na plaskim terenie
        /// rozjazd siatki i PQS bylby niewidoczny, a to wlasnie on jest do sprawdzenia.
        /// </summary>
        public static double TrueHeight(double lat, double lon)
        {
            const double T = 2 * Math.PI;
            return 210.0
                 + 18.0 * Math.Sin(lat * T / 0.070) * Math.Cos(lon * T / 0.110)
                 + 6.0 * Math.Sin((lat + lon) * T / 0.013)
                 + 2.0 * Math.Sin(lat * T / 0.0040) * Math.Sin(lon * T / 0.0050);
        }
    }
}
