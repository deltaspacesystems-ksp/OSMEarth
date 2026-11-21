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
        public List<Vector3> LampHeads = new List<Vector3>();   // srodki kloszy (poswiata, swiatla)
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
            var uv2 = new List<Vector2>();
            var tris = new List<int>[Submeshes];
            for (int i = 0; i < Submeshes; i++) tris[i] = new List<int>();
            var infos = new List<StreetsideBuilder.WayInfo>();

            var lampVerts = new List<Vector3>();
            var lampUvs = new List<Vector2>();
            var lampTris = new List<int>[2] { new List<int>(), new List<int>() };

            var res = new RoadResult();
            var pools = new List<Vector3>();
            bool sideFlip = false;
            dense = new List<Vector3>(256);

            // --- przejscie 1: geometria kazdej drogi (punkty co <= 8 m, wysokosc z siatki terenu) ---
            foreach (OsmWay way in ways)
            {
                if (!way.IsRoad) continue;
                if (way.IsTunnel && way.Layer < 0) continue;          // tunel pod ziemia - nie widac go
                int n = way.Points.Count;
                if (n < 2) continue;

                RoadClass cls = RoadClasses.From(way.Highway);
                float width = OsmXml.WidthFor(way.Highway) * widthScale;
                // prawdziwa szerokosc z OSM (width), inaczej z liczby pasow
                if (!float.IsNaN(way.WidthM)) width = Mathf.Clamp(way.WidthM, 1.5f, cls >= RoadClass.Runway ? 100f : 40f) * widthScale;
                else if (way.Lanes > 0 && cls <= RoadClass.Minor) width = Math.Max(width, way.Lanes * 3.25f * widthScale);
                float halfW = width * 0.5f;

                // Wezly OSM na prostych potrafia byc co 100-300 m - zageszczamy do MaxSegmentM,
                // inaczej pagorek wystawal ponad prosta plyte miedzy wezlami.
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
                // Punkty ciasniej niz 0.5 m daja odcinki o losowym kierunku - wstega skrecala sie w kolec.
                var clean = new List<Vector3>(dense.Count);
                foreach (Vector3 q in dense)
                {
                    if (clean.Count > 0)
                    {
                        Vector3 prev = clean[clean.Count - 1];
                        float dx = q.x - prev.x, dz = q.z - prev.z;
                        if (dx * dx + dz * dz < 0.25f) continue;
                    }
                    clean.Add(q);
                }
                if (clean.Count < 2) continue;
                Vector3[] pts = clean.ToArray();
                infos.Add(new StreetsideBuilder.WayInfo
                {
                    Pts = pts, HalfW = halfW, Cls = cls, ClassLift = ClassLift(cls), Way = way, Extra = new float[pts.Length]
                });
            }

            // --- przejscie 2: mosty (pomost nad drogami) i najazdy na nie ---
            BridgeLifter.Compute(infos, heightOffset);
            LevelRunways(infos);
            MarkJunctions(infos);

            // --- przejscie 3: siatki ---
            foreach (StreetsideBuilder.WayInfo inf in infos)
            {
                OsmWay way = inf.Way;
                Vector3[] pts = inf.Pts;
                int n = pts.Length;
                float halfW = inf.HalfW, cl = inf.ClassLift;
                RoadClass cls = inf.Cls;
                bool bridge = way.IsBridge;

                // Przekroj wstegi: pobocze L, krawedz L, os, krawedz R, pobocze R  (/‾‾‾‾\).
                // Nawierzchnia z OSM (beton, kostka, bruk, szuter, grunt) ma wlasny material.
                int surf = SurfaceSub(way.Surface, cls);
                List<int> classTris = tris[surf];
                int plainSub = surf == (int)cls ? PlainSub(cls) : -1;
                List<int> plainTris = plainSub >= 0 && inf.Plain != null ? tris[plainSub] : null;
                bool level = cls >= RoadClass.Runway;
                float skirtW = bridge ? 0.05f : Mathf.Clamp(halfW * 0.8f, 1.5f, 4f);
                // nasyp pasa: skarpa 1:4 od jezdni do gruntu
                if (level) skirtW = Mathf.Clamp(Max(inf.Extra) * 4f, 3f, 40f);

                int runStart = 0;
                for (int i = 1; i < n; i++)
                {
                    bool last = i == n - 1;
                    bool sharp = !last && Folds(pts[i - 1], pts[i], pts[i + 1], halfW);
                    if (!last && !sharp) continue;
                    res.Segments += AddRibbon(verts, uvs, uv2, cl, classTris, pts, runStart, i, halfW, skirtW,
                                              heightOffset, clipE, clipN, inf.Extra, bridge, plainTris, inf.Plain, level);
                    if (sharp && !bridge && !level) AddPad(verts, uvs, uv2, cl + inf.Extra[i], classTris, pts[i], halfW, skirtW, heightOffset, clipE, clipN);
                    runStart = i;
                }
                if (!bridge && !level)
                {
                    AddPad(verts, uvs, uv2, cl + inf.Extra[0], classTris, pts[0], halfW, skirtW, heightOffset, clipE, clipN);
                    AddPad(verts, uvs, uv2, cl + inf.Extra[n - 1], classTris, pts[n - 1], halfW, skirtW, heightOffset, clipE, clipN);
                }
                else BridgeParts(verts, uvs, uv2, tris, inf, heightOffset, clipE, clipN);

                // latarnie tylko na drogach na ziemi (na moscie i najezdzie stalyby w powietrzu / w nasypie)
                float spacing = RoadClasses.LampSpacing(cls);
                if (wantLamps && spacing > 0f && res.LampCount < MaxLampsPerTile && !bridge && Max(inf.Extra) < 0.3f)
                {
                    AddLamps(lampVerts, lampUvs, lampTris, pts, n, halfW,
                             RoadClasses.LampHeight(cls), spacing, heightOffset,
                             clipE, clipN, ref sideFlip, ref res.LampCount, res.LampHeads, pools);
                }
            }

            AddPools(verts, uvs, uv2, tris[SubLampPool], pools);
            BarrierBuilder.Build(ways, lat0, lon0, bodyRadius, grid, alt0, heightOffset, clipE, clipN, verts, uvs, uv2, tris);
            StreetsideBuilder.Build(infos, ways, lat0, lon0, bodyRadius, heightOffset, clipE, clipN,
                                    verts, uvs, uv2, tris);

            res.Roads = MeshData.From(verts, uvs, uv2, tris, "OSMRoads");
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
        public static bool SegmentInsidePublic(Vector3 a, Vector3 b, float clipE, float clipN)
        {
            return SegmentInside(a, b, 0f, clipE, clipN);
        }

        private static bool SegmentInside(Vector3 a, Vector3 b, float halfW,
                                          float clipE, float clipN)
        {
            float mx = (a.x + b.x) * 0.5f;
            float mz = (a.z + b.z) * 0.5f;
            return mx >= -clipE && mx < clipE && mz >= -clipN && mz < clipN;
        }

        /// <summary>Submeshe siatki drog: klasy jezdni, potem krawezniki, kostka (chodniki, wjazdy), trawa.</summary>
        public const int SubCurb = RoadClasses.Count, SubPaving = RoadClasses.Count + 1, SubGrass = RoadClasses.Count + 2;
        // nawierzchnie z OSM (surface) i ogrodzenia (barrier)
        public const int SubConcrete = RoadClasses.Count + 3, SubSett = RoadClasses.Count + 4,
                         SubGravel = RoadClasses.Count + 5, SubDirt = RoadClasses.Count + 6,
                         SubFence = RoadClasses.Count + 7, SubHedge = RoadClasses.Count + 8, SubWall = RoadClasses.Count + 9;
        public const int SubLampPool = RoadClasses.Count + 10;      // plamy swiatla latarni (addytywne, bez fizyki)
        // jezdnia bez oznakowania (skrzyzowania, ronda) dla klas, ktore oznakowanie maja
        public const int SubPlainMotorway = RoadClasses.Count + 11, SubPlainMajor = RoadClasses.Count + 12,
                         SubPlainRunway = RoadClasses.Count + 13;
        public const int Submeshes = RoadClasses.Count + 14;

        public static int PlainSub(RoadClass c)
        {
            switch (c)
            {
                case RoadClass.Motorway: return SubPlainMotorway;
                case RoadClass.Major: return SubPlainMajor;
                case RoadClass.Runway: return SubPlainRunway;
                default: return -1;
            }
        }

        public static RoadClass PlainClass(int sub)
        {
            return sub == SubPlainMotorway ? RoadClass.Motorway : sub == SubPlainMajor ? RoadClass.Major : RoadClass.Runway;
        }

        /// <summary>
        /// Skrzyzowania i ronda bez oznakowania. Kazda wstega niosla swoje kreski przez
        /// cale skrzyzowanie, wiec na rondzie i przy wlotach krzyzowaly sie osie pod
        /// dziwnymi katami ("strzalki" na rondzie). Prawdziwe skrzyzowanie ma czysty asfalt:
        /// os konczy sie przed krawedzia drogi poprzecznej.
        ///
        /// Skrzyzowanie = punkt wspolny co najmniej trzech jezdni albo dwoch, z ktorych
        /// jedna przez niego przechodzi (T). Sciezki i dojazdy nie licza sie - inaczej kazdy
        /// wjazd do posesji wycinalby kreske na glownej.
        /// </summary>
        private static void MarkJunctions(List<StreetsideBuilder.WayInfo> infos)
        {
            var map = new Dictionary<long, float[]>();      // klucz -> [liczba, przelotowa, max polszerokosc, x, z]
            var seen = new HashSet<long>();
            foreach (StreetsideBuilder.WayInfo inf in infos)
            {
                if (inf.Cls >= RoadClass.Service && inf.Cls != RoadClass.Runway && inf.Cls != RoadClass.Taxiway) continue;
                seen.Clear();
                Vector3[] p = inf.Pts;
                for (int k = 0; k < p.Length; k++)
                {
                    long key = JKey(p[k]);
                    if (!seen.Add(key)) continue;
                    float[] j;
                    if (!map.TryGetValue(key, out j)) map[key] = j = new[] { 0f, 0f, 0f, p[k].x, p[k].z };
                    j[0] += 1f;
                    if (k > 0 && k < p.Length - 1) j[1] = 1f;
                    j[2] = Mathf.Max(j[2], inf.HalfW);
                }
            }
            var cells = new Dictionary<long, List<float[]>>();
            foreach (float[] j in map.Values)
            {
                if (!(j[0] >= 3f || (j[0] >= 2f && j[1] > 0f))) continue;
                long ck = CKey(j[3], j[4]);
                List<float[]> l;
                if (!cells.TryGetValue(ck, out l)) cells[ck] = l = new List<float[]>();
                l.Add(j);
            }

            foreach (StreetsideBuilder.WayInfo inf in infos)
            {
                if (PlainSub(inf.Cls) < 0) continue;
                Vector3[] p = inf.Pts;
                int n = p.Length;
                var plain = new bool[n];
                bool any = false;
                OsmWay w = inf.Way;
                bool ring = n > 3 && w.Points.Count > 3 && (w.Flags & PackFormat.ROneway) != 0 &&
                            Math.Abs(w.Points[0].Lat - w.Points[w.Points.Count - 1].Lat) < 1e-9 &&
                            Math.Abs(w.Points[0].Lon - w.Points[w.Points.Count - 1].Lon) < 1e-9;
                for (int k = 0; k < n; k++)
                {
                    if (ring) { plain[k] = true; any = true; continue; }
                    int cx = (int)Math.Floor(p[k].x / JCell), cz = (int)Math.Floor(p[k].z / JCell);
                    for (int dz = -1; dz <= 1 && !plain[k]; dz++)
                        for (int dx = -1; dx <= 1 && !plain[k]; dx++)
                        {
                            List<float[]> l;
                            if (!cells.TryGetValue(((long)(cx + dx) << 32) ^ (uint)(cz + dz), out l)) continue;
                            foreach (float[] j in l)
                            {
                                float r = j[2] + 3f;
                                float ex = p[k].x - j[3], ez = p[k].z - j[4];
                                if (ex * ex + ez * ez < r * r) { plain[k] = true; any = true; break; }
                            }
                        }
                }
                if (any) inf.Plain = plain;
            }
        }

        /// <summary>
        /// Pas startowy i droga kolowania musza byc PLASKIE - prawdziwy pas ma staly spadek
        /// na calej dlugosci, a nie faluje z terenem. Przez os pasa przepuszczamy prosta
        /// (z wysokosci obu progow), podnosimy ja tak, zeby nigdzie nie wchodzila w grunt,
        /// i to ona jest jezdnia; roznica do gruntu zostaje jako nasyp pod poboczem.
        /// Droga kolowania bywa dluga i kreta, wiec zamiast prostej dostaje wygladzony
        /// profil (srednia ruchoma), ktory i tak kasuje wszystkie garby.
        /// </summary>
        private static void LevelRunways(List<StreetsideBuilder.WayInfo> infos)
        {
            foreach (StreetsideBuilder.WayInfo inf in infos)
            {
                if (inf.Cls != RoadClass.Runway && inf.Cls != RoadClass.Taxiway) continue;
                Vector3[] p = inf.Pts;
                int n = p.Length;
                if (n < 2) continue;

                var dist = new float[n];
                for (int i = 1; i < n; i++)
                    dist[i] = dist[i - 1] + Mathf.Sqrt((p[i].x - p[i - 1].x) * (p[i].x - p[i - 1].x) +
                                                       (p[i].z - p[i - 1].z) * (p[i].z - p[i - 1].z));
                float len = dist[n - 1];
                if (len < 1f) continue;

                var plane = new float[n];
                if (inf.Cls == RoadClass.Runway)
                {
                    float a = p[0].y, b = (p[n - 1].y - p[0].y) / len;
                    float up = 0f;
                    for (int i = 0; i < n; i++) up = Mathf.Max(up, p[i].y - (a + b * dist[i]));
                    for (int i = 0; i < n; i++) plane[i] = a + b * dist[i] + up;
                }
                else
                {
                    // srednia ruchoma +-60 m, potem podniesienie ponad grunt
                    const float Win = 60f;
                    for (int i = 0; i < n; i++)
                    {
                        float sum = 0f;
                        int cnt = 0;
                        for (int j = 0; j < n; j++)
                            if (Mathf.Abs(dist[j] - dist[i]) <= Win) { sum += p[j].y; cnt++; }
                        plane[i] = cnt > 0 ? sum / cnt : p[i].y;
                    }
                    float up = 0f;
                    for (int i = 0; i < n; i++) up = Mathf.Max(up, p[i].y - plane[i]);
                    for (int i = 0; i < n; i++) plane[i] += up;
                }

                // Nasyp ponad 12 m to znak, ze pas z OSM lezy na zboczu albo dane sa dziwne -
                // wtedy lepiej isc za terenem niz stawiac wiadukt przez pol mapy.
                float maxLift = 0f;
                for (int i = 0; i < n; i++) maxLift = Mathf.Max(maxLift, plane[i] - p[i].y);
                if (maxLift > 12f) continue;

                for (int i = 0; i < n; i++)
                {
                    plane[i] += 0.4f;
                    inf.Extra[i] = plane[i] - p[i].y;
                    p[i] = new Vector3(p[i].x, plane[i], p[i].z);
                }
            }
        }

        private const float JCell = 40f;
        private static long JKey(Vector3 p) { return ((long)Mathf.RoundToInt(p.x * 10f) << 32) ^ (uint)Mathf.RoundToInt(p.z * 10f); }
        private static long CKey(float x, float z) { return ((long)(int)Math.Floor(x / JCell) << 32) ^ (uint)(int)Math.Floor(z / JCell); }


        /// <summary>Plama swiatla pod kazda latarnia: siatka 3x3 lezaca na jezdni (unos Road), wiec
        /// przykleja sie do terenu razem z droga. Material addytywny, jasnosc z pory dnia.</summary>
        private static void AddPools(List<Vector3> verts, List<Vector2> uvs, List<Vector2> uv2, List<int> t, List<Vector3> pools)
        {
            const float R = 11f;
            foreach (Vector3 c in pools)
            {
                int b = verts.Count;
                for (int z = 0; z < 3; z++)
                    for (int x = 0; x < 3; x++)
                    {
                        verts.Add(new Vector3(c.x + (x - 1) * R, c.y, c.z + (z - 1) * R));
                        uvs.Add(new Vector2(x * 0.5f, z * 0.5f));
                        uv2.Add(Road(0.07f));
                    }
                for (int z = 0; z < 2; z++)
                    for (int x = 0; x < 2; x++)
                    {
                        int a = b + z * 3 + x;
                        t.Add(a); t.Add(a + 3); t.Add(a + 1);
                        t.Add(a + 1); t.Add(a + 3); t.Add(a + 4);
                    }
            }
        }

        /// <summary>Material jezdni wg tagu surface; asfalt i brak tagu = material klasy drogi.</summary>
        public static int SurfaceSub(string surface, RoadClass cls)
        {
            if (surface == null) return (int)cls;
            switch (surface)
            {
                case "concrete": case "concrete:plates": case "concrete:lanes": case "cement":
                    return SubConcrete;
                case "paving_stones": case "paving_stones:30": case "paving_stones:20": case "bricks": case "brick":
                case "grass_paver": case "interlock":
                    return SubPaving;
                case "sett": case "cobblestone": case "unhewn_cobblestone": case "cobblestone:flattened": case "stone":
                    return SubSett;
                case "gravel": case "fine_gravel": case "compacted": case "pebblestone": case "unpaved": case "rock":
                case "chipseal": case "crushed_limestone": case "slag":
                    return SubGravel;
                case "ground": case "dirt": case "earth": case "mud": case "sand": case "grass": case "soil": case "clay":
                    return SubDirt;
                default: return (int)cls;
            }
        }

        private static float Max(float[] a) { float m = 0f; foreach (float x in a) if (x > m) m = x; return m; }

        /// <summary>
        /// Mostek: barierki po obu stronach (beton, 1 m) i filary co ~22 m z ziemi pod pomost -
        /// tylko tam, gdzie pomost jest wyzej niz 2 m (niski most nad rowem filarow nie ma).
        /// </summary>
        private static void BridgeParts(List<Vector3> verts, List<Vector2> uvs, List<Vector2> uv2, List<int>[] tris,
                                        StreetsideBuilder.WayInfo inf, float heightOffset, float clipE, float clipN)
        {
            Vector3[] p = inf.Pts;
            int n = p.Length;
            float hw = inf.HalfW, cl = inf.ClassLift;
            List<int> t = tris[SubCurb];
            float along = 0f;
            for (int side = -1; side <= 1; side += 2)
            {
                int b0 = verts.Count;
                for (int k = 0; k < n; k++)
                {
                    Vector3 dir = Direction(p, k, n);
                    var across = new Vector3(dir.z, 0f, -dir.x) * (hw * side);
                    float ex = inf.Extra[k];
                    verts.Add(p[k] + across + new Vector3(0f, ex, 0f)); uvs.Add(new Vector2(0f, k)); uv2.Add(Road(cl + ex));
                    verts.Add(p[k] + across + new Vector3(0f, ex + 1.0f, 0f)); uvs.Add(new Vector2(0.5f, k)); uv2.Add(Road(cl + ex + 1.0f));
                }
                for (int k = 0; k + 1 < n; k++)
                {
                    if (!SegmentInside(p[k], p[k + 1], hw, clipE, clipN)) continue;
                    int a = b0 + k * 2, b = a + 2;
                    // dwustronnie: barierka widoczna z jezdni i z zewnatrz
                    t.Add(a); t.Add(b); t.Add(a + 1); t.Add(a + 1); t.Add(b); t.Add(b + 1);
                    t.Add(a); t.Add(a + 1); t.Add(b); t.Add(a + 1); t.Add(b + 1); t.Add(b);
                }
            }

            // filary
            for (int k = 1; k < n; k++)
            {
                along += Vector3.Distance(new Vector3(p[k].x, 0, p[k].z), new Vector3(p[k - 1].x, 0, p[k - 1].z));
                if (along < 22f) continue;
                along = 0f;
                if (inf.Extra[k] < 2f || k >= n - 1) continue;
                Vector3 c = p[k];
                if (c.x < -clipE || c.x >= clipE || c.z < -clipN || c.z >= clipN) continue;
                float s = Mathf.Clamp(hw * 0.35f, 0.6f, 1.4f);
                var corners = new[] { new Vector3(-s, 0, -s), new Vector3(s, 0, -s), new Vector3(s, 0, s), new Vector3(-s, 0, s) };
                float top = inf.Extra[k] - 0.45f;
                for (int f = 0; f < 4; f++)
                {
                    Vector3 a = c + corners[f], b = c + corners[(f + 1) % 4];
                    int i0 = verts.Count;
                    verts.Add(a); uvs.Add(Vector2.zero); uv2.Add(Ground(-0.5f));
                    verts.Add(b); uvs.Add(Vector2.zero); uv2.Add(Ground(-0.5f));
                    verts.Add(b + new Vector3(0, top, 0)); uvs.Add(Vector2.zero); uv2.Add(Road(cl + top));
                    verts.Add(a + new Vector3(0, top, 0)); uvs.Add(Vector2.zero); uv2.Add(Road(cl + top));
                    t.Add(i0); t.Add(i0 + 2); t.Add(i0 + 1); t.Add(i0); t.Add(i0 + 3); t.Add(i0 + 2);
                    t.Add(i0); t.Add(i0 + 1); t.Add(i0 + 2); t.Add(i0); t.Add(i0 + 2); t.Add(i0 + 3);
                }
            }
        }

        /// <summary>Warstwy klas: gdzie drogi na siebie zachodza, wazniejsza lezy 1.5 cm wyzej.</summary>
        public static float ClassLift(RoadClass c)
        {
            if (c == RoadClass.Runway) return 0.06f;
            if (c == RoadClass.Taxiway) return 0.045f;
            return ((int)RoadClass.Path - (int)c) * 0.015f;
        }

        /// <summary>uv2 wierzcholka: unos wzgledem jezdni (po unosie drogi i kompensacji ugiecia).</summary>
        public static Vector2 Road(float lift) { return new Vector2(lift, 0f); }

        /// <summary>uv2 wierzcholka: wysokosc wzgledem gruntu (pobocza, trawniki, wjazdy przy domu).</summary>
        public static Vector2 Ground(float lift) { return new Vector2(lift, 1f); }

        /// <summary>uv2 wierzcholka: wysokosc juz ustalona przy budowie - przyklejanie jej NIE rusza.
        /// Pas startowy musi byc plaski, a nie falowac razem z terenem.</summary>
        public static Vector2 Fixed(float y) { return new Vector2(y, 2f); }

        public const float SkirtUL = 0.012f, SkirtUR = 0.988f, PadU = 0.35f;
        public const float EdgeUL = 0.04f, EdgeUR = 0.96f;       // krawedz jezdni - tuz za pasem pobocza tekstury

        /// <summary>Czy wewnetrzna krawedz wstegi przejdzie tu sama przez siebie: zakret ostry
        /// albo lagodny, ale na odcinkach krotszych niz szerokosc drogi (ciasny luk).</summary>
        private static bool Folds(Vector3 a, Vector3 b, Vector3 c, float halfW)
        {
            float deg = TurnDeg(a, b, c);
            if (deg > 40f) return true;
            if (deg < 3f) return false;
            float l0 = Mathf.Sqrt((b.x - a.x) * (b.x - a.x) + (b.z - a.z) * (b.z - a.z));
            float l1 = Mathf.Sqrt((c.x - b.x) * (c.x - b.x) + (c.z - b.z) * (c.z - b.z));
            float need = 2f * halfW * Mathf.Tan(deg * 0.5f * Mathf.Deg2Rad);
            return need > Mathf.Min(l0, l1) * 0.8f;
        }

        private static Vector2 PadUv(Vector3 off, float halfW, Vector3 c)
        {
            return new Vector2(PadU + 0.1f * off.x / Mathf.Max(0.5f, halfW), (c.z + off.z) / RoadClasses.TileLengthM);
        }

        private static float TurnDeg(Vector3 a, Vector3 b, Vector3 c)
        {
            var d0 = new Vector2(b.x - a.x, b.z - a.z);
            var d1 = new Vector2(c.x - b.x, c.z - b.z);
            if (d0.sqrMagnitude < 1e-6f || d1.sqrMagnitude < 1e-6f) return 0f;
            double cos = (d0.x * d1.x + d0.y * d1.y) / Math.Sqrt((double)d0.sqrMagnitude * d1.sqrMagnitude);
            return (float)(Math.Acos(Math.Max(-1.0, Math.Min(1.0, cos))) * 180.0 / Math.PI);
        }

        /// <summary>Wstega z punktow [i0..i1]. Zwraca liczbe odcinkow nalezacych do kafla.</summary>
        private static int AddRibbon(List<Vector3> verts, List<Vector2> uvs, List<Vector2> uv2, float cl, List<int> tris, Vector3[] pts,
                                     int i0, int i1, float halfW, float skirtW, float heightOffset,
                                     float clipE, float clipN, float[] extra, bool bridge,
                                     List<int> plainTris, bool[] plain, bool level = false)
        {
            int baseIdx = verts.Count;
            float vCoord = 0f;
            int m = i1 - i0 + 1;
            var sub = new Vector3[m];
            System.Array.Copy(pts, i0, sub, 0, m);
            Vector3 down = new Vector3(0f, heightOffset + SkirtSink, 0f);

            for (int k = 0; k < m; k++)
            {
                Vector3 dir = Direction(sub, k, m);
                Vector3 across = new Vector3(dir.z, 0f, -dir.x);
                Vector3 side = across * halfW;
                Vector3 skirt = across * (halfW + skirtW);

                // na moscie "pobocze" to krawedz pomostu (40 cm plyty), nie nasyp do ziemi
                float ex = extra[i0 + k];
                Vector3 up = level ? Vector3.zero : new Vector3(0f, ex, 0f);
                Vector3 lip = bridge ? new Vector3(0f, ex - 0.45f, 0f) : -down;
                verts.Add(sub[k] - skirt + lip);
                verts.Add(sub[k] - side + up);
                verts.Add(sub[k] + up);
                verts.Add(sub[k] + side + up);
                verts.Add(sub[k] + skirt + lip);

                if (k > 0) vCoord += Vector3.Distance(sub[k], sub[k - 1]) / RoadClasses.TileLengthM;
                uvs.Add(new Vector2(SkirtUL, vCoord));
                uvs.Add(new Vector2(EdgeUL, vCoord));
                uvs.Add(new Vector2(0.5f, vCoord));
                uvs.Add(new Vector2(EdgeUR, vCoord));
                uvs.Add(new Vector2(SkirtUR, vCoord));
                Vector2 edge = bridge ? Road(cl + ex - 0.45f) : Ground(-SkirtSink);
                Vector2 deck = level ? Fixed(0f) : Road(cl + ex);
                uv2.Add(edge); uv2.Add(deck); uv2.Add(deck); uv2.Add(deck); uv2.Add(edge);
            }

            int segs = 0;
            for (int k = 0; k < m - 1; k++)
            {
                // Odcinek poza kaflem pomijamy - rysuje go kafel, w ktorym lezy jego srodek.
                if (!SegmentInside(sub[k], sub[k + 1], halfW, clipE, clipN)) continue;
                List<int> t = plainTris != null && (plain[i0 + k] || plain[i0 + k + 1]) ? plainTris : tris;
                for (int c = 0; c < 4; c++)
                {
                    int a = baseIdx + k * VertsPerPoint + c;
                    int b = a + VertsPerPoint;
                    t.Add(a); t.Add(b); t.Add(a + 1);
                    t.Add(a + 1); t.Add(b); t.Add(b + 1);
                }
                segs++;
            }
            return segs;
        }

        /// <summary>Okragla lata asfaltu (z pochylym poboczem) w rogu albo na koncu drogi.</summary>
        private static void AddPad(List<Vector3> verts, List<Vector2> uvs, List<Vector2> uv2, float cl, List<int> tris, Vector3 c,
                                   float halfW, float skirtW, float heightOffset, float clipE, float clipN)
        {
            if (c.x < -clipE || c.x >= clipE || c.z < -clipN || c.z >= clipN) return;
            const int N = 12;
            int b = verts.Count;
            // tekstura rzutowana z gory (U 0.25-0.45 = czysty asfalt miedzy koleina a osia),
            // zeby lata nie miala promienistych smug
            verts.Add(c); uvs.Add(PadUv(Vector3.zero, halfW, c)); uv2.Add(Road(cl - 0.01f));
            Vector3 down = new Vector3(0f, heightOffset + SkirtSink, 0f);
            for (int k = 0; k < N; k++)
            {
                float a = k * Mathf.PI * 2f / N;
                var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                verts.Add(c + d * halfW); uvs.Add(PadUv(d * halfW, halfW, c)); uv2.Add(Road(cl - 0.01f));
                verts.Add(c + d * (halfW + skirtW) - down); uvs.Add(new Vector2(SkirtUL, 0.1f)); uv2.Add(Ground(-SkirtSink));
            }
            for (int k = 0; k < N; k++)
            {
                int r0 = b + 1 + k * 2, r1 = b + 1 + ((k + 1) % N) * 2;
                // kolejnosc jak we wstedze (normalne w gore): srodek, nastepny, biezacy
                tris.Add(b); tris.Add(r1); tris.Add(r0);
                tris.Add(r0); tris.Add(r1); tris.Add(r1 + 1);
                tris.Add(r0); tris.Add(r1 + 1); tris.Add(r0 + 1);
            }
        }
        /// <summary>Najdluzszy odcinek wstegi. 12 m to kompromis: przy terenie
        /// z pagorkami co kilkaset metrow blad wysokosci spada ponizej 0.3 m
        /// unosu drogi, a liczba wierzcholkow rosnie tylko na dlugich prostych.</summary>
        public const double MaxSegmentM = 8.0;

        /// <summary>Wierzcholki na punkt osi: pobocze, krawedz, os, krawedz, pobocze.</summary>
        public const int VertsPerPoint = 5;

        /// <summary>Jak gleboko pod teren schodzi zewnetrzna krawedz pobocza.</summary>
        public const float SkirtSink = 0.2f;

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
                                     ref bool sideFlip, ref int count, List<Vector3> heads, List<Vector3> pools)
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
                        Vector3 under = p + across * (halfW - 0.9f);
                        heads.Add(new Vector3(under.x, p.y + height - 0.3f, under.z));
                        pools.Add(under);
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
