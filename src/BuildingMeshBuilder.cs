using System;
using System.Collections.Generic;
using UnityEngine;


namespace OSMRoads
{
    public class BuildingResult
    {
        public MeshData Full;      // wszystkie budynki
        public MeshData Coarse;    // tylko wysokie - LOD na dalsze kafle
        public int Built;
        public int Skipped;
    }

    /// <summary>Ksztalt dachu. Czterospadowy dostal osobna pozycje, bo to on
    /// odroznia dom wolnostojacy od blizniaka rownie mocno jak sam spadek
    /// odroznia dom od pudelka.</summary>
    public enum RoofKind { Flat = 0, Gabled = 1, Hipped = 2 }

    /// <summary>
    /// Obrysy OSM -> wyciagniete bryly. Sciany + plaski dach.
    ///
    /// Mesh ma 5 submeshy: 0..3 to kubelki kolorystyczne elewacji (kazdy budynek
    /// trafia do jednego wg hasza pozycji, zeby miasto nie bylo jednolite),
    /// 4 to dachy. Sciany dostaja UV w skali metrycznej, wiec okno ma zawsze
    /// ok. 3.5 m szerokosci i 3 m wysokosci, niezaleznie od wielkosci budynku.
    /// </summary>
    public static class BuildingMeshBuilder
    {
        /// <summary>O ile metrow zakopac podstawe ponizej najnizszego rogu.</summary>
        private const float FoundationDepth = 3f;

        /// <summary>Wysiew okapu poza lico sciany. Dach konczacy sie dokladnie
        /// na scianie wyglada jak nakleiona pokrywka - te 35 cm daje cien
        /// pod okapem, ktory czyta sie jako prawdziwy dach.</summary>
        private const float EaveM = 0.35f;

        /// <summary>Ponizej tej wysokosci budynek znika w LOD-zie dalekim.</summary>
        public const float CoarseMinHeight = 15f;

        private const int Buckets = MaterialPalette.FacadeBuckets;
        private const int PlinthSub = Buckets;
        private const int RoofEdgeSub = Buckets + 1;   // attyka
        private const int RoofSub = Buckets + 2;       // plaski dach (papa)
        private const int RoofPitchSub = Buckets + 3;  // dach spadzisty (dachowka)
        private const int SubCount = Buckets + 4;

        private class Accum
        {
            public readonly List<Vector3> Verts = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<int>[] Tris = new List<int>[SubCount];

            public Accum()
            {
                for (int i = 0; i < SubCount; i++) Tris[i] = new List<int>();
            }

            public MeshData ToData(string name)
            {
                return MeshData.From(Verts, Uvs, Tris, name);
            }
        }

        public static BuildingResult Build(TerrainGrid grid, double bodyRadius, List<OsmWay> ways,
                                           double lat0, double lon0, double alt0,
                                           float heightScale, int maxBuildings)
        {
            double R = bodyRadius;
            var full = new Accum();
            var coarse = new Accum();
            var res = new BuildingResult();

            var ring = new List<Vector2>();

            foreach (OsmWay way in ways)
            {
                if (!way.IsBuilding) continue;
                if (res.Built >= maxBuildings) { res.Skipped++; continue; }

                // obrys -> lokalne metry, bez zdublowanego ostatniego punktu
                ring.Clear();
                int n = way.Points.Count;
                if (n >= 2 &&
                    Math.Abs(way.Points[0].Lat - way.Points[n - 1].Lat) < 1e-9 &&
                    Math.Abs(way.Points[0].Lon - way.Points[n - 1].Lon) < 1e-9)
                    n--;
                if (n < 3) { res.Skipped++; continue; }

                double minAlt = double.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    GeoPoint g = way.Points[i];
                    double east, north;
                    Geo.ToLocalMeters(g.Lat, g.Lon, lat0, lon0, R, out east, out north);
                    ring.Add(new Vector2((float)east, (float)north));

                    double a = grid.HeightAt(g.Lat, g.Lon)
                               - Geo.CurvatureDrop(east, north, R);
                    if (a < minAlt) minAlt = a;
                }

                if (Area2(ring) < 4f) { res.Skipped++; continue; }   // smieci ponizej 4 m2

                float h = HeightFor(way, ring) * heightScale;
                bool shopFront = WantsShopFront(way, ring, h);
                float yGround = (float)(minAlt - alt0);

                // deterministyczny hasz z pozycji - ten sam budynek zawsze dostaje
                // ten sam kolor i to samo przesuniecie okien, takze po przeladowaniu
                uint hash = PosHash(way.Points[0].Lat, way.Points[0].Lon);
                int bucket = (int)(hash % (uint)Buckets);
                float uOff = ((hash >> 8) % 64u) / 8f;
                float vOff = ((hash >> 16) % 8u) / 8f;

                RoofKind roofKind = RoofKindFor(way, ring, h);

                AddPrism(full, ring, yGround - FoundationDepth, yGround + h,
                         yGround, bucket, uOff, vOff, roofKind, shopFront);

                if (h >= CoarseMinHeight)
                    AddPrism(coarse, ring, yGround - FoundationDepth, yGround + h,
                             yGround, bucket, uOff, vOff, roofKind, shopFront);

                res.Built++;
            }

