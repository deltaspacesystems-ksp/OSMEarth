using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System.Text;

namespace OSMRoads
{
    internal class LoadedTile
    {
        public int ILat, ILon;
        public CelestialBody Body;
        public double Lat, Lon, Alt;      // srodek kafla

        public GameObject Root;
        public GameObject Roads;
        public GameObject Lamps;
        public GameObject BuildFull;
        public GameObject BuildCoarse;
        public GameObject Landuse;
        public TileTrees Trees;
        public LampLights.TileLights Lights;
        public CropScatter.TileCrops Crops;

        public bool Empty;                // pobrany, ale nic w nim nie bylo
        public int Buildings;
        public int Lamps_;                // ile latarni stanelo w tym kaflu
        public bool CollidersOn;          // fizyka wlaczona (kafel blisko statku)

        // przyklejenie drog: siatka terenu (do liczenia wkleslosci) i stan ostatniego przyklejenia
        public TerrainGrid Grid;
        public bool SnapBusy;             // przyklejanie tego kafla wlasnie trwa / czeka w kolejce
        public double SnapCamLat, SnapCamLon, SnapCamAlt, SnapVesLat, SnapVesLon;
            public int SnapEpoch = -1;
        public float SnapK, SnapH;        // ustawienia (kompensacja, wysokosc), z ktorymi przyklejono

