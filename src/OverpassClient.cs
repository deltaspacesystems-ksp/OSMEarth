using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace OSMRoads
{
    [Flags]
    public enum OsmLayers
    {
        None = 0,
        Roads = 1,
        Buildings = 2,
        Landuse = 4
    }

    public static class OverpassClient
    {
        // Mozesz podmienic na wlasna instancje Overpassa - patrz README.
        public static string Endpoint = "https://overpass-api.de/api/interpreter";

        // Jedno zapytanie naraz - nie jako "throttling", tylko dlatego, ze rownolegle
        // zapytania do tego samego endpointu i tak konczyly by sie masowym 429.
        private static bool busy;
        private static float busySince;

        /// <summary>
        /// Straznik zawieszonej flagi. Busy jest statyczne, wiec przezywa zmiane sceny,
        /// a korutyna zabita w trakcie zadania nigdy go nie zwolni - i wtedy KAZDE
        /// kolejne zapytanie dostawalo "inne zapytanie w toku" bez konca.
        /// Po BusyTimeout uznajemy flage za osierocona.
        /// </summary>
        private const float BusyTimeout = 200f;

        public static bool Busy
        {
            get
            {
                if (busy && Time.realtimeSinceStartup - busySince > BusyTimeout)
                {
                    Debug.LogWarning("[OSMRoads] osierocona flaga Busy - zwalniam");
                    busy = false;
                }
                return busy;
            }
        }

        /// <summary>Wolane przy wejsciu w scene lotu: kasuje stan po przerwanej sesji.</summary>
        public static void ResetBusy()
        {
            busy = false;
        }

        /// <summary>Czy ostatnie Fetch faktycznie poszlo do sieci (false = cache).
        /// Streaming uzywa tego, zeby odstep miedzy zapytaniami dotyczyl tylko
        /// prawdziwych zapytan - kafle z cache moga leciec bez czekania.</summary>
        public static bool LastFetchHitNetwork { get; private set; }

        /// <summary>Licznik zapytan sieciowych w tej sesji - twardy bezpiecznik
        /// przed zalaniem publicznego Overpassa przez automatyczne pobieranie.</summary>
        public static int NetworkRequestCount { get; private set; }

        private const string UserAgent = "KSP-OSMRoads/0.2 (KSP mod, single-user, cached)";

        public static string CacheDir
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/OSMRoads/Cache"); }
        }

        private static string CachePath(BBox b, OsmLayers layers)
        {
            string key = b.ToString().Replace(',', '_').Replace('-', 'm').Replace('.', 'p');
            string tag = ((layers & OsmLayers.Roads) != 0 ? "r" : "")
                       + ((layers & OsmLayers.Buildings) != 0 ? "b" : "")
                       + ((layers & OsmLayers.Landuse) != 0 ? "l" : "");
            return Path.Combine(CacheDir, "osm_" + tag + "_" + key + ".osm");
        }

        /// <summary>Czy kafel jest juz na dysku - streaming pyta o to, zeby nie
        /// odczekiwac odstepu fair-use przed czyms, co i tak nie pojdzie do sieci.</summary>
        public static bool HasCached(BBox b, OsmLayers layers)
        {
            return File.Exists(CachePath(b, layers));
        }

        public static string BuildQuery(BBox b, OsmLayers layers)
        {
            // "out geom;" wklada lat/lon prosto w wezly <nd>, wiec nie musimy
            // budowac tablicy node id -> wspolrzedne.
            string box = b.ToString();
            var sb = new StringBuilder();
            sb.Append("[out:xml][timeout:120];(");

            if ((layers & OsmLayers.Roads) != 0)
            {
                sb.Append("way[\"highway\"~\"^(motorway|trunk|primary|secondary|tertiary|residential|unclassified)(_link)?$\"](");
                sb.Append(box);
                sb.Append(");");
            }
            if ((layers & OsmLayers.Buildings) != 0)
            {
                // Relacje (budynki z dziedzincami) swiadomie pomijamy w v0.2.
                sb.Append("way[\"building\"](");
                sb.Append(box);
                sb.Append(");");
            }

            if ((layers & OsmLayers.Landuse) != 0)
            {
                sb.Append("way[\"landuse\"](").Append(box).Append(");");
                sb.Append("way[\"natural\"~\"^(wood|scrub|grassland|heath|sand|beach)$\"](").Append(box).Append(");");
                sb.Append("way[\"leisure\"~\"^(park|garden|golf_course|pitch|playground)$\"](").Append(box).Append(");");
            }

            sb.Append(");out geom;");
            return sb.ToString();
        }

        /// <summary>
        /// Odstepy przed kolejnymi probami. Publiczny Overpass regularnie zwraca
        /// 504/429, gdy jest obciazony - to stan przejsciowy, nie blad zapytania.
        /// Mirrory sprawdzone 2026-08-31: kumi.systems nie rozwiazuje sie w DNS,
        /// private.coffee daje 502, osm.jp ma wygasly certyfikat. Zostaje retry.
        /// </summary>
        private static readonly int[] BackoffSeconds = { 0, 3, 8, 20 };

        private static bool IsRetryable(long httpCode)
        {
            return httpCode == 0 || httpCode == 429 || httpCode == 502
                || httpCode == 503 || httpCode == 504;
        }

        /// <summary>Zwraca surowy XML przez callback: onDone(xml, null) = sukces,
        /// onDone(null, blad) = porazka. Najpierw cache, potem siec.</summary>
        public static IEnumerator Fetch(BBox box, OsmLayers layers,
                                        Action<string, string> onDone,
                                        Action<string> onProgress = null)
        {
            if (layers == OsmLayers.None)
            {
                onDone(null, "Nie wybrano zadnej warstwy.");
                yield break;
            }

            string path = CachePath(box, layers);

            if (File.Exists(path))
            {
                string cached = null, err = null;
                try { cached = File.ReadAllText(path); }
                catch (Exception e) { err = "Cache read failed: " + e.Message; }
                if (cached != null) { LastFetchHitNetwork = false; onDone(cached, null); yield break; }
                Debug.LogWarning("[OSMRoads] " + err);
            }

            if (Busy)
            {
                onDone(null, "Inne zapytanie jest w toku (fair-use: jedno naraz).");
                yield break;
            }

            busy = true;
            busySince = Time.realtimeSinceStartup;
            LastFetchHitNetwork = true;
            NetworkRequestCount++;
            string query = BuildQuery(box, layers);

            Debug.Log("[OSMRoads] POST " + Endpoint + "\n[OSMRoads] query: " + query);

            string text = null;
            string lastError = null;

            for (int attempt = 0; attempt < BackoffSeconds.Length; attempt++)
            {
                if (BackoffSeconds[attempt] > 0)
                {
                    if (onProgress != null)
                        onProgress(string.Format("{0} - czekam {1}s, proba {2}/{3} ...",
                                                 lastError, BackoffSeconds[attempt],
                                                 attempt + 1, BackoffSeconds.Length));
                    yield return new WaitForSeconds(BackoffSeconds[attempt]);
                }

                if (onProgress != null)
                    onProgress(string.Format("Pobieram (proba {0}/{1}) ...",
                                             attempt + 1, BackoffSeconds.Length));

                var form = new Dictionary<string, string> { { "data", query } };
                using (UnityWebRequest req = UnityWebRequest.Post(Endpoint, form))
                {
                    req.SetRequestHeader("User-Agent", UserAgent);
                    req.timeout = 150;

                    yield return req.SendWebRequest();

                    string body = req.downloadHandler != null ? req.downloadHandler.text : null;

                    if (req.isNetworkError || req.isHttpError)
                    {
                        lastError = "HTTP " + req.responseCode + " (" + req.error + ")";
                        Debug.LogWarning("[OSMRoads] proba " + (attempt + 1) + ": " + lastError
                                         + "\n[OSMRoads] odpowiedz: " + Head(body));

                        if (IsRetryable(req.responseCode)) continue;

                        busy = false;
                        onDone(null, "Overpass: " + lastError);
                        yield break;
                    }

                    text = body;
                    Debug.Log("[OSMRoads] HTTP " + req.responseCode + ", "
                              + (text == null ? 0 : text.Length) + " znakow");
                    break;
                }
            }

            busy = false;

            if (text == null)
            {
                onDone(null, "Overpass nie odpowiedzial po " + BackoffSeconds.Length
                             + " probach. Ostatnio: " + lastError);
                yield break;
            }

            if (text.IndexOf("<osm", StringComparison.Ordinal) < 0)
            {
                Debug.LogError("[OSMRoads] to nie jest OSM XML:\n" + Head(text));
                onDone(null, "Overpass zwrocil cos, co nie wyglada na OSM XML. Szczegoly w KSP.log.");
                yield break;
            }

            // Overpass sygnalizuje timeout/przeciazenie <remark> WEWNATRZ poprawnego
            // XML-a. Bez tego testu wyszloby "zero obiektow" zamiast prawdziwej przyczyny.
            int rk = text.IndexOf("<remark>", StringComparison.Ordinal);
            if (rk >= 0)
            {
                int end = text.IndexOf("</remark>", rk, StringComparison.Ordinal);
                string remark = end > rk
                    ? text.Substring(rk + 8, end - rk - 8)
                    : "(nieczytelny)";
                Debug.LogError("[OSMRoads] Overpass remark: " + remark);
                onDone(null, "Overpass: " + remark);
                yield break;
            }

            try
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(path, text);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[OSMRoads] Nie udalo sie zapisac cache: " + e.Message);
            }

            onDone(text, null);
        }

        /// <summary>Poczatek odpowiedzi do loga - zeby nie wrzucac 4 MB XML-a.</summary>
        private static string Head(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(pusta)";
            return s.Length <= 400 ? s : s.Substring(0, 400) + " ...";
        }
    }
}
