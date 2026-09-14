using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Planer lotow: prawdziwa mapa w oknie KSP, z przewijaniem, zoomem
    /// i punktami trasy wpinanymi w stockowy system waypointow.
    ///
    /// Osobny addon od OsmRoadsAddon, bo to inne zadanie: czyta te same dane
    /// z LocalStore, ale nie zalezy od streamingu kafli ani od LOD-u - mapa
    /// pokazuje wszystko, co jest na dysku, takze setki kilometrow od statku.
    ///
    /// Dwa zakresy skali:
    ///   - do 150 m/px kafle OSM (MapTileCache),
    ///   - dalej widok swiata: tekstura planety z scaled space i siatka
    ///     poludnikow - tu mapa sluzy do orientacji i sledzenia toru lotu.
    ///
    /// Stan zmieniajacy sie w czasie (pozycja statku, slady, podazanie) jest
    /// odswiezany co OsmSettings.RefreshSec, a nie co klatke.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class MapPlannerAddon : MonoBehaviour
    {
        private const string LockId = "OSMRoadsPlanner";
        private const int MapMargin = 8;
        private const int SidePanel = 260;
        private const int MaxPastPoints = 6000;

        private bool show;
        private Rect win = new Rect(80, 60, 940, 660);

        private readonly MapView view = new MapView();
        private readonly List<MapWaypoint> waypoints = new List<MapWaypoint>();
        private readonly MapTileCache tiles = new MapTileCache();

        private RenderTexture rt;      // warstwa znacznikow nad kaflami
        private bool marksDirty = true;
        private double mLat, mLon; private float mMpp; private int mW, mH;

        private readonly List<GeoPoint> future = new List<GeoPoint>();
        private readonly List<GeoPoint> past = new List<GeoPoint>();
        private Guid trackedVessel;
        private float lastTick = -100f;

        private Texture worldTex;
        private CelestialBody worldTexBody;

        private string status = "";
        private float lastDrawMs;
        private Vector2 lastMouse;
        private bool dragging;
        private float dragDistance;
        private Vector2 listScroll, siteScroll, optScroll;
        private int tab;
        private bool drawSiteMarkers = true;
        private int capturing;          // 0 nic, 1 skrot planera, 2 skrot okna drog
        private bool lockSet;
        private AppButton appButton;

        // --- etykiety: przeliczane przy zmianie widoku albo nowym kaflu, rysowane co klatke ---
        private readonly MapLabels labels = new MapLabels();
        private double lLat = double.NaN, lLon; private float lMpp; private int lW, lH, lRev = -1;
        private float lTime;
        private bool labelsDirty = true;
        private Vector2 mapScroll;

        private static readonly string[] Tabs = { "Trasa", "Miejsca", "Mapa", "Opcje" };
        private static readonly string[] StyleNames = { "ciemna", "teren", "jasna (OSM)" };

        public void Start()
        {
            OsmSettings.EnsureLoaded();
            CenterOnVessel();
            appButton = new AppButton(AppIcons.Planner,
                () => { show = true; CenterOnVessel(); marksDirty = true; lastTick = -100f; },
                () => { show = false; },
                KSP.UI.Screens.ApplicationLauncher.AppScenes.FLIGHT | KSP.UI.Screens.ApplicationLauncher.AppScenes.MAPVIEW);
        }

        public void OnDestroy()
        {
            SetLock(false);
            if (appButton != null) appButton.Destroy();
            if (rt != null) { rt.Release(); Destroy(rt); rt = null; }
            tiles.Clear();
        }

        public void Update()
        {
            if (OsmSettings.Pressed(OsmSettings.PlannerKey, OsmSettings.PlannerModifier))
            {
                show = !show;
                if (show) { CenterOnVessel(); marksDirty = true; lastTick = -100f; }
                if (appButton != null) appButton.Sync(show);
            }

            // Kolko myszy nad oknem ma zoomowac mape, nie kamere KSP.
            Vector2 mp = Input.mousePosition;
            mp.y = Screen.height - mp.y;
            SetLock(show && win.Contains(mp));

            if (show && Time.unscaledTime - lastTick >= OsmSettings.RefreshSec)
            {
                lastTick = Time.unscaledTime;
                Tick();
            }
        }

        private void SetLock(bool on)
        {
            if (on == lockSet) return;
            lockSet = on;
            if (on) InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, LockId);
            else InputLockManager.RemoveControlLock(LockId);
        }

        private void CenterOnVessel()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody == null) return;
            view.CenterLat = v.latitude;
            view.CenterLon = v.longitude;
            view.BodyRadius = v.mainBody.Radius;
        }

        // ------------------------------------------------------------------
        //  Odswiezanie co RefreshSec: slady, podazanie
        // ------------------------------------------------------------------

        private void Tick()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody == null) return;

            if (v.id != trackedVessel)
            {
                trackedVessel = v.id;
                past.Clear();
                future.Clear();
            }

            TrackMode tm = OsmSettings.Track;
            if (tm == TrackMode.Past || tm == TrackMode.Both) RecordPast(v);
            if (tm == TrackMode.Orbit || tm == TrackMode.Both) ComputeFuture(v);
            else future.Clear();

            if (OsmSettings.FollowVessel)
            {
                view.CenterLat = v.latitude;
                view.CenterLon = Wrap(v.longitude);
            }

            marksDirty = true;
        }

        private void RecordPast(Vessel v)
        {
            if (past.Count > 0)
            {
                GeoPoint l = past[past.Count - 1];
                double d = LaunchSites.GreatCircle(l.Lat, l.Lon, v.latitude, v.longitude, v.mainBody.Radius);
                if (d < 25.0) return;
            }
            past.Add(new GeoPoint(v.latitude, Wrap(v.longitude)));
            if (past.Count > MaxPastPoints) past.RemoveRange(0, MaxPastPoints / 4);
        }

        /// <summary>
        /// Przyszly tor po powierzchni: pozycje z orbity w kolejnych chwilach,
        /// z poprawka na obrot planety (dlugosc liczona wzgledem planety w TAMTEJ
        /// chwili, nie teraz). Dla lotu suborbitalnego to trajektoria balistyczna
        /// konczaca sie w punkcie uderzenia - bez oporu powietrza.
        /// </summary>
        private void ComputeFuture(Vessel v)
        {
            future.Clear();
            if (v.LandedOrSplashed) return;

            Orbit o = v.orbit;
            CelestialBody b = v.mainBody;
            if (o == null || b == null) return;

            double now = Planetarium.GetUniversalTime();
            double span = o.eccentricity < 1.0 && o.period > 0 && !double.IsNaN(o.period)
                ? Math.Min(o.period * 1.25, 12 * 3600.0)
                : 3 * 3600.0;
            if (o.patchEndTransition != Orbit.PatchTransitionType.FINAL && o.EndUT > now)
                span = Math.Min(span, o.EndUT - now);

            const int steps = 360;
            for (int i = 0; i <= steps; i++)
            {
                double ut = now + span * i / steps;
                Vector3d pos = o.getPositionAtUT(ut);
                double alt = (pos - b.position).magnitude - b.Radius;

                double lat = b.GetLatitude(pos);
                double lon = b.GetLongitude(pos);
                if (b.rotates && b.rotationPeriod > 0)
                    lon -= (ut - now) * 360.0 / b.rotationPeriod;
                future.Add(new GeoPoint(lat, Wrap(lon)));

                if (i > 0 && alt < 0) break;      // uderzenie w poziom morza
            }
        }

        private static double Wrap(double lon)
        {
            lon %= 360.0;
            if (lon > 180.0) lon -= 360.0;
            else if (lon < -180.0) lon += 360.0;
            return lon;
        }

        // ------------------------------------------------------------------
        //  UI
        // ------------------------------------------------------------------

        public void OnGUI()
        {
            if (!show) return;
            GUI.skin = UiSkin.Get();
            win = GUILayout.Window(GetInstanceID(), win, Draw, "OSM Planer lotow");
        }

        private bool WorldMode { get { return view.MetersPerPixel > MapTileCache.MaxTileViewMpp; } }

        /// <summary>Szerokosc odniesienia: wspolna z kaflami, a w widoku swiata
        /// podazajaca za srodkiem (nie ma kafli, wiec nie ma czego zgrywac).</summary>
        private void SyncRef()
        {
            view.RefLat = WorldMode ? double.NaN : tiles.RefLat;
        }

        private void Draw(int id)
        {
            HandleCapture();

            Vessel v = FlightGlobals.ActiveVessel;
            if (v != null && v.mainBody != null) view.BodyRadius = v.mainBody.Radius;

            GUILayout.BeginHorizontal();

            int mapW = (int)win.width - SidePanel - MapMargin * 3;
            int mapH = (int)win.height - 70;
            Rect mapRect = GUILayoutUtility.GetRect(mapW, mapH);

            HandleMapInput(mapRect);

            if (Event.current.type == EventType.Repaint)
                DrawMap(mapRect, v);

            GUILayout.BeginVertical(GUILayout.Width(SidePanel));
            DrawSidePanel(v);
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            GUILayout.Label(string.Format("{0}   |   {1:N1} m/px ({2})   |   {3:F4}, {4:F4}   |   klatka mapy {5:F1} ms",
                                          status, view.MetersPerPixel,
                                          WorldMode ? "swiat" : "poziom " + MapTileCache.LevelFor(view.MetersPerPixel),
                                          view.CenterLat, view.CenterLon, lastDrawMs));

            GUI.DragWindow(new Rect(0, 0, win.width, 22));
        }

        private void DrawMap(Rect mapRect, Vessel v)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            EnsureTexture(mapRect);

            view.Width = (int)mapRect.width;
            view.Height = (int)mapRect.height;
            view.CenterLat = Geo.Clamp(view.CenterLat, -85.0, 85.0);
            view.CenterLon = Wrap(view.CenterLon);
            if (!WorldMode) tiles.EnsureRef(view.CenterLat);
            SyncRef();

            GUI.BeginGroup(mapRect);
            {
                Color prev = GUI.color;
                GUI.color = WorldMode ? new Color(0.05f, 0.09f, 0.15f) : MapRenderer.MapBackground;
                GUI.DrawTexture(new Rect(0, 0, mapRect.width, mapRect.height), Texture2D.whiteTexture);
                GUI.color = prev;

                if (WorldMode) DrawWorld(v != null ? v.mainBody : null, mapRect.width, mapRect.height);
                else
                {
                    SyncSatellite(v);
                    tiles.Draw(view, mapRect.width, mapRect.height);
                }

                if (OsmSettings.Labels) DrawLabels(mapRect.width, mapRect.height);

                // Znaczniki przerysowujemy przy zmianie widoku albo co RefreshSec.
                bool moved = mLat != view.CenterLat || mLon != view.CenterLon || mMpp != view.MetersPerPixel ||
                             mW != view.Width || mH != view.Height;
                if (rt != null && (marksDirty || moved))
                {
                    TrackMode tm = OsmSettings.Track;
                    MapRenderer.RenderMarks(rt, view, waypoints,
                                            v != null ? v.latitude : 0.0,
                                            v != null ? v.longitude : 0.0,
                                            Heading(v), drawSiteMarkers,
                                            tm == TrackMode.Orbit || tm == TrackMode.Both ? future : null,
                                            tm == TrackMode.Past || tm == TrackMode.Both ? past : null,
                                            WorldMode ? GraticuleStep(view.MetersPerPixel) : 0f);
                    marksDirty = false;
                    mLat = view.CenterLat; mLon = view.CenterLon; mMpp = view.MetersPerPixel;
                    mW = view.Width; mH = view.Height;
                }
                if (rt != null) GUI.DrawTexture(new Rect(0, 0, mapRect.width, mapRect.height), rt);
            }
            GUI.EndGroup();

            lastDrawMs = (float)sw.Elapsed.TotalMilliseconds;
            if (!WorldMode) status = string.Format("kafle {0}/{1}, w tle {2}", tiles.Ready, tiles.Visible, tiles.Jobs);
            else status = worldTex != null ? "widok swiata" : "widok swiata (bez tekstury planety)";
        }

        /// <summary>Zrodlo zdjec dla ciala, nad ktorym jest statek. Zmiana (wlaczenie,
        /// inna planeta) = kafle od nowa, bo zdjecie jest wypalone w obrazie kafla.</summary>
        private void SyncSatellite(Vessel v)
        {
            SatelliteSource want = null;
            if (OsmSettings.Satellite && v != null && v.mainBody != null)
            {
                want = SatelliteSource.For(v.mainBody.bodyName);
                if (want != null && !want.Available) want = null;
            }
            if (want != tiles.Satellite)
            {
                tiles.Satellite = want;
                tiles.Clear();
            }
        }

        private void DrawLabels(float w, float h)
        {
            bool moved = lLat != view.CenterLat || lLon != view.CenterLon || lMpp != view.MetersPerPixel ||
                         lW != view.Width || lH != view.Height;
            bool worldPending = WorldPlacesStore.Get() == null;
            if (moved || labelsDirty || lRev != tiles.Revision || (worldPending && Time.unscaledTime - lTime > 1f))
            {
                labels.Clear();
                WorldPlaces.Place[] world = WorldPlacesStore.Get();
                if (OsmSettings.CountryBorders) labels.AddCountries(view, w, h);
                if (!WorldMode) tiles.AddLabels(labels, view, w, h);
                // Miasta z indeksu swiata: w widoku swiata jedyne zrodlo, na kaflach
                // dopelniaja te, ktorych kafle jeszcze sie nie wczytaly.
                labels.AddWorldPlaces(view, world, 0, w, h);
                labels.AddWorldPlaces(view, world, 1, w, h);

                lLat = view.CenterLat; lLon = view.CenterLon; lMpp = view.MetersPerPixel;
                lW = view.Width; lH = view.Height; lRev = tiles.Revision;
                lTime = Time.unscaledTime;
                labelsDirty = false;
            }
            labels.Draw();
        }

        private static float GraticuleStep(float mpp)
        {
            if (mpp > 20000f) return 30f;
            if (mpp > 3000f) return 10f;
            if (mpp > 800f) return 5f;
            return 1f;
        }

        /// <summary>Tekstura planety ze scaled space rozciagnieta rownoprostokatnie.
        /// Przy Mirage material moze nie miec zwyklej tekstury - wtedy zostaje tlo i siatka.</summary>
        private void DrawWorld(CelestialBody body, float w, float h)
        {
            Texture tex = WorldTexture(body);
            if (tex == null) return;

            double degLat = view.MetersPerDegLat, degLon = view.MetersPerDegLon;
            if (degLon < 1e-6) degLon = 1e-6;
            double mpp = view.MetersPerPixel;
            double halfLon = w * 0.5 * mpp / degLon, halfLat = h * 0.5 * mpp / degLat;

            double south = Math.Max(-90.0, view.CenterLat - halfLat);
            double north = Math.Min(90.0, view.CenterLat + halfLat);
            float yTop = (float)(h * 0.5 - (north - view.CenterLat) * degLat / mpp);
            float yBot = (float)(h * 0.5 - (south - view.CenterLat) * degLat / mpp);
            float v0 = (float)((south + 90.0) / 180.0), v1 = (float)((north + 90.0) / 180.0);

            double off = OsmSettings.WorldTexLonOffset;
            double u0 = (view.CenterLon - halfLon + off) / 360.0;
            double u1 = (view.CenterLon + halfLon + off) / 360.0;

            // Pas po pasie, zeby tekstura powtorzyla sie przez poludnik 180 st.
            // niezaleznie od trybu zawijania, jaki ustawil planet pack.
            for (double k = Math.Floor(u0); k < u1; k += 1.0)
            {
                double a = Math.Max(u0, k), b = Math.Min(u1, k + 1.0);
                float x0 = (float)((a - u0) / (u1 - u0) * w);
                float x1 = (float)((b - u0) / (u1 - u0) * w);
                GUI.DrawTextureWithTexCoords(new Rect(x0, yTop, x1 - x0, yBot - yTop), tex,
                                             new Rect((float)(a - k), v0, (float)(b - a), v1 - v0));
            }
        }

        private Texture WorldTexture(CelestialBody body)
        {
            if (body == worldTexBody) return worldTex;
            worldTexBody = body;
            worldTex = null;
            if (body == null || body.scaledBody == null) return null;

            try
            {
                var mr = body.scaledBody.GetComponent<MeshRenderer>();
                Material m = mr != null ? mr.sharedMaterial : null;
                if (m == null) return null;
                foreach (string prop in new[] { "_MainTex", "_MainTexture", "_BaseMap" })
                {
                    if (!m.HasProperty(prop)) continue;
                    Texture t = m.GetTexture(prop);
                    if (t != null) { worldTex = t; break; }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[OSMRoads] tekstura planety: " + e.Message);
            }
            return worldTex;
        }

        private void EnsureTexture(Rect r)
        {
            int w = Mathf.Max(64, (int)r.width);
            int h = Mathf.Max(64, (int)r.height);
            if (rt != null && rt.width == w && rt.height == h) return;

            if (rt != null) { rt.Release(); Destroy(rt); }
            rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Bilinear };
            rt.Create();
            marksDirty = true;
        }

        private static float Heading(Vessel v)
        {
            if (v == null) return 0f;
            Vector3d up = v.upAxis;
            Vector3d north = Vector3d.Exclude(up,
                (v.mainBody.position + v.mainBody.transform.up * (float)v.mainBody.Radius)
                - v.transform.position).normalized;
            Vector3d fwd = Vector3d.Exclude(up, v.transform.up).normalized;
            double ang = Vector3d.Angle(north, fwd);
            if (Vector3d.Dot(Vector3d.Cross(north, fwd), up) < 0) ang = 360.0 - ang;
            return (float)ang;
        }

        // ------------------------------------------------------------------
        //  Wejscie na mapie
        // ------------------------------------------------------------------

        private void SetZoom(float mpp, Vector2 anchorPx, bool anchored)
        {
            double la0 = 0, lo0 = 0;
            SyncRef();
            if (anchored) view.FromPixel(anchorPx.x, anchorPx.y, out la0, out lo0);

            view.MetersPerPixel = Mathf.Clamp(mpp, 0.5f, MapTileCache.MaxViewMpp);

            if (!WorldMode) tiles.EnsureRef(view.CenterLat);
            SyncRef();
            if (anchored)
            {
                // Punkt pod kursorem zostaje pod kursorem - jak w kazdej aplikacji mapowej.
                // Kilka krokow, bo w widoku swiata skala pozioma zalezy od srodka,
                // a srodek wlasnie przesuwamy: jedna poprawka zostawiala do 87 px bledu.
                for (int it = 0; it < 4; it++)
                {
                    double la1, lo1;
                    view.FromPixel(anchorPx.x, anchorPx.y, out la1, out lo1);
                    view.CenterLat = Geo.Clamp(view.CenterLat + (la0 - la1), -85.0, 85.0);
                    view.CenterLon = view.CenterLon + (lo0 - lo1);
                }
                view.CenterLon = Wrap(view.CenterLon);
            }
            marksDirty = true;
        }

        private void HandleMapInput(Rect mapRect)
        {
            Event e = Event.current;
            if (!mapRect.Contains(e.mousePosition)) return;
            Vector2 local = e.mousePosition - new Vector2(mapRect.x, mapRect.y);

            if (e.type == EventType.ScrollWheel)
            {
                float f = e.delta.y > 0 ? 1.25f : 0.8f;
                // Przy podazaniu srodkiem jest statek - zoom wokol niego, nie kursora.
                SetZoom(view.MetersPerPixel * f, local, !OsmSettings.FollowVessel);
                e.Use();
            }
            else if (e.type == EventType.MouseDown && e.button == 0)
            {
                dragging = true;
                dragDistance = 0f;
                lastMouse = e.mousePosition;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && dragging)
            {
                Vector2 d = e.mousePosition - lastMouse;
                dragDistance += d.magnitude;
                if (dragDistance >= 4f && OsmSettings.FollowVessel)
                {
                    // Przeciagniecie to jasny sygnal "chce patrzec gdzie indziej".
                    OsmSettings.FollowVessel = false;
                    OsmSettings.Save();
                }
                SyncRef();
                view.PanPixels(d.x, d.y);
                lastMouse = e.mousePosition;
                marksDirty = true;
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0 && dragging)
            {
                dragging = false;

                // Klikniecie, nie przeciagniecie: prog kilku pikseli, zeby drobne
                // drgniecie myszy przy przewijaniu nie stawialo punktu.
                if (dragDistance < 4f)
                {
                    SyncRef();
                    double lat, lon;
                    view.FromPixel(local.x, local.y, out lat, out lon);
                    waypoints.Add(new MapWaypoint
                    {
                        Lat = lat, Lon = Wrap(lon),
                        Name = "WP " + (waypoints.Count + 1)
                    });
                    marksDirty = true;
                }
                e.Use();
            }
        }

        /// <summary>Przechwycenie nowego klawisza skrotu. Escape anuluje.</summary>
        private void HandleCapture()
        {
            if (capturing == 0) return;
            Event e = Event.current;
            if (e.type != EventType.KeyDown || e.keyCode == KeyCode.None) return;

            if (e.keyCode != KeyCode.Escape)
            {
                if (capturing == 1) OsmSettings.PlannerKey = e.keyCode;
                else OsmSettings.RoadsKey = e.keyCode;
                OsmSettings.Save();
            }
            capturing = 0;
            OsmSettings.EndCapture();
            e.Use();
        }

        // ------------------------------------------------------------------
        //  Panel boczny
        // ------------------------------------------------------------------

        private void DrawSidePanel(Vessel v)
        {
            GUILayout.Label(LocalStore.Available ? "dane: " + LocalStore.PackCount + " paczek" : "dane: BRAK");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Na statek")) { CenterOnVessel(); marksDirty = true; }
            bool follow = GUILayout.Toggle(OsmSettings.FollowVessel, " podazaj");
            if (follow != OsmSettings.FollowVessel)
            {
                OsmSettings.FollowVessel = follow;
                OsmSettings.Save();
                if (follow) { CenterOnVessel(); lastTick = -100f; }
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            var center = new Vector2(view.Width * 0.5f, view.Height * 0.5f);
            if (GUILayout.Button("+")) SetZoom(view.MetersPerPixel * 0.7f, center, false);
            if (GUILayout.Button("-")) SetZoom(view.MetersPerPixel / 0.7f, center, false);
            if (GUILayout.Button("Ziemia")) SetZoom(MapTileCache.MaxViewMpp * 0.75f, center, false);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            tab = GUILayout.Toolbar(tab, Tabs);
            GUILayout.Space(4);

            if (tab == 0) DrawRouteTab(v);
            else if (tab == 1) DrawSiteList(v);
            else if (tab == 2) DrawMapTab(v);
            else DrawOptions();
        }

        private void DrawMapTab(Vessel v)
        {
            mapScroll = GUILayout.BeginScrollView(mapScroll);
            bool retile = false, relabel = false, changed = false;

            GUILayout.Label("Wyglad mapy:");
            int s = GUILayout.SelectionGrid((int)OsmSettings.Style, StyleNames, 3);
            if (s != (int)OsmSettings.Style) { OsmSettings.Style = (MapStyle)s; retile = true; }

            GUILayout.Space(6);
            bool sat = GUILayout.Toggle(OsmSettings.Satellite, " zdjecia satelitarne");
            if (sat != OsmSettings.Satellite) { OsmSettings.Satellite = sat; retile = true; }
            if (OsmSettings.Satellite)
            {
                SatelliteSource src = v != null && v.mainBody != null ? SatelliteSource.For(v.mainBody.bodyName) : null;
                GUILayout.Label(src == null ? "brak ciala" : (src.Available ? src.Report : "brak zdjec dla " + src.Body));
                if (MapRenderer.SatelliteReport.Length > 0) GUILayout.Label(MapRenderer.SatelliteReport);

                string[] bias = { "-2", "-1", "0", "+1", "+2" };
                GUILayout.Label("Ostrosc (poziom wzgledem skali):");
                int b = GUILayout.SelectionGrid(OsmSettings.SatBias + 2, bias, 5) - 2;
                if (b != OsmSettings.SatBias) { OsmSettings.SatBias = b; retile = true; }

                double faceM = view.BodyRadius * Math.PI * 0.5;
                GUILayout.Label(string.Format("Najostrzejszy poziom: {0} (ok. {1:N0} m/px)",
                                              OsmSettings.SatMaxLevel, faceM / ((1 << OsmSettings.SatMaxLevel) * 256.0)));
                int ml = Mathf.RoundToInt(GUILayout.HorizontalSlider(OsmSettings.SatMaxLevel, 4f, 14f));
                if (ml != OsmSettings.SatMaxLevel) { OsmSettings.SatMaxLevel = ml; retile = true; }

                bool areas = GUILayout.Toggle(OsmSettings.SatAreas, " lasy i pola z OSM na zdjeciu");
                if (areas != OsmSettings.SatAreas) { OsmSettings.SatAreas = areas; retile = true; }
                GUILayout.Label("Zdjecia: Sentinel-2 cloudless 2024 by EOX (przez Mirage), Sol-Textures");
            }

            GUILayout.Space(6);
            bool lab = GUILayout.Toggle(OsmSettings.Labels, " nazwy miejsc i ulic");
            if (lab != OsmSettings.Labels) { OsmSettings.Labels = lab; relabel = true; }
            if (OsmSettings.Labels)
            {
                GUILayout.Label("Pokazuj do skali (w prawo = widac z dalej):");
                relabel |= LogSlider("miasta", ref OsmSettings.LabelCityMpp, 20f, 60000f);
                relabel |= LogSlider("miasteczka", ref OsmSettings.LabelTownMpp, 5f, 20000f);
                relabel |= LogSlider("wsie i dzielnice", ref OsmSettings.LabelVillageMpp, 2f, 2000f);
                relabel |= LogSlider("przysiolki", ref OsmSettings.LabelHamletMpp, 1f, 500f);
                relabel |= LogSlider("ulice", ref OsmSettings.LabelRoadMpp, 0.5f, 60f);

                GUILayout.Label(string.Format("Wielkosc napisow: {0:F1}x", OsmSettings.LabelScale));
                float sc = Mathf.Round(GUILayout.HorizontalSlider(OsmSettings.LabelScale, 0.6f, 2f) * 10f) / 10f;
                if (Mathf.Abs(sc - OsmSettings.LabelScale) > 0.01f) { OsmSettings.LabelScale = sc; relabel = true; }
                GUILayout.Label(WorldPlacesStore.Report + ", na ekranie " + labels.Count);
            }

            bool globe = GUILayout.Toggle(OsmSettings.GlobeLabels, " nazwy miast na kuli (widok mapy M)");
            if (globe != OsmSettings.GlobeLabels) { OsmSettings.GlobeLabels = globe; changed = true; }

            bool cb = GUILayout.Toggle(OsmSettings.CountryBorders, " granice i nazwy panstw (" + CountryBorders.Report + ")");
            if (cb != OsmSettings.CountryBorders) { OsmSettings.CountryBorders = cb; relabel = true; }

            GUILayout.EndScrollView();

            if (retile) tiles.Clear();
            if (retile || relabel) labelsDirty = true;
            if (retile || relabel || changed) { OsmSettings.Save(); marksDirty = true; }
        }

        /// <summary>Suwak w skali logarytmicznej - progi zoomu rozciagaja sie od metrow do kilometrow na piksel.</summary>
        private static bool LogSlider(string label, ref float value, float min, float max)
        {
            value = Mathf.Clamp(value, min, max);
            GUILayout.Label(string.Format("  {0}: {1}", label,
                            value >= 1000f ? (value / 1000f).ToString("F1") + " km/px" : value.ToString(value < 10f ? "F1" : "F0") + " m/px"));
            float lv = GUILayout.HorizontalSlider(Mathf.Log10(value), Mathf.Log10(min), Mathf.Log10(max));
            float nv = Mathf.Pow(10f, lv);
            if (Mathf.Abs(nv - value) / value < 0.01f) return false;
            value = nv;
            return true;
        }

        private void DrawRouteTab(Vessel v)
        {
            GUILayout.Label(string.Format("Trasa: {0} pkt", waypoints.Count));

            double total = 0;
            for (int i = 0; i + 1 < waypoints.Count; i++)
                total += view.DistanceM(waypoints[i].Lat, waypoints[i].Lon,
                                        waypoints[i + 1].Lat, waypoints[i + 1].Lon);
            if (waypoints.Count > 1)
                GUILayout.Label(string.Format("Dlugosc: {0:N1} km", total / 1000.0));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Do KSP")) PushAllToKsp();
            if (GUILayout.Button("Wyczysc")) { waypoints.Clear(); marksDirty = true; }
            GUILayout.EndHorizontal();

            listScroll = GUILayout.BeginScrollView(listScroll);

            for (int i = 0; i < waypoints.Count; i++)
            {
                MapWaypoint w = waypoints[i];
                GUILayout.BeginVertical(GUI.skin.box);

                GUILayout.Label(string.Format("{0}{1}", w.Name, w.PushedToKsp ? "  [w KSP]" : ""));
                GUILayout.Label(string.Format("{0:F4}, {1:F4}", w.Lat, w.Lon));

                if (v != null)
                {
                    double d = view.DistanceM(v.latitude, v.longitude, w.Lat, w.Lon);
                    double b = MapView.BearingDeg(v.latitude, v.longitude, w.Lat, w.Lon);
                    GUILayout.Label(string.Format("od statku: {0:N1} km, kurs {1:F0}", d / 1000.0, b));
                }

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Centruj"))
                {
                    view.CenterLat = w.Lat; view.CenterLon = w.Lon; marksDirty = true;
                }
                if (GUILayout.Button("Usun"))
                {
                    waypoints.RemoveAt(i);
                    marksDirty = true;
                    GUILayout.EndHorizontal();
                    GUILayout.EndVertical();
                    break;
                }
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }

            GUILayout.EndScrollView();
        }

        /// <summary>
        /// Lista kosmodromow posortowana wg odleglosci od SRODKA WIDOKU, nie od
        /// statku: planer sluzy do wybierania celu, a nie do raportowania, gdzie
        /// sie stoi. Gdy przewiniesz mape nad Bajkonur, on ma byc pierwszy.
        /// </summary>
        private void DrawSiteList(Vessel v)
        {
            bool markers = GUILayout.Toggle(drawSiteMarkers, " znaczniki na mapie");
            if (markers != drawSiteMarkers) { drawSiteMarkers = markers; marksDirty = true; }

            double radius = view.BodyRadius;
            var order = new List<int>(LaunchSites.All.Length);
            var dist = new double[LaunchSites.All.Length];
            for (int i = 0; i < LaunchSites.All.Length; i++)
            {
                LaunchSite ls = LaunchSites.All[i];
                dist[i] = LaunchSites.GreatCircle(view.CenterLat, view.CenterLon, ls.Lat, ls.Lon, radius);
                order.Add(i);
            }
            order.Sort((a, b) => dist[a].CompareTo(dist[b]));

            siteScroll = GUILayout.BeginScrollView(siteScroll);
            for (int k = 0; k < order.Count; k++)
            {
                int i = order[k];
                LaunchSite ls = LaunchSites.All[i];

                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label(ls.Name);
                GUILayout.Label(string.Format("{0}   {1:N0} km", ls.Operator, dist[i] / 1000.0));

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Centruj"))
                {
                    view.CenterLat = ls.Lat;
                    view.CenterLon = ls.Lon;
                    marksDirty = true;
                }
                if (GUILayout.Button("-> punkt"))
                {
                    waypoints.Add(new MapWaypoint { Lat = ls.Lat, Lon = ls.Lon, Name = ls.Name });
                    marksDirty = true;
                }
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();
        }

        private static readonly string[] TrackNames = { "brak", "orbita / tor lotu", "przebyta droga", "tor + przebyta" };

        private void DrawOptions()
        {
            optScroll = GUILayout.BeginScrollView(optScroll);
            bool changed = false;

            GUILayout.Label("Slad na mapie:");
            int t = GUILayout.SelectionGrid((int)OsmSettings.Track, TrackNames, 2);
            if (t != (int)OsmSettings.Track) { OsmSettings.Track = (TrackMode)t; changed = true; lastTick = -100f; }
            if (GUILayout.Button("Wyczysc przebyta droge")) { past.Clear(); marksDirty = true; }

            GUILayout.Space(6);
            GUILayout.Label(string.Format("Odswiezanie statku i sladow: {0:F1} s", OsmSettings.RefreshSec));
            float r = Mathf.Round(GUILayout.HorizontalSlider(OsmSettings.RefreshSec, 0.1f, 3f) * 10f) / 10f;
            if (Mathf.Abs(r - OsmSettings.RefreshSec) > 0.01f) { OsmSettings.RefreshSec = r; changed = true; }

            GUILayout.Space(6);
            bool modern = GUILayout.Toggle(OsmSettings.ModernUi, " styl Unity (ciemny, jak SmokeScreen)");
            if (modern != OsmSettings.ModernUi) { OsmSettings.ModernUi = modern; changed = true; }

            GUILayout.Space(6);
            GUILayout.Label("Skroty klawiszowe:");
            changed |= KeyRow("Planer", 1, ref OsmSettings.PlannerKey, ref OsmSettings.PlannerModifier);
            changed |= KeyRow("Okno drog", 2, ref OsmSettings.RoadsKey, ref OsmSettings.RoadsModifier);
            GUILayout.Label("Mod = klawisz modyfikatora z ustawien KSP (zwykle Alt).");

            GUILayout.Space(6);
            GUILayout.Label(string.Format("Przesuniecie tekstury planety: {0:F0} st.", OsmSettings.WorldTexLonOffset));
            GUILayout.Label("Ustaw tak, zeby znaczniki kosmodromow trafily w lad.");
            if (GUILayout.Button("Obroc o 90 st."))
            {
                OsmSettings.WorldTexLonOffset = (OsmSettings.WorldTexLonOffset + 90f) % 360f;
                changed = true;
            }

            GUILayout.EndScrollView();
            if (changed) { OsmSettings.Save(); marksDirty = true; }
        }

        private bool KeyRow(string label, int which, ref KeyCode key, ref bool modifier)
        {
            bool changed = false;
            GUILayout.BeginHorizontal();
            GUILayout.Label(string.Format("{0}: {1}", label,
                                          capturing == which ? "wcisnij klawisz..." : OsmSettings.Describe(key, modifier)),
                            GUILayout.Width(150));
            if (GUILayout.Button(capturing == which ? "Esc" : "Zmien"))
            {
                if (capturing == which) { capturing = 0; OsmSettings.EndCapture(); }
                else { capturing = which; OsmSettings.Capturing = true; }
            }
            bool m = GUILayout.Toggle(modifier, " Mod");
            if (m != modifier) { modifier = m; changed = true; }
            GUILayout.EndHorizontal();
            return changed;
        }

        // ------------------------------------------------------------------
        //  Integracja z waypointami KSP
        // ------------------------------------------------------------------

        /// <summary>
        /// Wpina punkty w STOCKOWY system FinePrint. Waypoint Manager, jesli jest
        /// zainstalowany, wyswietla dokladnie te sama liste - wiec integracja
        /// dzieje sie sama, bez zaleznosci od tego moda.
        /// </summary>
        private void PushAllToKsp()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody == null) { status = "brak statku"; return; }

            int n = 0;
            foreach (MapWaypoint w in waypoints)
            {
                if (w.PushedToKsp) continue;
                if (PushOne(v.mainBody, w)) { w.PushedToKsp = true; n++; }
            }

            status = n > 0 ? n + " punktow trafilo do KSP" : "nic nowego do wyslania";
            marksDirty = true;
        }

        private bool PushOne(CelestialBody body, MapWaypoint w)
        {
            try
            {
                var wp = new FinePrint.Waypoint
                {
                    celestialName = body.GetName(),
                    latitude = w.Lat,
                    longitude = w.Lon,
                    altitude = TerrainGrid.RawAltitude(body, w.Lat, w.Lon),
                    name = w.Name,
                    index = 0,
                    id = "report",
                    iconSize = 16,
                    seed = UnityEngine.Random.Range(0, int.MaxValue),
                    isOnSurface = true,
                    isNavigatable = true
                };

                ScenarioCustomWaypoints.AddWaypoint(wp);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[OSMRoads] nie udalo sie dodac waypointa: " + e);
                status = "blad waypointa - patrz KSP.log";
                return false;
            }
        }
    }
}