        // straznik terenu: punkty na drogach i wysokosc siatki PQS pod nimi w chwili przyklejenia
        public double[] ProbeLat, ProbeLon, ProbeH;
        public bool TerrainDirty;         // PQS przebudowal siatke pod kaflem - przykleic od nowa
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class OsmRoadsAddon : MonoBehaviour
    {
        // KSP LocalScenery - ta sama warstwa co teren i budynki KSC.
        private const int LayerLocalScenery = 15;

        // Odstep miedzy zapytaniami. 0 = bez czekania (ustawienie domyslne na zyczenie).
        // Podnies, jesli Overpass zacznie sypac 429.
        private float minRequestGap;

        private readonly Dictionary<long, LoadedTile> loaded = new Dictionary<long, LoadedTile>();
        private readonly List<long> pending = new List<long>();
        private int centerLat, centerLon;

        // Punkt odniesienia ostatniego skanu i siatka, w ktorej go liczono.
        // Kolejnosc wczytywania i wyrzucanie z pamieci licza odleglosc od tego
        // punktu W METRACH - roznica indeksow klamie, bo rzedy sa przesuniete.
        private double eyeLat, eyeLon;
        private TileGrid lastGrid;
        private bool haveGrid;
        private readonly List<long> around = new List<long>();
        private readonly HashSet<long> queued = new HashSet<long>();
        // Kafel, ktory sie nie zbudowal (blad watku roboczego), NIE wraca do kolejki co skan w kolko -
        // dostaje wygasajacy odstep, zeby jeden trwale zepsuty kafel nie zjadal CPU/pamieci bez konca.
        private readonly Dictionary<long, float> failedUntil = new Dictionary<long, float>();
        private readonly Dictionary<long, int> failCount = new Dictionary<long, int>();

        private Rect win = new Rect(120, 90, 370, 560);
        private Vector2 roadsScroll;
        private string status = "Ready.";
        private bool workerRunning;
        private float lastNetworkTime = -999f;
        private float lastScanTime;

        // --- ustawienia ---
        private bool autoLoad = true;

        /// <summary>Glowny wylacznik moda, zapisywany w .cfg. Wylaczony = zero pracy: bez skanowania,
        /// bez watku roboczego, bez LateUpdate (drzewa/drogi/LOD) - i wszystkie juz zbudowane kafle
        /// znikaja natychmiast (ClearTiles). UI panelu zostaje dostepne, zeby dalo sie wlaczyc z powrotem.</summary>
        private bool modEnabled = true;

        private void SetModEnabled(bool on)
        {
            if (on == modEnabled) return;
            modEnabled = on;
            OsmSettings.Set("enabled", modEnabled ? 1f : 0f);
            OsmSettings.Save();
            if (!modEnabled) ClearTiles();
        }
        private bool useCameraOrigin = true;
        private bool useLocalStore = true;
        private bool wantRoads = true;
        private bool wantBuildings = true;
        private bool wantLanduse = true;

        /// <summary>
        /// Plaskie wypelnienia landuse. Domyslnie WYLACZONE: plachta lezy kilkanascie
        /// cm nad terenem, a teren PQS rysowany w oddali ma uproszczona siatke,
        /// ktora odchyla sie od prawdziwej wysokosci o wiecej - w jednym miejscu
        /// wystaje teren, w drugim plachta, i wychodza postrzepione laty. Przy
        /// zdjeciach satelitarnych Mirage wypelnienia bardziej zaslaniaja, niz pomagaja.
        /// Drzewa sa rozsiewane z tych samych obszarow niezaleznie od tej opcji.
        /// </summary>
        private bool drawLanduseFill = false;
        private AppButton appButton;
        private bool wantLamps = true;
        private bool wantColliders = true;
        private bool wantSnap = true;
        private float treeDensity = 1f;
        private int maxTreesPerTile { get { return OsmSettings.TreeLimit; } }
        private float distTrees = 2400f;
        private float distTreesFar = 9000f;     // LOD drzew: dalej juz tylko bryly koron
        private float tileSizeM = 1500f;
        private int maxRing = 42;              // gorny bezpiecznik na liczbe kafli
        private int maxLoadedTiles = 260;      // budzet kafli w pamieci, jak dysk w Mirage
        // 0.8 m to kompromis: nizej zaczyna sie z-fighting z terenem PQS przy
        // patrzeniu z kilku kilometrow, wyzej droga widocznie odstaje i robi
        // prog dla lazika. Przy wlaczonym przyklejaniu ta wartosc jest juz
        // tylko rownym unosem nad rzeczywistym terenem, a nie zapasem na blad.
        private float roadHeight = 0.3f;

        /// <summary>Mnoznik kompensacji ugiecia siatki terenu (0 = wylaczona). Patrz GroundSnapper.</summary>
        private float sagComp = 1f;
        private float widthScale = 1f;
        private float buildingHeightScale = 1f;
        private int maxBuildingsPerTile = 3000;

        // Rozdzielczosc siatki wysokosci. 64x64 na kafel 1500 m = wezel co ~23 m.
        // Wiecej = wierniejszy teren, ale dluzsze probkowanie na glownym watku.
        private int terrainGridN = 64;
        private int terrainRowsPerFrame = 8;
        private int lastJobMs;

        // --- LOD (metry) ---
        private float distFullBuildings = 4000f;
        private float distCoarseBuildings = 16000f;
        private float distRoads = 14000f;
        private float distUnload = 22000f;

        /// <summary>Latarnie to cienka geometria - z kilometra sa juz tylko szumem
        /// na krawedziach pikseli, wiec gasna duzo wczesniej niz same drogi.</summary>
        private float distLamps = 1600f;

        /// <summary>Zasieg fizyki. Colliderow nie ma sensu trzymac dalej niz
        /// siega sfera fizyki KSP - poza nia i tak nic sie z nimi nie zderzy.</summary>
        private float distColliders = 2300f;

        private float nightFactor;

        private void LoadLod()
        {
            distFullBuildings = OsmSettings.Get("lodFull", distFullBuildings);
            distCoarseBuildings = OsmSettings.Get("lodCoarse", distCoarseBuildings);
            distRoads = OsmSettings.Get("lodRoads", distRoads);
            distUnload = OsmSettings.Get("lodUnload", distUnload);
            distLamps = OsmSettings.Get("lodLamps", distLamps);
            distColliders = OsmSettings.Get("lodPhysics", distColliders);
            distTrees = OsmSettings.Get("lodTrees", distTrees);
            distTreesFar = OsmSettings.Get("lodTreesFar", distTreesFar);
            maxLoadedTiles = (int)OsmSettings.Get("tileBudget", maxLoadedTiles);
            tileSizeM = OsmSettings.Get("tileSize", tileSizeM);
        }

        private float lodSaveAt = -1f;

        /// <summary>Zapis zasiegow po puszczeniu suwaka (sekunde po ostatniej zmianie), nie co klatke.</summary>
        private void SaveLod()
        {
            bool ch = false;
            ch |= OsmSettings.Set("lodFull", distFullBuildings);
            ch |= OsmSettings.Set("lodCoarse", distCoarseBuildings);
            ch |= OsmSettings.Set("lodRoads", distRoads);
            ch |= OsmSettings.Set("lodUnload", distUnload);
            ch |= OsmSettings.Set("lodLamps", distLamps);
            ch |= OsmSettings.Set("lodPhysics", distColliders);
            ch |= OsmSettings.Set("lodTrees", distTrees);
            ch |= OsmSettings.Set("lodTreesFar", distTreesFar);
            ch |= OsmSettings.Set("tileBudget", maxLoadedTiles);
            ch |= OsmSettings.Set("tileSize", tileSizeM);
            ch |= LampLights.SaveSettings();
            ch |= OrthoGround.SaveSettings();
            ch |= CropScatter.SaveSettings();
            if (ch) lodSaveAt = Time.unscaledTime + 1f;
        }

        public void Start()
        {
            OsmSettings.EnsureLoaded();
            LoadLod();
            modEnabled = OsmSettings.Get("enabled", 1f) > 0.5f;
            win = new Rect(win.x, win.y,
                           Mathf.Min(OsmSettings.RoadsW, Screen.width - 20f),
                           Mathf.Min(OsmSettings.RoadsH, Screen.height - 20f));
            // Okno to panel DeltaUI: taby, wyciaganie do osobnych okien, "_" chowa do paska aplikacji.
            DeltaUI.Dock.Register(PanelId, "Roads", DrawPanel, win, false, AppIcons.Roads, 25);
            DeltaUI.Dock.OpenChanged += OnPanelOpenChanged;
            appButton = new AppButton(AppIcons.Roads, () => DeltaUI.Dock.SetOpen(PanelId, true), () => DeltaUI.Dock.SetOpen(PanelId, false),
                KSP.UI.Screens.ApplicationLauncher.AppScenes.FLIGHT | KSP.UI.Screens.ApplicationLauncher.AppScenes.MAPVIEW);
            appButton.Sync(DeltaUI.Dock.IsOpen(PanelId));
            // Statyczna flaga przezywa zmiane sceny; korutyna zabita w trakcie
            // zadania zostawilaby ja zapalona na stale.
            OverpassClient.ResetBusy();
        }

        public void Update()
        {
            if (OsmSettings.Pressed(OsmSettings.RoadsKey, OsmSettings.RoadsModifier))
            {
                DeltaUI.Dock.Toggle(PanelId);
            }

            if (lodSaveAt > 0f && Time.unscaledTime > lodSaveAt) { lodSaveAt = -1f; OsmSettings.Save(); }

            if (!modEnabled) return;               // wylaczony calkowicie - ani skanowania, ani watku roboczego

            if (autoLoad && Time.time - lastScanTime > 2f)
            {
                lastScanTime = Time.time;
                ScanForTiles();
            }

            if (!workerRunning && pending.Count > 0)
                StartCoroutine(Worker());
        }

        // ------------------------------------------------------------------
        //  Streaming
        // ------------------------------------------------------------------

        private TileGrid Grid(CelestialBody b)
        {
            return new TileGrid(tileSizeM, b.Radius);
        }

        /// <summary>
        /// Punkt odniesienia do ladowania i LOD-u. Domyslnie KAMERA, nie statek:
        /// przy odjechanej kamerze albo w trybie zewnetrznym miasto ma sie renderowac
        /// tam, gdzie patrzysz, a nie tam, gdzie stoi rakieta.
        /// </summary>
        private Vector3d ViewPoint()
        {
            if (useCameraOrigin)
            {
                if (FlightCamera.fetch != null && FlightCamera.fetch.transform != null)
                    return FlightCamera.fetch.transform.position;
                if (Camera.main != null)
                    return Camera.main.transform.position;
            }

            Vessel v = FlightGlobals.ActiveVessel;
            return v != null ? (Vector3d)v.transform.position : Vector3d.zero;
        }

        /// <summary>
        /// Promien siatki WYNIKA z zasiegu LOD - nie jest osobnym ustawieniem.
        /// Wczesniej byly to dwie niezalezne liczby i rozjechaly sie po cichu:
        /// LOD obiecywal budynki do 16 km, a ladowala sie siatka 3x3, czyli 4.5 km.
        /// Efekt: wiekszosc kafli nigdy sie nie pojawiala.
        /// </summary>
        private int NeededRing()
        {
            float reach = Mathf.Max(distCoarseBuildings, distRoads);
            return Mathf.Clamp(Mathf.CeilToInt(reach / tileSizeM), 1, maxRing);
        }

        private void ScanForTiles()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody == null || v.mainBody.pqsController == null) return;
            if (!wantRoads && !wantBuildings && !wantLanduse) return;

            CelestialBody body = v.mainBody;
            Vector3d eye = ViewPoint();

            TileGrid g = Grid(body);
            eyeLat = body.GetLatitude(eye);
            eyeLon = body.GetLongitude(eye);
            g.IndexOf(eyeLat, eyeLon, out centerLat, out centerLon);
            lastGrid = g;
            haveGrid = true;

            double radius = NeededRing() * tileSizeM;
            g.TilesAround(eyeLat, eyeLon, radius, around);

            foreach (long key in around)
            {
                if (loaded.ContainsKey(key) || queued.Contains(key)) continue;
                float until;
                if (failedUntil.TryGetValue(key, out until) && Time.unscaledTime < until) continue;
                queued.Add(key);
                pending.Add(key);
            }

