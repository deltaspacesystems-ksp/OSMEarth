using System;
using System.Collections.Generic;
using System.IO;

namespace OSMRoads
{
    /// <summary>
    /// Budynek 3D z modeli GUGiK (CityGML LoD2): prawdziwe sciany i dachy.
    /// Wierzcholki jako przesuniecia lat/lon od kotwicy (1e-7 st.) i wysokosc w cm
    /// ponad najnizszy punkt podstawy - mod przelicza je tak samo jak dane OSM,
    /// wiec budynek trafia dokladnie na swoj obrys niezaleznie od skali planety.
    /// </summary>
    public class Lod2Building
    {
        public double Lat, Lon;              // kotwica
        public ushort RoofType;              // kod CityGML: 1000 plaski, 1030 dwuspadowy, 1040 czterospadowy...
        public byte R, G, B;                 // kolor dachu z ortofotomapy (0,0,0 = nieznany)
        public double[] VLat, VLon;          // wierzcholki
        public float[] VH;                   // wysokosc nad podstawa [m]
        public int[] Walls, Roofs;           // trojkaty (po 3 indeksy)
        public double[] FootLat, FootLon;    // obrys podstawy

        public bool HasColor { get { return R != 0 || G != 0 || B != 0; } }
    }

    /// <summary>
    /// Plik b3d_&lt;kwadrat 1 st.&gt;.bin:
    ///   "OSB3", int wersja, int liczba komorek,
    ///   komorki (iLat, iLon int32; offset int64; dlugosc, surowa dlugosc int32),
    ///   bloki Deflate: varint liczba budynkow, potem budynki (Write/Read).
    /// Komorki jak w paczkach OSM: 0.05 st., budynek nalezy do komorki swojej kotwicy.
    /// </summary>
    public static class Lod2Format
    {
        public const uint Magic = 0x3342534F;   // "OSB3"
        public const int Version = 1;

        public static string SquareOf(double lat, double lon)
        {
            int la = (int)Math.Floor(lat), lo = (int)Math.Floor(lon);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "b3d_{0}{1:00}{2}{3:000}.bin",
                                 la >= 0 ? "n" : "s", Math.Abs(la), lo >= 0 ? "e" : "w", Math.Abs(lo));
        }

        public static void Write(BinaryWriter w, Lod2Building b)
        {
            long aLat = (long)Math.Round(b.Lat * 1e7), aLon = (long)Math.Round(b.Lon * 1e7);
            PackFormat.WriteVarInt(w, aLat);
            PackFormat.WriteVarInt(w, aLon);
            w.Write(b.RoofType);
            w.Write(b.R); w.Write(b.G); w.Write(b.B);

            PackFormat.WriteVarInt(w, b.VLat.Length);
            for (int i = 0; i < b.VLat.Length; i++)
            {
                PackFormat.WriteVarInt(w, (long)Math.Round(b.VLat[i] * 1e7) - aLat);
                PackFormat.WriteVarInt(w, (long)Math.Round(b.VLon[i] * 1e7) - aLon);
                PackFormat.WriteVarInt(w, (long)Math.Round(b.VH[i] * 100.0));
            }
            WriteIdx(w, b.Walls);
            WriteIdx(w, b.Roofs);

            PackFormat.WriteVarInt(w, b.FootLat.Length);
            for (int i = 0; i < b.FootLat.Length; i++)
            {
                PackFormat.WriteVarInt(w, (long)Math.Round(b.FootLat[i] * 1e7) - aLat);
                PackFormat.WriteVarInt(w, (long)Math.Round(b.FootLon[i] * 1e7) - aLon);
            }
        }

        public static Lod2Building Read(BinaryReader r)
        {
            var b = new Lod2Building();
            long aLat = PackFormat.ReadVarInt(r), aLon = PackFormat.ReadVarInt(r);
            b.Lat = aLat / 1e7; b.Lon = aLon / 1e7;
            b.RoofType = r.ReadUInt16();
            b.R = r.ReadByte(); b.G = r.ReadByte(); b.B = r.ReadByte();

            int n = (int)PackFormat.ReadVarInt(r);
            b.VLat = new double[n]; b.VLon = new double[n]; b.VH = new float[n];
            for (int i = 0; i < n; i++)
            {
                b.VLat[i] = (aLat + PackFormat.ReadVarInt(r)) / 1e7;
                b.VLon[i] = (aLon + PackFormat.ReadVarInt(r)) / 1e7;
                b.VH[i] = PackFormat.ReadVarInt(r) / 100f;
            }
            b.Walls = ReadIdx(r);
            b.Roofs = ReadIdx(r);

            int f = (int)PackFormat.ReadVarInt(r);
            b.FootLat = new double[f]; b.FootLon = new double[f];
            for (int i = 0; i < f; i++)
            {
                b.FootLat[i] = (aLat + PackFormat.ReadVarInt(r)) / 1e7;
                b.FootLon[i] = (aLon + PackFormat.ReadVarInt(r)) / 1e7;
            }
            return b;
        }

