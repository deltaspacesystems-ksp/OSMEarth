using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    public class RoadResult
    {
        public MeshData Roads;     // 5 submeshy, po jednym na RoadClass
        public MeshData Lamps;     // 2 submeshe: slupy i klosze
        public int Segments;
        public int LampCount;
    }

    /// <summary>
    /// Zamienia polilinie OSM na wstegi kafla, przyklejone do terenu.
    ///
    /// Wierzcholki liczone sa w LOKALNEJ plaszczyznie stycznej zaczepionej w srodku
    /// kafla (x = wschod, y = gora, z = polnoc). Dzieki temu GameObject wystarczy
    /// co klatke ustawic na nowo (pozycja + rotacja) i cala siatka jedzie razem
    /// z obracajaca sie planeta oraz floating origin - bez przeliczania wierzcholkow.
    ///
    /// Cala klasa jest bezpieczna watkowo: nie dotyka niczego z Unity poza
    /// strukturami Vector2/Vector3, a wysokosci bierze z gotowej TerrainGrid.
    /// </summary>
    public static class RoadMeshBuilder
    {
        private const int LampPoleSub = 0;
        private const int LampHeadSub = 1;

        /// <summary>Ile latarni maksymalnie na kafel. Przy gestej siatce ulic
        /// samo oswietlenie potrafiloby przewyzszyc geometria cala reszte kafla.</summary>
        public const int MaxLampsPerTile = 900;

        public static RoadResult Build(TerrainGrid grid, double bodyRadius, List<OsmWay> ways,
                                       double lat0, double lon0, double alt0,
                                       float heightOffset, float widthScale, bool wantLamps,
                                       float clipE, float clipN)
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>[RoadClasses.Count];
            for (int i = 0; i < RoadClasses.Count; i++) tris[i] = new List<int>();

            var lampVerts = new List<Vector3>();
            var lampUvs = new List<Vector2>();
            var lampTris = new List<int>[2] { new List<int>(), new List<int>() };

            var res = new RoadResult();
            bool sideFlip = false;
            dense = new List<Vector3>(256);

            foreach (OsmWay way in ways)
            {
                if (!way.IsRoad) continue;
                int n = way.Points.Count;
                if (n < 2) continue;

                RoadClass cls = RoadClasses.From(way.Highway);
                float width = OsmXml.WidthFor(way.Highway) * widthScale;
                float halfW = width * 0.5f;

                // 1) polilinia -> lokalne metry + wysokosc z siatki terenu,
                //    zageszczona do MaxSegmentM.
                //
                // Wezly OSM na prostych potrafia byc co 100-300 m. Wysokosc (i potem
                // przyklejenie do PQS) byla liczona tylko w nich, a miedzy nimi szla
                // prosta plyta - pagorek wystawal ponad asfalt. Tester zmierzyl do
                // 10.7 m terenu nad jezdnia, czyli widoczne "dziury w drodze".
                dense.Clear();
                for (int i = 0; i < n; i++)
                {
                    GeoPoint g = way.Points[i];
                    double east, north;
                    Geo.ToLocalMeters(g.Lat, g.Lon, lat0, lon0, bodyRadius, out east, out north);

                    if (i > 0)
                    {
                        GeoPoint pg = way.Points[i - 1];
                        double pe, pn;
                        Geo.ToLocalMeters(pg.Lat, pg.Lon, lat0, lon0, bodyRadius, out pe, out pn);
                        double len = Math.Sqrt((east - pe) * (east - pe) + (north - pn) * (north - pn));
                        int steps = (int)Math.Ceiling(len / MaxSegmentM);
                        for (int k = 1; k < steps; k++)
                        {
                            double t = k / (double)steps;
                            AddPoint(grid, bodyRadius, alt0, heightOffset,
                                     pg.Lat + (g.Lat - pg.Lat) * t, pg.Lon + (g.Lon - pg.Lon) * t,
                                     pe + (east - pe) * t, pn + (north - pn) * t);
                        }
                    }
                    AddPoint(grid, bodyRadius, alt0, heightOffset, g.Lat, g.Lon, east, north);
                }
                var pts = dense.ToArray();
                n = pts.Length;

                // 2) polilinia -> wstega (quad strip)
                List<int> classTris = tris[(int)cls];
                int baseIdx = verts.Count;
                float vCoord = 0f;

                for (int i = 0; i < n; i++)
                {
                    Vector3 dir = Direction(pts, i, n);
                    Vector3 side = new Vector3(dir.z, 0f, -dir.x) * halfW;

                    verts.Add(pts[i] - side);
                    verts.Add(pts[i] + side);

                    if (i > 0) vCoord += Vector3.Distance(pts[i], pts[i - 1]) / RoadClasses.TileLengthM;
                    uvs.Add(new Vector2(0f, vCoord));
                    uvs.Add(new Vector2(1f, vCoord));
                }

                for (int i = 0; i < n - 1; i++)
                {
                    // Odcinek poza kaflem pomijamy. Wierzcholki zostaja (sa juz
                    // policzone i nic nie rysuja), ale trojkatow nie ma - dzieki
                    // temu droga krajowa przecinajaca dziesiec kafli jest w kazdym
                    // z nich narysowana tylko na swoim odcinku, a nie w calosci.
                    if (!SegmentInside(pts[i], pts[i + 1], halfW, clipE, clipN)) continue;

                    int a = baseIdx + i * 2;
                    classTris.Add(a); classTris.Add(a + 2); classTris.Add(a + 1);
                    classTris.Add(a + 1); classTris.Add(a + 2); classTris.Add(a + 3);
                    res.Segments++;
                }

                // 3) latarnie
                float spacing = RoadClasses.LampSpacing(cls);
                if (wantLamps && spacing > 0f && res.LampCount < MaxLampsPerTile)
                {
                    AddLamps(lampVerts, lampUvs, lampTris, pts, n, halfW,
                             RoadClasses.LampHeight(cls), spacing, heightOffset,
                             clipE, clipN, ref sideFlip, ref res.LampCount);
                }
            }

            res.Roads = MeshData.From(verts, uvs, tris, "OSMRoads");
            res.Lamps = MeshData.From(lampVerts, lampUvs, lampTris, "OSMLamps");
            return res;
        }

        /// <summary>
        /// Wlascicielem odcinka jest ten kafel, w ktorym lezy jego SRODEK.
        ///
        /// Test na zachodzenie obwiedni byl zly: odcinek przecinajacy granice
        /// nalezalby do obu kafli i obie kopie lezalyby na sobie w tej samej
        /// plaszczyznie. Srodek lezy dokladnie w jednym prostokacie, wiec kazdy
        /// odcinek ma dokladnie jednego wlasciciela - bez dziur i bez dubli.
        /// Jezdnia moze przy tym wystawac poza kafel o polowe szerokosci
        /// i to jest w porzadku, bo sasiad tego odcinka juz nie narysuje.
        /// </summary>
        private static bool SegmentInside(Vector3 a, Vector3 b, float halfW,
                                          float clipE, float clipN)
        {
            float mx = (a.x + b.x) * 0.5f;
            float mz = (a.z + b.z) * 0.5f;
            return mx >= -clipE && mx < clipE && mz >= -clipN && mz < clipN;
        }

        /// <summary>Najdluzszy odcinek wstegi. 12 m to kompromis: przy terenie
        /// z pagorkami co kilkaset metrow blad wysokosci spada ponizej 0.3 m
        /// unosu drogi, a liczba wierzcholkow rosnie tylko na dlugich prostych.</summary>
        public const double MaxSegmentM = 12.0;

        [ThreadStatic] private static List<Vector3> dense;

        private static void AddPoint(TerrainGrid grid, double bodyRadius, double alt0, float heightOffset,
                                     double lat, double lon, double east, double north)
        {
            double alt = grid.HeightAt(lat, lon);
            double y = (alt - alt0) - Geo.CurvatureDrop(east, north, bodyRadius) + heightOffset;
            dense.Add(new Vector3((float)east, (float)y, (float)north));
        }

        private static Vector3 Direction(Vector3[] pts, int i, int n)
        {
            Vector3 dir;
            if (i == 0) dir = pts[1] - pts[0];
            else if (i == n - 1) dir = pts[n - 1] - pts[n - 2];
            else dir = pts[i + 1] - pts[i - 1];

            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-8f) dir = Vector3.forward;
            dir.Normalize();
            return dir;
        }

        /// <summary>
        /// Rozstawia latarnie co "spacing" metrow wzdluz polilinii, na przemian
        /// po obu stronach. Naprzemiennosc zamiast obustronnego rzedu to polowa
        /// geometrii przy praktycznie tym samym efekcie swietlnym w nocy.
        /// </summary>
        private static void AddLamps(List<Vector3> verts, List<Vector2> uvs, List<int>[] tris,
                                     Vector3[] pts, int n, float halfW, float height,
                                     float spacing, float roadHeight,
                                     float clipE, float clipN,
                                     ref bool sideFlip, ref int count)
        {
            float travelled = 0f;
            float next = spacing * 0.5f;

            for (int i = 0; i < n - 1; i++)
            {
                Vector3 a = pts[i], b = pts[i + 1];
                float segLen = Vector3.Distance(a, b);
                if (segLen < 1e-4f) continue;

                while (next <= travelled + segLen)
                {
                    if (count >= MaxLampsPerTile) return;

                    float t = (next - travelled) / segLen;
                    Vector3 p = Vector3.Lerp(a, b, t);

                    Vector3 dir = b - a;
                    dir.y = 0f;
                    if (dir.sqrMagnitude < 1e-8f) dir = Vector3.forward;
                    dir.Normalize();

                    Vector3 across = new Vector3(dir.z, 0f, -dir.x);
                    if (sideFlip) across = -across;
                    sideFlip = !sideFlip;

                    if (p.x >= -clipE && p.x <= clipE && p.z >= -clipN && p.z <= clipN)
                    {
                        AddOneLamp(verts, uvs, tris, p, dir, across, halfW, height, roadHeight);
                        count++;
                    }
                    next += spacing;
                }

                travelled += segLen;
            }
        }

        private static void AddOneLamp(List<Vector3> verts, List<Vector2> uvs, List<int>[] tris,
                                       Vector3 p, Vector3 dir, Vector3 across,
                                       float halfW, float height, float roadHeight)
        {
            // Podstawa schodzi ponizej wstegi drogi: jezdnia jest podniesiona nad
            // teren o roadHeight, wiec slup postawiony na jej poziomie wisialby.
            Vector3 foot = p + across * (halfW + 0.9f);
            foot.y -= roadHeight + 0.6f;

            // Slup PIONOWY. Wczesniej caly slup szedl skosem od podstawy do punktu
            // 1.3 m nad jezdnia, wiec z boku kazda latarnia wygladala na krzywa.
            // Nad jezdnie wychodzi teraz tylko wysiegnik, jak w prawdziwej latarni.
            Vector3 top = foot + Vector3.up * (height + roadHeight + 0.6f);

            // --- slup: dwa skrzyzowane prostokaty ---
            // Prostokat o szerokosci 0.16 m JEST sylwetka slupa, wiec nie potrzeba
            // ani walca, ani przezroczystosci - dwa skrzyzowane czytaja sie
            // poprawnie z kazdej strony przy osmiu wierzcholkach.
            AddQuadTwoSided(verts, uvs, tris[LampPoleSub], foot, top, across * 0.08f);
            AddQuadTwoSided(verts, uvs, tris[LampPoleSub], foot, top, dir * 0.08f);

            // --- wysiegnik: nad jezdnie, lekko opadajacy ---
            Vector3 armEnd = top - across * 1.8f - Vector3.up * 0.22f;
            AddQuadTwoSided(verts, uvs, tris[LampPoleSub], top, armEnd, Vector3.up * 0.05f);
            AddQuadTwoSided(verts, uvs, tris[LampPoleSub], top, armEnd, dir * 0.05f);

            // --- klosz: plaska skrzynka na koncu wysiegnika, dluzsza wzdluz wysiegnika ---
            Vector3 c = armEnd - Vector3.up * 0.08f;
            Vector3 dx = across * 0.45f;
            Vector3 dz = dir * 0.19f;
            Vector3 dn = Vector3.up * 0.10f;

            AddFlatQuad(verts, uvs, tris[LampHeadSub], c - dn, dx, dz);   // spod - swieci na jezdnie
            AddFlatQuad(verts, uvs, tris[LampHeadSub], c + dn, dx, dz);   // wierzch
        }

        /// <summary>Pionowy prostokat od "a" do "b" o polszerokosci "side",
        /// widoczny z obu stron.</summary>
        private static void AddQuadTwoSided(List<Vector3> verts, List<Vector2> uvs,
                                            List<int> tris, Vector3 a, Vector3 b, Vector3 side)
        {
            int v0 = verts.Count;
            verts.Add(a - side); verts.Add(a + side);
            verts.Add(b + side); verts.Add(b - side);
            for (int i = 0; i < 4; i++) uvs.Add(Vector2.zero);

            tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 1);
            tris.Add(v0); tris.Add(v0 + 3); tris.Add(v0 + 2);
            tris.Add(v0); tris.Add(v0 + 1); tris.Add(v0 + 2);
            tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 3);
        }

        /// <summary>Poziomy prostokat wokol "c", rozpiety na "dx" i "dz", dwustronny.</summary>
        private static void AddFlatQuad(List<Vector3> verts, List<Vector2> uvs,
                                        List<int> tris, Vector3 c, Vector3 dx, Vector3 dz)
        {
            int v0 = verts.Count;
            verts.Add(c - dx - dz); verts.Add(c + dx - dz);
            verts.Add(c + dx + dz); verts.Add(c - dx + dz);
            for (int i = 0; i < 4; i++) uvs.Add(Vector2.zero);

            tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 1);
            tris.Add(v0); tris.Add(v0 + 3); tris.Add(v0 + 2);
            tris.Add(v0); tris.Add(v0 + 1); tris.Add(v0 + 2);
            tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 3);
        }
    }
}
