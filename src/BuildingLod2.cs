using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Obrysy podstaw modeli LoD2 w lokalnych metrach kafla - do sprawdzania,
    /// czy obrys OSM jest juz pokryty modelem 3D (wtedy OSM odpada).
    /// </summary>
    public sealed class Lod2Index
    {
        private const float Cell = 20f;
        private readonly Dictionary<long, List<Vector2[]>> grid = new Dictionary<long, List<Vector2[]>>();

        public Lod2Index(List<Lod2Building> list, double lat0, double lon0, double r)
        {
            foreach (Lod2Building b in list)
            {
                int n = b.FootLat.Length;
                if (n < 3) continue;
                var poly = new Vector2[n];
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                for (int i = 0; i < n; i++)
                {
                    double e, no;
                    Geo.ToLocalMeters(b.FootLat[i], b.FootLon[i], lat0, lon0, r, out e, out no);
                    poly[i] = new Vector2((float)e, (float)no);
                    minX = Math.Min(minX, poly[i].x); maxX = Math.Max(maxX, poly[i].x);
                    minZ = Math.Min(minZ, poly[i].y); maxZ = Math.Max(maxZ, poly[i].y);
                }
                for (int cz = (int)Math.Floor(minZ / Cell); cz <= (int)Math.Floor(maxZ / Cell); cz++)
                    for (int cx = (int)Math.Floor(minX / Cell); cx <= (int)Math.Floor(maxX / Cell); cx++)
                    {
                        long k = ((long)cx << 32) ^ (uint)cz;
                        List<Vector2[]> l;
                        if (!grid.TryGetValue(k, out l)) grid[k] = l = new List<Vector2[]>();
                        l.Add(poly);
                    }
            }
        }

        /// <summary>Obrys OSM jest pokryty, gdy jego srodek lezy w podstawie modelu 3D
        /// (OSM i BDOT10k rysuja te same budynki, roznia sie o pojedyncze metry).</summary>
        public bool Covers(List<Vector2> ring)
        {
            Vector2 c = Vector2.zero;
            foreach (Vector2 p in ring) c += p;
            c /= ring.Count;
            List<Vector2[]> l;
            long k = ((long)(int)Math.Floor(c.x / Cell) << 32) ^ (uint)(int)Math.Floor(c.y / Cell);
            if (!grid.TryGetValue(k, out l)) return false;
            foreach (Vector2[] poly in l) if (Inside(poly, c)) return true;
            return false;
        }

        private static bool Inside(Vector2[] poly, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                    p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            return inside;
        }
    }

    public static partial class BuildingMeshBuilder
    {
        /// <summary>
        /// Model LoD2 -&gt; siatka w tych samych submeshach co budynki OSM (kubelek elewacji,
        /// dach plaski / dachowka), wiec wyglada spojnie i nie potrzebuje nowych materialow.
        /// Wierzcholki sa rozdzielone na trojkat: kazda sciana ma wlasne UV (okna ukladaja sie
        /// wzdluz sciany) i ostre krawedzie. Dol scian schodzi FoundationDepth pod teren,
        /// jak przy budynkach OSM - na zboczu nie ma szczeliny pod domem.
        /// </summary>
        private static void AddLod2(Accum full, Accum coarse, BuildingResult res, TerrainGrid grid, Lod2Building b,
                                    double lat0, double lon0, double alt0, double R, float clipE, float clipN)
        {
            double ae, an;
            Geo.ToLocalMeters(b.Lat, b.Lon, lat0, lon0, R, out ae, out an);
            if (ae < -clipE || ae >= clipE || an < -clipN || an >= clipN) return;     // nalezy do sasiedniego kafla

            int n = b.VLat.Length;
            if (n < 3) return;

            // grunt: najnizszy punkt terenu gry pod podstawa (jak w obrysach OSM)
            double minAlt = double.MaxValue;
            for (int i = 0; i < b.FootLat.Length; i++)
            {
                double e, no;
                Geo.ToLocalMeters(b.FootLat[i], b.FootLon[i], lat0, lon0, R, out e, out no);
                double a = grid.HeightAt(b.FootLat[i], b.FootLon[i]) - Geo.CurvatureDrop(e, no, R);
                if (a < minAlt) minAlt = a;
            }
            if (minAlt == double.MaxValue) return;
            float yGround = (float)(minAlt - alt0);

            var p = new Vector3[n];
            float top = 0f;
            Vector2 c = Vector2.zero;
            for (int i = 0; i < n; i++)
            {
                double e, no;
                Geo.ToLocalMeters(b.VLat[i], b.VLon[i], lat0, lon0, R, out e, out no);
                float h = b.VH[i];
                float y = h < 0.05f ? yGround - FoundationDepth : yGround + h;
                p[i] = new Vector3((float)e, y, (float)no);
                c += new Vector2((float)e, (float)no);
                top = Math.Max(top, h);
            }
            c /= n;

            uint hash = PosHash(b.Lat, b.Lon);
            int bucket = (int)(hash % (uint)Buckets);

            // pole podstawy -> dom jednorodzinny czy blok
            float area;
            {
                int fn = b.FootLat.Length;
                var fx = new double[fn]; var fz = new double[fn];
                for (int i = 0; i < fn; i++) Geo.ToLocalMeters(b.FootLat[i], b.FootLon[i], lat0, lon0, R, out fx[i], out fz[i]);
                double a2 = 0;
                for (int i = 0, j = fn - 1; i < fn; j = i++) a2 += fx[j] * fz[i] - fx[i] * fz[j];
                area = (float)Math.Abs(a2 * 0.5);
            }
            if (IsHouse("yes", area, top, b.RoofType != 1000 && b.Roofs.Length > 3))
                bucket = HouseSub + (int)((hash >> 4) % (uint)HouseBuckets);
            float uOff = ((hash >> 8) % 64u) / 8f;
            float vOff = ((hash >> 16) % 8u) / 8f;

            // kolor dachu zmierzony na ortofotomapie -> najblizszy z palety
            int roofSub = b.HasColor ? RoofColorSub + MaterialPalette.RoofColorIndex(b.R, b.G, b.B) : RoofPitchSub;

            Emit(full, p, b.Walls, b.Roofs, c, yGround, bucket, uOff, vOff, roofSub);
            if (top >= CoarseMinHeight) Emit(coarse, p, b.Walls, b.Roofs, c, yGround, bucket, uOff, vOff, roofSub);
            res.Lod2++;
            res.Built++;
        }

        private static void Emit(Accum acc, Vector3[] p, int[] walls, int[] roofs, Vector2 c, float yGround,
                                 int bucket, float uOff, float vOff, int pitchSub)
        {
            List<Vector3> v = acc.Verts;
            List<Vector2> uv = acc.Uvs;
            bool house = bucket >= HouseSub;
            float uPerM = house ? FacadeTexture.HouseUvPerMeterU : FacadeTexture.UvPerMeterU;
            float vPerM = house ? FacadeTexture.HouseUvPerMeterV : FacadeTexture.UvPerMeterV;
            HashSet<int> eaveVerts = EaveVertices(roofs);

            for (int t = 0; t + 2 < walls.Length; t += 3)
            {
                Vector3 a = p[walls[t]], b = p[walls[t + 1]], d = p[walls[t + 2]];
                Vector3 nrm = Vector3.Cross(b - a, d - a);
                var hn = new Vector2(nrm.x, nrm.z);
                if (hn.sqrMagnitude < 1e-8f) continue;
                // na zewnatrz: od srodka budynku do srodka trojkata
                var mid = new Vector2((a.x + b.x + d.x) / 3f - c.x, (a.z + b.z + d.z) / 3f - c.y);
                if (Vector2.Dot(hn, mid) < 0f) { Vector3 tmp = b; b = d; d = tmp; hn = -hn; }
                hn.Normalize();
                var tan = new Vector2(-hn.y, hn.x);          // poziomo wzdluz sciany

                int i0 = v.Count;
                foreach (Vector3 q in new[] { a, b, d })
                {
                    v.Add(q);
                    uv.Add(new Vector2((q.x * tan.x + q.z * tan.y) * uPerM + uOff,
                                       (q.y - yGround) * vPerM + vOff));
                }
                acc.Tris[bucket].Add(i0); acc.Tris[bucket].Add(i0 + 1); acc.Tris[bucket].Add(i0 + 2);

                // Druga strona sciany. Test "na zewnatrz od srodka" myli sie na wewnetrznych
                // narozach budynkow w ksztalcie L/U - sciana patrzyla do srodka i z ulicy
                // widac bylo dziure. Obie strony = nigdy dziury. Osobne wierzcholki, zeby
                // normalne sie nie znosily.
                int j0 = v.Count;
                foreach (Vector3 q in new[] { a, d, b })
                {
                    v.Add(q);
                    uv.Add(new Vector2((q.x * tan.x + q.z * tan.y) * uPerM + uOff,
                                       (q.y - yGround) * vPerM + vOff));
                }
                acc.Tris[bucket].Add(j0); acc.Tris[bucket].Add(j0 + 1); acc.Tris[bucket].Add(j0 + 2);
            }

            for (int t = 0; t + 2 < roofs.Length; t += 3)
            {
                int ia = roofs[t], ib = roofs[t + 1], id = roofs[t + 2];
                Vector3 a = p[ia], b = p[ib], d = p[id];
                Vector3 nrm = Vector3.Cross(b - a, d - a);
                float len = nrm.magnitude;
                if (len < 1e-6f) continue;
                if (nrm.y < 0f) { Vector3 tmp = b; b = d; d = tmp; int ti = ib; ib = id; id = ti; nrm = -nrm; }
                bool pitched = nrm.y / len < 0.97f;                 // spadek ponad ok. 14 st.

                // Okap: LoD2 konczy dach rowno ze sciana, a prawdziwy dach wystaje ok. 45 cm
                // (tez nad szczytem). Wierzcholki brzegu dachu przesuwamy na zewnatrz od srodka
                // budynku, w plaszczyznie tej polaci, a od spodu dokladamy podbitke.
                if (pitched)
                {
                    Vector3 n1 = nrm / len;
                    a = Eave(a, ia, eaveVerts, c, n1);
                    b = Eave(b, ib, eaveVerts, c, n1);
                    d = Eave(d, id, eaveVerts, c, n1);
                    int s0 = v.Count;
                    v.Add(a); v.Add(d); v.Add(b);
                    uv.Add(Vector2.zero); uv.Add(Vector2.zero); uv.Add(Vector2.zero);
                    acc.Tris[RoofEdgeSub].Add(s0); acc.Tris[RoofEdgeSub].Add(s0 + 1); acc.Tris[RoofEdgeSub].Add(s0 + 2);
                }
                int sub = pitched ? pitchSub : RoofSub;
                float k = pitched ? FacadeTexture.RoofPitchUvPerMeter : FacadeTexture.RoofUvPerMeter;

                // dachowka: rzedy maja biec wzdluz okapu, wiec UV w ukladzie spadku dachu
                var down = new Vector2(nrm.x, nrm.z);
                if (down.sqrMagnitude < 1e-8f) down = new Vector2(0f, 1f);
                down.Normalize();
                var along = new Vector2(-down.y, down.x);

                int i0 = v.Count;
                foreach (Vector3 q in new[] { a, b, d })
                {
                    v.Add(q);
                    float su = q.x * along.x + q.z * along.y;
                    float sv = (q.x * down.x + q.z * down.y) / Mathf.Max(0.2f, nrm.y / len);   // dlugosc po spadku
                    uv.Add(new Vector2(su * k + uOff * 7f, sv * k + vOff * 7f));
                }
                acc.Tris[sub].Add(i0); acc.Tris[sub].Add(i0 + 1); acc.Tris[sub].Add(i0 + 2);
            }
        }

        private const float EaveM2 = 0.45f;

        private static Vector3 Eave(Vector3 q, int idx, HashSet<int> eave, Vector2 c, Vector3 n)
        {
            if (!eave.Contains(idx) || n.y < 0.25f) return q;
            var dir = new Vector2(q.x - c.x, q.z - c.y);
            if (dir.sqrMagnitude < 1e-4f) return q;
            dir.Normalize();
            float dx = dir.x * EaveM2, dz = dir.y * EaveM2;
            float dy = -(n.x * dx + n.z * dz) / n.y;              // zostajemy w plaszczyznie polaci
            return new Vector3(q.x + dx, q.y + dy, q.z + dz);
        }

        /// <summary>Wierzcholki na brzegu dachu (krawedzie uzyte przez jeden trojkat dachu).</summary>
        private static HashSet<int> EaveVertices(int[] roofs)
        {
            var count = new Dictionary<long, int>();
            for (int t = 0; t + 2 < roofs.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = roofs[t + k], b = roofs[t + (k + 1) % 3];
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    int n; count.TryGetValue(key, out n); count[key] = n + 1;
                }
            var set = new HashSet<int>();
            foreach (KeyValuePair<long, int> kv in count)
                if (kv.Value == 1) { set.Add((int)(kv.Key >> 32)); set.Add((int)(uint)kv.Key); }
            return set;
        }
    }
}
