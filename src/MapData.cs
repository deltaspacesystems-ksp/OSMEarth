using System.Collections.Generic;
using System.Threading;

namespace OSMRoads
{
    /// <summary>
    /// Wycinek danych OSM pod okno mapy, czytany w tle.
    ///
    /// Osobno od TileJob, bo mapa ma inne potrzeby: obejmuje dziesiatki kilometrow
    /// (a nie 1.5 km kafla), nie potrzebuje wysokosci terenu ani geometrii 3D,
    /// i musi umiec sie przerwac, gdy uzytkownik przewinie widok dalej.
    /// </summary>
    public class MapData
    {
        public BBox Box;
        public List<OsmWay> Ways;
        public bool IncludeBuildings;

        private volatile bool done;
        public bool Done { get { return done; } }

        /// <summary>Ustawiane, gdy wynik przestal byc potrzebny - watek konczy
        /// robote, ale nikt jej nie odbiera.</summary>
        public volatile bool Cancelled;

        public string Error;

        /// <summary>Skala, w jakiej dane beda rysowane. 0 = bez filtra.</summary>
        public float MetersPerPixel;

        /// <summary>Zbierac nazwy miejsc i ulic do etykiet.</summary>
        public bool WantLabels;
        public double BodyRadius = 6371010.0;
        public List<OsmPlace> Places;
        public List<RoadLabel> RoadLabels;

        /// <summary>!= null: dociagnij tez kafle zdjec satelitarnych na ten poziom.</summary>
        public SatelliteSource Satellite;
        public int SatLevel, SatMaxLevel;
        public List<SatelliteSource.Patch> SatPatches;

        public MapData(BBox box, bool includeBuildings) : this(box, includeBuildings, 0f) { }

        public MapData(BBox box, bool includeBuildings, float metersPerPixel)
        {
            Box = box;
            IncludeBuildings = includeBuildings;
            MetersPerPixel = metersPerPixel;
        }

        public void Start()
        {
            var t = new Thread(Run) { IsBackground = true, Name = "OSMRoads.MapData" };
            t.Start();
        }

        private void Run()
        {
            try
            {
                if (Satellite != null)
                    SatPatches = Satellite.Collect(Box, SatLevel, SatMaxLevel);

                List<OsmPlace> places = WantLabels ? new List<OsmPlace>() : null;
                List<OsmWay> all = LocalStore.Read(Box, places);
                if (WantLabels)
                {
                    Places = places;
                    RoadLabels = MapLabels.ExtractRoads(all, BodyRadius);
                }

                float mpp = MetersPerPixel;
                double degPerPx = mpp / (6371010.0 * Geo.DegToRad);

                if (IncludeBuildings && mpp <= 0f)
                {
                    Ways = all;
                }
                else
                {
                    // Przy szerokim widoku budynki to wiekszosc obiektow, a i tak
                    // zlewaja sie w plame - odsiewamy je od razu, zeby nie wlec
                    // setek tysiecy obrysow do rysowania. Przy duzym oddaleniu
                    // odpadaja tez drogi niewidoczne w tej skali i obszary mniejsze
                    // od dwoch pikseli - to one zjadaly pamiec kafli po 60 km.
                    var keep = new List<OsmWay>(all.Count / 4);
                    foreach (OsmWay w in all)
                    {
                        if (w.IsBuilding) { if (IncludeBuildings) keep.Add(w); continue; }
                        if (mpp > 0f)
                        {
                            if (w.IsRoad && !MapRenderer.RoadVisibleAt(w.Highway, mpp)) continue;
                            if (w.IsArea && mpp >= 12f)
                            {
                                w.EnsureBounds();
                                if (!w.SpansAtLeast(degPerPx * 2.0)) continue;
                            }
                        }
                        keep.Add(w);
                    }
                    Ways = keep;
                }
            }
            catch (System.Exception e)
            {
                Error = e.ToString();
            }
            finally
            {
                done = true;
            }
        }
    }
}
