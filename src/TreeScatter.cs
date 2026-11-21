using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    public struct TreeInstance
    {
        public Vector3 Pos;      // lokalne metry w ramce kafla
        public float Scale;
        public float YawDeg;
        public byte Kind;        // 0 = dab, 1 = sosna
    }

    /// <summary>
    /// Rozrzuca drzewa wewnatrz poligonow lasu/parku. Jitterowana krata zamiast
    /// czystego losowania - inaczej drzewa robia kepy i luki.
    ///
    /// Bezpieczne watkowo: same struktury Vector, wysokosci z TerrainGrid.
    /// </summary>
    public static class TreeScatter
    {
        public static List<TreeInstance> Build(TerrainGrid grid, double bodyRadius,
                                               List<OsmWay> ways,
                                               double lat0, double lon0, double alt0,
                                               float densityScale, int maxTrees, float clipE, float clipN)
        {
            var result = new List<TreeInstance>();
            var ring = new List<Vector2>();
            var blocked = new Obstacles(ways, lat0, lon0, bodyRadius, clipE, clipN);

            // pojedyncze drzewa z OSM (natural=tree) - dokladnie tam, gdzie rosna
            foreach (OsmWay t in ways)
            {
                if (!t.IsTree || t.Points.Count == 0) continue;
                double e, n;
                Geo.ToLocalMeters(t.Points[0].Lat, t.Points[0].Lon, lat0, lon0, bodyRadius, out e, out n);
                float x = (float)e, z = (float)n;
                if (x < -clipE || x >= clipE || z < -clipN || z >= clipN) continue;
                if (blocked.Hit(x, z)) continue;
                uint h = Hash((uint)(int)(x * 4f + 32768), (uint)(int)(z * 4f + 32768));
                result.Add(new TreeInstance
                {
                    Pos = new Vector3(x, LanduseMeshBuilder.HeightLocal(grid, lat0, lon0, bodyRadius, alt0, x, z), z),
                    Scale = 0.8f + ((h >> 20) % 100u) / 140f,
                    YawDeg = ((h >> 6) % 360u),
                    Kind = (byte)(((h >> 3) % 100u) < 25u ? 1 : 0)
                });
            }

            // Budzet drzew dzielimy RÓWNO na caly kafel: najpierw szacujemy, ile drzew
            // wyszloby przy pelnej gestosci, i w razie potrzeby rozrzedzamy siatke.
            // Wczesniej limit zapelnial sie od poludniowego brzegu pierwszego lasu
            // (czesto poza kaflem), a reszta lasu zostawala pusta - pas drzew i golizna.
            float thin = 1f;
            {
                double est = 0;
                foreach (OsmWay w in ways)
                {
                    if (!w.IsArea) continue;
                    float sp = AreaKinds.TreeSpacing(w.Area);
                    if (sp <= 0f) continue;
                    sp /= Mathf.Max(0.2f, densityScale);
                    est += ClippedArea(w, lat0, lon0, bodyRadius, clipE, clipN) / (sp * sp);
                }
                if (est > maxTrees) thin = (float)Math.Sqrt(est / maxTrees) * 1.05f;
            }

            foreach (OsmWay way in ways)
            {
                if (!way.IsArea) continue;
                float spacing = AreaKinds.TreeSpacing(way.Area);
                if (spacing <= 0f) continue;
                spacing /= Mathf.Max(0.2f, densityScale);
                spacing *= thin;

                ring.Clear();
                int n = way.Points.Count;
                if (n >= 2 &&
                    Math.Abs(way.Points[0].Lat - way.Points[n - 1].Lat) < 1e-9 &&
                    Math.Abs(way.Points[0].Lon - way.Points[n - 1].Lon) < 1e-9)
                    n--;
                if (n < 3) continue;

                float minX = float.MaxValue, maxX = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;
                for (int i = 0; i < n; i++)
                {
                    GeoPoint g = way.Points[i];
                    double east, north;
                    Geo.ToLocalMeters(g.Lat, g.Lon, lat0, lon0, bodyRadius, out east, out north);
                    var p = new Vector2((float)east, (float)north);
                    ring.Add(p);
                    if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
                    if (p.y < minZ) minZ = p.y; if (p.y > maxZ) maxZ = p.y;
                }

                // Wielki las (multipoligon, tysiace wierzcholkow) przycinamy do kafla przed
                // sadzeniem: test punktu w wielokacie dla kazdego drzewa jest liniowy
                // wzgledem liczby wierzcholkow.
                List<Vector2> clipped = ring.Count > 64 ? ClipRect(ring, clipE + 2f, clipN + 2f) : ring;
                if (clipped.Count < 3) continue;

                // tylko czesc lasu lezaca w tym kaflu - sasiedzi posadza swoja
                minX = Mathf.Max(minX, -clipE); maxX = Mathf.Min(maxX, clipE);
                minZ = Mathf.Max(minZ, -clipN); maxZ = Mathf.Min(maxZ, clipN);
                if (minX > maxX || minZ > maxZ) continue;
                // siatka zaczepiona w stalym miejscu (wielokrotnosc odstepu), zeby kafle sie zazebialy
                minX = Mathf.Floor(minX / spacing) * spacing;
                minZ = Mathf.Floor(minZ / spacing) * spacing;

                bool conifer = way.Area == AreaKind.Forest;

                for (float z = minZ; z <= maxZ && result.Count < maxTrees; z += spacing)
                {
                    for (float x = minX; x <= maxX && result.Count < maxTrees; x += spacing)
                    {
                        uint h = Hash((uint)(int)(x * 4f + 32768), (uint)(int)(z * 4f + 32768));

                        // jitter w obrebie komorki - krata bez tego jest widoczna z powietrza
                        float jx = x + ((h % 1000u) / 1000f - 0.5f) * spacing * 0.85f;
                        float jz = z + (((h >> 10) % 1000u) / 1000f - 0.5f) * spacing * 0.85f;

                        if (jx < -clipE || jx >= clipE || jz < -clipN || jz >= clipN) continue;
                        if (blocked.Hit(jx, jz)) continue;          // jezdnia, chodnik albo dom
                        if (!PointInPolygon(clipped, jx, jz)) continue;

                        float y = LanduseMeshBuilder.HeightLocal(grid, lat0, lon0, bodyRadius,
                                                                 alt0, jx, jz);

                        result.Add(new TreeInstance
                        {
                            Pos = new Vector3(jx, y, jz),
                            Scale = 0.75f + ((h >> 20) % 100u) / 160f,
                            YawDeg = ((h >> 6) % 360u),
                            Kind = (byte)(conifer && ((h >> 3) % 100u) < 55u ? 1 : 0)
                        });
                    }
                }
            }

            return result;
        }

        /// <summary>Pole wielokata przycietego do prostokata kafla (m2) - tylko do szacunku,
        /// wiec wystarczy przyciecie Sutherlanda-Hodgmana do czterech krawedzi.</summary>
        private static double ClippedArea(OsmWay w, double lat0, double lon0, double r, float ce, float cn)
        {
            var poly = new List<Vector2>(w.Points.Count);
            foreach (GeoPoint g in w.Points)
            {
                double e, n;
                Geo.ToLocalMeters(g.Lat, g.Lon, lat0, lon0, r, out e, out n);
                poly.Add(new Vector2((float)e, (float)n));
            }
            poly = Clip(poly, 0, -ce, false); poly = Clip(poly, 0, ce, true);
            poly = Clip(poly, 1, -cn, false); poly = Clip(poly, 1, cn, true);
            double a = 0;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                a += (double)poly[j].x * poly[i].y - (double)poly[i].x * poly[j].y;
            return Math.Abs(a) * 0.5;
        }

        internal static List<Vector2> ClipRect(List<Vector2> poly, float ce, float cn)
        {
            poly = Clip(poly, 0, -ce, false); poly = Clip(poly, 0, ce, true);
            poly = Clip(poly, 1, -cn, false); poly = Clip(poly, 1, cn, true);
            return poly;
        }

        private static List<Vector2> Clip(List<Vector2> p, int axis, float lim, bool keepBelow)
        {
            var o = new List<Vector2>(p.Count + 4);
            for (int i = 0; i < p.Count; i++)
            {
                Vector2 a = p[i], b = p[(i + 1) % p.Count];
                bool ia = keepBelow ? Ax(a, axis) <= lim : Ax(a, axis) >= lim;
                bool ib = keepBelow ? Ax(b, axis) <= lim : Ax(b, axis) >= lim;
                if (ia) o.Add(a);
                if (ia != ib)
                {
                    float t = (lim - Ax(a, axis)) / (Ax(b, axis) - Ax(a, axis));
                    o.Add(a + (b - a) * t);
                }
            }
            return o;
        }

        private static float Ax(Vector2 v, int axis) { return axis == 0 ? v.x : v.y; }

        private static bool PointInPolygon(List<Vector2> poly, float x, float z)
        {
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if ((poly[i].y > z) != (poly[j].y > z) &&
                    x < (poly[j].x - poly[i].x) * (z - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            }
            return inside;
        }

        private static uint Hash(uint x, uint y)
        {
            uint h = x * 374761393u + y * 668265263u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }

    /// <summary>
    /// Miejsca, gdzie drzewo stac nie moze: droga razem z poboczem (chodnik i trawnik
    /// przy ulicach w zabudowie), obrys budynku z zapasem na korone. Siatka 20 m, zeby
    /// sprawdzenie tysiecy drzew nie przegladalo wszystkich drog kafla.
    /// </summary>
    internal sealed class Obstacles
    {
        private const float Cell = 20f;
        private readonly Dictionary<long, List<int>> segs = new Dictionary<long, List<int>>();
        private readonly Dictionary<long, List<int>> polys = new Dictionary<long, List<int>>();
        private readonly List<float[]> seg = new List<float[]>();     // ax, az, bx, bz
        private readonly List<float> clear = new List<float>();
        private readonly List<Vector2[]> poly = new List<Vector2[]>();
        private readonly List<Vector2[]> clearings = new List<Vector2[]>();    // dziury multipoligonow lasu
        private readonly List<float[]> clearBox = new List<float[]>();
        private const float HouseMargin = 2.0f;

        public Obstacles(List<OsmWay> ways, double lat0, double lon0, double r, float clipE, float clipN)
        {
            float lim = Math.Max(clipE, clipN) + 60f;
            foreach (OsmWay w in ways)
            {
                if (w.IsRoad && w.Points.Count >= 2)
                {
                    RoadClass c = RoadClasses.From(w.Highway);
                    float hw = OsmXml.WidthFor(w.Highway) * 0.5f;
                    float cl = c == RoadClass.Major || c == RoadClass.Minor ? hw + 5.0f
                             : c == RoadClass.Runway ? hw + 75f : c == RoadClass.Taxiway ? hw + 25f : hw + 1.8f;
                    Vector2 prev = Local(w.Points[0], lat0, lon0, r);
                    for (int i = 1; i < w.Points.Count; i++)
                    {
                        Vector2 cur = Local(w.Points[i], lat0, lon0, r);
                        if (Math.Abs(cur.x) < lim && Math.Abs(cur.y) < lim || Math.Abs(prev.x) < lim && Math.Abs(prev.y) < lim)
                        {
                            int id = seg.Count;
                            seg.Add(new[] { prev.x, prev.y, cur.x, cur.y });
                            clear.Add(cl);
                            Insert(segs, id, Mathf.Min(prev.x, cur.x) - cl, Mathf.Min(prev.y, cur.y) - cl,
                                   Mathf.Max(prev.x, cur.x) + cl, Mathf.Max(prev.y, cur.y) + cl);
                        }
                        prev = cur;
                    }
                }
                else if (w.Area == AreaKind.Clearing && w.Points.Count >= 4)
                {
                    var pts = new List<Vector2>(w.Points.Count);
                    for (int i = 0; i + 1 < w.Points.Count; i++) pts.Add(Local(w.Points[i], lat0, lon0, r));
                    pts = TreeScatter.ClipRect(pts, clipE + 2f, clipN + 2f);
                    if (pts.Count < 3) continue;
                    float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
                    foreach (Vector2 q in pts)
                    {
                        x0 = Math.Min(x0, q.x); x1 = Math.Max(x1, q.x);
                        z0 = Math.Min(z0, q.y); z1 = Math.Max(z1, q.y);
                    }
                    clearings.Add(pts.ToArray());
                    clearBox.Add(new[] { x0, z0, x1, z1 });
                }
                else if (w.IsBuilding && w.Points.Count >= 3)
                {
                    var pts = new Vector2[w.Points.Count];
                    float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
                    for (int i = 0; i < pts.Length; i++)
                    {
                        pts[i] = Local(w.Points[i], lat0, lon0, r);
                        x0 = Math.Min(x0, pts[i].x); x1 = Math.Max(x1, pts[i].x);
                        z0 = Math.Min(z0, pts[i].y); z1 = Math.Max(z1, pts[i].y);
                    }
                    if (x1 < -lim || x0 > lim || z1 < -lim || z0 > lim) continue;
                    int id = poly.Count;
                    poly.Add(pts);
                    Insert(polys, id, x0 - HouseMargin, z0 - HouseMargin, x1 + HouseMargin, z1 + HouseMargin);
                }
            }
        }

        private static Vector2 Local(GeoPoint g, double lat0, double lon0, double r)
        {
            double e, n;
            Geo.ToLocalMeters(g.Lat, g.Lon, lat0, lon0, r, out e, out n);
            return new Vector2((float)e, (float)n);
        }

        private static long Key(int cx, int cz) { return ((long)cx << 32) ^ (uint)cz; }

        private static void Insert(Dictionary<long, List<int>> map, int id, float x0, float z0, float x1, float z1)
        {
            for (int cz = (int)Math.Floor(z0 / Cell); cz <= (int)Math.Floor(z1 / Cell); cz++)
                for (int cx = (int)Math.Floor(x0 / Cell); cx <= (int)Math.Floor(x1 / Cell); cx++)
                {
                    List<int> l;
                    long k = Key(cx, cz);
                    if (!map.TryGetValue(k, out l)) map[k] = l = new List<int>();
                    l.Add(id);
                }
        }

        public bool Hit(float x, float z)
        {
            long k = Key((int)Math.Floor(x / Cell), (int)Math.Floor(z / Cell));
            List<int> l;
            var p = new Vector2(x, z);
            if (segs.TryGetValue(k, out l))
                foreach (int id in l)
                {
                    float[] s = seg[id];
                    var a = new Vector2(s[0], s[1]); var b = new Vector2(s[2], s[3]);
                    Vector2 ab = b - a;
                    float t = ab.sqrMagnitude > 1e-4f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
                    if ((p - (a + ab * t)).sqrMagnitude < clear[id] * clear[id]) return true;
                }
            for (int c = 0; c < clearings.Count; c++)
            {
                float[] bx = clearBox[c];
                if (x < bx[0] || x > bx[2] || z < bx[1] || z > bx[3]) continue;
                Vector2[] q = clearings[c];
                bool inside = false;
                for (int i = 0, j = q.Length - 1; i < q.Length; j = i++)
                    if ((q[i].y > z) != (q[j].y > z) && x < (q[j].x - q[i].x) * (z - q[i].y) / (q[j].y - q[i].y) + q[i].x)
                        inside = !inside;
                if (inside) return true;
            }
            if (polys.TryGetValue(k, out l))
                foreach (int id in l)
                {
                    Vector2[] q = poly[id];
                    bool inside = false;
                    float best = float.MaxValue;
                    for (int i = 0, j = q.Length - 1; i < q.Length; j = i++)
                    {
                        if ((q[i].y > z) != (q[j].y > z) && x < (q[j].x - q[i].x) * (z - q[i].y) / (q[j].y - q[i].y) + q[i].x)
                            inside = !inside;
                        Vector2 e = q[i] - q[j];
                        float t = e.sqrMagnitude > 1e-4f ? Mathf.Clamp01(Vector2.Dot(p - q[j], e) / e.sqrMagnitude) : 0f;
                        best = Math.Min(best, (p - (q[j] + e * t)).sqrMagnitude);
                    }
                    if (inside || best < HouseMargin * HouseMargin) return true;
                }
            return false;
        }
    }
}
