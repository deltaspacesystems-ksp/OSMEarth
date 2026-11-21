using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Nawigacja w locie: HUD z nastepnym manewrem i holograficzne strzalki
    /// unoszace sie nad trasa przed pojazdem.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class NavigationAddon : MonoBehaviour
    {
        private NavArrows arrows;

        public void Start()
        {
            OsmSettings.EnsureLoaded();
            arrows = new NavArrows();
        }

        public void OnDestroy()
        {
            if (arrows != null) arrows.Destroy();
        }

        public void Update()
        {
            Navigation.Tick(FlightGlobals.ActiveVessel);
        }

        public void LateUpdate()
        {
            if (arrows != null) arrows.Update(FlightGlobals.ActiveVessel);
        }

        public void OnGUI()
        {
            if (!Navigation.Active) return;
            if (global::MapView.MapIsEnabled && Navigation.Route != null && !Navigation.Busy) { NavHud.Draw(true); return; }
            NavHud.Draw(false);
        }
    }

    // ======================================================================
    //  HUD
    // ======================================================================

    public static class NavHud
    {
        private static Texture2D bg, bar, barFill;
        private static readonly Dictionary<int, Texture2D> icons = new Dictionary<int, Texture2D>();
        private static GUIStyle big, mid, small, close;
        private static float styleScale = -1f;
        private static bool dragging;
        private static Vector2 dragOff;

        public static readonly Color Cyan = new Color(0.30f, 0.90f, 1.00f);
        public static readonly Color Amber = new Color(1.00f, 0.70f, 0.20f);

        private static void Styles(float s)
        {
            if (bg == null)
            {
                bg = Rounded(32, 10, new Color(0.04f, 0.06f, 0.09f, 0.84f), new Color(0.30f, 0.90f, 1f, 0.35f));
                bar = Solid(new Color(1f, 1f, 1f, 0.12f));
                barFill = Solid(Cyan);
            }
            if (Mathf.Approximately(styleScale, s) && big != null) return;
            styleScale = s;
            big = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(30 * s), fontStyle = FontStyle.Bold };
            big.normal.textColor = Color.white;
            mid = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(15 * s), wordWrap = false, clipping = TextClipping.Clip };
            mid.normal.textColor = new Color(0.92f, 0.96f, 1f);
            small = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(12 * s) };
            small.normal.textColor = new Color(0.65f, 0.78f, 0.85f);
            close = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(14 * s), alignment = TextAnchor.MiddleCenter };
            close.normal.textColor = new Color(0.7f, 0.8f, 0.9f);
            close.hover.textColor = Color.white;
        }

        public static void Draw(bool compact)
        {
            float s = OsmSettings.NavHudScale * GameSettings.UI_SCALE;
            Styles(s);

            float w = 430f * s, h = (compact ? 64f : 128f) * s;
            float x = OsmSettings.NavHudX >= 0f ? OsmSettings.NavHudX : (Screen.width - w) * 0.5f;
            float y = OsmSettings.NavHudY >= 0f ? OsmSettings.NavHudY : Screen.height * 0.115f;
            x = Mathf.Clamp(x, 0f, Screen.width - w); y = Mathf.Clamp(y, 0f, Screen.height - h);
            var r = new Rect(x, y, w, h);

            Event e = Event.current;
            GUI.color = Color.white;
            GUI.Box(r, GUIContent.none, BgStyle());

            // zamkniecie
            if (GUI.Button(new Rect(r.xMax - 24f * s, r.y + 4f * s, 20f * s, 20f * s), "×", close)) { Navigation.Stop(); return; }

            // przeciaganie za tlo
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition)) { dragging = true; dragOff = e.mousePosition - new Vector2(x, y); e.Use(); }
            if (dragging && e.type == EventType.MouseDrag) { OsmSettings.NavHudX = e.mousePosition.x - dragOff.x; OsmSettings.NavHudY = e.mousePosition.y - dragOff.y; e.Use(); }
            if (dragging && e.rawType == EventType.MouseUp) { dragging = false; OsmSettings.Save(); }

            float pad = 12f * s;
            NavRoute route = Navigation.Route;

            if (route == null)
            {
                string msg = Navigation.Error ?? ("Calculating route to " + Navigation.DestName + "…  " + Navigation.Status);
                var st = new GUIStyle(mid) { wordWrap = true };
                if (Navigation.Error != null) st.normal.textColor = new Color(1f, 0.6f, 0.5f);
                GUI.Label(new Rect(r.x + pad, r.y + pad, w - 2 * pad - 20f * s, h - 2 * pad), msg, st);
                return;
            }

            RouteStep step = Navigation.NextStep;
            bool near = step != null && Navigation.DistToStep < 150.0;
            float pulse = near ? 0.75f + 0.25f * Mathf.Sin(Time.realtimeSinceStartup * 6f) : 1f;
            Color accent = Navigation.Arrived ? new Color(0.4f, 1f, 0.5f) : (!Navigation.OnRoute ? new Color(1f, 0.45f, 0.35f) : (near ? Amber : Cyan));

            float icon = h - 2 * pad - (compact ? 0f : 18f * s);
            GUI.color = new Color(accent.r, accent.g, accent.b, pulse);
            if (step != null) GUI.DrawTexture(new Rect(r.x + pad, r.y + pad, icon, icon), Icon(step));
            GUI.color = Color.white;

            float tx = r.x + pad * 2 + icon, tw = r.xMax - tx - pad - 16f * s;

            if (Navigation.Arrived)
            {
                GUI.Label(new Rect(tx, r.y + pad, tw, 40f * s), "Arrived", big);
                GUI.Label(new Rect(tx, r.y + pad + 38f * s, tw, 22f * s), Navigation.DestName, mid);
                return;
            }

            string dist = RoadRouter.FormatDistance(Navigation.DistToStep);
            string what = !Navigation.OnRoute ? (Navigation.Busy ? "Off route - recalculating…" : "Off route - return to the highlighted road")
                                              : RoadRouter.Describe(step);
            GUI.Label(new Rect(tx, r.y + pad * 0.6f, tw, 40f * s), dist, big);
            GUI.Label(new Rect(tx, r.y + pad * 0.6f + 36f * s, tw, 22f * s), what, mid);
            if (compact) return;

            RouteStep then = Navigation.StepAfterNext;
            if (then != null && step != null && then.AtM - step.AtM < 400.0 && then.Turn != Maneuver.Arrive)
                GUI.Label(new Rect(tx, r.y + pad * 0.6f + 58f * s, tw, 18f * s), "then " + RoadRouter.Describe(then).ToLowerInvariant(), small);

            // dol: postep i podsumowanie
            float by = r.yMax - pad - 16f * s;
            string sum = string.Format("{0} left  ·  {1}  ·  {2:0} km/h  ·  to {3}",
                                       RoadRouter.FormatDistance(Navigation.RemainingM), RoadRouter.FormatTime(Navigation.EtaS),
                                       Navigation.SpeedMs * 3.6, Navigation.DestName);
            GUI.Label(new Rect(r.x + pad, by - 4f * s, w - 2 * pad, 18f * s), sum, small);
            var track = new Rect(r.x + pad, r.yMax - pad * 0.6f, w - 2 * pad, 3f * s);
            GUI.DrawTexture(track, bar);
            float frac = route.LengthM > 1 ? (float)(Navigation.AlongM / route.LengthM) : 0f;
            GUI.color = accent;
            GUI.DrawTexture(new Rect(track.x, track.y, track.width * Mathf.Clamp01(frac), track.height), barFill);
            GUI.color = Color.white;
        }

        private static GUIStyle bgStyle;
        private static GUIStyle BgStyle()
        {
            if (bgStyle == null)
            {
                bgStyle = new GUIStyle();
                bgStyle.normal.background = bg;
                bgStyle.border = new RectOffset(12, 12, 12, 12);
            }
            return bgStyle;
        }

        // ------------------------------------------------------------------
        //  Ikony manewrow rysowane w kodzie (bez plikow tekstur)
        // ------------------------------------------------------------------

        public static Texture2D Icon(RouteStep st)
        {
            int key;
            if (st.Turn == Maneuver.Arrive) key = 1000;
            else if (st.Turn == Maneuver.UTurn) key = st.Angle >= 0 ? 1001 : 1002;
            else if (st.Turn == Maneuver.Depart || st.Turn == Maneuver.Straight) key = 0;
            else key = Mathf.Clamp(Mathf.RoundToInt(st.Angle / 15f) * 15, -150, 150);

            Texture2D t;
            if (icons.TryGetValue(key, out t) && t != null) return t;
            t = MakeIcon(key);
            icons[key] = t;
            return t;
        }

        private static Texture2D MakeIcon(int key)
        {
            const int N = 128;
            var pts = new List<Vector2>();
            Vector2 headAt, headDir;
            if (key == 1000)
            {
                return Target(N);
            }
            if (key == 1001 || key == 1002)
            {
                float sgn = key == 1001 ? 1f : -1f;
                float cx = 64f, r0 = 22f;
                pts.Add(new Vector2(cx - sgn * r0, 118f));
                pts.Add(new Vector2(cx - sgn * r0, 58f));
                for (int i = 1; i <= 12; i++)
                {
                    float a = Mathf.PI * i / 12f;
                    pts.Add(new Vector2(cx - sgn * r0 * Mathf.Cos(a), 58f - r0 * Mathf.Sin(a)));
                }
                pts.Add(new Vector2(cx + sgn * r0, 78f));
                headAt = new Vector2(cx + sgn * r0, 96f); headDir = new Vector2(0f, 1f);
            }
            else
            {
                float a = key * Mathf.Deg2Rad;
                var dir = new Vector2(Mathf.Sin(a), -Mathf.Cos(a));
                pts.Add(new Vector2(64f, 120f));
                Vector2 knee = key == 0 ? new Vector2(64f, 60f) : new Vector2(64f, 70f);
                pts.Add(knee);
                float len = key == 0 ? 10f : 30f;
                Vector2 end = knee + dir * len;
                pts.Add(end);
                headAt = end + dir * 26f; headDir = dir;
            }

            var tex = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            var px = new Color32[N * N];
            Vector2 side = new Vector2(-headDir.y, headDir.x);
            Vector2 h0 = headAt, h1 = headAt - headDir * 36f + side * 26f, h2 = headAt - headDir * 36f - side * 26f;
            const float half = 9f, outline = 3.5f;

            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    var p = new Vector2(x + 0.5f, N - y - 0.5f);     // wiersz 0 tekstury jest na dole, geometria od gory
                    float d = float.MaxValue;
                    for (int i = 0; i + 1 < pts.Count; i++) d = Mathf.Min(d, SegDist(p, pts[i], pts[i + 1]));
                    float shaft = half - d;
                    float head = TriSigned(p, h0, h1, h2);
                    float inside = Mathf.Max(shaft, head);                 // >0 wewnatrz
                    float core = Mathf.Clamp01(inside + 0.5f);
                    float ring = Mathf.Clamp01(inside + outline + 0.5f);
                    // bialy rdzen, ciemna obwodka (kolor nadaje GUI.color)
                    float lum = core;
                    float alpha = Mathf.Max(core, ring * 0.85f);
                    px[y * N + x] = new Color32((byte)(255 * lum), (byte)(255 * lum), (byte)(255 * lum), (byte)(255 * alpha));
                }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        private static Texture2D Target(int N)
        {
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(64f, 64f));
                    float ring = Mathf.Clamp01(8f - Mathf.Abs(d - 44f) + 0.5f);
                    float dot = Mathf.Clamp01(18f - d + 0.5f);
                    float c = Mathf.Max(ring, dot);
                    float o = Mathf.Max(Mathf.Clamp01(11.5f - Mathf.Abs(d - 44f)), Mathf.Clamp01(21.5f - d)) * 0.85f;
                    px[y * N + x] = new Color32((byte)(255 * c), (byte)(255 * c), (byte)(255 * c), (byte)(255 * Mathf.Max(c, o)));
                }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        private static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude));
            return Vector2.Distance(p, a + ab * t);
        }

        /// <summary>Odleglosc ze znakiem od brzegu trojkata: dodatnia wewnatrz.</summary>
        private static float TriSigned(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Edge(p, a, b), d2 = Edge(p, b, c), d3 = Edge(p, c, a);
            float sgn = Edge(c, a, b) > 0 ? 1f : -1f;
            return Mathf.Min(Mathf.Min(d1 * sgn, d2 * sgn), d3 * sgn);
        }

        private static float Edge(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 e = b - a;
            return ((p.x - a.x) * e.y - (p.y - a.y) * e.x) / Mathf.Max(1e-5f, e.magnitude);
        }

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private static Texture2D Rounded(int n, int rad, Color fill, Color edge)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, rad, n - rad), cy = Mathf.Clamp(y + 0.5f, rad, n - rad);
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                    float a = Mathf.Clamp01(rad - d + 0.5f);
                    Color c = d > rad - 1.5f ? Color.Lerp(fill, edge, 0.8f) : fill;
                    c.a *= a;
                    t.SetPixel(x, y, c);
                }
            t.Apply();
            return t;
        }
    }

    // ======================================================================
    //  Holograficzne strzalki 3D
    // ======================================================================

    public sealed class NavArrows
    {
        private const int Count = 28;
        private const float Spacing = 12f, Lead = 9f, Hover = 1.7f, FlowSpeed = 5f;

        private readonly GameObject[] go = new GameObject[Count];
        private readonly MeshRenderer[] mr = new MeshRenderer[Count];
        private readonly MaterialPropertyBlock mpb = new MaterialPropertyBlock();
        private Material mat;
        private bool shown;
        private readonly RaycastHit[] hits = new RaycastHit[4];

        public NavArrows()
        {
            Mesh mesh = Chevron();
            mat = MakeMaterial();
            for (int i = 0; i < Count; i++)
            {
                go[i] = new GameObject("OSMRoads.NavArrow" + i);
                go[i].AddComponent<MeshFilter>().sharedMesh = mesh;
                mr[i] = go[i].AddComponent<MeshRenderer>();
                mr[i].sharedMaterial = mat;
                mr[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr[i].receiveShadows = false;
                go[i].SetActive(false);
            }
        }

        public void Destroy()
        {
            foreach (GameObject g in go) if (g != null) UnityEngine.Object.Destroy(g);
            if (mat != null) UnityEngine.Object.Destroy(mat);
        }

        private void Hide()
        {
            if (!shown) return;
            shown = false;
            foreach (GameObject g in go) g.SetActive(false);
        }

        public void Update(Vessel v)
        {
            NavRoute r = Navigation.Route;
            if (!OsmSettings.NavArrows || r == null || v == null || v.mainBody == null || Navigation.Arrived ||
                global::MapView.MapIsEnabled || Navigation.CrossM > 150.0 || v.heightFromTerrain > 400f ||
                Math.Abs(r.BodyRadius - v.mainBody.Radius) > 1.0)
            { Hide(); return; }

            shown = true;
            CelestialBody body = v.mainBody;
            float t = Time.time;
            float flow = (t * FlowSpeed) % Spacing;
            RouteStep step = Navigation.NextStep;

            for (int i = 0; i < Count; i++)
            {
                double s = Navigation.AlongM + Lead + i * Spacing + flow;
                if (s > r.LengthM - 2.0) { go[i].SetActive(false); continue; }

                int seg;
                GeoPoint p = Navigation.PointAt(r, s, out seg);
                GeoPoint pa = Navigation.PointAt(r, s + 3.0, out seg);
                GeoPoint pb = Navigation.PointAt(r, s - 3.0, out seg);

                double alt = GroundAlt(body, p.Lat, p.Lon);
                float bob = 0.18f * Mathf.Sin(t * 2.2f - i * 0.55f);
                Vector3d pos = body.GetWorldSurfacePosition(p.Lat, p.Lon, alt + Hover + bob);
                Vector3d fwd = body.GetWorldSurfacePosition(pa.Lat, pa.Lon, alt) - body.GetWorldSurfacePosition(pb.Lat, pb.Lon, alt);
                Vector3 up = (pos - body.position).normalized;
                Vector3 f = Vector3.ProjectOnPlane((Vector3)fwd, up);
                if (f.sqrMagnitude < 1e-6f) { go[i].SetActive(false); continue; }

                go[i].transform.position = pos;
                go[i].transform.rotation = Quaternion.LookRotation(f.normalized, up) * Quaternion.Euler(-18f, 0f, 0f);
                go[i].SetActive(true);

                // kolor: bursztyn przy manewrze, cyjan na prostej; wygaszanie na koncach
                bool atTurn = step != null && step.Turn != Maneuver.Arrive && s > step.AtM - 45.0 && s < step.AtM + 25.0;
                Color c = atTurn ? NavHud.Amber : NavHud.Cyan;
                float fadeIn = Mathf.Clamp01((float)(s - Navigation.AlongM - Lead) / 10f + 0.2f);
                float fadeOut = 1f - Mathf.Clamp01((i * Spacing + flow - (Count - 4) * Spacing) / (3f * Spacing));
                float glow = 0.75f + 0.25f * Mathf.Sin(t * 5f - i * 0.8f);
                c *= glow * fadeIn * fadeOut;
                c.a = glow * fadeIn * fadeOut;
                mpb.SetColor("_TintColor", c * 0.5f);
                mpb.SetColor("_Color", c);
                mr[i].SetPropertyBlock(mpb);
            }
        }

        /// <summary>Wysokosc drogi/terenu: promien w dol na warstwe scenerii (tam sa kolidery
        /// drog i PQS), a gdy nic nie trafi - wysokosc PQS.</summary>
        private double GroundAlt(CelestialBody body, double lat, double lon)
        {
            double pqs = body.TerrainAltitude(lat, lon, true);
            Vector3d top = body.GetWorldSurfacePosition(lat, lon, pqs + 60.0);
            Vector3 down = -(Vector3)(top - body.position).normalized;
            int n = Physics.RaycastNonAlloc((Vector3)top, down, hits, 160f, 1 << 15, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++) if (hits[i].distance < best) best = hits[i].distance;
            return best < float.MaxValue ? pqs + 60.0 - best : pqs;
        }

        private static Material MakeMaterial()
        {
            string[] names = { "KSP/Particles/Additive", "Legacy Shaders/Particles/Additive", "Particles/Additive", "Sprites/Default" };
            Shader sh = null;
            foreach (string n in names) { sh = Shader.Find(n); if (sh != null) break; }
            var m = new Material(sh) { renderQueue = 3100 };
            var white = new Texture2D(1, 1);
            white.SetPixel(0, 0, Color.white); white.Apply();
            m.mainTexture = white;
            return m;
        }

        /// <summary>Szewron "^" lezacy w plaszczyznie XZ, grotem w +Z, z miekka poswiata dookola.</summary>
        private static Mesh Chevron()
        {
            var v = new List<Vector3>();
            var c = new List<Color>();
            var tr = new List<int>();

            Action<float, float, float, float> layer = (scale, width, aIn, aOut) =>
            {
                // punkty grotu i ramion (w metrach)
                Vector2 tip = new Vector2(0f, 1.0f) * scale, tipIn = new Vector2(0f, 1.0f - width) * scale;
                Vector2 l = new Vector2(-1.5f, -0.45f) * scale, lIn = new Vector2(-1.5f, -0.45f - width) * scale;
                Vector2 rr = new Vector2(1.5f, -0.45f) * scale, rIn = new Vector2(1.5f, -0.45f - width) * scale;
                foreach (Vector2[] q in new[] { new[] { tip, l, lIn, tipIn }, new[] { tip, tipIn, rIn, rr } })
                {
                    int b = v.Count;
                    for (int k = 0; k < 4; k++)
                    {
                        v.Add(new Vector3(q[k].x, 0f, q[k].y));
                        bool outer = q[k] == tip || q[k] == l || q[k] == rr;
                        c.Add(new Color(1f, 1f, 1f, outer ? aOut : aIn));
                    }
                    tr.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }
            };
            layer(1.25f, 0.95f, 0.10f, 0.22f);     // poswiata
            layer(1.0f, 0.55f, 0.75f, 1.0f);       // rdzen

            var m = new Mesh { name = "OSMRoads.NavChevron" };
            m.SetVertices(v);
            m.SetColors(c);
            m.SetTriangles(tr, 0);
            m.RecalculateBounds();
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 6f);
            return m;
        }
    }
}
