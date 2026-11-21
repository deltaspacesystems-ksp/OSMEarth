using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using OSMRoads;
using UnityEngine;

namespace OSMView
{
    /// <summary>
    ///   osmview style &lt;lat&gt; &lt;lon&gt; [katalog]
    ///
    /// Te same kafle planera w trzech wygladach (ciemny, jasny OSM, satelita)
    /// i kilku skalach, z etykietami - dokladnie ta sama sciezka co w grze:
    /// MapTileCache -> MapData (satelita + nazwy) -> MapRenderer -> MapLabels.
    /// </summary>
    internal static class StyleTest
    {
        private const int W = 900, H = 640;

        public static int Run(string[] args)
        {
            double lat = double.Parse(args[1], CultureInfo.InvariantCulture);
            double lon = double.Parse(args[2], CultureInfo.InvariantCulture);
            string outDir = args.Length > 3 ? args[3] : "osmview-style";
            Directory.CreateDirectory(outDir);

            LocalStore.Reload();
            Console.WriteLine(LocalStore.Report.Length > 200 ? LocalStore.Report.Substring(0, 200) + "..." : LocalStore.Report);
            SatelliteSource sat = SatelliteSource.For("Earth");
            Console.WriteLine("satelita: " + sat.Report);

            var cases = new[]
            {
                new { Name = "ciemny", Style = MapStyle.Dark, Sat = false },
                new { Name = "jasny", Style = MapStyle.Light, Sat = false },
                new { Name = "satelita", Style = MapStyle.Dark, Sat = true },
            };

            foreach (var c in cases)
            {
                OsmSettings.Style = c.Style;
                OsmSettings.Satellite = c.Sat;
                var cache = new MapTileCache { Satellite = c.Sat ? sat : null };
                var labels = new MapLabels();

                foreach (float mpp in new[] { 2f, 8f, 40f, 140f })
                {
                    var view = new MapView { CenterLat = lat, CenterLon = lon, BodyRadius = 6371010.0, Width = W, Height = H, MetersPerPixel = mpp };
                    var sw = Stopwatch.StartNew();
                    int frames = 0;
                    while (true)
                    {
                        Frame(view, cache, labels);
                        frames++;
                        if (cache.Ready == cache.Visible && cache.Jobs == 0) break;
                        if (sw.Elapsed.TotalSeconds > 240) { Console.WriteLine("  przerwane"); break; }
                        System.Threading.Thread.Sleep(2);
                    }
                    long lc = GUI.LabelCalls;
                    Frame(view, cache, labels);
                    string name = string.Format("{0}_{1:000}mpp.png", c.Name, mpp);
                    Png.Save(Path.Combine(outDir, name), GUI.Screen, W, H);
                    Console.WriteLine("  {0,-22} {1,5:F1} s  {2,3} kafli  {3,3} etykiet  {4}", name, sw.Elapsed.TotalSeconds,
                                      cache.Visible, labels.Count, MapRenderer.SatelliteReport);
                }
                cache.Clear();
            }
            return 0;
        }

        /// <summary>To, co MapPlannerAddon.DrawMap robi przy Repaint - razem z etykietami.</summary>
        private static void Frame(MapView view, MapTileCache cache, MapLabels labels)
        {
            GUI.SetScreen(W, H);
            cache.EnsureRef(view.CenterLat);
            view.RefLat = cache.RefLat;

            GUI.BeginGroup(new Rect(0, 0, W, H));
            GUI.color = MapRenderer.MapBackground;
            GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture);
            GUI.color = Color.white;
            cache.Draw(view, W, H);

            labels.Clear();
            cache.AddLabels(labels, view, W, H);
            labels.Draw();
            GUI.EndGroup();
        }
    }
}
