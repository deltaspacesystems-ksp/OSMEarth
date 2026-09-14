using System;
using System.Collections.Generic;

namespace OSMRoads
{
    /// <summary>
    /// Siatka kafli w lat/lon. Rozmiar w stopniach liczony jest z promienia
    /// planety W GRZE, wiec kafel ma zadana wielkosc w metrach swiata, niezaleznie
    /// od tego, czy Ziemia jest 1:1 czy 1/4.
    ///
    /// dLon rosnie z szerokoscia geograficzna, zeby kafle zostawaly mniej wiecej
    /// kwadratowe zamiast robic sie paskami przy biegunie.
    /// </summary>
    public struct TileGrid
    {
        public double DLat;
        public double BodyRadius;
        public double SizeMeters;

        public TileGrid(double sizeMeters, double bodyRadius)
        {
            SizeMeters = sizeMeters;
            BodyRadius = bodyRadius;
            DLat = sizeMeters / (bodyRadius * Geo.DegToRad);
        }

        public double DLonAt(int iLat)
        {
            double centerLat = (iLat + 0.5) * DLat;
            double c = Math.Cos(centerLat * Geo.DegToRad);
            if (Math.Abs(c) < 0.02) c = 0.02;      // nie pozwol kaflom eksplodowac przy biegunie
            return DLat / c;
        }

        public void IndexOf(double lat, double lon, out int iLat, out int iLon)
        {
            iLat = (int)Math.Floor(lat / DLat);
            iLon = (int)Math.Floor(lon / DLonAt(iLat));
        }

        public BBox BoxOf(int iLat, int iLon)
        {
            double dLon = DLonAt(iLat);
            BBox b;
            b.South = Geo.Clamp(iLat * DLat, -89.9, 89.9);
            b.North = Geo.Clamp((iLat + 1) * DLat, -89.9, 89.9);
            b.West = iLon * dLon;
            b.East = (iLon + 1) * dLon;
            return b;
        }

        public void CenterOf(int iLat, int iLon, out double lat, out double lon)
        {
            double dLon = DLonAt(iLat);
            lat = (iLat + 0.5) * DLat;
            lon = (iLon + 0.5) * dLon;
        }

        public static long Key(int iLat, int iLon)
        {
            return ((long)iLat << 32) | (uint)iLon;
        }

        /// <summary>
        /// Odleglosc w metrach od punktu do SRODKA kafla.
        ///
        /// Wszystkie decyzje o kaflach (co wczytac, co najpierw, co wyrzucic)
        /// musza liczyc w metrach, a nie w roznicy indeksow: kolumna o tym samym
        /// numerze lezy w kazdym rzedzie gdzie indziej, bo szerokosc kafla
        /// w stopniach zalezy od cosinusa szerokosci geograficznej rzedu.
        /// </summary>
        public double DistanceM(long key, double lat, double lon)
        {
            int iLat = (int)(key >> 32);
            int iLon = (int)(uint)key;
            double cLat, cLon;
            CenterOf(iLat, iLon, out cLat, out cLon);

            double dLon = cLon - lon;
            if (dLon > 180.0) dLon -= 360.0;
            else if (dLon < -180.0) dLon += 360.0;

            double dN = (cLat - lat) * Geo.DegToRad * BodyRadius;
            double dE = dLon * Geo.DegToRad * BodyRadius * Math.Cos(lat * Geo.DegToRad);
            return Math.Sqrt(dN * dN + dE * dE);
        }

        /// <summary>
        /// Kafle, ktorych srodek lezy w promieniu radiusM od punktu, od najblizszego.
        ///
        /// Kazdy rzad dostaje WLASNY indeks kolumny policzony z dlugosci geograficznej
        /// punktu. Wczesniej gra brala (srodek + dx, srodek + dy), a przy 51 st. N
        /// kolumna o tym samym numerze uciekala o 534 m na rzad - 14 rzedow dalej
        /// o 6.4 km. Kolo wczytanych kafli bylo przez to mocno pochylone: z jednej
        /// strony dziury, z drugiej kafle poza zasiegiem, ktore od razu wypadaly
        /// i wracaly do kolejki.
        /// </summary>
        public void TilesAround(double lat, double lon, double radiusM, List<long> into)
        {
            into.Clear();
            int iLat0 = (int)Math.Floor(lat / DLat);
            int span = (int)Math.Ceiling(radiusM / SizeMeters) + 1;

            for (int dy = -span; dy <= span; dy++)
            {
                int row = iLat0 + dy;
                int iLon0 = (int)Math.Floor(lon / DLonAt(row));
                for (int dx = -span; dx <= span; dx++)
                {
                    long key = Key(row, iLon0 + dx);
                    if (DistanceM(key, lat, lon) <= radiusM) into.Add(key);
                }
            }

            double la = lat, lo = lon;
            TileGrid self = this;
            into.Sort((a, b) => self.DistanceM(a, la, lo).CompareTo(self.DistanceM(b, la, lo)));
        }
    }
}
