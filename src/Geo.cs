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

        /// <summary>
        /// DOKLADNE polozenie punktu kuli na plaszczyznie stycznej w (lat0, lon0): rzut
        /// prostokatny na osie wschod/polnoc tego punktu plus spadek pod plaszczyzne.
        ///
        /// ToLocalMeters jest rownoodlegla (wschod = dLon * cos(lat0)) - wygodna do dzielenia
        /// na kafle, ale ignoruje zbieganie sie poludnikow: ten sam punkt na styku dwoch kafli
        /// wypadal w swiecie w dwoch miejscach, do ok. 20 cm od siebie (szczelina w poprzek
        /// drogi). Tutaj wynik jest spojny miedzy kaflami, bo to czysta geometria kuli.
        /// </summary>
        public static void ToTangent(double lat, double lon, double lat0, double lon0, double r,
                                     out double east, out double north, out double drop)
        {
            double p = lat * DegToRad, p0 = lat0 * DegToRad;
            double dl = lon - lon0;
            if (dl > 180.0) dl -= 360.0; else if (dl < -180.0) dl += 360.0;
            dl *= DegToRad;
            double cp = Math.Cos(p), sp = Math.Sin(p), cp0 = Math.Cos(p0), sp0 = Math.Sin(p0), cdl = Math.Cos(dl);
            east = r * cp * Math.Sin(dl);
            north = r * (cp0 * sp - sp0 * cp * cdl);
            drop = r - r * (sp0 * sp + cp0 * cp * cdl);
        }

        /// <summary>Odwrotnosc ToTangent (punkt lezy na kuli).</summary>
        public static void FromTangent(double east, double north, double lat0, double lon0, double r,
                                       out double lat, out double lon)
        {
            double p0 = lat0 * DegToRad, cp0 = Math.Cos(p0), sp0 = Math.Sin(p0);
            double u = Math.Sqrt(Math.Max(0.0, r * r - east * east - north * north));
            double sp = (north * cp0 + u * sp0) / r;
            lat = Math.Asin(Math.Max(-1.0, Math.Min(1.0, sp))) / DegToRad;
            lon = lon0 + Math.Atan2(east, u * cp0 - north * sp0) / DegToRad;
        }

        /// <summary>Spadek pod plaszczyzne styczna dla wspolrzednych z ToTangent.</summary>
        public static double TangentDrop(double east, double north, double r)
        {
            return r - Math.Sqrt(Math.Max(0.0, r * r - east * east - north * north));
        }

        public static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
