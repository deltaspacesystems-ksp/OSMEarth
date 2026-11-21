using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using OSMRoads;

namespace OSMView
{
    /// <summary>
    ///   osmview search                     - zestaw zapytan z oczekiwanymi wynikami
    ///   osmview search "tekst" [lat lon]   - jedno zapytanie, wyniki z punktacja
    ///
    /// Uruchamia PRAWDZIWA wyszukiwarke moda (PlaceSearch) na prawdziwych danych:
    /// places.idx i pl_*.idx z serwera paczek, cities.bin i paczki OSM z ulicami.
    /// </summary>
    internal static class SearchTest
    {
        private const double R = 6371010.0;
        private static int fails;

        private sealed class Case
        {
            public string Query; public double Lat, Lon;
            public string ExpectTitle;      // fragment tytulu pierwszego wyniku (null = tylko wypisz)
            public SearchKind? ExpectKind;
            public string Why;
        }

        public static int Run(string[] args)
        {
            LocalStore.Reload();
            Console.WriteLine(LocalStore.Report.Length > 160 ? LocalStore.Report.Substring(0, 160) + "..." : LocalStore.Report);

            if (args.Length >= 2)
            {
                double lat = args.Length >= 4 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 50.93;
                double lon = args.Length >= 4 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 22.23;
                Show(args[1], lat, lon, null, null, null);
                return 0;
            }

            var cases = new List<Case>
            {
                new Case { Query = "Warsaw", Lat = 52.2, Lon = 21.0, ExpectTitle = "Warsaw", Why = "angielska nazwa duzego miasta (cities.bin)" },
                new Case { Query = "warszawa", Lat = 52.2, Lon = 21.0, ExpectTitle = "Warsaw", ExpectKind = SearchKind.City, Why = "lokalna nazwa -> to samo miasto" },
                new Case { Query = "lodz", Lat = 51.7, Lon = 19.4, ExpectTitle = "Łódź", Why = "bez polskich znakow" },
                new Case { Query = "krasnik", Lat = 50.9, Lon = 22.2, ExpectTitle = "Kraśnik", Why = "miasteczko, bez polskich znakow" },
                new Case { Query = "Kraśnik", Lat = 50.9, Lon = 22.2, ExpectTitle = "Kraśnik", Why = "z polskimi znakami" },
                new Case { Query = "koln", Lat = 50.9, Lon = 6.9, ExpectTitle = null, Why = "Koln/Cologne/Koeln" },
                new Case { Query = "cologne", Lat = 50.9, Lon = 6.9, ExpectTitle = "Cologne", Why = "angielska nazwa" },
                new Case { Query = "new york", Lat = 40.7, Lon = -74.0, ExpectTitle = "New York", Why = "dwa slowa" },
                new Case { Query = "moscow", Lat = 55.7, Lon = 37.6, ExpectTitle = "Moscow", Why = "angielska nazwa, Rosja" },
                new Case { Query = "baikonur", Lat = 45.9, Lon = 63.3, ExpectTitle = "Baikonur", Why = "kosmodrom z listy moda" },
                new Case { Query = "poland", Lat = 50.0, Lon = 20.0, ExpectTitle = "Poland", ExpectKind = SearchKind.Country, Why = "kraj" },
                new Case { Query = "51.2465, 22.5684", Lat = 0, Lon = 0, ExpectTitle = "51.24650", ExpectKind = SearchKind.Coordinates, Why = "wspolrzedne dziesietne" },
                new Case { Query = "N51.2465 E22.5684", Lat = 0, Lon = 0, ExpectTitle = "51.24650", ExpectKind = SearchKind.Coordinates, Why = "z literami polkul" },
                new Case { Query = "51°14'47\"N 22°34'06\"E", Lat = 0, Lon = 0, ExpectTitle = "51.24639", ExpectKind = SearchKind.Coordinates, Why = "stopnie, minuty, sekundy" },
                new Case { Query = "-33.86, 151.21", Lat = 0, Lon = 0, ExpectTitle = "-33.86000", ExpectKind = SearchKind.Coordinates, Why = "polkula poludniowa" },
                new Case { Query = "a", Lat = 50.9, Lon = 22.2, ExpectTitle = "", Why = "jedna litera: brak wynikow" },
                new Case { Query = "zalesie", Lat = 50.9, Lon = 22.2, ExpectTitle = null, Why = "wies / przysiolek w okolicy" },
                new Case { Query = "lubelska", Lat = 50.92, Lon = 22.22, ExpectTitle = null, Why = "ulica w okolicy widoku" },
                new Case { Query = "ul lubelska krasnik", Lat = 50.92, Lon = 22.22, ExpectTitle = "Lubelska", Why = "kilka slow" },
            };

            foreach (Case c in cases) Show(c.Query, c.Lat, c.Lon, c.ExpectTitle, c.ExpectKind, c.Why);

            Console.WriteLine();
            Console.WriteLine(fails == 0 ? "WSZYSTKIE OCZEKIWANIA SPELNIONE" : "NIESPELNIONE: " + fails);
            return fails == 0 ? 0 : 1;
        }

        private static void Show(string q, double lat, double lon, string expectTitle, SearchKind? expectKind, string why)
        {
            var sw = Stopwatch.StartNew();
            List<SearchResult> rs = PlaceSearch.Search(q, lat, lon, R, 8, null);
            double ms = sw.Elapsed.TotalMilliseconds;

            Console.WriteLine();
            Console.WriteLine("\"{0}\"  (widok {1:F1}, {2:F1})  {3}  -> {4} wynikow, {5:F0} ms", q, lat, lon, why ?? "", rs.Count, ms);
            int i = 0;
            foreach (SearchResult r in rs)
            {
                Console.WriteLine("   {0}. {1,-28} {2,-44} {3,8:F4} {4,9:F4}  {5,7:F0}  {6}",
                                  ++i, r.Title, r.Subtitle, r.Lat, r.Lon, r.Score, r.Kind);
                if (i >= 6) break;
            }

            if (expectTitle == null) return;
            bool ok;
            if (expectTitle.Length == 0) ok = rs.Count == 0;
            else
            {
                ok = rs.Count > 0 && rs[0].Title.IndexOf(expectTitle, StringComparison.OrdinalIgnoreCase) >= 0;
                if (ok && expectKind.HasValue) ok = rs[0].Kind == expectKind.Value;
            }
            if (!ok) { fails++; Console.WriteLine("   ^^^ BLAD: oczekiwano na pierwszym miejscu: '" + expectTitle + "'" + (expectKind.HasValue ? " (" + expectKind + ")" : "")); }
        }
    }
}
