using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OSMRoads;

namespace Lod2Pack
{
    /// <summary>
    /// lod2pack color &lt;katalog b3d&gt; &lt;katalog cache kafli&gt; [--threads N] [--dry]
    ///
    /// Kolor dachu kazdego budynku z ortofotomapy GUGiK (WMTS HighResolution,
    /// zestaw EPSG:4326, poziom 13 = ok. 0.5 m/px). Probki leza na trojkatach dachu,
    /// sciagniete o 20% do srodka (okap i przesuniecie zdjecia wzgledem modelu),
    /// a kolor to mediana kazdego kanalu - drzewo czy cien nad czescia dachu nie psuja wyniku.
    /// Kafle sa trzymane na dysku, wiec powtorne uruchomienie nie pyta serwera jeszcze raz.
    /// </summary>
    internal static class ColorCommand
    {
        private const string Base = "https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WMTS/HighResolution";
        private const int Level = 13, TilePx = 512;
        private static readonly double DegPerPx = 1889.884732150179 * 0.00028 / 111319.49079327357;
        private static readonly double TileDeg = DegPerPx * TilePx;

        private struct Sample { public int B; public int X, Y; }

        public static int Run(string[] args)
        {
            string dir = args[1], cache = args[2];
            int threads = 4;
            bool dry = args.Contains("--dry");
            for (int i = 3; i + 1 < args.Length; i++) if (args[i] == "--threads") threads = int.Parse(args[i + 1]);
            Directory.CreateDirectory(cache);

            // --- wszystkie budynki w pamieci ---
            var files = Directory.GetFiles(dir, "b3d_*.bin");
            var squares = new List<KeyValuePair<string, List<KeyValuePair<long, List<Lod2Building>>>>>();
            var all = new List<Lod2Building>();
            foreach (string f in files)
            {
                var cells = Program.ReadSquarePublic(f).ToList();
                squares.Add(new KeyValuePair<string, List<KeyValuePair<long, List<Lod2Building>>>>(f, cells));
                foreach (var c in cells) all.AddRange(c.Value);
            }
            Console.WriteLine("{0:N0} budynkow w {1} plikach", all.Count, files.Length);

            // --- probki pogrupowane po kaflach ---
            var byTile = new Dictionary<long, List<Sample>>();
            for (int bi = 0; bi < all.Count; bi++)
                foreach (double[] ll in RoofSamples(all[bi]))
                {
                    double fx = (ll[1] - 12.0) / DegPerPx, fy = (56.0 - ll[0]) / DegPerPx;
                    int col = (int)Math.Floor(fx / TilePx), row = (int)Math.Floor(fy / TilePx);
                    long key = ((long)row << 32) | (uint)col;
                    List<Sample> l;
                    if (!byTile.TryGetValue(key, out l)) byTile[key] = l = new List<Sample>();
                    l.Add(new Sample { B = bi, X = (int)(fx - col * (double)TilePx), Y = (int)(fy - row * (double)TilePx) });
                }
            int cached = byTile.Keys.Count(k => File.Exists(TilePath(cache, k)));
            Console.WriteLine("kafli ortofotomapy: {0:N0} (w cache {1:N0}, do pobrania {2:N0}, ok. {3:N0} MB)",
                              byTile.Count, cached, byTile.Count - cached, (byTile.Count - cached) * 0.035);
            if (dry) return 0;

            // --- pobieranie i probkowanie ---
            var samples = new List<int>[all.Count];
            for (int i = 0; i < samples.Length; i++) samples[i] = new List<int>();
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OSMRoads-lod2pack/1.0 (KSP mod; roof colours)");
            var queue = new ConcurrentQueue<KeyValuePair<long, List<Sample>>>(byTile);
            int done = 0, failed = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var workers = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
            {
                KeyValuePair<long, List<Sample>> job;
                while (queue.TryDequeue(out job))
                {
                    byte[] jpg = GetTile(http, cache, job.Key);
                    if (jpg == null) { Interlocked.Increment(ref failed); continue; }
                    try
                    {
                        using (var bmp = new Bitmap(new MemoryStream(jpg)))
                        {
                            BitmapData d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                            var px = new byte[d.Stride * d.Height];
                            System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length);
                            bmp.UnlockBits(d);
                            foreach (Sample s in job.Value)
                            {
                                if (s.X < 0 || s.Y < 0 || s.X >= bmp.Width || s.Y >= bmp.Height) continue;
                                int o = s.Y * d.Stride + s.X * 3;
                                int rgb = (px[o + 2] << 16) | (px[o + 1] << 8) | px[o];      // BGR w pamieci
                                lock (samples[s.B]) samples[s.B].Add(rgb);
                            }
                        }
                    }
                    catch (Exception e) { Interlocked.Increment(ref failed); Console.WriteLine("  kafel: " + e.Message); }
                    int n = Interlocked.Increment(ref done);
                    if (n % 500 == 0) Console.WriteLine("  {0:N0}/{1:N0} kafli, {2:F0} s", n, byTile.Count, sw.Elapsed.TotalSeconds);
                }
            })).ToArray();
            Task.WaitAll(workers);

            // --- mediana na budynek ---
            int colored = 0;
            for (int i = 0; i < all.Count; i++)
            {
                List<int> s = samples[i];
                if (s.Count == 0) continue;
                all[i].R = (byte)Math.Max(1, Median(s, 16));
                all[i].G = (byte)Math.Max(1, Median(s, 8));
                all[i].B = (byte)Math.Max(1, Median(s, 0));
                colored++;
            }
            Console.WriteLine("pokolorowane dachy: {0:N0}/{1:N0}, nieudanych kafli {2}", colored, all.Count, failed);

            foreach (var sq in squares) Program.WriteSquareReplace(sq.Key, sq.Value);
            return 0;
        }

        private static int Median(List<int> s, int shift)
        {
            var v = s.Select(c => (c >> shift) & 255).OrderBy(x => x).ToList();
            return v[v.Count / 2];
        }

        private static string TilePath(string cache, long key)
        {
            int row = (int)(key >> 32), col = (int)(uint)key;
            return Path.Combine(cache, row.ToString(), col + ".jpg");
        }

        private static byte[] GetTile(HttpClient http, string cache, long key)
        {
            string path = TilePath(cache, key);
            if (File.Exists(path)) return File.ReadAllBytes(path);
            int row = (int)(key >> 32), col = (int)(uint)key;
            string url = Base + "?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER=ORTOFOTOMAPA&STYLE=default" +
                         "&TILEMATRIXSET=EPSG:4326&TILEMATRIX=EPSG:4326:" + Level + "&TILEROW=" + row + "&TILECOL=" + col + "&FORMAT=image/jpeg";
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    HttpResponseMessage r = http.GetAsync(url).Result;
                    if ((int)r.StatusCode == 429 || (int)r.StatusCode >= 500) { Thread.Sleep(2000 * (attempt + 1)); continue; }
                    if (!r.IsSuccessStatusCode) return null;
                    string ct = r.Content.Headers.ContentType?.MediaType ?? "";
                    if (!ct.StartsWith("image")) return null;
                    byte[] b = r.Content.ReadAsByteArrayAsync().Result;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, b);
                    return b;
                }
                catch (Exception) { Thread.Sleep(1500 * (attempt + 1)); }
            }
            return null;
        }

        /// <summary>Punkty (lat, lon) na dachu: kilka na trojkat, wg jego pola, sciagniete do srodka dachu.</summary>
        private static IEnumerable<double[]> RoofSamples(Lod2Building b)
        {
            if (b.Roofs.Length < 3) yield break;
            double cLat = 0, cLon = 0; int cn = 0;
            foreach (int i in b.Roofs) { cLat += b.VLat[i]; cLon += b.VLon[i]; cn++; }
            cLat /= cn; cLon /= cn;
            double mLat = 111320.0, mLon = 111320.0 * Math.Cos(cLat * Math.PI / 180.0);
            double[,] bary = { { 1 / 3.0, 1 / 3.0 }, { 0.6, 0.2 }, { 0.2, 0.6 }, { 0.2, 0.2 }, { 0.45, 0.45 }, { 0.1, 0.45 }, { 0.45, 0.1 } };
            for (int t = 0; t + 2 < b.Roofs.Length; t += 3)
            {
                int a = b.Roofs[t], p = b.Roofs[t + 1], q = b.Roofs[t + 2];
                double ax = (b.VLon[p] - b.VLon[a]) * mLon, ay = (b.VLat[p] - b.VLat[a]) * mLat;
                double bx = (b.VLon[q] - b.VLon[a]) * mLon, by = (b.VLat[q] - b.VLat[a]) * mLat;
                double area = Math.Abs(ax * by - ay * bx) * 0.5;
                int n = (int)Math.Max(1, Math.Min(7, area / 6.0));
                for (int k = 0; k < n; k++)
                {
                    double u = bary[k, 0], v = bary[k, 1], w = 1 - u - v;
                    double lat = b.VLat[a] * w + b.VLat[p] * u + b.VLat[q] * v;
                    double lon = b.VLon[a] * w + b.VLon[p] * u + b.VLon[q] * v;
                    yield return new[] { cLat + (lat - cLat) * 0.8, cLon + (lon - cLon) * 0.8 };
                }
            }
        }
    }
}