        private static void WriteIdx(BinaryWriter w, int[] t)
        {
            PackFormat.WriteVarInt(w, t.Length);
            foreach (int i in t) PackFormat.WriteVarInt(w, i);
        }

        private static int[] ReadIdx(BinaryReader r)
        {
            var t = new int[(int)PackFormat.ReadVarInt(r)];
            for (int i = 0; i < t.Length; i++) t[i] = (int)PackFormat.ReadVarInt(r);
            return t;
        }
    }

    /// <summary>
    /// PUWG 1992 (EPSG:2180): odwzorowanie poprzeczne Merkatora na GRS80,
    /// poludnik srodkowy 19 st., k0 = 0.9993, FE = 500 000, FN = -5 300 000.
    /// Wzory Snydera (USGS PP 1395) - dokladnosc milimetrowa w granicach Polski.
    /// </summary>
    public static class Puwg1992
    {
        private const double A = 6378137.0, F = 1 / 298.257222101, K0 = 0.9993;
        private const double Lon0 = 19.0 * Math.PI / 180.0, FE = 500000.0, FN = -5300000.0;
        private static readonly double E2 = 2 * F - F * F, Ep2 = E2 / (1 - E2);

        private static double M(double phi)
        {
            double e4 = E2 * E2, e6 = e4 * E2;
            return A * ((1 - E2 / 4 - 3 * e4 / 64 - 5 * e6 / 256) * phi
                        - (3 * E2 / 8 + 3 * e4 / 32 + 45 * e6 / 1024) * Math.Sin(2 * phi)
                        + (15 * e4 / 256 + 45 * e6 / 1024) * Math.Sin(4 * phi)
                        - (35 * e6 / 3072) * Math.Sin(6 * phi));
        }

        /// <summary>easting, northing -&gt; lat, lon [st.]</summary>
        public static void ToLatLon(double easting, double northing, out double lat, out double lon)
        {
            double x = easting - FE, y = northing - FN;
            double e4 = E2 * E2, e6 = e4 * E2;
            double mu = y / K0 / (A * (1 - E2 / 4 - 3 * e4 / 64 - 5 * e6 / 256));
            double s = Math.Sqrt(1 - E2), e1 = (1 - s) / (1 + s);
            double phi1 = mu + (3 * e1 / 2 - 27 * e1 * e1 * e1 / 32) * Math.Sin(2 * mu)
                             + (21 * e1 * e1 / 16 - 55 * Math.Pow(e1, 4) / 32) * Math.Sin(4 * mu)
                             + (151 * Math.Pow(e1, 3) / 96) * Math.Sin(6 * mu)
                             + (1097 * Math.Pow(e1, 4) / 512) * Math.Sin(8 * mu);
            double sin = Math.Sin(phi1), cos = Math.Cos(phi1), tan = Math.Tan(phi1);
            double c1 = Ep2 * cos * cos, t1 = tan * tan;
            double n1 = A / Math.Sqrt(1 - E2 * sin * sin);
            double r1 = A * (1 - E2) / Math.Pow(1 - E2 * sin * sin, 1.5);
            double d = x / (n1 * K0);
            double phi = phi1 - (n1 * tan / r1) * (d * d / 2
                          - (5 + 3 * t1 + 10 * c1 - 4 * c1 * c1 - 9 * Ep2) * Math.Pow(d, 4) / 24
                          + (61 + 90 * t1 + 298 * c1 + 45 * t1 * t1 - 252 * Ep2 - 3 * c1 * c1) * Math.Pow(d, 6) / 720);
            double lam = Lon0 + (d - (1 + 2 * t1 + c1) * Math.Pow(d, 3) / 6
                          + (5 - 2 * c1 + 28 * t1 - 3 * c1 * c1 + 8 * Ep2 + 24 * t1 * t1) * Math.Pow(d, 5) / 120) / cos;
            lat = phi * 180.0 / Math.PI;
            lon = lam * 180.0 / Math.PI;
        }

        /// <summary>lat, lon [st.] -&gt; easting, northing</summary>
        public static void FromLatLon(double lat, double lon, out double easting, out double northing)
        {
            double phi = lat * Math.PI / 180.0, lam = lon * Math.PI / 180.0;
            double sin = Math.Sin(phi), cos = Math.Cos(phi), tan = Math.Tan(phi);
            double n = A / Math.Sqrt(1 - E2 * sin * sin);
            double t = tan * tan, c = Ep2 * cos * cos, a = cos * (lam - Lon0);
            double m = M(phi);
            easting = FE + K0 * n * (a + (1 - t + c) * Math.Pow(a, 3) / 6
                                     + (5 - 18 * t + t * t + 72 * c - 58 * Ep2) * Math.Pow(a, 5) / 120);
            northing = FN + K0 * (m + n * tan * (a * a / 2 + (5 - t + 9 * c + 4 * c * c) * Math.Pow(a, 4) / 24
                                  + (61 - 58 * t + t * t + 600 * c - 330 * Ep2) * Math.Pow(a, 6) / 720));
        }
    }
}
