using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Ogrodzenia z OSM (barrier=fence/wall/hedge/...): plot jako cienka sciana widoczna
    /// z obu stron, zywoplot i mur jako bryla z gora. Punkty co 6 m i wysokosci liczone
    /// od gruntu (uv2 Ground), wiec ogrodzenie idzie po terenie jak droga.
    /// Siedzi w siatce drog, wiec ma kolider - lazik zatrzyma sie na plocie.
    /// </summary>
    internal static class BarrierBuilder
    {
        private const float Step = 6f;

        private static void Params(string type, out int sub, out float h, out float w)
        {
            switch (type)
            {
                case "hedge": case "hedge_bank": sub = RoadMeshBuilder.SubHedge; h = 1.6f; w = 0.9f; break;
                case "wall": sub = RoadMeshBuilder.SubWall; h = 1.8f; w = 0.3f; break;
                case "city_wall": sub = RoadMeshBuilder.SubWall; h = 5f; w = 1.2f; break;
                case "retaining_wall": sub = RoadMeshBuilder.SubWall; h = 1.0f; w = 0.4f; break;
                case "guard_rail": sub = RoadMeshBuilder.SubCurb; h = 0.75f; w = 0f; break;
                default: sub = RoadMeshBuilder.SubFence; h = 1.5f; w = 0f; break;    // fence, wire_fence, wood_fence
            }
        }

        public static void Build(List<OsmWay> ways, double lat0, double lon0, double r, TerrainGrid grid, double alt0,
                                 float heightOffset, float clipE, float clipN,
                                 List<Vector3> verts, List<Vector2> uvs, List<Vector2> uv2, List<int>[] tris)
        {
            foreach (OsmWay way in ways)
            {
                if (way.Barrier == null || way.Points.Count < 2) continue;
                int sub; float h, w;
                Params(way.Barrier, out sub, out h, out w);

                // punkty co <= 6 m, wysokosc z siatki terenu (poprawi ja przyklejanie)
                var pts = new List<Vector3>();
                for (int i = 0; i < way.Points.Count; i++)
                {
                    Vector3 cur = Local(way.Points[i], grid, lat0, lon0, r, alt0);
                    if (i > 0)
                    {
                        Vector3 prev = pts[pts.Count - 1];
                        float len = Mathf.Sqrt((cur.x - prev.x) * (cur.x - prev.x) + (cur.z - prev.z) * (cur.z - prev.z));
                        int steps = (int)Math.Ceiling(len / Step);
                        for (int k = 1; k < steps; k++) pts.Add(Vector3.Lerp(prev, cur, k / (float)steps));
                        if (len < 0.2f) continue;
                    }
                    pts.Add(cur);
                }
                if (pts.Count < 2) continue;

                List<int> t = tris[sub];
                float along = 0f;
                int b0 = verts.Count;
                int n = pts.Count;
                for (int k = 0; k < n; k++)
                {
                    if (k > 0) along += Vector3.Distance(pts[k], pts[k - 1]);
                    Vector3 dir = k + 1 < n ? pts[k + 1] - pts[k] : pts[k] - pts[k - 1];
                    dir.y = 0f;
                    Vector3 side = dir.sqrMagnitude > 1e-6f ? new Vector3(dir.z, 0f, -dir.x).normalized * (w * 0.5f) : Vector3.zero;
                    float u = along / 2f;
                    // dol lewy, gora lewa, gora prawa, dol prawy (plot: lewy == prawy)
                    Add(verts, uvs, uv2, pts[k] - side, -0.15f, new Vector2(u, 0f));
                    Add(verts, uvs, uv2, pts[k] - side, h, new Vector2(u, h / 2f));
                    Add(verts, uvs, uv2, pts[k] + side, h, new Vector2(u, h / 2f));
                    Add(verts, uvs, uv2, pts[k] + side, -0.15f, new Vector2(u, 0f));
                }
                for (int k = 0; k + 1 < n; k++)
                {
                    Vector3 m = (pts[k] + pts[k + 1]) * 0.5f;
                    if (m.x < -clipE || m.x >= clipE || m.z < -clipN || m.z >= clipN) continue;
                    int a = b0 + k * 4, b = a + 4;
                    if (w <= 0f)
                    {
                        // cienki plot: jedna sciana, obie strony
                        Quad(t, a, b, b + 1, a + 1);
                        Quad(t, a, a + 1, b + 1, b);
                    }
                    else
                    {
                        // kolejnosc tak, zeby normalne szly na zewnatrz (przeliczone: gora x kierunek itd.)
                        Quad(t, a, b, b + 1, a + 1);             // bok lewy (-side)
                        Quad(t, a + 3, a + 2, b + 2, b + 3);     // bok prawy (+side)
                        Quad(t, a + 1, b + 1, b + 2, a + 2);     // wierzch
                    }
                }
            }
        }

        private static void Quad(List<int> t, int a, int b, int c, int d)
        {
            t.Add(a); t.Add(b); t.Add(c);
            t.Add(a); t.Add(c); t.Add(d);
        }

        private static void Add(List<Vector3> verts, List<Vector2> uvs, List<Vector2> uv2, Vector3 p, float lift, Vector2 uv)
        {
            verts.Add(new Vector3(p.x, p.y + lift, p.z));
            uvs.Add(uv);
            uv2.Add(RoadMeshBuilder.Ground(lift));
        }

        private static Vector3 Local(GeoPoint g, TerrainGrid grid, double lat0, double lon0, double r, double alt0)
        {
            double e, n;
            Geo.ToLocalMeters(g.Lat, g.Lon, lat0, lon0, r, out e, out n);
            double y = grid.HeightAt(g.Lat, g.Lon) - alt0 - Geo.CurvatureDrop(e, n, r);
            return new Vector3((float)e, (float)y, (float)n);
        }
    }
}