            res.Full = full.ToData("OSMBuildings");
            res.Coarse = coarse.ToData("OSMBuildingsCoarse");
            return res;
        }

        private static void AddPrism(Accum acc, List<Vector2> ring,
                                     float yBase, float yTop, float yGround,
                                     int bucket, float uOff, float vOff, RoofKind roofKind,
                                     bool shopFront)
        {
            bool pitched = roofKind != RoofKind.Flat;
            int n = ring.Count;
            List<Vector3> verts = acc.Verts;
            List<Vector2> uvs = acc.Uvs;
            List<int> tris = acc.Tris[bucket];

            // srodek obrysu - sluzy do sprawdzenia, w ktora strone patrzy sciana
            Vector2 c = Vector2.zero;
            for (int i = 0; i < n; i++) c += ring[i];
            c /= n;

            // Parter dostaja tylko budynki wyzsze niz prog - szopa czy garaz nie
            // ma witryn. To wlasnie ten pas najmocniej rozbija wrazenie
            // "nieskonczonej kraty okien".
            bool hasPlinth = shopFront && (yTop - yGround) >= FacadeTexture.PlinthMinBuildingM;
            float yPlinth = hasPlinth
                ? yGround + FacadeTexture.PlinthHeightM
                : yBase;

            float vBottom = vOff + (yPlinth - yGround) * FacadeTexture.UvPerMeterV;
            float vTop = vOff + (yTop - yGround) * FacadeTexture.UvPerMeterV;

            // --- SCIANY ---
            // Kazda sciana ma wlasne 4 wierzcholki: RecalculateNormals daje wtedy
            // ostre krawedzie, a UV nie przelewa sie miedzy scianami.
            float uRun = uOff;
            float uRunP = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = ring[i];
                Vector2 b = ring[(i + 1) % n];
                float len = (b - a).magnitude;

                float u0 = uRun;
                float u1 = uRun + len * FacadeTexture.UvPerMeterU;
                uRun = u1;

                // Zamiast rozumowac o skretnosci ukladu: policz normalna i sprawdz,
                // czy patrzy OD srodka bryly. Jesli nie - odwroc nawijanie.
                Vector2 edge = b - a;
                Vector2 nrm = new Vector2(edge.y, -edge.x);
                Vector2 mid = (a + b) * 0.5f;
                bool outward = Vector2.Dot(nrm, mid - c) > 0f;

                if (hasPlinth)
                {
                    // parter: v od 0 (grunt) do 1 (gora pasa), bez powtarzania w pionie
                    float p0 = uRunP;
                    float p1 = uRunP + len * FacadeTexture.PlinthUvPerMeterU;
                    uRunP = p1;

                    float vSunk = -(yGround - yBase) / FacadeTexture.PlinthHeightM;
                    AddQuad(verts, uvs, acc.Tris[PlinthSub], a, b, yBase, yPlinth,
                            p0, p1, vSunk, 1f, outward);
                }

                AddQuad(verts, uvs, tris, a, b, yPlinth, yTop,
                        u0, u1, vBottom, vTop, outward);

                // Attyka: niski gzyms nad krawedzia plaskiego dachu. Bez niego dach
                // konczy sie ostrym scieciem i z gory wyglada jak wieko pudelka.
                if (!pitched && hasPlinth)
                    AddQuad(verts, uvs, acc.Tris[RoofEdgeSub], a, b,
                            yTop, yTop + FacadeTexture.ParapetHeightM,
                            0f, 1f, 0f, 1f, outward);
            }

