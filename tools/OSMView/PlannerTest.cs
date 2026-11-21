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
    /// Test mapy planera: ten sam MapTileCache i MapRenderer co w grze, klatka
    /// po klatce, na programowym GL.
    ///
    ///   osmview mapa &lt;lat&gt; &lt;lon&gt; [katalog]
    ///
    /// Dla kilku skal robi to, co planer: rysuje klatki, az wszystkie kafle beda
    /// gotowe, i zapisuje zrzut w polowie (widac zastepcze kafle z nizszego zoomu)
    /// oraz na koncu. Obok liczy koszt: wywolania GL.Vertex3 na klatke w nowym
    /// ukladzie i w starym (cala mapa wektorowo co klatke) - w Unity to one,
    /// a nie piksele, obciazaja CPU.
    /// </summary>
    internal static class PlannerTest
    {
        private const int W = 900, H = 640;

        public static int Run(string[] args)
        {
            double lat = double.Parse(args[1], CultureInfo.InvariantCulture);
            double lon = double.Parse(args[2], CultureInfo.InvariantCulture);
            string outDir = args.Length > 3 ? args[3] : "osmview-mapa";
            Directory.CreateDirectory(outDir);

            LocalStore.Reload();
            Console.WriteLine(LocalStore.Report);

            var view = new MapView { CenterLat = lat, CenterLon = lon, BodyRadius = 6371010.0, Width = W, Height = H };
            var cache = new MapTileCache();
            var marks = new RenderTexture(W, H, 0);
            var waypoints = new List<MapWaypoint>
            {
                new MapWaypoint { Lat = lat + 0.004, Lon = lon + 0.006, Name = "WP 1" },
                new MapWaypoint { Lat = lat - 0.003, Lon = lon + 0.012, Name = "WP 2", PushedToKsp = true }
            };

            Console.WriteLine();
            Console.WriteLine("  skala      poziom  kafle  klatek do pelnej  czas    GL.Vertex/klatke (stary: cala mapa)   klatka z gotowymi");

            foreach (float mpp in new[] { 1.5f, 6f, 25f, 100f, MapTileCache.MaxTileViewMpp })
            {
                view.MetersPerPixel = mpp;
                int frames = 0;
                var sw = Stopwatch.StartNew();
                long vertsNew = 0;
                bool half = false;

                while (true)
                {
                    long v0 = GL.VertexCalls;
                    Frame(view, cache, marks, waypoints, lat, lon);
                    vertsNew += GL.VertexCalls - v0;
                    frames++;

                    if (!half && cache.Ready > 0 && cache.Ready < cache.Visible)
                    {
                        Save(Path.Combine(outDir, string.Format("mapa_{0:000.0}mpp_w_trakcie.png", mpp)));
                        half = true;
                    }
                    if (cache.Ready == cache.Visible && cache.Jobs == 0) break;
                    if (sw.Elapsed.TotalSeconds > 180) { Console.WriteLine("  przerwane po 180 s"); break; }
                    System.Threading.Thread.Sleep(2);   // w grze miedzy klatkami mija ~16 ms
                }
                double fullS = sw.Elapsed.TotalSeconds;
                Save(Path.Combine(outDir, string.Format("mapa_{0:000.0}mpp.png", mpp)));

                // Klatka z kompletem kafli: to jest koszt przewijania w nowym ukladzie.
                long vc = GL.VertexCalls;
                var fsw = Stopwatch.StartNew();
                Frame(view, cache, marks, waypoints, lat, lon);
                long cachedVerts = GL.VertexCalls - vc;

                // Stary uklad: odczyt obszaru i cala mapa wektorowo do jednej tekstury.
                var old = new MapData(view.VisibleBox(0.0), mpp < 8f);
                old.Start();
                while (!old.Done) System.Threading.Thread.Sleep(2);
                long ov = GL.VertexCalls;
                MapRenderer.Render(new RenderTexture(W, H, 0), view, old.Ways, waypoints, lat, lon, 45f, true);
                long oldVerts = GL.VertexCalls - ov;

                Console.WriteLine(string.Format("  {0,6:F1} m/px  {1,3}   {2,4}   {3,6}            {4,5:F1} s  {5,9:N0} ({6:N0})   {7,9:N0} vert",
                                  mpp, MapTileCache.LevelFor(mpp), cache.Visible, frames, fullS,
                                  vertsNew / Math.Max(1, frames), oldVerts, cachedVerts));
            }

            // --- zoom do kursora: punkt pod myszka ma zostac pod myszka, takze przy
            //     przejsciu kafle -> widok swiata (inna szerokosc odniesienia) ---
            {
                double worstPx = 0;
                var rnd = new Random(3);
                foreach (float from in new[] { 3f, 40f, 120f, 140f, 900f, 20000f })
                {
                    for (int k = 0; k < 20; k++)
                    {
                        var tv = new MapView { CenterLat = lat, CenterLon = lon, BodyRadius = 6371010.0, Width = W, Height = H, MetersPerPixel = from };
                        var tc = new MapTileCache();
                        float mx = (float)(rnd.NextDouble() * W), my = (float)(rnd.NextDouble() * H);
                        float to = from * (k % 2 == 0 ? 1.25f : 0.8f);

                        // to samo co MapPlannerAddon.SetZoom
                        if (tv.MetersPerPixel <= MapTileCache.MaxTileViewMpp) tc.EnsureRef(tv.CenterLat);
                        tv.RefLat = tv.MetersPerPixel > MapTileCache.MaxTileViewMpp ? double.NaN : tc.RefLat;
                        double la0, lo0; tv.FromPixel(mx, my, out la0, out lo0);
                        tv.MetersPerPixel = to;
                        if (tv.MetersPerPixel <= MapTileCache.MaxTileViewMpp) tc.EnsureRef(tv.CenterLat);
                        tv.RefLat = tv.MetersPerPixel > MapTileCache.MaxTileViewMpp ? double.NaN : tc.RefLat;
                        for (int it = 0; it < 4; it++)
                        {
                            double la1, lo1; tv.FromPixel(mx, my, out la1, out lo1);
                            tv.CenterLat += la0 - la1; tv.CenterLon += lo0 - lo1;
                        }

                        // RefLat NaN liczy sie od srodka - po przesunieciu srodka dolicz jeszcze raz
                        Vector2 px = tv.ToPixel(la0, lo0);
                        double err = Math.Sqrt((px.x - mx) * (px.x - mx) + (px.y - my) * (px.y - my));
                        if (err > worstPx) worstPx = err;
                    }
                }
                Console.WriteLine(string.Format("  zoom do kursora: najwiekszy rozjazd punktu pod myszka {0:F2} px (120 prob, takze przez granice kafle/swiat)", worstPx));
            }

            // --- widok swiata: siatka i slady (orbita przez poludnik 180) ---
            {
                var wv = new MapView { CenterLat = 10, CenterLon = 150, BodyRadius = 6371010.0, Width = W, Height = H, MetersPerPixel = 45000f };
                var fut = new List<GeoPoint>(); var pst = new List<GeoPoint>();
                for (int i = 0; i <= 360; i++)
                {
                    double t = i / 360.0 * 2 * Math.PI;
                    double la = 51.6 * Math.Sin(t);
                    double lo = ((120 + i * 1.1 + 180) % 360) - 180;   // przechodzi przez 180 st.
                    fut.Add(new GeoPoint(la, lo));
                    if (i < 90) pst.Add(new GeoPoint(la * 0.5, lo - 30));
                }
                GUI.SetScreen(W, H);
                GUI.BeginGroup(new Rect(0, 0, W, H));
                GUI.color = new Color(0.05f, 0.09f, 0.15f);
                GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture);
                GUI.color = Color.white;
                MapRenderer.RenderMarks(marks, wv, waypoints, 20, 170, 90f, true, fut, pst, 30f);
                GUI.DrawTexture(new Rect(0, 0, W, H), marks);
                GUI.EndGroup();
                Save(Path.Combine(outDir, "swiat_slady.png"));
            }

            // Przewiniecie o pol okna na wschod: nowe kafle, stare zostaja.
            view.MetersPerPixel = 6f;
            view.PanPixels(-W * 0.5f, 0);
            int pf = 0;
            do { Frame(view, cache, marks, waypoints, lat, lon); pf++; System.Threading.Thread.Sleep(2); }
            while ((cache.Ready < cache.Visible || cache.Jobs > 0) && pf < 5000);
            Save(Path.Combine(outDir, "mapa_006.0mpp_przewinieta.png"));
            Console.WriteLine(string.Format("  przewiniecie o pol okna: {0} klatek do kompletu, {1} gotowych RenderTexture w pamieci",
                              pf, RenderTexture.Created));
            return 0;
        }

        /// <summary>Dokladnie to, co MapPlannerAddon.Draw robi przy Repaint.</summary>
        private static void Frame(MapView view, MapTileCache cache, RenderTexture marks,
                                  List<MapWaypoint> waypoints, double vLat, double vLon)
        {
            GUI.SetScreen(W, H);
            cache.EnsureRef(view.CenterLat);
            view.RefLat = cache.RefLat;

            GUI.BeginGroup(new Rect(0, 0, W, H));
            GUI.color = MapRenderer.MapBackground;
            GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture);
            GUI.color = Color.white;

            cache.Draw(view, W, H);

            MapRenderer.RenderMarks(marks, view, waypoints, vLat, vLon, 45f, true);
            GUI.DrawTexture(new Rect(0, 0, W, H), marks);
            GUI.EndGroup();
        }

        private static void Save(string path)
        {
            Png.Save(path, GUI.Screen, GUI.ScreenW, GUI.ScreenH);
        }
    }
}
