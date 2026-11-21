using System;

namespace OSMRoads
{
    /// <summary>
    /// Matematyka kompensacji ugiecia siatki terenu, osobno od GroundSnapper, zeby dalo
    /// sie ja testowac offline (tools/OSMView, polecenie "sag") bez KSP.
    ///
    /// Teren jest rysowany jako siatka czworokatow, liniowo interpolowana miedzy
    /// wierzcholkami. W zaglebieniu cieciwa lezy NAD prawdziwa powierzchnia o okolo
    /// c * s^2 / 8 (c = wkleslosc terenu w 1/m, s = odstep wierzcholkow siatki w m),
    /// wiec droga polozona na dokladnej wysokosci PQS tonie w tej siatce.
    /// Odstep wierzcholkow PQS rosnie z odleglocia od kamery (kwadrat ma zblizona
    /// wielkosc katowa), wiec zapas rosnie z kwadratem odleglosci.
    /// </summary>
    public static class SagMath
    {
        /// <summary>Stosunek odleglosci od kamery do odstepu wierzcholkow siatki terenu.</summary>
        public static float SagSpan = 16f;

        /// <summary>Ponizej tej wkleslosci (1/m) i tak liczymy zapas: PQS ma szum drobniejszy
        /// niz siatka 23 m z TerrainGrid, wiec "plaski" teren nie jest idealnie plaski.</summary>
        public static float MinConcavity = 0.0006f;

        /// <summary>Gorna granica zapasu (m) - dalej droga i tak jest tylko plama koloru.</summary>
        public static float MaxSagLift = 4f;

        /// <summary>Wklesla czesc krzywizny terenu (1/m) w punkcie, z siatki terenu kafla.
        /// Druga roznica po obu osiach; wypukle (ujemne) traktujemy jak zero - tam siatka
        /// lezy POD prawdziwa powierzchnia i droga jej nie dotyka.</summary>
        public static double Concavity(TerrainGrid g, double lat, double lon, double radius)
        {
            double h0 = g.HeightAt(lat, lon);
            double hn = g.HeightAt(lat + g.DLat, lon), hs = g.HeightAt(lat - g.DLat, lon);
            double he = g.HeightAt(lat, lon + g.DLon), hw = g.HeightAt(lat, lon - g.DLon);

            double sLat = g.DLat * radius * Geo.DegToRad;
            double sLon = g.DLon * radius * Geo.DegToRad * Math.Cos(lat * Geo.DegToRad);
            if (sLat < 1e-3 || sLon < 1e-3) return 0.0;

            double cLat = (hn + hs - 2.0 * h0) / (sLat * sLat);
            double cLon = (he + hw - 2.0 * h0) / (sLon * sLon);
            return Math.Max(0.0, Math.Max(cLat, cLon));
        }

        /// <summary>Zapas nad dokladnym terenem (m) dla punktu w danej odleglosci od kamery.</summary>
        public static double SagLift(double distance, double concavity, float k)
        {
            double s = distance / SagSpan;
            double c = Math.Max(concavity, MinConcavity);
            double lift = k * c * s * s / 8.0;
            return lift > MaxSagLift ? MaxSagLift : lift;
        }
    }
}
