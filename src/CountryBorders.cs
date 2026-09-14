using System;
using System.Collections.Generic;
using System.IO;

namespace OSMRoads
{
    /// <summary>
    /// Kontury panstw - dane Natural Earth 1:110m (domena publiczna), uproszczone
    /// do ~10 tys. punktow i wbudowane w moda (GameData/OSMRoads/countries.bin).
    /// Nie ida z paczek OSM: admin_level=2 to relacje, ktorych nasz konwerter
    /// nie parsuje, a granice calego swiata ze zwyklych wegow bylyby ogromne.
    /// Dokladnosc (~2 km) wystarcza przy skali, w jakiej granice w ogole widac.
    /// </summary>
    public static class CountryBorders
    {
        public struct Country
        {
            public string Name;
            public GeoPoint[][] Rings;      // otwarte pierscienie - domykac przy rysowaniu
            public double CLat, CLon;       // srodek najwiekszego pierscienia - do etykiety
        }

        private static Country[] all;
        private static string report = "granice panstw: nie wczytane";
        public static string Report { get { return report; } }
        public static Country[] All { get { Load(); return all; } }

        private static void Load()
        {
            if (all != null) return;
            all = new Country[0];
            try
            {
                string path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/OSMRoads/countries.bin");
                if (!File.Exists(path)) { report = "granice panstw: brak countries.bin"; return; }

                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    if (r.ReadUInt32() != 0x4243534F) throw new IOException("zly naglowek countries.bin"); // "OSCB" (LE)
                    r.ReadInt32();
                    int n = r.ReadInt32();
                    var list = new Country[n];
                    for (int i = 0; i < n; i++)
                    {
                        int nameLen = r.ReadByte();
                        string name = System.Text.Encoding.UTF8.GetString(r.ReadBytes(nameLen));
                        int nRings = r.ReadUInt16();
                        var rings = new GeoPoint[nRings][];
                        double bestArea = -1; double cLat = 0, cLon = 0;
                        for (int j = 0; j < nRings; j++)
                        {
                            int nPts = r.ReadUInt16();
                            var pts = new GeoPoint[nPts];
                            double sLat = 0, sLon = 0;
                            for (int k = 0; k < nPts; k++)
                            {
                                float lat = r.ReadSingle(), lon = r.ReadSingle();
                                pts[k] = new GeoPoint(lat, lon);
                                sLat += lat; sLon += lon;
                            }
                            rings[j] = pts;
                            // przyblizona "wielkosc" pierscienia do wyboru, gdzie postawic etykiete
                            double area = nPts;
                            if (area > bestArea) { bestArea = area; cLat = sLat / nPts; cLon = sLon / nPts; }
                        }
                        list[i] = new Country { Name = name, Rings = rings, CLat = cLat, CLon = cLon };
                    }
                    all = list;
                }
                report = "granice panstw: " + all.Length;
            }
            catch (Exception e)
            {
                report = "granice panstw: blad " + e.Message;
            }
        }
    }
}
