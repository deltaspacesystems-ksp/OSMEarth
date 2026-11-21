using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using OSMRoads;

namespace OSMView
{
    /// <summary>
    ///   osmview sat [lat lon] [katalog]
    ///
    /// Sprawdza czytnik zdjec satelitarnych bez gry: mape swiata z poziomu 2
    /// (czy kontynenty sa na swoich miejscach - orientacja scian szescianu)
    /// i zblizenie w podanym punkcie z najostrzejszego dostepnego poziomu.
    /// </summary>
    internal static class SatTest
    {
        public static int Run(string[] args)
        {
            double lat = args.Length > 2 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 50.93;
            double lon = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 22.23;
            string outDir = args.Length > 3 ? args[3] : "osmview-sat";
            Directory.CreateDirectory(outDir);

            Console.WriteLine(Bc7.SelfCheck());
            SatelliteSource src = SatelliteSource.For("Earth");
            Console.WriteLine("satelita: " + src.Report);

            // --- swiat rownoprostokatnie z poziomu 2 ---
            {
                const int W = 1440, H = 720;
                var rgb = new byte[W * H * 3];
                var decoded = new Dictionary<ulong, byte[]>();
                for (int y = 0; y < H; y++)
                {
                    double la = 90.0 - (y + 0.5) * 180.0 / H;
                    for (int x = 0; x < W; x++)
                    {
                        double lo = -180.0 + (x + 0.5) * 360.0 / W;
                        Sample(src, decoded, la, lo, 2, rgb, (y * W + x) * 3);
                    }
                }
                Png.Save(Path.Combine(outDir, "swiat_L2.png"), rgb, W, H);
            }

            // --- zblizenie: 900x640 px po 1.5 poziomu, najostrzejszy dostepny ---
            foreach (double mpp in new[] { 400.0, 60.0, 12.0 })
            {
                const int W = 900, H = 640;
                int level = Math.Min(SatelliteSource.LevelFor(mpp, 6371010.0, 0), Math.Max(src.MaxWebLevel, src.MaxCanonicalLevel));
                var rgb = new byte[W * H * 3];
                var decoded = new Dictionary<ulong, byte[]>();
                double mLat = 6371010.0 * Math.PI / 180.0, mLon = mLat * Math.Cos(lat * Math.PI / 180.0);
                int used = -1;
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        double la = lat - (y - H * 0.5) * mpp / mLat;
                        double lo = lon + (x - W * 0.5) * mpp / mLon;
                        int u = Sample(src, decoded, la, lo, level, rgb, (y * W + x) * 3);
                        if (u > used) used = u;
                    }
                string name = string.Format("zblizenie_{0:000}mpp_L{1}.png", mpp, used);
                Png.Save(Path.Combine(outDir, name), rgb, W, H);
                Console.WriteLine("  {0}: chciany poziom {1}, uzyty {2}, {3} kafli", name, level, used, decoded.Count);
            }
            return 0;
        }

        /// <summary>Probka jednego punktu; zwraca uzyty poziom (-1 = brak).</summary>
        private static int Sample(SatelliteSource src, Dictionary<ulong, byte[]> decoded,
                                  double lat, double lon, int level, byte[] rgb, int o)
        {
            double dx, dy, dz, cu, cv; int face;
            CubeMath.LatLonToDir(lat, lon, out dx, out dy, out dz);
            CubeMath.DirToFaceUV(dx, dy, dz, out face, out cu, out cv);

            for (int l = level; l >= 0; l--)
            {
                int n = 1 << l;
                int tx = Math.Min(n - 1, (int)(cu * n)), ty = Math.Min(n - 1, (int)(cv * n));
                ulong key = SatelliteSource.Key(face, l, tx, ty);
                byte[] px;
                if (!decoded.TryGetValue(key, out px))
                {
                    byte[] bc = src.Has(face, l, tx, ty) ? src.Read(face, l, tx, ty) : null;
                    px = bc != null ? Bc7.Decode(bc, SatelliteSource.TilePx, SatelliteSource.TilePx) : null;
                    decoded[key] = px;
                }
                if (px == null) continue;

                double fu = cu * n - tx, fv = cv * n - ty;
                int ix = Math.Min(SatelliteSource.TilePx - 1, SatelliteSource.BorderPx + (int)(fu * SatelliteSource.InnerPx));
                int iy = Math.Min(SatelliteSource.TilePx - 1, SatelliteSource.BorderPx + (int)(fv * SatelliteSource.InnerPx));
                int p = (iy * SatelliteSource.TilePx + ix) * 4;
                rgb[o] = px[p]; rgb[o + 1] = px[p + 1]; rgb[o + 2] = px[p + 2];
                return l;
            }
            rgb[o] = 255; rgb[o + 1] = 0; rgb[o + 2] = 255;
            return -1;
        }
    }
}
