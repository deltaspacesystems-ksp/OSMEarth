using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using OSMRoads;
using UnityEngine;

namespace OSMView
{
    /// <summary>
    /// osmview - podglad kafli moda bez KSP.
    ///
    ///   osmview &lt;lat&gt; &lt;lon&gt; [katalog-wyjscia] [--ring N] [--no-snap] [--tag nazwa]
    ///
    /// Buduje kafle DOKLADNIE tym samym kodem co gra (TileJob i buildery sa
    /// wkompilowane z src/), na terenie testowym z pagorkami, i renderuje kilka
    /// ujec do PNG: z gory, z ukosa, z ulicy, drzewa i latarnie z bliska.
    /// </summary>
    internal static class Program
    {
        private const double R = 6371010.0;
        private const float TileSize = 1500f;
        private const float RoadHeight = 0.3f;     // = domyslne roadHeight w OsmRoadsAddon

        private sealed class Part
        {
            public Vector3[] World;
            public Vector2[] Uv;
            public int[] Tris;
            public Mat Mat;
        }

        private static readonly List<Part> parts = new List<Part>();
        private static double sceneLat, sceneLon, sceneAlt;

        private static int Main(string[] args)
        {
            if (args.Length >= 3 && args[0] == "mapa") return PlannerTest.Run(args);
            if (args.Length >= 3 && args[0] == "poly") return PolygonTest.Run(args);
            if (args.Length >= 1 && args[0] == "sat") return SatTest.Run(args);
            if (args.Length >= 1 && args[0] == "sag") return SagTest.Run(args);
            if (args.Length >= 1 && args[0] == "search") return SearchTest.Run(args);
            if (args.Length >= 4 && args[0] == "find") return FindTest.Run(args);
            if (args.Length >= 6 && args[0] == "runways") return RunwaysTest.Run(args);
            if (args.Length >= 1 && args[0] == "route") return RouteTest.Run(args);
            if (args.Length >= 3 && args[0] == "style") return StyleTest.Run(args);

            if (args.Length < 2)
            {
                Console.WriteLine("uzycie: osmview <lat> <lon> [katalog] [--ring N] [--no-snap] [--tag nazwa]");
                Console.WriteLine("        osmview mapa <lat> <lon> [katalog]      - test mapy planera");
                return 1;
            }

            double lat = double.Parse(args[0], CultureInfo.InvariantCulture);
            double lon = double.Parse(args[1], CultureInfo.InvariantCulture);
            string outDir = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : "osmview-out";
            int ring = 1;
            bool snap = true;
            string tag = "";
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--ring") ring = int.Parse(args[++i]);
                else if (args[i] == "--no-snap") snap = false;
                else if (args[i] == "--tag") tag = "_" + args[++i];
            }
            Directory.CreateDirectory(outDir);

            var sw = Stopwatch.StartNew();
            LocalStore.Reload();
            Console.WriteLine(LocalStore.Report);
            if (!LocalStore.Covers(Geo.BoxAround(lat, lon, 50, R)))
            {
                Console.WriteLine("brak paczki na ten punkt");
                return 2;
            }

            var grid = new TileGrid(TileSize, R);
            // Ten sam dobor kafli co w grze (TileGrid.TilesAround), promien w metrach.
            var keys = new List<long>();
            // (ring + 0.75) kafla: przy promieniu rownym ring kafli naroznikowe
            // wypadaja, bo ich srodek jest dalej niz ring * rozmiar.
            grid.TilesAround(lat, lon, (ring + 0.75f) * TileSize, keys);
            int cLat = (int)(keys[0] >> 32), cLon = (int)(uint)keys[0];
            grid.CenterOf(cLat, cLon, out sceneLat, out sceneLon);
            sceneAlt = TerrainGrid.TrueHeight(sceneLat, sceneLon);

            var trees = new List<KeyValuePair<Vector3, TreeInstance>>();
            OsmWay street = null;
            double streetBest = double.MaxValue;
            int buildings = 0, lamps = 0;

