using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Swiatlo latarni w nocy, w trzech warstwach od najtanszej:
    ///
    ///  1. Plama na jezdni pod kazda latarnia - czesc siatki drogi (RoadMeshBuilder.SubLampPool),
    ///     wiec przykleja sie do terenu razem z nia. Material addytywny, zero kosztu za dnia.
    ///  2. Poswiata klosza - skrzyzowane plaszczyzny z miekka tekstura, rozmiar rosnie z
    ///     odlegloscia kafla, zeby siatke ulic bylo widac z samolotu na wiele km (zasieg z UI).
    ///  3. Prawdziwe swiatla Unity dla kilkunastu latarni najblizej kamery - oswietlaja
    ///     pojazd i elewacje. Wiecej nie ma sensu: w forward renderingu KSP kazde swiatlo
    ///     to kolejne przejscie po siatkach, ktorych dotyka.
    ///
    /// Do tego luna nad miastem: miekki krazek na wysokosci chmur, jasnosc z liczby latarni
    /// i budynkow kafla - od spodu rozjasnia niebo nad miastem jak swiatlo odbite od chmur.
    /// </summary>
    internal static class LampLights
    {
        // --- ustawienia (OsmSettings.Num) ---
        public static bool Enabled = true;
        public static float GlowDist = 25000f;      // poswiata kloszy
        public static int RealCount = 12;           // prawdziwe swiatla przy kamerze
        public static float Brightness = 1f;
        public static float SkyGlow = 1f;           // luna nad miastami (0 = wylaczona)

        private static bool loaded;
        private static float night;

        private static Material poolMat, glowMat, skyMat;
        private static Texture2D radial;

        public static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            Enabled = OsmSettings.Get("lightsOn", 1f) > 0.5f;
            GlowDist = OsmSettings.Get("lightsGlowDist", GlowDist);
            RealCount = (int)OsmSettings.Get("lightsReal", RealCount);
            Brightness = OsmSettings.Get("lightsBright", Brightness);
            SkyGlow = OsmSettings.Get("lightsSky", SkyGlow);
        }

        public static bool SaveSettings()
        {
            bool ch = false;
            ch |= OsmSettings.Set("lightsOn", Enabled ? 1f : 0f);
            ch |= OsmSettings.Set("lightsGlowDist", GlowDist);
            ch |= OsmSettings.Set("lightsReal", RealCount);
            ch |= OsmSettings.Set("lightsBright", Brightness);
            ch |= OsmSettings.Set("lightsSky", SkyGlow);
            return ch;
        }

        public static void DrawSettings()
        {
            EnsureLoaded();
            Enabled = GUILayout.Toggle(Enabled, " street lighting at night (light pools, glow, real lights)");
            if (!Enabled) return;
            GUILayout.Label(string.Format("Lamp glow range: {0:N0} m", GlowDist));
            GlowDist = Mathf.Round(GUILayout.HorizontalSlider(GlowDist, 1000f, 90000f) / 500f) * 500f;
            GUILayout.Label(string.Format("Real lights near camera: {0}", RealCount));
            RealCount = Mathf.RoundToInt(GUILayout.HorizontalSlider(RealCount, 0f, 48f));
            GUILayout.Label(string.Format("Light brightness: {0:F2}x", Brightness));
            Brightness = GUILayout.HorizontalSlider(Brightness, 0.2f, 3f);
            GUILayout.Label(string.Format("City sky glow (light on clouds): {0:F2}x", SkyGlow));
            SkyGlow = GUILayout.HorizontalSlider(SkyGlow, 0f, 3f);
        }

        // ------------------------------------------------------------------
        //  Materialy
        // ------------------------------------------------------------------

        private static Shader Additive()
        {
            string[] names = { "KSP/Particles/Additive", "Legacy Shaders/Particles/Additive", "Particles/Additive", "Mobile/Particles/Additive" };
            foreach (string n in names)
            {
                Shader s = Shader.Find(n);
                if (s != null) return s;
            }
            return null;
        }

        private static Texture2D Radial()
        {
            if (radial != null) return radial;
            const int S = 64;
            radial = new Texture2D(S, S, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "OSMLampRadial" };
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = (x + 0.5f) / S * 2f - 1f, dy = (y + 0.5f) / S * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float v = Mathf.Clamp01(1f - r);
                    v = v * v * (3f - 2f * v);                  // miekkie brzegi
                    v = v * v;
                    byte b = (byte)(255f * v);
                    px[y * S + x] = new Color32(b, b, b, b);
                }
            radial.SetPixels32(px);
            radial.Apply(true, true);
            return radial;
        }

        private static Material Make(string name, int queue)
        {
            Shader sh = Additive();
            if (sh == null) return null;
            var m = new Material(sh) { name = name };
            m.mainTexture = Radial();
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", Color.black);
            m.color = Color.black;
            m.renderQueue = queue;
            return m;
        }

        public static Material PoolMaterial
        {
            get
            {
                if (poolMat == null) poolMat = Make("OSMLampPool", 3000);
                return poolMat;
            }
        }

        private static Material GlowMaterial
        {
            get
            {
                if (glowMat == null) glowMat = Make("OSMLampGlow", 3001);
                return glowMat;
            }
        }

        private static Material SkyMaterial
        {
            get
            {
                if (skyMat == null) skyMat = Make("OSMSkyGlow", 2999);
                return skyMat;
            }
        }

        private static void Tint(Material m, Color c)
        {
            if (m == null) return;
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", c);
            m.color = c;
        }

        /// <summary>0 = dzien, 1 = noc. Raz na klatke.</summary>
        public static void SetNight(float n)
        {
            EnsureLoaded();
            night = Enabled ? n : 0f;
            float k = night * Brightness;
            // sod: cieply pomaranczowy; addytywny shader czastek mnozy przez 2
            Tint(PoolMaterial, new Color(1.0f, 0.62f, 0.26f, 1f) * (0.30f * k));
            Tint(GlowMaterial, new Color(1.0f, 0.70f, 0.38f, 1f) * (0.55f * k));
            Tint(SkyMaterial, new Color(1.0f, 0.55f, 0.25f, 1f) * (0.06f * night * SkyGlow));
        }

        public static bool Night { get { return night > 0.02f; } }

        // ------------------------------------------------------------------
        //  Per kafel: poswiata i luna
        // ------------------------------------------------------------------

        internal sealed class TileLights
        {
            public Vector3[] Heads;          // lokalnie w kaflu
            public int Buildings;
            public Mesh Glow;
            public float GlowSize;           // rozmiar, z jakim zbudowano Glow
            public Mesh Sky;
        }

        public static TileLights Make(List<Vector3> heads, int buildings)
        {
            if ((heads == null || heads.Count == 0) && buildings < 30) return null;
            return new TileLights { Heads = heads != null ? heads.ToArray() : new Vector3[0], Buildings = buildings };
        }

        /// <summary>Rysuje poswiate kloszy i lune kafla. d = odleglosc kafla od kamery.</summary>
        public static void Draw(TileLights t, Matrix4x4 tileToWorld, float d, int layer)
        {
            if (t == null || !Night) return;

            if (t.Heads.Length > 0 && d <= GlowDist && GlowMaterial != null)
            {
                // rozmiar poswiaty skokami x1.5 - siatka przebudowuje sie rzadko
                float want = Mathf.Clamp(d * 0.0016f, 0.7f, 40f);
                if (t.Glow == null || want > t.GlowSize * 1.5f || want < t.GlowSize / 1.5f)
                {
                    if (t.Glow != null) UnityEngine.Object.Destroy(t.Glow);
                    t.Glow = GlowMesh(t.Heads, want);
                    t.GlowSize = want;
                }
                Graphics.DrawMesh(t.Glow, tileToWorld, GlowMaterial, layer);
            }

            if (SkyGlow > 0.01f && SkyMaterial != null)
            {
                if (t.Sky == null) t.Sky = SkyMesh(t);
                if (t.Sky != null) Graphics.DrawMesh(t.Sky, tileToWorld, SkyMaterial, layer);
            }
        }

        public static void Destroy(TileLights t)
        {
            if (t == null) return;
            if (t.Glow != null) UnityEngine.Object.Destroy(t.Glow);
            if (t.Sky != null) UnityEngine.Object.Destroy(t.Sky);
        }

        /// <summary>Trzy skrzyzowane kwadraty na klosz: widac je z kazdej strony bez obracania co klatke.</summary>
        private static Mesh GlowMesh(Vector3[] heads, float size)
        {
            int n = Math.Min(heads.Length, 5000);
            var v = new Vector3[n * 12];
            var uv = new Vector2[n * 12];
            var tri = new int[n * 18];
            Vector3[] ax = { Vector3.right, Vector3.forward, Vector3.up };
            for (int i = 0; i < n; i++)
            {
                Vector3 c = heads[i];
                for (int q = 0; q < 3; q++)
                {
                    Vector3 a = ax[q] * size, b = ax[(q + 1) % 3] * size;
                    int o = i * 12 + q * 4;
                    v[o] = c - a - b; v[o + 1] = c + a - b; v[o + 2] = c + a + b; v[o + 3] = c - a + b;
                    uv[o] = new Vector2(0, 0); uv[o + 1] = new Vector2(1, 0); uv[o + 2] = new Vector2(1, 1); uv[o + 3] = new Vector2(0, 1);
                    int t = i * 18 + q * 6;
                    tri[t] = o; tri[t + 1] = o + 1; tri[t + 2] = o + 2;
                    tri[t + 3] = o; tri[t + 4] = o + 2; tri[t + 5] = o + 3;
                }
            }
            var m = new Mesh { name = "OSMLampGlow" };
            if (v.Length > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.vertices = v;
            m.uv = uv;
            m.triangles = tri;
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Luna: poziomy krazek ~1.2 km nad kaflem, jasnosc z gestosci zabudowy i latarni.
        /// Od spodu (dwustronny) rozjasnia niebo nad miastem, z boku widac jasna kopule nad horyzontem.</summary>
        private static Mesh SkyMesh(TileLights t)
        {
            float weight = Mathf.Clamp01((t.Heads.Length * 1.5f + t.Buildings * 0.4f) / 1400f);
            if (weight < 0.05f) return null;
            // srodek jasnosci tam, gdzie sa latarnie
            Vector3 c = Vector3.zero;
            if (t.Heads.Length > 0)
            {
                foreach (Vector3 h in t.Heads) c += h;
                c /= t.Heads.Length;
            }
            c.y += 1200f;
            float r = 900f + 900f * weight;
            const int Seg = 24;
            var v = new Vector3[Seg + 1];
            var uv = new Vector2[Seg + 1];
            var col = new Color[Seg + 1];
            v[0] = c; uv[0] = new Vector2(0.5f, 0.5f); col[0] = Color.white * weight;
            for (int i = 0; i < Seg; i++)
            {
                float a = i * Mathf.PI * 2f / Seg;
                v[i + 1] = c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                uv[i + 1] = new Vector2(0.5f + Mathf.Cos(a) * 0.5f, 0.5f + Mathf.Sin(a) * 0.5f);
                col[i + 1] = Color.white * weight;
            }
            var tri = new int[Seg * 6];
            for (int i = 0; i < Seg; i++)
            {
                int a = i + 1, b = (i + 1) % Seg + 1;
                tri[i * 6] = 0; tri[i * 6 + 1] = a; tri[i * 6 + 2] = b;          // od gory
                tri[i * 6 + 3] = 0; tri[i * 6 + 4] = b; tri[i * 6 + 5] = a;      // od spodu
            }
            var m = new Mesh { name = "OSMSkyGlow", vertices = v, uv = uv, colors = col, triangles = tri };
            m.RecalculateBounds();
            return m;
        }

        // ------------------------------------------------------------------
        //  Prawdziwe swiatla przy kamerze
        // ------------------------------------------------------------------

        private static readonly List<Light> pool = new List<Light>();
        private static float nextPick;
        private struct Cand { public float D2; public Transform Root; public Vector3 Local; }
        private static readonly List<Cand> cand = new List<Cand>();

        /// <summary>Raz na klatke po przejsciu kafli: N swiatel na latarniach najblizszych kamerze.
        /// Swiatlo jest dzieckiem korzenia kafla, wiec jedzie razem z nim (floating origin).</summary>
        public static void UpdateReal(List<KeyValuePair<Transform, TileLights>> tiles, Vector3 eye)
        {
            int want = Night ? RealCount : 0;
            for (int i = pool.Count - 1; i >= 0; i--) if (pool[i] == null) pool.RemoveAt(i);   // zniknal z kaflem
            while (pool.Count < want)
            {
                var go = new GameObject("OSMLampLight");
                Light l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.range = 24f;
                l.color = new Color(1f, 0.74f, 0.45f);
                l.shadows = LightShadows.None;
                l.renderMode = LightRenderMode.ForcePixel;
                l.enabled = false;
                pool.Add(l);
            }
            for (int i = want; i < pool.Count; i++)
                if (pool[i].enabled) pool[i].enabled = false;
            if (want == 0 || Time.time < nextPick) return;
            nextPick = Time.time + 0.3f;

            cand.Clear();
            foreach (KeyValuePair<Transform, TileLights> kv in tiles)
            {
                if (kv.Value == null || kv.Key == null) continue;
                Matrix4x4 m = kv.Key.localToWorldMatrix;
                Vector3[] h = kv.Value.Heads;
                for (int i = 0; i < h.Length; i++)
                {
                    float d2 = (m.MultiplyPoint3x4(h[i]) - eye).sqrMagnitude;
                    if (d2 > 600f * 600f) continue;
                    cand.Add(new Cand { D2 = d2, Root = kv.Key, Local = h[i] });
                }
            }
            cand.Sort((a, b) => a.D2.CompareTo(b.D2));
            for (int i = 0; i < want; i++)
            {
                Light l = pool[i];
                if (i < cand.Count)
                {
                    if (l.transform.parent != cand[i].Root) l.transform.SetParent(cand[i].Root, false);
                    l.transform.localPosition = cand[i].Local;
                    l.intensity = 1.6f * Brightness * night;
                    l.enabled = true;
                }
                else l.enabled = false;
            }
        }

        public static void DisableAll()
        {
            foreach (Light l in pool) if (l != null) l.enabled = false;
        }
    }
}