            // --- DACH ---
            List<int> cap = Polygon.Triangulate(ring);
            if (cap == null) return;

            // Plaska czapa zawsze - przy dachu spadzistym sluzy za strop pod krokwiami
            // i zamyka bryle, gdy prostokat OBB nie pokrywa sie idealnie z obrysem.
            List<int> roofTris = acc.Tris[pitched ? RoofPitchSub : RoofSub];
            float roofUv = pitched
                ? FacadeTexture.RoofPitchUvPerMeter
                : FacadeTexture.RoofUvPerMeter;

            int rBase = verts.Count;
            for (int i = 0; i < n; i++)
            {
                verts.Add(new Vector3(ring[i].x, yTop, ring[i].y));
                // UV metryczne: tekstura papy ma staly rozmiar w metrach niezaleznie
                // od wielkosci dachu, wiec nie rozciaga sie na duzych halach
                uvs.Add(new Vector2((ring[i].x + uOff * 7f) * roofUv,
                                    (ring[i].y + vOff * 7f) * roofUv));
            }

            bool up = true;
            if (cap.Count >= 3)
            {
                Vector3 p0 = verts[rBase + cap[0]];
                Vector3 p1 = verts[rBase + cap[1]];
                Vector3 p2 = verts[rBase + cap[2]];
                up = Vector3.Cross(p1 - p0, p2 - p0).y > 0f;
            }

            for (int i = 0; i + 2 < cap.Count; i += 3)
            {
                if (up)
                {
                    roofTris.Add(rBase + cap[i]);
                    roofTris.Add(rBase + cap[i + 1]);
                    roofTris.Add(rBase + cap[i + 2]);
                }
                else
                {
                    roofTris.Add(rBase + cap[i]);
                    roofTris.Add(rBase + cap[i + 2]);
                    roofTris.Add(rBase + cap[i + 1]);
                }
            }

