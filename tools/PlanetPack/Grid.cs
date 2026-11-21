using System;
using OSMRoads;

namespace PlanetPack
{
    /// <summary>
    /// Trzy siatki, na ktorych stoi caly podzial:
    ///
    ///   komorka paczki  0.05 st.  - to, co czyta mod (PackFormat.CellOf)
    ///   stopien         1 st.     - jednostka histogramu i plikow zrzutu
    ///   kwadrat         10 st.    - jedna paczka na dysku
    ///
    /// Siatki sa zagniezdzone (20 komorek na stopien, 10 stopni na kwadrat),
    /// wiec kazda komorka lezy w dokladnie jednym stopniu i jednym kwadracie.
    /// To jest warunek, zeby zaden obiekt nie trafil do dwoch paczek naraz
    /// w tej samej komorce.
    /// </summary>
    internal static class Grid
    {
        public const int Deg1Count = 180 * 360;
        public const int SquareCount = 18 * 36;

        public static int FloorDiv(int a, int b)
        {
            int q = a / b;
            if ((a % b) != 0 && ((a < 0) ^ (b < 0))) q--;
            return q;
        }

        private static int Deg1Index(int latDeg, int lonDeg)
        {
            if (latDeg < -90) latDeg = -90;
            if (latDeg > 89) latDeg = 89;
            if (lonDeg < -180) lonDeg = -180;
            if (lonDeg > 179) lonDeg = 179;
            return (latDeg + 90) * 360 + (lonDeg + 180);
        }

        /// <summary>Stopien, w ktorym lezy wezel (wspolrzedne w 1e-7 st.).</summary>
        public static int Deg1OfE7(int latE7, int lonE7)
        {
            return Deg1Index(FloorDiv(latE7, 10000000), FloorDiv(lonE7, 10000000));
        }

        /// <summary>Stopien, w ktorym lezy komorka paczki. Liczone z indeksu
        /// komorki, a nie ze wspolrzednych - inaczej blad zaokraglenia na
        /// granicy stopnia moglby rozdzielic komorke miedzy dwa pliki.</summary>
        public static int Deg1OfCell(long cellKey)
        {
            int iLat = (int)(cellKey >> 32);
            int iLon = (int)(uint)cellKey;
            return Deg1Index(FloorDiv(iLat, 20), FloorDiv(iLon, 20));
        }

        public static int SquareOfDeg1(int d1)
        {
            int la = d1 / 360, lo = d1 % 360;
            return (la / 10) * 36 + (lo / 10);
        }

        public static void SquareCorner(int sq, out int south, out int west)
        {
            south = (sq / 36) * 10 - 90;
            west = (sq % 36) * 10 - 180;
        }

        public static string SquareName(int sq)
        {
            int s, w;
            SquareCorner(sq, out s, out w);
            return string.Format("p_{0}{1:00}{2}{3:000}",
                                 s < 0 ? 's' : 'n', Math.Abs(s),
                                 w < 0 ? 'w' : 'e', Math.Abs(w));
        }

        /// <summary>Odwrotnosc SquareName, do filtra --squares.</summary>
        public static int ParseSquare(string name)
        {
            string n = name.StartsWith("p_") ? name.Substring(2) : name;
            if (n.Length != 7) return -1;
            int lat = int.Parse(n.Substring(1, 2)) * (n[0] == 's' ? -1 : 1);
            int lon = int.Parse(n.Substring(4, 3)) * (n[3] == 'w' ? -1 : 1);
            if (lat % 10 != 0 || lon % 10 != 0) return -1;
            return ((lat + 90) / 10) * 36 + (lon + 180) / 10;
        }

        public static void Deg1Corner(int d1, out int latDeg, out int lonDeg)
        {
            latDeg = d1 / 360 - 90;
            lonDeg = d1 % 360 - 180;
        }

        public static long CellOfE7(int latE7, int lonE7)
        {
            int iLat, iLon;
            PackFormat.CellOf(latE7 / PackFormat.CoordScale, lonE7 / PackFormat.CoordScale,
                              out iLat, out iLon);
            return PackFormat.CellKey(iLat, iLon);
        }
    }
}
