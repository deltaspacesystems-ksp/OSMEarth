using System;
using System.Collections.Generic;

// Zamienniki typow Unity uzywanych przez buildery moda. Semantyka tam, gdzie
// ma znaczenie dla geometrii, jest taka jak w Unity: uklad lewoskretny,
// Cross wg wzoru standardowego, RecalculateNormals z cross(b-a, c-a).

public static class KSPUtil
{
    public static string ApplicationRootPath =
        Environment.GetEnvironmentVariable("OSM_ROOT") ?? @"C:\ksp for mod testing\";
}

namespace UnityEngine.Rendering
{
    public enum IndexFormat { UInt16, UInt32 }
}

namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }

        public static Vector2 zero { get { return new Vector2(0, 0); } }
        public static Vector2 one { get { return new Vector2(1, 1); } }
        public static Vector2 up { get { return new Vector2(0, 1); } }
        public static Vector2 right { get { return new Vector2(1, 0); } }

        public float magnitude { get { return (float)Math.Sqrt(x * x + y * y); } }
        public float sqrMagnitude { get { return x * x + y * y; } }
        public Vector2 normalized { get { float m = magnitude; return m > 1e-5f ? this / m : zero; } }
        public void Normalize() { this = normalized; }

        public static float Dot(Vector2 a, Vector2 b) { return a.x * b.x + a.y * b.y; }
        public static float Distance(Vector2 a, Vector2 b) { return (a - b).magnitude; }
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t) { t = Mathf.Clamp01(t); return a + (b - a) * t; }

        public static Vector2 operator +(Vector2 a, Vector2 b) { return new Vector2(a.x + b.x, a.y + b.y); }
        public static Vector2 operator -(Vector2 a, Vector2 b) { return new Vector2(a.x - b.x, a.y - b.y); }
        public static Vector2 operator -(Vector2 a) { return new Vector2(-a.x, -a.y); }
        public static Vector2 operator *(Vector2 a, float d) { return new Vector2(a.x * d, a.y * d); }
        public static Vector2 operator *(float d, Vector2 a) { return new Vector2(a.x * d, a.y * d); }
        public static Vector2 operator /(Vector2 a, float d) { return new Vector2(a.x / d, a.y / d); }
        public static bool operator ==(Vector2 a, Vector2 b) { return (a - b).sqrMagnitude < 1e-10f; }
        public static bool operator !=(Vector2 a, Vector2 b) { return !(a == b); }
        public override bool Equals(object o) { return o is Vector2 && this == (Vector2)o; }
        public override int GetHashCode() { return x.GetHashCode() ^ (y.GetHashCode() << 2); }

        public static implicit operator Vector2(Vector3 v) { return new Vector2(v.x, v.y); }
        public static implicit operator Vector3(Vector2 v) { return new Vector3(v.x, v.y, 0); }
        public override string ToString() { return string.Format("({0:F2}, {1:F2})", x, y); }
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }

        public static Vector3 zero { get { return new Vector3(0, 0, 0); } }
        public static Vector3 one { get { return new Vector3(1, 1, 1); } }
        public static Vector3 up { get { return new Vector3(0, 1, 0); } }
        public static Vector3 down { get { return new Vector3(0, -1, 0); } }
        public static Vector3 forward { get { return new Vector3(0, 0, 1); } }
        public static Vector3 back { get { return new Vector3(0, 0, -1); } }
        public static Vector3 right { get { return new Vector3(1, 0, 0); } }
        public static Vector3 left { get { return new Vector3(-1, 0, 0); } }

        public float magnitude { get { return (float)Math.Sqrt(x * x + y * y + z * z); } }
        public float sqrMagnitude { get { return x * x + y * y + z * z; } }
        public Vector3 normalized { get { float m = magnitude; return m > 1e-5f ? this / m : zero; } }
        public void Normalize() { this = normalized; }

        public static float Dot(Vector3 a, Vector3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
        public static Vector3 Cross(Vector3 a, Vector3 b)
        {
            return new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        }
        public static float Distance(Vector3 a, Vector3 b) { return (a - b).magnitude; }
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) { t = Mathf.Clamp01(t); return a + (b - a) * t; }
        public static Vector3 Scale(Vector3 a, Vector3 b) { return new Vector3(a.x * b.x, a.y * b.y, a.z * b.z); }

        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x - b.x, a.y - b.y, a.z - b.z); }
        public static Vector3 operator -(Vector3 a) { return new Vector3(-a.x, -a.y, -a.z); }
        public static Vector3 operator *(Vector3 a, float d) { return new Vector3(a.x * d, a.y * d, a.z * d); }
        public static Vector3 operator *(float d, Vector3 a) { return new Vector3(a.x * d, a.y * d, a.z * d); }
        public static Vector3 operator /(Vector3 a, float d) { return new Vector3(a.x / d, a.y / d, a.z / d); }
        public static bool operator ==(Vector3 a, Vector3 b) { return (a - b).sqrMagnitude < 1e-10f; }
        public static bool operator !=(Vector3 a, Vector3 b) { return !(a == b); }
        public override bool Equals(object o) { return o is Vector3 && this == (Vector3)o; }
        public override int GetHashCode() { return x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2); }
        public override string ToString() { return string.Format("({0:F2}, {1:F2}, {2:F2})", x, y, z); }
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b) { this.r = r; this.g = g; this.b = b; a = 1f; }
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white { get { return new Color(1, 1, 1, 1); } }
        public static Color black { get { return new Color(0, 0, 0, 1); } }
        public static Color gray { get { return new Color(0.5f, 0.5f, 0.5f, 1); } }
        public static Color grey { get { return gray; } }
        public static Color clear { get { return new Color(0, 0, 0, 0); } }
        public static Color operator *(Color c, float d) { return new Color(c.r * d, c.g * d, c.b * d, c.a * d); }
        public static Color operator *(Color a, Color b) { return new Color(a.r * b.r, a.g * b.g, a.b * b.b, a.a * b.a); }
        public static Color operator +(Color a, Color b) { return new Color(a.r + b.r, a.g + b.g, a.b + b.b, a.a + b.a); }
        public static bool operator ==(Color a, Color b) { return a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a; }
        public static bool operator !=(Color a, Color b) { return !(a == b); }
        public override bool Equals(object o) { return o is Color && this == (Color)o; }
        public override int GetHashCode() { return r.GetHashCode() ^ g.GetHashCode() ^ b.GetHashCode(); }
    }

    public struct Color32
    {
        public byte r, g, b, a;
        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    public static class Mathf
    {
        public const float PI = (float)Math.PI;
        public const float Deg2Rad = (float)(Math.PI / 180.0);
        public const float Rad2Deg = (float)(180.0 / Math.PI);
        public const float Epsilon = 1.401298E-45f;

        public static float Min(float a, float b) { return a < b ? a : b; }
        public static int Min(int a, int b) { return a < b ? a : b; }
        public static float Max(float a, float b) { return a > b ? a : b; }
        public static int Max(int a, int b) { return a > b ? a : b; }
        public static float Abs(float v) { return Math.Abs(v); }
        public static int Abs(int v) { return Math.Abs(v); }
        public static float Clamp(float v, float a, float b) { return v < a ? a : (v > b ? b : v); }
        public static int Clamp(int v, int a, int b) { return v < a ? a : (v > b ? b : v); }
        public static float Clamp01(float v) { return Clamp(v, 0f, 1f); }
        public static float Sqrt(float v) { return (float)Math.Sqrt(v); }
        public static float Sin(float v) { return (float)Math.Sin(v); }
        public static float Cos(float v) { return (float)Math.Cos(v); }
        public static float Tan(float v) { return (float)Math.Tan(v); }
        public static float Atan2(float y, float x) { return (float)Math.Atan2(y, x); }
        public static float Pow(float a, float b) { return (float)Math.Pow(a, b); }
        public static float Exp(float v) { return (float)Math.Exp(v); }
        public static float Log(float v) { return (float)Math.Log(v); }
        public static float Floor(float v) { return (float)Math.Floor(v); }
        public static float Ceil(float v) { return (float)Math.Ceiling(v); }
        public static float Round(float v) { return (float)Math.Round(v); }
        public static int FloorToInt(float v) { return (int)Math.Floor(v); }
        public static int CeilToInt(float v) { return (int)Math.Ceiling(v); }
        public static int RoundToInt(float v) { return (int)Math.Round(v); }
        public static float Sign(float v) { return v >= 0f ? 1f : -1f; }
        public static float Lerp(float a, float b, float t) { return a + (b - a) * Clamp01(t); }
        public static float InverseLerp(float a, float b, float v) { return a != b ? Clamp01((v - a) / (b - a)) : 0f; }
        public static bool Approximately(float a, float b) { return Math.Abs(b - a) < Math.Max(1e-6f * Math.Max(Math.Abs(a), Math.Abs(b)), 1e-5f); }
        public static float MoveTowards(float c, float t, float d) { return Math.Abs(t - c) <= d ? t : c + Math.Sign(t - c) * d; }
        public static float SmoothStep(float a, float b, float t) { t = Clamp01(t); t = -2f * t * t * t + 3f * t * t; return b * t + a * (1f - t); }
    }

    public enum TextureFormat { Alpha8 = 1, RGB24 = 3, RGBA32 = 4, ARGB32 = 5, BC7 = 25 }
    public enum FilterMode { Point, Bilinear, Trilinear }
    public enum TextureWrapMode { Repeat, Clamp, Mirror }

    public class Texture2D : Texture
    {
        public string name;

        private static Texture2D white;
        public static Texture2D whiteTexture
        {
            get
            {
                if (white == null)
                {
                    white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    white.Pixels[0] = new Color32(255, 255, 255, 255);
                }
                return white;
            }
        }
        public TextureWrapMode wrapMode;
        public FilterMode filterMode;
        public int anisoLevel;
        public bool HasMips;
        public TextureFormat Format;
        public Color32[] Pixels;

        public Texture2D(int w, int h) : this(w, h, TextureFormat.RGBA32, true) { }
        public Texture2D(int w, int h, TextureFormat fmt, bool mipChain)
        {
            width = w; height = h; Format = fmt; HasMips = mipChain;
            Pixels = new Color32[w * h];
        }

        public void SetPixels32(Color32[] p) { Array.Copy(p, Pixels, Math.Min(p.Length, Pixels.Length)); }

        /// <summary>BC7 rozpakowany programowo (w grze robi to karta graficzna).</summary>
        public void LoadRawTextureData(byte[] data)
        {
            if (Format != TextureFormat.BC7) throw new NotSupportedException("stub: tylko BC7");
            byte[] rgba = OSMView.Bc7.Decode(data, width, height);
            for (int i = 0; i < Pixels.Length; i++)
                Pixels[i] = new Color32(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);
        }

        /// <summary>Recznie ustawione mipmapy (poziom 1+). Pusta lista = liczy je tester.</summary>
        public readonly List<Color32[]> Mips = new List<Color32[]>();
        public void SetPixels32(Color32[] p, int level)
        {
            if (level == 0) { SetPixels32(p); return; }
            while (Mips.Count < level) Mips.Add(null);
            Mips[level - 1] = (Color32[])p.Clone();
        }
        public Color32[] GetPixels32() { return Pixels; }
        public void Apply() { }
        public void Apply(bool updateMipmaps) { }
        public void Apply(bool updateMipmaps, bool makeNoLongerReadable) { }
    }

    public class Mesh
    {
        public string name;
        public Rendering.IndexFormat indexFormat;
        public Vector3[] vertices = new Vector3[0];
        public Vector2[] uv = new Vector2[0];
        public Vector2[] uv2 = new Vector2[0];
        public Vector3[] normals = new Vector3[0];
        private readonly List<int[]> subs = new List<int[]>();

        public int subMeshCount
        {
            get { return subs.Count; }
            set { while (subs.Count < value) subs.Add(new int[0]); while (subs.Count > value) subs.RemoveAt(subs.Count - 1); }
        }

        public int[] triangles
        {
            get { var all = new List<int>(); foreach (int[] s in subs) all.AddRange(s); return all.ToArray(); }
            set { subs.Clear(); subs.Add(value); }
        }

        public void SetVertices(List<Vector3> v) { vertices = v.ToArray(); }
        public void SetNormals(List<Vector3> n) { normals = n.ToArray(); }
        public void SetUVs(int channel, List<Vector2> u) { if (channel == 0) uv = u.ToArray(); }
        public void SetTriangles(int[] t, int sub) { if (subs.Count <= sub) subMeshCount = sub + 1; subs[sub] = t; }
        public void SetTriangles(List<int> t, int sub) { SetTriangles(t.ToArray(), sub); }
        public int[] GetTriangles(int sub) { return subs[sub]; }
        public void RecalculateBounds() { }
        public void RecalculateNormals() { }
    }

    public static class Debug
    {
        public static void Log(object o) { }
        public static void LogWarning(object o) { Console.WriteLine("  [warn] " + o); }
        public static void LogError(object o) { Console.WriteLine("  [err] " + o); }
    }

    public static class Time
    {
        public static float realtimeSinceStartup
        {
            get { return (float)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency; }
        }
    }
}
