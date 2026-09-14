using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Rysuje mape wektorowo do RenderTexture przez GL.
    ///
    /// Dlaczego nie GUI.DrawTexture per obiekt: mapa to dziesiatki tysiecy odcinkow,
    /// a kazde DrawTexture to osobny quad przez IMGUI. GL rysuje wszystko w jednym
    /// przebiegu, a wynik lezy w teksturze i przerysowuje sie tylko przy zmianie
    /// widoku - przewijanie nie kosztuje nic.
    /// </summary>
    public static class MapRenderer
    {
        private static Material glMat;

        private static Material GLMaterial()
        {
            if (glMat != null) return glMat;

            Shader sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Unlit/Color");

            glMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            if (glMat.HasProperty("_SrcBlend"))
            {
                glMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                glMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            }
            if (glMat.HasProperty("_Cull")) glMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            if (glMat.HasProperty("_ZWrite")) glMat.SetInt("_ZWrite", 0);
            return glMat;
        }

        /// <summary>Szerokosc drogi na mapie W PIKSELACH - mapa ma czytac sie jak mapa,
        /// wiec drogi maja stala grubosc niezaleznie od zoomu, tylko rozna wg klasy.</summary>
        private static float RoadPixels(string highway)
        {
            switch (highway)
            {
                case "motorway": case "motorway_link": return 3.4f;
                case "trunk": case "trunk_link": return 2.8f;
                case "primary": case "primary_link": return 2.4f;
                case "secondary": case "secondary_link": return 1.9f;
                case "tertiary": case "tertiary_link": return 1.5f;
                default: return 1.1f;
            }
        }

        private static Color RoadColor(string highway)
        {
            if (OsmSettings.Style == MapStyle.Light && !OsmSettings.Satellite)
            {
                // barwy stylu openstreetmap.org
                switch (highway)
                {
                    case "motorway": case "motorway_link": return new Color(0.91f, 0.57f, 0.64f);
                    case "trunk": case "trunk_link": return new Color(0.98f, 0.70f, 0.61f);
                    case "primary": case "primary_link": return new Color(0.99f, 0.84f, 0.64f);
                    case "secondary": case "secondary_link": return new Color(0.97f, 0.98f, 0.75f);
                    default: return Color.white;
                }
            }
            switch (highway)
            {
                case "motorway": case "motorway_link": return new Color(0.98f, 0.55f, 0.35f);
                case "trunk": case "trunk_link": return new Color(0.97f, 0.65f, 0.42f);
                case "primary": case "primary_link": return new Color(0.99f, 0.80f, 0.45f);
                case "secondary": case "secondary_link": return new Color(0.97f, 0.93f, 0.60f);
                default: return new Color(0.92f, 0.92f, 0.92f);
            }
        }

        /// <summary>Obwodka drogi - na jasnym tle i na zdjeciu bez niej biale ulice znikaja.</summary>
        private static bool RoadCasing { get { return OsmSettings.Satellite || OsmSettings.Style == MapStyle.Light; } }

        private static Color CasingColor
        {
            get { return OsmSettings.Satellite ? new Color(0f, 0f, 0f, 0.55f) : new Color(0.62f, 0.60f, 0.58f); }
        }

        /// <summary>Ciemne tlo: przerwy miedzy wielokatami OSM sa od razu widoczne
        /// ("tu nie ma lasu"). Teren: mapa czyta sie spokojniej. Jasne: jak openstreetmap.org.</summary>
        public static readonly Color MapBackgroundDark = new Color(0.13f, 0.14f, 0.15f);
        public static readonly Color MapBackgroundLand = new Color(0.27f, 0.29f, 0.23f);
        public static readonly Color MapBackgroundLight = new Color(0.949f, 0.937f, 0.914f);
        public static Color MapBackground
        {
            get
            {
                if (OsmSettings.Satellite) return new Color(0.04f, 0.05f, 0.08f);
                switch (OsmSettings.Style)
                {
                    case MapStyle.Light: return MapBackgroundLight;
                    case MapStyle.Terrain: return MapBackgroundLand;
                    default: return MapBackgroundDark;
                }
            }
        }

        /// <summary>Etykiety: jasny tekst z ciemna obwodka, a na jasnej mapie odwrotnie.</summary>
        public static bool DarkLabels { get { return OsmSettings.Style == MapStyle.Light && !OsmSettings.Satellite; } }

        private static Color AreaColor(AreaKind k)
        {
            if (OsmSettings.Style != MapStyle.Light || OsmSettings.Satellite) return AreaKindColors.Tint(k);
            switch (k)
            {
                case AreaKind.Water: return new Color(0.667f, 0.827f, 0.875f);
                case AreaKind.Forest: return new Color(0.678f, 0.820f, 0.620f);
                case AreaKind.Grass: return new Color(0.804f, 0.922f, 0.690f);
                case AreaKind.Farmland: return new Color(0.933f, 0.941f, 0.835f);
                case AreaKind.Sand: return new Color(0.961f, 0.914f, 0.776f);
                case AreaKind.Residential: return new Color(0.878f, 0.875f, 0.875f);
                case AreaKind.Industrial: return new Color(0.922f, 0.859f, 0.910f);
                case AreaKind.Pavement: return new Color(0.867f, 0.867f, 0.910f);
                default: return new Color(0.85f, 0.85f, 0.85f);
            }
        }

        public static bool RoadVisibleAt(string highway, float mpp) { return VisibleAt(highway, mpp); }

        /// <summary>
        /// Jeden kafel mapy: tlo, obszary, budynki, drogi. Bez znacznikow - te
        /// zmieniaja sie co klatke (statek, trasa), a kafel ma powstac raz.
        /// </summary>
        public static void RenderTile(RenderTexture rt, MapView view, List<OsmWay> ways, Color background)
        {
            RenderTile(rt, view, ways, background, null);
        }

        /// <summary>Kafel z opcjonalnym zdjeciem satelitarnym pod wektorami.</summary>
        public static void RenderTile(RenderTexture rt, MapView view, List<OsmWay> ways, Color background,
                                      List<SatelliteSource.Patch> satellite)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, rt.width, rt.height, 0f);
            // Tlo w kolorze ziemi, nie prawie czarne: pola w Polsce sa w OSM wasko
            // pocietymi pasami z przerwami miedzy wielokatami. Na ciemnym tle te
            // przerwy wygladaly jak czarne szpice przez cala mape.
            GL.Clear(true, true, background);

            bool sat = satellite != null && satellite.Count > 0;
            if (sat) DrawSatellite(view, satellite);

            GLMaterial().SetPass(0);

            if (ways != null)
            {
                BBox cull = view.VisibleBox(0.05);
                if (!sat || OsmSettings.SatAreas)
                    DrawAreas(view, ways, sat ? 0.30f : 0.85f, cull.South, cull.North, cull.West, cull.East);
                if (view.MetersPerPixel < 8f && !sat)
                    DrawBuildings(view, ways, cull.South, cull.North, cull.West, cull.East);
                DrawRoads(view, ways, cull.South, cull.North, cull.West, cull.East);
            }

            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        // ------------------------------------------------------------------
        //  Satelita
        // ------------------------------------------------------------------

        private static Material texMat;

        private static Material TextureMaterial()
        {
            if (texMat != null) return texMat;
            foreach (string name in new[] { "Sprites/Default", "UI/Default", "Unlit/Texture", "Unlit/Transparent" })
            {
                Shader sh = Shader.Find(name);
                if (sh == null) continue;
                texMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                break;
            }
            return texMat;
        }

        /// <summary>Siatka na kafel szescianu: 8x8 czworokatow wystarcza, zeby zakrzywienie
        /// scian (rzutowanie gnomoniczne) nie dawalo widocznych zalaman na mapie.</summary>
        private const int SatGrid = 8;

        public static string SatelliteReport = "";

        private static void DrawSatellite(MapView view, List<SatelliteSource.Patch> patches)
        {
            Material m = TextureMaterial();
            if (m == null) { SatelliteReport = "brak shadera tekstur"; return; }

            var px = new Vector2[(SatGrid + 1) * (SatGrid + 1)];
            var uv = new Vector2[px.Length];
            foreach (SatelliteSource.Patch p in patches)
            {
                Texture2D tex = null;
                try
                {
                    tex = new Texture2D(SatelliteSource.TilePx, SatelliteSource.TilePx, TextureFormat.BC7, false)
                    {
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Bilinear
                    };
                    tex.LoadRawTextureData(p.Data);
                    tex.Apply(false, true);
                }
                catch (System.Exception e)
                {
                    SatelliteReport = "BC7: " + e.Message;
                    if (tex != null) Object.Destroy(tex);
                    return;
                }

                int n = 1 << p.Level;
                for (int j = 0; j <= SatGrid; j++)
                    for (int i = 0; i <= SatGrid; i++)
                    {
                        double fu = i / (double)SatGrid, fv = j / (double)SatGrid;
                        double x, y, z, lat, lon;
                        CubeMath.FaceUVToDir(p.Face, (p.X + fu) / n, (p.Y + fv) / n, out x, out y, out z);
                        CubeMath.DirToLatLon(x, y, z, out lat, out lon);
                        int k = j * (SatGrid + 1) + i;
                        px[k] = view.ToPixel(lat, lon);
                        // Teksel 0 danych to dol tekstury; ramka 4 px z kazdej strony.
                        uv[k] = new Vector2((float)((SatelliteSource.BorderPx + fu * SatelliteSource.InnerPx) / SatelliteSource.TilePx),
                                            (float)((SatelliteSource.BorderPx + fv * SatelliteSource.InnerPx) / SatelliteSource.TilePx));
                    }

                m.mainTexture = tex;
                m.SetPass(0);
                GL.Begin(GL.TRIANGLES);
                GL.Color(Color.white);
                for (int j = 0; j < SatGrid; j++)
                    for (int i = 0; i < SatGrid; i++)
                    {
                        int a = j * (SatGrid + 1) + i, b = a + 1, c = a + SatGrid + 1, d = c + 1;
                        TexVert(px[a], uv[a]); TexVert(px[b], uv[b]); TexVert(px[d], uv[d]);
                        TexVert(px[a], uv[a]); TexVert(px[d], uv[d]); TexVert(px[c], uv[c]);
                    }
                GL.End();
                m.mainTexture = null;
                Object.Destroy(tex);
            }
            SatelliteReport = "";
        }

        private static void TexVert(Vector2 p, Vector2 uv)
        {
            GL.TexCoord2(uv.x, uv.y);
            GL.Vertex3(p.x, p.y, 0f);
        }

        /// <summary>Warstwa znacznikow nad kaflami: przezroczyste tlo, trasa,
        /// punkty, kosmodromy i statek. Kilka quadow - tanie co klatke.</summary>
        public static void RenderMarks(RenderTexture rt, MapView view, List<MapWaypoint> waypoints,
                                       double vesselLat, double vesselLon, float vesselHeading,
                                       bool drawLaunchSites)
        {
            RenderMarks(rt, view, waypoints, vesselLat, vesselLon, vesselHeading, drawLaunchSites, null, null, 0f);
        }

        /// <summary>Znaczniki plus slady: przyszly tor lotu (orbita / trajektoria
        /// balistyczna) i przebyta droga, a przy duzym oddaleniu siatka poludnikow.</summary>
        public static void RenderMarks(RenderTexture rt, MapView view, List<MapWaypoint> waypoints,
                                       double vesselLat, double vesselLon, float vesselHeading,
                                       bool drawLaunchSites, List<GeoPoint> future, List<GeoPoint> past,
                                       float graticuleDeg)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, rt.width, rt.height, 0f);
            GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
            GLMaterial().SetPass(0);

            if (graticuleDeg > 0f) DrawGraticule(view, graticuleDeg);
            if (OsmSettings.CountryBorders) DrawCountryBorders(view);
            if (past != null) DrawPolyline(view, past, new Color(1f, 0.55f, 0.20f, 0.85f), 1.2f);
            if (future != null) DrawPolyline(view, future, new Color(0.35f, 0.95f, 1f, 0.95f), 1.4f);
            if (drawLaunchSites) DrawLaunchSites(view);
            DrawRoute(view, waypoints);
            DrawWaypoints(view, waypoints);
            DrawVessel(view, vesselLat, vesselLon, vesselHeading);

            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        /// <summary>Czy przy tej skali w ogole rysowac dana klase drogi.</summary>
        private static bool VisibleAt(string highway, float mpp)
        {
            if (mpp < 12f) return true;
            switch (highway)
            {
                case "motorway": case "motorway_link":
                case "trunk": case "trunk_link":
                case "primary": case "primary_link":
                    return true;
                case "secondary": case "secondary_link":
                    return mpp < 60f;
                case "tertiary": case "tertiary_link":
                    return mpp < 30f;
                default:
                    return mpp < 18f;
            }
        }

        /// <summary>
        /// Wariant pod nakladke na kule: PRZEZROCZYSTE tlo i bez znacznika statku.
        /// Wszedzie, gdzie nie ma danych OSM, ma byc widac satelite spod spodu.
        /// </summary>
        public static void RenderOverlay(RenderTexture rt, MapView view, List<OsmWay> ways)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, rt.width, rt.height, 0f);
            GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
            GLMaterial().SetPass(0);

            if (ways != null)
            {
                BBox cull = view.VisibleBox(0.02);
                // Landuse pol-przezroczyscie: ma podbarwiac zdjecie, nie zakrywac go.
                DrawAreas(view, ways, 0.35f, cull.South, cull.North, cull.West, cull.East);
                if (view.MetersPerPixel < 14f)
                    DrawBuildings(view, ways, cull.South, cull.North, cull.West, cull.East);
                DrawRoads(view, ways, cull.South, cull.North, cull.West, cull.East);
            }

            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        /// <summary>Czas ostatniego przerysowania w milisekundach - pokazywany
        /// w planerze, zeby koszt rysowania nie byl niewidzialny.</summary>
        public static float LastRenderMs;

        public static void Render(RenderTexture rt, MapView view, List<OsmWay> ways,
                                  List<MapWaypoint> waypoints,
                                  double vesselLat, double vesselLon, float vesselHeading,
                                  bool drawLaunchSites)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, rt.width, rt.height, 0f);
            GL.Clear(true, true, new Color(0.13f, 0.14f, 0.15f));
            GLMaterial().SetPass(0);

            if (ways != null)
            {
                BBox cull = view.VisibleBox(0.02);
                DrawAreas(view, ways, 0.85f, cull.South, cull.North, cull.West, cull.East);
                if (view.MetersPerPixel < 8f)
                    DrawBuildings(view, ways, cull.South, cull.North, cull.West, cull.East);
                DrawRoads(view, ways, cull.South, cull.North, cull.West, cull.East);
            }

            if (drawLaunchSites) DrawLaunchSites(view);
            DrawRoute(view, waypoints);
            DrawWaypoints(view, waypoints);
            DrawVessel(view, vesselLat, vesselLon, vesselHeading);

            GL.PopMatrix();
            RenderTexture.active = prev;

            sw.Stop();
            LastRenderMs = (float)sw.Elapsed.TotalMilliseconds;
        }

        // Bufor punktow po przerzedzeniu. Statyczny, bo rysowanie i tak jest
        // jednowatkowe (GL dziala tylko na watku glownym), a alokacja listy
        // na kazdy obrys byla widoczna w GC.
        private static readonly List<Vector2> buf = new List<Vector2>(256);

        /// <summary>Ponizej tylu pikseli miedzy kolejnymi punktami roznicy i tak
        /// nie widac, a to wlasnie dlugie obrysy lasow i rzek generowaly wiekszosc
        /// wierzcholkow przy oddaleniu.</summary>
        private const float MinStepPx = 1.4f;

        /// <summary>Przepisuje punkty obrysu na piksele, pomijajac te lezace
        /// blizej niz MinStepPx od poprzedniego. Ostatni punkt zostaje zawsze,
        /// zeby nie urwac konca drogi.</summary>
        private static void Project(MapView view, List<GeoPoint> pts, bool closed)
        {
            buf.Clear();
            int n = pts.Count;
            if (n == 0) return;

            Vector2 last = view.ToPixel(pts[0].Lat, pts[0].Lon);
            buf.Add(last);

            float minSq = MinStepPx * MinStepPx;
            for (int i = 1; i < n; i++)
            {
                Vector2 p = view.ToPixel(pts[i].Lat, pts[i].Lon);
                if (i < n - 1)
                {
                    float dx = p.x - last.x, dy = p.y - last.y;
                    if (dx * dx + dy * dy < minSq) continue;
                }
                buf.Add(p);
                last = p;
            }

            if (closed && buf.Count < 3) buf.Clear();
        }

        private static void DrawAreas(MapView view, List<OsmWay> ways, float alpha,
                                      double cS, double cN, double cW, double cE)
        {
            GL.Begin(GL.TRIANGLES);
            foreach (OsmWay w in ways)
            {
                if (!w.IsArea || w.Points.Count < 3) continue;
                w.EnsureBounds();
                if (!w.Overlaps(cS, cN, cW, cE)) continue;

                Project(view, w.Points, true);
                if (buf.Count < 3) continue;

                Color c = AreaColor(w.Area);
                GL.Color(new Color(c.r, c.g, c.b, alpha));

                // Prawdziwa triangulacja. Wachlarz z pierwszego wierzcholka byl
                // "tani i prawie dobry" tylko dla wypuklych obrysow - pola i lasy
                // sa wklesle i wachlarz wychodzil z nich kilometrowymi klinami.
                // Koszt placimy raz na kafel, a punkty sa juz przerzedzone.
                List<int> tri = Polygon.Triangulate(buf);
                if (tri == null) continue;
                for (int i = 0; i + 2 < tri.Count; i += 3)
                {
                    Vector2 q0 = buf[tri[i]], q1 = buf[tri[i + 1]], q2 = buf[tri[i + 2]];
                    GL.Vertex3(q0.x, q0.y, 0f);
                    GL.Vertex3(q1.x, q1.y, 0f);
                    GL.Vertex3(q2.x, q2.y, 0f);
                }
            }
            GL.End();
        }

        private static void DrawBuildings(MapView view, List<OsmWay> ways,
                                          double cS, double cN, double cW, double cE)
        {
            GL.Begin(GL.TRIANGLES);
            GL.Color(OsmSettings.Style == MapStyle.Light
                     ? new Color(0.85f, 0.82f, 0.79f, 1f)
                     : new Color(0.44f, 0.42f, 0.40f, 0.95f));
            foreach (OsmWay w in ways)
            {
                if (!w.IsBuilding || w.Points.Count < 3) continue;
                w.EnsureBounds();
                if (!w.Overlaps(cS, cN, cW, cE)) continue;

                // Budynkow NIE przerzedzamy: obrys ma zwykle cztery punkty,
                // a przy tej skali kazdy z nich niesie ksztalt.
                Vector2 p0 = view.ToPixel(w.Points[0].Lat, w.Points[0].Lon);
                for (int i = 1; i + 1 < w.Points.Count; i++)
                {
                    Vector2 p1 = view.ToPixel(w.Points[i].Lat, w.Points[i].Lon);
                    Vector2 p2 = view.ToPixel(w.Points[i + 1].Lat, w.Points[i + 1].Lon);
                    GL.Vertex3(p0.x, p0.y, 0f);
                    GL.Vertex3(p1.x, p1.y, 0f);
                    GL.Vertex3(p2.x, p2.y, 0f);
                }
            }
            GL.End();
        }

        private static void DrawRoads(MapView view, List<OsmWay> ways,
                                      double cS, double cN, double cW, double cE)
        {
            float mpp = view.MetersPerPixel;
            bool casing = RoadCasing;
            float fillAlpha = OsmSettings.Satellite ? 0.85f : 1f;

            // Dwa przebiegi: najpierw obwodki wszystkich drog, potem wypelnienia -
            // inaczej obwodka nastepnej drogi przecinalaby juz narysowane skrzyzowanie.
            for (int pass = casing ? 0 : 1; pass < 2; pass++)
            {
                GL.Begin(GL.QUADS);
                Color lastColor = new Color(-1f, -1f, -1f, -1f);
                if (pass == 0) GL.Color(CasingColor);

                foreach (OsmWay w in ways)
                {
                    if (!w.IsRoad || w.Points.Count < 2) continue;
                    if (!VisibleAt(w.Highway, mpp)) continue;
                    w.EnsureBounds();
                    if (!w.Overlaps(cS, cN, cW, cE)) continue;

                    Project(view, w.Points, false);
                    if (buf.Count < 2) continue;

                    float half = RoadPixels(w.Highway) * 0.5f;
                    if (pass == 0) half += 0.9f;
                    else
                    {
                        Color c = RoadColor(w.Highway);
                        c.a = fillAlpha;
                        if (c != lastColor) { GL.Color(c); lastColor = c; }
                    }

                    for (int i = 1; i < buf.Count; i++)
                        Quad(buf[i - 1], buf[i], half);
                }
                GL.End();
            }
        }

        /// <summary>Odcinek jako prostokat - GL.LINES ma zawsze 1 piksel.</summary>
        private static void Quad(Vector2 a, Vector2 b, float half)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.001f) return;
            Vector2 n = new Vector2(-d.y / len, d.x / len) * half;

            GL.Vertex3(a.x + n.x, a.y + n.y, 0f);
            GL.Vertex3(b.x + n.x, b.y + n.y, 0f);
            GL.Vertex3(b.x - n.x, b.y - n.y, 0f);
            GL.Vertex3(a.x - n.x, a.y - n.y, 0f);
        }

        /// <summary>
        /// Linia po punktach lat/lon. Odcinek przechodzacy przez SZEW WIDOKU jest
        /// pomijany - a szew lezy naprzeciwko srodka mapy, nie na poludniku 180.
        /// Test po skoku samej dlugosci geograficznej przepuszczal odcinek przez
        /// szew przy kazdym srodku innym niz 0 i przez mape szla pozioma kreska.
        /// </summary>
        private static void DrawPolyline(MapView view, List<GeoPoint> pts, Color c, float half)
        {
            if (pts.Count < 2) return;
            GL.Begin(GL.QUADS);
            GL.Color(c);
            Vector2 a = view.ToPixel(pts[0].Lat, pts[0].Lon);
            double da = WrapDeg(pts[0].Lon - view.CenterLon);
            for (int i = 1; i < pts.Count; i++)
            {
                Vector2 b = view.ToPixel(pts[i].Lat, pts[i].Lon);
                double db = WrapDeg(pts[i].Lon - view.CenterLon);
                if (System.Math.Abs(db - da) < 180.0) Quad(a, b, half);
                a = b;
                da = db;
            }
            GL.End();
        }

        private static double WrapDeg(double d)
        {
            d %= 360.0;
            if (d > 180.0) d -= 360.0;
            else if (d < -180.0) d += 360.0;
            return d;
        }

        /// <summary>Poludniki i rownolezniki co "step" stopni w widocznym obszarze.</summary>
        /// <summary>
        /// Kontury panstw (Natural Earth, wbudowane w moda). Ponizej progu skali
        /// nie rysujemy - przy ulicznym zoomie tylko by migotaly w tle, a granica
        /// panstwa i tak biegnie zwykle poza kadrem.
        /// </summary>
        private static void DrawCountryBorders(MapView view)
        {
            if (view.MetersPerPixel < 25f) return;
            CountryBorders.Country[] all = CountryBorders.All;
            if (all.Length == 0) return;

            Color c = OsmSettings.Style == MapStyle.Light && !OsmSettings.Satellite
                ? new Color(0.55f, 0.42f, 0.55f, 0.8f)
                : new Color(1f, 0.85f, 0.55f, 0.55f);

            GL.Begin(GL.QUADS);
            GL.Color(c);
            foreach (CountryBorders.Country co in all)
                foreach (GeoPoint[] ring in co.Rings)
                    DrawRing(view, ring, 0.9f);
            GL.End();
        }

        /// <summary>Zamkniety obrys (ostatni punkt -> pierwszy), z tym samym testem
        /// szwu widoku co DrawPolyline.</summary>
        private static void DrawRing(MapView view, GeoPoint[] pts, float half)
        {
            if (pts.Length < 2) return;
            Vector2 a = view.ToPixel(pts[0].Lat, pts[0].Lon);
            double da = WrapDeg(pts[0].Lon - view.CenterLon);
            for (int i = 1; i <= pts.Length; i++)
            {
                GeoPoint gp = pts[i % pts.Length];
                Vector2 b = view.ToPixel(gp.Lat, gp.Lon);
                double db = WrapDeg(gp.Lon - view.CenterLon);
                if (System.Math.Abs(db - da) < 180.0) Quad(a, b, half);
                a = b; da = db;
            }
        }

        private static void DrawGraticule(MapView view, float step)
        {
            BBox b = view.VisibleBox(0.02);
            GL.Begin(GL.QUADS);
            GL.Color(new Color(1f, 1f, 1f, 0.14f));

            double s0 = System.Math.Floor(System.Math.Max(-90.0, b.South) / step) * step;
            for (double la = s0; la <= System.Math.Min(90.0, b.North); la += step)
                Quad(view.ToPixel(la, b.West), view.ToPixel(la, b.East), 0.5f);

            double w0 = System.Math.Floor(b.West / step) * step;
            for (double lo = w0; lo <= b.East; lo += step)
                Quad(view.ToPixel(System.Math.Max(-90.0, b.South), lo), view.ToPixel(System.Math.Min(90.0, b.North), lo), 0.5f);

            GL.Color(new Color(1f, 0.9f, 0.5f, 0.30f));   // rownik i poludnik zerowy mocniej
            if (b.South < 0 && b.North > 0) Quad(view.ToPixel(0, b.West), view.ToPixel(0, b.East), 0.7f);
            if (b.West < 0 && b.East > 0) Quad(view.ToPixel(b.South, 0), view.ToPixel(b.North, 0), 0.7f);
            GL.End();
        }

        private static void DrawRoute(MapView view, List<MapWaypoint> wps)
        {
            if (wps == null || wps.Count < 2) return;

            GL.Begin(GL.QUADS);
            GL.Color(new Color(0.30f, 0.85f, 1f, 0.95f));
            for (int i = 0; i + 1 < wps.Count; i++)
            {
                Vector2 a = view.ToPixel(wps[i].Lat, wps[i].Lon);
                Vector2 b = view.ToPixel(wps[i + 1].Lat, wps[i + 1].Lon);
                Quad(a, b, 1.3f);
            }
            GL.End();
        }

        private static void DrawWaypoints(MapView view, List<MapWaypoint> wps)
        {
            if (wps == null) return;

            GL.Begin(GL.QUADS);
            for (int i = 0; i < wps.Count; i++)
            {
                Vector2 p = view.ToPixel(wps[i].Lat, wps[i].Lon);
                GL.Color(wps[i].PushedToKsp
                         ? new Color(0.35f, 1f, 0.45f, 1f)
                         : new Color(1f, 0.85f, 0.25f, 1f));
                Box(p, 4.5f);
            }
            GL.End();
        }

        /// <summary>
        /// Kosmodromy jako romby. Romb, a nie kwadrat, celowo: waypointy sa juz
        /// kwadratami, a na mapie o jednej barwie ksztalt rozroznia je pewniej
        /// niz kolor.
        ///
        /// Rysujemy tylko te w kadrze - lista jest krotka, ale przy dalekim
        /// oddaleniu wszystkie 30 spietrzyloby sie w jednym pikselu.
        /// </summary>
        private static void DrawLaunchSites(MapView view)
        {
            GL.Begin(GL.TRIANGLES);
            GL.Color(new Color(0.35f, 0.85f, 1f, 1f));

            for (int i = 0; i < LaunchSites.All.Length; i++)
            {
                LaunchSite ls = LaunchSites.All[i];
                Vector2 p = view.ToPixel(ls.Lat, ls.Lon);

                if (p.x < -12f || p.y < -12f ||
                    p.x > view.Width + 12f || p.y > view.Height + 12f) continue;

                const float r = 6f;
                GL.Vertex3(p.x, p.y - r, 0f);
                GL.Vertex3(p.x + r, p.y, 0f);
                GL.Vertex3(p.x, p.y + r, 0f);

                GL.Vertex3(p.x, p.y - r, 0f);
                GL.Vertex3(p.x, p.y + r, 0f);
                GL.Vertex3(p.x - r, p.y, 0f);
            }

            GL.End();
        }

        private static void DrawVessel(MapView view, double lat, double lon, float headingDeg)
        {
            Vector2 p = view.ToPixel(lat, lon);

            GL.Begin(GL.TRIANGLES);
            GL.Color(new Color(1f, 0.25f, 0.25f, 1f));

            // strzalka w kierunku kursu
            float r = 8f;
            float a = (90f - headingDeg) * Mathf.Deg2Rad;
            Vector2 tip = p + new Vector2(Mathf.Cos(a), -Mathf.Sin(a)) * r;
            Vector2 l = p + new Vector2(Mathf.Cos(a + 2.5f), -Mathf.Sin(a + 2.5f)) * r * 0.7f;
            Vector2 rr = p + new Vector2(Mathf.Cos(a - 2.5f), -Mathf.Sin(a - 2.5f)) * r * 0.7f;

            GL.Vertex3(tip.x, tip.y, 0f);
            GL.Vertex3(l.x, l.y, 0f);
            GL.Vertex3(rr.x, rr.y, 0f);
            GL.End();
        }

        private static void Box(Vector2 p, float half)
        {
            GL.Vertex3(p.x - half, p.y - half, 0f);
            GL.Vertex3(p.x + half, p.y - half, 0f);
            GL.Vertex3(p.x + half, p.y + half, 0f);
            GL.Vertex3(p.x - half, p.y + half, 0f);
        }
    }
}
