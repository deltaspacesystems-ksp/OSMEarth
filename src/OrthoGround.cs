using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Ortofotomapa GUGiK na ziemi wokol kamery (tylko Polska).
    ///
    /// Satelita Mirage z bliska to kilka metrow na piksel - pole, miedza i podworko
    /// zlewaja sie w jedna plame. Geoportal udostepnia ortofotomape ~0.5-1 m/px przez
    /// otwarte WMTS (zestaw EPSG:4326, kafle 512 px, lewy gorny rog 56N 12E).
    ///
    /// Kazdy kafel WMTS to osobna plachta: siatka 17x17 w stopniach, przyklejona do
    /// terenu tym samym mechanizmem co drogi (GroundSnapper, rola Ground +10 cm, z
    /// kompensacja ugiecia z daleka). Kafle ciagniete w tle i trzymane na dysku
    /// (PluginData/orto), tekstury kompresowane do DXT1 (~128 KB na kafel).
    /// </summary>
    internal static class OrthoGround
    {
        public static bool Enabled = true;
        public static float Radius = 1200f;
        public static int Level = 13;              // 11 = 3.3 m/px, 12 = 1.3, 13 = 0.53, 14 = 0.26

        private const string Base = "https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WMTS/HighResolution";
        private const int TilePx = 512;
        private const int Grid = 17;
        // mianowniki skali z GetCapabilities (zestaw EPSG:4326 NIE jest poteguja dwojki)
        private static readonly double[] Scale = { 9449.423660750896, 4724.711830375448, 1889.884732150179, 944.9423660750895 };

        private sealed class Patch
        {
            public int Row, Col, Level;
            public double Lat, Lon, Alt;            // srodek (punkt zaczepienia)
            public GameObject Go;
            public Texture2D Tex;
            public Material Mat;
            public TerrainGrid Sag;
            public bool Snapped;
            public Color32[] Small;                 // 128x128 kopia do probkowania koloru (uprawy)
        }

        private const int SmallN = 128;

        /// <summary>Kolor zdjecia lotniczego w punkcie (jesli kafel jest wczytany).</summary>
        /// <summary>Zdjecia sa wlaczone i kamera jest nad Polska (sa albo zaraz beda).</summary>
        public static bool Wanted;

        public static bool SampleColor(double lat, double lon, out Color32 c)
        {
            c = new Color32(0, 0, 0, 0);
            if (live.Count == 0) return false;
            int level = Level;
            double td = TileDeg(level);
            int row = (int)Math.Floor((56.0 - lat) / td), col = (int)Math.Floor((lon - 12.0) / td);
            Patch p;
            if (!live.TryGetValue(Key(level, row, col), out p) || p.Small == null) return false;
            double fx = (lon - (12.0 + col * td)) / td, fy = ((56.0 - row * td) - lat) / td;     // fy od gory
            int x = Mathf.Clamp((int)(fx * SmallN), 0, SmallN - 1), y = Mathf.Clamp((int)((1.0 - fy) * SmallN), 0, SmallN - 1);
            c = p.Small[y * SmallN + x];
            return true;
        }

        private static readonly Dictionary<long, Patch> live = new Dictionary<long, Patch>();
        private static readonly HashSet<long> busy = new HashSet<long>();
        private static readonly HashSet<long> failed = new HashSet<long>();
        private static readonly Queue<KeyValuePair<long, byte[]>> ready = new Queue<KeyValuePair<long, byte[]>>();
        private static readonly Queue<long> todo = new Queue<long>();
        private static int running;
        private static bool loaded;
        private static Shader shader;
        public static string Report = "";

        private static double TileDeg(int level) { return Scale[level - 11] * 0.00028 / 111319.49079327357 * TilePx; }

        private static long Key(int level, int row, int col) { return ((long)level << 56) | ((long)row << 28) | (uint)col; }

        public static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            Enabled = OsmSettings.Get("orthoOn", 1f) > 0.5f;
            Radius = OsmSettings.Get("orthoRadius", Radius);
            Level = Mathf.Clamp((int)OsmSettings.Get("orthoLevel", Level), 11, 14);
        }

        public static bool SaveSettings()
        {
            bool ch = false;
            ch |= OsmSettings.Set("orthoOn", Enabled ? 1f : 0f);
            ch |= OsmSettings.Set("orthoRadius", Radius);
            ch |= OsmSettings.Set("orthoLevel", Level);
            return ch;
        }

        public static void DrawSettings()
        {
            EnsureLoaded();
            bool on = GUILayout.Toggle(Enabled, " aerial photo on the ground (Poland, GUGiK orthophoto)");
            if (on != Enabled) { Enabled = on; if (!on) Clear(); }
            if (!Enabled) return;
            GUILayout.Label(string.Format("Photo radius: {0:N0} m   detail: {1}", Radius,
                Level == 14 ? "0.26 m/px" : Level == 13 ? "0.53 m/px" : Level == 12 ? "1.3 m/px" : "3.3 m/px"));
            Radius = Mathf.Round(GUILayout.HorizontalSlider(Radius, 300f, 6000f) / 100f) * 100f;
            int lv = Mathf.RoundToInt(GUILayout.HorizontalSlider(Level, 11f, 14f));
            if (lv != Level) { Level = lv; Clear(); }
            if (Report.Length > 0) GUILayout.Label("   " + Report);
        }

        private static bool InPoland(double lat, double lon)
        {
            return lat > 48.95 && lat < 54.95 && lon > 14.0 && lon < 24.25;
        }

        private static string CachePath(int level, int row, int col)
        {
            return Path.Combine(KSPUtil.ApplicationRootPath,
                string.Format("GameData/OSMRoads/PluginData/orto/{0}/{1}/{2}.jpg", level, row, col));
        }

        // ------------------------------------------------------------------

        /// <summary>Raz na klatke z OsmRoadsAddon.LateUpdate.</summary>
        public static void Update(CelestialBody body, Vector3d eye, int layer, float sagK)
        {
            EnsureLoaded();
            if (!Enabled || body == null || body.pqsController == null) { Wanted = false; if (live.Count > 0) Clear(); return; }

            double lat = body.GetLatitude(eye), lon = body.GetLongitude(eye), alt = body.GetAltitude(eye);
            Wanted = InPoland(lat, lon);
            if (!Wanted || alt > Radius * 3.0 + 2000.0) { if (live.Count > 0) Clear(); return; }

            int level = Level;
            double td = TileDeg(level);
            double mLat = body.Radius * Math.PI / 180.0, mLon = mLat * Math.Cos(lat * Math.PI / 180.0);
            int r0 = (int)Math.Floor((56.0 - lat) / td), c0 = (int)Math.Floor((lon - 12.0) / td);
            int nr = (int)Math.Ceiling(Radius / (td * mLat)) + 1, nc = (int)Math.Ceiling(Radius / (td * mLon)) + 1;

            // potrzebne kafle, najblizsze najpierw
            var want = new List<KeyValuePair<double, long>>();
            for (int dr = -nr; dr <= nr; dr++)
                for (int dc = -nc; dc <= nc; dc++)
                {
                    int row = r0 + dr, col = c0 + dc;
                    double cLat = 56.0 - (row + 0.5) * td, cLon = 12.0 + (col + 0.5) * td;
                    double d = Math.Sqrt(Sq((cLat - lat) * mLat) + Sq((cLon - lon) * mLon));
                    if (d > Radius) continue;
                    want.Add(new KeyValuePair<double, long>(d, Key(level, row, col)));
                }
            want.Sort((a, b) => a.Key.CompareTo(b.Key));

            // wyladowanie dalekich (z histereza)
            var drop = new List<long>();
            foreach (KeyValuePair<long, Patch> kv in live)
            {
                Patch p = kv.Value;
                double d = Math.Sqrt(Sq((p.Lat - lat) * mLat) + Sq((p.Lon - lon) * mLon));
                if (p.Level != level || d > Radius * 1.35 + td * mLat) drop.Add(kv.Key);
            }
            foreach (long k in drop) { Destroy(live[k]); live.Remove(k); }

            // kolejka pobierania
            foreach (KeyValuePair<double, long> w in want)
            {
                long k = w.Value;
                if (live.ContainsKey(k) || busy.Contains(k) || failed.Contains(k)) continue;
                busy.Add(k);
                lock (todo) todo.Enqueue(k);
            }
            while (running < 4)
            {
                long k;
                lock (todo) { if (todo.Count == 0) break; k = todo.Dequeue(); }
                Interlocked.Increment(ref running);
                long kk = k;
                ThreadPool.QueueUserWorkItem(_ => Fetch(kk));
            }

            // gotowe kafle -> plachty (najwyzej 2 na klatke: LoadImage + Compress kosztuja)
            for (int n = 0; n < 2; n++)
            {
                KeyValuePair<long, byte[]> item;
                lock (ready) { if (ready.Count == 0) break; item = ready.Dequeue(); }
                busy.Remove(item.Key);
                if (item.Value == null) { failed.Add(item.Key); continue; }
                int lv = (int)(item.Key >> 56), row = (int)((item.Key >> 28) & 0xFFFFFFF), col = (int)(uint)(item.Key & 0xFFFFFFF);
                if (lv != level) continue;
                Patch p = Build(body, lv, row, col, item.Value, layer);
                if (p == null) { failed.Add(item.Key); continue; }
                live[item.Key] = p;
            }

            // polozenie wzgledem obracajacej sie planety + przyklejanie
            foreach (Patch p in live.Values)
            {
                Place(body, p);
                if (!p.Snapped && GroundSnapper.Pending < 4)
                {
                    p.Snapped = true;
                    double camLat = lat, camLon = lon, camAlt = alt;
                    Vessel v = FlightGlobals.ActiveVessel;
                    GroundSnapper.RequestRoads(p.Go, body, p.Lat, p.Lon, p.Alt, body.Radius, 0f, p.Sag, sagK,
                        camLat, camLon, camAlt,
                        v != null ? v.latitude : 0, v != null ? v.longitude : 0, v != null ? v.altitude : 0, null);
                }
            }
            Report = string.Format("{0} photo tiles, {1} loading", live.Count, busy.Count);
        }

        /// <summary>Po zmianie siatki PQS (straznik terenu w OsmRoadsAddon) - przyklej wszystko od nowa.</summary>
        public static void Resnap()
        {
            foreach (Patch p in live.Values) p.Snapped = false;
        }

        private static double Sq(double x) { return x * x; }

        private static void Fetch(long key)
        {
            byte[] data = null;
            try
            {
                int lv = (int)(key >> 56), row = (int)((key >> 28) & 0xFFFFFFF), col = (int)(uint)(key & 0xFFFFFFF);
                string path = CachePath(lv, row, col);
                if (File.Exists(path)) data = File.ReadAllBytes(path);
                else
                {
                    string url = Base + "?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER=ORTOFOTOMAPA&STYLE=default" +
                                 "&TILEMATRIXSET=EPSG:4326&TILEMATRIX=EPSG:4326:" + lv + "&TILEROW=" + row + "&TILECOL=" + col +
                                 "&FORMAT=image/jpeg";
                    for (int attempt = 0; attempt < 3 && data == null; attempt++)
                    {
                        try
                        {
                            var req = (HttpWebRequest)WebRequest.Create(url);
                            req.UserAgent = "OSMRoads-KSP";
                            req.Timeout = 20000;
                            using (var resp = (HttpWebResponse)req.GetResponse())
                            {
                                if (resp.ContentType == null || !resp.ContentType.StartsWith("image")) break;
                                using (Stream s = resp.GetResponseStream())
                                using (var ms = new MemoryStream())
                                {
                                    s.CopyTo(ms);
                                    data = ms.ToArray();
                                }
                            }
                        }
                        catch (Exception) { Thread.Sleep(1000 * (attempt + 1)); }
                    }
                    if (data != null && data.Length > 1000)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllBytes(path, data);
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[OSMRoads] orto: " + e.Message); data = null; }
            lock (ready) ready.Enqueue(new KeyValuePair<long, byte[]>(key, data != null && data.Length > 1000 ? data : null));
            Interlocked.Decrement(ref running);
        }

        private static Patch Build(CelestialBody body, int level, int row, int col, byte[] jpg, int layer)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, true);
            if (!tex.LoadImage(jpg)) { UnityEngine.Object.Destroy(tex); return null; }
            // poza zasiegiem zdjec serwer oddaje biale/puste kafle
            Color32[] px = tex.GetPixels32();
            long sum = 0;
            int cnt = 0;
            for (int q = 0; q < px.Length; q += 97) { sum += px[q].r + px[q].g + px[q].b; cnt++; }
            if (cnt == 0 || sum / (3.0 * cnt) > 247.0) { UnityEngine.Object.Destroy(tex); return null; }
            // mala kopia (srednia z blokow 4x4) - tekstura po kompresji nie jest juz czytelna
            var small = new Color32[SmallN * SmallN];
            int W = tex.width, H = tex.height;
            for (int y = 0; y < SmallN; y++)
                for (int x = 0; x < SmallN; x++)
                {
                    int sx = x * W / SmallN, sy = y * H / SmallN, r = 0, g = 0, bb = 0, nn = 0;
                    for (int dy = 0; dy < 4 && sy + dy < H; dy++)
                        for (int dx = 0; dx < 4 && sx + dx < W; dx++)
                        {
                            Color32 q = px[(sy + dy) * W + sx + dx];
                            r += q.r; g += q.g; bb += q.b; nn++;
                        }
                    small[y * SmallN + x] = nn > 0 ? new Color32((byte)(r / nn), (byte)(g / nn), (byte)(bb / nn), 255) : new Color32();
                }
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.anisoLevel = 4;
            tex.Compress(true);
            tex.Apply(true, true);

            double td = TileDeg(level);
            double north = 56.0 - row * td, west = 12.0 + col * td;
            var p = new Patch { Row = row, Col = col, Level = level, Lat = north - td * 0.5, Lon = west + td * 0.5, Tex = tex, Small = small };
            p.Alt = TerrainGrid.RawAltitude(body, p.Lat, p.Lon);
            double R = body.Radius + p.Alt;

            var box = new BBox { South = north - td, North = north, West = west, East = west + td };
            p.Sag = TerrainGrid.Create(box, 9, 0.30);
            System.Collections.IEnumerator it = p.Sag.Sample(body, 64);
            while (it.MoveNext()) { }

            var verts = new Vector3[Grid * Grid];
            var uvs = new Vector2[Grid * Grid];
            var uv2 = new Vector2[Grid * Grid];
            for (int j = 0; j < Grid; j++)
                for (int i = 0; i < Grid; i++)
                {
                    double la = north - td + td * j / (Grid - 1), lo = west + td * i / (Grid - 1);
                    double e, n, drop;
                    Geo.ToTangent(la, lo, p.Lat, p.Lon, R, out e, out n, out drop);
                    double h = TerrainGrid.RawAltitude(body, la, lo) - p.Alt - drop + 0.1;
                    int k = j * Grid + i;
                    verts[k] = new Vector3((float)e, (float)h, (float)n);
                    uvs[k] = new Vector2(i / (float)(Grid - 1), j / (float)(Grid - 1));
                    uv2[k] = RoadMeshBuilder.Ground(0.10f);
                }
            var tris = new int[(Grid - 1) * (Grid - 1) * 6];
            int t = 0;
            for (int j = 0; j + 1 < Grid; j++)
                for (int i = 0; i + 1 < Grid; i++)
                {
                    int a = j * Grid + i, b = a + 1, c = a + Grid, d = c + 1;
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }
            var mesh = new Mesh { name = "OSMOrtho" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.uv2 = uv2;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            if (shader == null) shader = Shader.Find("Standard") ?? Shader.Find("KSP/Diffuse");
            p.Mat = new Material(shader) { name = "OSMOrtho", mainTexture = tex };
            if (p.Mat.HasProperty("_Glossiness")) p.Mat.SetFloat("_Glossiness", 0.05f);
            if (p.Mat.HasProperty("_Metallic")) p.Mat.SetFloat("_Metallic", 0f);

            p.Go = new GameObject("OSMOrtho[" + row + "," + col + "]");
            p.Go.layer = layer;
            p.Go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = p.Go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = p.Mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = true;
            Place(body, p);
            return p;
        }

        private static void Place(CelestialBody b, Patch p)
        {
            if (p.Go == null) return;
            Vector3d origin = b.GetWorldSurfacePosition(p.Lat, p.Lon, p.Alt);
            Vector3d up = (origin - b.position).normalized;
            Vector3d northPt = b.GetWorldSurfacePosition(p.Lat + 0.001, p.Lon, p.Alt);
            Vector3 fUp = (Vector3)up, fNorth = (Vector3)(northPt - origin);
            Vector3.OrthoNormalize(ref fUp, ref fNorth);
            p.Go.transform.position = origin;
            p.Go.transform.rotation = Quaternion.LookRotation(fNorth, fUp);
        }

        private static void Destroy(Patch p)
        {
            if (p.Go != null)
            {
                MeshFilter mf = p.Go.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) UnityEngine.Object.Destroy(mf.sharedMesh);
                UnityEngine.Object.Destroy(p.Go);
            }
            if (p.Mat != null) UnityEngine.Object.Destroy(p.Mat);
            if (p.Tex != null) UnityEngine.Object.Destroy(p.Tex);
        }

        public static void Clear()
        {
            foreach (Patch p in live.Values) Destroy(p);
            live.Clear();
            busy.Clear();
            lock (todo) todo.Clear();
            lock (ready) ready.Clear();
            Report = "";
        }
    }
}
