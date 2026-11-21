using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Kosciol z samego obrysu OSM (building=church/cathedral/chapel) - tam, gdzie nie ma
    /// modelu LoD2. Wczesniej wychodzil z tego 18-metrowy blok z kratka okien mieszkan.
    ///
    /// Teraz: tynkowane sciany z wysokimi oknami zakonczonymi lukiem, stromy dach
    /// (dwuspadowy nad prostokatem, stozek nad obrysem okraglym) i - przy wiekszej
    /// bryle na planie wydluzonym - wieza z ostrym helmem na jednym koncu nawy.
    /// </summary>
    public static partial class BuildingMeshBuilder
    {
        public static bool IsChurch(string tag)
        {
            return tag == "church" || tag == "cathedral" || tag == "chapel";
        }

        private static void AddChurch(Accum acc, List<Vector2> ring, float yBase, float yGround,
                                      float totalH, string tag, bool explicitHeight, uint hash)
        {
            float area = Area2(ring);
            float wallH = explicitHeight ? Mathf.Max(4f, totalH * 0.58f)
                        : tag == "chapel" ? 5.5f : tag == "cathedral" ? 17f : Mathf.Clamp(6f + Mathf.Sqrt(area) * 0.18f, 8f, 14f);
            float yTop = yGround + wallH;

            // kolor dachu: blacha grafitowa, miedz (zielen) albo ceramika
            int pick = (int)((hash >> 5) % 10u);
            int roofSub = RoofColorSub + (pick < 5 ? 3 : pick < 7 ? 6 : pick < 9 ? 0 : 4);

            Walls(acc, ring, yBase, yGround, yTop, ChurchSub, 0f);

            Vector2 c, ax;
            float hu, hv;
            bool rect = MinAreaRect(ring, out c, out ax, out hu, out hv);
            float fill = rect && hu * hv > 1e-3f ? area / (4f * hu * hv) : 0f;
            float aspect = rect ? Mathf.Max(hu, hv) / Mathf.Max(0.1f, Mathf.Min(hu, hv)) : 1f;

            // plaska czapa zawsze (zamyka bryle pod dachem)
            Cap(acc, ring, yTop, roofSub);

            bool round = ring.Count >= 10 && aspect < 1.25f && fill > 0.70f && fill < 0.88f;
            if (round || !rect)
            {
                Vector2 cc = Vector2.zero;
                foreach (Vector2 p in ring) cc += p;
                cc /= ring.Count;
                float r = Mathf.Sqrt(area / Mathf.PI) + EaveM;
                AddCone(acc, cc, r, yTop, Mathf.Max(3f, r * 0.75f), 24, roofSub);
                return;
            }

            // dach dwuspadowy, stromy (~50 st.)
            if (hv > hu) { ax = new Vector2(-ax.y, ax.x); float t = hu; hu = hv; hv = t; }
            AddSteepGable(acc, c, ax, hu + EaveM, hv + EaveM, yTop, (hv + EaveM) * 1.2f, roofSub);

            // wieza na jednym koncu nawy (strona z hasza)
            if (area > 180f && aspect > 1.35f)
            {
                float side = Mathf.Clamp(hv * 1.1f, 4f, 8.5f);
                float dir = ((hash >> 11) & 1u) == 0u ? 1f : -1f;
                Vector2 tc = c + ax * dir * (hu - side * 0.5f);
                Vector2 ay = new Vector2(-ax.y, ax.x);
                var sq = new List<Vector2>
                {
                    tc - ax * side * 0.5f - ay * side * 0.5f,
                    tc + ax * side * 0.5f - ay * side * 0.5f,
                    tc + ax * side * 0.5f + ay * side * 0.5f,
                    tc - ax * side * 0.5f + ay * side * 0.5f
                };
                float towerTop = yTop + (hv + EaveM) * 1.2f + Mathf.Max(6f, wallH * 0.8f);
                Walls(acc, sq, yBase, yGround, towerTop, ChurchSub, 0.37f);
                Cap(acc, sq, towerTop, roofSub);
                AddCone(acc, tc, side * 0.5f * 1.414f + 0.3f, towerTop, side * 1.8f, 4, roofSub, 45f);
            }
        }

        /// <summary>Sciany z teksturą kosciola: u metrycznie, v od gruntu (okna na stalej wysokosci).</summary>
        private static void Walls(Accum acc, List<Vector2> ring, float yBase, float yGround, float yTop, int sub, float uOff)
        {
            int n = ring.Count;
            Vector2 c = Vector2.zero;
            foreach (Vector2 p in ring) c += p;
            c /= n;
            float run = uOff;
            float v0 = (yBase - yGround) * FacadeTexture.ChurchUvPerMeterV;
            float v1 = (yTop - yGround) * FacadeTexture.ChurchUvPerMeterV;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = ring[i], b = ring[(i + 1) % n];
                float len = (b - a).magnitude;
                Vector2 e = b - a;
                bool outward = Vector2.Dot(new Vector2(e.y, -e.x), (a + b) * 0.5f - c) > 0f;
                AddQuad(acc.Verts, acc.Uvs, acc.Tris[sub], a, b, yBase, yTop,
                        run, run + len * FacadeTexture.ChurchUvPerMeterU, v0, v1, outward);
                run += len * FacadeTexture.ChurchUvPerMeterU;
            }
        }

        private static void Cap(Accum acc, List<Vector2> ring, float y, int sub)
        {
            List<int> cap = Polygon.Triangulate(ring);
            if (cap == null || cap.Count < 3) return;
            int b = acc.Verts.Count;
            float k = FacadeTexture.RoofPitchUvPerMeter;
            foreach (Vector2 p in ring) { acc.Verts.Add(new Vector3(p.x, y, p.y)); acc.Uvs.Add(p * k); }
            Vector3 p0 = acc.Verts[b + cap[0]], p1 = acc.Verts[b + cap[1]], p2 = acc.Verts[b + cap[2]];
            bool up = Vector3.Cross(p1 - p0, p2 - p0).y > 0f;
            for (int i = 0; i + 2 < cap.Count; i += 3)
            {
                acc.Tris[sub].Add(b + cap[i]);
                acc.Tris[sub].Add(b + (up ? cap[i + 1] : cap[i + 2]));
                acc.Tris[sub].Add(b + (up ? cap[i + 2] : cap[i + 1]));
            }
        }

        /// <summary>Stozek (sides=4: helm ostroslupowy) - kazda polac z wlasnymi wierzcholkami.</summary>
        private static void AddCone(Accum acc, Vector2 c, float r, float y, float rise, int sides, int sub, float rotDeg = 0f)
        {
            float k = FacadeTexture.RoofPitchUvPerMeter;
            Vector3 apex = new Vector3(c.x, y + rise, c.y);
            float slant = Mathf.Sqrt(r * r + rise * rise);
            for (int i = 0; i < sides; i++)
            {
                float a0 = (i * 360f / sides + rotDeg) * Mathf.Deg2Rad, a1 = ((i + 1) * 360f / sides + rotDeg) * Mathf.Deg2Rad;
                var p0 = new Vector3(c.x + Mathf.Cos(a0) * r, y, c.y + Mathf.Sin(a0) * r);
                var p1 = new Vector3(c.x + Mathf.Cos(a1) * r, y, c.y + Mathf.Sin(a1) * r);
                int b = acc.Verts.Count;
                float w = Vector3.Distance(p0, p1);
                acc.Verts.Add(p0); acc.Verts.Add(p1); acc.Verts.Add(apex);
                acc.Uvs.Add(new Vector2(0f, 0f)); acc.Uvs.Add(new Vector2(w * k, 0f)); acc.Uvs.Add(new Vector2(w * k * 0.5f, slant * k));
                // normalna na zewnatrz i do gory: (p1-p0) x (apex-p0) ma skladowa y < 0 przy tej kolejnosci
                acc.Tris[sub].Add(b); acc.Tris[sub].Add(b + 2); acc.Tris[sub].Add(b + 1);
            }
        }

        /// <summary>Stromy dach dwuspadowy nad prostokatem (c, os ax, polowy hu/hv) ze szczytami z tynku.</summary>
        private static void AddSteepGable(Accum acc, Vector2 c, Vector2 ax, float hu, float hv, float y, float rise, int sub)
        {
            Vector2 ay = new Vector2(-ax.y, ax.x);
            Vector2 a0 = c - ax * hu - ay * hv, a1 = c + ax * hu - ay * hv;
            Vector2 b1 = c + ax * hu + ay * hv, b0 = c - ax * hu + ay * hv;
            Vector2 r0 = c - ax * hu, r1 = c + ax * hu;
            float slope = Mathf.Sqrt(hv * hv + rise * rise);
            float k = FacadeTexture.RoofPitchUvPerMeter;
            AddSlope(acc.Verts, acc.Uvs, acc.Tris[sub], a0, a1, r1, r0, y, y + rise, hu * 2f, slope, k);
            AddSlope(acc.Verts, acc.Uvs, acc.Tris[sub], b1, b0, r0, r1, y, y + rise, hu * 2f, slope, k);
            AddGableEnd(acc.Verts, acc.Uvs, acc.Tris[ChurchSub], a0, b0, r0, y, y + rise);
            AddGableEnd(acc.Verts, acc.Uvs, acc.Tris[ChurchSub], b1, a1, r1, y, y + rise);
        }
    }
}
