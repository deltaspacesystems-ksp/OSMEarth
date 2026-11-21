using System;
using System.Collections.Generic;
using System.Threading;

namespace OSMRoads
{
    /// <summary>
    /// Cala ciezka robota kafla wykonana POZA glownym watkiem: parsowanie XML-a
    /// i budowa tablic wierzcholkow.
    ///
    /// Warunkiem jest to, ze nic tutaj nie dotyka Unity ani KSP. Wysokosci terenu
    /// przychodza gotowe w TerrainGrid (spróbkowanej wczesniej na glownym watku),
    /// a wynikiem sa zwykle tablice w MeshData - obiekty Mesh powstaja dopiero
    /// po powrocie, w OsmRoadsAddon.
    /// </summary>
    public class TileJob
    {
        // --- wejscie ---
        private readonly string xml;
        private readonly BBox box;
        private readonly bool fromLocalStore;
        private readonly TerrainGrid grid;
        private readonly double bodyRadius, lat0, lon0, alt0;
        private readonly bool wantRoads, wantBuildings, wantLanduse, wantLamps;
        private readonly float treeDensity;
        private readonly int maxTrees;
        private readonly float roadHeight, widthScale, buildingHeightScale;
        private readonly int maxBuildings;

        // --- wyjscie (czytane dopiero gdy Done) ---
        public MeshData Roads;
        public MeshData Lamps;
        public MeshData BuildFull;
        public MeshData BuildCoarse;
        public MeshData Landuse;
        public List<TreeInstance> Trees;
        public List<UnityEngine.Vector3> LampHeads;
        internal List<CropArea> CropAreas;
        internal Obstacles Blocked;
        public int AreaCount;
        public int WayCount, RoadSegments, BuildingCount, SkippedCount, LampCount;
        public int Lod2Count;          // z tego budynki 3D GUGiK
        public string Lod2Error;
        public string Error;

        /// <summary>Budowac plachte landuse? Drzewa i tak powstaja z tych samych
        /// obszarow - wylaczenie oszczedza tylko geometrie wypelnien.</summary>
        public bool BuildLanduseMesh = true;

        private volatile bool done;
        public bool Done { get { return done; } }

        public TileJob(string xml, BBox box, bool fromLocalStore,
                       TerrainGrid grid, double bodyRadius,
                       double lat0, double lon0, double alt0,
                       bool wantRoads, bool wantBuildings, bool wantLanduse, bool wantLamps,
                       float treeDensity, int maxTrees,
                       float roadHeight, float widthScale,
                       float buildingHeightScale, int maxBuildings)
        {
            this.xml = xml;
            this.box = box;
            this.fromLocalStore = fromLocalStore;
            this.grid = grid;
            this.bodyRadius = bodyRadius;
            this.lat0 = lat0; this.lon0 = lon0; this.alt0 = alt0;
            this.wantRoads = wantRoads; this.wantBuildings = wantBuildings;
            this.wantLanduse = wantLanduse; this.wantLamps = wantLamps;
            this.treeDensity = treeDensity; this.maxTrees = maxTrees;
            this.roadHeight = roadHeight; this.widthScale = widthScale;
            this.buildingHeightScale = buildingHeightScale;
            this.maxBuildings = maxBuildings;
        }

        private void Warp(MeshData d)
        {
            if (d == null || d.Verts == null) return;
            for (int i = 0; i < d.Verts.Length; i++) d.Verts[i] = WarpPoint(d.Verts[i]);
        }

        private UnityEngine.Vector3 WarpPoint(UnityEngine.Vector3 v)
        {
            double la, lo, e, n, drop;
            Geo.FromLocalMeters(v.x, v.z, lat0, lon0, bodyRadius, out la, out lo);
            // promien na wysokosci kafla: tam lezy jego srodek w swiecie (GetWorldSurfacePosition
            // z alt0), wiec punkty na styku dwoch kafli wychodza w tym samym miejscu co do milimetra
            Geo.ToTangent(la, lo, lat0, lon0, bodyRadius + alt0, out e, out n, out drop);
            double y = v.y + Geo.CurvatureDrop(v.x, v.z, bodyRadius) - drop;
            return new UnityEngine.Vector3((float)e, (float)y, (float)n);
        }

        /// <summary>Kafel czyta dane z zapasem ~220 m: most u sasiada i jego najazd w tym kaflu
        /// (albo odwrotnie) musza byc policzone z tych samych drog w obu kaflach. Budowane i tak
        /// jest tylko to, co nalezy do kafla (clipE/clipN).</summary>
        private BBox Padded(BBox b)
        {
            double dLat = 0.002, dLon = dLat / Math.Max(0.2, Math.Cos(lat0 * Geo.DegToRad));
            b.South -= dLat; b.North += dLat; b.West -= dLon; b.East += dLon;
            return b;
        }

        public void Start()
        {
            var t = new Thread(Run);
            t.IsBackground = true;      // nie blokuj zamkniecia gry
            t.Name = "OSMRoads.TileJob";
            t.Start();
        }