            foreach (long key in keys)
                {
                    int kLat = (int)(key >> 32), kLon = (int)(uint)key;
                    bool isCenter = key == keys[0];
                    BBox box = grid.BoxOf(kLat, kLon);
                    double tLat, tLon;
                    grid.CenterOf(kLat, kLon, out tLat, out tLon);

                    var terrain = TerrainGrid.Create(box, 64, 0.30);
                    terrain.Fill(TerrainGrid.TrueHeight);
                    double alt0 = TerrainGrid.TrueHeight(tLat, tLon);

                    var job = new TileJob(null, box, true, terrain, R, tLat, tLon, alt0,
                                          true, true, true, true, 1f, 4000,
                                          RoadHeight, 1f, 1f, 3000);
                    job.Start();
                    while (!job.Done) System.Threading.Thread.Sleep(5);
                    if (job.Error != null) { Console.WriteLine("TileJob: " + job.Error); return 3; }

                    Vector3 origin = TileOrigin(tLat, tLon, alt0);
                    buildings += job.BuildingCount;
                    lamps += job.LampCount;

                    if (job.Landuse != null)
                    {
                        if (snap) SnapKeepingLift(job.Landuse, tLat, tLon, alt0, terrain);
                        AddMesh(job.Landuse, origin, i => LanduseMat(i));
                    }
                    if (job.Roads != null)
                    {
                        if (snap) Snap(job.Roads, tLat, tLon, alt0, RoadHeight);
                        Buried(job.Roads, tLat, tLon, alt0);
                        RoadSanity(job.Roads);
                        AddMesh(job.Roads, origin, i => RoadMat(i));
                    }
                    if (job.Lamps != null) AddMesh(job.Lamps, origin, i => LampMat(i));
                    if (job.BuildFull != null) AddMesh(job.BuildFull, origin, i => BuildingMat(i));
                    if (job.Trees != null)
                        foreach (TreeInstance t in job.Trees) trees.Add(new KeyValuePair<Vector3, TreeInstance>(origin, t));

                    if (isCenter)
                        foreach (OsmWay w in LocalStore.Read(box))
                        {
                            if (!w.IsRoad) continue;
                            RoadClass rc = RoadClasses.From(w.Highway);
                            if (rc != RoadClass.Minor && rc != RoadClass.Major) continue;
                            foreach (GeoPoint g in w.Points)
                            {
                                double d = Math.Abs(g.Lat - tLat) + Math.Abs(g.Lon - tLon);
                                if (d < streetBest) { streetBest = d; street = w; }
                            }
                        }
                }

