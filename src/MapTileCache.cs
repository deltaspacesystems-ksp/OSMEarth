using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Kafelki mapy planera: gotowe obrazki 256x256 na stalych poziomach zoomu.
    ///
    /// Wczesniej planer przy KAZDYM przewinieciu i zoomie rysowal od nowa cala
    /// mape wektorowo - dziesiatki tysiecy odcinkow na klatke, stad zaciecia.
    /// Teraz kafel powstaje raz i lezy w pamieci, a przewijanie to tylko
    /// narysowanie kilkunastu gotowych tekstur.
    ///
    /// Brakujace kafle:
    ///   - dane czyta watek w tle (MapData), najwyzej MaxJobs naraz,
    ///   - render idzie po RendersPerFrame na klatke, najblizsze srodka najpierw,
    ///   - w miejscu pustego kafla rysuje sie powiekszony kafel z nizszego zoomu,
    ///     wiec przy zoomie widac rozmyty obraz, a nie czarne dziury.
    ///
    /// Rzutowanie jest rownoprostokatne ze STALA szerokoscia odniesienia (RefLat).
    /// Przy przewinieciu dalej niz 3 st. od niej kafle licza sie od nowa - inaczej
    /// skala pozioma przestalaby pasowac do terenu.
    /// </summary>
    public sealed class MapTileCache
    {
        public const int TilePx = 256;
        /// <summary>0.5 * 2^8 = 128 m/px. Wyzej kafel ma ponad 60 km boku i kazdy
        /// wymaga zdekodowania calego regionu: tester pokazal 2 minuty do pelnego
        /// widoku przy 400 m/px. Do skali kontynentow jest widok mapy KSP.</summary>
        public const int MaxLevel = 8;

        /// <summary>Do tej skali mapa jest z kafli OSM. Dalej planer przechodzi
        /// w widok swiata: tekstura planety i siatka, bez danych OSM.</summary>
        public const float MaxTileViewMpp = 150f;

        /// <summary>Cala Ziemia (40 tys. km) miesci sie w oknie ~900 px.</summary>
        public const float MaxViewMpp = 60000f;
        private const int MaxTiles = 180;         // ~45 MB tekstur
        private const int MaxJobs = 2;
        private const int RendersPerFrame = 2;
        private const double BaseMpp = 0.5;

        private sealed class Tile
        {
            public int L, X, Y;
            public RenderTexture Rt;
            public MapData Job;
            public int LastUsed;
            public List<OsmPlace> Places;          // do etykiet - zostaja po wyrenderowaniu
            public List<RoadLabel> RoadLabels;
        }

        /// <summary>!= null: kafle dostaja zdjecie satelitarne pod spodem. Zmiana = Clear().</summary>
        public SatelliteSource Satellite;

        // zakres kafli z ostatniego Draw - etykiety biora z tych samych
        private int lastLevel = -1, lastTx0, lastTx1, lastTy0, lastTy1;
        public int Revision;          // rosnie z kazdym gotowym kaflem - sygnal do przeliczenia etykiet

        private readonly Dictionary<long, Tile> tiles = new Dictionary<long, Tile>();
        private readonly List<Tile> wanted = new List<Tile>();
        private readonly List<Tile> inFlight = new List<Tile>();
        private int frame;

        public double RefLat = double.NaN;
        public int Visible, Ready;
        public int Jobs { get { return inFlight.Count; } }

        public static double TileMpp(int level) { return BaseMpp * (1 << level); }

        /// <summary>Poziom, ktorego kafle ogladamy w skali 0.7-1.4 - wystarczajaco
        /// ostro, a przy zoomie kolkiem poziom nie przeskakuje co krok.</summary>
        public static int LevelFor(double viewMpp)
        {
            int l = (int)Math.Floor(Math.Log(viewMpp / BaseMpp, 2.0) + 0.5);
            return l < 0 ? 0 : (l > MaxLevel ? MaxLevel : l);
        }

        private static long Key(int l, int x, int y)
        {
            return ((long)l << 58) | (((long)x + (1L << 28)) << 29) | ((long)y + (1L << 28));
        }

        public void EnsureRef(double centerLat)
        {
            if (!double.IsNaN(RefLat) && Math.Abs(centerLat - RefLat) <= 3.0) return;
            Clear();
            RefLat = Math.Round(centerLat);
        }

        private static int FloorDiv(int a, int b)
        {
            int q = a / b;
            if ((a % b) != 0 && ((a < 0) ^ (b < 0))) q--;
            return q;
        }

        // ------------------------------------------------------------------
        //  Rysowanie - wolac w OnGUI przy Repaint, wewnatrz GUI.BeginGroup obszaru mapy
        // ------------------------------------------------------------------

        public void Draw(MapView view, float w, float h)
        {
            frame++;
            double mLat = view.BodyRadius * Geo.DegToRad;
            double mLon = mLat * Math.Cos(RefLat * Geo.DegToRad);
            double mpp = view.MetersPerPixel;

            int level = LevelFor(mpp);
            double tileM = TileMpp(level) * TilePx;

            double cx = view.CenterLon * mLon;
            double cy = view.CenterLat * mLat;
            double halfW = w * 0.5 * mpp, halfH = h * 0.5 * mpp;

            int tx0 = (int)Math.Floor((cx - halfW) / tileM), tx1 = (int)Math.Floor((cx + halfW) / tileM);
            int ty0 = (int)Math.Floor((cy - halfH) / tileM), ty1 = (int)Math.Floor((cy + halfH) / tileM);

            wanted.Clear();
            Visible = 0;
            Ready = 0;
            lastLevel = level; lastTx0 = tx0; lastTx1 = tx1; lastTy0 = ty0; lastTy1 = ty1;

            for (int ty = ty0; ty <= ty1; ty++)
            {
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    // Krawedzie zaokraglone do pikseli wspolnie dla sasiadow - bez
                    // tego miedzy kaflami przeswiecaja jednopikselowe szczeliny.
                    float x0 = (float)Math.Floor((tx * tileM - cx) / mpp + w * 0.5);
                    float x1 = (float)Math.Floor(((tx + 1) * tileM - cx) / mpp + w * 0.5);
                    float yTop = (float)Math.Floor(h * 0.5 - ((ty + 1) * tileM - cy) / mpp);
                    float yBot = (float)Math.Floor(h * 0.5 - (ty * tileM - cy) / mpp);
                    var rect = new Rect(x0, yTop, x1 - x0, yBot - yTop);

                    Visible++;
                    Tile t = GetOrCreate(level, tx, ty);
                    t.LastUsed = frame;

                    if (t.Rt != null)
                    {
                        GUI.DrawTexture(rect, t.Rt);
                        Ready++;
                        continue;
                    }

                    if (!DrawFromAncestor(level, tx, ty, rect)) DrawFromChildren(level, tx, ty, rect);
                    if (t.Job == null) wanted.Add(t);
                }
            }

            StartJobs(view, cx, cy, tileM, mLat, mLon);
            RenderFinished(view.BodyRadius, mLat, mLon);
            Evict();
        }

        /// <summary>Wycinek kafla z nizszego zoomu w miejscu brakujacego - do trzech
        /// poziomow w gore. Obraz jest rozmyty, ale jest, zamiast czarnej dziury.</summary>
        private bool DrawFromAncestor(int level, int tx, int ty, Rect rect)
        {
            for (int k = 1; k <= 3 && level + k <= MaxLevel; k++)
            {
                int f = 1 << k;
                int px = FloorDiv(tx, f), py = FloorDiv(ty, f);
                Tile p;
                if (!tiles.TryGetValue(Key(level + k, px, py), out p) || p.Rt == null) continue;

                p.LastUsed = frame;
                float u = (tx - px * f) / (float)f;
                float v = (ty - py * f) / (float)f;      // v rosnie na polnoc, jak ty
                GUI.DrawTextureWithTexCoords(rect, p.Rt, new Rect(u, v, 1f / f, 1f / f));
                return true;
            }
            return false;
        }

        /// <summary>Przy ODDALANIU kafli z wyzszego poziomu jeszcze nie ma, za to sa
        /// ostrzejsze z nizszego - skladamy kafel z jego czterech cwiartek.</summary>
        private void DrawFromChildren(int level, int tx, int ty, Rect rect)
        {
            if (level == 0) return;
            float hw = rect.width * 0.5f, hh = rect.height * 0.5f;
            for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                {
                    Tile c;
                    if (!tiles.TryGetValue(Key(level - 1, tx * 2 + dx, ty * 2 + dy), out c) || c.Rt == null) continue;
                    c.LastUsed = frame;
                    // dy = 1 to polnocna polowa, czyli GORNA czesc prostokata
                    GUI.DrawTexture(new Rect(rect.x + dx * hw, rect.y + (1 - dy) * hh, hw, hh), c.Rt);
                }
        }

        private Tile GetOrCreate(int l, int x, int y)
        {
            long key = Key(l, x, y);
            Tile t;
            if (!tiles.TryGetValue(key, out t))
            {
                t = new Tile { L = l, X = x, Y = y };
                tiles[key] = t;
            }
            return t;
        }

        private void StartJobs(MapView view, double cx, double cy, double tileM, double mLat, double mLon)
        {
            if (inFlight.Count >= MaxJobs || wanted.Count == 0) return;
            if (!LocalStore.Available && Satellite == null) return;

            wanted.Sort((a, b) =>
            {
                double da = Sq((a.X + 0.5) * tileM - cx) + Sq((a.Y + 0.5) * tileM - cy);
                double db = Sq((b.X + 0.5) * tileM - cx) + Sq((b.Y + 0.5) * tileM - cy);
                return da.CompareTo(db);
            });

            foreach (Tile t in wanted)
            {
                if (inFlight.Count >= MaxJobs) break;

                float mpp = (float)TileMpp(t.L);
                // Zapas 6 px: droga tuz za krawedzia ma grubosc, ktora wchodzi w kafel.
                double pad = 6 * mpp;
                BBox box;
                box.South = Geo.Clamp((t.Y * tileM - pad) / mLat, -89.9, 89.9);
                box.North = Geo.Clamp(((t.Y + 1) * tileM + pad) / mLat, -89.9, 89.9);
                box.West = (t.X * tileM - pad) / mLon;
                box.East = ((t.X + 1) * tileM + pad) / mLon;

                t.Job = new MapData(box, mpp < 8f, mpp)
                {
                    WantLabels = true,
                    BodyRadius = view.BodyRadius,
                    Satellite = Satellite,
                    SatLevel = SatelliteSource.LevelFor(mpp, view.BodyRadius, OsmSettings.SatBias),
                    SatMaxLevel = OsmSettings.SatMaxLevel
                };
                t.Job.Start();
                inFlight.Add(t);
            }
        }

        private static double Sq(double v) { return v * v; }

        private void RenderFinished(double bodyRadius, double mLat, double mLon)
        {
            int done = 0;
            for (int i = inFlight.Count - 1; i >= 0 && done < RendersPerFrame; i--)
            {
                Tile t = inFlight[i];
                if (!t.Job.Done) continue;

                inFlight.RemoveAt(i);
                if (t.Job.Error != null)
                {
                    Debug.LogError("[OSMRoads] kafel mapy: " + t.Job.Error);
                    t.Job = null;
                    continue;
                }

                double tileM = TileMpp(t.L) * TilePx;
                var tv = new MapView
                {
                    BodyRadius = bodyRadius,
                    RefLat = RefLat,
                    MetersPerPixel = (float)TileMpp(t.L),
                    Width = TilePx,
                    Height = TilePx,
                    CenterLat = (t.Y + 0.5) * tileM / mLat,
                    CenterLon = (t.X + 0.5) * tileM / mLon
                };

                t.Rt = new RenderTexture(TilePx, TilePx, 0, RenderTextureFormat.ARGB32)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                t.Rt.Create();
                MapRenderer.RenderTile(t.Rt, tv, t.Job.Ways, MapRenderer.MapBackground,
                                       Satellite != null ? t.Job.SatPatches : null);

                t.Places = t.Job.Places;
                t.RoadLabels = t.Job.RoadLabels;
                t.Job = null;     // obiekty OSM nie sa juz potrzebne - zostaje obraz i nazwy
                done++;
                Revision++;
            }
        }

        /// <summary>
        /// Etykiety z kafli widocznych przy ostatnim Draw. Brakujacy kafel zastepuje
        /// przodek (jak obraz) - nazwy nie migaja przy zoomie. Kolejnosc: miasta,
        /// miasteczka, ... na koncu ulice, bo pierwszy polozony napis wygrywa.
        /// </summary>
        public void AddLabels(MapLabels labels, MapView view, float w, float h)
        {
            if (lastLevel < 0) return;
            var srcTiles = new List<Tile>();
            var seen = new HashSet<Tile>();
            for (int ty = lastTy0; ty <= lastTy1; ty++)
                for (int tx = lastTx0; tx <= lastTx1; tx++)
                {
                    Tile t = null;
                    for (int k = 0; k <= 3 && lastLevel + k <= MaxLevel; k++)
                    {
                        Tile c;
                        if (tiles.TryGetValue(Key(lastLevel + k, FloorDiv(tx, 1 << k), FloorDiv(ty, 1 << k)), out c) && c.Rt != null)
                        { t = c; break; }
                    }
                    if (t != null && seen.Add(t)) srcTiles.Add(t);
                }

            for (int rank = 0; rank <= 4; rank++)
                foreach (Tile t in srcTiles) labels.AddPlaces(view, t.Places, rank, w, h);
            for (int rank = 0; rank <= 3; rank++)
                foreach (Tile t in srcTiles)
                {
                    if (t.RoadLabels == null) continue;
                    var sub = t.RoadLabels.FindAll(r => r.Rank == rank);
                    sub.Sort((a, b) => b.LengthM.CompareTo(a.LengthM));
                    labels.AddRoads(view, sub, w, h);
                }
        }

        private void Evict()
        {
            if (tiles.Count <= MaxTiles) return;

            var list = new List<KeyValuePair<long, Tile>>(tiles);
            list.Sort((a, b) => a.Value.LastUsed.CompareTo(b.Value.LastUsed));

            foreach (var kv in list)
            {
                if (tiles.Count <= MaxTiles) break;
                if (kv.Value.LastUsed == frame || kv.Value.Job != null) continue;
                Release(kv.Value);
                tiles.Remove(kv.Key);
            }
        }

        private static void Release(Tile t)
        {
            if (t.Rt != null) { t.Rt.Release(); UnityEngine.Object.Destroy(t.Rt); t.Rt = null; }
        }

        public void Clear()
        {
            foreach (Tile t in tiles.Values) Release(t);
            tiles.Clear();
            wanted.Clear();
            // Zadania w toku po prostu porzucamy - watek skonczy, nikt nie odbierze.
            foreach (Tile t in inFlight) if (t.Job != null) t.Job.Cancelled = true;
            inFlight.Clear();
        }
    }
}
