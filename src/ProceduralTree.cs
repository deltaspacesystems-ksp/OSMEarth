using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Drzewa generowane w kodzie: skrzyzowane karty na korone + ostroslup na pien.
    ///
    /// Dlaczego nie modele Parallaxa: ich meshe sa autorowane pod wlasny shader
    /// (BILLBOARD rozwija karty w vertex shaderze, skala idzie z configu Parallaxa,
    /// a albedo niekoniecznie siedzi pod _MainTex). Pod Graphics.DrawMeshInstanced
    /// ze zwyklym Standard wychodzily z tego gigantyczne biale plachty.
    /// Tutaj wszystko jest w metrach i pod naszym wlasnym materialem.
    /// </summary>
    public static class ProceduralTree
    {
        public const float TreeHeightM = 12f;

        private const int Cards = 3;          // skrzyzowane karty korony
        private const int TexSize = 512;

        /// <summary>Prog wyciecia alfy - musi byc ten sam w materiale i w mipmapach,
        /// bo mipmapy sa liczone tak, zeby przy TYM progu zachowac pokrycie.</summary>
        public const float LeafCutoff = 0.45f;

        /// <summary>
        /// Korona: Cards prostokatow obroconych wokol osi Y. Ksztalt robi ALFA
        /// tekstury, nie geometria - stad rozny obrys liscastego i iglastego
        /// przy tym samym meshu.
        /// </summary>
        public static Mesh MakeFoliage(bool conifer)
        {
            float h = conifer ? TreeHeightM * 0.78f : TreeHeightM * 0.62f;
            float w = conifer ? TreeHeightM * 0.42f : TreeHeightM * 0.68f;
            float baseY = conifer ? TreeHeightM * 0.20f : TreeHeightM * 0.36f;

            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var norms = new List<Vector3>();
            var tris = new List<int>();

            for (int c = 0; c < Cards; c++)
            {
                float a = Mathf.PI * c / Cards;
                float ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                Vector3 right = new Vector3(ca, 0f, sa) * (w * 0.5f);

                int v0 = verts.Count;
                verts.Add(new Vector3(-right.x, baseY, -right.z));
                verts.Add(new Vector3(right.x, baseY, right.z));
                verts.Add(new Vector3(right.x, baseY + h, right.z));
                verts.Add(new Vector3(-right.x, baseY + h, -right.z));

                uvs.Add(new Vector2(0f, 0f));
                uvs.Add(new Vector2(1f, 0f));
                uvs.Add(new Vector2(1f, 1f));
                uvs.Add(new Vector2(0f, 1f));

                // Normalne w gore zamiast prostopadle do karty: karta oswietlona
                // wlasna normalna robi ostry kontrast i widac, ze to plaskie plachty.
                for (int i = 0; i < 4; i++) norms.Add(Vector3.up);

                // obie strony - karte widac z kazdej strony
                tris.Add(v0); tris.Add(v0 + 1); tris.Add(v0 + 2);
                tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 3);
                tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 1);
                tris.Add(v0); tris.Add(v0 + 3); tris.Add(v0 + 2);
            }

            if (!conifer)
            {
                // Pozioma karta w polowie korony. Z samolotu drzewa ogladamy z gory,
                // a z gory trzy pionowe karty to trzy kreski z przerwami - las
                // wygladal jak rozsypane zapalki.
                // Mniejsza i wyzej niz srodek: z wysokosci oczu cala plachta wychodzila
                // jako pozioma kreska przez koronę, a jej brzeg wystawal pod galeziami.
                float y = baseY + h * 0.62f;
                float r = w * 0.30f;
                int v0 = verts.Count;
                verts.Add(new Vector3(-r, y, -r)); uvs.Add(new Vector2(0f, 0f));
                verts.Add(new Vector3(r, y, -r)); uvs.Add(new Vector2(1f, 0f));
                verts.Add(new Vector3(r, y, r)); uvs.Add(new Vector2(1f, 1f));
                verts.Add(new Vector3(-r, y, r)); uvs.Add(new Vector2(0f, 1f));
                for (int i = 0; i < 4; i++) norms.Add(Vector3.up);
                tris.Add(v0); tris.Add(v0 + 1); tris.Add(v0 + 2);
                tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 3);
                tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 1);
                tris.Add(v0); tris.Add(v0 + 3); tris.Add(v0 + 2);
            }

            var m = new Mesh { name = conifer ? "OSMFoliageConifer" : "OSMFoliageBroadleaf" };
            m.SetVertices(verts);
            m.SetUVs(0, uvs);
            m.SetNormals(norms);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Pien: pieciokatny ostroslup sciety, zwezajacy sie ku gorze.</summary>
        public static Mesh MakeTrunk(bool conifer)
        {
            const int sides = 5;
            float h = conifer ? TreeHeightM * 0.30f : TreeHeightM * 0.48f;
            float r0 = TreeHeightM * 0.030f;
            float r1 = TreeHeightM * 0.018f;

            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            for (int i = 0; i < sides; i++)
            {
                float a0 = Mathf.PI * 2f * i / sides;
                float a1 = Mathf.PI * 2f * (i + 1) / sides;

                Vector3 b0 = new Vector3(Mathf.Cos(a0) * r0, 0f, Mathf.Sin(a0) * r0);
                Vector3 b1 = new Vector3(Mathf.Cos(a1) * r0, 0f, Mathf.Sin(a1) * r0);
                Vector3 t0 = new Vector3(Mathf.Cos(a0) * r1, h, Mathf.Sin(a0) * r1);
                Vector3 t1 = new Vector3(Mathf.Cos(a1) * r1, h, Mathf.Sin(a1) * r1);

                int v0 = verts.Count;
                verts.Add(b0); verts.Add(b1); verts.Add(t1); verts.Add(t0);
                uvs.Add(new Vector2((float)i / sides, 0f));
                uvs.Add(new Vector2((i + 1f) / sides, 0f));
                uvs.Add(new Vector2((i + 1f) / sides, 1f));
                uvs.Add(new Vector2((float)i / sides, 1f));

                tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 1);
                tris.Add(v0); tris.Add(v0 + 3); tris.Add(v0 + 2);
            }

            var m = new Mesh { name = conifer ? "OSMTrunkConifer" : "OSMTrunkBroadleaf" };
            m.SetVertices(verts);
            m.SetUVs(0, uvs);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// Tekstura korony z ALFA. Sylwetke rysuje maska: elipsa dla liscastego,
        /// stozek z pietrami galezi dla iglastego.
        ///
        /// Wczesniej szum byl BLOKOWY (rzutowanie wspolrzednej na int, bez
        /// interpolacji) - brzeg korony liczyl sie z blokow po 11 px, a cien
        /// skupisk z blokow po 6 px. Z bliska dawalo to wyrazna kratke, czyli
        /// "piksele". Teraz caly szum jest wygladzony, a rozdzielczosc wieksza.
        /// </summary>
        public static Texture2D MakeFoliageTexture(bool conifer)
        {
            int S = TexSize;
            var pix = new Color32[S * S];

            for (int y = 0; y < S; y++)
            {
                float fy = y / (float)(S - 1);                    // 0 = dol korony
                for (int x = 0; x < S; x++)
                {
                    float fx = x / (float)(S - 1) * 2f - 1f;        // -1..1
                    int i = y * S + x;

                    float halfWidth;
                    if (conifer)
                    {
                        float tier = Mathf.Abs(Mathf.Sin(fy * 9f));
                        halfWidth = (1f - fy) * (0.70f + 0.30f * tier);
                    }
                    else
                    {
                        float t = (fy - 0.45f) / 0.55f;
                        halfWidth = Mathf.Sqrt(Mathf.Max(0f, 1f - t * t)) * 0.95f;
                    }

                    // Brzeg poszarpany gladko: dwie skale szumu, obie interpolowane.
                    float n = (Smooth(x, y, 40f) - 0.5f) * 0.22f
                            + (Smooth(x, y, 13f) - 0.5f) * 0.12f;
                    float d = Mathf.Abs(fx) / Mathf.Max(0.02f, halfWidth + n);

                    // Skupiska lisci: jasne kepy z przerwami, im blizej brzegu, tym
                    // wiecej dziur - od srodka korona jest pelna.
                    float clump = 0.6f * Smooth(x + 300, y + 300, 22f) + 0.4f * Smooth(x + 70, y + 40, 7f);
                    float hole = Smooth(x + 900, y + 100, 9f);
                    bool gap = d > 0.62f && hole < 0.18f + (d - 0.62f) * 0.9f;

                    if (d > 1f || gap)
                    {
                        pix[i] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    // Swiatlo z gory i z lewej, cien od spodu i we wnetrzu korony.
                    float shade = 0.52f + 0.40f * fy + 0.10f * (-fx);
                    float g = shade * (0.72f + 0.42f * clump);
                    g *= 0.86f + 0.14f * Smooth(x, y, 2.5f);    // drobna faktura lisci

                    if (conifer)
                        pix[i] = new Color32(B(52 * g), B(92 * g), B(50 * g), 255);
                    else
                        pix[i] = new Color32(B(78 * g), B(122 * g), B(46 * g), 255);
                }
            }

            return BuildCutout(S, S, pix);
        }

        private static byte B(float v) { return (byte)Mathf.Clamp(v, 0f, 255f); }

        /// <summary>
        /// Tekstura z recznie liczonymi mipmapami, ktore ZACHOWUJA pokrycie alfy.
        ///
        /// Zwykle mipmapy usredniaja alfe: na brzegach 255 i 0 daja 128, na
        /// kolejnych poziomach coraz mniej, az piksele spadaja pod prog wyciecia.
        /// Efekt w grze: korony przerzedzaja sie z odlegloscia, a las z daleka
        /// wyglada na rzadki. Tu dla kazdego poziomu dobieramy mnoznik alfy tak,
        /// zeby odsetek pikseli powyzej progu byl taki sam jak na poziomie 0.
        /// Kolor usredniamy z waga alfy, zeby puste piksele nie przyciemnialy brzegow.
        /// </summary>
        private static Texture2D BuildCutout(int w, int h, Color32[] pix)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, true);
            t.SetPixels32(pix, 0);

            byte cut = (byte)(LeafCutoff * 255f);
            float coverage = Coverage(pix, cut);

            Color32[] cur = pix;
            int cw = w, ch = h, level = 1;
            while (cw > 1 || ch > 1)
            {
                int nw = Mathf.Max(1, cw / 2), nh = Mathf.Max(1, ch / 2);
                var next = new Color32[nw * nh];
                var alpha = new float[nw * nh];

                for (int y = 0; y < nh; y++)
                    for (int x = 0; x < nw; x++)
                    {
                        float r = 0, g = 0, b = 0, a = 0, ar = 0, ag = 0, ab = 0;
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                Color32 c = cur[Mathf.Min(ch - 1, y * 2 + dy) * cw + Mathf.Min(cw - 1, x * 2 + dx)];
                                float ca = c.a / 255f;
                                r += c.r * ca; g += c.g * ca; b += c.b * ca; a += ca;
                                ar += c.r; ag += c.g; ab += c.b;
                            }
                        int o = y * nw + x;
                        alpha[o] = a * 0.25f;
                        next[o] = a > 1e-4f
                            ? new Color32(B(r / a), B(g / a), B(b / a), 0)
                            : new Color32(B(ar * 0.25f), B(ag * 0.25f), B(ab * 0.25f), 0);
                    }

                // Wyszukiwanie binarne mnoznika alfy pod zadane pokrycie.
                float lo = 0.5f, hi = 8f;
                for (int it = 0; it < 14; it++)
                {
                    float mid = (lo + hi) * 0.5f;
                    int above = 0;
                    for (int k = 0; k < alpha.Length; k++)
                        if (alpha[k] * mid * 255f >= cut) above++;
                    if (above / (float)alpha.Length < coverage) lo = mid; else hi = mid;
                }
                float scale = (lo + hi) * 0.5f;
                for (int k = 0; k < next.Length; k++)
                    next[k].a = B(alpha[k] * scale * 255f);

                t.SetPixels32(next, level);
                cur = next; cw = nw; ch = nh; level++;
            }

            // false: nie przeliczaj mipmap - nasze sa lepsze od domyslnych.
            t.Apply(false);
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Trilinear;
            t.anisoLevel = 4;
            return t;
        }

        private static float Coverage(Color32[] pix, byte cut)
        {
            int n = 0;
            for (int i = 0; i < pix.Length; i++) if (pix[i].a >= cut) n++;
            return n / (float)pix.Length;
        }

        /// <summary>Wygladzony szum wartosciowy o oczku "cell" pikseli, 0..1.</summary>
        private static float Smooth(float x, float y, float cell)
        {
            float gx = x / cell, gy = y / cell;
            int x0 = Mathf.FloorToInt(gx), y0 = Mathf.FloorToInt(gy);
            float tx = gx - x0, ty = gy - y0;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            float a = Noise01(x0, y0), b = Noise01(x0 + 1, y0);
            float c = Noise01(x0, y0 + 1), d = Noise01(x0 + 1, y0 + 1);
            float top = a + (b - a) * tx, bot = c + (d - c) * tx;
            return top + (bot - top) * ty;
        }

        /// <summary>Kora: pionowe smugi w brazie.</summary>
        public static Texture2D MakeBarkTexture()
        {
            const int w = 64, h = 256;
            var pix = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float v = 0.62f + 0.38f * Noise01(x * 0.7f, y * 0.09f);
                    pix[y * w + x] = new Color32((byte)(96 * v), (byte)(74 * v), (byte)(54 * v), 255);
                }
            return Build(w, h, pix, TextureFormat.RGBA32);
        }

        private static Texture2D Build(int w, int h, Color32[] pix, TextureFormat fmt)
        {
            var t = new Texture2D(w, h, fmt, true);
            t.SetPixels32(pix);
            t.Apply(true);
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            t.anisoLevel = 4;
            return t;
        }

        /// <summary>Tani szum wartosciowy w zakresie 0..1 - nie potrzebujemy tu Perlina.</summary>
        private static float Noise01(float x, float y)
        {
            int xi = (int)x, yi = (int)y;
            uint h = (uint)(xi * 374761393 + yi * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) % 256u) / 255f;
        }
    }
}
