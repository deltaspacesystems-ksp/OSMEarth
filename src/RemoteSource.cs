using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Paczki czytane przez HTTP(S) - z wlasnego serwera za Cloudflare.
    ///
    /// W datadirs.txt linia:
    ///     https://deltatechksp.eu/packs/  KLUCZ  [cache=D:\osmcache] [limit=4]
    ///
    /// Z sieci idzie tylko to, czego potrzeba:
    ///   - packs.tsv (zasiegi paczek) - raz na sesje,
    ///   - .idx kwadratu - raz, potem z dysku; ponownie tylko gdy serwer ma nowszy,
    ///   - z .bin wylacznie potrzebne komorki, zapytaniem Range.
    /// Wszystko pobrane zostaje w pamieci podrecznej na dysku, wiec drugi lot nad
    /// tym samym miejscem nie dotyka sieci wcale - takze bez internetu.
    ///
    /// Klucz idzie w naglowku X-OSM-Key i sprawdza go nginx. Dzieki temu pliki
    /// nie sa dostepne dla przypadkowych botow, a klucz przyjaciela da sie cofnac
    /// jedna linijka w konfiguracji, bez ruszania innych.
    /// </summary>
    internal sealed class RemoteSource
    {
        public readonly string BaseUrl;
        public readonly string Key;
        public readonly string CacheDir;
        public readonly long LimitBytes;

        public long BytesDownloaded, Requests, CacheHits;

        static RemoteSource()
        {
            // Mono w KSP domyslnie potrafi wystartowac z TLS 1.0 - Cloudflare go nie przyjmie.
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            ServicePointManager.Expect100Continue = false;
            if (ServicePointManager.DefaultConnectionLimit < 8) ServicePointManager.DefaultConnectionLimit = 8;
        }

        private RemoteSource(string url, string key, string cache, long limit)
        {
            BaseUrl = url.EndsWith("/") ? url : url + "/";
            Key = key;
            CacheDir = cache;
            LimitBytes = limit;
        }

        public static bool IsRemote(string line)
        {
            return line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   line.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>"url [klucz] [cache=katalog] [limit=GB]"</summary>
        public static RemoteSource Parse(string line)
        {
            string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string url = parts[0], key = null, cache = null;
            double limitGb = 3;

            for (int i = 1; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p.StartsWith("cache=", StringComparison.OrdinalIgnoreCase)) cache = p.Substring(6);
                else if (p.StartsWith("limit=", StringComparison.OrdinalIgnoreCase))
                    double.TryParse(p.Substring(6), System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out limitGb);
                else if (key == null) key = p;
            }

            if (cache == null)
            {
                var uri = new Uri(url);
                cache = Path.Combine(KSPUtil.ApplicationRootPath,
                                     "GameData/OSMRoads/Cache/" + uri.Host + "_" + uri.Port);
            }
            Directory.CreateDirectory(cache);
            return new RemoteSource(url, key, cache, (long)(limitGb * 1073741824.0));
        }

        public string CachePath(string file) { return Path.Combine(CacheDir, file); }

        // ------------------------------------------------------------------
        //  Pliki calosciowe: packs.tsv, .idx
        // ------------------------------------------------------------------

        /// <summary>
        /// Odswieza plik w pamieci podrecznej. Zwraca: 1 = pobrany nowy,
        /// 0 = na serwerze bez zmian (albo brak sieci, a kopia jest), -1 = brak pliku.
        /// Z naglowkiem If-Modified-Since, wiec niezmieniony indeks kosztuje
        /// jedno krotkie zapytanie, a nie megabajty.
        /// </summary>
        public int Refresh(string file)
        {
            string local = CachePath(file);
            bool have = File.Exists(local);

            try
            {
                HttpWebRequest req = NewRequest(file);
                if (have) req.IfModifiedSince = File.GetLastWriteTimeUtc(local);

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    // Przekierowanie albo strona HTML (np. wyzwanie Cloudflare) to nie
                    // nasz plik - nie nadpisuj nia dobrej kopii.
                    if (resp.StatusCode != HttpStatusCode.OK ||
                        (resp.ContentType ?? "").IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Debug.LogWarning("[OSMRoads] " + BaseUrl + file + ": nieoczekiwana odpowiedz " +
                                         (int)resp.StatusCode + " " + resp.ContentType +
                                         (have ? " - uzywam kopii z dysku" : ""));
                        return have ? 0 : -1;
                    }
                    string tmp = local + ".part";
                    using (Stream s = resp.GetResponseStream())
                    using (var fs = File.Create(tmp))
                        Interlocked.Add(ref BytesDownloaded, Copy(s, fs));

                    if (File.Exists(local)) File.Delete(local);
                    File.Move(tmp, local);
                    DateTime lm = resp.LastModified;
                    if (lm.Year > 2000) File.SetLastWriteTimeUtc(local, lm.ToUniversalTime());
                    return 1;
                }
            }
            catch (WebException e)
            {
                var r = e.Response as HttpWebResponse;
                if (r != null && r.StatusCode == HttpStatusCode.NotModified) { r.Close(); return 0; }
                string why = r != null ? (int)r.StatusCode + " " + r.StatusDescription : e.Status + ": " + e.Message;
                if (r != null) r.Close();
                Debug.LogWarning("[OSMRoads] " + BaseUrl + file + ": " + why + (have ? " - uzywam kopii z dysku" : ""));
                return have ? 0 : -1;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[OSMRoads] " + BaseUrl + file + ": " + e.Message);
                return have ? 0 : -1;
            }
        }

        // ------------------------------------------------------------------
        //  Komorki .bin
        // ------------------------------------------------------------------

        /// <summary>
        /// Skompresowane bajty jednej komorki: z dysku, a gdy ich nie ma - z serwera
        /// zapytaniem Range. Zwraca null, gdy sieci nie ma i kopii tez nie.
        /// Bezpieczne z wielu watkow naraz (kazda komorka to osobny plik,
        /// zapisywany przez .part i przemianowanie).
        /// </summary>
        public byte[] Cell(string pack, long key, long offset, int length)
        {
            string dir = CachePath(pack);
            string local = Path.Combine(dir, key.ToString("x16") + ".c");

            try
            {
                if (File.Exists(local))
                {
                    byte[] cached = File.ReadAllBytes(local);
                    if (cached.Length == length) { Interlocked.Increment(ref CacheHits); return cached; }
                }
            }
            catch (IOException) { }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    HttpWebRequest req = NewRequest(pack + ".bin");
                    req.AddRange(offset, offset + length - 1);

                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        // 200 zamiast 206 znaczy, ze po drodze ktos zignorowal Range
                        // i wysylalby CALY plik (setki MB). Przerywamy od razu.
                        if (resp.StatusCode != HttpStatusCode.PartialContent)
                            throw new IOException("serwer zignorowal Range (" + (int)resp.StatusCode + ")");

                        var buf = new byte[length];
                        using (Stream s = resp.GetResponseStream())
                        {
                            int done = 0;
                            while (done < length)
                            {
                                int n = s.Read(buf, done, length - done);
                                if (n <= 0) throw new IOException("urwana odpowiedz");
                                done += n;
                            }
                        }
                        Interlocked.Increment(ref Requests);
                        Interlocked.Add(ref BytesDownloaded, length);

                        try
                        {
                            Directory.CreateDirectory(dir);
                            string tmp = local + "." + Thread.CurrentThread.ManagedThreadId + ".part";
                            File.WriteAllBytes(tmp, buf);
                            if (File.Exists(local)) File.Delete(local);
                            File.Move(tmp, local);
                        }
                        catch (IOException) { }   // inny watek zapisal to samo - bez znaczenia

                        return buf;
                    }
                }
                catch (Exception e)
                {
                    var we = e as WebException;
                    var r = we != null ? we.Response as HttpWebResponse : null;
                    int code = r != null ? (int)r.StatusCode : 0;
                    if (r != null) r.Close();

                    // 403/404 nie minie od powtorzenia - zly klucz albo brak pliku.
                    if (code == 403 || code == 404 || attempt == 2)
                    {
                        Debug.LogWarning(string.Format("[OSMRoads] {0}{1}.bin komorka {2:x}: {3}",
                                                       BaseUrl, pack, key, code != 0 ? "HTTP " + code : e.Message));
                        return null;
                    }
                    // 429 = serwer ogranicza tempo; odczekaj dluzej niz przy zwyklym bledzie.
                    Thread.Sleep((code == 429 ? 1500 : 300) * (attempt + 1));
                }
            }
            return null;
        }

        /// <summary>Po zmianie indeksu na serwerze stare komorki sa bezuzyteczne -
        /// offsety w nowym .bin sa inne.</summary>
        public void DropCells(string pack)
        {
            try
            {
                string dir = CachePath(pack);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[OSMRoads] czyszczenie cache '" + pack + "': " + e.Message);
            }
        }

        /// <summary>
        /// Przycina pamiec podreczna do limitu, zaczynajac od najdawniej uzywanych
        /// komorek. W tle, bo przy setkach tysiecy plikow samo przejrzenie trwa.
        /// </summary>
        public void TrimInBackground()
        {
            var t = new Thread(() =>
            {
                try
                {
                    var files = new List<FileInfo>(new DirectoryInfo(CacheDir).GetFiles("*.c", SearchOption.AllDirectories));
                    long total = 0;
                    foreach (FileInfo f in files) total += f.Length;
                    if (total <= LimitBytes) return;

                    files.Sort((a, b) => a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc));
                    long target = (long)(LimitBytes * 0.8);
                    foreach (FileInfo f in files)
                    {
                        if (total <= target) break;
                        try { total -= f.Length; f.Delete(); } catch { }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[OSMRoads] przycinanie cache: " + e.Message);
                }
            }) { IsBackground = true, Name = "OSMRoads.CacheTrim" };
            t.Start();
        }

        private HttpWebRequest NewRequest(string file)
        {
            var req = (HttpWebRequest)WebRequest.Create(BaseUrl + file);
            req.Method = "GET";
            req.Timeout = 20000;
            req.ReadWriteTimeout = 30000;
            req.KeepAlive = true;
            req.UserAgent = "OSMRoads/0.21 (KSP)";
            req.AutomaticDecompression = DecompressionMethods.None;
            // Przekierowanie zabraloby naglowek z kluczem pod obcy adres.
            req.AllowAutoRedirect = false;
            if (!string.IsNullOrEmpty(Key) && KeyAllowed()) req.Headers["X-OSM-Key"] = Key;
            return req;
        }

        private int keyWarned;

        /// <summary>Klucz tylko po https albo do maszyny w sieci domowej - zwyklym
        /// http szedlby przez internet jawnym tekstem.</summary>
        private bool KeyAllowed()
        {
            var uri = new Uri(BaseUrl);
            if (uri.Scheme == Uri.UriSchemeHttps || IsLocalHost(uri)) return true;
            if (Interlocked.Exchange(ref keyWarned, 1) == 0)
                Debug.LogWarning("[OSMRoads] " + uri.Host + ": klucz nie zostanie wyslany zwyklym http - uzyj https://");
            return false;
        }

        private static bool IsLocalHost(Uri uri)
        {
            if (uri.IsLoopback) return true;
            System.Net.IPAddress ip;
            if (!System.Net.IPAddress.TryParse(uri.Host, out ip))
                return uri.Host.IndexOf('.') < 0;               // nazwa w sieci lokalnej, np. Hp-z230
            byte[] b = ip.GetAddressBytes();
            return b.Length == 4 && (b[0] == 10 || (b[0] == 192 && b[1] == 168) ||
                                     (b[0] == 172 && b[1] >= 16 && b[1] < 32));
        }

        private static long Copy(Stream from, Stream to)
        {
            var buf = new byte[1 << 16];
            long total = 0;
            int n;
            while ((n = from.Read(buf, 0, buf.Length)) > 0) { to.Write(buf, 0, n); total += n; }
            return total;
        }
    }
}
