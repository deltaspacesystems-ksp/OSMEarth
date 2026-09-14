using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>Kandydat na nazwe ulicy: prosty odcinek drogi, wzdluz ktorego kladziemy napis.</summary>
    public struct RoadLabel
    {
        public string Name;
        public int Rank;                    // 0 autostrada ... 3 lokalna
        public double LatA, LonA, LatB, LonB;
        public float LengthM;
    }

    /// <summary>
    /// Etykiety mapy: nazwy miejsc i ulic.
    ///
    /// Dane przychodza z kafli (miejsca i ulice z paczek) albo, przy duzym oddaleniu,
    /// z places.idx. Rozmieszczenie jest chciwe: od najwazniejszych, a napis, ktory
    /// nachodzi na juz polozony, odpada. Liczy sie je tylko przy zmianie widoku -
    /// w klatce zostaje samo rysowanie.
    /// </summary>
    public sealed class MapLabels
    {
        private const int MaxLabels = 160;

        private struct Placed
        {
            public string Text;
            public Rect Box;              // prostokat tekstu PRZED obrotem
            public float Angle;
            public int Size;
            public bool Bold;
        }

        private readonly List<Placed> placed = new List<Placed>();
        private readonly List<Rect> taken = new List<Rect>();
        private readonly Dictionary<string, Vector2> sizeCache = new Dictionary<string, Vector2>();
        private readonly Dictionary<int, GUIStyle> styles = new Dictionary<int, GUIStyle>();
        private readonly HashSet<string> seenPlace = new HashSet<string>();
        private readonly HashSet<string> seenRoad = new HashSet<string>();

        public int Count { get { return placed.Count; } }

        // ------------------------------------------------------------------
        //  Wyciaganie nazw ulic z geometrii (watek roboczy)
        // ------------------------------------------------------------------

        /// <summary>
        /// Najdluzszy prawie prosty kawalek kazdej nazwanej drogi. Napis wzdluz
        /// zakretu wygladalby zle, a na odcinku krotszym niz tekst i tak sie nie zmiesci.
        /// W obrebie kafla jedna ulica = jeden kandydat (najdluzszy).
        /// </summary>
        public static List<RoadLabel> ExtractRoads(List<OsmWay> ways, double bodyRadius)
        {
            var best = new Dictionary<string, RoadLabel>();
            double mLat = bodyRadius * Geo.DegToRad;
            foreach (OsmWay w in ways)
            {
                if (!w.IsRoad || string.IsNullOrEmpty(w.Name) || w.Points.Count < 2) continue;
                int rank = RoadRank(w.Highway);
                if (rank < 0) continue;
                double mLon = mLat * Math.Cos(w.Points[0].Lat * Geo.DegToRad);

                int start = 0;
                while (start < w.Points.Count - 1)
                {
                    GeoPoint a = w.Points[start];
                    GeoPoint b1 = w.Points[start + 1];
                    double dx0 = (b1.Lon - a.Lon) * mLon, dy0 = (b1.Lat - a.Lat) * mLat;
                    double l0 = Math.Sqrt(dx0 * dx0 + dy0 * dy0);
                    int end = start + 1;
                    if (l0 > 1e-3)
                    {
                        dx0 /= l0; dy0 /= l0;
                        // wydluzaj, dopoki kierunek od poczatku odbiega mniej niz ok. 12 st.
                        while (end + 1 < w.Points.Count)
                        {
                            GeoPoint c = w.Points[end + 1];
                            double dx = (c.Lon - a.Lon) * mLon, dy = (c.Lat - a.Lat) * mLat;
                            double l = Math.Sqrt(dx * dx + dy * dy);
                            if (l < 1e-3 || (dx * dx0 + dy * dy0) / l < 0.978) break;
                            end++;
                        }
                    }
                    GeoPoint e = w.Points[end];
                    double ex = (e.Lon - a.Lon) * mLon, ey = (e.Lat - a.Lat) * mLat;
                    float len = (float)Math.Sqrt(ex * ex + ey * ey);

                    RoadLabel cur;
                    if (!best.TryGetValue(w.Name, out cur) || len > cur.LengthM)
                        best[w.Name] = new RoadLabel
                        {
                            Name = w.Name, Rank = rank, LengthM = len,
                            LatA = a.Lat, LonA = a.Lon, LatB = e.Lat, LonB = e.Lon
                        };
                    start = end;
                }
            }
            return new List<RoadLabel>(best.Values);
        }

        private static int RoadRank(string hw)
        {
            switch (hw)
            {
                case "motorway": case "trunk": return 0;
                case "primary": case "secondary": return 1;
                case "tertiary": case "residential": case "unclassified": case "living_street": return 2;
                case "service": case "pedestrian": return 3;
                default: return hw != null && hw.EndsWith("_link") ? -1 : 3;
            }
        }

        // ------------------------------------------------------------------
        //  Rozmieszczenie (watek glowny, przy zmianie widoku)
        // ------------------------------------------------------------------

        public void Clear()
        {
            placed.Clear();
            taken.Clear();
            seenPlace.Clear();
            seenRoad.Clear();
        }

        /// <summary>Miejsca z kafli. Wywolywac od najwazniejszych - pierwszy wygrywa.</summary>
        public void AddPlaces(MapView view, List<OsmPlace> places, int rank, float width, float height)
        {
            if (places == null) return;
            float mpp = view.MetersPerPixel;
            foreach (OsmPlace p in places)
            {
                if (placed.Count >= MaxLabels) return;
                if (p.Rank != rank || mpp > OsmSettings.PlaceMaxMpp(rank)) continue;
                TryPlace(view.ToPixel(p.Lat, p.Lon), p.Name, rank, width, height);
            }
        }

        /// <summary>Miejsca z places.idx (miasta calego swiata) - widok swiata i kula.</summary>
        public void AddWorldPlaces(MapView view, WorldPlaces.Place[] places, int rank, float width, float height)
        {
            if (places == null) return;
            float mpp = view.MetersPerPixel;
            if (mpp > OsmSettings.PlaceMaxMpp(rank)) return;
            BBox b = view.VisibleBox(0.05);
            foreach (WorldPlaces.Place p in places)
            {
                if (placed.Count >= MaxLabels) return;
                if (p.Rank != rank) continue;
                if (p.Lat < b.South || p.Lat > b.North) continue;
                double dl = p.Lon - view.CenterLon;
                if (dl > 180) dl -= 360; else if (dl < -180) dl += 360;
                if (Math.Abs(dl) > (b.East - b.West) * 0.5) continue;
                TryPlace(view.ToPixel(p.Lat, p.Lon), p.Name, rank, width, height);
            }
        }

        /// <summary>Etykieta w podanym punkcie ekranu - dla kuli w widoku mapy.</summary>
        public bool AddScreen(Vector2 at, string name, int rank, float width, float height)
        {
            if (placed.Count >= MaxLabels) return false;
            return TryPlace(at, name, rank, width, height);
        }

        private bool TryPlace(Vector2 at, string name, int rank, float width, float height)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (at.x < -40 || at.y < -20 || at.x > width + 40 || at.y > height + 20) return false;

            // ta sama nazwa blisko (z sasiednich kafli) - raz
            string key = name + "|" + (int)(at.x / 24) + "|" + (int)(at.y / 24);
            if (seenPlace.Contains(key)) return false;

            int size = PlaceFont(rank);
            bool bold = rank <= 1;
            Vector2 s = Measure(name, size, bold);
            var box = new Rect(at.x - s.x * 0.5f, at.y - s.y * 0.5f, s.x, s.y);
            if (box.x < 0 || box.y < 0 || box.xMax > width || box.yMax > height) return false;
            if (Hits(box, 3f)) return false;

            seenPlace.Add(key);
            taken.Add(box);
            placed.Add(new Placed { Text = name, Box = box, Size = size, Bold = bold });
            return true;
        }

        /// <summary>Nazwy panstw w srodku ich najwiekszego obrysu. Wolac przed
        /// AddPlaces - napis panstwa ma wygrac miejsce nad miastem, ktore akurat
        /// wypadlo w tym samym punkcie przy duzym oddaleniu.</summary>
        public void AddCountries(MapView view, float width, float height)
        {
            if (view.MetersPerPixel < 25f) return;
            foreach (CountryBorders.Country co in CountryBorders.All)
            {
                if (placed.Count >= MaxLabels) return;
                TryPlaceCountry(view.ToPixel(co.CLat, co.CLon), co.Name, width, height);
            }
        }

        private bool TryPlaceCountry(Vector2 at, string name, float width, float height)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (at.x < -60 || at.y < -30 || at.x > width + 60 || at.y > height + 30) return false;

            int size = Mathf.RoundToInt(15 * OsmSettings.LabelScale);
            Vector2 s = Measure(name, size, true);
            var box = new Rect(at.x - s.x * 0.5f, at.y - s.y * 0.5f, s.x, s.y);
            // panstwa moga wystawac poza ekran (napis blisko krawedzi) - to w porzadku,
            // w przeciwienstwie do miast, ktorych centroid zwykle jest w kadrze
            if (Hits(box, 4f)) return false;

            taken.Add(box);
            placed.Add(new Placed { Text = name, Box = box, Size = size, Bold = true });
            return true;
        }

        public void AddRoads(MapView view, List<RoadLabel> roads, float width, float height)
        {
            if (roads == null) return;
            float mpp = view.MetersPerPixel;
            if (mpp > OsmSettings.LabelRoadMpp * 4f) return;

            foreach (RoadLabel r in roads)
            {
                if (placed.Count >= MaxLabels) return;
                // wazniejsze drogi podpisujemy z dalej niz osiedlowe
                float limit = OsmSettings.LabelRoadMpp * (r.Rank == 0 ? 4f : r.Rank == 1 ? 2.5f : r.Rank == 2 ? 1f : 0.5f);
                if (mpp > limit) continue;
                Vector2 a = view.ToPixel(r.LatA, r.LonA), b = view.ToPixel(r.LatB, r.LonB);
                Vector2 d = b - a;
                float len = d.magnitude;
                int size = RoadFont();
                Vector2 s = Measure(r.Name, size, false);
                if (len < s.x + 12f) continue;

                Vector2 mid = (a + b) * 0.5f;
                if (mid.x < 0 || mid.y < 0 || mid.x > width || mid.y > height) continue;

                // Ta sama ulica z sasiednich kafli - raz na okolice. Po samej nazwie
                // odpadalyby wszystkie "Glowne" poza pierwsza wsia na ekranie.
                string key = r.Name + "|" + (int)(mid.x / 220) + "|" + (int)(mid.y / 220);
                if (seenRoad.Contains(key)) continue;

                float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
                if (angle > 90f) angle -= 180f; else if (angle < -90f) angle += 180f;

                var box = new Rect(mid.x - s.x * 0.5f, mid.y - s.y * 0.5f, s.x, s.y);
                Rect aabb = RotatedBounds(box, angle);
                if (aabb.x < 0 || aabb.y < 0 || aabb.xMax > width || aabb.yMax > height) continue;
                if (Hits(aabb, 2f)) continue;

                seenRoad.Add(key);
                taken.Add(aabb);
                placed.Add(new Placed { Text = r.Name, Box = box, Angle = angle, Size = size });
            }
        }

        private bool Hits(Rect r, float pad)
        {
            for (int i = 0; i < taken.Count; i++)
            {
                Rect t = taken[i];
                if (r.x - pad < t.xMax && r.xMax + pad > t.x && r.y - pad < t.yMax && r.yMax + pad > t.y) return true;
            }
            return false;
        }

        private static Rect RotatedBounds(Rect r, float angle)
        {
            float a = Mathf.Abs(angle) * Mathf.Deg2Rad;
            float c = Mathf.Cos(a), s = Mathf.Sin(a);
            float w = r.width * c + r.height * s, h = r.width * s + r.height * c;
            return new Rect(r.center.x - w * 0.5f, r.center.y - h * 0.5f, w, h);
        }

        private static int PlaceFont(int rank)
        {
            int[] px = { 16, 14, 12, 12, 11 };
            return Mathf.RoundToInt(px[Mathf.Clamp(rank, 0, 4)] * OsmSettings.LabelScale);
        }

        private static int RoadFont() { return Mathf.RoundToInt(11 * OsmSettings.LabelScale); }

        private GUIStyle Style(int size, bool bold)
        {
            int key = size * 2 + (bold ? 1 : 0);
            GUIStyle st;
            if (styles.TryGetValue(key, out st)) return st;
            st = new GUIStyle
            {
                fontSize = size,
                fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
                alignment = TextAnchor.MiddleCenter,
                clipping = TextClipping.Overflow,
                wordWrap = false
            };
            styles[key] = st;
            return st;
        }

        private Vector2 Measure(string text, int size, bool bold)
        {
            string key = size + (bold ? "b|" : "|") + text;
            Vector2 v;
            if (sizeCache.TryGetValue(key, out v)) return v;
            if (sizeCache.Count > 4000) sizeCache.Clear();
            v = Style(size, bold).CalcSize(new GUIContent(text));
            v.x += 2f;
            sizeCache[key] = v;
            return v;
        }

        // ------------------------------------------------------------------
        //  Rysowanie (co klatke, w OnGUI przy Repaint, wewnatrz grupy mapy)
        // ------------------------------------------------------------------

        public void Draw()
        {
            bool dark = MapRenderer.DarkLabels;
            Color text = dark ? new Color(0.15f, 0.15f, 0.17f) : new Color(1f, 1f, 1f);
            Color halo = dark ? new Color(1f, 1f, 1f, 0.9f) : new Color(0f, 0f, 0f, 0.85f);

            Matrix4x4 saved = GUI.matrix;
            foreach (Placed p in placed)
            {
                GUIStyle st = Style(p.Size, p.Bold);
                if (p.Angle != 0f) GUIUtility.RotateAroundPivot(p.Angle, p.Box.center);

                st.normal.textColor = halo;
                Rect r = p.Box;
                GUI.Label(new Rect(r.x - 1, r.y, r.width, r.height), p.Text, st);
                GUI.Label(new Rect(r.x + 1, r.y, r.width, r.height), p.Text, st);
                GUI.Label(new Rect(r.x, r.y - 1, r.width, r.height), p.Text, st);
                GUI.Label(new Rect(r.x, r.y + 1, r.width, r.height), p.Text, st);
                st.normal.textColor = text;
                GUI.Label(r, p.Text, st);

                if (p.Angle != 0f) GUI.matrix = saved;
            }
        }
    }

    /// <summary>places.idx wczytywany raz, w tle - plik moze isc z serwera.</summary>
    public static class WorldPlacesStore
    {
        private static WorldPlaces.Place[] places;
        private static bool started;
        public static string Report = "miasta swiata: nie wczytane";

        public static WorldPlaces.Place[] Get()
        {
            if (places != null || started) return places;
            started = true;
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    string path = LocalStore.FindShared(WorldPlaces.FileName);
                    if (path == null) { Report = "miasta swiata: brak places.idx"; return; }
                    WorldPlaces.Place[] p = WorldPlaces.Read(path);
                    places = p;
                    Report = "miasta swiata: " + p.Length;
                }
                catch (Exception e)
                {
                    Report = "miasta swiata: blad " + e.Message;
                }
            }) { IsBackground = true, Name = "OSMRoads.Places" };
            t.Start();
            return null;
        }
    }
}
