using System;
using System.Text;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Strona nawigacji dla RasterPropMonitor (MFD ALCOR z ASET, uzywane m.in. przez DE_IVAExtension).
    ///
    /// RPM tworzy ten modul sam, z wezla PAGEHANDLER / BACKGROUNDHANDLER w konfiguracji
    /// ekranu (patch w OSMRoads_RPM.cfg), i wola metody po nazwie - dlatego mod nie
    /// potrzebuje referencji do RasterPropMonitor.dll i dziala tez bez niego.
    ///
    /// Trzy tryby tla (strzalki LEWO/PRAWO):
    ///   MAP  - trasa na ciemnym tle, kierunkiem jazdy do gory (lekka, czytelna),
    ///   TOP  - prawdziwa scena 3D (teren, drogi, lasy, domy) prosto z gory, kamera ortogonalna,
    ///   3D   - kamera za pojazdem i nad nim, jak w nawigacji samochodowej.
    /// Na TOP i 3D trasa jest dorysowana na obrazie z kamery.
    /// </summary>
    public class OsmNavRpmPage : InternalModule
    {
        private static readonly float[] Zooms = { 0.25f, 0.5f, 1f, 2f, 4f, 8f, 16f, 32f };     // metry na piksel (przy 1024 px)
        private static readonly string[] ModeNames = { "MAP 2D", "MAP 3D", "TOP", "CHASE", "FOLLOW", "MUSIC" };
        /// <summary>Stan KAZDEGO ekranu osobno (klucz: prop ekranu) - na dwoch MFD moga byc
        /// rozne tryby i przyblizenia. Handler strony i handler tla tego samego ekranu
        /// dziela stan przez ten sam prop, nawet jesli RPM utworzy dla nich osobne moduly.</summary>
        private sealed class ScreenState
        {
            public int Zoom = 3, Mode, LastWidth = 40;
            public bool NorthUp, Buildings = true;
            public Vector3 FollowFwd;
            public float Aspect = 1f;             // szerokosc / wysokosc tekstury ekranu
        }
        private static readonly System.Collections.Generic.Dictionary<int, ScreenState> states =
            new System.Collections.Generic.Dictionary<int, ScreenState>();
        private ScreenState st;
        private ScreenState S
        {
            get
            {
                if (st != null) return st;
                int key = internalProp != null ? internalProp.GetInstanceID() : GetInstanceID();
                if (!states.TryGetValue(key, out st)) states[key] = st = new ScreenState();
                return st;
            }
        }
        private int zoom { get { return S.Zoom; } set { S.Zoom = value; } }
        private int mode { get { return S.Mode; } set { S.Mode = value; } }
        private bool northUp { get { return S.NorthUp; } set { S.NorthUp = value; } }
        private int lastWidth { get { return S.LastWidth; } set { S.LastWidth = value; } }
        private Vector3 followFwd { get { return S.FollowFwd; } set { S.FollowFwd = value; } }
        private static Material glMat;
        private static Camera cam;

        // ------------------------------------------------------------------
        //  Przyciski
        // ------------------------------------------------------------------

        /// <summary>
        /// Numery to indeksy z globalButtons ekranu:
        ///   40x20: 0 UP, 1 DOWN, 2 ENTER, 3 ESC, 4 HOME, 5 RIGHT, 6 LEFT, 7 R9, 8 R10
        ///   60x30: 0 UP, 1 DOWN, 2 ZoomIn, 3 ZoomOut, 4 HOME, 5 RIGHT, 6 LEFT, 7 PREV, 8 NEXT
        /// </summary>
        public void ButtonProcessor(int button)
        {
            bool big = lastWidth >= 60;
            if (mode == MusicMode && button != 5 && button != 6)
            {
                // muzyka: ^v / +- glosnosc, ENTER / HOME play-pauza, R9/PREV poprzedni, R10/NEXT nastepny
                switch (button)
                {
                    case 0: MediaClient.SetVolume(MediaClient.Volume + 0.08f); break;
                    case 1: MediaClient.SetVolume(MediaClient.Volume - 0.08f); break;
                    case 2: if (big) MediaClient.SetVolume(MediaClient.Volume + 0.08f); else MediaClient.Toggle(); break;
                    case 3: if (big) MediaClient.SetVolume(MediaClient.Volume - 0.08f); break;
                    case 4: MediaClient.Toggle(); break;
                    case 7: MediaClient.Prev(); break;
                    case 8: MediaClient.Next(); break;
                }
                return;
            }
            switch (button)
            {
                case 0: if (big) northUp = !northUp; else ZoomIn(); break;
                case 1: if (big) S.Buildings = !S.Buildings; else ZoomOut(); break;
                case 2: if (big) ZoomIn(); else northUp = !northUp; break;
                case 3: if (big) ZoomOut(); else Navigation.Reroute(); break;
                case 4: S.Buildings = !S.Buildings; break;
                case 5: mode = (mode + 1) % ModeNames.Length; break;
                case 6: mode = (mode + ModeNames.Length - 1) % ModeNames.Length; break;
                case 7: Navigation.Reroute(); break;
                case 8: Navigation.Stop(); break;
            }
        }

        private void ZoomIn() { zoom = Math.Max(0, zoom - 1); }
        private void ZoomOut() { zoom = Math.Min(Zooms.Length - 1, zoom + 1); }

        public void PageActive(bool active, int pageNumber) { }

        // ------------------------------------------------------------------
        //  Tekst
        // ------------------------------------------------------------------

        public string ShowPage(int screenWidth, int screenHeight)
        {
            lastWidth = screenWidth;
            var sb = new StringBuilder();
            int w = Math.Max(20, screenWidth);
            string head = " OSM NAV  " + ModeNames[mode] + (northUp ? " N-UP" : "") + (mode <= 1 && S.Buildings ? " BLD" : "");
            string scale = RoadRouter.FormatDistance(ScaleBarM()) + " ";
            sb.AppendLine("[#4CE6FFFF]" + Fit(head, w - scale.Length).PadRight(w - scale.Length) + "[#FFFFFFFF]" + scale);

            string legend = lastWidth >= 60
                ? " </> mode  +/- zoom  ^ north-up  v 3D bld  PREV reroute  NEXT stop"
                : " </> mode ^v zoom ENT N-up HOME bld ESC reroute";
            int body = screenHeight - 1 - 5;

            if (mode == MusicMode) return MusicText(sb, w, screenHeight);
            // na mapach: druga linia = co gra (maly kontroler jest w lewym gornym rogu obrazu)
            bool widget = OsmSettings.MusicWidget && MediaClient.Running && MediaClient.HasSession;

            if (widget)
            {
                sb.AppendLine();
                sb.AppendLine("[#A0B8C8FF]" + "".PadRight(WidgetCols) + Fit(Ascii(MediaClient.Title), w - WidgetCols));
                sb.AppendLine("[#6F8796FF]" + "".PadRight(WidgetCols) + Fit(Ascii(MediaClient.Artist), w - WidgetCols));
                body -= 3;
            }
            if (!Navigation.Active)
            {
                sb.AppendLine("[#A0B8C8FF]" + Fit(" No destination - OSM Flight Planner,", w));
                sb.AppendLine("[#A0B8C8FF]" + Fit(" search a place and press Navigate.", w));
                for (int i = 0; i < body + 2; i++) sb.AppendLine();
                sb.Append("[#6F8796FF]" + Fit(legend, w));
                return sb.ToString();
            }

            for (int i = 0; i < body; i++) sb.AppendLine();
            NavRoute r = Navigation.Route;
            if (r == null)
            {
                sb.AppendLine("[#FFB333FF]" + Fit(" " + (Navigation.Error ?? "Calculating route... " + Navigation.Status), w));
                sb.AppendLine(Fit(" to " + Ascii(Navigation.DestName), w));
                sb.AppendLine(); sb.AppendLine();
                sb.Append("[#6F8796FF]" + Fit(legend, w));
                return sb.ToString();
            }
            if (Navigation.Arrived)
            {
                sb.AppendLine("[#66FF80FF]" + Fit(" ARRIVED", w));
                sb.AppendLine(Fit(" " + Ascii(Navigation.DestName), w));
                sb.AppendLine(); sb.AppendLine();
                sb.Append("[#6F8796FF]" + Fit(legend, w));
                return sb.ToString();
            }

            RouteStep st = Navigation.NextStep;
            string dist = RoadRouter.FormatDistance(Navigation.DistToStep);
            string color = Navigation.DistToStep < 150 ? "[#FFB333FF]" : "[#4CE6FFFF]";
            if (!Navigation.OnRoute)
                sb.AppendLine("[#FF6655FF]" + Fit(" OFF ROUTE" + (Navigation.Busy ? " - recalculating" : ""), w));
            else
                sb.AppendLine(color + Fit(" " + Arrow(st) + " " + dist + "  " + Ascii(RoadRouter.Describe(st)), w));

            RouteStep then = Navigation.StepAfterNext;
            sb.AppendLine("[#A0B8C8FF]" + Fit(then != null && st != null && then.AtM - st.AtM < 400 && then.Turn != Maneuver.Arrive
                                              ? "   then " + Ascii(RoadRouter.Describe(then)).ToLowerInvariant() : "", w));
            sb.AppendLine("[#FFFFFFFF]" + Fit(string.Format(" {0} left  {1}  {2:0} km/h",
                                              RoadRouter.FormatDistance(Navigation.RemainingM), RoadRouter.FormatTime(Navigation.EtaS),
                                              Navigation.SpeedMs * 3.6), w));
            sb.AppendLine("[#A0B8C8FF]" + Fit(" to " + Ascii(Navigation.DestName), w));
            sb.Append("[#6F8796FF]" + Fit(legend, w));
            return sb.ToString();
        }

        private float ScaleBarM() { return Zooms[zoom] * 256f; }     // ok. cwierc szerokosci ekranu

        private static string Arrow(RouteStep s)
        {
            if (s == null) return "  ";
            switch (s.Turn)
            {
                case Maneuver.Left: case Maneuver.SharpLeft: return "<-";
                case Maneuver.SlightLeft: return "<^";
                case Maneuver.Right: case Maneuver.SharpRight: return "->";
                case Maneuver.SlightRight: return "^>";
                case Maneuver.UTurn: return "U ";
                case Maneuver.Arrive: return "[]";
                default: return "^^";
            }
        }

        private static string Fit(string s, int w)
        {
            if (s == null) s = "";
            return s.Length > w ? s.Substring(0, w) : s;
        }

        /// <summary>Czcionki MFD maja tylko ASCII - polskie litery bez ogonkow, reszta "?".</summary>
        private static string Ascii(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c < 128) { sb.Append(c == '[' ? '(' : c == ']' ? ')' : c); continue; }
                string f = TextFold.Fold(c.ToString());
                if (char.IsUpper(c) && f.Length > 0) f = f.ToUpperInvariant();
                sb.Append(f.Length == 1 && f[0] < 128 ? f : "?");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  Obraz
        // ------------------------------------------------------------------

        public bool RenderMap(RenderTexture screen, float cameraAspect)
        {
            if (glMat == null) glMat = new Material(Shader.Find("Hidden/Internal-Colored"));
            Vessel v = FlightGlobals.ActiveVessel;
            float W = screen.width, H = screen.height;
            float mpp = Zooms[zoom] * 1024f / Math.Max(256f, W);

            screenW = W; screenH = H; S.Aspect = W / Math.Max(1f, H);
            FindScreen(screen);
            if (mode == MusicMode) { DrawMusic(screen); return true; }

            bool rendered = false, mapScene = false;
            if (v != null && v.mainBody != null)
            {
                try
                {
                    if (mode <= 1)
                    {
                        EnsureCamera();
                        mapScene = rendered = NavMapScene.Render(cam, screen, v, mode == 1, mpp, northUp, S.Buildings);
                    }
                    else rendered = RenderScene(screen, v, mpp);
                }
                catch (Exception e) { Debug.LogWarning("[OSMRoads] RPM camera: " + e); mode = 0; rendered = mapScene = false; }
            }

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = screen;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, W, H, 0f);
            if (!rendered) GL.Clear(true, true, new Color(0.02f, 0.04f, 0.07f, 1f));
            glMat.SetPass(0);

            if (!rendered && v != null) DrawGrid(W, H);
            if (v != null && !mapScene) DrawRoute(screen, v, W, H, mpp, rendered);
            if (OsmSettings.MusicWidget && MediaClient.Running && MediaClient.HasSession) DrawWidget(W, H);

            GL.PopMatrix();
            RenderTexture.active = prev;
            return true;
        }

        /// <summary>Kierunek "do gory ekranu" w poziomie (swiat Unity).</summary>
        private Vector3 ForwardDir(Vessel v, out Vector3 up)
        {
            up = (Vector3)(v.CoMD - v.mainBody.position).normalized;
            Vector3 north = Vector3.ProjectOnPlane(v.mainBody.transform.up, up).normalized;
            if (northUp) return north;
            float h = (float)Navigation.HeadingDeg;
            return Quaternion.AngleAxis(h, up) * north;
        }

        private static void EnsureCamera()
        {
            if (cam != null) return;
            var go = new GameObject("OSMRoads.RPMCamera");
            UnityEngine.Object.DontDestroyOnLoad(go);
            cam = go.AddComponent<Camera>();
            cam.enabled = false;
        }


        private bool RenderScene(RenderTexture screen, Vessel v, float mpp)
        {
            EnsureCamera();
            Camera main = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : Camera.main;

            Vector3 up;
            Vector3 fwd = ForwardDir(v, out up);
            Vector3 pos = v.CoMD;

            // teren, drogi, domy, drzewa (warstwa 15) i pojazd (0)
            cam.cullingMask = (1 << 0) | (1 << 15) | (1 << 4);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.52f, 0.66f, 0.82f, 1f);
            cam.targetTexture = screen;
            cam.aspect = screen.width / (float)screen.height;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.renderingPath = main != null ? main.renderingPath : RenderingPath.Forward;

            if (mode == 2)
            {
                float halfH = mpp * screen.height * 0.5f;
                cam.orthographic = true;
                cam.orthographicSize = halfH;
                float lift = Math.Max(800f, halfH * 4f);
                // pojazd lekko ponizej srodka, jak na mapie - wiecej widac przed nim
                cam.transform.position = pos + up * lift + fwd * (halfH * 0.24f);
                cam.transform.rotation = Quaternion.LookRotation(-up, fwd);
                cam.nearClipPlane = 1f;
                cam.farClipPlane = lift + 2000f;
            }
            else if (mode == 4)
            {
                // FOLLOW: nisko za pojazdem, kierunek doganiany z opoznieniem jak w grach wyscigowych
                if (followFwd == Vector3.zero || Vector3.Dot(followFwd, fwd) < -0.5f) followFwd = fwd;
                followFwd = Vector3.Slerp(followFwd, fwd, Mathf.Clamp01(Time.deltaTime * 2.5f));
                followFwd = Vector3.ProjectOnPlane(followFwd, up).normalized;
                float dist = 9f * Mathf.Pow(2f, zoom - 3);
                cam.orthographic = false;
                cam.fieldOfView = 62f;
                cam.transform.position = pos - followFwd * dist + up * (dist * 0.32f + 1.5f);
                cam.transform.rotation = Quaternion.LookRotation((pos + followFwd * dist * 0.6f + up * 1f) - cam.transform.position, up);
                cam.nearClipPlane = 0.3f;
                cam.farClipPlane = 8000f;
            }
            else
            {
                float dist = 18f * Mathf.Pow(2f, zoom - 3);
                cam.orthographic = false;
                cam.fieldOfView = 55f;
                cam.transform.position = pos - fwd * dist + up * (dist * 0.55f);
                cam.transform.rotation = Quaternion.LookRotation((pos + fwd * dist * 0.8f) - cam.transform.position, up);
                cam.nearClipPlane = 0.5f;
                cam.farClipPlane = 8000f;
            }

            cam.Render();          // targetTexture zostaje: WorldToScreenPoint liczy w pikselach tego ekranu
            return true;
        }

        private static void DrawGrid(float W, float H)
        {
            float cx = W * 0.5f, cy = H * 0.62f;
            GL.Begin(GL.LINES);
            GL.Color(new Color(0.3f, 0.9f, 1f, 0.08f));
            for (float gx = cx % 100f; gx < W; gx += 100f) { GL.Vertex3(gx, 0, 0); GL.Vertex3(gx, H, 0); }
            for (float gy = cy % 100f; gy < H; gy += 100f) { GL.Vertex3(0, gy, 0); GL.Vertex3(W, gy, 0); }
            GL.End();
        }

        /// <summary>Trasa i znaczniki. W trybie MAP rzut plaski (metry wzgledem pojazdu),
        /// w TOP/3D rzut przez kamere - linia lezy dokladnie na drodze z obrazu.</summary>
        private void DrawRoute(RenderTexture screen, Vessel v, float W, float H, float mpp, bool viaCamera)
        {
            NavRoute r = Navigation.Route;
            double lat = v.latitude, lon = Navigation.Norm(v.longitude);
            CelestialBody body = v.mainBody;

            Func<GeoPoint, Vector3> project;
            if (viaCamera)
            {
                project = p =>
                {
                    double alt = body.TerrainAltitude(p.Lat, p.Lon, true) + 1.0;
                    Vector3 sp = cam.WorldToScreenPoint(body.GetWorldSurfacePosition(p.Lat, p.Lon, alt));
                    return new Vector3(sp.x * W / cam.pixelWidth, H - sp.y * H / cam.pixelHeight, sp.z);
                };
            }
            else
            {
                double hdg = (northUp ? 0.0 : Navigation.HeadingDeg) * Math.PI / 180.0;
                float cs = (float)Math.Cos(hdg), sn = (float)Math.Sin(hdg);
                float cx = W * 0.5f, cy = H * 0.62f;
                project = p =>
                {
                    double e, n;
                    Geo.ToLocalMeters(p.Lat, p.Lon, lat, lon, body.Radius, out e, out n);
                    float x = (float)(e * cs - n * sn), y = (float)(e * sn + n * cs);
                    return new Vector3(cx + x / mpp, cy - y / mpp, 1f);
                };
            }

            if (r != null && r.Points.Count > 1)
            {
                double view = mpp * Math.Max(W, H) * 1.2;
                int from = Math.Max(0, Navigation.SegIdx - 400);
                GL.Begin(GL.QUADS);
                Vector3 prevP = Vector3.zero; bool hasPrev = false;
                for (int i = from; i < r.Points.Count; i++)
                {
                    if (r.Cum[i] - Navigation.AlongM > view) break;
                    if (i + 1 < r.Points.Count && Navigation.AlongM - r.Cum[i + 1] > view) { hasPrev = false; continue; }
                    Vector3 q = project(r.Points[i]);
                    if (hasPrev && q.z > 0f && prevP.z > 0f)
                    {
                        bool done = r.Cum[i] <= Navigation.AlongM;
                        Thick(prevP, q, done ? 3f : 7f, done ? new Color(0.3f, 0.5f, 0.6f, 0.6f) : new Color(0.25f, 0.85f, 1f, 0.95f));
                    }
                    prevP = q; hasPrev = true;
                }
                GL.End();

                RouteStep st = Navigation.NextStep;
                if (st != null) { Vector3 q = project(r.Points[Math.Min(st.Index, r.Points.Count - 1)]); if (q.z > 0f) Dot(q, 9f, new Color(1f, 0.7f, 0.2f, 1f)); }
                Vector3 end = project(r.Points[r.Points.Count - 1]);
                if (end.z > 0f) Dot(end, 10f, new Color(0.4f, 1f, 0.5f, 1f));
            }

            // pojazd: w MAP/TOP trojkat w miejscu pojazdu, w 3D widac model, wiec bez znacznika
            if (!viaCamera || mode == 2)
            {
                Vector3 c = viaCamera ? project(new GeoPoint(lat, lon)) : new Vector3(W * 0.5f, H * 0.62f, 1f);
                float a = northUp ? (float)(Navigation.HeadingDeg * Math.PI / 180.0) : 0f;
                Vector2 f = new Vector2(Mathf.Sin(a), -Mathf.Cos(a)), s = new Vector2(-f.y, f.x);
                GL.Begin(GL.TRIANGLES);
                GL.Color(Color.white);
                Vertex(new Vector2(c.x, c.y) + f * 16f);
                Vertex(new Vector2(c.x, c.y) - f * 12f + s * 11f);
                Vertex(new Vector2(c.x, c.y) - f * 12f - s * 11f);
                GL.End();
            }
        }

        private static void Vertex(Vector2 p) { GL.Vertex3(p.x, p.y, 0); }

        private static void Thick(Vector3 a, Vector3 b, float w, Color c)
        {
            var d = new Vector2(b.x - a.x, b.y - a.y);
            if (d.sqrMagnitude < 0.01f) return;
            Vector2 n = new Vector2(-d.y, d.x).normalized * (w * 0.5f);
            GL.Color(c);
            GL.Vertex3(a.x + n.x, a.y + n.y, 0); GL.Vertex3(b.x + n.x, b.y + n.y, 0);
            GL.Vertex3(b.x - n.x, b.y - n.y, 0); GL.Vertex3(a.x - n.x, a.y - n.y, 0);
        }

        // ------------------------------------------------------------------
        //  Muzyka: tryb MUSIC (caly ekran) i maly kontroler na mapach
        // ------------------------------------------------------------------

        private const int MusicMode = 5;
        private const int WidgetCols = 11;          // tyle znakow tekstu zajmuje z lewej maly kontroler
        private float screenW = 512f, screenH = 512f;
        private static Material texMat;

        // prostokaty do klikania (w pikselach ekranu, od lewego gornego rogu)
        private Rect hitPrev, hitPlay, hitNext, hitBar, hitVolDown, hitVolUp, hitVol;
        private bool hitsValid;

        private string MusicText(System.Text.StringBuilder sb, int w, int screenHeight)
        {
            MediaClient.EnsureStarted();
            var rows = new string[Math.Max(6, screenHeight - 1)];
            for (int i = 0; i < rows.Length; i++) rows[i] = "";
            if (!MediaClient.HasSession)
            {
                rows[0] = "[#A0B8C8FF]" + Fit(" Nothing is playing in Windows.", w);
                rows[1] = "[#6F8796FF]" + Fit(" Start Spotify, YouTube or any player.", w);
            }
            else
            {
                rows[0] = "[#FFFFFFFF]" + Fit(" " + Ascii(MediaClient.Title), w);
                rows[1] = "[#A0B8C8FF]" + Fit(" " + Ascii(MediaClient.Artist), w);

                // czasy tuz pod paskiem postepu: przegrany po lewej, calosc po prawej
                // (ten sam uklad co DrawMusic: pasek na 0.19 + min(0.62 * W/H, 0.52) wysokosci)
                float barFrac = 0.19f + Mathf.Min(0.62f * S.Aspect, 0.52f);
                int barRow = (int)Math.Floor(barFrac * screenHeight + 0.35f);        // wiersz ekranu z paskiem
                int r = Math.Min(rows.Length - 2, barRow);                           // rows[] zaczyna sie od wiersza 1
                string left = " " + MediaClient.Clock(MediaClient.Position);
                string right = MediaClient.Clock(MediaClient.Duration) + " ";
                rows[r] = "[#A0B8C8FF]" + left + "".PadRight(Math.Max(1, w - left.Length - right.Length)) + right;
            }
            rows[rows.Length - 1] = "[#6F8796FF]" + Fit(lastWidth >= 60
                ? " </> mode  HOME play  PREV/NEXT track  +/- volume  (click screen)"
                : " </> mode ENT play R9/R10 track ^v vol", w);
            for (int i = 0; i < rows.Length; i++) { if (i < rows.Length - 1) sb.AppendLine(rows[i]); else sb.Append(rows[i]); }
            return sb.ToString();
        }
        private static Material TexMat(Texture t)
        {
            if (texMat == null) texMat = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Transparent"));
            texMat.mainTexture = t;
            return texMat;
        }

        private static void Quad(Rect r, Texture t, Color c)
        {
            if (t == null) return;
            TexMat(t).SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(c);
            GL.TexCoord2(0, 1); GL.Vertex3(r.xMin, r.yMin, 0);
            GL.TexCoord2(1, 1); GL.Vertex3(r.xMax, r.yMin, 0);
            GL.TexCoord2(1, 0); GL.Vertex3(r.xMax, r.yMax, 0);
            GL.TexCoord2(0, 0); GL.Vertex3(r.xMin, r.yMax, 0);
            GL.End();
        }

        private static void Box(Rect r, Color c)
        {
            glMat.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(c);
            GL.Vertex3(r.xMin, r.yMin, 0); GL.Vertex3(r.xMax, r.yMin, 0);
            GL.Vertex3(r.xMax, r.yMax, 0); GL.Vertex3(r.xMin, r.yMax, 0);
            GL.End();
        }

        private void DrawMusic(RenderTexture screen)
        {
            MediaClient.EnsureStarted();
            MediaClient.Tick();
            float W = screen.width, H = screen.height;
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = screen;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, W, H, 0f);
            GL.Clear(true, true, new Color(0.02f, 0.04f, 0.07f, 1f));

            // okladka pod dwiema liniami tekstu, na srodku
            float top = H * 0.14f, artS = Mathf.Min(W * 0.62f, H * 0.52f);
            var art = new Rect((W - artS) * 0.5f, top, artS, artS);
            Box(new Rect(art.x - 3, art.y - 3, art.width + 6, art.height + 6), new Color(0.3f, 0.9f, 1f, 0.35f));
            if (MediaClient.Art != null) Quad(art, MediaClient.Art, Color.white);
            else Quad(new Rect(art.x + artS * 0.25f, art.y + artS * 0.25f, artS * 0.5f, artS * 0.5f), AppIconMusic(), new Color(0.3f, 0.9f, 1f, 0.6f));

            // pasek postepu
            float by = art.yMax + H * 0.05f;
            hitBar = new Rect(W * 0.08f, by - 8f, W * 0.84f, 22f);
            var bar = new Rect(W * 0.08f, by, W * 0.84f, 6f);
            Box(bar, new Color(1f, 1f, 1f, 0.14f));
            double dur = MediaClient.Duration;
            float frac = dur > 0 ? Mathf.Clamp01((float)(MediaClient.Position / dur)) : 0f;
            Box(new Rect(bar.x, bar.y, bar.width * frac, bar.height), new Color(0.3f, 0.9f, 1f, 1f));

            // przyciski
            float bs = H * 0.09f, cy = by + H * 0.045f, cx = W * 0.5f;
            hitPrev = new Rect(cx - bs * 2.2f, cy, bs, bs);
            hitPlay = new Rect(cx - bs * 0.65f, cy - bs * 0.15f, bs * 1.3f, bs * 1.3f);
            hitNext = new Rect(cx + bs * 1.2f, cy, bs, bs);
            Quad(hitPrev, MediaUI.IconTexture(0), Color.white);
            Box(new Rect(hitPlay.x - 2, hitPlay.y - 2, hitPlay.width + 4, hitPlay.height + 4), new Color(0.3f, 0.9f, 1f, 0.25f));
            Quad(hitPlay, MediaUI.IconTexture(MediaClient.Playing ? 3 : 2), Color.white);
            Quad(hitNext, MediaUI.IconTexture(1), Color.white);

            // glosnosc: - [pasek] +
            hitsValid = true;
            if (MediaClient.Volume >= 0f)
            {
                float vy = cy + bs * 1.45f, vh = bs * 0.55f;
                hitVolDown = new Rect(W * 0.14f, vy, vh, vh);
                hitVolUp = new Rect(W * 0.86f - vh, vy, vh, vh);
                hitVol = new Rect(hitVolDown.xMax + 8f, vy + vh * 0.35f, hitVolUp.x - hitVolDown.xMax - 16f, vh * 0.3f);
                Box(new Rect(hitVolDown.x, hitVolDown.center.y - 2, vh, 4), Color.white);
                Box(new Rect(hitVolUp.x, hitVolUp.center.y - 2, vh, 4), Color.white);
                Box(new Rect(hitVolUp.center.x - 2, hitVolUp.y, 4, vh), Color.white);
                Box(hitVol, new Color(1f, 1f, 1f, 0.14f));
                Box(new Rect(hitVol.x, hitVol.y, hitVol.width * MediaClient.Volume, hitVol.height), new Color(0.3f, 0.9f, 1f, 0.9f));
            }
            else { hitVolDown = hitVolUp = hitVol = new Rect(); }

            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        /// <summary>Maly kontroler w lewym gornym rogu map: okladka i trzy przyciski (tekst obok daje RPM).</summary>
        private void DrawWidget(float W, float H)
        {
            MediaClient.Tick();
            float row = H / 20f, top = row * 1.05f, s = row * 1.9f;       // miesci sie w wierszach 2-3 tekstu
            var bg = new Rect(4f, top, W * WidgetCols / Math.Max(20, lastWidth) - 6f, s + 8f);
            Box(bg, new Color(0.02f, 0.04f, 0.07f, 0.8f));
            var art = new Rect(bg.x + 4f, top + 4f, s, s);
            if (MediaClient.Art != null) Quad(art, MediaClient.Art, Color.white);
            float bs = Mathf.Min(s * 0.55f, (bg.xMax - art.xMax - 10f) / 3.2f);
            float bx = art.xMax + 6f, byy = top + 4f + (s - bs) * 0.5f;
            hitPrev = new Rect(bx, byy, bs, bs);
            hitPlay = new Rect(bx + bs * 1.05f, byy, bs, bs);
            hitNext = new Rect(bx + bs * 2.1f, byy, bs, bs);
            Quad(hitPrev, MediaUI.IconTexture(0), Color.white);
            Quad(hitPlay, MediaUI.IconTexture(MediaClient.Playing ? 3 : 2), Color.white);
            Quad(hitNext, MediaUI.IconTexture(1), Color.white);
            hitBar = new Rect(bg.x, bg.yMax - 4f, bg.width, 8f);
            double dur = MediaClient.Duration;
            float frac = dur > 0 ? Mathf.Clamp01((float)(MediaClient.Position / dur)) : 0f;
            Box(new Rect(bg.x, bg.yMax - 3f, bg.width, 3f), new Color(1f, 1f, 1f, 0.14f));
            Box(new Rect(bg.x, bg.yMax - 3f, bg.width * frac, 3f), new Color(0.3f, 0.9f, 1f, 1f));
            hitVolDown = hitVolUp = hitVol = new Rect();
            hitsValid = true;
            glMat.SetPass(0);
        }

        private static Texture2D musicIcon;
        private static Texture2D AppIconMusic() { if (musicIcon == null) musicIcon = AppIcons.Music(); return musicIcon; }

        // --- klikanie myszka w ekran w IVA ---
        private Renderer screenRenderer;
        private Collider screenCollider;

        /// <summary>Ekran to ten renderer propa, ktory ma na materiale nasza teksture RPM.</summary>
        private void FindScreen(RenderTexture rt)
        {
            if (screenRenderer != null || internalProp == null) return;
            foreach (Renderer r in internalProp.GetComponentsInChildren<Renderer>(true))
            {
                Material m = r.sharedMaterial;
                if (m == null) continue;
                if ((m.HasProperty("_MainTex") && m.GetTexture("_MainTex") == rt) ||
                    (m.HasProperty("_Emissive") && m.GetTexture("_Emissive") == rt))
                {
                    screenRenderer = r;
                    screenCollider = r.GetComponent<Collider>();
                    if (screenCollider == null)
                    {
                        MeshFilter mf = r.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null)
                        {
                            var mc = r.gameObject.AddComponent<MeshCollider>();
                            mc.sharedMesh = mf.sharedMesh;
                            screenCollider = mc;
                        }
                    }
                    break;
                }
            }
        }

        public override void OnUpdate()
        {
            base.OnUpdate();
            if (!hitsValid || screenCollider == null || !Input.GetMouseButtonDown(0)) return;
            if (mode != MusicMode && !(OsmSettings.MusicWidget && MediaClient.HasSession)) return;
            if (InternalCamera.Instance == null) return;
            Camera ic = InternalCamera.Instance.GetComponent<Camera>();
            if (ic == null) return;

            RaycastHit hit;
            if (!screenCollider.Raycast(ic.ScreenPointToRay(Input.mousePosition), out hit, 10f)) return;
            Vector2 uv = hit.textureCoord;
            if (!(screenCollider is MeshCollider))
            {
                // kolider pudelkowy: UV z polozenia w granicach ekranu
                Vector3 lp = screenRenderer.transform.InverseTransformPoint(hit.point);
                Bounds b = screenRenderer.GetComponent<MeshFilter>() != null ? screenRenderer.GetComponent<MeshFilter>().sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one);
                uv = new Vector2((lp.x - b.min.x) / b.size.x, (lp.z - b.min.z) / Mathf.Max(1e-5f, b.size.z));
            }
            var p = new Vector2(uv.x * screenW, (1f - uv.y) * screenH);

            if (hitPlay.Contains(p)) MediaClient.Toggle();
            else if (hitPrev.Contains(p)) MediaClient.Prev();
            else if (hitNext.Contains(p)) MediaClient.Next();
            else if (hitBar.Contains(p) && MediaClient.Duration > 0)
                MediaClient.Seek(Mathf.Clamp01((p.x - hitBar.x) / hitBar.width) * MediaClient.Duration);
            else if (hitVolDown.Contains(p)) MediaClient.SetVolume(MediaClient.Volume - 0.08f);
            else if (hitVolUp.Contains(p)) MediaClient.SetVolume(MediaClient.Volume + 0.08f);
            else if (hitVol.width > 0 && new Rect(hitVol.x, hitVol.y - 10, hitVol.width, hitVol.height + 20).Contains(p))
                MediaClient.SetVolume(Mathf.Clamp01((p.x - hitVol.x) / hitVol.width));
        }

        private static void Dot(Vector3 p, float r, Color c)
        {
            GL.Begin(GL.TRIANGLES);
            GL.Color(c);
            for (int i = 0; i < 12; i++)
            {
                float a0 = i * Mathf.PI / 6f, a1 = (i + 1) * Mathf.PI / 6f;
                GL.Vertex3(p.x, p.y, 0);
                GL.Vertex3(p.x + Mathf.Cos(a0) * r, p.y + Mathf.Sin(a0) * r, 0);
                GL.Vertex3(p.x + Mathf.Cos(a1) * r, p.y + Mathf.Sin(a1) * r, 0);
            }
            GL.End();
        }
    }
}