            // Kolejka sprzed ruchu kamery: kafle, ktore juz wyszly poza zasieg,
            // wczytalyby sie tylko po to, zeby od razu wypasc przy wyladowaniu.
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (g.DistanceM(pending[i], eyeLat, eyeLon) <= radius * 1.25) continue;
                queued.Remove(pending[i]);
                pending.RemoveAt(i);
            }
        }

        /// <summary>Najblizszy oczekujacy kafel. Kolejka FIFO ladowala w kolejnosci
        /// skanowania, wiec to, na co patrzysz, potrafilo czekac za odleglym rogiem.</summary>
        private bool TakeNearest(out long key)
        {
            key = 0;
            if (pending.Count == 0) return false;

            int best = 0;
            double bestD = double.MaxValue;
            for (int i = 0; i < pending.Count; i++)
            {
                double d = haveGrid ? lastGrid.DistanceM(pending[i], eyeLat, eyeLon) : 0;
                if (d < bestD) { bestD = d; best = i; }
            }

            key = pending[best];
            pending.RemoveAt(best);
            queued.Remove(key);
            return true;
        }

        private IEnumerator Worker()
        {
            workerRunning = true;

            long key;
            while (TakeNearest(out key))
            {

                Vessel v = FlightGlobals.ActiveVessel;
                if (v == null || v.mainBody == null || v.mainBody.pqsController == null) break;

                int iLat = (int)(key >> 32);
                int iLon = (int)(key & 0xFFFFFFFFL);
                yield return StartCoroutine(LoadTile(v.mainBody, iLat, iLon));
            }

            workerRunning = false;
        }

        private IEnumerator LoadTile(CelestialBody body, int iLat, int iLon)
        {
            long key = TileGrid.Key(iLat, iLon);
            if (loaded.ContainsKey(key)) yield break;

            TileGrid g = Grid(body);
            BBox box = g.BoxOf(iLat, iLon);
            double lat, lon;
            g.CenterOf(iLat, iLon, out lat, out lon);

            OsmLayers layers = OsmLayers.None;
            if (wantRoads) layers |= OsmLayers.Roads;
            if (wantBuildings) layers |= OsmLayers.Buildings;
            if (wantLanduse) layers |= OsmLayers.Landuse;

            // --- 1. zrodlo danych ---
            // Lokalny zbior wygrywa z siecia: jest szybszy, dziala offline i nie
            // obciaza publicznego Overpassa. Sam odczyt robi juz watek roboczy.
            // Nie "czy mam paczki", tylko "czy mam paczke NA TO MIEJSCE".
            // Inaczej lot nad regionem spoza zbioru dawalby same puste kafle
            // zamiast siegnac po dane do sieci.
            // Wersja publiczna: tylko Overpass, bez paczek.
            bool local = false;
            string xml = null;

            if (!local)
            {
                // Odstep domyslnie zerowy. Zostaje jako suwak na wypadek, gdyby
                // Overpass zaczal odbijac 429 - czekanie jest tansze niz seria retry.
                if (minRequestGap > 0f)
                {
                    float wait = minRequestGap - (Time.time - lastNetworkTime);
                    if (wait > 0f && !OverpassClient.HasCached(box, layers))
                    {
                        status = string.Format("Request gap: waiting {0:F0}s ...", wait);
                        yield return new WaitForSeconds(wait);
                    }
                }

                string err = null;
                yield return StartCoroutine(OverpassClient.Fetch(box, layers,
                    (x, e) => { xml = x; err = e; },
                    p => { status = p; }));

                if (OverpassClient.LastFetchHitNetwork) lastNetworkTime = Time.time;

                if (xml == null)
                {
                    status = "Error: " + err;
                    yield break;      // nie oznaczamy jako loaded - sprobuje ponownie
                }
            }

            // --- 2. probkowanie terenu: PQS wolno tylko z glownego watku ---
            status = "Sampling terrain ...";
            var terrain = TerrainGrid.Create(box, terrainGridN, 0.30);
            yield return StartCoroutine(terrain.Sample(body, terrainRowsPerFrame));

            double alt0 = TerrainGrid.RawAltitude(body, lat, lon);

            // --- 3. parsowanie i geometria na watku roboczym ---
            status = "Building geometry in the background ...";
            var job = new TileJob(xml, box, local, terrain, body.Radius, lat, lon, alt0,
                                  wantRoads, wantBuildings, wantLanduse,
                                  wantLamps && wantRoads,
                                  treeDensity, maxTreesPerTile,
                                  roadHeight, widthScale, buildingHeightScale,
                                  maxBuildingsPerTile);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            job.BuildLanduseMesh = drawLanduseFill;
            job.Start();
            while (!job.Done) yield return null;
            sw.Stop();

            if (job.Error != null)
            {
                status = "Worker thread failed - see KSP.log";
                Debug.LogError("[OSMRoads] TileJob: " + job.Error);
                // Odstep rosnie z kazda kolejna porazka tego samego kafla (10s, 40s, 90s, ... max 20 min) -
                // zeby trwale zepsuty kafel nie probowal sie budowac od nowa co kilka sekund bez konca.
                int fails; failCount.TryGetValue(key, out fails); fails++; failCount[key] = fails;
                float backoff = Mathf.Min(10f * fails * fails, 1200f);
                failedUntil[key] = Time.unscaledTime + backoff;
                yield break;
            }
            failCount.Remove(key);
            failedUntil.Remove(key);

            var tile = new LoadedTile
            {
                ILat = iLat, ILon = iLon, Body = body,
                Lat = lat, Lon = lon, Alt = alt0,
                Grid = terrain
            };

            if (job.WayCount == 0)
            {
                tile.Empty = true;
                loaded[key] = tile;
                status = "Tile is empty (ocean / no data).";
                yield break;
            }

            // --- 4. obiekty Unity: znowu glowny watek, ale juz tylko przepisanie tablic ---
            tile.Root = new GameObject(string.Format("OSMTile[{0},{1}]", iLat, iLon));
            tile.Root.layer = LayerLocalScenery;

            // WYLACZONY na czas budowy. Miedzy dodaniem siatek a PlaceTile jest
            // "yield return null", czyli cala klatka - a swiezy GameObject siedzi
            // w poczatku ukladu swiata. Przy floating origin to jest tuz obok statku,
            // wiec landuse kazdego nowego kafla mrugal na klatke dookola gracza.
            tile.Root.SetActive(false);

            // Landuse pierwszy - to podloze, na ktorym leza drogi i budynki.
            if (job.Landuse != null)
                tile.Landuse = AttachChild(tile.Root, "Landuse", job.Landuse,
                                           MaterialLib.LanduseMaterials(), false);

            if (job.Trees != null && TreeModels.Ready)
                tile.Trees = new TileTrees(job.Trees, TreeModels.Species);

            if (job.Roads != null)
                tile.Roads = AttachChild(tile.Root, "Roads", job.Roads,
                                         MaterialLib.RoadMaterials(), false);

            if (job.Lamps != null)
            {
                tile.Lamps = AttachChild(tile.Root, "Lamps", job.Lamps,
                                         MaterialLib.LampMaterials(), false);
                tile.Lamps_ = job.LampCount;
            }

            yield return null;

            Material[] mats = MaterialLib.BuildingMaterials();
            if (job.BuildFull != null)
                tile.BuildFull = AttachChild(tile.Root, "BuildingsFull", job.BuildFull, mats, true);
            if (job.BuildCoarse != null)
                tile.BuildCoarse = AttachChild(tile.Root, "BuildingsCoarse", job.BuildCoarse, mats, true);

            tile.Buildings = job.BuildingCount;
            tile.Lights = LampLights.Make(job.LampHeads, job.BuildingCount);
            if (job.CropAreas != null && job.CropAreas.Count > 0)
                tile.Crops = new CropScatter.TileCrops { Areas = job.CropAreas, Blocked = job.Blocked };
            loaded[key] = tile;
            PlaceTile(tile);

            // Dokladne doklejenie do terenu. Watek roboczy mial tylko siatke
            // 64x64, PQS zna prawdziwa wysokosc w kazdym punkcie - ale wolno
            // go pytac wylacznie stad, z glownego watku.
            if (tile.Roads != null) MakeProbes(tile, job.Roads);
            if (wantSnap && tile.Roads != null)
                SnapRoads(tile);

            // Landuse musi byc przyklejony TAK SAMO jak drogi. Z samej siatki 64x64
            // plachta w zaglebieniu terenu unosila sie ponad przyklejona droge
            // i ja zaslaniala ("znikaja drogi"), a na wypuklosci przebijala sie
            // przez teren jako lata. Unos poszczegolnych klas (0.10-0.24 m) jest juz
            // zapisany w wierzcholkach, wiec snapper dodaje go z powrotem z siatki.
            if (wantSnap && tile.Landuse != null)
                GroundSnapper.RequestKeepingLift(tile.Landuse, body, lat, lon, alt0,
                                                 body.Radius, terrain);
            tile.Root.SetActive(true);      // dopiero teraz kafel stoi tam, gdzie ma

            lastJobMs = (int)sw.ElapsedMilliseconds;
            status = string.Format("[{0},{1}]: {2} buildings, {3} road segments, {4} street lamps, {5} ms in background. Tiles: {6}, queue: {7}",
                                   iLat, iLon, job.BuildingCount, job.RoadSegments,
                                   job.LampCount, lastJobMs, loaded.Count, pending.Count);
        }

        /// <summary>Kamera i statek w stopniach i metrach n.p.m. - punkty odniesienia kompensacji ugiecia.</summary>
        private void ReferencePoints(CelestialBody body, out double camLat, out double camLon, out double camAlt,
                                     out double vesLat, out double vesLon, out double vesAlt)
        {
            Vector3d eye = ViewPoint();
            camLat = body.GetLatitude(eye);
            camLon = body.GetLongitude(eye);
            camAlt = body.GetAltitude(eye);

            Vessel v = FlightGlobals.ActiveVessel;
            if (v != null && v.mainBody == body) { vesLat = v.latitude; vesLon = v.longitude; vesAlt = v.altitude; }
            else { vesLat = 0; vesLon = 0; vesAlt = 0; }
        }

        /// <summary>Przykleja droge kafla do terenu, z zapasem na ugiecie siatki PQS.</summary>
        private void SnapRoads(LoadedTile t)
        {
            if (t.Roads == null || t.Body == null) return;

            // Wspolny punkt odniesienia dla wszystkich kafli (epoka), a nie aktualna kamera:
            // dwa sasiednie kafle przyklejone w roznych chwilach dawaly na styku rozne
            // wysokosci - prog, w ktory uderzaly kola.
            if (!epochSet) UpdateSnapEpoch(t.Body);
            double cLat = eCamLat, cLon = eCamLon, cAlt = eCamAlt, vLat = eVesLat, vLon = eVesLon, vAlt = eVesAlt;

            t.SnapBusy = true;
            t.TerrainDirty = false;
            t.SnapEpoch = snapEpoch;
            t.SnapCamLat = cLat; t.SnapCamLon = cLon; t.SnapCamAlt = cAlt;
            t.SnapVesLat = vLat; t.SnapVesLon = vLon;
            t.SnapK = sagComp; t.SnapH = roadHeight;

            GroundSnapper.RequestRoads(t.Roads, t.Body, t.Lat, t.Lon, t.Alt, t.Body.Radius, roadHeight,
                                       t.Grid, sagComp, cLat, cLon, cAlt, vLat, vLon, vAlt,
                                       () => { t.SnapBusy = false; RecordProbes(t); });
        }

        /// <summary>
        /// Czy droge trzeba przykleic jeszcze raz: zmienily sie ustawienia albo kamera / statek
        /// odjechaly na tyle, ze rozdzielczosc siatki PQS wokol nich jest inna niz przy
        /// poprzednim przyklejeniu (blizej czworokaty sa drobniejsze, zapas mniejszy).
        /// </summary>
        private bool NeedsResnap(LoadedTile t)
        {
            if (Mathf.Abs(t.SnapK - sagComp) > 0.05f || Mathf.Abs(t.SnapH - roadHeight) > 0.05f) return true;
            return t.SnapEpoch != snapEpoch;
        }

        // --- epoka przyklejania: wspolny punkt odniesienia kamery i statku ---
        private int snapEpoch;
        private bool epochSet;
        private double eCamLat, eCamLon, eCamAlt, eVesLat, eVesLon, eVesAlt;

        /// <summary>Nowa epoka, gdy kamera albo statek odjada o 100 m, albo kamera zmieni wysokosc
        /// na tyle, ze PQS rysuje inna gestosc siatki. Wszystkie kafle w zasiegu przyklejaja sie
        /// wtedy od nowa wzgledem TEGO SAMEGO punktu - szwy miedzy nimi zostaja rowne.</summary>
        private void UpdateSnapEpoch(CelestialBody body)
        {
            if (body == null) return;
            double cLat, cLon, cAlt, vLat, vLon, vAlt;
            ReferencePoints(body, out cLat, out cLon, out cAlt, out vLat, out vLon, out vAlt);
            bool change = !epochSet;
            if (!change)
            {
                double mLat = body.Radius * Geo.DegToRad, mLon = mLat * Math.Cos(cLat * Geo.DegToRad);
                double cam = Math.Sqrt(Sq((cLat - eCamLat) * mLat) + Sq((cLon - eCamLon) * mLon));
                double ves = Math.Sqrt(Sq((vLat - eVesLat) * mLat) + Sq((vLon - eVesLon) * mLon));
                double da = Math.Abs(cAlt - eCamAlt);
                change = cam > 400.0 || ves > 400.0 || da > Math.Max(60.0, 0.35 * Math.Min(cAlt, eCamAlt));
            }
            if (!change) return;
            epochSet = true;
            snapEpoch++;
            OrthoGround.Resnap();
            eCamLat = cLat; eCamLon = cLon; eCamAlt = cAlt;
            eVesLat = vLat; eVesLon = vLon; eVesAlt = vAlt;
        }

        /// <summary>Nie podmieniamy kolidera drogi pod jadacym lazikiem: nowa wysokosc w jednej
        /// klatce wciskala kola pod jezdnie albo wystrzeliwala pojazd w gore. Kolider dogoni
        /// wyglad, gdy pojazd stanie albo odjedzie z tego kafla.</summary>
        private static bool HoldCollider(GameObject go)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            // Wylaczone: wstrzymany kolider pod pojazdem przy nowym u sasiadow dawal prog na styku kafli.
            // Zamiast tego GroundSnapper nie przeladowuje kolidera, gdy droga sie praktycznie nie zmienila.
            return false;