            AddTrees(trees);
            AddTerrain(ring);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "drogi pod terenem: {0:N0} z {1:N0} probek ({2:P2}), trojkatow z dziura {3:N0} z {4:N0}, najglebiej {5:F2} m",
                buriedSamples, roadSamples, buriedSamples / (double)Math.Max(1, roadSamples),
                buriedTris, roadTris, buriedMax));
            Console.WriteLine(string.Format("scena: {0} kafli, {1} budynkow, {2} latarni, {3} drzew, {4:N0} czesci, {5:F1} s",
                              keys.Count, buildings, lamps, trees.Count, parts.Count, sw.Elapsed.TotalSeconds));

            float span = TileSize * (2 * ring + 1);
            Render(Path.Combine(outDir, "1_gora" + tag + ".png"), new Vector3(0, span * 0.9f, 1), new Vector3(0, 0, 0), 62f, 1600, 1600);
            Render(Path.Combine(outDir, "2_ukos" + tag + ".png"), new Vector3(-span * 0.30f, 420, -span * 0.30f), new Vector3(0, 0, 0), 60f, 1600, 900);
            { Vector3 g0 = Ground(lat, lon, 0); Render(Path.Combine(outDir, "7_srodek" + tag + ".png"), g0 + new Vector3(-70, 40, -90), g0 + new Vector3(0, 8, 0), 55f, 1600, 900); }

            if (street != null)
            {
                int k = street.Points.Count / 2;
                GeoPoint a = street.Points[Math.Max(0, k - 1)], b = street.Points[Math.Min(street.Points.Count - 1, k + 1)];
                Vector3 pa = Ground(a.Lat, a.Lon, 1.9f), pb = Ground(b.Lat, b.Lon, 1.9f);
                Vector3 dir = (pb - pa); dir.y = 0; dir = dir.normalized;
                Vector3 eye = pa + new Vector3(dir.z, 0, -dir.x) * 1.5f;
                eye.y = Ground(a.Lat, a.Lon, 0).y + 1.9f;
                Render(Path.Combine(outDir, "3_ulica" + tag + ".png"), eye, eye + dir * 100f + new Vector3(0, -1.5f, 0), 75f, 1600, 900);
                Render(Path.Combine(outDir, "4_ulica_z_gory" + tag + ".png"), eye + new Vector3(0, 60, 0) - dir * 60f, eye + dir * 40f, 60f, 1600, 900);
            }

            if (trees.Count > 0)
            {
                KeyValuePair<Vector3, TreeInstance> best = trees[0];
                float bd = float.MaxValue;
                foreach (var t in trees)
                {
                    Vector3 p = t.Key + t.Value.Pos;
                    float d = p.x * p.x + p.z * p.z;
                    if (d < bd) { bd = d; best = t; }
                }
                Vector3 tp = best.Key + best.Value.Pos;
                Render(Path.Combine(outDir, "5_drzewa" + tag + ".png"), tp + new Vector3(-30, 9, -38), tp + new Vector3(0, 6, 0), 60f, 1600, 900);
                Render(Path.Combine(outDir, "6_drzewa_daleko" + tag + ".png"), tp + new Vector3(-260, 60, -300), tp, 45f, 1600, 900);
            }

            Console.WriteLine(string.Format("gotowe w {0:F1} s -> {1}", sw.Elapsed.TotalSeconds, Path.GetFullPath(outDir)));
            return 0;
        }

        // ------------------------------------------------------------------
        //  Uklad sceny
        // ------------------------------------------------------------------

        /// <summary>Srodek kafla w ukladzie sceny. Obrot miedzy plaszczyznami stycznymi
        /// sasiednich kafli (setne czesci stopnia) pomijamy - na podgladzie niewidoczny.</summary>
        private static Vector3 TileOrigin(double lat, double lon, double alt0)
        {
            double e, n;
            Geo.ToLocalMeters(lat, lon, sceneLat, sceneLon, R, out e, out n);
            return new Vector3((float)e, (float)(alt0 - sceneAlt - Geo.CurvatureDrop(e, n, R)), (float)n);
        }

        private static Vector3 Ground(double lat, double lon, float lift)
        {
            double e, n;
            Geo.ToLocalMeters(lat, lon, sceneLat, sceneLon, R, out e, out n);
            double y = TerrainGrid.TrueHeight(lat, lon) - sceneAlt - Geo.CurvatureDrop(e, n, R) + lift;
            return new Vector3((float)e, (float)y, (float)n);
        }

        /// <summary>To samo, co GroundSnapper w grze: PQS punkt po punkcie
        /// zamiast siatki 64x64.</summary>
        private static void Snap(MeshData d, double lat0, double lon0, double alt0, float offset)
        {
            for (int i = 0; i < d.Verts.Length; i++)
            {
                Vector3 v = d.Verts[i];
                double la, lo;
                Geo.FromTangent(v.x, v.z, lat0, lon0, R, out la, out lo);
                double lift = offset;
                if (d.Uvs2 != null) lift = d.Uvs2[i].y > 0.5f ? d.Uvs2[i].x : offset + d.Uvs2[i].x;
                double y = TerrainGrid.TrueHeight(la, lo) - alt0 - Geo.TangentDrop(v.x, v.z, R) + lift;
                d.Verts[i] = new Vector3(v.x, (float)y, v.z);
            }
        }

        private static long roadSamples, buriedSamples, roadTris, buriedTris;
        private static double buriedMax;

        /// <summary>
        /// Ile jezdni lezy POD terenem. W kazdym trojkacie wstegi probkujemy
        /// punkty barycentryczne i porownujemy wysokosc asfaltu z prawdziwym
        /// terenem. Wezly sa przyklejone dokladnie, wiec wszystko, co tu wyjdzie,
        /// to teren wystajacy ponad prosty odcinek miedzy wezlami - "dziura w drodze".
        /// </summary>
        private static void Buried(MeshData d, double lat0, double lon0, double alt0)
        {
            float[,] bary = { { 1f/3, 1f/3 }, { 0.5f, 0.25f }, { 0.25f, 0.5f }, { 0.25f, 0.25f }, { 0.1f, 0.45f }, { 0.45f, 0.1f } };
            foreach (int[] tris in d.SubTris)
            {
                if (tris == null) continue;
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    Vector3 a = d.Verts[tris[t]], b = d.Verts[tris[t + 1]], c = d.Verts[tris[t + 2]];
                    roadTris++;
                    bool hole = false;
                    for (int k = 0; k < bary.GetLength(0); k++)
                    {
                        float u = bary[k, 0], v = bary[k, 1];
                        Vector3 p = a * (1 - u - v) + b * u + c * v;
                        double la, lo;
                        Geo.FromTangent(p.x, p.z, lat0, lon0, R, out la, out lo);
                        double ground = TerrainGrid.TrueHeight(la, lo) - alt0 - Geo.TangentDrop(p.x, p.z, R);
                        double depth = ground - p.y;
                        roadSamples++;
                        if (depth > 0.02) { buriedSamples++; hole = true; if (depth > buriedMax) buriedMax = depth; }
                    }
                    if (hole) buriedTris++;
                }
            }
        }

        /// <summary>GroundSnapper.RequestKeepingLift: unos z siatki przeniesiony na PQS.</summary>
        private static void SnapKeepingLift(MeshData d, double lat0, double lon0, double alt0, TerrainGrid grid)
        {
            for (int i = 0; i < d.Verts.Length; i++)
            {
                Vector3 v = d.Verts[i];
                double la, lo;
                Geo.FromTangent(v.x, v.z, lat0, lon0, R, out la, out lo);
                double drop = Geo.TangentDrop(v.x, v.z, R);
                double lift = v.y - ((grid.HeightAt(la, lo) - alt0) - drop);
                double y = TerrainGrid.TrueHeight(la, lo) - alt0 - drop + lift;
                d.Verts[i] = new Vector3(v.x, (float)y, v.z);
            }
        }

        private static void AddMesh(MeshData d, Vector3 origin, Func<int, Mat> mat)
        {
            var world = new Vector3[d.Verts.Length];
            for (int i = 0; i < world.Length; i++) world[i] = origin + d.Verts[i];
            for (int s = 0; s < d.SubTris.Length; s++)
                if (d.SubTris[s] != null && d.SubTris[s].Length > 0 && mat(s) != null)
                    parts.Add(new Part { World = world, Uv = d.Uvs, Tris = d.SubTris[s], Mat = mat(s) });
        }

        private static void AddTrees(List<KeyValuePair<Vector3, TreeInstance>> trees)
        {
            if (trees.Count == 0) return;
            var species = new[]
            {
                new { Trunk = ProceduralTree.MakeTrunk(false), Leaf = ProceduralTree.MakeFoliage(false),
                      LeafMat = new Mat { Albedo = Tex.From(ProceduralTree.MakeFoliageTexture(false)), Cutout = true, TwoSided = true, UpNormal = true, Name = "liscie" } },
                new { Trunk = ProceduralTree.MakeTrunk(true), Leaf = ProceduralTree.MakeFoliage(true),
                      LeafMat = new Mat { Albedo = Tex.From(ProceduralTree.MakeFoliageTexture(true)), Cutout = true, TwoSided = true, UpNormal = true, Name = "igly" } }
            };
            var bark = new Mat { Albedo = Tex.From(ProceduralTree.MakeBarkTexture()), Name = "kora" };

            foreach (var kv in trees)
            {
                TreeInstance t = kv.Value;
                var sp = species[Math.Min(1, (int)t.Kind)];
                float yaw = t.YawDeg * Mathf.Deg2Rad, c = Mathf.Cos(yaw), s = Mathf.Sin(yaw);
                Vector3 pos = kv.Key + t.Pos;

                foreach (var pair in new[] { new { M = sp.Trunk, Mt = bark }, new { M = sp.Leaf, Mt = sp.LeafMat } })
                {
                    var w = new Vector3[pair.M.vertices.Length];
                    for (int i = 0; i < w.Length; i++)
                    {
                        Vector3 v = pair.M.vertices[i] * t.Scale;
                        // Quaternion.Euler(0, yaw, 0) w Unity: obrot wokol Y zgodnie z lewoskretnym ukladem
                        w[i] = pos + new Vector3(v.x * c + v.z * s, v.y, -v.x * s + v.z * c);
                    }
                    parts.Add(new Part { World = w, Uv = pair.M.uv, Tris = pair.M.triangles, Mat = pair.Mt });
                }
            }
        }

        private static void AddTerrain(int ring)
        {
            // Teren "PQS": prawdziwa funkcja, siatka co 8 m. Kolor zamiast zdjec Mirage'a.
            float half = TileSize * (ring + 0.5f) + 300f;
            const float step = 8f;
            int n = (int)(2 * half / step) + 1;
            var world = new Vector3[n * n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    double e = -half + i * step, no = -half + j * step, la, lo;
                    Geo.FromLocalMeters(e, no, sceneLat, sceneLon, R, out la, out lo);
                    world[j * n + i] = new Vector3((float)e, (float)(TerrainGrid.TrueHeight(la, lo) - sceneAlt - Geo.CurvatureDrop(e, no, R)), (float)no);
                }
            var tris = new int[(n - 1) * (n - 1) * 6];
            int k = 0;
            for (int j = 0; j < n - 1; j++)
                for (int i = 0; i < n - 1; i++)
                {
                    int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                    // przod widziany z gory (zgodnie z zegarem patrzac w dol)
                    tris[k++] = a; tris[k++] = c; tris[k++] = b;
                    tris[k++] = b; tris[k++] = c; tris[k++] = d;
                }
            parts.Insert(0, new Part { World = world, Tris = tris, Mat = new Mat { Tint = new Color(0.40f, 0.46f, 0.30f), Name = "teren" } });
        }

        // ------------------------------------------------------------------
        //  Materialy - te same kolory i tekstury co MaterialLib
        // ------------------------------------------------------------------

        private static readonly Dictionary<string, Mat> mats = new Dictionary<string, Mat>();

        private static Mat Cached(string key, Func<Mat> make)
        {
            Mat m;
            if (!mats.TryGetValue(key, out m)) mats[key] = m = make();
            return m;
        }

        private static Mat LanduseMat(int i)
        {
            return Cached("lu" + i, () => new Mat { Tint = AreaKindColors.Tint((AreaKind)i), Name = ((AreaKind)i).ToString() });
        }

        private static Mat RoadMat(int i)
        {
            if (i == RoadMeshBuilder.SubLampPool) return null;     // plamy swiatla - addytywne, tylko w nocy w grze
            return Cached("rd" + i, () => new Mat { Albedo = Tex.From(RoadTexture.For((RoadClass)i)), Name = ((RoadClass)i).ToString() });
        }

        private static Mat LampMat(int i)
        {
            return Cached("lp" + i, () => new Mat { Tint = i == 0 ? MaterialPalette.LampPole : MaterialPalette.LampHead, Name = i == 0 ? "slup" : "klosz" });
        }

        private static Mat BuildingMat(int i)
        {
            int b = MaterialPalette.FacadeBuckets;
            return Cached("bd" + i, () =>
            {
                if (i < b) return new Mat { Albedo = Tex.From(FacadeTexture.Albedo), Tint = MaterialPalette.FacadeTints[i], Name = "elewacja" + i };
                if (i == b) return new Mat { Albedo = Tex.From(FacadeTexture.PlinthAlbedo), Tint = MaterialPalette.Plinth, Name = "parter" };
                if (i == b + 1) return new Mat { Tint = MaterialPalette.RoofEdge, Name = "attyka" };
                if (i == b + 2) return new Mat { Albedo = Tex.From(FacadeTexture.RoofFlat), Tint = MaterialPalette.RoofFlat, Name = "dach plaski" };
                if (i == BuildingMeshBuilder.ChurchSub) return new Mat { Albedo = Tex.From(FacadeTexture.ChurchAlbedo), Tint = new Color(0.96f, 0.93f, 0.86f), Name = "kosciol" };
                if (i == BuildingMeshBuilder.EaveShadowSub) return new Mat { Tint = new Color(0.42f, 0.41f, 0.40f), Name = "okap" };
                if (i >= b + 4 + MaterialPalette.RoofColors.Length) return new Mat { Albedo = Tex.From(FacadeTexture.HouseAlbedo), Tint = MaterialPalette.HouseTints[(i - b - 4 - MaterialPalette.RoofColors.Length) % MaterialPalette.HouseTints.Length], Name = "dom" };
                if (i >= b + 4) return new Mat { Albedo = Tex.From(FacadeTexture.RoofTilesNeutral), Tint = MaterialPalette.RoofColors[i - b - 4], Name = "dach kolor" + (i - b - 4) };
                return new Mat { Albedo = Tex.From(FacadeTexture.RoofTiles), Tint = MaterialPalette.RoofTiles, Name = "dachowka" };
            });
        }

        private static void Render(string path, Vector3 eye, Vector3 target, float fov, int w, int h)
        {
            var sw = Stopwatch.StartNew();
            var r = new Raster(w, h);
            r.Camera(eye, target, fov);
            r.Clear();
            foreach (Part p in parts) r.Draw(p.World, p.Uv, p.Tris, p.Mat);
            Png.Save(path, r.Rgb, w, h);
            Console.WriteLine(string.Format("  {0}: {1:N0} trojkatow, odrzuconych tylem {2:N0}, {3:F1} s",
                              Path.GetFileName(path), r.Tris, r.Culled, sw.Elapsed.TotalSeconds));
        }
    
        /// <summary>Trojkaty jezdni (bez poboczy) odwrocone albo stojace deba = kolce na drodze.</summary>
        private static void RoadSanity(MeshData m)
        {
            int total = 0, flipped = 0, steep = 0;
            var flipBySub = new int[m.SubTris.Length];
            for (int s = 0; s < m.SubTris.Length; s++)
            {
                int[] t = m.SubTris[s];
                for (int k = 0; k < t.Length; k += 3)
                {
                    int a = t[k], b = t[k + 1], c = t[k + 2];
                    bool skirt = false;
                    foreach (int q in new[] { a, b, c })
                    {
                        float u = m.Uvs[q].x;
                        if (Math.Abs(u - RoadMeshBuilder.SkirtUL) < 1e-4 || Math.Abs(u - RoadMeshBuilder.SkirtUR) < 1e-4) skirt = true;
                    }
                    if (skirt) continue;
                    Vector3 e1 = m.Verts[b] - m.Verts[a], e2 = m.Verts[c] - m.Verts[a];
                    Vector3 nrm = Vector3.Cross(e1, e2);
                    float len = nrm.magnitude;
                    if (len < 1e-6f) continue;
                    total++;
                    float ny = nrm.y / len;
                    if (ny < 0f) { flipped++; flipBySub[s]++; }
                    else if (ny < 0.5f) steep++;
                }
            }
            Console.WriteLine("  jezdnia: {0} trojkatow, odwroconych {1} [{3}], stromych {2}", total, flipped, steep, string.Join(",", flipBySub));
        }    }
}
