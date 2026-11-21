using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMView
{
    /// <summary>Tekstura z lancuchem mipmap liczonym tak jak w Unity (usrednianie 2x2).</summary>
    internal sealed class Tex
    {
        public readonly List<int> W = new List<int>(), H = new List<int>();
        public readonly List<float[]> Px = new List<float[]>();   // RGBA 0..1
        public bool Repeat;

        public static Tex From(Texture2D t)
        {
            var x = new Tex { Repeat = t.wrapMode == TextureWrapMode.Repeat };
            int w = t.width, h = t.height;
            var p = new float[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                Color32 c = t.Pixels[i];
                p[i * 4] = c.r / 255f; p[i * 4 + 1] = c.g / 255f; p[i * 4 + 2] = c.b / 255f;
                p[i * 4 + 3] = t.Format == TextureFormat.RGB24 ? 1f : c.a / 255f;
            }
            x.W.Add(w); x.H.Add(h); x.Px.Add(p);

            if (t.Mips.Count > 0)
            {
                // Mipmapy podane przez kod moda (np. drzewa z zachowanym pokryciem alfy).
                foreach (Color32[] mp in t.Mips)
                {
                    w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
                    var q = new float[w * h * 4];
                    for (int i = 0; i < w * h; i++)
                    {
                        q[i * 4] = mp[i].r / 255f; q[i * 4 + 1] = mp[i].g / 255f;
                        q[i * 4 + 2] = mp[i].b / 255f; q[i * 4 + 3] = mp[i].a / 255f;
                    }
                    x.W.Add(w); x.H.Add(h); x.Px.Add(q);
                }
                return x;
            }

            while (t.HasMips && (w > 1 || h > 1))
            {
                int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
                var q = new float[nw * nh * 4];
                for (int yy = 0; yy < nh; yy++)
                    for (int xx = 0; xx < nw; xx++)
                        for (int c = 0; c < 4; c++)
                        {
                            float s = 0;
                            for (int dy = 0; dy < 2; dy++)
                                for (int dx = 0; dx < 2; dx++)
                                {
                                    int sx = Math.Min(w - 1, xx * 2 + dx), sy = Math.Min(h - 1, yy * 2 + dy);
                                    s += p[(sy * w + sx) * 4 + c];
                                }
                            q[(yy * nw + xx) * 4 + c] = s * 0.25f;
                        }
                w = nw; h = nh; p = q;
                x.W.Add(w); x.H.Add(h); x.Px.Add(p);
            }
            return x;
        }

        public void Sample(float u, float v, float level, out float r, out float g, out float b, out float a)
        {
            int l = (int)Math.Round(Math.Max(0, Math.Min(W.Count - 1, level)));
            int w = W[l], h = H[l];
            float[] p = Px[l];

            float fx = u * w - 0.5f, fy = v * h - 0.5f;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            float tx = fx - x0, ty = fy - y0;

            r = g = b = a = 0;
            for (int k = 0; k < 4; k++)
            {
                int xi = x0 + (k & 1), yi = y0 + (k >> 1);
                float wgt = ((k & 1) != 0 ? tx : 1 - tx) * ((k >> 1) != 0 ? ty : 1 - ty);
                if (Repeat)
                {
                    xi %= w; if (xi < 0) xi += w;
                    yi %= h; if (yi < 0) yi += h;
                }
                else
                {
                    xi = xi < 0 ? 0 : (xi >= w ? w - 1 : xi);
                    yi = yi < 0 ? 0 : (yi >= h ? h - 1 : yi);
                }
                int o = (yi * w + xi) * 4;
                r += p[o] * wgt; g += p[o + 1] * wgt; b += p[o + 2] * wgt; a += p[o + 3] * wgt;
            }
        }
    }

    internal sealed class Mat
    {
        public Color Tint = Color.white;
        public Tex Albedo;
        public bool Cutout;
        public float Cutoff = 0.45f;
        public bool TwoSided;
        public bool UpNormal;      // korony drzew: oswietlenie z normalnej w gore, jak w ProceduralTree
        public string Name;
    }

    /// <summary>
    /// Programowy rasteryzer: bufor glebi, korekcja perspektywy UV, mipmapy,
    /// alfa-cutout i odrzucanie tylnych scian wg konwencji Unity.
    ///
    /// Konwencja ma znaczenie: w Unity przednia sciana to trojkat widziany
    /// zgodnie z ruchem wskazowek zegara. Gdyby tester rysowal wszystko
    /// dwustronnie, zle nawiniete sciany - ktore w grze znikaja - tu bylyby widoczne.
    /// </summary>
    internal sealed class Raster
    {
        public readonly int W, H;
        public readonly byte[] Rgb;
        private readonly float[] depth;   // 1/z; wieksze = blizej

        private Vector3 camPos, right, up, fwd;
        private float focal;
        public Vector3 Sun = new Vector3(0.45f, 0.78f, 0.43f).normalized;
        public float Ambient = 0.42f, Diffuse = 0.70f;
        public Color Sky = new Color(0.66f, 0.76f, 0.88f);
        public float FogDist = 9000f;

        public long Tris, Drawn, Culled;

        public Raster(int w, int h)
        {
            W = w; H = h;
            Rgb = new byte[w * h * 3];
            depth = new float[w * h];
        }

        public void Camera(Vector3 pos, Vector3 target, float hFovDeg)
        {
            camPos = pos;
            fwd = (target - pos).normalized;
            Vector3 worldUp = Math.Abs(fwd.y) > 0.98f ? new Vector3(0, 0, 1) : Vector3.up;
            right = Vector3.Cross(worldUp, fwd).normalized;
            up = Vector3.Cross(fwd, right).normalized;
            focal = (W * 0.5f) / (float)Math.Tan(hFovDeg * Math.PI / 360.0);
        }

        public void Clear()
        {
            for (int y = 0; y < H; y++)
            {
                float t = y / (float)H;
                byte r = (byte)(255 * (Sky.r * (1 - 0.25f * t))), g = (byte)(255 * (Sky.g * (1 - 0.18f * t))), b = (byte)(255 * Sky.b);
                for (int x = 0; x < W; x++)
                {
                    int o = (y * W + x) * 3;
                    Rgb[o] = r; Rgb[o + 1] = g; Rgb[o + 2] = b;
                }
            }
            Array.Clear(depth, 0, depth.Length);
        }

        private struct CV { public float x, y, z, u, v; }   // wierzcholek w ukladzie kamery

        /// <summary>Rysuje trojkaty; world(i) zwraca pozycje w ukladzie sceny.</summary>
        public void Draw(Vector3[] world, Vector2[] uv, int[] tris, Mat m)
        {
            var poly = new CV[4];
            var tmp = new CV[3];

            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                Tris++;
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                Vector3 w0 = world[i0], w1 = world[i1], w2 = world[i2];

                Vector3 n = Vector3.Cross(w1 - w0, w2 - w0);
                if (n.sqrMagnitude < 1e-12f) continue;
                n = n.normalized;

                for (int k = 0; k < 3; k++)
                {
                    Vector3 wv = k == 0 ? w0 : (k == 1 ? w1 : w2);
                    int ix = k == 0 ? i0 : (k == 1 ? i1 : i2);
                    Vector3 d = wv - camPos;
                    tmp[k].x = Vector3.Dot(d, right);
                    tmp[k].y = Vector3.Dot(d, up);
                    tmp[k].z = Vector3.Dot(d, fwd);
                    tmp[k].u = uv != null && uv.Length > ix ? uv[ix].x : 0;
                    tmp[k].v = uv != null && uv.Length > ix ? uv[ix].y : 0;
                }

                // --- przyciecie do plaszczyzny bliskiej ---
                const float near = 0.25f;
                int pc = 0;
                for (int k = 0; k < 3; k++)
                {
                    CV a = tmp[k], b = tmp[(k + 1) % 3];
                    bool ain = a.z >= near, bin = b.z >= near;
                    if (ain) poly[pc++] = a;
                    if (ain != bin)
                    {
                        float s = (near - a.z) / (b.z - a.z);
                        poly[pc++] = new CV
                        {
                            x = a.x + (b.x - a.x) * s, y = a.y + (b.y - a.y) * s, z = near,
                            u = a.u + (b.u - a.u) * s, v = a.v + (b.v - a.v) * s
                        };
                    }
                }
                if (pc < 3) continue;

                // --- tyl czy przod (Unity: zgodnie z zegarem = przod) ---
                float sx0 = W * 0.5f + focal * poly[0].x / poly[0].z, sy0 = H * 0.5f - focal * poly[0].y / poly[0].z;
                float sx1 = W * 0.5f + focal * poly[1].x / poly[1].z, sy1 = H * 0.5f - focal * poly[1].y / poly[1].z;
                float sx2 = W * 0.5f + focal * poly[2].x / poly[2].z, sy2 = H * 0.5f - focal * poly[2].y / poly[2].z;
                float area = (sx1 - sx0) * (sy2 - sy0) - (sy1 - sy0) * (sx2 - sx0);
                bool front = area > 0;
                if (!front && !m.TwoSided) { Culled++; continue; }

                Vector3 ln = m.UpNormal ? Vector3.up : (front ? n : -n);
                float light = Ambient + Diffuse * Math.Max(0f, Vector3.Dot(ln, Sun));

                for (int f = 1; f + 1 < pc; f++)
                    Fill(poly[0], poly[f], poly[f + 1], m, light);
                Drawn++;
            }
        }

        private void Fill(CV a, CV b, CV c, Mat m, float light)
        {
            float ax = W * 0.5f + focal * a.x / a.z, ay = H * 0.5f - focal * a.y / a.z;
            float bx = W * 0.5f + focal * b.x / b.z, by = H * 0.5f - focal * b.y / b.z;
            float cx = W * 0.5f + focal * c.x / c.z, cy = H * 0.5f - focal * c.y / c.z;

            float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (Math.Abs(area) < 1e-6f) return;

            int minX = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx))));
            int maxX = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
            int minY = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy))));
            int maxY = Math.Min(H - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
            if (minX > maxX || minY > maxY) return;

            float iza = 1f / a.z, izb = 1f / b.z, izc = 1f / c.z;
            float ua = a.u * iza, ub = b.u * izb, uc = c.u * izc;
            float va = a.v * iza, vb = b.v * izb, vc = c.v * izc;

            // Poziom mipmapy na trojkat: teksele na piksel (bez anizotropii).
            float mip = 0;
            if (m.Albedo != null)
            {
                float tw = m.Albedo.W[0], th = m.Albedo.H[0];
                float uvArea = Math.Abs((b.u - a.u) * (c.v - a.v) - (b.v - a.v) * (c.u - a.u)) * tw * th;
                float ratio = uvArea / Math.Abs(area);
                mip = ratio > 1 ? 0.5f * (float)Math.Log(ratio, 2) : 0;
            }

            float dist = (a.z + b.z + c.z) / 3f;
            float fog = 1f - (float)Math.Exp(-dist / FogDist);

            float inv = 1f / area;
            for (int y = minY; y <= maxY; y++)
            {
                float py = y + 0.5f;
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f;
                    float w0 = ((bx - px) * (cy - py) - (by - py) * (cx - px)) * inv;
                    float w1 = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) * inv;
                    float w2 = 1f - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;

                    float iz = w0 * iza + w1 * izb + w2 * izc;
                    int di = y * W + x;
                    if (iz <= depth[di]) continue;

                    float r = m.Tint.r, g = m.Tint.g, bl = m.Tint.b;
                    if (m.Albedo != null)
                    {
                        float u = (w0 * ua + w1 * ub + w2 * uc) / iz;
                        float v = (w0 * va + w1 * vb + w2 * vc) / iz;
                        float tr, tg, tb, ta;
                        // v odwrocone: w Unity v=0 to dol tekstury, a tablica pikseli
                        // zaczyna sie od dolnego wiersza - wiec indeks wiersza = v.
                        m.Albedo.Sample(u, v, mip, out tr, out tg, out tb, out ta);
                        if (m.Cutout && ta < m.Cutoff) continue;
                        r *= tr; g *= tg; bl *= tb;
                    }

                    depth[di] = iz;
                    r = r * light * (1 - fog) + Sky.r * fog;
                    g = g * light * (1 - fog) + Sky.g * fog;
                    bl = bl * light * (1 - fog) + Sky.b * fog;

                    int o = di * 3;
                    Rgb[o] = (byte)Math.Min(255, r * 255);
                    Rgb[o + 1] = (byte)Math.Min(255, g * 255);
                    Rgb[o + 2] = (byte)Math.Min(255, bl * 255);
                }
            }
        }
    }
}
