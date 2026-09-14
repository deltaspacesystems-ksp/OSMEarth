using System;
using UnityEngine;

namespace OSMRoads
{
    public class MapWaypoint
    {
        public double Lat, Lon;
        public string Name;
        public bool PushedToKsp;
    }

    /// <summary>
    /// Rzutowanie mapy: srodek w lat/lon, skala w metrach na piksel.
    ///
    /// Rownoprostokatne wokol szerokosci srodka - na obszarze planera (dziesiatki
    /// kilometrow) blad jest niewidoczny, a rachunek zostaje odwracalny, co jest
    /// potrzebne, bo klikniecie w mape musi wrocic na lat/lon.
    ///
    /// Metry licza sie z promienia planety W GRZE, wiec skala zgadza sie z tym,
    /// co pokazuje nawigacja.
    /// </summary>
    public class MapView
    {
        public double CenterLat, CenterLon;
        public float MetersPerPixel = 6f;
        public double BodyRadius = 6371010.0;
        public int Width = 800, Height = 600;

        /// <summary>
        /// Szerokosc odniesienia skali poziomej. NaN = srodek widoku (jak dawniej).
        ///
        /// Kafle mapy musza pasowac do siebie na szwach, wiec wszystkie - i sam
        /// widok - dziela JEDNA szerokosc odniesienia. Gdyby skala pozioma szla
        /// za srodkiem widoku, kazde przewiniecie na polnoc zmienialoby szerokosc
        /// kafla w pikselach i obraz rozjezdzalby sie na granicach.
        /// </summary>
        public double RefLat = double.NaN;

        private double EffLat { get { return double.IsNaN(RefLat) ? CenterLat : RefLat; } }

        public double MetersPerDegLat { get { return BodyRadius * Geo.DegToRad; } }

        public double MetersPerDegLon
        {
            get { return MetersPerDegLat * Math.Cos(EffLat * Geo.DegToRad); }
        }

        // --- zbuforowane wspolczynniki rzutowania ---
        // ToPixel bylo wywolywane raz na KAZDY punkt kazdego obiektu, a przez
        // property MetersPerDegLon liczylo przy tym Math.Cos i dwa dzielenia.
        // Przy stu tysiacach wierzcholkow to sto tysiecy cosinusow na przerysowanie.
        // Teraz caly rachunek sprowadza sie do dwoch mnozen.
        private double cLat, cLon, cRadius, cRef = double.NaN;
        private float cMpp;
        private int cW, cH;
        private double kx, ky, halfW, halfH;

        private void EnsureScale()
        {
            if (cLat == CenterLat && cLon == CenterLon && cRadius == BodyRadius &&
                cMpp == MetersPerPixel && cW == Width && cH == Height &&
                (cRef == RefLat || (double.IsNaN(cRef) && double.IsNaN(RefLat)))) return;

            cLat = CenterLat; cLon = CenterLon; cRadius = BodyRadius;
            cMpp = MetersPerPixel; cW = Width; cH = Height; cRef = RefLat;

            double mLat = BodyRadius * Geo.DegToRad;
            double mLon = mLat * Math.Cos(EffLat * Geo.DegToRad);
            double inv = 1.0 / MetersPerPixel;

            kx = mLon * inv;
            ky = mLat * inv;
            halfW = Width * 0.5;
            halfH = Height * 0.5;
        }

        public Vector2 ToPixel(double lat, double lon)
        {
            EnsureScale();

            double dLon = lon - CenterLon;
            if (dLon > 180.0) dLon -= 360.0;
            else if (dLon < -180.0) dLon += 360.0;

            double x = dLon * kx + halfW;
            double y = -(lat - CenterLat) * ky + halfH;
            return new Vector2((float)x, (float)y);
        }

        public void FromPixel(float px, float py, out double lat, out double lon)
        {
            double mLon = MetersPerDegLon;
            if (Math.Abs(mLon) < 1e-6) mLon = 1e-6;

            lon = CenterLon + (px - Width * 0.5) * MetersPerPixel / mLon;
            lat = CenterLat - (py - Height * 0.5) * MetersPerPixel / MetersPerDegLat;
        }

        /// <summary>Obszar widoczny, powiekszony o margines - zeby przewijanie
        /// nie wymagalo natychmiast nowego odczytu z dysku.</summary>
        public BBox VisibleBox(double padFraction)
        {
            double halfLat = Height * 0.5 * MetersPerPixel / MetersPerDegLat;
            double mLon = MetersPerDegLon;
            if (Math.Abs(mLon) < 1e-6) mLon = 1e-6;
            double halfLon = Width * 0.5 * MetersPerPixel / mLon;

            halfLat *= 1.0 + padFraction;
            halfLon *= 1.0 + padFraction;

            BBox b;
            b.South = Geo.Clamp(CenterLat - halfLat, -89.9, 89.9);
            b.North = Geo.Clamp(CenterLat + halfLat, -89.9, 89.9);
            b.West = CenterLon - halfLon;
            b.East = CenterLon + halfLon;
            return b;
        }

        public void PanPixels(float dx, float dy)
        {
            double mLon = MetersPerDegLon;
            if (Math.Abs(mLon) < 1e-6) mLon = 1e-6;

            CenterLon -= dx * MetersPerPixel / mLon;
            CenterLat += dy * MetersPerPixel / MetersPerDegLat;
            CenterLat = Geo.Clamp(CenterLat, -89.0, 89.0);
        }

        /// <summary>Odleglosc po wielkim kole w metrach swiata gry.</summary>
        public double DistanceM(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * Geo.DegToRad, p2 = lat2 * Geo.DegToRad;
            double dp = (lat2 - lat1) * Geo.DegToRad;
            double dl = (lon2 - lon1) * Geo.DegToRad;

            double a = Math.Sin(dp / 2) * Math.Sin(dp / 2)
                     + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2.0 * BodyRadius * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        /// <summary>Kurs poczatkowy z punktu 1 do 2, w stopniach od polnocy.</summary>
        public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * Geo.DegToRad, p2 = lat2 * Geo.DegToRad;
            double dl = (lon2 - lon1) * Geo.DegToRad;

            double y = Math.Sin(dl) * Math.Cos(p2);
            double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
            double b = Math.Atan2(y, x) / Geo.DegToRad;
            return (b + 360.0) % 360.0;
        }
    }
}
