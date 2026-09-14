using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Menu i warstwa OSM w widoku mapy (M).
    ///
    /// Uklad warstw jest celowy: satelita Mirage'a maluje kule, my kladziemy
    /// nad nia plat z drogami i budynkami, chmury EVE zostaja jeszcze wyzej.
    /// Przelacznik "OSM" wlacza i wylacza sam plat - satelita zostaje.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class MapOverlayAddon : MonoBehaviour
    {
        private readonly ScaledOverlay overlay = new ScaledOverlay();
        private MapData job;

        private bool osmOn = true;
        private Rect win = new Rect(20, 60, 260, 0);
        private string status = "";

        /// <summary>Rozmiar wypalanej tekstury i gestosc platu. 2048 na obszar
        /// rzedu stopnia daje ok. 50 m na teksel - drogi sa czytelne, a pamiec
        /// zostaje w rozsadku.</summary>
        private int texSize = 2048;
        private int subdiv = 48;

        /// <summary>Polowa boku obszaru w stopniach. Wiecej = wiekszy zasieg,
        /// ale ta sama tekstura rozciaga sie na wiekszy teren.</summary>
        private float halfSpanDeg = 0.55f;

        private double builtLat = double.NaN, builtLon;

        private bool showWindow = true;
        private AppButton appButton;

        // --- nazwy na kuli ---
        private struct GlobeCand { public double Lat, Lon; public string Name; public int Rank; }
        private readonly MapLabels globeLabels = new MapLabels();
        private readonly List<GlobeCand> cands = new List<GlobeCand>();
        private List<OsmPlace> areaPlaces;          // wsie i miasteczka z wypalonego obszaru
        private float candTime = -10f;
        private const int MaxCands = 700;

        public void Start()
        {
            appButton = new AppButton(AppIcons.Globe, () => { showWindow = true; }, () => { showWindow = false; },
                                      KSP.UI.Screens.ApplicationLauncher.AppScenes.MAPVIEW);
            // Okno warstwy jest domyslnie widoczne w widoku mapy - przycisk startuje wlaczony.
            appButton.Sync(true);
        }

        public void OnDestroy()
        {
            if (appButton != null) appButton.Destroy();
            overlay.Destroy();
        }

        public void Update()
        {
            bool inMap = KSP_MapEnabled();
            overlay.SetVisible(osmOn && inMap && overlay.HasContent);

            if (!inMap || !osmOn) return;

            if (job != null && job.Done)
            {
                if (job.Error == null)
                {
                    overlay.Paint(job.Ways);
                    areaPlaces = job.Places;
                    candTime = -10f;
                    status = job.Ways.Count + " obiektow";
                }
                else
                {
                    status = "blad odczytu - patrz KSP.log";
                    Debug.LogError("[OSMRoads] nakladka: " + job.Error);
                }
                job = null;
            }

            MaybeRebuild();
        }

        /// <summary>Czy gracz jest w widoku mapy. Nazwa z prefiksem, bo w naszej
        /// przestrzeni nazw MapView to nasza wlasna klasa rzutowania.</summary>
        private static bool KSP_MapEnabled()
        {
            return global::MapView.MapIsEnabled;
        }

        private void MaybeRebuild()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody == null) return;
            if (!LocalStore.Available) { status = "brak lokalnego zbioru"; return; }
            if (job != null) return;

            // Przebudowa dopiero, gdy statek wyjedzie poza polowe obszaru -
            // inaczej wypalalibysmy teksture bez przerwy.
            if (!double.IsNaN(builtLat)
                && System.Math.Abs(v.latitude - builtLat) < halfSpanDeg * 0.5
                && System.Math.Abs(v.longitude - builtLon) < halfSpanDeg * 0.5)
                return;

            BBox box;
            box.South = Geo.Clamp(v.latitude - halfSpanDeg, -89.9, 89.9);
            box.North = Geo.Clamp(v.latitude + halfSpanDeg, -89.9, 89.9);
            box.West = v.longitude - halfSpanDeg;
            box.East = v.longitude + halfSpanDeg;

            overlay.Build(v.mainBody, box, texSize, subdiv);
            builtLat = v.latitude;
            builtLon = v.longitude;

            // Budynkow NIE czytamy. Plat ma ok. 60 m na teksel, a RenderOverlay
            // rysuje budynki dopiero ponizej 14 m/px - wiec i tak nie trafilyby
            // na teksture, a przy obszarze ponad stu kilometrow to one stanowia
            // wiekszosc odczytu z dysku i wiekszosc zajetej pamieci.
            job = new MapData(box, false) { WantLabels = true, BodyRadius = v.mainBody.Radius };
            job.Start();
            status = "wypalam ...";
        }

        public void OnGUI()
        {
            if (!KSP_MapEnabled()) return;
            if (osmOn && OsmSettings.GlobeLabels && Event.current.type == EventType.Repaint) DrawGlobeLabels();
            if (!showWindow) return;
            GUI.skin = UiSkin.Get();
            win = GUILayout.Window(GetInstanceID(), win, Draw, "OSM");
        }

        /// <summary>
        /// Nazwy miast przyklejone do kuli. Kandydaci (miejsca po tej stronie planety,
        /// ktora widzi kamera) liczeni co pol sekundy; w klatce tylko rzut na ekran,
        /// test horyzontu i rozmieszczenie bez nakladania.
        ///
        /// Prog skali jest ten sam co w planerze: z odleglosci kamery do punktu
        /// i kata widzenia wychodzi "ile metrow na piksel" w tym miejscu ekranu.
        /// </summary>
        private void DrawGlobeLabels()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            Camera cam = PlanetariumCamera.Camera;
            if (v == null || v.mainBody == null || cam == null) return;
            CelestialBody body = v.mainBody;

            Vector3d camWorld = ScaledSpace.ScaledToLocalSpace(cam.transform.position);
            double pixelAngle = 2.0 * Math.Tan(cam.fieldOfView * 0.5 * Math.PI / 180.0) / Math.Max(1, Screen.height);

            if (Time.unscaledTime - candTime > 0.5f) RebuildCands(body, camWorld, pixelAngle);

            globeLabels.Clear();
            foreach (GlobeCand c in cands)
            {
                Vector3d world = body.GetWorldSurfacePosition(c.Lat, c.Lon, 0.0);
                Vector3d toCam = camWorld - world;
                if (Vector3d.Dot(world - body.position, toCam) <= 0.0) continue;        // za horyzontem
                double mpp = toCam.magnitude * pixelAngle;
                if (mpp > OsmSettings.PlaceMaxMpp(c.Rank)) continue;

                Vector3 sp = cam.WorldToScreenPoint(ScaledSpace.LocalToScaledSpace(world));
                if (sp.z <= 0f) continue;
                globeLabels.AddScreen(new Vector2(sp.x, Screen.height - sp.y), c.Name, c.Rank, Screen.width, Screen.height);
            }
            globeLabels.Draw();
        }

        private void RebuildCands(CelestialBody body, Vector3d camWorld, double pixelAngle)
        {
            candTime = Time.unscaledTime;
            cands.Clear();

            Vector3d rel = camWorld - body.position;
            double dist = rel.magnitude;
            double alt = dist - body.Radius;
            if (alt <= 0) return;
            double camLat = body.GetLatitude(camWorld), camLon = body.GetLongitude(camWorld);
            double horizon = Math.Acos(Math.Min(1.0, body.Radius / dist));        // katowy zasieg widocznej czapy
            double nearMpp = alt * pixelAngle;                                     // najblizszy punkt kuli

            var list = new List<GlobeCand>();
            WorldPlaces.Place[] world = WorldPlacesStore.Get();
            if (world != null)
            {
                foreach (WorldPlaces.Place p in world)
                {
                    if (nearMpp > OsmSettings.PlaceMaxMpp(p.Rank)) continue;
                    if (Angle(camLat, camLon, p.Lat, p.Lon) > horizon) continue;
                    list.Add(new GlobeCand { Lat = p.Lat, Lon = p.Lon, Name = p.Name, Rank = p.Rank });
                }
            }
            if (areaPlaces != null)
                foreach (OsmPlace p in areaPlaces)
                {
                    int rank = p.Rank;
                    if (rank <= 1 || nearMpp > OsmSettings.PlaceMaxMpp(rank)) continue;     // miasta sa juz z indeksu
                    if (Angle(camLat, camLon, p.Lat, p.Lon) > horizon) continue;
                    list.Add(new GlobeCand { Lat = p.Lat, Lon = p.Lon, Name = p.Name, Rank = rank });
                }

            // najwazniejsze i najblizsze srodka widoku pierwsze - one wygrywaja miejsce na ekranie
            list.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank)
                                                 : Angle(camLat, camLon, a.Lat, a.Lon).CompareTo(Angle(camLat, camLon, b.Lat, b.Lon)));
            for (int i = 0; i < list.Count && i < MaxCands; i++) cands.Add(list[i]);
        }

        /// <summary>Kat miedzy dwoma punktami na kuli, w radianach.</summary>
        private static double Angle(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * Math.PI / 180.0, p2 = lat2 * Math.PI / 180.0;
            double dl = (lon2 - lon1) * Math.PI / 180.0;
            double c = Math.Sin(p1) * Math.Sin(p2) + Math.Cos(p1) * Math.Cos(p2) * Math.Cos(dl);
            return Math.Acos(Math.Max(-1.0, Math.Min(1.0, c)));
        }

        private void Draw(int id)
        {
            GUILayout.BeginVertical();

            bool was = osmOn;
            osmOn = GUILayout.Toggle(osmOn, " warstwa OSM na kuli");
            if (osmOn != was && osmOn) builtLat = double.NaN;   // wymus odbudowe

            GUILayout.Label(status);
            GUILayout.Label(overlay.Report);
            GUILayout.Label(overlay.Valid
                ? (overlay.HasContent ? "plat: zbudowany, z trescia" : "plat: zbudowany, PUSTY")
                : "plat: NIE zbudowany");

            GUILayout.Label(string.Format("Wyniesienie nad kule: {0:F5}", ScaledOverlay.Lift));
            float lf = GUILayout.HorizontalSlider((ScaledOverlay.Lift - 1f) * 10000f, 0.1f, 30f);
            float newLift = 1f + lf / 10000f;
            if (Mathf.Abs(newLift - ScaledOverlay.Lift) > 1e-6f)
            {
                ScaledOverlay.Lift = newLift;
                builtLat = double.NaN;
            }

            GUILayout.Label(string.Format("Zasieg: {0:F2} st", halfSpanDeg * 2f));
            float hs = GUILayout.HorizontalSlider(halfSpanDeg, 0.15f, 2.5f);
            if (Mathf.Abs(hs - halfSpanDeg) > 0.01f)
            {
                halfSpanDeg = hs;
                builtLat = double.NaN;
            }

            if (GUILayout.Button("Przebuduj tutaj")) builtLat = double.NaN;

            GUILayout.Space(4);
            GUILayout.Label("Planer: " + OsmSettings.Describe(OsmSettings.PlannerKey, OsmSettings.PlannerModifier));
            GUILayout.Label("Dane (c) OpenStreetMap, ODbL");

            GUILayout.EndVertical();
            GUI.DragWindow();
        }
    }
}
