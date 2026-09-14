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
        public int AreaCount;
        public int WayCount, RoadSegments, BuildingCount, SkippedCount, LampCount;
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
                    ? LocalStore.Read(box)
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

                    if (maxTrees > 0)
                        Trees = TreeScatter.Build(grid, bodyRadius, ways, lat0, lon0, alt0,
                                                  treeDensity, maxTrees);
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
                }

                if (wantBuildings)
                {
                    BuildingResult br = BuildingMeshBuilder.Build(grid, bodyRadius, ways,
                                                                  lat0, lon0, alt0,
                                                                  buildingHeightScale, maxBuildings);
                    BuildFull = br.Full;
                    BuildCoarse = br.Coarse;
                    BuildingCount = br.Built;
                    SkippedCount = br.Skipped;
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
