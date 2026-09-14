using System.IO;

namespace OSMRoads
{
    /// <summary>
    /// Format lokalnego zbioru OSM: jeden .bin z danymi + jeden .idx z mapa
    /// komorka -> (offset, dlugosc). Ten sam pomysl, co web.color.bin/.idx Mirage'a.
    ///
    /// Po co nie miliony malych plikow: planeta w siatce 0.05 stopnia to ~26 mln
    /// komorek. Nawet gdyby zapisac tylko te z danymi, system plikow by tego
    /// nie polubil. Jeden plik + Seek jest szybszy i znosi sie po sieci.
    ///
    /// Wspolrzedne ida jako delty w jednostkach 1e-7 stopnia, zygzakiem i varintem:
    /// kolejne wezly drogi roznia sie o metry, wiec zwykle mieszcza sie w 2 bajtach
    /// zamiast w 8.
    /// </summary>
    public static class PackFormat
    {
        public const uint Magic = 0x4B50534F;      // "OSPK"

        /// <summary>3 = doszly nazwy (ulic, budynkow) i punkty place z nazwami.
        /// 2 = kazda komorka skompresowana deflate + tablica stringow.
        /// Wersja 1 zapisywala surowe bajty i wychodzila na 84% rozmiaru .pbf,
        /// bo .pbf jest zlibem, a nazwy tagow powtarzaly sie miliony razy.</summary>
        public const int Version = 3;

        /// <summary>Bok komorki w stopniach. 0.05 stopnia to ~5.5 km na rowniku -
        /// kilka komorek na kafel, wiec odczyt zostaje drobny, a indeks maly.</summary>
        public const double CellDeg = 0.05;

        public const double CoordScale = 1e7;

        // rodzaje obiektow w strumieniu
        public const byte KindRoad = 1;
        public const byte KindBuilding = 2;
        public const byte KindArea = 3;

        /// <summary>Punkt z nazwa: miasto, wies, dzielnica. Jedna wspolrzedna
        /// zamiast obrysu, plus indeks nazwy i indeks typu miejsca.</summary>
        public const byte KindPlace = 4;

        public static long CellKey(int iLat, int iLon)
        {
            return ((long)iLat << 32) | (uint)iLon;
        }

        public static void CellOf(double lat, double lon, out int iLat, out int iLon)
        {
            iLat = (int)System.Math.Floor(lat / CellDeg);
            iLon = (int)System.Math.Floor(lon / CellDeg);
        }

        /// <summary>
        /// Kompresja NA KOMORKE, nie na caly plik: gdyby zdeflatowac calosc,
        /// trzeba by ja rozpakowac od poczatku, zeby dojsc do jednej komorki.
        /// Tak zostaje losowy dostep przez Seek, a placimy tylko rozpakowaniem
        /// tego jednego kawalka.
        /// </summary>
        public static byte[] Deflate(byte[] raw)
        {
            using (var ms = new MemoryStream())
            {
                using (var ds = new System.IO.Compression.DeflateStream(
                           ms, System.IO.Compression.CompressionLevel.Optimal, true))
                    ds.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }

        public static byte[] Inflate(byte[] packed, int rawLength)
        {
            var outBuf = new byte[rawLength];
            using (var ms = new MemoryStream(packed))
            using (var ds = new System.IO.Compression.DeflateStream(
                       ms, System.IO.Compression.CompressionMode.Decompress))
            {
                int done = 0;
                while (done < rawLength)
                {
                    int n = ds.Read(outBuf, done, rawLength - done);
                    if (n <= 0) break;
                    done += n;
                }
            }
            return outBuf;
        }

        // --- zygzak + varint ---

        public static void WriteVarInt(BinaryWriter w, long value)
        {
            ulong z = (ulong)((value << 1) ^ (value >> 63));
            while (z >= 0x80)
            {
                w.Write((byte)(z | 0x80));
                z >>= 7;
            }
            w.Write((byte)z);
        }

        public static long ReadVarInt(BinaryReader r)
        {
            ulong z = 0;
            int shift = 0;
            while (true)
            {
                byte b = r.ReadByte();
                z |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return (long)(z >> 1) ^ -(long)(z & 1);
        }
    }
}
