using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Mapa nawigacji dla ekranu w kokpicie (RPM), w ciemnym stylu planera:
    /// plaska mapa OSM (drogi, obszary, obrysy) na podlodze, na niej nazwy ulic lezace
    /// wzdluz drog, trasa jako swiecaca wstega, zamiast pojazdu strzalka, i - opcjonalnie -
    /// budynki wyciagniete w 3D. Kamera patrzy prosto z gory (2D) albo zza strzalki (3D).
    ///
    /// Scena lezy na osobnej warstwie i jest wlaczana tylko na czas renderu naszej
    /// kamery, wiec kamery gry nigdy jej nie widza. Uklad: X = wschod, Z = polnoc,
    /// metry wzgledem srodka plotna. Plotno (2048 px) jest przerysowywane w tle,
    /// gdy pojazd odjedzie od srodka albo zmieni sie skala.
    /// </summary>
    /// <summary>Wejscie dla ekranow: dwa poziomy mapy (szczegolowy ~2 km i ogolny ~12 km),
    /// kazdy ze swoim plotnem - ekrany z roznym przyblizeniem nie przerysowuja sobie nawzajem mapy.</summary>
    public static class NavMapScene
    {
        private static readonly NavMapLevel fine = new NavMapLevel(1.0f), coarse = new NavMapLevel(6.0f);

        public static bool Render(Camera cam, RenderTexture target, Vessel v, bool threeD, float mpp, bool northUp, bool buildings3D)
        {
            NavMapLevel lvl = mpp <= 2.5f ? fine : coarse;
            if (!lvl.Ready && (lvl == fine ? coarse : fine).Ready)
            {
                (lvl == fine ? coarse : fine).Render(cam, target, v, threeD, mpp, northUp, buildings3D, false);
                lvl.Render(cam, null, v, threeD, mpp, northUp, buildings3D, true);       // tylko dociaga dane
                return true;
            }
            return lvl.Render(cam, target, v, threeD, mpp, northUp, buildings3D, false);
        }
    }

    public sealed class NavMapLevel
    {
        private readonly float levelMpp;
        public NavMapLevel(float mpp) { levelMpp = mpp; }

        private const int Layer = 22;               // KerbalInstructors - w locie nieuzywana
        private const int CanvasPx = 2048;
        private static readonly Vector3 Origin = new Vector3(0f, 30000f, 0f);
        private static readonly Color Background = new Color(0.02f, 0.04f, 0.07f, 1f);
        private static readonly Color Cyan = new Color(0.30f, 0.90f, 1.00f, 1f);

        private GameObject root, ground, buildings, route, arrow, labelsRoot;
        private RenderTexture canvas;
        private Material groundMat, routeMat, routeDoneMat, arrowMat, arrowEdgeMat;
        private Material[] buildingMats;
        private static Font font;
        private readonly List<TextMesh> labels = new List<TextMesh>();
        private readonly List<Vector3> labelDirs = new List<Vector3>();

        private double cLat = double.NaN, cLon;
        private float canvasMpp;
        private MapData job;
        private double jobLat, jobLon;
        private float jobMpp;

        public bool Ready { get { return !double.IsNaN(cLat); } }

        // ------------------------------------------------------------------

        private void EnsureObjects()
        {
            if (root != null) return;
            root = new GameObject("OSMRoads.NavMap" + levelMpp);
            UnityEngine.Object.DontDestroyOnLoad(root);
            root.transform.position = Origin;

            Shader unlitTex = Shader.Find("Unlit/Texture") ?? Shader.Find("Sprites/Default");
            Shader unlit = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");

            ground = Child("ground", Quad());
            groundMat = new Material(unlitTex);
            ground.GetComponent<MeshRenderer>().sharedMaterial = groundMat;

            buildings = Child("buildings", null);
            buildingMats = new Material[5];
            Color[] bc =
            {
                new Color(0.24f, 0.33f, 0.40f),     // dachy
                new Color(0.10f, 0.16f, 0.21f),     // sciany cienia
                new Color(0.13f, 0.20f, 0.26f),
                new Color(0.16f, 0.24f, 0.31f),
                new Color(0.19f, 0.28f, 0.36f),     // sciany od "slonca"
            };
            for (int i = 0; i < 5; i++) { buildingMats[i] = new Material(unlit); buildingMats[i].color = bc[i]; }
            buildings.GetComponent<MeshRenderer>().sharedMaterials = buildingMats;

            route = Child("route", null);
            routeMat = new Material(unlit) { color = Cyan };
            routeDoneMat = new Material(unlit) { color = new Color(0.20f, 0.35f, 0.42f) };
            route.GetComponent<MeshRenderer>().sharedMaterials = new[] { routeMat, routeDoneMat };

            arrow = Child("arrow", Chevron());
            arrowMat = new Material(unlit) { color = Color.white };
            arrowEdgeMat = new Material(unlit) { color = Cyan };
            arrow.GetComponent<MeshRenderer>().sharedMaterials = new[] { arrowEdgeMat, arrowMat };

            labelsRoot = new GameObject("labels");
            labelsRoot.transform.SetParent(root.transform, false);
            labelsRoot.layer = Layer;

            if (font == null) font = FindFont();

            root.SetActive(false);
        }

        /// <summary>KSP nie ma wbudowanego Ariala - bierzemy czcionke, ktora gra ma juz wczytana
        /// (dynamiczna, z materialem), najchetniej bezszeryfowa.</summary>
        private static Font FindFont()
        {
            Font best = null; int bestScore = -1;
            try
            {
                foreach (Font f in Resources.FindObjectsOfTypeAll<Font>())
                {
                    if (f == null || f.material == null) continue;
                    string n = f.name.ToLowerInvariant();
                    int score = f.dynamic ? 1 : 0;
                    if (n.Contains("calibri") || n.Contains("noto") || n.Contains("arial") || n.Contains("liberation") || n.Contains("roboto")) score += 4;
                    if (n.Contains("mono") || n.Contains("icon") || n.Contains("dings")) score -= 3;
                    if (score > bestScore) { bestScore = score; best = f; }
                }
            }
            catch (Exception e) { Debug.LogWarning("[OSMRoads] map font: " + e.Message); }
            if (best == null) { try { best = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch (Exception) { } }
            Debug.Log("[OSMRoads] map labels font: " + (best != null ? best.name : "NONE"));
            return best;
        }

        private GameObject Child(string name, Mesh mesh)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.layer = Layer;
            go.AddComponent<MeshFilter>().sharedMesh = mesh ?? new Mesh();
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go;
        }

        private static Mesh Quad()
        {
            var m = new Mesh();
            m.vertices = new[] { new Vector3(-0.5f, 0, -0.5f), new Vector3(-0.5f, 0, 0.5f), new Vector3(0.5f, 0, 0.5f), new Vector3(0.5f, 0, -0.5f) };
            m.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Strzalka jak w nawigacji: grot w +Z, obwodka (submesh 0) pod wypelnieniem (1).</summary>
        private static Mesh Chevron()
        {
            var v = new List<Vector3>();
            var t0 = new List<int>(); var t1 = new List<int>();
            Action<float, float, List<int>> add = (s, y, t) =>
            {
                int b = v.Count;
                v.Add(new Vector3(0, y, 1.0f * s)); v.Add(new Vector3(0.75f * s, y, -0.8f * s));
                v.Add(new Vector3(0, y, -0.35f * s)); v.Add(new Vector3(-0.75f * s, y, -0.8f * s));
                t.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            };
            add(1.25f, 0f, t0);
            add(1.0f, 0.05f, t1);
            var m = new Mesh();
            m.SetVertices(v);
            m.subMeshCount = 2;
            m.SetTriangles(t0, 0); m.SetTriangles(t1, 1);
            m.RecalculateBounds();
            return m;
        }

        // ------------------------------------------------------------------
        //  Dane i plotno
        // ------------------------------------------------------------------

        private void UpdateData(double lat, double lon, float viewMpp, double radius)
        {
            float want = viewMpp;

            if (job != null && job.Done)
            {
                if (job.Error == null && job.Ways != null) Build(job, jobLat, jobLon, jobMpp, radius);
                job = null;
            }
            if (job != null) return;

            bool far = true;
            if (Ready)
            {
                double e, n;
                Geo.ToLocalMeters(lat, lon, cLat, cLon, radius, out e, out n);
                double half = CanvasPx * canvasMpp * 0.5;
                far = Math.Abs(e) > half * 0.35 || Math.Abs(n) > half * 0.35 ||
                      want > canvasMpp * 1.8f || want < canvasMpp / 1.8f;
            }
            if (!far) return;

            double halfM = CanvasPx * want * 0.5;
            BBox box = Geo.BoxAround(lat, lon, halfM * 1.05, radius);
            job = new MapData(box, true, want) { WantLabels = true, BodyRadius = radius };
            jobLat = lat; jobLon = lon; jobMpp = want;
            job.Start();
        }

        private void Build(MapData d, double lat, double lon, float mpp, double radius)
        {
            if (canvas == null)
            {
                canvas = new RenderTexture(CanvasPx, CanvasPx, 0, RenderTextureFormat.ARGB32) { useMipMap = true, autoGenerateMips = true, anisoLevel = 4 };
                canvas.Create();
            }

            var view = new MapView { CenterLat = lat, CenterLon = lon, MetersPerPixel = mpp, BodyRadius = radius, Width = CanvasPx, Height = CanvasPx, RefLat = lat };
            MapStyle prevStyle = OsmSettings.Style; bool prevSat = OsmSettings.Satellite;
            OsmSettings.Style = MapStyle.Dark; OsmSettings.Satellite = false;
            try { MapRenderer.RenderTile(canvas, view, d.Ways, Background); }
            finally { OsmSettings.Style = prevStyle; OsmSettings.Satellite = prevSat; }

            cLat = lat; cLon = lon; canvasMpp = mpp;
            float size = CanvasPx * mpp;
            ground.transform.localScale = new Vector3(size, 1f, size);
            groundMat.mainTexture = canvas;

            BuildBuildings(d.Ways, radius);
            BuildLabels(d.RoadLabels, radius);
        }

        private Vector3 Local(double lat, double lon, double radius, float y)
        {
            double e, n;
            Geo.ToLocalMeters(lat, lon, cLat, cLon, radius, out e, out n);
            return new Vector3((float)e, y, (float)n);
        }

        private void BuildBuildings(List<OsmWay> ways, double radius)
        {
            var v = new List<Vector3>();
            var subs = new List<int>[5];
            for (int i = 0; i < 5; i++) subs[i] = new List<int>();
            var ring = new List<Vector3>();
            int count = 0;
            foreach (OsmWay w in ways)
            {
                if (!w.IsBuilding || w.Points.Count < 4 || count > 12000) continue;
                ring.Clear();
                foreach (GeoPoint g in w.Points) ring.Add(Local(g.Lat, g.Lon, radius, 0f));
                if ((ring[0] - ring[ring.Count - 1]).sqrMagnitude < 0.01f) ring.RemoveAt(ring.Count - 1);
                if (ring.Count < 3) continue;
                float h = Mathf.Clamp(w.EffectiveHeight(), 3f, 120f);
                count++;

                // obrys w kierunku przeciwnym do wskazowek zegara (z gory)
                float area = 0f;
                for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++) area += ring[j].x * ring[i].z - ring[i].x * ring[j].z;
                if (area < 0f) ring.Reverse();

                for (int i = 0; i < ring.Count; i++)
                {
                    Vector3 a = ring[i], b = ring[(i + 1) % ring.Count];
                    var n = new Vector2(b.z - a.z, -(b.x - a.x)).normalized;       // na zewnatrz
                    float light = Vector2.Dot(n, new Vector2(-0.6f, 0.8f).normalized);  // "slonce" z polnocnego zachodu
                    int sub = 1 + Mathf.Clamp(Mathf.RoundToInt((light + 1f) * 1.5f), 0, 3);
                    int k = v.Count;
                    v.Add(a); v.Add(b); v.Add(b + Vector3.up * h); v.Add(a + Vector3.up * h);
                    subs[sub].AddRange(new[] { k, k + 2, k + 1, k, k + 3, k + 2 });
                }
                // dach: wachlarz (obrysy OSM sa w wiekszosci wypukle albo prawie)
                int r0 = v.Count;
                foreach (Vector3 p in ring) v.Add(p + Vector3.up * h);
                for (int i = 1; i + 1 < ring.Count; i++) subs[0].AddRange(new[] { r0, r0 + i + 1, r0 + i });
            }
            var m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(v);
            m.subMeshCount = 5;
            for (int i = 0; i < 5; i++) m.SetTriangles(subs[i], i);
            m.RecalculateBounds();
            MeshFilter mf = buildings.GetComponent<MeshFilter>();
            if (mf.sharedMesh != null) UnityEngine.Object.Destroy(mf.sharedMesh);
            mf.sharedMesh = m;
        }

        private void BuildLabels(List<RoadLabel> list, double radius)
        {
            foreach (TextMesh t in labels) if (t != null) UnityEngine.Object.Destroy(t.gameObject);
            labels.Clear(); labelDirs.Clear();
            if (font == null || list == null) return;

            float half = CanvasPx * canvasMpp * 0.5f;
            var placed = new List<Vector4>();
            // wazniejsze drogi pierwsze - to one wygrywaja miejsce
            list = new List<RoadLabel>(list);
            list.Sort((x, y) => x.Rank != y.Rank ? x.Rank.CompareTo(y.Rank) : y.LengthM.CompareTo(x.LengthM));
            foreach (RoadLabel rl in list)
            {
                if (string.IsNullOrEmpty(rl.Name) || rl.LengthM < canvasMpp * 60f) continue;
                Vector3 a = Local(rl.LatA, rl.LonA, radius, 0f), b = Local(rl.LatB, rl.LonB, radius, 0f);
                Vector3 mid = (a + b) * 0.5f;
                if (Mathf.Abs(mid.x) > half || Mathf.Abs(mid.z) > half) continue;
                Vector3 dir = b - a; dir.y = 0f;
                if (dir.sqrMagnitude < 1f) continue;

                // napisy nie moga na siebie wchodzic: odstep co najmniej pol dlugosci napisu
                float charM = canvasMpp * (rl.Rank <= 1 ? 0.34f : 0.26f) * 48f * 0.55f;
                float lenM = rl.Name.Length * charM;
                bool clash = false;
                foreach (Vector4 q in placed)
                {
                    float dx = q.x - mid.x, dz = q.z - mid.z;
                    float need = (q.w + lenM) * 0.55f;
                    if (dx * dx + dz * dz < need * need) { clash = true; break; }
                }
                if (clash) continue;
                placed.Add(new Vector4(mid.x, 0f, mid.z, lenM));

                var go = new GameObject("label");
                go.layer = Layer;
                go.transform.SetParent(labelsRoot.transform, false);
                go.transform.localPosition = mid + Vector3.up * 0.3f;
                var tm = go.AddComponent<TextMesh>();
                tm.font = font;
                tm.text = rl.Name;
                tm.fontSize = 48;
                tm.characterSize = canvasMpp * (rl.Rank <= 1 ? 0.34f : 0.26f);
                tm.anchor = TextAnchor.MiddleCenter;
                tm.alignment = TextAlignment.Center;
                tm.color = rl.Rank <= 1 ? new Color(0.85f, 0.95f, 1f) : new Color(0.62f, 0.78f, 0.86f);
                go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
                labels.Add(tm);
                labelDirs.Add(dir.normalized);
                if (labels.Count >= 90) break;
            }
        }

        // ------------------------------------------------------------------
        //  Klatka
        // ------------------------------------------------------------------

        /// <summary>Renderuje mape do 'target'. false = dane jeszcze sie wczytuja.</summary>
        public bool Render(Camera cam, RenderTexture target, Vessel v, bool threeD, float mpp, bool northUp, bool buildings3D, bool dataOnly)
        {
            EnsureObjects();
            double radius = v.mainBody.Radius;
            double lat = v.latitude, lon = Navigation.Norm(v.longitude);
            UpdateData(lat, lon, levelMpp, radius);
            if (!Ready || dataOnly || target == null) return false;

            Vector3 me = Local(lat, lon, radius, 0f);
            float heading = (float)Navigation.HeadingDeg;
            Vector3 hdir = Quaternion.Euler(0f, heading, 0f) * Vector3.forward;       // kierunek jazdy w ukladzie mapy
            Vector3 up = northUp ? Vector3.forward : hdir;                            // "gora ekranu"
            float H = target.height;

            // strzalka: wielkosc stala na ekranie
            arrow.transform.localPosition = me + Vector3.up * 1.5f;
            arrow.transform.localRotation = Quaternion.Euler(0f, heading, 0f);
            float arrowM = mpp * (threeD ? 13f : 11f);
            arrow.transform.localScale = Vector3.one * arrowM;

            buildings.SetActive(buildings3D);
            BuildRoute(radius, mpp);

            // kamera
            cam.cullingMask = 1 << Layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Background;
            cam.targetTexture = target;
            cam.aspect = target.width / (float)target.height;
            Vector3 worldMe = Origin + me;
            if (!threeD)
            {
                float halfH = mpp * H * 0.5f;
                cam.orthographic = true;
                cam.orthographicSize = halfH;
                cam.transform.position = worldMe + Vector3.up * 3000f + up * (halfH * 0.24f);
                cam.transform.rotation = Quaternion.LookRotation(Vector3.down, up);
                cam.nearClipPlane = 10f; cam.farClipPlane = 6000f;
            }
            else
            {
                float d = mpp * H * 0.55f;
                cam.orthographic = false;
                cam.fieldOfView = 50f;
                cam.transform.position = worldMe - hdir * d + Vector3.up * (d * 0.75f);
                cam.transform.rotation = Quaternion.LookRotation(worldMe + hdir * d * 0.45f - cam.transform.position, Vector3.up);
                cam.nearClipPlane = 1f; cam.farClipPlane = d * 12f + 3000f;
            }

            // napisy lezace wzdluz drog, zawsze czytelne (nie do gory nogami)
            Vector3 right = cam.transform.right; right.y = 0f;
            for (int i = 0; i < labels.Count; i++)
            {
                Vector3 dir = labelDirs[i];
                if (Vector3.Dot(dir, right) < 0f) dir = -dir;
                // TextMesh pisze wzdluz lokalnego X i czyta sie od strony -Z: Z w dol (lezy na mapie),
                // X wzdluz drogi, Y = Z x X (Unity jest lewoskretne).
                labels[i].transform.localRotation = Quaternion.LookRotation(Vector3.down, Vector3.Cross(Vector3.down, dir));
            }

            root.SetActive(true);
            try { cam.Render(); }
            finally { root.SetActive(false); }
            return true;
        }

        /// <summary>Wstega trasy na mapie; przejechana czesc przygaszona.</summary>
        private void BuildRoute(double radius, float mpp)
        {
            MeshFilter mf = route.GetComponent<MeshFilter>();
            Mesh m = mf.sharedMesh;
            m.Clear();
            NavRoute r = Navigation.Route;
            if (r == null || r.Points.Count < 2) return;

            float half = CanvasPx * canvasMpp * 0.5f;
            float w = Mathf.Max(3f, mpp * 7f) * 0.5f;
            var v = new List<Vector3>();
            var todo = new List<int>(); var done = new List<int>();
            for (int i = 0; i + 1 < r.Points.Count; i++)
            {
                Vector3 a = Local(r.Points[i].Lat, r.Points[i].Lon, radius, 0.8f);
                Vector3 b = Local(r.Points[i + 1].Lat, r.Points[i + 1].Lon, radius, 0.8f);
                if (Mathf.Abs(a.x) > half && Mathf.Abs(b.x) > half) continue;
                if (Mathf.Abs(a.z) > half && Mathf.Abs(b.z) > half) continue;
                Vector3 d = b - a; d.y = 0f;
                if (d.sqrMagnitude < 0.01f) continue;
                Vector3 s = new Vector3(d.z, 0f, -d.x).normalized * w;
                int k = v.Count;
                v.Add(a - s); v.Add(a + s); v.Add(b + s); v.Add(b - s);
                // przeguby: krotkie przedluzenie za punktem, zeby na zakretach nie bylo szczelin
                bool isDone = r.Cum[i + 1] <= Navigation.AlongM;
                (isDone ? done : todo).AddRange(new[] { k, k + 2, k + 1, k, k + 3, k + 2 });
                Vector3 e = d.normalized * w;
                int j = v.Count;
                v.Add(b - s - e * 0f); v.Add(b + s); v.Add(b + s + e); v.Add(b - s + e);
                (isDone ? done : todo).AddRange(new[] { j, j + 2, j + 1, j, j + 3, j + 2 });
            }
            m.SetVertices(v);
            m.subMeshCount = 2;
            m.SetTriangles(todo, 0);
            m.SetTriangles(done, 1);
            m.RecalculateBounds();
        }

        public void Clear()
        {
            cLat = double.NaN;
            job = null;
        }
    }
}