#pragma warning disable 0162
            if (v == null || go == null || !v.LandedOrSplashed || v.srfSpeed < 0.3) return false;
            Transform root = go.transform.parent != null ? go.transform.parent : go.transform;
            return (root.position - (Vector3)v.CoMD).sqrMagnitude < 1300f * 1300f;
#pragma warning restore 0162
        }
        private static double Sq(double x) { return x * x; }

        private readonly List<KeyValuePair<Transform, LampLights.TileLights>> lightTiles =
            new List<KeyValuePair<Transform, LampLights.TileLights>>();

        private void DestroyTile(LoadedTile t)
        {
            if (t == null) return;
            LampLights.Destroy(t.Lights);
            CropScatter.Release(t.Crops);
            if (t.Root != null) Destroy(t.Root);
        }

        // ------------------------------------------------------------------
        //  Straznik terenu
        //
        //  PQS zageszcza i rozrzedza siatke terenu sam z siebie (kamera sie zbliza,
        //  kafle PQS dobudowuja sie po kilka klatek). Droga przyklejona do rzadszej
        //  siatki zostawala na jej wysokosci - w zaglebieniu metr-dwa nad ziemia,
        //  razem z koliderem ("niewidzialny kolider", lazik jedzie w powietrzu).
        //  Co klatke sprawdzamy jeden kafel: kilkanascie promieni w punkty na
        //  drogach i porownanie z wysokoscia z chwili przyklejenia.
        // ------------------------------------------------------------------

        private const int ProbeCount = 16;
        private int watchCursor, watchIndex;

        private static void MakeProbes(LoadedTile t, MeshData roads)
        {
            if (roads == null || roads.Verts == null || roads.Verts.Length == 0) return;
            int n = roads.Verts.Length;
            int step = Math.Max(1, n / ProbeCount);
            var la = new List<double>(); var lo = new List<double>();
            for (int i = step / 2; i < n && la.Count < ProbeCount; i += step)
            {
                Vector3 v = roads.Verts[i];
                double lat, lon;
                Geo.FromTangent(v.x, v.z, t.Lat, t.Lon, t.Body.Radius + t.Alt, out lat, out lon);
                la.Add(lat); lo.Add(lon);
            }
            t.ProbeLat = la.ToArray(); t.ProbeLon = lo.ToArray();
            t.ProbeH = null;
        }

        private static double ProbeHeight(CelestialBody b, double lat, double lon)
        {
            double raw = TerrainGrid.RawAltitude(b, lat, lon), h;
            // brak kolidera PQS = inna liczba niz jakakolwiek wysokosc, zeby pojawienie sie
            // albo znikniecie kolidera tez liczylo sie jako zmiana
            return GroundSnapper.MeshAltitude(b, lat, lon, raw, out h) ? h : -1e7;
        }

        private static void RecordProbes(LoadedTile t)
        {
            if (t.ProbeLat == null || t.Body == null || t.Body.pqsController == null) return;
            var h = new double[t.ProbeLat.Length];
            for (int i = 0; i < h.Length; i++) h[i] = ProbeHeight(t.Body, t.ProbeLat[i], t.ProbeLon[i]);
            t.ProbeH = h;
        }

        /// <summary>true, gdy siatka PQS pod drogami kafla jest inna niz przy przyklejaniu.</summary>
        private static bool TerrainChanged(LoadedTile t)
        {
            if (t.ProbeH == null || t.Body == null || t.Body.pqsController == null) return false;
            for (int i = 0; i < t.ProbeH.Length; i++)
            {
                double h = ProbeHeight(t.Body, t.ProbeLat[i], t.ProbeLon[i]);
                if (Math.Abs(h - t.ProbeH[i]) > 0.12) return true;
            }
            return false;
        }

        private static GameObject AttachChild(GameObject root, string name, MeshData data,
                                              Material[] mats, bool castShadows)
        {
            var go = new GameObject(name);
            go.layer = LayerLocalScenery;
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = data.ToMesh();

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats;
            mr.shadowCastingMode = castShadows
                ? UnityEngine.Rendering.ShadowCastingMode.On
                : UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        // ------------------------------------------------------------------
        //  Umiejscowienie, LOD, noc
        // ------------------------------------------------------------------

        public void LateUpdate()
        {
            if (!modEnabled) return;               // wylaczony calkowicie - nic sie nie liczy ani nie rysuje

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;

            UpdateNight(v);
            UpdateSnapEpoch(v.mainBody);
            if (GroundSnapper.HoldCollider == null) GroundSnapper.HoldCollider = HoldCollider;

            Vector3d vp = ViewPoint();
            var dead = new List<long>();
            lightTiles.Clear();
            int cropBudget = 1;
            if (CropScatter.SettingsChanged)
            {
                CropScatter.SettingsChanged = false;
                foreach (LoadedTile lt in loaded.Values) CropScatter.Release(lt.Crops);
            }
            watchIndex = 0;
            watchCursor = loaded.Count > 0 ? (watchCursor + 1) % loaded.Count : 0;

            foreach (KeyValuePair<long, LoadedTile> kv in loaded)
            {
                LoadedTile t = kv.Value;
                if (t.Empty || t.Root == null) continue;

                PlaceTile(t);

                float d = Vector3.Distance(t.Root.transform.position, (Vector3)vp);

                if (d > distUnload) { dead.Add(kv.Key); continue; }

                // LOD: blisko pelne budynki, dalej tylko wysokie, jeszcze dalej nic.
                bool full = d <= distFullBuildings;
                bool coarse = !full && d <= distCoarseBuildings;

                if (t.BuildFull != null && t.BuildFull.activeSelf != full)
                    t.BuildFull.SetActive(full);
                if (t.BuildCoarse != null && t.BuildCoarse.activeSelf != coarse)
                    t.BuildCoarse.SetActive(coarse);
                if (t.Roads != null)
                {
                    bool r = d <= distRoads;
                    if (t.Roads.activeSelf != r) t.Roads.SetActive(r);
                }

                if (t.Lamps != null)
                {
                    bool l = d <= distLamps;
                    if (t.Lamps.activeSelf != l) t.Lamps.SetActive(l);
                }

                UpdateColliders(t, d);

                // Kompensacja ugiecia zalezy od odleglosci od kamery, wiec kafle, ktore sa w jej
                // zasiegu (dalej zapas i tak siedzi na gornej granicy), przykleja sie od nowa
                // po przesunieciu. Po dwa naraz, zeby nie zapchac kolejki przyklejania.
                if (wantSnap && t.Roads != null && t.Grid != null && !t.SnapBusy)
                {
                    // jeden kafel na klatke w zasiegu widocznych drog
                    if (watchIndex++ == watchCursor && t.Roads.activeSelf && d < Mathf.Min(distRoads, 12000f) &&
                        TerrainChanged(t))
                        t.TerrainDirty = true;
                    if (GroundSnapper.Pending < 3 && (t.TerrainDirty || (d < 2600f && NeedsResnap(t))))
                        SnapRoads(t);
                }

                // Drzewa nie sa GameObjectami, wiec nie ma czego wylaczac - po prostu
                // rysujemy tylko te z bliskich kafli.
                if (t.Trees != null && TreeModels.Ready)
                {
                    if (d <= distTrees)
                        t.Trees.Draw(t.Root.transform.localToWorldMatrix, TreeModels.Species, LayerLocalScenery, d);
                    else if (d <= distTreesFar)
                        t.Trees.DrawFar(t.Root.transform.localToWorldMatrix, TreeModels.Species, LayerLocalScenery, d);
                }

                if (t.Crops != null)
                {
                    if (d < CropScatter.Radius + 1200f)
                        CropScatter.UpdateTile(t.Crops, t.Root.transform, t.Body, t.Lat, t.Lon, t.Alt, t.Grid,
                                               t.Root.transform.InverseTransformPoint((Vector3)vp), ref cropBudget, LayerLocalScenery);
                    else if (t.Crops.Chunks.Count > 0) CropScatter.Release(t.Crops);
                }

                if (t.Lights != null)
                {
                    LampLights.Draw(t.Lights, t.Root.transform.localToWorldMatrix, d, LayerLocalScenery);
                    if (d < 2000f) lightTiles.Add(new KeyValuePair<Transform, LampLights.TileLights>(t.Root.transform, t.Lights));
                }
            }

            // Jeden collider na klatke. Cooking PhysX kosztuje kilkanascie
            // milisekund na siatke - hurtem daje zwieche przy wjezdzie w miasto.
            ColliderQueue.Step();
            GroundSnapper.Step();
            LampLights.UpdateReal(lightTiles, (Vector3)vp);
            OrthoGround.Update(v.mainBody, vp, LayerLocalScenery, sagComp);

            foreach (long k in dead)
            {
                DestroyTile(loaded[k]);
                loaded.Remove(k);
            }

            // Budzet kafli, w duchu webDiskCapMB Mirage'a: gdy przekroczony,
            // wypada NAJDALSZY, a nie najstarszy. Przy zawracaniu kamery
            // to, co masz przed soba, zostaje.
            while (loaded.Count > maxLoadedTiles)
            {
                long worst = 0;
                double worstD = -1;
                foreach (KeyValuePair<long, LoadedTile> kv in loaded)
                {
                    double d = haveGrid ? lastGrid.DistanceM(kv.Key, eyeLat, eyeLon) : 0;
                    if (d > worstD) { worstD = d; worst = kv.Key; }
                }

                DestroyTile(loaded[worst]);
                loaded.Remove(worst);
            }
        }

        /// <summary>
        /// Wlacza fizyke na kaflach w poblizu i zdejmuje ja z oddalonych.
        ///
        /// Histereza (1.6x) jest tu istotna: bez niej kafel balansujacy na granicy
        /// zasiegu dokladalby i kasowal collider co druga klatke, a kazde dolozenie
        /// to ponowny cooking.
        /// </summary>
        private void UpdateColliders(LoadedTile t, float d)
        {
            bool want = wantColliders && d <= distColliders;

            if (want && !t.CollidersOn)
            {
                ColliderQueue.Request(t.BuildFull);
                ColliderQueue.Request(t.Roads);
                t.CollidersOn = true;
            }
            else if (t.CollidersOn && (!wantColliders || d > distColliders * 1.6f))
            {
                ColliderQueue.Drop(t.BuildFull);
                ColliderQueue.Drop(t.Roads);
                t.CollidersOn = false;
            }
        }

        /// <summary>
        /// Kat slonca nad horyzontem w miejscu statku -> plynne rozswietlenie okien
        /// o zmierzchu. Emisja jest na wspoldzielonych materialach, wiec to jedno
        /// ustawienie na klatke, nie przejscie po kaflach.
        /// </summary>
        private void UpdateNight(Vessel v)
        {
            CelestialBody body = v.mainBody;
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (body == null || sun == null || body == sun) return;

            Vector3d pos = v.transform.position;
            Vector3d up = (pos - body.position).normalized;
            Vector3d toSun = (sun.position - pos).normalized;
            float dot = (float)Vector3d.Dot(up, toSun);

            // 1 gdy slonce ponizej -0.10, 0 gdy powyzej +0.10
            float target = Mathf.InverseLerp(0.10f, -0.10f, dot);
            nightFactor = Mathf.MoveTowards(nightFactor, target, Time.deltaTime * 0.5f);
            MaterialLib.SetNightFactor(nightFactor);
            LampLights.SetNight(nightFactor);
        }

        /// <summary>
        /// Ustawia transform kafla wzgledem obracajacej sie planety. Wierzcholki
        /// zostaja nietkniete - stad to jest tanie i mozna robic co klatke.
        /// </summary>
        private static void PlaceTile(LoadedTile t)
        {
            CelestialBody b = t.Body;
            Vector3d origin = b.GetWorldSurfacePosition(t.Lat, t.Lon, t.Alt);
            Vector3d up = (origin - b.position).normalized;

            double dLat = t.Lat > 89.9 ? -0.001 : 0.001;
            Vector3d northPt = b.GetWorldSurfacePosition(t.Lat + dLat, t.Lon, t.Alt);
            Vector3d north = (northPt - origin) * Math.Sign(dLat);

            Vector3 fUp = (Vector3)up;
            Vector3 fNorth = (Vector3)north;
            Vector3.OrthoNormalize(ref fUp, ref fNorth);

            t.Root.transform.position = origin;
            t.Root.transform.rotation = Quaternion.LookRotation(fNorth, fUp);
        }

        // ------------------------------------------------------------------
        //  UI
        // ------------------------------------------------------------------

        private const string PanelId = "osmroads.roads";

        private void OnPanelOpenChanged(string id, bool open)
        {
            if (id == PanelId && appButton != null) appButton.Sync(open);
        }

        private void DrawPanel()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            GUILayout.BeginVertical();

            GUILayout.BeginHorizontal();
            bool nowOn = GUILayout.Toggle(modEnabled, modEnabled ? " OSMRoads: ON" : " OSMRoads: OFF (does nothing, tiles cleared)", GUILayout.Height(24));
            if (nowOn != modEnabled) SetModEnabled(nowOn);
            GUILayout.EndHorizontal();
            if (!modEnabled)
            {
                DeltaUI.Widgets.Dim("Mod is fully off: no scanning, no worker thread, no tiles rendered. Saved to settings.cfg.");
                GUILayout.EndVertical();
                return;
            }
            GUILayout.Space(4);

            roadsScroll = GUILayout.BeginScrollView(roadsScroll);

            if (v == null || v.mainBody == null)
            {
                GUILayout.Label("No active vessel.");
            }
            else
            {
                CelestialBody b = v.mainBody;
                GUILayout.Label(string.Format("{0}  lat {1:F5}  lon {2:F5}",
                                              b.bodyName, v.latitude, v.longitude));
                GUILayout.Label(string.Format("Tiles: {0}   queue: {1}   requests: {2}   night: {3:P0}",
                                              loaded.Count, pending.Count,
                                              OverpassClient.NetworkRequestCount, nightFactor));

                GUILayout.Space(4);
                autoLoad = GUILayout.Toggle(autoLoad, " auto-load tiles");
                useCameraOrigin = GUILayout.Toggle(useCameraOrigin, " measure from camera (not vessel)");
                useLocalStore = GUILayout.Toggle(useLocalStore, " use local data set (when available)");
                wantRoads = GUILayout.Toggle(wantRoads, " roads");
                wantBuildings = GUILayout.Toggle(wantBuildings, " buildings");
                wantLanduse = GUILayout.Toggle(wantLanduse, " trees (from forest and park areas)");
                bool fill = GUILayout.Toggle(drawLanduseFill, " landuse fills (may flicker against terrain)");
                if (fill != drawLanduseFill) { drawLanduseFill = fill; ClearTiles(); }
                wantLamps = GUILayout.Toggle(wantLamps, " street lamps (along roads)");
                wantColliders = GUILayout.Toggle(wantColliders, " physics: building and road colliders");
                wantSnap = GUILayout.Toggle(wantSnap, " snap roads to terrain (exact PQS)");
                if (GroundSnapper.Pending > 0)
                    GUILayout.Label(string.Format("   snapping: {0} tiles queued",
                                                  GroundSnapper.Pending));

                // Skad polecialyby dane dla miejsca, nad ktorym jestes teraz.
                // Bez tego jedyna oznaka braku paczki byly puste kafle.
                if (b.pqsController != null)
                {
                    TileGrid tg = Grid(b);
                    int qLat, qLon;
                    tg.IndexOf(v.latitude, v.longitude, out qLat, out qLon);
                    bool covered = LocalStore.Covers(tg.BoxOf(qLat, qLon));
                    GUILayout.Label(covered
                        ? "Data source here: local pack"
                        : "Data source here: Overpass (network) - no pack for this area");
                }

                double siteDist;
                LaunchSite near = LaunchSites.Nearest(v.latitude, v.longitude,
                                                      b.Radius, out siteDist);
                if (near != null)
                    GUILayout.Label(string.Format("Nearest launch site: {0} ({1}) - {2:N0} km",
                                                  near.Name, near.Operator, siteDist / 1000.0));

                GUILayout.Space(4);
                GUILayout.Label(string.Format("Tile size: {0:N0} m", tileSizeM));
                tileSizeM = Mathf.Round(GUILayout.HorizontalSlider(tileSizeM, 500f, 4000f) / 100f) * 100f;

                // Promien nie jest juz osobnym suwakiem - wynika z zasiegu LOD.
                // Pokazujemy, ile kafli z tego wychodzi, zeby ustawienia nie
                // obiecywaly cicho czegos, czego streaming nie dowiezie.
                int ring = NeededRing();
                int need = 0;
                for (int dy = -ring; dy <= ring; dy++)
                    for (int dx = -ring; dx <= ring; dx++)
                        if (dx * dx + dy * dy <= ring * ring) need++;

                GUILayout.Label(string.Format("Radius from LOD: {0} tiles = {1:N0} m   -> {2} tiles in the circle",
                                              ring, ring * tileSizeM, need));
                if (need > maxLoadedTiles)
                    GUILayout.Label(string.Format("   note: budget is {0} - distant tiles will be dropped",
                                                  maxLoadedTiles));

                GUILayout.Label(string.Format("Tile budget in memory: {0}", maxLoadedTiles));
                maxLoadedTiles = (int)GUILayout.HorizontalSlider(maxLoadedTiles, 40f, 2700f);

                GUILayout.Label(string.Format("Road height above ground: {0:F2} m", roadHeight));
                roadHeight = GUILayout.HorizontalSlider(roadHeight, 0f, 6f);

                GUILayout.Label(string.Format("Terrain-mesh sag compensation: {0:F2}x  (0 = off)", sagComp));
                sagComp = GUILayout.HorizontalSlider(sagComp, 0f, 4f);

                GUILayout.Label(string.Format("Road width multiplier: {0:F2}", widthScale));
                widthScale = GUILayout.HorizontalSlider(widthScale, 0.25f, 4f);

                GUILayout.Label(string.Format("Building height multiplier: {0:F2}", buildingHeightScale));
                buildingHeightScale = GUILayout.HorizontalSlider(buildingHeightScale, 0.25f, 3f);

                GUILayout.Label(string.Format("Delay between requests: {0:F0} s  (0 = no limit)", minRequestGap));
                minRequestGap = GUILayout.HorizontalSlider(minRequestGap, 0f, 20f);

                GUILayout.Label(string.Format("Lamp / physics range: {0:N0} / {1:N0} m",
                                              distLamps, distColliders));
                distLamps = GUILayout.HorizontalSlider(distLamps, 200f, 18000f);
                distColliders = GUILayout.HorizontalSlider(distColliders, 200f, 8000f);

                GUILayout.Label(string.Format("Trees full / far LOD: {0:N0} / {1:N0} m", distTrees, distTreesFar));
                distTrees = GUILayout.HorizontalSlider(distTrees, 300f, 12000f);
                distTreesFar = GUILayout.HorizontalSlider(distTreesFar, distTrees, 60000f);

                LampLights.DrawSettings();
                OrthoGround.DrawSettings();
                CropScatter.DrawSettings();

                GUILayout.Label(string.Format("Tree density: {0:F2}   (limit {1}/tile)", treeDensity, maxTreesPerTile));
                treeDensity = GUILayout.HorizontalSlider(treeDensity, 0.2f, 2.5f);
            GUILayout.Label(string.Format("Tree limit per tile: {0:N0}  (applies to newly loaded tiles)", OsmSettings.TreeLimit));
            int tl = Mathf.RoundToInt(GUILayout.HorizontalSlider(OsmSettings.TreeLimit, 1000f, 60000f) / 1000f) * 1000;
            if (tl != OsmSettings.TreeLimit) { OsmSettings.TreeLimit = tl; OsmSettings.Save(); }

                GUILayout.Label(string.Format("Terrain grid: {0}x{0}  (node every {1:F0} m)",
                                              terrainGridN, tileSizeM / (terrainGridN - 1)));
                terrainGridN = (int)GUILayout.HorizontalSlider(terrainGridN, 16f, 128f);

                GUILayout.Space(4);
                GUILayout.Label(string.Format("LOD full / simplified / roads / unload:"));
                GUILayout.Label(string.Format("{0:N0} / {1:N0} / {2:N0} / {3:N0} m",
                                              distFullBuildings, distCoarseBuildings,
                                              distRoads, distUnload));
                distFullBuildings = GUILayout.HorizontalSlider(distFullBuildings, 500f, 60000f);
                distCoarseBuildings = GUILayout.HorizontalSlider(distCoarseBuildings,
                                                                 distFullBuildings, 180000f);
                distRoads = GUILayout.HorizontalSlider(distRoads, 500f, 180000f);
                distUnload = GUILayout.HorizontalSlider(distUnload,
                                                        Mathf.Max(distCoarseBuildings, distRoads),
                                                        270000f);
                SaveLod();

                GUILayout.Space(4);
                GUI.enabled = b.pqsController != null;
                if (GUILayout.Button("Load tile here (manual)"))
                {
                    TileGrid g = Grid(b);
                    int iLat, iLon;
                    g.IndexOf(v.latitude, v.longitude, out iLat, out iLon);
                    long k = TileGrid.Key(iLat, iLon);
                    if (!loaded.ContainsKey(k) && queued.Add(k)) pending.Add(k);
                }
                GUI.enabled = true;

                if (b.pqsController == null)
                    GUILayout.Label("PQS inactive - you must be close to the surface.");

                if (GUILayout.Button("Write Kerbal Konstructs airports (Poland)"))
                    status = KKExport.Run(b);

                GUI.enabled = !capeBusy;
                if (GUILayout.Button(capeBusy ? "Cape Canaveral: downloading ..." : "Write Kerbal Konstructs launch sites (Cape Canaveral)"))
                    StartCoroutine(ExportCapeCanaveral(b));
                GUI.enabled = true;

                if (GUILayout.Button("Fix Katniss Cape Canaveral heights (Modern / Starship LC-39A)"))
                    status = KatnissHeightFix.Run(b);

                if (GUILayout.Button("Remove all"))
                    ClearTiles();

                // Katalog Data skanowany jest raz na sesje. Bez tego przycisku
                // paczka dorzucona w trakcie gry byla widoczna dopiero po
                // restarcie KSP. Kafle lecą razem z nia, bo te oznaczone jako
                // puste (brak pokrycia) same by sie nie odswiezyly.
                if (GUILayout.Button("Reload packs from disk"))
                {
                    LocalStore.Reload();
                    ClearTiles();
                    status = "Packs rescanned: " + LocalStore.PackCount;
                }
            }

            GUILayout.Space(4);
            GUILayout.Label(status);
            GUILayout.Label(LocalStore.Report);
            GUILayout.Label(TreeModels.Report);
            GUILayout.Label(MaterialLib.ShaderReport
                            + (MaterialLib.HasEmission ? ", emission OK" : ", NO emission (lights will not glow)"));
            GUILayout.Label("Dane (c) OpenStreetMap contributors, ODbL.");
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void ClearTiles()
        {
            foreach (KeyValuePair<long, LoadedTile> kv in loaded)
                DestroyTile(kv.Value);
            loaded.Clear();
            pending.Clear();
            queued.Clear();
            ColliderQueue.Clear();
            GroundSnapper.Clear();
            OrthoGround.Clear();
            status = "Cleared.";
        }

        private bool capeBusy;

        /// <summary>
        /// Pobiera realne kompleksy startowe (man_made=launch_pad) i pasy (aeroway=runway) z OSM w okolicy
        /// Przyladka Canaveral (KSC + CCSFS, Floryda) i zapisuje config KK z punktami startu. Live przez
        /// Overpass (jak planer), a nie z lokalnych paczek - to jednorazowe dzialanie, nie streaming.
        /// </summary>
        private IEnumerator ExportCapeCanaveral(CelestialBody body)
        {
            capeBusy = true;
            status = "Cape Canaveral: downloading from Overpass ...";

            // KSC (LC-39A/39B) + CCSFS (SLC-40/41/37/36/17 itd.) + Shuttle Landing Facility / Skid Strip.
            var box = new BBox { South = 28.30, North = 28.65, West = -80.75, East = -80.50 };

            string xml = null, err = null;
            yield return StartCoroutine(OverpassClient.Fetch(box, OsmLayers.LaunchPads,
                (x, e) => { xml = x; err = e; },
                p => { status = "Cape Canaveral: " + p; }));

            if (xml == null)
            {
                status = "Cape Canaveral: " + err;
                capeBusy = false;
                yield break;
            }

            List<OsmWay> ways = OsmXml.ParseWays(xml);
            List<GeoTag> nodes = OsmXml.ParseTaggedNodes(xml, "man_made", "launch_pad");

            var pads = KKExportSpaceports.FromWays(ways, body.Radius);
            // Punkt trafiony juz jako obszar (way) nie powinien dublowac sie jako wezel w tym samym miejscu -
            // Overpass czasem zwraca oba dla tego samego obiektu przy niejednoznacznym tagowaniu.
            var wayNames = new HashSet<string>();
            foreach (KKExportSpaceports.Pad p in pads) if (p.Kind == "launch_pad") wayNames.Add(p.Name);
            foreach (KKExportSpaceports.Pad p in KKExportSpaceports.FromNodes(nodes))
                if (wayNames.Add(p.Name)) pads.Add(p);

            if (pads.Count == 0)
            {
                status = "Cape Canaveral: no launch pads or runways found in that area.";
                capeBusy = false;
                yield break;
            }

            string text = KKExportSpaceports.BuildConfig(pads, body, "OSMRoads_CapeCanaveral",
                (la, lo) => TerrainGrid.RawAltitude(body, la, lo));

            string outPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/OSMRoads/KK/CapeCanaveral.cfg");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllText(outPath, text, new UTF8Encoding(false));

            status = string.Format("Wrote {0} Cape Canaveral launch sites to GameData/OSMRoads/KK/CapeCanaveral.cfg - restart KSP (needs Kerbal Konstructs).", pads.Count);
            capeBusy = false;
        }

        public void OnDestroy()
        {
            if (appButton != null) appButton.Destroy();
            DeltaUI.Dock.OpenChanged -= OnPanelOpenChanged;
            DeltaUI.Dock.Unregister(PanelId);
            ClearTiles();
        }
    }
}
