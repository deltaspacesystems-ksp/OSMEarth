using System;
using System.Collections.Generic;

// Zamienniki GL / RenderTexture / GUI - tyle, ile uzywa planer mapy.
// GL rysuje programowo do bufora aktywnej RenderTexture, a GUI.DrawTexture
// sklada tekstury na "ekran" z mieszaniem alfy. Dzieki temu MapRenderer
// i MapTileCache kompiluja sie tu bez jednej zmiany.

namespace UnityEngine.Rendering
{
    public enum BlendMode { Zero = 0, One = 1, SrcAlpha = 5, OneMinusSrcAlpha = 10 }
    public enum CullMode { Off = 0, Front = 1, Back = 2 }
}

namespace UnityEngine
{
    public enum HideFlags { None = 0, HideAndDontSave = 61 }
    public enum RenderTextureFormat { ARGB32 = 0, Default = 7 }

    public class Object
    {
        public static void Destroy(Object o) { }
    }

    public class Texture : Object
    {
        public int width, height;
    }

    public class Shader : Object
    {
        public string name;
        public static Shader Find(string n) { return new Shader { name = n }; }
    }

    public class Material : Object
    {
        public Material(Shader s) { }
        public HideFlags hideFlags;
        public Texture mainTexture;
        public bool HasProperty(string n) { return true; }
        public void SetInt(string n, int v) { }
        public bool SetPass(int pass) { GL.CurrentTexture = mainTexture as Texture2D; return true; }
    }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float w, float h) { this.x = x; this.y = y; width = w; height = h; }
        public float xMax { get { return x + width; } }
        public float yMax { get { return y + height; } }
        public Vector2 center { get { return new Vector2(x + width * 0.5f, y + height * 0.5f); } }
    }

    public struct Matrix4x4 { public float Angle; }
    public enum FontStyle { Normal, Bold }
    public enum TextAnchor { MiddleCenter = 4 }
    public enum TextClipping { Overflow, Clip }

    public class GUIStyleState { public Color textColor; }
    public class GUIContent { public string text; public GUIContent(string t) { text = t; } }

    public class GUIStyle
    {
        public int fontSize = 12;
        public FontStyle fontStyle;
        public TextAnchor alignment;
        public TextClipping clipping;
        public bool wordWrap;
        public GUIStyleState normal = new GUIStyleState();
        public Vector2 CalcSize(GUIContent c)
        {
            return new Vector2(c.text.Length * fontSize * (fontStyle == FontStyle.Bold ? 0.6f : 0.55f), fontSize * 1.3f);
        }
    }

    public static class GUIUtility
    {
        public static void RotateAroundPivot(float angle, Vector2 pivot) { GUI.matrix = new Matrix4x4 { Angle = angle }; }
    }

    public class RenderTexture : Texture
    {
        public FilterMode filterMode;
        public TextureWrapMode wrapMode;
        public byte[] Data;                   // RGBA, wiersz 0 = GORA (jak po LoadPixelMatrix)

        public static RenderTexture active;
        public static long Created;

        public RenderTexture(int w, int h, int depth) : this(w, h, depth, RenderTextureFormat.ARGB32) { }
        public RenderTexture(int w, int h, int depth, RenderTextureFormat fmt)
        {
            width = w; height = h;
            Data = new byte[w * h * 4];
        }
        public bool Create() { Created++; return true; }
        public void Release() { }
    }

    /// <summary>GL w trybie natychmiastowym rasteryzowany do RenderTexture.active.</summary>
    public static class GL
    {
        public const int TRIANGLES = 4, QUADS = 7, LINES = 1;

        public static long VertexCalls;       // miara kosztu CPU - w Unity kazde GL.Vertex3 to wywolanie natywne
        public static Texture2D CurrentTexture;
        private static float tu, tv;
        private static readonly List<float> vu = new List<float>(), vv = new List<float>();
        private static int mode;
        private static UnityEngine.Color cur = UnityEngine.Color.white;
        private static readonly List<float> vx = new List<float>(), vy = new List<float>();
        private static readonly List<UnityEngine.Color> vc = new List<UnityEngine.Color>();

        public static void PushMatrix() { }
        public static void PopMatrix() { }
        public static void LoadPixelMatrix(float l, float r, float b, float t) { }

        public static void Clear(bool depth, bool color, UnityEngine.Color c)
        {
            RenderTexture rt = RenderTexture.active;
            if (rt == null || !color) return;
            byte R = B(c.r), G = B(c.g), Bl = B(c.b), A = B(c.a);
            for (int i = 0; i < rt.Data.Length; i += 4)
            {
                rt.Data[i] = R; rt.Data[i + 1] = G; rt.Data[i + 2] = Bl; rt.Data[i + 3] = A;
            }
        }

        public static void Begin(int m) { mode = m; vx.Clear(); vy.Clear(); vc.Clear(); vu.Clear(); vv.Clear(); }
        public static void Color(UnityEngine.Color c) { cur = c; }
        public static void TexCoord2(float u, float v) { tu = u; tv = v; }

        public static void Vertex3(float x, float y, float z)
        {
            VertexCalls++;
            vx.Add(x); vy.Add(y); vc.Add(cur); vu.Add(tu); vv.Add(tv);
            int per = mode == QUADS ? 4 : (mode == TRIANGLES ? 3 : 2);
            if (vx.Count < per) return;

            RenderTexture rt = RenderTexture.active;
            if (rt != null)
            {
                if (mode == TRIANGLES) Tri(rt, 0, 1, 2);
                else if (mode == QUADS) { Tri(rt, 0, 1, 2); Tri(rt, 0, 2, 3); }
            }
            vx.Clear(); vy.Clear(); vc.Clear(); vu.Clear(); vv.Clear();
        }

        public static void End() { vx.Clear(); vy.Clear(); vc.Clear(); vu.Clear(); vv.Clear(); }

        private static byte B(float v) { return (byte)Math.Max(0, Math.Min(255, v * 255f)); }

        private static void Tri(RenderTexture rt, int i0, int i1, int i2)
        {
            float ax = vx[i0], ay = vy[i0], bx = vx[i1], by = vy[i1], cx = vx[i2], cy = vy[i2];
            float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (Math.Abs(area) < 1e-6f) return;

            int minX = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx))));
            int maxX = Math.Min(rt.width - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
            int minY = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy))));
            int maxY = Math.Min(rt.height - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
            if (minX > maxX || minY > maxY) return;

            UnityEngine.Color c = vc[i0];
            float a = Math.Max(0f, Math.Min(1f, c.a));
            float inv = 1f / area;

            for (int y = minY; y <= maxY; y++)
            {
                float py = y + 0.5f;
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f;
                    float w0 = ((bx - px) * (cy - py) - (by - py) * (cx - px)) * inv;
                    float w1 = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) * inv;
                    if (w0 < 0 || w1 < 0 || 1f - w0 - w1 < 0) continue;

                    int o = (y * rt.width + x) * 4;
                    float cr = c.r, cg = c.g, cb = c.b;
                    Texture2D tex = CurrentTexture;
                    if (tex != null)
                    {
                        float w2 = 1f - w0 - w1;
                        float u = vu[i0] * w0 + vu[i1] * w1 + vu[i2] * w2;
                        float v = vv[i0] * w0 + vv[i1] * w1 + vv[i2] * w2;
                        int sx = Math.Max(0, Math.Min(tex.width - 1, (int)(u * tex.width)));
                        int sy = Math.Max(0, Math.Min(tex.height - 1, (int)(v * tex.height)));
                        Color32 t = tex.Pixels[sy * tex.width + sx];
                        cr *= t.r / 255f; cg *= t.g / 255f; cb *= t.b / 255f;
                    }
                    rt.Data[o] = (byte)(rt.Data[o] * (1 - a) + cr * 255f * a);
                    rt.Data[o + 1] = (byte)(rt.Data[o + 1] * (1 - a) + cg * 255f * a);
                    rt.Data[o + 2] = (byte)(rt.Data[o + 2] * (1 - a) + cb * 255f * a);
                    rt.Data[o + 3] = (byte)Math.Min(255, rt.Data[o + 3] + a * 255f * (1 - rt.Data[o + 3] / 255f));
                }
            }
        }
    }

    /// <summary>GUI.DrawTexture skladajace tekstury na ekran z mieszaniem alfy.</summary>
    public static class GUI
    {
        public static Color color = Color.white;
        public static Matrix4x4 matrix;
        public static long LabelCalls;
        public static byte[] Screen;

        /// <summary>Napis jako polprzezroczysty prostokat w kolorze tekstu - w testerze
        /// wystarcza, zeby sprawdzic rozmieszczenie i nachodzenie etykiet.</summary>
        public static void Label(Rect r, string text, GUIStyle st)
        {
            LabelCalls++;
            Rect g = groups.Count > 0 ? groups.Peek() : new Rect(0, 0, ScreenW, ScreenH);
            Color c = st.normal.textColor;
            float a = c.a * 0.35f;
            int x0 = (int)Math.Max(0, g.x + r.x), x1 = (int)Math.Min(ScreenW, g.x + r.x + r.width);
            int y0 = (int)Math.Max(0, g.y + r.y), y1 = (int)Math.Min(ScreenH, g.y + r.y + r.height);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int o = (y * ScreenW + x) * 3;
                    Screen[o] = (byte)(Screen[o] * (1 - a) + c.r * 255f * a);
                    Screen[o + 1] = (byte)(Screen[o + 1] * (1 - a) + c.g * 255f * a);
                    Screen[o + 2] = (byte)(Screen[o + 2] * (1 - a) + c.b * 255f * a);
                }
        }
        public static int ScreenW, ScreenH;
        public static long DrawCalls;

        private static readonly Stack<Rect> groups = new Stack<Rect>();

        public static void SetScreen(int w, int h)
        {
            ScreenW = w; ScreenH = h;
            Screen = new byte[w * h * 3];
            groups.Clear();
        }

        public static void BeginGroup(Rect r)
        {
            Rect parent = groups.Count > 0 ? groups.Peek() : new Rect(0, 0, ScreenW, ScreenH);
            groups.Push(new Rect(parent.x + r.x, parent.y + r.y, r.width, r.height));
        }

        public static void EndGroup() { groups.Pop(); }

        public static void DrawTexture(Rect r, Texture t)
        {
            DrawTextureWithTexCoords(r, t, new Rect(0, 0, 1, 1));
        }

        public static void DrawTextureWithTexCoords(Rect r, Texture t, Rect uv)
        {
            DrawCalls++;
            Rect g = groups.Count > 0 ? groups.Peek() : new Rect(0, 0, ScreenW, ScreenH);
            float sx0 = g.x + r.x, sy0 = g.y + r.y;

            int x0 = (int)Math.Max(Math.Floor(sx0), g.x), x1 = (int)Math.Min(Math.Ceiling(sx0 + r.width), g.x + g.width);
            int y0 = (int)Math.Max(Math.Floor(sy0), g.y), y1 = (int)Math.Min(Math.Ceiling(sy0 + r.height), g.y + g.height);
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
            x1 = Math.Min(ScreenW, x1); y1 = Math.Min(ScreenH, y1);

            for (int y = y0; y < y1; y++)
            {
                float fy = (y + 0.5f - sy0) / r.height;            // 0 = gora prostokata
                float v = uv.y + (1f - fy) * uv.height;             // v rosnie w gore
                for (int x = x0; x < x1; x++)
                {
                    float fx = (x + 0.5f - sx0) / r.width;
                    float u = uv.x + fx * uv.width;

                    float cr, cg, cb, ca;
                    Sample(t, u, v, out cr, out cg, out cb, out ca);
                    cr *= color.r; cg *= color.g; cb *= color.b; ca *= color.a;

                    int o = (y * ScreenW + x) * 3;
                    Screen[o] = (byte)(Screen[o] * (1 - ca) + cr * 255f * ca);
                    Screen[o + 1] = (byte)(Screen[o + 1] * (1 - ca) + cg * 255f * ca);
                    Screen[o + 2] = (byte)(Screen[o + 2] * (1 - ca) + cb * 255f * ca);
                }
            }
        }

        private static void Sample(Texture t, float u, float v, out float r, out float g, out float b, out float a)
        {
            int w = t.width, h = t.height;
            float fx = u * w - 0.5f, fy;
            var rt = t as RenderTexture;
            // RenderTexture: wiersz 0 = gora, wiec v=1 to wiersz 0. Texture2D: wiersz 0 = dol.
            fy = rt != null ? (1f - v) * h - 0.5f : v * h - 0.5f;

            int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
            float tx = fx - ix, ty = fy - iy;
            r = g = b = a = 0;
            for (int k = 0; k < 4; k++)
            {
                int xi = Math.Max(0, Math.Min(w - 1, ix + (k & 1)));
                int yi = Math.Max(0, Math.Min(h - 1, iy + (k >> 1)));
                float wt = ((k & 1) != 0 ? tx : 1 - tx) * ((k >> 1) != 0 ? ty : 1 - ty);
                if (rt != null)
                {
                    int o = (yi * w + xi) * 4;
                    r += rt.Data[o] / 255f * wt; g += rt.Data[o + 1] / 255f * wt;
                    b += rt.Data[o + 2] / 255f * wt; a += rt.Data[o + 3] / 255f * wt;
                }
                else
                {
                    Color32 c = ((Texture2D)t).Pixels[yi * w + xi];
                    r += c.r / 255f * wt; g += c.g / 255f * wt; b += c.b / 255f * wt; a += c.a / 255f * wt;
                }
            }
        }
    }
}
