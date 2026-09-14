using System;

namespace OSMRoads
{
    public struct GeoPoint
    {
        public double Lat;
        public double Lon;
        public GeoPoint(double lat, double lon) { Lat = lat; Lon = lon; }
    }

    public struct BBox
    {
        public double South, West, North, East;
        public override string ToString()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:F5},{1:F5},{2:F5},{3:F5}", South, West, North, East);
        }
    }

    public static class Geo
    {
        public const double DegToRad = Math.PI / 180.0;

        /// <summary>
        /// Promien Ziemi w realu. Sluzy TYLKO do pokazania uzytkownikowi,
        /// ile rzeczywistego terenu OSM odpowiada danemu promieniowi w grze.
        /// </summary>
        public const double RealEarthRadius = 6371010.0;

        /// <summary>
        /// BBox pokrywajacy 'radiusMeters' mierzone W SWIECIE GRY.
        /// Uwaga: bodyRadius to promien planety w grze (SOL Quarter = 1 592 752 m),
        /// wiec 3 km w grze to 3 km * 4 = 12 km rzeczywistego terenu OSM.
        /// To jest zamierzone - geografia (lat/lon) jest 1:1, skala liniowa nie.
        /// </summary>
        public static BBox BoxAround(double lat, double lon, double radiusMeters, double bodyRadius)
        {
            double dLat = radiusMeters / (bodyRadius * DegToRad);
            double cosLat = Math.Cos(lat * DegToRad);
            if (Math.Abs(cosLat) < 1e-6) cosLat = 1e-6;          // biegun
            double dLon = dLat / cosLat;

            BBox b;
            b.South = Clamp(lat - dLat, -89.9, 89.9);
            b.North = Clamp(lat + dLat, -89.9, 89.9);
            b.West = lon - dLon;
            b.East = lon + dLon;
            return b;
        }

        /// <summary>
        /// Lat/lon -> lokalne metry (east, north) w plaszczyznie stycznej zaczepionej
        /// w (lat0, lon0). Uzywa promienia planety W GRZE, wiec geometria automatycznie
        /// kurczy sie razem ze swiatem.
        /// </summary>
        public static void ToLocalMeters(double lat, double lon, double lat0, double lon0,
                                         double bodyRadius, out double east, out double north)
        {
            double dLon = lon - lon0;
            if (dLon > 180.0) dLon -= 360.0;
            else if (dLon < -180.0) dLon += 360.0;

            east = dLon * DegToRad * bodyRadius * Math.Cos(lat0 * DegToRad);
            north = (lat - lat0) * DegToRad * bodyRadius;
        }

        /// <summary>
        /// Odwrotnosc ToLocalMeters. Potrzebna, bo wierzcholek gotowej siatki zna
        /// tylko swoje metry lokalne, a PQS pyta sie o lat/lon.
        /// </summary>
        public static void FromLocalMeters(double east, double north, double lat0, double lon0,
                                           double bodyRadius, out double lat, out double lon)
        {
            lat = lat0 + north / (DegToRad * bodyRadius);

            double cosLat = Math.Cos(lat0 * DegToRad);
            if (Math.Abs(cosLat) < 1e-6) cosLat = 1e-6;          // biegun
            lon = lon0 + east / (DegToRad * bodyRadius * cosLat);
        }

        /// <summary>Spadek plaszczyzny stycznej wzgledem kuli - inaczej drogi
        /// oddalone od srodka kafla zawisly by w powietrzu.</summary>
        public static double CurvatureDrop(double east, double north, double bodyRadius)
        {
            return (east * east + north * north) / (2.0 * bodyRadius);
        }

        public static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
