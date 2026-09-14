using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

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

        public bool Empty;                // pobrany, ale nic w nim nie bylo
        public int Buildings;
        public int Lamps_;                // ile latarni stanelo w tym kaflu
        public bool CollidersOn;          // fizyka wlaczona (kafel blisko statku)
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

        private bool show;
        private Rect win = new Rect(120, 90, 370, 0);
        private string status = "Gotowe.";
        private bool workerRunning;
        private float lastNetworkTime = -999f;
        private float lastScanTime;

        // --- ustawienia ---
        private bool autoLoad = true;
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
        private int maxTreesPerTile = 4000;
        private float distTrees = 1800f;
        private float tileSizeM = 1500f;
        private int maxRing = 14;              // gorny bezpiecznik na liczbe kafli
        private int maxLoadedTiles = 260;      // budzet kafli w pamieci, jak dysk w Mirage
        // 0.8 m to kompromis: nizej zaczyna sie z-fighting z terenem PQS przy
        // patrzeniu z kilku kilometrow, wyzej droga widocznie odstaje i robi
        // prog dla lazika. Przy wlaczonym przyklejaniu ta wartosc jest juz
        // tylko rownym unosem nad rzeczywistym terenem, a nie zapasem na blad.
        private float roadHeight = 0.3f;
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

        public void Start()
        {
            appButton = new AppButton(AppIcons.Roads, () => { show = true; }, () => { show = false; },
                KSP.UI.Screens.ApplicationLauncher.AppScenes.FLIGHT | KSP.UI.Screens.ApplicationLauncher.AppScenes.MAPVIEW);
            // Statyczna flaga przezywa zmiane sceny; korutyna zabita w trakcie
            // zadania zostawilaby ja zapalona na stale.
            OverpassClient.ResetBusy();
        }

        public void Update()
        {
            if (OsmSettings.Pressed(OsmSettings.RoadsKey, OsmSettings.RoadsModifier))
                show = !show;
                if (appButton != null) appButton.Sync(show);

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
            bool local = useLocalStore && LocalStore.Covers(box);
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
                        status = string.Format("Odstep: czekam {0:F0}s ...", wait);
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
                    status = "Blad: " + err;
                    yield break;      // nie oznaczamy jako loaded - sprobuje ponownie
                }
            }

            // --- 2. probkowanie terenu: PQS wolno tylko z glownego watku ---
            status = "Probkuje teren ...";
            var terrain = TerrainGrid.Create(box, terrainGridN, 0.30);
            yield return StartCoroutine(terrain.Sample(body, terrainRowsPerFrame));

            double alt0 = TerrainGrid.RawAltitude(body, lat, lon);

            // --- 3. parsowanie i geometria na watku roboczym ---
            status = "Licze geometrie w tle ...";
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
                status = "Watek roboczy padl - szczegoly w KSP.log";
                Debug.LogError("[OSMRoads] TileJob: " + job.Error);
                yield break;
            }

            var tile = new LoadedTile
            {
                ILat = iLat, ILon = iLon, Body = body,
                Lat = lat, Lon = lon, Alt = alt0
            };

            if (job.WayCount == 0)
            {
                tile.Empty = true;
                loaded[key] = tile;
                status = "Kafel pusty (ocean/pustka).";
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
            loaded[key] = tile;
            PlaceTile(tile);

            // Dokladne doklejenie do terenu. Watek roboczy mial tylko siatke
            // 64x64, PQS zna prawdziwa wysokosc w kazdym punkcie - ale wolno
            // go pytac wylacznie stad, z glownego watku.
            if (wantSnap && tile.Roads != null)
                GroundSnapper.Request(tile.Roads, body, lat, lon, alt0,
                                      body.Radius, roadHeight);

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
            status = string.Format("[{0},{1}]: {2} budynkow, {3} segm. drog, {4} latarni, {5} ms w tle. Kafle: {6}, kolejka: {7}",
                                   iLat, iLon, job.BuildingCount, job.RoadSegments,
                                   job.LampCount, lastJobMs, loaded.Count, pending.Count);
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
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;

            UpdateNight(v);

            Vector3d vp = ViewPoint();
            var dead = new List<long>();

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

                // Drzewa nie sa GameObjectami, wiec nie ma czego wylaczac - po prostu
                // rysujemy tylko te z bliskich kafli.
                if (t.Trees != null && d <= distTrees && TreeModels.Ready)
                    t.Trees.Draw(t.Root.transform.localToWorldMatrix,
                                 TreeModels.Species, LayerLocalScenery);
            }

            // Jeden collider na klatke. Cooking PhysX kosztuje kilkanascie
            // milisekund na siatke - hurtem daje zwieche przy wjezdzie w miasto.
            ColliderQueue.Step();
            GroundSnapper.Step();

            foreach (long k in dead)
            {
                LoadedTile t = loaded[k];
                if (t.Root != null) Destroy(t.Root);
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

                LoadedTile t = loaded[worst];
                if (t.Root != null) Destroy(t.Root);
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

        public void OnGUI()
        {
            if (!show) return;
            GUI.skin = UiSkin.Get();
            win = GUILayout.Window(GetInstanceID(), win, DrawWindow, "OSM Roads 0.21");
        }

        private void DrawWindow(int id)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            GUILayout.BeginVertical();

            if (v == null || v.mainBody == null)
            {
                GUILayout.Label("Brak aktywnego statku.");
            }
            else
            {
                CelestialBody b = v.mainBody;
                GUILayout.Label(string.Format("{0}  lat {1:F5}  lon {2:F5}",
                                              b.bodyName, v.latitude, v.longitude));
                GUILayout.Label(string.Format("Kafle: {0}   kolejka: {1}   zapytania: {2}   noc: {3:P0}",
                                              loaded.Count, pending.Count,
                                              OverpassClient.NetworkRequestCount, nightFactor));

                GUILayout.Space(4);
                autoLoad = GUILayout.Toggle(autoLoad, " automatyczne pobieranie");
                useCameraOrigin = GUILayout.Toggle(useCameraOrigin, " licz od kamery (nie od statku)");
                useLocalStore = GUILayout.Toggle(useLocalStore, " uzywaj lokalnego zbioru (gdy jest)");
                wantRoads = GUILayout.Toggle(wantRoads, " drogi");
                wantBuildings = GUILayout.Toggle(wantBuildings, " budynki");
                wantLanduse = GUILayout.Toggle(wantLanduse, " drzewa (z obszarow lasow i parkow)");
                bool fill = GUILayout.Toggle(drawLanduseFill, " wypelnienia landuse (moga migac na tle terenu)");
                if (fill != drawLanduseFill) { drawLanduseFill = fill; ClearTiles(); }
                wantLamps = GUILayout.Toggle(wantLamps, " latarnie uliczne (przy drogach)");
                wantColliders = GUILayout.Toggle(wantColliders, " fizyka: collidery budynkow i drog");
                wantSnap = GUILayout.Toggle(wantSnap, " przyklejaj drogi do terenu (dokladny PQS)");
                if (GroundSnapper.Pending > 0)
                    GUILayout.Label(string.Format("   przyklejanie: {0} kafli w kolejce",
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
                        ? "Zrodlo tutaj: lokalna paczka"
                        : "Zrodlo tutaj: Overpass (siec) - brak paczki na ten obszar");
                }

                double siteDist;
                LaunchSite near = LaunchSites.Nearest(v.latitude, v.longitude,
                                                      b.Radius, out siteDist);
                if (near != null)
                    GUILayout.Label(string.Format("Najblizszy kosmodrom: {0} ({1}) - {2:N0} km",
                                                  near.Name, near.Operator, siteDist / 1000.0));

                GUILayout.Space(4);
                GUILayout.Label(string.Format("Rozmiar kafla: {0:N0} m", tileSizeM));
                tileSizeM = Mathf.Round(GUILayout.HorizontalSlider(tileSizeM, 500f, 4000f) / 100f) * 100f;

                // Promien nie jest juz osobnym suwakiem - wynika z zasiegu LOD.
                // Pokazujemy, ile kafli z tego wychodzi, zeby ustawienia nie
                // obiecywaly cicho czegos, czego streaming nie dowiezie.
                int ring = NeededRing();
                int need = 0;
                for (int dy = -ring; dy <= ring; dy++)
                    for (int dx = -ring; dx <= ring; dx++)
                        if (dx * dx + dy * dy <= ring * ring) need++;

                GUILayout.Label(string.Format("Promien z LOD: {0} kafli = {1:N0} m   -> {2} kafli w kole",
                                              ring, ring * tileSizeM, need));
                if (need > maxLoadedTiles)
                    GUILayout.Label(string.Format("   uwaga: budzet to {0} - dalekie kafle beda wypadac",
                                                  maxLoadedTiles));

                GUILayout.Label(string.Format("Budzet kafli w pamieci: {0}", maxLoadedTiles));
                maxLoadedTiles = (int)GUILayout.HorizontalSlider(maxLoadedTiles, 40f, 900f);

                GUILayout.Label(string.Format("Drogi nad gruntem: {0:F2} m", roadHeight));
                roadHeight = GUILayout.HorizontalSlider(roadHeight, 0f, 6f);

                GUILayout.Label(string.Format("Mnoznik szerokosci drog: {0:F2}", widthScale));
                widthScale = GUILayout.HorizontalSlider(widthScale, 0.25f, 4f);

                GUILayout.Label(string.Format("Mnoznik wysokosci budynkow: {0:F2}", buildingHeightScale));
                buildingHeightScale = GUILayout.HorizontalSlider(buildingHeightScale, 0.25f, 3f);

                GUILayout.Label(string.Format("Odstep miedzy zapytaniami: {0:F0} s  (0 = bez limitu)", minRequestGap));
                minRequestGap = GUILayout.HorizontalSlider(minRequestGap, 0f, 20f);

                GUILayout.Label(string.Format("Zasieg latarni / fizyki: {0:N0} / {1:N0} m",
                                              distLamps, distColliders));
                distLamps = GUILayout.HorizontalSlider(distLamps, 200f, 6000f);
                distColliders = GUILayout.HorizontalSlider(distColliders, 200f, 8000f);

                GUILayout.Label(string.Format("Gestosc drzew: {0:F2}   (limit {1}/kafel)", treeDensity, maxTreesPerTile));
                treeDensity = GUILayout.HorizontalSlider(treeDensity, 0.2f, 2.5f);

                GUILayout.Label(string.Format("Siatka terenu: {0}x{0}  (wezel co {1:F0} m)",
                                              terrainGridN, tileSizeM / (terrainGridN - 1)));
                terrainGridN = (int)GUILayout.HorizontalSlider(terrainGridN, 16f, 128f);

                GUILayout.Space(4);
                GUILayout.Label(string.Format("LOD pelny / uproszczony / drogi / unload:"));
                GUILayout.Label(string.Format("{0:N0} / {1:N0} / {2:N0} / {3:N0} m",
                                              distFullBuildings, distCoarseBuildings,
                                              distRoads, distUnload));
                distFullBuildings = GUILayout.HorizontalSlider(distFullBuildings, 500f, 20000f);
                distCoarseBuildings = GUILayout.HorizontalSlider(distCoarseBuildings,
                                                                 distFullBuildings, 60000f);
                distRoads = GUILayout.HorizontalSlider(distRoads, 500f, 60000f);
                distUnload = GUILayout.HorizontalSlider(distUnload,
                                                        Mathf.Max(distCoarseBuildings, distRoads),
                                                        90000f);

                GUILayout.Space(4);
                GUI.enabled = b.pqsController != null;
                if (GUILayout.Button("Zaladuj kafel tutaj (recznie)"))
                {
                    TileGrid g = Grid(b);
                    int iLat, iLon;
                    g.IndexOf(v.latitude, v.longitude, out iLat, out iLon);
                    long k = TileGrid.Key(iLat, iLon);
                    if (!loaded.ContainsKey(k) && queued.Add(k)) pending.Add(k);
                }
                GUI.enabled = true;

                if (b.pqsController == null)
                    GUILayout.Label("PQS nieaktywny - musisz byc blisko powierzchni.");

                if (GUILayout.Button("Usun wszystko"))
                    ClearTiles();

                // Katalog Data skanowany jest raz na sesje. Bez tego przycisku
                // paczka dorzucona w trakcie gry byla widoczna dopiero po
                // restarcie KSP. Kafle lecą razem z nia, bo te oznaczone jako
                // puste (brak pokrycia) same by sie nie odswiezyly.
                if (GUILayout.Button("Przeladuj paczki z dysku"))
                {
                    LocalStore.Reload();
                    ClearTiles();
                    status = "Paczki przeskanowane: " + LocalStore.PackCount;
                }
            }

            GUILayout.Space(4);
            GUILayout.Label(status);
            GUILayout.Label(LocalStore.Report);
            GUILayout.Label(TreeModels.Report);
            GUILayout.Label(MaterialLib.ShaderReport
                            + (MaterialLib.HasEmission ? ", emisja OK" : ", BRAK emisji (nie bedzie swiatel)"));
            GUILayout.Label("Dane (c) OpenStreetMap contributors, ODbL.");
            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private void ClearTiles()
        {
            foreach (KeyValuePair<long, LoadedTile> kv in loaded)
                if (kv.Value.Root != null) Destroy(kv.Value.Root);
            loaded.Clear();
            pending.Clear();
            queued.Clear();
            ColliderQueue.Clear();
            GroundSnapper.Clear();
            status = "Wyczyszczone.";
        }

        public void OnDestroy()
        {
            if (appButton != null) appButton.Destroy();
            ClearTiles();
        }
    }
}
