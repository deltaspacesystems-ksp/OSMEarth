using System;
using System.Collections.Generic;
using System.IO;

namespace OSMRoads
{
    /// <summary>
    /// Miasta calego swiata w jednym malym pliku (places.idx obok paczek).
    ///
    /// Etykiety przy duzym oddaleniu nie moga pochodzic z kafli mapy: kafli
    /// ponad 150 m/px nie ma, a przeczytanie kontynentu z paczek to gigabajty.
    /// Plik powstaje raz narzedziem tools/PlacesIndex i ma tylko city i town -
    /// wiosek sa miliony, a i tak pokazuje sie je dopiero z danych kafla.
    ///
    /// Format: "OSPL", wersja, liczba, potem na miejsce: float lat, float lon,
    /// byte ranga (0 city, 1 town), string nazwa (BinaryWriter, UTF-8).
    /// </summary>
    public static class WorldPlaces
    {
        public const uint Magic = 0x4C50534F;      // "OSPL"
        public const int Version = 1;
        public const string FileName = "places.idx";

        public struct Place
        {
            public float Lat, Lon;
            public byte Rank;
            public string Name;
        }

        public static void Write(string path, List<Place> places)
        {
            string tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(places.Count);
                foreach (Place p in places)
                {
                    w.Write(p.Lat);
                    w.Write(p.Lon);
                    w.Write(p.Rank);
                    w.Write(p.Name);
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static Place[] Read(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var r = new BinaryReader(fs))
            {
                if (r.ReadUInt32() != Magic) throw new IOException("zly naglowek places.idx");
                if (r.ReadInt32() != Version) throw new IOException("inna wersja places.idx");
                int n = r.ReadInt32();
                var list = new Place[n];
                for (int i = 0; i < n; i++)
                {
                    list[i].Lat = r.ReadSingle();
                    list[i].Lon = r.ReadSingle();
                    list[i].Rank = r.ReadByte();
                    list[i].Name = r.ReadString();
                }
                return list;
            }
        }

        public static int RankOf(string kind)
        {
            switch (kind)
            {
                case "city": return 0;
                case "town": return 1;
                case "suburb": return 2;
                case "village": return 3;
                default: return 4;
            }
        }
    }
}
