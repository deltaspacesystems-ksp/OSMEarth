using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace OSMRoads
{
    public enum SearchKind { Coordinates, Country, City, Town, Suburb, Village, Hamlet, Street, LaunchSite }

    public sealed class SearchResult
    {
        public string Title, Subtitle;
        public double Lat, Lon;
        public SearchKind Kind;
        public float Mpp;            // sugerowana skala mapy po wybraniu wyniku (m/px)
        public double Score;
    }

    /// <summary>
    /// Wyszukiwarka miejsc: jedno pole, wiele zrodel, jeden ranking.
    ///
    ///   1. wspolrzedne ("51.2465, 22.5684", "N51.2465 E22.5684", "51°14'47"N 22°34'06"E"),
    ///   2. kosmodromy z listy moda,
    ///   3. kraje (Natural Earth, nazwy angielskie),
    ///   4. duze miasta z angielskimi nazwami (cities.bin, Natural Earth),
    ///   5. miasta i miasteczka calego swiata z OSM w lokalnym jezyku (places.idx),
    ///   6. wszystkie miejsca (takze wsie, przysiolki, dzielnice) kwadratow 10x10 st. wokol
    ///      widoku (pl_*.idx z serwera paczek),
    ///   7. ulice w okolicy widoku - czytane prosto z paczek OSM.
    ///
    /// Wolac z watku roboczego: doczytywanie plikow moze isc siecia. Wszystko poza
    /// listami z pamieci (kosmodromy, kraje) jest cache'owane, wiec kolejne zapytania
    /// z tego samego miejsca sa szybkie.
    /// </summary>
    public static class PlaceSearch
    {
        // ------------------------------------------------------------------
        //  Dane
        // ------------------------------------------------------------------

        private sealed class CityEn
        {
            public string Name, Country;
            public double Lat, Lon;
            public uint Pop;
            public bool Capital;
            public string Fold;           // " " + nazwa i nazwy alternatywne, znormalizowane
        }

        private sealed class Square
        {
            public WorldPlaces.Place[] Places;
            public string[] Fold;         // " " + znormalizowana nazwa, rownolegle do Places
            public float Loaded;
        }

        private static readonly object gate = new object();
        private static CityEn[] cities;
        private static WorldPlaces.Place[] worldRef;
        private static string[] worldFold;
        private static readonly Dictionary<string, Square> squares = new Dictionary<string, Square>();
        private static string[] countryFold;
        private static float[] countryMpp;
        private static string[] siteFold;

        private static string CitiesPath
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/OSMRoads/cities.bin"); }
        }

        public static string Report = "";

        // ------------------------------------------------------------------
        //  Wejscie
        // ------------------------------------------------------------------

        public static List<SearchResult> Search(string query, double viewLat, double viewLon, double bodyRadius,
                                                int max, Func<bool> cancelled)
        {
            var results = new List<SearchResult>();
            if (query == null) return results;
            query = query.Trim();
            if (query.Length == 0) return results;
            if (cancelled == null) cancelled = () => false;

            // --- 1. wspolrzedne: odpowiadaja natychmiast i sa zawsze na gorze ---
            double clat, clon;
            if (ParseCoordinates(query, out clat, out clon))
            {
                results.Add(new SearchResult
                {
                    Title = string.Format(CultureInfo.InvariantCulture, "{0:F5}, {1:F5}", clat, clon),
                    Subtitle = "Coordinates",
                    Lat = clat, Lon = clon, Kind = SearchKind.Coordinates, Mpp = 5f, Score = 10000
                });
                return results;
            }

            string full = TextFold.Fold(query);
            string[] qw = TextFold.Words(full);
            if (qw.Length == 0 || full.Length < 2) return results;

            // "ul. Lubelska" - skroty typu ulicy nie wystepuja w nazwach OSM, pomijamy je
            if (qw.Length > 1)
            {
                var keep = new List<string>();
                foreach (string w in qw)
                    if (w != "ul" && w != "ulica" && w != "al" && w != "aleja" && w != "pl" && w != "os") keep.Add(w);
                if (keep.Count > 0 && keep.Count < qw.Length) { qw = keep.ToArray(); full = string.Join(" ", qw); }
            }

            var qsp = new string[qw.Length];
            for (int i = 0; i < qw.Length; i++) qsp[i] = " " + qw[i];

            SearchSites(qsp, full, results);
            SearchCountries(qsp, full, results);
            if (cancelled()) return Finish(results, max);

            SearchCities(qsp, full, viewLat, viewLon, bodyRadius, results);
            if (cancelled()) return Finish(results, max);

            SearchWorld(qsp, full, viewLat, viewLon, bodyRadius, results, cancelled);
            if (cancelled()) return Finish(results, max);

            SearchSquares(qsp, full, viewLat, viewLon, bodyRadius, results, cancelled);
            if (cancelled()) return Finish(results, max);

            if (full.Length >= 3) SearchStreets(qsp, full, viewLat, viewLon, bodyRadius, results, cancelled);

            return Finish(results, max);
        }

        // ------------------------------------------------------------------
        //  Dopasowanie
        // ------------------------------------------------------------------

        /// <summary>0 = brak dopasowania. Kazde slowo zapytania musi byc poczatkiem jakiegos slowa
        /// nazwy; pelny prefiks i dokladna zgodnosc dostaja premie.</summary>
        private static double Match(string[] qsp, string full, string fold)
        {
            for (int i = 0; i < qsp.Length; i++)
                if (fold.IndexOf(qsp[i], StringComparison.Ordinal) < 0) return 0.0;

            double sc = 1.0;
            if (fold.Length - 1 == full.Length && string.CompareOrdinal(fold, 1, full, 0, full.Length) == 0) sc += 2.0;
            else if (fold.Length - 1 > full.Length && string.CompareOrdinal(fold, 1, full, 0, full.Length) == 0) sc += 1.0;
            return sc;
        }

        private static double Proximity(double lat, double lon, double viewLat, double viewLon, double radius, double scaleKm)
        {
            double d = LaunchSites.GreatCircle(viewLat, viewLon, lat, lon, radius) / 1000.0;
            return Math.Exp(-d / scaleKm);
        }

        // ------------------------------------------------------------------
        //  Zrodla
        // ------------------------------------------------------------------

        private static void SearchSites(string[] qsp, string full, List<SearchResult> into)
        {
            if (siteFold == null)
            {
                var f = new string[LaunchSites.All.Length];
                for (int i = 0; i < f.Length; i++)
                    f[i] = " " + TextFold.Fold(LaunchSites.All[i].Name + " " + LaunchSites.All[i].Operator + " launch site cosmodrome spaceport");
                siteFold = f;
            }
            for (int i = 0; i < siteFold.Length; i++)
            {
                double m = Match(qsp, full, siteFold[i]);
                if (m <= 0) continue;
                LaunchSite ls = LaunchSites.All[i];
                into.Add(new SearchResult
                {
                    Title = ls.Name, Subtitle = "Launch site - " + ls.Operator,
                    Lat = ls.Lat, Lon = ls.Lon, Kind = SearchKind.LaunchSite, Mpp = 4f,
                    Score = 200 + m * 50
                });
            }
        }

        private static void SearchCountries(string[] qsp, string full, List<SearchResult> into)
        {
            CountryBorders.Country[] all = CountryBorders.All;
            if (all.Length == 0) return;
            if (countryFold == null || countryFold.Length != all.Length)
            {
                var f = new string[all.Length];
                var z = new float[all.Length];
                for (int i = 0; i < all.Length; i++)
                {
                    f[i] = " " + TextFold.Fold(all[i].Name);
                    double s = 90, n = -90, w = 180, e = -180;
                    foreach (GeoPoint[] ring in all[i].Rings)
                        foreach (GeoPoint p in ring)
                        {
                            if (p.Lat < s) s = p.Lat; if (p.Lat > n) n = p.Lat;
                            if (p.Lon < w) w = p.Lon; if (p.Lon > e) e = p.Lon;
                        }
                    double spanLat = Math.Max(0.05, n - s), spanLon = Math.Max(0.05, e - w);
                    if (spanLon > 200) spanLon = 60;            // kraj przez poludnik 180 st. - nie liczymy rozpietosci sztucznie
                    double mpp = Math.Max(spanLat * 111320.0 / 480.0,
                                          spanLon * 111320.0 * Math.Cos(all[i].CLat * Math.PI / 180.0) / 700.0) * 1.2;
                    z[i] = (float)Math.Max(800.0, Math.Min(60000.0, mpp));
                }
                countryFold = f; countryMpp = z;
            }
            for (int i = 0; i < all.Length; i++)
            {
                double m = Match(qsp, full, countryFold[i]);
                if (m <= 0) continue;
                into.Add(new SearchResult
                {
                    Title = all[i].Name, Subtitle = "Country",
                    Lat = all[i].CLat, Lon = all[i].CLon, Kind = SearchKind.Country, Mpp = countryMpp[i],
                    Score = 300 + m * 50
                });
            }
        }

        private static void EnsureCities()
        {
            if (cities != null) return;
            var list = new List<CityEn>();
            try
            {
                if (File.Exists(CitiesPath))
                    using (var r = new BinaryReader(File.OpenRead(CitiesPath), Encoding.UTF8))
                    {
                        if (r.ReadUInt32() != 0x5443534F) throw new IOException("zly naglowek cities.bin");   // "OSCT"
                        r.ReadInt32();
                        int n = r.ReadInt32();
                        for (int i = 0; i < n; i++)
                        {
                            var c = new CityEn();
                            c.Lat = r.ReadSingle(); c.Lon = r.ReadSingle();
                            c.Pop = r.ReadUInt32(); c.Capital = r.ReadByte() != 0;
                            c.Name = Encoding.UTF8.GetString(r.ReadBytes(r.ReadByte()));
                            c.Country = Encoding.UTF8.GetString(r.ReadBytes(r.ReadByte()));
                            string alt = Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));
                            c.Fold = " " + TextFold.Fold(c.Name) + (alt.Length > 0 ? " " + TextFold.Fold(alt.Replace('|', ' ')) : "");
                            list.Add(c);
                        }
                    }
            }
            catch (Exception e) { Debug.LogWarning("[OSMRoads] cities.bin: " + e.Message); }
            cities = list.ToArray();
        }

        private static void SearchCities(string[] qsp, string full, double viewLat, double viewLon, double radius,
                                         List<SearchResult> into)
        {
            lock (gate) EnsureCities();
            foreach (CityEn c in cities)
            {
                double m = Match(qsp, full, c.Fold);
                if (m <= 0) continue;
                into.Add(new SearchResult
                {
                    Title = c.Name,
                    Subtitle = (c.Capital ? "Capital - " : "City - ") + c.Country + (c.Pop > 0 ? ", pop. " + c.Pop.ToString("N0", CultureInfo.InvariantCulture) : ""),
                    Lat = c.Lat, Lon = c.Lon, Kind = SearchKind.City, Mpp = c.Pop > 500000 ? 60f : 30f,
                    Score = 150 + m * 60 + Math.Log10(c.Pop + 1.0) * 15.0 + (c.Capital ? 20 : 0)
                                + 30 * Proximity(c.Lat, c.Lon, viewLat, viewLon, radius, 500.0)
                });
            }
        }

        private static float RankBase(int rank)
        {
            switch (rank)
            {
                case 0: return 120f;      // city
                case 1: return 100f;      // town
                case 2: return 60f;       // suburb
                case 3: return 70f;       // village
                default: return 50f;      // hamlet
            }
        }

        private static SearchKind KindOf(int rank)
        {
            switch (rank)
            {
                case 0: return SearchKind.City;
                case 1: return SearchKind.Town;
                case 2: return SearchKind.Suburb;
                case 3: return SearchKind.Village;
                default: return SearchKind.Hamlet;
            }
        }

        private static float MppOf(int rank)
        {
            switch (rank)
            {
                case 0: return 40f;
                case 1: return 20f;
                case 2: return 10f;
                case 3: return 8f;
                default: return 4f;
            }
        }

        private static string KindName(int rank)
        {
            switch (rank)
            {
                case 0: return "City";
                case 1: return "Town";
                case 2: return "Suburb";
                case 3: return "Village";
                default: return "Hamlet";
            }
        }

        private static void ScanPlaces(WorldPlaces.Place[] places, string[] fold, string[] qsp, string full,
                                       double viewLat, double viewLon, double radius,
                                       List<SearchResult> into, Func<bool> cancelled)
        {
            for (int i = 0; i < places.Length; i++)
            {
                if ((i & 0x3FFF) == 0 && cancelled()) return;
                if (fold[i].IndexOf(qsp[0], StringComparison.Ordinal) < 0) continue;     // tanie odsianie
                double m = Match(qsp, full, fold[i]);
                if (m <= 0) continue;

                WorldPlaces.Place p = places[i];
                into.Add(new SearchResult
                {
                    Title = p.Name, Subtitle = KindName(p.Rank),
                    Lat = p.Lat, Lon = p.Lon, Kind = KindOf(p.Rank), Mpp = MppOf(p.Rank),
                    Score = RankBase(p.Rank) + m * 60 + 40 * Proximity(p.Lat, p.Lon, viewLat, viewLon, radius, 60.0)
                });
            }
        }

        private static void SearchWorld(string[] qsp, string full, double viewLat, double viewLon, double radius,
                                        List<SearchResult> into, Func<bool> cancelled)
        {
            WorldPlaces.Place[] world = WorldPlacesStore.GetWait(4000);
            if (world == null) return;

            string[] fold;
            lock (gate)
            {
                if (!ReferenceEquals(worldRef, world))
                {
                    var f = new string[world.Length];
                    for (int i = 0; i < f.Length; i++) f[i] = " " + TextFold.Fold(world[i].Name);
                    worldFold = f; worldRef = world;
                }
                fold = worldFold;
            }
            ScanPlaces(world, fold, qsp, full, viewLat, viewLon, radius, into, cancelled);
        }

        // ------------------------------------------------------------------
        //  Kwadraty 10x10 st. (pl_<kwadrat>.idx)
        // ------------------------------------------------------------------

        public static string SquareName(double lat, double lon)
        {
            int la = (int)Math.Floor(lat / 10.0) * 10, lo = (int)Math.Floor(lon / 10.0) * 10;
            return string.Format(CultureInfo.InvariantCulture, "{0}{1:00}{2}{3:000}",
                                 la >= 0 ? "n" : "s", Math.Abs(la), lo >= 0 ? "e" : "w", Math.Abs(lo));
        }

        private static Square LoadSquare(string name, Func<bool> cancelled)
        {
            lock (gate)
            {
                Square s;
                if (squares.TryGetValue(name, out s) && (s.Places.Length > 0 || Time.realtimeSinceStartup - s.Loaded < 90f)) return s;
            }

            var sq = new Square { Places = new WorldPlaces.Place[0], Fold = new string[0], Loaded = Time.realtimeSinceStartup };
            try
            {
                string path = LocalStore.FindShared("pl_" + name + ".idx");
                if (path != null && !cancelled())
                {
                    WorldPlaces.Place[] p = WorldPlaces.Read(path);
                    var f = new string[p.Length];
                    for (int i = 0; i < f.Length; i++) f[i] = " " + TextFold.Fold(p[i].Name);
                    sq.Places = p; sq.Fold = f;
                }
            }
            catch (Exception e) { Debug.LogWarning("[OSMRoads] pl_" + name + ": " + e.Message); }

            lock (gate)
            {
                if (squares.Count >= 12)
                {
                    string oldest = null; float t = float.MaxValue;
                    foreach (KeyValuePair<string, Square> kv in squares)
                        if (kv.Value.Loaded < t) { t = kv.Value.Loaded; oldest = kv.Key; }
                    if (oldest != null) squares.Remove(oldest);
                }
                squares[name] = sq;
            }
            return sq;
        }

        private static void SearchSquares(string[] qsp, string full, double viewLat, double viewLon, double radius,
                                          List<SearchResult> into, Func<bool> cancelled)
        {
            // Kwadrat widoku i sasiedzi - rzadko szukasz wsi po drugiej stronie kontynentu.
            var seen = new HashSet<string>();
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (cancelled()) return;
                    double la = viewLat + dy * 10.0, lo = viewLon + dx * 10.0;
                    if (la < -89.9 || la > 89.9) continue;
                    if (lo >= 180.0) lo -= 360.0; else if (lo < -180.0) lo += 360.0;
                    string name = SquareName(la, lo);
                    if (!seen.Add(name)) continue;

                    Square s = LoadSquare(name, cancelled);
                    ScanPlaces(s.Places, s.Fold, qsp, full, viewLat, viewLon, radius, into, cancelled);
                }
        }

        // ------------------------------------------------------------------
        //  Ulice w okolicy widoku
        // ------------------------------------------------------------------

        private static double ClassBonus(string hw)
        {
            switch (hw)
            {
                case "motorway": case "trunk": return 25;
                case "primary": return 20;
                case "secondary": return 15;
                case "tertiary": return 10;
                case "residential": case "unclassified": return 6;
                default: return 0;
            }
        }

        private static void SearchStreets(string[] qsp, string full, double viewLat, double viewLon, double radius,
                                          List<SearchResult> into, Func<bool> cancelled)
        {
            if (!LocalStore.Available) return;

            double half = 0.10;
            BBox box;
            box.South = Geo.Clamp(viewLat - half, -89.9, 89.9);
            box.North = Geo.Clamp(viewLat + half, -89.9, 89.9);
            double lonHalf = half / Math.Max(0.2, Math.Cos(viewLat * Math.PI / 180.0));
            box.West = viewLon - lonHalf; box.East = viewLon + lonHalf;

            List<OsmWay> ways;
            try { ways = LocalStore.Read(box); }
            catch (Exception e) { Debug.LogWarning("[OSMRoads] ulice: " + e.Message); return; }

            var best = new Dictionary<string, OsmWay>();
            foreach (OsmWay w in ways)
            {
                if (!w.IsRoad || string.IsNullOrEmpty(w.Name) || w.Points.Count < 2) continue;
                string f = " " + TextFold.Fold(w.Name);
                bool any = false;
                for (int i = 0; i < qsp.Length && !any; i++) any = f.IndexOf(qsp[i], StringComparison.Ordinal) >= 0;
                if (!any) continue;
                OsmWay cur;
                if (!best.TryGetValue(f, out cur) || w.Points.Count > cur.Points.Count) best[f] = w;
                if (cancelled()) return;
            }

            // najblizsze miejsce do podpisu ("ulica w ...")
            foreach (KeyValuePair<string, OsmWay> kv in best)
            {
                OsmWay w = kv.Value;
                GeoPoint mid = w.Points[w.Points.Count / 2];
                string near = null;
                double m = Match(qsp, full, kv.Key);
                if (m <= 0 && qsp.Length > 1)
                {
                    // "lubelska krasnik" - reszta slow moze byc nazwa miejscowosci
                    near = NearestPlaceName(mid.Lat, mid.Lon, radius);
                    if (near != null && Match(qsp, full, kv.Key + " " + TextFold.Fold(near)) > 0) m = 0.8;
                }
                if (m <= 0) continue;
                if (near == null) near = NearestPlaceName(mid.Lat, mid.Lon, radius);
                into.Add(new SearchResult
                {
                    Title = w.Name,
                    Subtitle = "Street" + (near != null ? " near " + near : ""),
                    Lat = mid.Lat, Lon = mid.Lon, Kind = SearchKind.Street, Mpp = 3f,
                    Score = 40 + ClassBonus(w.Highway) + m * 60 + 60 * Proximity(mid.Lat, mid.Lon, viewLat, viewLon, radius, 8.0)
                });
            }
        }

        private static string NearestPlaceName(double lat, double lon, double radius)
        {
            Square s;
            lock (gate)
            {
                if (!squares.TryGetValue(SquareName(lat, lon), out s)) return null;
            }
            string best = null; double bestD = 8000.0;
            foreach (WorldPlaces.Place p in s.Places)
            {
                if (Math.Abs(p.Lat - lat) > 0.1 || Math.Abs(p.Lon - lon) > 0.16) continue;
                double d = LaunchSites.GreatCircle(lat, lon, p.Lat, p.Lon, radius);
                if (d < bestD) { bestD = d; best = p.Name; }
            }
            return best;
        }

        // ------------------------------------------------------------------
        //  Wynik koncowy
        // ------------------------------------------------------------------

        private static List<SearchResult> Finish(List<SearchResult> all, int max)
        {
            all.Sort((a, b) => b.Score.CompareTo(a.Score));

            // To samo miasto z dwoch zrodel (angielska nazwa + lokalna) - zostaje wyzej punktowane.
            var kept = new List<SearchResult>();
            foreach (SearchResult r in all)
            {
                bool dup = false;
                if (r.Kind == SearchKind.City || r.Kind == SearchKind.Town)
                    foreach (SearchResult k in kept)
                    {
                        if (k.Kind != SearchKind.City && k.Kind != SearchKind.Town) continue;
                        if (Math.Abs(k.Lat - r.Lat) < 0.08 && Math.Abs(k.Lon - r.Lon) < 0.12) { dup = true; break; }
                    }
                if (!dup && !SameNameNear(kept, r)) kept.Add(r);
                if (kept.Count >= max) break;
            }
            return kept;
        }

        private static bool SameNameNear(List<SearchResult> kept, SearchResult r)
        {
            foreach (SearchResult k in kept)
                if (k.Kind == r.Kind && k.Title == r.Title &&
                    Math.Abs(k.Lat - r.Lat) < 0.05 && Math.Abs(k.Lon - r.Lon) < 0.08) return true;
            return false;
        }

        // ------------------------------------------------------------------
        //  Wspolrzedne
        // ------------------------------------------------------------------

        private static readonly Regex DecRx = new Regex(
            @"^\s*([NSns])?\s*(-?\d{1,2}(?:[.,]\d+)?)\s*°?\s*([NSns])?\s*[,;\s]\s*([EWewOo])?\s*(-?\d{1,3}(?:[.,]\d+)?)\s*°?\s*([EWewOo])?\s*$",
            RegexOptions.Compiled);

        private static readonly Regex DmsRx = new Regex(
            @"(\d{1,3})\s*[°d]\s*(\d{1,2})\s*['′m]\s*(\d{1,2}(?:[.,]\d+)?)\s*[""″s]?\s*([NSEWnsew])",
            RegexOptions.Compiled);

        /// <summary>Rozpoznaje wspolrzedne w kilku zapisach; false, gdy to nie sa wspolrzedne.</summary>
        public static bool ParseCoordinates(string text, out double lat, out double lon)
        {
            lat = lon = 0;
            if (string.IsNullOrEmpty(text)) return false;

            MatchCollection dms = DmsRx.Matches(text);
            if (dms.Count == 2)
            {
                double a, b; char ca, cb;
                Dms(dms[0], out a, out ca); Dms(dms[1], out b, out cb);
                bool aLat = "NSns".IndexOf(ca) >= 0, bLat = "NSns".IndexOf(cb) >= 0;
                if (aLat == bLat) return false;
                lat = aLat ? a : b; lon = aLat ? b : a;
                return InRange(lat, lon);
            }

            Match m = DecRx.Match(text);
            if (!m.Success) return false;

            double v1, v2;
            if (!double.TryParse(m.Groups[2].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v1)) return false;
            if (!double.TryParse(m.Groups[5].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v2)) return false;

            string h1 = m.Groups[1].Value + m.Groups[3].Value, h2 = m.Groups[4].Value + m.Groups[6].Value;
            if (h1.Length > 0 && "Ss".IndexOf(h1[0]) >= 0) v1 = -Math.Abs(v1);
            if (h2.Length > 0 && "Ww".IndexOf(h2[0]) >= 0) v2 = -Math.Abs(v2);
            lat = v1; lon = v2;
            return InRange(lat, lon);
        }

        private static void Dms(Match m, out double deg, out char hemi)
        {
            double d = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            double mi = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            double s = double.Parse(m.Groups[3].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            hemi = m.Groups[4].Value[0];
            deg = d + mi / 60.0 + s / 3600.0;
            if ("SsWw".IndexOf(hemi) >= 0) deg = -deg;
        }

        private static bool InRange(double lat, double lon) { return Math.Abs(lat) <= 90.0 && Math.Abs(lon) <= 180.0; }
    }
}
