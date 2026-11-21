using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Pobocze ulicy w zabudowie: kraweznik, chodnik z kostki, pas trawnika - i wjazdy
    /// z kostki od domow do jezdni.
    ///
    /// OSM prawie nigdy nie ma chodnikow ani podjazdow (a jak ma, to jako osobne sciezki),
    /// wiec zgadujemy je z sasiedztwa: strona ulicy, przy ktorej stoja co najmniej dwa
    /// budynki, dostaje chodnik; dom blisko ulicy dostaje wjazd. Przy skrzyzowaniach
    /// chodnik sie urywa (inaczej plyta przecinalaby jezdnie poprzeczna) i schodzi
    /// do poziomu jezdni - obnizony kraweznik, bez progu dla kol.
    ///
    /// Wysokosci ida przez uv2 (RoadMeshBuilder.Road/Ground), GroundSnapper je stosuje.
    /// Watek roboczy - tylko struktury Unity, bez obiektow.
    /// </summary>
    public static class StreetsideBuilder
    {
        public class WayInfo
        {
            public Vector3[] Pts;
            public float HalfW;
            public RoadClass Cls;
            public float ClassLift;
            public OsmWay Way;
            public int[] SideCount = new int[2];   // 0 = lewa (-across), 1 = prawa (+across)
            public float[] Extra;                  // unos ponad teren (most, najazd) na punkt
            public bool[] Plain;                   // punkt w obrebie skrzyzowania / ronda - bez oznakowania
        }

        private const float CurbH = 0.12f, CurbW = 0.15f, GrassW = 1.6f, GrassSink = -0.15f;
        private const float DriveHalfW = 1.5f, DriveLift = 0.06f;
        private const float HouseSearch = 45f;

        private struct House
        {
            public Vector2 C;
            public List<Vector2> Poly;
            public bool Small;
        }

        private static bool Eligible(OsmWay w, RoadClass c)
        {
            if (c != RoadClass.Major && c != RoadClass.Minor) return false;
            return w.Highway != "living_street" && !w.Highway.EndsWith("_link");
        }

        private static float SidewalkW(RoadClass c) { return c == RoadClass.Major ? 2.2f : 1.8f; }

        public static void Build(List<WayInfo> infos, List<OsmWay> ways, double lat0, double lon0, double radius,
                                 float heightOffset, float clipE, float clipN,
                                 List<Vector3> verts, List<Vector2> uvs, List<Vector2> uv2, List<int>[] tris)
        {
            if (infos.Count == 0) return;

            // --- budynki w poblizu kafla ---
            var houses = new List<House>();
            float margin = HouseSearch + 20f;
            foreach (OsmWay w in ways)
            {
                if (!w.IsBuilding || w.Points.Count < 3) continue;
                var poly = new List<Vector2>(w.Points.Count);
                Vector2 sum = Vector2.zero;
                foreach (GeoPoint g in w.Points)
                {
                    double e, n;
                    Geo.ToLocalMeters(g.Lat, g.Lon, lat0, lon0, radius, out e, out n);
                    var p = new Vector2((float)e, (float)n);
                    poly.Add(p); sum += p;
                }
                Vector2 c = sum / poly.Count;
                if (Math.Abs(c.x) > clipE + margin || Math.Abs(c.y) > clipN + margin) continue;
                float area = Area(poly);
                houses.Add(new House { C = c, Poly = poly, Small = area < 400f && IsHome(w.Building) });
            }

            // --- siatka punktow osi ulic (do szukania najblizszej ulicy) ---
            const float Cell = 25f;
            var grid = new Dictionary<long, List<int>>();       // (way << 20) | index
            for (int wi = 0; wi < infos.Count; wi++)
            {
                WayInfo inf = infos[wi];
                if (inf.Cls == RoadClass.Motorway || inf.Cls >= RoadClass.Path) continue;
                for (int i = 0; i < inf.Pts.Length; i++)
                {
                    long k = Key(inf.Pts[i].x, inf.Pts[i].z, Cell);
                    List<int> l;
                    if (!grid.TryGetValue(k, out l)) grid[k] = l = new List<int>();
                    l.Add((wi << 20) | i);
                }
            }

            // --- kazdy dom: najblizsza ulica, strona, odleglosc ---
            var near = new List<KeyValuePair<House, Hit>>();
            foreach (House h in houses)
            {
                Hit hit;
                if (!Nearest(infos, grid, Cell, h.C, out hit)) continue;
                if (hit.Dist > HouseSearch) continue;
                infos[hit.Way].SideCount[hit.Side]++;
                near.Add(new KeyValuePair<House, Hit>(h, hit));
            }

            // --- skrzyzowania: punkty wspolne kilku drog ---
            var junction = new Dictionary<long, float>();       // klucz 10 cm -> najwieksza polszerokosc
            var owner = new Dictionary<long, int>();
            for (int wi = 0; wi < infos.Count; wi++)
            {
                // sciezki i chodniki z OSM przecinajace ulice to nie skrzyzowania - chodnik nie urywa sie na nich
                if (infos[wi].Cls >= RoadClass.Path) continue;
                foreach (Vector3 p in infos[wi].Pts)
                {
                    long k = Key(p.x, p.z, 0.1f);
                    int o;
                    if (!owner.TryGetValue(k, out o)) { owner[k] = wi; continue; }
                    if (o == wi) continue;
                    float hw = Math.Max(infos[wi].HalfW, infos[o].HalfW);
                    float cur;
                    junction[k] = junction.TryGetValue(k, out cur) ? Math.Max(cur, hw) : hw;
                }
            }
            var jList = new List<Vector3>();                    // x, z, polszerokosc
            foreach (KeyValuePair<long, float> kv in junction)
            {
                float x = (int)(kv.Key >> 32) * 0.1f, z = (int)(kv.Key & 0xFFFFFFFF) * 0.1f;
                jList.Add(new Vector3(x, kv.Value, z));
            }

            // --- chodniki ---
            for (int wi = 0; wi < infos.Count; wi++)
            {
                WayInfo inf = infos[wi];
                if (!Eligible(inf.Way, inf.Cls)) continue;
                int fl = inf.Way.Flags;
                // OSM mowi, ze chodnik jest osobna linia albo go nie ma - nie dorabiamy
                if ((fl & (PackFormat.RSideSeparate | PackFormat.RSideNone)) != 0) continue;
                for (int side = 0; side < 2; side++)
                {
                    bool tagged = (fl & (side == 1 ? PackFormat.RSideRight : PackFormat.RSideLeft)) != 0;
                    if (tagged || inf.SideCount[side] >= 2)
                        AddSidewalk(inf, side, jList, heightOffset, clipE, clipN, verts, uvs, uv2, tris);
                }
            }

            // --- wjazdy ---
            var starts = new List<Vector2>();
            foreach (KeyValuePair<House, Hit> kv in near)
            {
                if (!kv.Key.Small) continue;
                WayInfo inf = infos[kv.Value.Way];
                if (inf.Cls != RoadClass.Major && inf.Cls != RoadClass.Minor && inf.Cls != RoadClass.Service) continue;
                bool walk = Eligible(inf.Way, inf.Cls) && inf.SideCount[kv.Value.Side] >= 2;
                AddDriveway(kv.Key, kv.Value, inf, walk, jList, starts, heightOffset, clipE, clipN, verts, uvs, uv2, tris);
            }
        }

        // ------------------------------------------------------------------

        private struct Hit
        {
            public int Way, Seg, Side;
            public float Dist, T;
            public Vector2 Foot, Dir;
        }

        private static bool Nearest(List<WayInfo> infos, Dictionary<long, List<int>> grid, float cell, Vector2 c, out Hit best)
        {
            best = new Hit { Dist = float.MaxValue };
            int cx = (int)Math.Floor(c.x / cell), cz = (int)Math.Floor(c.y / cell);
            var tried = new HashSet<int>();
            for (int dz = -2; dz <= 2; dz++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    List<int> l;
                    if (!grid.TryGetValue(((long)(cx + dx) << 32) ^ (uint)(cz + dz), out l)) continue;
                    foreach (int code in l)
                    {
                        int wi = code >> 20, i = code & 0xFFFFF;
                        Vector3[] p = infos[wi].Pts;
                        for (int s = Math.Max(0, i - 1); s <= Math.Min(p.Length - 2, i); s++)
                        {
                            if (!tried.Add((wi << 20) | s)) continue;
                            var a = new Vector2(p[s].x, p[s].z);
                            var b = new Vector2(p[s + 1].x, p[s + 1].z);
                            Vector2 ab = b - a;
                            float l2 = ab.sqrMagnitude;
                            if (l2 < 1e-4f) continue;
                            float t = Mathf.Clamp01(Vector2.Dot(c - a, ab) / l2);
                            Vector2 f = a + ab * t;
                            float d = (c - f).magnitude;
                            if (d >= best.Dist) continue;
                            Vector2 dir = ab / Mathf.Sqrt(l2);
                            // across = (dir.z, -dir.x) w RoadMeshBuilder -> tu (dir.y, -dir.x)
                            float sideDot = (c.x - f.x) * dir.y - (c.y - f.y) * dir.x;
                            best = new Hit { Way = wi, Seg = s, T = t, Dist = d, Foot = f, Dir = dir, Side = sideDot >= 0 ? 1 : 0 };
                        }
                    }
                }
            return best.Dist < float.MaxValue;
        }

        private static bool NearJunction(List<Vector3> j, float x, float z, float extra)
        {
            foreach (Vector3 q in j)
            {
                float r = q.y + extra;
                float dx = q.x - x, dz = q.z - z;
                if (dx * dx + dz * dz < r * r) return true;
            }
            return false;
        }

        private static void AddSidewalk(WayInfo inf, int side, List<Vector3> jList, float heightOffset,
                                        float clipE, float clipN,
                                        List<Vector3> verts, List<Vector2> uvs, List<Vector2> uv2, List<int>[] tris)
        {
            Vector3[] p = inf.Pts;
            int n = p.Length;
            float sw = SidewalkW(inf.Cls);
            float cutExtra = inf.HalfW + sw + 1.2f;
            float sgn = side == 1 ? 1f : -1f;

            var ok = new bool[n];
            for (int i = 0; i < n; i++) ok[i] = !NearJunction(jList, p[i].x, p[i].z, cutExtra) && (inf.Extra == null || inf.Extra[i] < 0.3f);
            // koniec slepej ulicy: chodnik tez sie konczy (tam czesto jest plac do zawracania)
            ok[0] = false; ok[n - 1] = false;

            int i0 = 0;
            while (i0 < n)
            {
                while (i0 < n && !ok[i0]) i0++;
                int i1 = i0;
                while (i1 + 1 < n && ok[i1 + 1]) i1++;
                if (i1 - i0 >= 1) Run(inf, p, i0 > 0 ? i0 - 1 : i0, i1 < n - 1 ? i1 + 1 : i1, sgn, sw, heightOffset, clipE, clipN, verts, uvs, uv2, tris);
                i0 = i1 + 1;
            }
        }

        /// <summary>Jeden ciagly odcinek chodnika z punktow [a..b]; na koncach obnizony do jezdni.</summary>
        private static void Run(WayInfo inf, Vector3[] p, int a, int b, float sgn, float sw, float heightOffset,
                                float clipE, float clipN,
                                List<Vector3> verts, List<Vector2> uvs, List<Vector2> uv2, List<int>[] tris)
        {
            int m = b - a + 1;
            if (m < 2) return;
            float hw = inf.HalfW, cl = inf.ClassLift;
            // kolumny: 0 kraweznik dol, 1 kraweznik gora (wewn.), 2 kraweznik gora (zewn.),
            //          3 chodnik wewn., 4 chodnik zewn., 5 trawa wewn., 6 trawa zewn. (pod gruntem)
            var idx = new int[m, 7];
            float along = 0f;
            for (int k = 0; k < m; k++)
            {
                int i = a + k;
                if (k > 0) along += Horizontal(p[i] - p[i - 1]);
                Vector3 dir = Dir(p, i);
                var across = new Vector3(dir.z, 0f, -dir.x) * sgn;
                bool end = k == 0 || k == m - 1;
                float top = end ? cl + 0.01f : cl + CurbH;          // obnizony kraweznik na koncach
                float v = along / 4f;

                idx[k, 0] = Add(verts, uvs, uv2, p[i] + across * hw, 0f, cl - 0.03f, false, heightOffset, new Vector2(0f, v));
                idx[k, 1] = Add(verts, uvs, uv2, p[i] + across * hw, 0f, top, false, heightOffset, new Vector2(0.12f, v));
                idx[k, 2] = Add(verts, uvs, uv2, p[i] + across * (hw + CurbW), 0f, top, false, heightOffset, new Vector2(0.27f, v));
                idx[k, 3] = Add(verts, uvs, uv2, p[i] + across * (hw + CurbW), 0f, top, false, heightOffset, new Vector2(0f, v));
                idx[k, 4] = Add(verts, uvs, uv2, p[i] + across * (hw + CurbW + sw), 0f, top, false, heightOffset, new Vector2(sw / 2f, v));
                idx[k, 5] = Add(verts, uvs, uv2, p[i] + across * (hw + CurbW + sw), 0f, top, false, heightOffset, new Vector2(0f, v));
                idx[k, 6] = Add(verts, uvs, uv2, p[i] + across * (hw + CurbW + sw + GrassW), 0f, GrassSink, true, heightOffset, new Vector2(GrassW / 2f, v));
            }

            List<int> curb = tris[RoadMeshBuilder.SubCurb], pave = tris[RoadMeshBuilder.SubPaving], grass = tris[RoadMeshBuilder.SubGrass];
            for (int k = 0; k < m - 1; k++)
            {
                if (!RoadMeshBuilder.SegmentInsidePublic(p[a + k], p[a + k + 1], clipE, clipN)) continue;
                // Kolumny rosna "na zewnatrz"; po prawej stronie to +across (jak we wstedze),
                // po lewej -across, wiec tam zamieniamy kolejnosc, zeby normalne szly w gore.
                // Czolo kraweznika (0-1) jest pionowe: po prawej (dol, gora), po lewej (gora, dol)
                // daje normalna skierowana na jezdnie.
                // Na wewnetrznej stronie ciasnego luku pas 4-8 m od osi zawija sie sam na siebie -
                // taki kawalek pomijamy (osobno dla kraweznika, chodnika i trawy).
                Vector3 axis = p[a + k + 1] - p[a + k];
                if (Forward(verts, idx, k, 2, axis)) { Strip(curb, idx, k, 0, 1, sgn); Strip(curb, idx, k, 1, 2, sgn); }
                if (Forward(verts, idx, k, 4, axis)) Strip(pave, idx, k, 3, 4, sgn);
                if (Forward(verts, idx, k, 6, axis)) Strip(grass, idx, k, 5, 6, sgn);
            }
        }

        private static bool Forward(List<Vector3> verts, int[,] idx, int k, int col, Vector3 axis)
        {
            Vector3 e = verts[idx[k + 1, col]] - verts[idx[k, col]];
            float dot = e.x * axis.x + e.z * axis.z;
            float la = Mathf.Sqrt(axis.x * axis.x + axis.z * axis.z);
            return dot > 0.25f * la * la;          // brzeg musi isc do przodu przynajmniej w 1/4 dlugosci osi
        }

        private static void Strip(List<int> t, int[,] idx, int k, int cIn, int cOut, float sgn)
        {
            int l0, l1, r0, r1;
            if (sgn > 0) { l0 = idx[k, cIn]; l1 = idx[k + 1, cIn]; r0 = idx[k, cOut]; r1 = idx[k + 1, cOut]; }
            else { l0 = idx[k, cOut]; l1 = idx[k + 1, cOut]; r0 = idx[k, cIn]; r1 = idx[k + 1, cIn]; }
            t.Add(l0); t.Add(l1); t.Add(r0);
            t.Add(r0); t.Add(l1); t.Add(r1);
        }

        private static void AddDriveway(House h, Hit hit, WayInfo inf, bool walk, List<Vector3> jList, List<Vector2> starts,
                                        float heightOffset, float clipE, float clipN,
                                        List<Vector3> verts, List<Vector2> uvs, List<Vector2> uv2, List<int>[] tris)
        {
            Vector2 d = h.C - hit.Foot;
            float dl = d.magnitude;
            if (dl < 1f) return;
            d /= dl;
            // wjazd prostopadly do ulicy wyglada naturalniej niz ukosny do srodka domu
            var perp = new Vector2(hit.Dir.y, -hit.Dir.x) * (hit.Side == 1 ? 1f : -1f);
            if (Vector2.Dot(perp, d) > 0.6f) d = perp;

            float hw = inf.HalfW;
            Vector2 s = hit.Foot + d * (hw - 0.3f);
            if (s.x < -clipE || s.x >= clipE || s.y < -clipN || s.y >= clipN) return;
            foreach (Vector2 q in starts) if ((q - s).sqrMagnitude < 36f) return;
            if (NearJunction(jList, hit.Foot.x, hit.Foot.y, hw + 4f)) return;

            float enter = RayPoly(hit.Foot, d, h.Poly);
            if (enter < 0f) return;
            float len = enter - 0.3f - (hw - 0.3f);
            if (len < 2.5f || len > 32f) return;
            starts.Add(s);

            Vector3 roadP = Lerp(inf.Pts[hit.Seg], inf.Pts[hit.Seg + 1], hit.T);
            float cl = inf.ClassLift;
            float sw = SidewalkW(inf.Cls);

            // profil: [odleglosc od startu, unos, od gruntu?]
            var prof = new List<Vector3>();
            prof.Add(new Vector3(0f, cl + 0.02f, 0f));
            if (walk)
            {
                prof.Add(new Vector3(0.9f, cl + CurbH + 0.01f, 0f));                   // najazd na kraweznik
                prof.Add(new Vector3(0.3f + CurbW + sw, cl + CurbH + 0.01f, 0f));      // przez chodnik
                prof.Add(new Vector3(0.3f + CurbW + sw + 1.2f, DriveLift, 1f));        // zjazd na grunt
            }
            else prof.Add(new Vector3(1.6f, DriveLift + 0.05f, 1f));
            for (float x = prof[prof.Count - 1].x + 4f; x < len; x += 4f) prof.Add(new Vector3(x, DriveLift, 1f));
            prof.Add(new Vector3(len, DriveLift, 1f));

            var side = new Vector3(d.y, 0f, -d.x) * DriveHalfW;      // "across" wjazdu
            var dir3 = new Vector3(d.x, 0f, d.y);
            var start3 = new Vector3(s.x, roadP.y, s.y);
            int first = verts.Count;
            foreach (Vector3 q in prof)
            {
                if (q.x > len + 0.01f) continue;
                Vector3 c = start3 + dir3 * q.x;
                bool ground = q.z > 0.5f;
                Add(verts, uvs, uv2, c - side, 0f, q.y, ground, heightOffset, new Vector2(0f, q.x / 4f));
                Add(verts, uvs, uv2, c + side, 0f, q.y, ground, heightOffset, new Vector2(DriveHalfW, q.x / 4f));
            }
            List<int> pave = tris[RoadMeshBuilder.SubPaving];
            int rows = (verts.Count - first) / 2;
            for (int k = 0; k < rows - 1; k++)
            {
                int a = first + k * 2, b = a + 2;
                pave.Add(a); pave.Add(b); pave.Add(a + 1);
                pave.Add(a + 1); pave.Add(b); pave.Add(b + 1);
            }
        }

        // ------------------------------------------------------------------

        private static int Add(List<Vector3> verts, List<Vector2> uvs, List<Vector2> uv2, Vector3 pos, float unused,
                               float lift, bool ground, float heightOffset, Vector2 uv)
        {
            // wstepna wysokosc (przed doklejeniem do PQS): os drogi ma juz unos drogi w sobie
            float y = ground ? pos.y - heightOffset + lift : pos.y + lift;
            verts.Add(new Vector3(pos.x, y, pos.z));
            uvs.Add(uv);
            uv2.Add(ground ? RoadMeshBuilder.Ground(lift) : RoadMeshBuilder.Road(lift));
            return verts.Count - 1;
        }

        private static Vector3 Dir(Vector3[] p, int i)
        {
            int n = p.Length;
            Vector3 d = i == 0 ? p[1] - p[0] : i == n - 1 ? p[n - 1] - p[n - 2] : p[i + 1] - p[i - 1];
            d.y = 0f;
            float l = Mathf.Sqrt(d.x * d.x + d.z * d.z);
            return l < 1e-5f ? new Vector3(0f, 0f, 1f) : d / l;
        }

        private static float Horizontal(Vector3 v) { return Mathf.Sqrt(v.x * v.x + v.z * v.z); }

        private static Vector3 Lerp(Vector3 a, Vector3 b, float t) { return a + (b - a) * t; }

        private static long Key(float x, float z, float cell)
        {
            return ((long)(int)Math.Floor(x / cell) << 32) ^ (uint)(int)Math.Floor(z / cell);
        }

        /// <summary>Pierwsze przeciecie promienia (o + t*d, t &gt; 0) z obrysem; -1 gdy brak.</summary>
        private static float RayPoly(Vector2 o, Vector2 d, List<Vector2> poly)
        {
            float best = float.MaxValue;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                Vector2 a = poly[j], e = poly[i] - poly[j];
                float den = d.x * e.y - d.y * e.x;
                if (Math.Abs(den) < 1e-6f) continue;
                Vector2 ao = a - o;
                float t = (ao.x * e.y - ao.y * e.x) / den;
                float u = (ao.x * d.y - ao.y * d.x) / den;
                if (t > 0f && u >= 0f && u <= 1f && t < best) best = t;
            }
            return best == float.MaxValue ? -1f : best;
        }

        private static float Area(List<Vector2> p)
        {
            float a = 0f;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) a += p[j].x * p[i].y - p[i].x * p[j].y;
            return Math.Abs(a) * 0.5f;
        }

        private static bool IsHome(string b)
        {
            switch (b)
            {
                case "house": case "detached": case "semidetached_house": case "terrace": case "bungalow":
                case "residential": case "yes": case "farm": case "villa":
                    return true;
                default: return false;
            }
        }
    }
}
