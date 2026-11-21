using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Mosty i wiadukty z tagow OSM (bridge, layer), na terenie gry, ktory nasypow nie ma.
    ///
    /// Pomost: linia miedzy wysokosciami terenu na koncach mostu (przez doline/rzeke idzie
    /// prosto), ale nigdy nizej niz 0.3 m nad terenem. Jesli pod mostem przechodzi inna
    /// droga (nizszy layer albo zwykla droga, ktora most przecina), pomost nad nia ma
    /// skrajnie 5.3 m przeswitu - a do niej dochodzi z nachyleniem najwyzej 7%.
    ///
    /// Najazdy: to, czego brakuje na koncach mostu, dostaja drogi podlaczone w tym samym
    /// wezle - unos maleje liniowo z odlegloscia (te same 7%), takze przez kolejne odcinki.
    ///
    /// Wynik to Extra[i] na punkt drogi: o ile metrow ponad teren (i ponad zwykly unos)
    /// polozyc jezdnie. Liczone z tych samych danych w kazdym kaflu (czytane z zapasem),
    /// wiec na styku kafli wysokosci sie zgadzaja.
    /// </summary>
    internal static class BridgeLifter
    {
        public const float Clearance = 5.3f;
        public const float MaxGrade = 0.07f;
        private const float Cell = 25f;

        public static void Compute(List<StreetsideBuilder.WayInfo> ways, float heightOffset)
        {
            bool any = false;
            foreach (StreetsideBuilder.WayInfo w in ways) if (w.Way.IsBridge) { any = true; break; }
            if (!any) return;

            // wezly (punkty wspolne drog) i siatka odcinkow do szukania przeciec
            // klucz punktu drogi: droga w gornych bitach, punkt w dolnych - jako long, zeby nie przepelnic
            // przy duzej liczbie drog w kaflu (przy int i przesunieciu 20 bitow wystarczylo ~2000 drog, zeby
            // (wi << 20) przekrecilo sie na ujemna liczbe i dalo IndexOutOfRange gdzie indziej w tym pliku -
            // taki kafel probowal sie zbudowac od nowa co skan, w kolko, az KSP wypadal z pamieci)
            var nodes = new Dictionary<long, List<long>>();
            var segs = new Dictionary<long, List<long>>();
            for (int wi = 0; wi < ways.Count; wi++)
            {
                Vector3[] p = ways[wi].Pts;
                for (int i = 0; i < p.Length; i++)
                {
                    long k = Key(p[i].x, p[i].z, 0.1f);
                    List<long> l;
                    if (!nodes.TryGetValue(k, out l)) nodes[k] = l = new List<long>();
                    l.Add(Code(wi, i));
                    if (i + 1 < p.Length)
                    {
                        float x0 = Math.Min(p[i].x, p[i + 1].x), x1 = Math.Max(p[i].x, p[i + 1].x);
                        float z0 = Math.Min(p[i].z, p[i + 1].z), z1 = Math.Max(p[i].z, p[i + 1].z);
                        for (int cz = (int)Math.Floor(z0 / Cell); cz <= (int)Math.Floor(z1 / Cell); cz++)
                            for (int cx = (int)Math.Floor(x0 / Cell); cx <= (int)Math.Floor(x1 / Cell); cx++)
                            {
                                long ck = ((long)cx << 32) ^ (uint)cz;
                                if (!segs.TryGetValue(ck, out l)) segs[ck] = l = new List<long>();
                                l.Add(Code(wi, i));
                            }
                    }
                }
            }

            for (int bi = 0; bi < ways.Count; bi++)
            {
                StreetsideBuilder.WayInfo b = ways[bi];
                if (!b.Way.IsBridge) continue;
                Vector3[] p = b.Pts;
                int n = p.Length;
                var s = new float[n];
                for (int i = 1; i < n; i++) s[i] = s[i - 1] + Horizontal(p[i] - p[i - 1]);
                float L = Math.Max(0.1f, s[n - 1]);
                float g0 = p[0].y - heightOffset, gN = p[n - 1].y - heightOffset;

                // drogi przechodzace pod mostem: miejsce (metr mostu) i potrzebna wysokosc pomostu
                var need = new List<Vector2>();       // x = metr mostu, y = wysokosc pomostu nad nim
                for (int i = 0; i + 1 < n; i++)
                {
                    var seen = new HashSet<long>();
                    float x0 = Math.Min(p[i].x, p[i + 1].x), x1 = Math.Max(p[i].x, p[i + 1].x);
                    float z0 = Math.Min(p[i].z, p[i + 1].z), z1 = Math.Max(p[i].z, p[i + 1].z);
                    for (int cz = (int)Math.Floor(z0 / Cell); cz <= (int)Math.Floor(z1 / Cell); cz++)
                        for (int cx = (int)Math.Floor(x0 / Cell); cx <= (int)Math.Floor(x1 / Cell); cx++)
                        {
                            List<long> l;
                            if (!segs.TryGetValue(((long)cx << 32) ^ (uint)cz, out l)) continue;
                            foreach (long code in l)
                            {
                                if (!seen.Add(code)) continue;
                                int wj = WayOf(code), j = PointOf(code);
                                if (wj == bi) continue;
                                OsmWay other = ways[wj].Way;
                                if (other.IsBridge && other.Layer >= b.Way.Layer) continue;   // rownolegly most
                                Vector3[] q = ways[wj].Pts;
                                float t, u;
                                if (!Intersect(p[i], p[i + 1], q[j], q[j + 1], out t, out u)) continue;
                                if (t < 0.02f && i == 0 || t > 0.98f && i + 1 == n - 1) continue;      // styk na koncu, nie przejazd pod spodem
                                float gUnder = Mathf.Lerp(q[j].y, q[j + 1].y, u) - heightOffset;
                                need.Add(new Vector2(s[i] + t * (s[i + 1] - s[i]), gUnder + Clearance));
                            }
                        }
                }

                var deck = new float[n];
                for (int i = 0; i < n; i++)
                {
                    float gi = p[i].y - heightOffset;
                    float d = Math.Max(Mathf.Lerp(g0, gN, s[i] / L), gi + 0.3f);
                    foreach (Vector2 c in need) d = Math.Max(d, c.y - MaxGrade * Math.Abs(s[i] - c.x));
                    deck[i] = d;
                    b.Extra[i] = Math.Max(b.Extra[i], d - gi);
                }

                Ramp(ways, nodes, p[0], deck[0] - g0, bi, 0);
                Ramp(ways, nodes, p[n - 1], deck[n - 1] - gN, bi, 0);
            }
        }

        /// <summary>Najazd: od wezla 'at' unos 'e' maleje o MaxGrade na metr wzdluz kazdej
        /// podlaczonej drogi (poza mostem 'from'), dalej przez kolejne wezly.</summary>
        private static void Ramp(List<StreetsideBuilder.WayInfo> ways, Dictionary<long, List<long>> nodes,
                                 Vector3 at, float e, int from, int depth)
        {
            if (e <= 0.05f || depth > 5) return;
            List<long> l;
            if (!nodes.TryGetValue(Key(at.x, at.z, 0.1f), out l)) return;
            foreach (long code in l)
            {
                int wi = WayOf(code), idx = PointOf(code);
                if (wi == from) continue;
                StreetsideBuilder.WayInfo w = ways[wi];
                if (w.Way.IsBridge) continue;
                Vector3[] p = w.Pts;
                if (idx < p.Length - 1) Walk(ways, nodes, w, wi, idx, +1, e, depth);
                if (idx > 0) Walk(ways, nodes, w, wi, idx, -1, e, depth);
            }
        }

        private static void Walk(List<StreetsideBuilder.WayInfo> ways, Dictionary<long, List<long>> nodes,
                                 StreetsideBuilder.WayInfo w, int wi, int start, int dir, float e, int depth)
        {
            Vector3[] p = w.Pts;
            float d = 0f;
            int i = start;
            while (true)
            {
                float lift = e - MaxGrade * d;
                if (lift <= 0f) return;
                if (lift <= w.Extra[i] + 0.001f && i != start) return;     // tu juz jest wyzej (inny most)
                w.Extra[i] = Math.Max(w.Extra[i], lift);
                int nx = i + dir;
                if (nx < 0 || nx >= p.Length)
                {
                    Ramp(ways, nodes, p[i], lift, wi, depth + 1);            // najazd ciagnie sie dalej
                    return;
                }
                d += Horizontal(p[nx] - p[i]);
                i = nx;
            }
        }

        private static bool Intersect(Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float t, out float u)
        {
            t = u = 0f;
            float rx = b.x - a.x, rz = b.z - a.z, sx = d.x - c.x, sz = d.z - c.z;
            float den = rx * sz - rz * sx;
            if (Math.Abs(den) < 1e-6f) return false;
            float qx = c.x - a.x, qz = c.z - a.z;
            t = (qx * sz - qz * sx) / den;
            u = (qx * rz - qz * rx) / den;
            return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
        }

        private static float Horizontal(Vector3 v) { return Mathf.Sqrt(v.x * v.x + v.z * v.z); }

        private static long Key(float x, float z, float cell)
        {
            return ((long)(int)Math.Floor(x / cell) << 32) ^ (uint)(int)Math.Floor(z / cell);
        }

        // Droga w gornych 32 bitach, punkt (indeks w jej Pts) w dolnych - jako long z gory,
        // wiec liczba drog w kaflu nigdy tego nie przepelni (int przy << 20 przekrecal sie juz przy ~2000 drog).
        private static long Code(int wi, int i) { return ((long)wi << 32) | (uint)i; }
        private static int WayOf(long code) { return (int)(code >> 32); }
        private static int PointOf(long code) { return (int)(code & 0xFFFFFFFFL); }
    }
}
