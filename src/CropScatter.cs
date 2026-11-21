using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Zboze, kukurydza, rzepak, jeczmien i trawa jako rosliny 3D wokol kamery.
    ///
    /// Gdzie: tylko w obszarach OSM landuse=farmland (pola) i trawiastych (laki, parki),
    /// z dala od drog i domow (te same przeszkody co dla drzew). Co: z koloru zdjecia
    /// lotniczego GUGiK pod punktem - zlote = dojrzale zboze, zolte = rzepak, ciemna
    /// zielen = kukurydza, jasna zielen = mlode zboze / trawa, braz = zaorane (nic).
    /// Dzieki temu pas po pasie wyglada jak prawdziwa szachownica polskich pol, a nie
    /// jak jedno pole z OSM pomalowane jednym kolorem.
    ///
    /// Geometria: kwadraty 48 m, kazdy to jedna siatka na rodzaj rosliny (tysiace kepek
    /// sklejonych w jeden mesh - bez pracy CPU co klatke). Wysokosc z kolidera PQS
    /// (siatka 9x9 promieni na kwadrat, miedzy nimi interpolacja).
    /// </summary>
    internal static class CropScatter
    {
        public static bool Enabled = true;
        public static float Radius = 160f;
        public static float Density = 1f;

        private const float Chunk = 48f;
        private const int Kinds = 6;     // 0 trawa, 1 zboze zielone, 2 zboze dojrzale, 3 jeczmien/sciernisko, 4 kukurydza, 5 rzepak

        private static Material[] mats;
        private static bool loaded;

        internal sealed class TileCrops
        {
            public List<CropArea> Areas;
            public Obstacles Blocked;
            public readonly Dictionary<long, Mesh[]> Chunks = new Dictionary<long, Mesh[]>();
        }

        public static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            Enabled = OsmSettings.Get("cropsOn", 1f) > 0.5f;
            Radius = OsmSettings.Get("cropsRadius", Radius);
            Density = OsmSettings.Get("cropsDensity", Density);
        }

        public static bool SaveSettings()
        {
            bool ch = false;
            ch |= OsmSettings.Set("cropsOn", Enabled ? 1f : 0f);
            ch |= OsmSettings.Set("cropsRadius", Radius);
            ch |= OsmSettings.Set("cropsDensity", Density);
            return ch;
        }

        public static bool SettingsChanged;

        public static void DrawSettings()
        {
            EnsureLoaded();
            bool on = GUILayout.Toggle(Enabled, " crops and grass in 3D (wheat, barley, corn, rapeseed)");
            if (on != Enabled) { Enabled = on; SettingsChanged = true; }
            if (!Enabled) return;
            GUILayout.Label(string.Format("Crops radius: {0:N0} m   density: {1:F2}x", Radius, Density));
            Radius = Mathf.Round(GUILayout.HorizontalSlider(Radius, 40f, 600f) / 10f) * 10f;
            float dn = Mathf.Round(GUILayout.HorizontalSlider(Density, 0.25f, 2f) * 20f) / 20f;
            if (Math.Abs(dn - Density) > 1e-3f) { Density = dn; SettingsChanged = true; }
        }

        // ------------------------------------------------------------------
        //  Dane z watku roboczego (TileJob)
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
        //  Glowny watek
        // ------------------------------------------------------------------

        /// <summary>Buduje brakujace kwadraty w zasiegu (najwyzej jeden na wywolanie) i rysuje kafel.</summary>
        public static void UpdateTile(TileCrops tc, Transform root, CelestialBody body, double lat0, double lon0, double alt0,
                                      TerrainGrid grid, Vector3 camLocal, ref int budget, int layer)
        {
            EnsureLoaded();
            if (tc == null || tc.Areas == null) return;
            if (!Enabled || tc.Areas.Count == 0) { Release(tc); return; }
            Ensure();

            // kwadraty w zasiegu (po odleglosci w poziomie; kamera wysoko = nic)
            float rad = Radius;
            if (camLocal.y > rad * 1.5f + 30f) { Release(tc); return; }
            int cx0 = (int)Math.Floor((camLocal.x - rad) / Chunk), cx1 = (int)Math.Floor((camLocal.x + rad) / Chunk);
            int cz0 = (int)Math.Floor((camLocal.z - rad) / Chunk), cz1 = (int)Math.Floor((camLocal.z + rad) / Chunk);

            // za daleko - zwolnij
            var dropList = new List<long>();
            foreach (long k in tc.Chunks.Keys)
            {
                int kx = (int)(k >> 32), kz = (int)(uint)k;
                float mx = (kx + 0.5f) * Chunk - camLocal.x, mz = (kz + 0.5f) * Chunk - camLocal.z;
                if (mx * mx + mz * mz > (rad * 1.3f + Chunk) * (rad * 1.3f + Chunk)) dropList.Add(k);
            }
            foreach (long k in dropList) { Free(tc.Chunks[k]); tc.Chunks.Remove(k); }

            Matrix4x4 m = root.localToWorldMatrix;
            for (int cz = cz0; cz <= cz1; cz++)
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    float mx = (cx + 0.5f) * Chunk - camLocal.x, mz = (cz + 0.5f) * Chunk - camLocal.z;
                    if (mx * mx + mz * mz > (rad + Chunk * 0.7f) * (rad + Chunk * 0.7f)) continue;
                    long key = ((long)cx << 32) | (uint)cz;
                    Mesh[] ms;
                    if (!tc.Chunks.TryGetValue(key, out ms))
                    {
                        if (budget <= 0) continue;
                        budget--;
                        bool ready;
                        ms = BuildChunk(tc, cx, cz, body, lat0, lon0, alt0, grid, out ready);
                        if (!ready) continue;              // zdjecie lotnicze jeszcze sie wczytuje - sprobuj pozniej
                        tc.Chunks[key] = ms;
                    }
                    if (ms == null) continue;
                    for (int k = 0; k < Kinds; k++)
                        if (ms[k] != null) Graphics.DrawMesh(ms[k], m, mats[k], layer, null, 0, null, false, true);
                }
        }

        public static void Release(TileCrops tc)
        {
            if (tc == null) return;
            foreach (Mesh[] ms in tc.Chunks.Values) Free(ms);
            tc.Chunks.Clear();
        }

        private static void Free(Mesh[] ms)
        {
            if (ms == null) return;
            foreach (Mesh x in ms) if (x != null) UnityEngine.Object.Destroy(x);
        }

        private static Mesh[] BuildChunk(TileCrops tc, int cx, int cz, CelestialBody body, double lat0, double lon0, double alt0,
                                         TerrainGrid grid, out bool ready)
        {
            ready = true;
            float x0 = cx * Chunk, z0 = cz * Chunk;

            // pola, ktore dotykaja kwadratu
            var hit = new List<CropArea>();
            foreach (CropArea a in tc.Areas)
                if (a.X1 >= x0 && a.X0 <= x0 + Chunk && a.Z1 >= z0 && a.Z0 <= z0 + Chunk) hit.Add(a);
            if (hit.Count == 0) return null;

            double R = body.Radius + alt0;

            // rodzaj rosliny bierzemy ze zdjecia - czekamy, az kafel zdjecia bedzie wczytany
            if (OrthoGround.Wanted)
            {
                double cla, clo;
                Color32 cc;
                Geo.FromTangent(x0 + Chunk * 0.5f, z0 + Chunk * 0.5f, lat0, lon0, R, out cla, out clo);
                if (!OrthoGround.SampleColor(cla, clo, out cc)) { ready = false; return null; }
            }

            // wysokosc: 9x9 promieni w kolider PQS, reszta z interpolacji
            const int HN = 9;
            var hh = new float[HN * HN];
            for (int j = 0; j < HN; j++)
                for (int i = 0; i < HN; i++)
                {
                    float x = x0 + Chunk * i / (HN - 1), z = z0 + Chunk * j / (HN - 1);
                    double la, lo;
                    Geo.FromTangent(x, z, lat0, lon0, R, out la, out lo);
                    double raw = grid != null ? grid.HeightAt(la, lo) : alt0, h;
                    if (!GroundSnapper.MeshAltitude(body, la, lo, raw, out h)) h = raw;
                    hh[j * HN + i] = (float)(h - alt0 - Geo.TangentDrop(x, z, R));
                }

            var verts = new List<Vector3>[Kinds];
            var uvs = new List<Vector2>[Kinds];
            var tris = new List<int>[Kinds];
            for (int k = 0; k < Kinds; k++) { verts[k] = new List<Vector3>(); uvs[k] = new List<Vector2>(); tris[k] = new List<int>(); }

            float step = 1.15f / Mathf.Sqrt(Mathf.Max(0.25f, Density));
            for (float z = z0 + step * 0.5f; z < z0 + Chunk; z += step)
                for (float x = x0 + step * 0.5f; x < x0 + Chunk; x += step)
                {
                    uint hsh = Hash((uint)(int)(x * 8f + 1e6f), (uint)(int)(z * 8f + 1e6f));
                    float jx = x + ((hsh % 1000u) / 1000f - 0.5f) * step * 0.9f;
                    float jz = z + (((hsh >> 10) % 1000u) / 1000f - 0.5f) * step * 0.9f;

                    CropArea area = null;
                    foreach (CropArea a in hit)
                        if (jx >= a.X0 && jx <= a.X1 && jz >= a.Z0 && jz <= a.Z1 && Inside(a.Poly, jx, jz)) { area = a; break; }
                    if (area == null) continue;
                    if (tc.Blocked != null && tc.Blocked.Hit(jx, jz)) continue;

                    double la, lo;
                    Geo.FromTangent(jx, jz, lat0, lon0, R, out la, out lo);
                    Color32 c;
                    int kind = OrthoGround.SampleColor(la, lo, out c) ? Classify(c, area.Farm) : (area.Farm ? 1 : 0);
                    if (kind < 0) continue;
                    // trawa rzadziej (i tak jest niska), kukurydza w rzedach co 0.75 m - tu po prostu gesciej
                    if (kind == 0 && (hsh >> 21) % 3u == 0u) continue;

                    float fx = (jx - x0) / Chunk * (HN - 1), fz = (jz - z0) / Chunk * (HN - 1);
                    int ix = Mathf.Clamp((int)fx, 0, HN - 2), iz = Mathf.Clamp((int)fz, 0, HN - 2);
                    float tx = fx - ix, tz = fz - iz;
                    float y = Mathf.Lerp(Mathf.Lerp(hh[iz * HN + ix], hh[iz * HN + ix + 1], tx),
                                         Mathf.Lerp(hh[(iz + 1) * HN + ix], hh[(iz + 1) * HN + ix + 1], tx), tz);

                    AddTuft(verts[kind], uvs[kind], tris[kind], new Vector3(jx, y - 0.05f, jz), kind, hsh);
                }

            var res = new Mesh[Kinds];
            bool any = false;
            for (int k = 0; k < Kinds; k++)
            {
                if (tris[k].Count == 0) continue;
                var mesh = new Mesh { name = "OSMCrops" + k };
                if (verts[k].Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(verts[k]);
                mesh.SetUVs(0, uvs[k]);
                var nrm = new Vector3[verts[k].Count];
                for (int i = 0; i < nrm.Length; i++) nrm[i] = Vector3.up;
                mesh.normals = nrm;
                mesh.SetTriangles(tris[k], 0);
                mesh.RecalculateBounds();
                res[k] = mesh;
                any = true;
            }
            return any ? res : null;
        }

        /// <summary>Rodzaj rosliny z koloru zdjecia; -1 = nic (goła ziemia, droga, cien, las).</summary>
        private static int Classify(Color32 c, bool farm)
        {
            float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
            float mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            float v = mx, s = mx > 1e-3f ? (mx - mn) / mx : 0f;
            float h;
            if (mx - mn < 1e-3f) h = 0f;
            else if (mx == r) h = 60f * (((g - b) / (mx - mn)) % 6f);
            else if (mx == g) h = 60f * ((b - r) / (mx - mn) + 2f);
            else h = 60f * ((r - g) / (mx - mn) + 4f);
            if (h < 0f) h += 360f;

            if (v < 0.16f) return -1;                              // cien, ciemny las
            if (s < 0.10f) return -1;                              // szare: droga, dach, beton
            if (!farm) return h > 60f && h < 170f ? 0 : -1;        // laka: tylko tam, gdzie zielono
            if (h >= 45f && h < 68f && s > 0.45f && v > 0.62f) return 5;          // rzepak w kwiecie
            if (h >= 68f && h < 170f)
            {
                if (v < 0.33f) return 4;                           // ciemna zielen: kukurydza
                return v > 0.52f && h < 95f ? 0 : 1;               // jasna: trawa/koniczyna, reszta: mlode zboze
            }
            if (h >= 28f && h < 68f)
            {
                if (v > 0.58f) return s > 0.30f ? 2 : 3;           // zloto: dojrzale zboze, blade: jeczmien/sciernisko
                return -1;                                        // brazowe: zaorane
            }
            return -1;
        }

        private static bool Inside(Vector2[] q, float x, float z)
        {
            bool inside = false;
            for (int i = 0, j = q.Length - 1; i < q.Length; j = i++)
                if ((q[i].y > z) != (q[j].y > z) && x < (q[j].x - q[i].x) * (z - q[i].y) / (q[j].y - q[i].y) + q[i].x)
                    inside = !inside;
            return inside;
        }

        // ------------------------------------------------------------------
        //  Kepki
        // ------------------------------------------------------------------

        // wysokosc [m], szerokosc karty [m], liczba kart
        private static readonly float[] KH = { 0.32f, 0.55f, 0.95f, 0.80f, 2.3f, 1.25f };
        private static readonly float[] KW = { 0.55f, 0.55f, 0.60f, 0.60f, 0.95f, 0.75f };
        private static readonly int[] KC = { 2, 2, 3, 2, 3, 3 };

        private static void AddTuft(List<Vector3> v, List<Vector2> uv, List<int> t, Vector3 p, int kind, uint h)
        {
            float sc = 0.8f + ((h >> 4) % 100u) / 250f;
            float hgt = KH[kind] * sc, w = KW[kind] * sc * 0.5f;
            int cards = KC[kind];
            float yaw = ((h >> 12) % 360u) * Mathf.Deg2Rad;
            float u0 = ((h >> 7) % 4u) * 0.25f;                   // jeden z 4 wariantow tekstury
            for (int c = 0; c < cards; c++)
            {
                float a = yaw + c * Mathf.PI / cards;
                var d = new Vector3(Mathf.Cos(a) * w, 0f, Mathf.Sin(a) * w);
                var lean = new Vector3(((h >> (c + 3)) % 7u - 3f) * 0.02f, 0f, ((h >> (c + 9)) % 7u - 3f) * 0.02f) * hgt;
                int b = v.Count;
                v.Add(p - d); v.Add(p + d); v.Add(p + d + Vector3.up * hgt + lean); v.Add(p - d + Vector3.up * hgt + lean);
                uv.Add(new Vector2(u0, 0f)); uv.Add(new Vector2(u0 + 0.25f, 0f)); uv.Add(new Vector2(u0 + 0.25f, 1f)); uv.Add(new Vector2(u0, 1f));
                t.Add(b); t.Add(b + 1); t.Add(b + 2); t.Add(b); t.Add(b + 2); t.Add(b + 3);
                t.Add(b); t.Add(b + 2); t.Add(b + 1); t.Add(b); t.Add(b + 3); t.Add(b + 2);
            }
        }

        private static void Ensure()
        {
            if (mats != null) return;
            Shader sh = Shader.Find("Standard") ?? Shader.Find("KSP/Alpha/Cutoff") ?? Shader.Find("Legacy Shaders/Transparent/Cutout/Diffuse");
            mats = new Material[Kinds];
            Color[] tint =
            {
                new Color(0.42f, 0.60f, 0.24f),   // trawa
                new Color(0.46f, 0.64f, 0.26f),   // mlode zboze
                new Color(0.86f, 0.73f, 0.40f),   // dojrzala pszenica
                new Color(0.80f, 0.74f, 0.52f),   // jeczmien / sciernisko
                new Color(0.26f, 0.44f, 0.16f),   // kukurydza
                Color.white                       // rzepak: kolory lodyg i kwiatow w teksturze
            };
            for (int k = 0; k < Kinds; k++)
            {
                var m = new Material(sh) { name = "OSMCrop" + k };
                m.mainTexture = MakeTexture(k);
                m.color = tint[k];
                if (m.HasProperty("_Mode"))
                {
                    m.SetFloat("_Mode", 1f);
                    m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                    m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                    m.SetInt("_ZWrite", 1);
                    m.EnableKeyword("_ALPHATEST_ON");
                    m.renderQueue = 2450;
                }
                if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.4f);
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.05f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                mats[k] = m;
            }
        }

        /// <summary>Tekstura 4 wariantow kepki obok siebie (u po 0.25): zdzbla, klosy, liscie, kwiaty.</summary>
        private static Texture2D MakeTexture(int kind)
        {
            const int W = 256, H = 128;
            var px = new Color32[W * H];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(120, 140, 80, 0);
            var rnd = new System.Random(1000 + kind);
            int blades = kind == 4 ? 7 : kind == 0 ? 40 : 26;
            for (int varnt = 0; varnt < 4; varnt++)
            {
                int ox = varnt * 64;
                for (int bI = 0; bI < blades; bI++)
                {
                    float x0 = 6 + (float)rnd.NextDouble() * 52f;
                    float lean = ((float)rnd.NextDouble() - 0.5f) * (kind == 4 ? 34f : 18f);
                    float top = H * (0.70f + 0.30f * (float)rnd.NextDouble());
                    float wid = kind == 4 ? 3.2f : kind == 0 ? 1.1f : 1.3f;
                    byte shade = (byte)(185 + rnd.Next(70));
                    for (int y = 0; y < (int)top; y++)
                    {
                        float t = y / top;
                        float xc = x0 + lean * t * t;
                        float ww = wid * (1f - 0.7f * t) + (kind == 4 ? 4f * (float)Math.Sin(t * 3.1f) : 0f);
                        for (int x = (int)(xc - ww); x <= (int)(xc + ww); x++)
                        {
                            if (x < 0 || x >= 64) continue;
                            byte g = (byte)(shade * (0.72f + 0.28f * t));
                            px[y * W + ox + x] = kind == 5 ? new Color32((byte)(g * 0.42f), (byte)(g * 0.62f), (byte)(g * 0.24f), 255)
                                                           : new Color32(g, g, g, 255);
                        }
                    }
                    // klos (zboza), kwiatostan (rzepak)
                    if (kind == 2 || kind == 3 || kind == 1 || kind == 5)
                    {
                        float ex = x0 + lean;
                        int eh = kind == 5 ? 16 : 13;
                        for (int y = (int)top - eh; y < (int)top + (kind == 5 ? 4 : 1); y++)
                            for (int x = (int)(ex - 2.4f); x <= (int)(ex + 2.4f); x++)
                            {
                                if (x < 0 || x >= 64 || y < 0 || y >= H) continue;
                                Color32 ec = kind == 5 ? new Color32(255, 236, 60, 255)
                                           : kind == 1 ? new Color32(210, 236, 180, 255)
                                                       : new Color32(255, 246, 214, 255);
                                if (kind == 5 && rnd.NextDouble() < 0.25) continue;
                                px[y * W + ox + x] = ec;
                            }
                    }
                }
            }
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, true) { name = "OSMCropTex" + kind, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        private static uint Hash(uint x, uint y)
        {
            uint h = x * 374761393u + y * 668265263u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }
}
