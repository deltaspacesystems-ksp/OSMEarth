using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Poligony pokrycia terenu naciagniete na teren.
    ///
    /// Sam obrys nie wystarczy: park rozciagniety przez pagorek przeciolby teren
    /// na wylot, bo trojkat jest plaski. Dlatego kazdy trojkat jest dzielony przez
    /// najdluzszy bok tak dlugo, az bok zejdzie ponizej MaxEdge - a kazdy nowy
    /// wierzcholek dostaje wlasna wysokosc z siatki terenu.
    ///
    /// Bezpieczne watkowo - jak reszta builderow.
    /// </summary>
    public static class LanduseMeshBuilder
    {
        /// <summary>
        /// Kryterium podzialu to ODCHYLKA OD TERENU, nie sama dlugosc boku.
        /// Dzielenie wszystkiego do stalej dlugosci mielilo geometrie takze na
        /// plaskim terenie: na probce Krakowa wyczerpywalo budzet trojkatow
        /// i czesc poligonow w ogole nie powstawala.
        /// Teraz plaska laka zostaje jednym trojkatem, a park na zboczu sie dzieli.
        /// </summary>
        private const float MaxSagM = 0.6f;

        /// <summary>Twardy limit na bardzo dlugie boki - nawet plaski, ale ogromny
        /// trojkat warto podzielic, zeby oswietlenie nie skakalo.</summary>
        private const float MaxEdge = 150f;
        private const int MaxDepth = 10;

        /// <summary>Gorna granica poligonu w m2 - powyzej lepiej zostawic satelite.</summary>
        private const float MaxAreaM2 = 1200000f;   // 1.2 km2

        public static MeshData Build(TerrainGrid grid, double bodyRadius, List<OsmWay> ways,
                                     double lat0, double lon0, double alt0,
                                     int maxTriangles, float clipE, float clipN,
                                     out int areas)
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>[AreaKinds.Count];
            for (int i = 0; i < AreaKinds.Count; i++) tris[i] = new List<int>();

            areas = 0;
            int total = 0;
            var ring = new List<Vector2>();
            var heights = new List<float>();

            foreach (OsmWay way in ways)
            {
                if (!way.IsArea) continue;
                if (total >= maxTriangles) break;

                // WODY NIE RYSUJEMY. Scatterer i Mirage renderuja ja poprawnie,
                // a plaski poligon OSM lezy dokladnie na poziomie morza - wychodzil
                // z tego migoczacy, lsniacy placek z prosta krawedzia przecinajaca
                // linie brzegowa (laguna przy KSC).
                if (way.Area == AreaKind.Water) continue;

                ring.Clear();
                heights.Clear();

                int n = way.Points.Count;
                if (n >= 2 &&
                    Math.Abs(way.Points[0].Lat - way.Points[n - 1].Lat) < 1e-9 &&
                    Math.Abs(way.Points[0].Lon - way.Points[n - 1].Lon) < 1e-9)
                    n--;
                if (n < 3) continue;

                float lift = AreaKinds.HeightOffset(way.Area);

                for (int i = 0; i < n; i++)
                {
                    GeoPoint g = way.Points[i];
                    double east, north;
                    Geo.ToLocalMeters(g.Lat, g.Lon, lat0, lon0, bodyRadius, out east, out north);
                    ring.Add(new Vector2((float)east, (float)north));
                }

                // Przyciecie do kafla. Kompleks lesny potrafi dotykac kilkunastu
                // kafli naraz, a bez tego kazdy z nich budowal go w CALOSCI -
                // ta sama plachta lezala kilkanascie razy jedna na drugiej.
                ClipToRect(ring, clipE, clipN);
                n = ring.Count;
                if (n < 3) continue;

                for (int i = 0; i < n; i++)
                    heights.Add(HeightLocal(grid, lat0, lon0, bodyRadius, alt0,
                                            ring[i].x, ring[i].y) + lift);

                // Gigantyczne poligony (rezerwaty, cale dzielnice) to plaskie plachty
                // przykrywajace zdjecia satelitarne Mirage'a. Na tej skali prawdziwy
                // teren wyglada lepiej niz jednolity kolor.
                if (PolygonArea(ring) > MaxAreaM2) continue;

                List<int> cap = Polygon.Triangulate(ring);
                if (cap == null) continue;

                List<int> target = tris[(int)way.Area];
                for (int i = 0; i + 2 < cap.Count; i += 3)
                {
                    Vector3 a = new Vector3(ring[cap[i]].x, heights[cap[i]], ring[cap[i]].y);
                    Vector3 b = new Vector3(ring[cap[i + 1]].x, heights[cap[i + 1]], ring[cap[i + 1]].y);
                    Vector3 c = new Vector3(ring[cap[i + 2]].x, heights[cap[i + 2]], ring[cap[i + 2]].y);

                    total += Subdivide(verts, uvs, target, grid, lat0, lon0, bodyRadius, alt0,
                                       lift, a, b, c, 0);
                    if (total >= maxTriangles) break;
                }

                areas++;
            }

            return MeshData.From(verts, uvs, tris, "OSMLanduse");
        }

        /// <summary>
        /// Sutherland-Hodgman: przycina wielokat do prostokata kafla. Prostokat
        /// jest wypukly, wiec ta klasyczna metoda wystarcza - kolejno obcinamy
        /// czterema polplaszczyznami, za kazdym razem wstawiajac punkt przeciecia
        /// tam, gdzie krawedz przechodzi przez granice.
        /// </summary>
        private static void ClipToRect(List<Vector2> poly, float clipE, float clipN)
        {
            ClipHalfPlane(poly, 0, -clipE, true);    // x >= -clipE
            ClipHalfPlane(poly, 0, clipE, false);    // x <=  clipE
            ClipHalfPlane(poly, 1, -clipN, true);    // y >= -clipN
            ClipHalfPlane(poly, 1, clipN, false);    // y <=  clipN
        }

        private static readonly List<Vector2> clipBuf = new List<Vector2>(64);

        private static void ClipHalfPlane(List<Vector2> poly, int axis, float bound, bool keepGreater)
        {
            int n = poly.Count;
            if (n == 0) return;

            clipBuf.Clear();

            for (int i = 0; i < n; i++)
            {
                Vector2 cur = poly[i];
                Vector2 prev = poly[(i + n - 1) % n];

                float cv = axis == 0 ? cur.x : cur.y;
                float pv = axis == 0 ? prev.x : prev.y;

                bool curIn = keepGreater ? cv >= bound : cv <= bound;
                bool prevIn = keepGreater ? pv >= bound : pv <= bound;

                if (curIn)
                {
                    if (!prevIn) clipBuf.Add(Intersect(prev, cur, pv, cv, bound));
                    clipBuf.Add(cur);
                }
                else if (prevIn)
                {
                    clipBuf.Add(Intersect(prev, cur, pv, cv, bound));
                }
            }

            poly.Clear();
            poly.AddRange(clipBuf);
        }

        private static Vector2 Intersect(Vector2 a, Vector2 b, float av, float bv, float bound)
        {
            float d = bv - av;
            float t = Math.Abs(d) < 1e-6f ? 0f : (bound - av) / d;
            return new Vector2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
        }

        /// <summary>Dzieli trojkat przez najdluzszy bok, dopoki nie przylegnie do terenu.</summary>
        private static int Subdivide(List<Vector3> verts, List<Vector2> uvs, List<int> tris,
                                     TerrainGrid grid, double lat0, double lon0,
                                     double bodyRadius, double alt0, float lift,
                                     Vector3 a, Vector3 b, Vector3 c, int depth)
        {
            float ab = Flat(a, b), bc = Flat(b, c), ca = Flat(c, a);

            // najdluzszy bok idzie na podzial; p-q to on, r to wierzcholek naprzeciw
            Vector3 p, q, r;
            float longest;
            if (ab >= bc && ab >= ca) { p = a; q = b; r = c; longest = ab; }
            else if (bc >= ca) { p = b; q = c; r = a; longest = bc; }
            else { p = c; q = a; r = b; longest = ca; }

            if (depth < MaxDepth && longest > 2f)
            {
                var mid = new Vector3((p.x + q.x) * 0.5f, 0f, (p.z + q.z) * 0.5f);
                float terrain = HeightLocal(grid, lat0, lon0, bodyRadius, alt0, mid.x, mid.z) + lift;

                // ile trojkat "wisi" nad terenem w polowie najdluzszego boku
                float sag = Mathf.Abs(terrain - (p.y + q.y) * 0.5f);

                if (sag > MaxSagM || longest > MaxEdge)
                {
                    mid.y = terrain;
                    return Subdivide(verts, uvs, tris, grid, lat0, lon0, bodyRadius, alt0, lift, p, mid, r, depth + 1)
                         + Subdivide(verts, uvs, tris, grid, lat0, lon0, bodyRadius, alt0, lift, mid, q, r, depth + 1);
                }
            }

            int v0 = verts.Count;
            verts.Add(a); uvs.Add(new Vector2(a.x * 0.05f, a.z * 0.05f));
            verts.Add(b); uvs.Add(new Vector2(b.x * 0.05f, b.z * 0.05f));
            verts.Add(c); uvs.Add(new Vector2(c.x * 0.05f, c.z * 0.05f));

            // powierzchnia ma patrzec do gory
            if (Vector3.Cross(b - a, c - a).y > 0f)
            {
                tris.Add(v0); tris.Add(v0 + 1); tris.Add(v0 + 2);
            }
            else
            {
                tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 1);
            }
            return 1;
        }

        private static float PolygonArea(List<Vector2> p)
        {
            float s = 0f;
            for (int i = 0, n = p.Count; i < n; i++)
            {
                Vector2 a = p[i], b = p[(i + 1) % n];
                s += a.x * b.y - b.x * a.y;
            }
            return Mathf.Abs(s) * 0.5f;
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Wysokosc lokalna z metrow lokalnych: odwrotnosc Geo.ToLocalMeters
        /// plus poprawka na krzywizne, ta sama co w pozostalych builderach.</summary>
        public static float HeightLocal(TerrainGrid grid, double lat0, double lon0,
                                        double bodyRadius, double alt0, float east, float north)
        {
            double lat = lat0 + north / (bodyRadius * Geo.DegToRad);
            double lon = lon0 + east / (bodyRadius * Geo.DegToRad * Math.Cos(lat0 * Geo.DegToRad));
            double h = grid.HeightAt(lat, lon) - Geo.CurvatureDrop(east, north, bodyRadius);
            return (float)(h - alt0);
        }

        // --- ear clipping, ten sam co przy budynkach ---

    }
}