            if (pitched) AddPitchedRoof(acc, ring, yTop, roofKind == RoofKind.Hipped);
        }

        /// <summary>
        /// Dach spadzisty na prostokacie o minimalnej powierzchni opisanym na obrysie.
        /// Swiadomie NIE idzie po dokladnym obrysie: wywolujemy to tylko dla budynkow,
        /// ktore juz sprawdzilismy jako prawie prostokatne, wiec roznica jest niewidoczna,
        /// a kod zostaje prosty i odporny.
        ///
        /// Dwuspadowy i czterospadowy to ta sama konstrukcja z jednym parametrem:
        /// przy czterospadowym kalenica jest skrocona z obu koncow, a powstale
        /// trojkaty zamykaja polacie zamiast pionowych szczytow.
        /// </summary>
        private static void AddPitchedRoof(Accum acc, List<Vector2> ring, float yTop, bool hipped)
        {
            Vector2 c, ax;
            float halfU, halfV;
            if (!MinAreaRect(ring, out c, out ax, out halfU, out halfV)) return;

            Vector2 ay = new Vector2(-ax.y, ax.x);

            // kalenica wzdluz dluzszej osi
            if (halfV > halfU)
            {
                Vector2 t = ax; ax = ay; ay = t;
                float th = halfU; halfU = halfV; halfV = th;
            }

            // Okap wychodzi poza lico sciany - stad cien pod krawedzia dachu.
            halfU += EaveM;
            halfV += EaveM;

            float rise = Mathf.Min(4.2f, halfV * 0.85f);
            if (rise < 0.6f) return;

            // Skrocenie kalenicy przy czterospadowym. Polowa krotszego boku daje
            // naturalny kat polaci szczytowej, taki sam jak polaci glownych.
            float inset = hipped ? Mathf.Min(halfV, halfU * 0.6f) : 0f;

            Vector2 a0 = c - ax * halfU - ay * halfV;
            Vector2 a1 = c + ax * halfU - ay * halfV;
            Vector2 b1 = c + ax * halfU + ay * halfV;
            Vector2 b0 = c - ax * halfU + ay * halfV;
            Vector2 r0 = c - ax * (halfU - inset);
            Vector2 r1 = c + ax * (halfU - inset);

            List<Vector3> verts = acc.Verts;
            List<Vector2> uvs = acc.Uvs;
            List<int> tris = acc.Tris[RoofPitchSub];

            float slope = Mathf.Sqrt(halfV * halfV + rise * rise);
            float k = FacadeTexture.RoofPitchUvPerMeter;

            // dwie polacie glowne (przy skroconej kalenicy sa to trapezy)
            AddSlope(verts, uvs, tris, a0, a1, r1, r0, yTop, yTop + rise, halfU * 2f, slope, k);
            AddSlope(verts, uvs, tris, b1, b0, r0, r1, yTop, yTop + rise, halfU * 2f, slope, k);

            if (hipped)
            {
                // polacie szczytowe - takze pokryte dachowka
                AddHipEnd(verts, uvs, tris, a0, b0, r0, yTop, yTop + rise, k);
                AddHipEnd(verts, uvs, tris, b1, a1, r1, yTop, yTop + rise, k);
            }
            else
            {
                // dwa szczyty - trojkatne SCIANY pod kalenica, wiec material muru
                List<int> gableTris = acc.Tris[RoofEdgeSub];
                AddGableEnd(verts, uvs, gableTris, a0, b0, r0, yTop, yTop + rise);
                AddGableEnd(verts, uvs, gableTris, b1, a1, r1, yTop, yTop + rise);
            }
        }

        /// <summary>Trojkatna polac szczytowa dachu czterospadowego. Jednostronna,
        /// z nawijaniem dobranym tak, zeby normalna patrzyla do gory.</summary>
        private static void AddHipEnd(List<Vector3> verts, List<Vector2> uvs, List<int> tris,
                                      Vector2 p0, Vector2 p1, Vector2 apex,
                                      float yEave, float yRidge, float k)
        {
            int v0 = verts.Count;
            verts.Add(new Vector3(p0.x, yEave, p0.y)); uvs.Add(new Vector2(p0.x * k, p0.y * k));
            verts.Add(new Vector3(p1.x, yEave, p1.y)); uvs.Add(new Vector2(p1.x * k, p1.y * k));
            verts.Add(new Vector3(apex.x, yRidge, apex.y)); uvs.Add(new Vector2(apex.x * k, apex.y * k));

            Vector3 nrm = Vector3.Cross(verts[v0 + 1] - verts[v0], verts[v0 + 2] - verts[v0]);
            if (nrm.y > 0f)
            {
                tris.Add(v0); tris.Add(v0 + 1); tris.Add(v0 + 2);
            }
            else
            {
                tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 1);
            }
        }

        private static void AddSlope(List<Vector3> verts, List<Vector2> uvs, List<int> tris,
                                     Vector2 e0, Vector2 e1, Vector2 rTop1, Vector2 rTop0,
                                     float yEave, float yRidge, float lenU, float lenV, float k)
        {
            int v0 = verts.Count;
            verts.Add(new Vector3(e0.x, yEave, e0.y)); uvs.Add(new Vector2(0f, 0f));
            verts.Add(new Vector3(e1.x, yEave, e1.y)); uvs.Add(new Vector2(lenU * k, 0f));
            verts.Add(new Vector3(rTop1.x, yRidge, rTop1.y)); uvs.Add(new Vector2(lenU * k, lenV * k));
            verts.Add(new Vector3(rTop0.x, yRidge, rTop0.y)); uvs.Add(new Vector2(0f, lenV * k));

            // polac musi patrzec do gory - jak wszedzie, sprawdzamy zamiast zakladac
            Vector3 n = Vector3.Cross(verts[v0 + 1] - verts[v0], verts[v0 + 3] - verts[v0]);
            if (n.y > 0f)
            {
                tris.Add(v0); tris.Add(v0 + 1); tris.Add(v0 + 2);
                tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 3);
            }
            else
            {
                tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 1);
                tris.Add(v0); tris.Add(v0 + 3); tris.Add(v0 + 2);
            }
        }

        private static void AddGableEnd(List<Vector3> verts, List<Vector2> uvs, List<int> tris,
                                        Vector2 p0, Vector2 p1, Vector2 apex,
                                        float yEave, float yRidge)
        {
            int v0 = verts.Count;
            verts.Add(new Vector3(p0.x, yEave, p0.y)); uvs.Add(Vector2.zero);
            verts.Add(new Vector3(p1.x, yEave, p1.y)); uvs.Add(Vector2.zero);
            verts.Add(new Vector3(apex.x, yRidge, apex.y)); uvs.Add(Vector2.zero);

            tris.Add(v0); tris.Add(v0 + 1); tris.Add(v0 + 2);
            tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 1);   // druga strona - szczyt widac z obu
        }

        /// <summary>Prostokat o minimalnej powierzchni opisany na obrysie (rotating calipers
        /// w wersji naiwnej: kazda krawedz jako kandydat na kierunek).</summary>
        private static bool MinAreaRect(List<Vector2> ring, out Vector2 center,
                                        out Vector2 axis, out float halfU, out float halfV)
        {
            center = Vector2.zero; axis = new Vector2(1f, 0f);
            halfU = halfV = 0f;

            int n = ring.Count;
            if (n < 3) return false;

            float bestArea = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                Vector2 e = ring[(i + 1) % n] - ring[i];
                float len = e.magnitude;
                if (len < 1e-4f) continue;
                Vector2 ux = e / len;
                Vector2 uy = new Vector2(-ux.y, ux.x);

                float minX = float.MaxValue, maxX = float.MinValue;
                float minY = float.MaxValue, maxY = float.MinValue;
                for (int j = 0; j < n; j++)
                {
                    float px = Vector2.Dot(ring[j], ux);
                    float py = Vector2.Dot(ring[j], uy);
                    if (px < minX) minX = px; if (px > maxX) maxX = px;
                    if (py < minY) minY = py; if (py > maxY) maxY = py;
                }

                float area = (maxX - minX) * (maxY - minY);
                if (area < bestArea && area > 1e-3f)
                {
                    bestArea = area;
                    halfU = (maxX - minX) * 0.5f;
                    halfV = (maxY - minY) * 0.5f;
                    axis = ux;
                    center = ux * ((minX + maxX) * 0.5f) + uy * ((minY + maxY) * 0.5f);
                }
            }

            return bestArea < float.MaxValue;
        }

        /// <summary>Jeden prostokat sciany. Wlasne 4 wierzcholki - stad ostre
        /// krawedzie po RecalculateNormals i brak przeciekania UV miedzy scianami.</summary>
        private static void AddQuad(List<Vector3> verts, List<Vector2> uvs, List<int> tris,
                                    Vector2 a, Vector2 b, float yLo, float yHi,
                                    float u0, float u1, float v0uv, float v1uv, bool outward)
        {
            if (yHi - yLo < 0.01f) return;

            int v0 = verts.Count;
            verts.Add(new Vector3(a.x, yLo, a.y)); uvs.Add(new Vector2(u0, v0uv));
            verts.Add(new Vector3(b.x, yLo, b.y)); uvs.Add(new Vector2(u1, v0uv));
            verts.Add(new Vector3(b.x, yHi, b.y)); uvs.Add(new Vector2(u1, v1uv));
            verts.Add(new Vector3(a.x, yHi, a.y)); uvs.Add(new Vector2(u0, v1uv));

            if (outward)
            {
                tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 1);
                tris.Add(v0); tris.Add(v0 + 3); tris.Add(v0 + 2);
            }
            else
            {
                tris.Add(v0); tris.Add(v0 + 1); tris.Add(v0 + 2);
                tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 3);
            }
        }

        /// <summary>
        /// Jaki dach dostaje budynek. Tag "roof:shape" z OSM ma pierwszenstwo;
        /// bez niego zgadujemy po wielkosci - mala, niska i prawie prostokatna
        /// bryla to niemal na pewno dom jednorodzinny.
        ///
        /// Warunek prostokatnosci jest kluczowy: konstrukcja kalenicy idzie po
        /// prostokacie opisanym, wiec przy dziwnym obrysie dach by odstawal.
        /// </summary>
        private static RoofKind RoofKindFor(OsmWay way, List<Vector2> ring, float height)
        {
            if (way.RoofShape != null)
            {
                switch (way.RoofShape)
                {
                    case "hipped": case "pyramidal": case "half-hipped":
                        return RoofKind.Hipped;
                    case "gabled": case "gambrel": case "round":
                        return RoofKind.Gabled;
                    case "flat":
                        return RoofKind.Flat;
                }
            }

            if (height > FacadeTexture.PitchedMaxHeight) return RoofKind.Flat;

            float area = Area2(ring);
            if (area > FacadeTexture.PitchedMaxArea || area < 12f) return RoofKind.Flat;

            Vector2 c, ax;
            float hu, hv;
            if (!MinAreaRect(ring, out c, out ax, out hu, out hv)) return RoofKind.Flat;

            float rectArea = 4f * hu * hv;
            if (rectArea < 1e-3f) return RoofKind.Flat;
            if (area / rectArea <= 0.84f) return RoofKind.Flat;

            // Rzut blizszy kwadratu dostaje dach czterospadowy. Przy dlugim, waskim
            // obrysie kalenica i tak zajelaby prawie cala dlugosc, wiec roznica
            // bylaby niewidoczna, a dwuspadowy jest tanszy o dwa trojkaty.
            float longer = Mathf.Max(hu, hv), shorter = Mathf.Min(hu, hv);
            if (shorter > 1e-3f && longer / shorter < 1.55f) return RoofKind.Hipped;
            return RoofKind.Gabled;
        }

        /// <summary>
        /// Wysokosc bryly. Jawne "height" i "building:levels" z OSM wygrywaja zawsze.
        ///
        /// Bez nich kiedys bylo 8 m dla wszystkiego - czyli domek jednorodzinny mial
        /// wysokosc trzypietrowej kamienicy i do tego witryny sklepowe na parterze.
        /// Wiekszosc budynkow w Polsce ma tylko "building=yes", wiec o wysokosci
        /// decyduje tu glownie powierzchnia obrysu.
        /// </summary>
        private static float HeightFor(OsmWay way, List<Vector2> ring)
        {
            if (!float.IsNaN(way.HeightM) && way.HeightM > 0.5f) return way.HeightM;
            if (way.Levels > 0) return way.Levels * 3.0f;

            switch (way.Building)
            {
                case "garage": case "garages": case "carport": case "shed": case "hut":
                case "kiosk": case "toilets": case "roof": case "greenhouse":
                    return 3.0f;
                case "house": case "detached": case "semidetached_house":
                case "bungalow": case "cabin": case "farm":
                    return 6.5f;
                case "terrace": return 7.5f;
                case "barn": case "farm_auxiliary": case "stable": case "cowshed":
                    return 6.0f;
                case "industrial": case "warehouse": return 9.0f;
                case "apartments": return 15.0f;
                case "church": case "cathedral": return 18.0f;
            }

            float area = Area2(ring);
            if (area < 45f) return 3.2f;       // garaz, szopa
            if (area < 200f) return 6.5f;      // dom
            if (area < 700f) return 10.0f;     // maly blok, szkola
            return 12.0f;
        }

        /// <summary>Witryny sklepowe na parterze tylko tam, gdzie realnie bywaja:
        /// budynki handlowo-biurowe i duze budynki w zabudowie miejskiej.
        /// Domom i garazom witryny robily wyglad pawilonu z czarnymi bramami.</summary>
        private static bool WantsShopFront(OsmWay way, List<Vector2> ring, float height)
        {
            switch (way.Building)
            {
                case "commercial": case "retail": case "office":
                case "hotel": case "supermarket": case "mixed_use":
                    return height >= 6f;
                case "apartments": case "yes": case "residential":
                    return height >= 12f && Area2(ring) >= 300f;
                default:
                    return false;
            }
        }

        private static uint PosHash(double lat, double lon)
        {
            uint a = (uint)(int)Math.Round(lat * 1e5);
            uint b = (uint)(int)Math.Round(lon * 1e5);
            uint h = a * 374761393u + b * 668265263u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }

        private static float Area2(List<Vector2> p)
        {
            float s = 0f;
            for (int i = 0, n = p.Count; i < n; i++)
            {
                Vector2 a = p[i], b = p[(i + 1) % n];
                s += a.x * b.y - b.x * a.y;
            }
            return Math.Abs(s) * 0.5f;
        }

    }
}