        private void Run()
        {
            try
            {
                // Lokalny zbior czytamy prosto tutaj - to zwykly Seek po pliku,
                // wiec nie potrzebuje korutyny ani glownego watku.
                List<OsmWay> ways = fromLocalStore
                    ? LocalStore.Read(Padded(box))
                    : OsmXml.ParseWays(xml);
                WayCount = ways.Count;

                // Landuse pierwszy: to jest podloze, po nim ida drogi i budynki.
                // Polprzekatne kafla w metrach. Sluza do przyciecia dlugich
                // i wielkich obiektow (droga krajowa, kompleks lesny), ktore
                // dotykaja kafla, ale ciagna sie daleko poza niego - bez tego
                // kazdy kafel po drodze budowal cala droge od nowa.
                double cE, cN;
                Geo.ToLocalMeters(box.North, box.East, lat0, lon0, bodyRadius, out cE, out cN);
                // Bez marginesu. Prostokaty kafli maja sie ze soba STYKAC, a nie
                // zachodzic: dwie wspolplaszczyznowe jezdnie w pasie zakladki
                // to gwarantowany z-fighting na kazdym szwie.
                float clipE = (float)Math.Abs(cE);
                float clipN = (float)Math.Abs(cN);

                if (wantLanduse)
                {
                    int areas = 0;
                    if (BuildLanduseMesh)
                        Landuse = LanduseMeshBuilder.Build(grid, bodyRadius, ways, lat0, lon0, alt0,
                                                           60000, clipE, clipN, out areas);
                    AreaCount = areas;

                    CropAreas = OSMRoads.CropAreas.Areas(ways, lat0, lon0, bodyRadius, clipE, clipN);
                    if (CropAreas.Count > 0) Blocked = new Obstacles(ways, lat0, lon0, bodyRadius, clipE, clipN);

                    if (maxTrees > 0)
                        Trees = TreeScatter.Build(grid, bodyRadius, ways, lat0, lon0, alt0,
                                                  treeDensity, maxTrees, clipE, clipN);
                }

                if (wantRoads)
                {
                    RoadResult rr = RoadMeshBuilder.Build(grid, bodyRadius, ways,
                                                          lat0, lon0, alt0,
                                                          roadHeight, widthScale, wantLamps,
                                                          clipE, clipN);
                    Roads = rr.Roads;
                    Lamps = rr.Lamps;
                    RoadSegments = rr.Segments;
                    LampCount = rr.LampCount;
                    LampHeads = rr.LampHeads;
                }

                if (wantBuildings)
                {
                    // modele 3D GUGiK (tam, gdzie sa) - kotwice z calego pudelka kafla,
                    // budowane sa tylko te lezace w kaflu (clipE/clipN)
                    List<Lod2Building> lod2 = null;
                    // pudelko z zapasem ~70 m: model z kotwica u sasiada tez musi zaslonic
                    // obrys OSM lezacy w tym kaflu, inaczej na szwie bylyby oba
                    BBox wide = box;
                    double dLat = 0.00065, dLon = dLat / Math.Max(0.2, Math.Cos(lat0 * Geo.DegToRad));
                    wide.South -= dLat; wide.North += dLat; wide.West -= dLon; wide.East += dLon;
                    try { lod2 = Lod2Store.Read(wide); }
                    catch (Exception e) { Lod2Error = e.Message; }

                    BuildingResult br = BuildingMeshBuilder.Build(grid, bodyRadius, ways,
                                                                  lat0, lon0, alt0,
                                                                  buildingHeightScale, maxBuildings,
                                                                  lod2, clipE, clipN);
                    BuildFull = br.Full;
                    BuildCoarse = br.Coarse;
                    BuildingCount = br.Built;
                    SkippedCount = br.Skipped;
                    Lod2Count = br.Lod2;
                }

                // Na koniec wszystko z ukladu rownoodleglego (w nim liczy sie przynaleznosc do
                // kafla) na dokladna plaszczyzne styczna - punkty na styku kafli trafiaja
                // w swiecie w to samo miejsce, bez szczelin w poprzek drog.
                Warp(Roads); Warp(Lamps); Warp(BuildFull); Warp(BuildCoarse); Warp(Landuse);
                if (LampHeads != null)
                    for (int i = 0; i < LampHeads.Count; i++) LampHeads[i] = WarpPoint(LampHeads[i]);
                if (Trees != null)
                    for (int i = 0; i < Trees.Count; i++)
                    {
                        TreeInstance t = Trees[i];
                        t.Pos = WarpPoint(t.Pos);
                        Trees[i] = t;
                    }
            }
            catch (Exception e)
            {
                // Wyjatek na watku roboczym przepadlby po cichu - przenosimy go
                // do pola i zglaszamy na glownym watku.
                Error = e.ToString();
            }
            finally
            {
                done = true;
            }
        }
    }
}
