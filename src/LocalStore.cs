using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Jedna paczka regionu: para .bin/.idx plus zasieg z manifestu .json.
    ///
    /// Indeks i tablica stringow wchodza do pamieci LENIWIE - dopiero gdy
    /// zapytanie pierwszy raz trafi w ten region. Dzieki temu mozna miec
    /// na dysku dwadziescia paczek calego swiata, a w pamieci tylko te,
    /// nad ktorymi faktycznie latasz.
    /// </summary>
    internal class Pack
    {
        public string Name;
        public string BinPath, IdxPath;
        public BBox Box;
        public bool HasBox;
        public long Bytes;

        public Dictionary<long, long> Offsets;
        public Dictionary<long, int> Lengths;
        public Dictionary<long, int> RawLengths;
        public string[] Strings;

        public FileStream Stream;
        public BinaryReader Reader;

        /// <summary>!= null: paczka z serwera HTTP(S). IdxPath wskazuje wtedy kopie
        /// indeksu w pamieci podrecznej, a .bin nie ma lokalnie wcale.</summary>
        public RemoteSource Remote;
        public bool RemoteChecked;

        /// <summary>Kiedy ostatnio uzyta - do eksmisji przy przekroczeniu budzetu.</summary>
        public float LastUsed;

        public bool Loaded { get { return Offsets != null; } }

        public bool Intersects(BBox b)
        {
            if (!HasBox) return true;      // bez manifestu nie ryzykujemy pominiecia
            return !(b.North < Box.South || b.South > Box.North ||
                     b.East < Box.West || b.West > Box.East);
        }
    }

    /// <summary>
    /// Czytnik lokalnych paczek OSM z GameData/OSMRoads/Data.
    ///
    /// Kazdy plik *.idx to osobny region. Zapytanie idzie tylko do tych paczek,
    /// ktorych zasieg przecina bbox - reszta nawet nie jest otwierana.
    ///
    /// Wczesniej byl to jeden globalny zbior z jedna tablica stringow wczytywana
    /// w calosci przy starcie. Przy planecie oznaczaloby to kazda nazwe ulicy
    /// na Ziemi w RAM zanim wczytasz pierwszy kafel - stad podzial na paczki.
    ///
    /// Odczyt jest bezpieczny watkowo pod wspolnym zamkiem, wiec TileJob i okno
    /// mapy moga wolac to prosto z watkow roboczych.
    /// </summary>
    public static class LocalStore
    {
        private sealed class CellJob
        {
            public Pack Pack;
            public long Key, Offset;
            public int Length, RawLength;
            public string[] Strings;
            public byte[] Packed;     // lokalne: juz przeczytane pod blokada
        }

        /// <summary>Ile paczek trzymamy rozwinietych w pamieci naraz.</summary>
        /// <summary>Paczki planety to kwadraty 10x10 st. Lot przez naroznik
        /// kwadratu dotyka czterech, a okno mapy przy oddaleniu nawet dziewieciu -
        /// przy budzecie 4 kazdy odczyt wyrzucalby paczke, ktora za chwile
        /// bylaby znowu potrzebna.</summary>
        private const int MaxLoadedPacks = 9;

        private static List<Pack> packs;
        private static readonly object gate = new object();

        public static string Report = "(nie sprawdzono)";

        public static string Dir
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/OSMRoads/Data"); }
        }

        /// <summary>
        /// Wszystkie katalogi z paczkami: lokalny Data plus te wpisane w
        /// GameData/OSMRoads/datadirs.txt (jedna sciezka na linie, '#' to komentarz).
        ///
        /// Paczki calej planety to kilkadziesiat GB i nie musza lezec w instalacji
        /// gry - moga siedziec na dysku sieciowym. Odczyt i tak idzie z watku
        /// roboczego, a zmierzone opoznienie po SMB to ok. 2 ms na odczyt.
        /// </summary>
        public static List<string> Dirs()
        {
            var dirs = new List<string> { Dir };
            string list = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/OSMRoads/datadirs.txt");
            if (!File.Exists(list)) return dirs;

            try
            {
                foreach (string raw in File.ReadAllLines(list))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    dirs.Add(line);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[OSMRoads] datadirs.txt: " + e.Message);
            }
            return dirs;
        }

        public static bool Available { get { Ensure(); return packs != null && packs.Count > 0; } }

        /// <summary>Pobrane z sieci / zapytania / trafienia w pamiec podreczna - do okna moda.</summary>
        public static string NetworkReport()
        {
            if (packs == null) return "";
            var seen = new HashSet<RemoteSource>();
            long bytes = 0, req = 0, hits = 0;
            lock (gate)
            {
                foreach (Pack p in packs)
                    if (p.Remote != null && seen.Add(p.Remote))
                    {
                        bytes += p.Remote.BytesDownloaded; req += p.Remote.Requests; hits += p.Remote.CacheHits;
                    }
            }
            if (seen.Count == 0) return "";
            return string.Format("siec: {0:N1} MB pobrane, {1:N0} zapytan, {2:N0} z dysku", bytes / 1048576.0, req, hits);
        }

        /// <summary>
        /// Czy lokalne paczki faktycznie pokrywaja ten obszar.
        ///
        /// To NIE jest to samo co Available. Available mowi tylko "cos mam na dysku";
        /// nad Niemcami z paczka Polski bylo by prawda, a odczyt zwrocilby zero
        /// obiektow i kafel zostalby oznaczony jako pusty - bez proby pobrania
        /// z sieci. Stad osobne pytanie o zasieg.
        /// </summary>
        public static bool Covers(BBox b)
        {
            Ensure();
            if (packs == null) return false;

            lock (gate)
            {
                foreach (Pack p in packs)
                    if (p.HasBox && p.Intersects(b)) return true;
            }
            return false;
        }
        public static int PackCount { get { Ensure(); return packs == null ? 0 : packs.Count; } }

        /// <summary>
        /// Plik wspolny dla calego zbioru, lezacy obok paczek (np. places.idx):
        /// pierwsza lokalna kopia, a gdy jej nie ma - z serwera do pamieci podrecznej.
        /// null, gdy nigdzie go nie ma. Wolac z watku roboczego - moze isc siecia.
        /// </summary>
        public static string FindShared(string file)
        {
            foreach (string dir in Dirs())
            {
                try
                {
                    if (RemoteSource.IsRemote(dir))
                    {
                        RemoteSource src = RemoteSource.Parse(dir);
                        if (src.Refresh(file) >= 0) return src.CachePath(file);
                    }
                    else
                    {
                        string p = Path.Combine(dir, file);
                        if (File.Exists(p)) return p;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[OSMRoads] " + file + " w " + dir + ": " + e.Message);
                }
            }
            return null;
        }

        /// <summary>Przeladowanie po dorzuceniu paczek bez restartu gry.</summary>
        public static void Reload()
        {
            lock (gate)
            {
                if (packs != null)
                    foreach (Pack p in packs) Close(p);
                packs = null;
            }
            Ensure();
        }

        // ------------------------------------------------------------------
        //  Skanowanie katalogu
        // ------------------------------------------------------------------

        private static void Ensure()
        {
            lock (gate)
            {
                if (packs != null) return;
                packs = new List<Pack>();

                long totalBytes = 0;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var missing = new List<string>();

                int remoteCount = 0;
                foreach (string dir in Dirs())
                {
                    if (RemoteSource.IsRemote(dir))
                    {
                        remoteCount += AddRemote(dir, names, missing);
                        continue;
                    }
                    if (!Directory.Exists(dir)) { missing.Add(dir); continue; }

                    // Jedna lista katalogu zamiast pytania o kazdy plik: SMB zwraca
                    // nazwy RAZEM z rozmiarem i data, wiec to jeden round-trip.
                    FileInfo[] files;
                    try { files = new DirectoryInfo(dir).GetFiles(); }
                    catch (Exception e) { missing.Add(dir + " (" + e.Message + ")"); continue; }

                    var info = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
                    foreach (FileInfo f in files) info[f.Name] = f;

                    // Zbiorczy manifest katalogu. Bez niego mod czytal osobny .json
                    // kazdej paczki - przy 440 paczkach planety przez SMB to kilka
                    // sekund zamrozenia gry przy pierwszym uzyciu.
                    FileInfo tsvInfo;
                    info.TryGetValue(TsvName, out tsvInfo);
                    Dictionary<string, BBox> tsv = tsvInfo != null ? ReadTsv(tsvInfo.FullName) : null;
                    bool tsvStale = tsv == null;

                    foreach (FileInfo idx in files)
                    {
                        if (!idx.Name.EndsWith(".idx", StringComparison.OrdinalIgnoreCase)) continue;

                        string name = Path.GetFileNameWithoutExtension(idx.Name);
                        FileInfo bin;
                        if (!info.TryGetValue(name + ".bin", out bin)) continue;

                        // Ta sama nazwa w dwoch katalogach: wygrywa pierwszy, czyli
                        // lokalny Data - pozwala nadpisac pojedynczy kwadrat z sieci.
                        if (!names.Add(name)) continue;

                        var p = new Pack
                        {
                            Name = name,
                            IdxPath = idx.FullName,
                            BinPath = bin.FullName,
                            Bytes = bin.Length + idx.Length
                        };

                        string manifest = Path.Combine(dir, name + ".json");
                        FileInfo json;
                        info.TryGetValue(name + ".json", out json);

                        // Z pliku zbiorczego tylko wtedy, gdy manifest paczki nie jest
                        // od niego nowszy - przebudowana paczka czyta sie na nowo.
                        BBox box;
                        if (tsv != null && tsv.TryGetValue(name, out box) &&
                            (json == null || json.LastWriteTimeUtc <= tsvInfo.LastWriteTimeUtc))
                        {
                            p.Box = box;
                            p.HasBox = true;
                        }
                        else
                        {
                            tsvStale = true;
                            if (json != null) ReadManifest(manifest, p);

                            // Paczka bez manifestu (np. zbudowana starsza wersja narzedzia)
                            // nie moglaby brac udzialu w tescie zasiegu, a bez zasiegu
                            // nie da sie stwierdzic, czy nad danym miejscem wypada siegnac
                            // do sieci. Wyliczamy go raz z kluczy komorek i zapisujemy obok.
                            if (!p.HasBox) DeriveBox(p, manifest);
                        }

                        packs.Add(p);
                        totalBytes += p.Bytes;
                    }

                    if (tsvStale) WriteTsv(dir, names);
                }

                foreach (string m in missing)
                    Debug.LogWarning("[OSMRoads] katalog paczek niedostepny: " + m);

                Report = packs.Count == 0
                    ? "brak paczek (Data i datadirs.txt)"
                    : string.Format("{0} paczek{1}, {2:N0} MB lokalnie: {3}{4}",
                                    packs.Count, remoteCount > 0 ? " (" + remoteCount + " z serwera)" : "",
                                    totalBytes / 1048576, NameList(),
                                    missing.Count > 0 ? "  | niedostepne: " + missing.Count : "");
                Debug.Log("[OSMRoads] " + Report);
            }
        }

        /// <summary>
        /// Zasieg wyliczony z kluczy komorek indeksu. Kosztuje jedno wczytanie
        /// indeksu przy starcie, ale tylko dla paczek bez manifestu - i tylko raz,
        /// bo wynik ladujemy obok jako .json.
        /// </summary>
        private static void DeriveBox(Pack p, string manifestPath)
        {
            if (!Load(p)) return;

            int minLat = int.MaxValue, maxLat = int.MinValue;
            int minLon = int.MaxValue, maxLon = int.MinValue;

            foreach (long key in p.Offsets.Keys)
            {
                int iLat = (int)(key >> 32);
                int iLon = (int)(key & 0xFFFFFFFFL);
                if (iLat < minLat) minLat = iLat;
                if (iLat > maxLat) maxLat = iLat;
                if (iLon < minLon) minLon = iLon;
                if (iLon > maxLon) maxLon = iLon;
            }

            int cells = p.Offsets.Count;
            Close(p);       // indeks wraca do lenistwa - potrzebowalismy tylko kluczy

            if (minLat == int.MaxValue) return;

            // Klucz to indeks komorki, wiec gorna krawedz siega o jedna komorke dalej.
            p.Box.South = minLat * PackFormat.CellDeg;
            p.Box.West = minLon * PackFormat.CellDeg;
            p.Box.North = (maxLat + 1) * PackFormat.CellDeg;
            p.Box.East = (maxLon + 1) * PackFormat.CellDeg;
            p.HasBox = true;

            Debug.Log(string.Format(
                "[OSMRoads] zasieg '{0}' wyliczony z indeksu: {1:F3}..{2:F3} N, {3:F3}..{4:F3} E",
                p.Name, p.Box.South, p.Box.North, p.Box.West, p.Box.East));

            WriteManifest(manifestPath, p, cells);
        }

        /// <summary>Zapis jest wygoda, nie warunkiem dzialania - instalacja moze
        /// stac na dysku tylko do odczytu i to jest w porzadku.</summary>
        private static void WriteManifest(string path, Pack p, int cells)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("{").Append(NL);
                Field(sb, "name", "\"" + p.Name + "\"", true);
                Field(sb, "south", p.Box.South.ToString("F5", CultureInfo.InvariantCulture), true);
                Field(sb, "west", p.Box.West.ToString("F5", CultureInfo.InvariantCulture), true);
                Field(sb, "north", p.Box.North.ToString("F5", CultureInfo.InvariantCulture), true);
                Field(sb, "east", p.Box.East.ToString("F5", CultureInfo.InvariantCulture), true);
                Field(sb, "cells", cells.ToString(CultureInfo.InvariantCulture), true);
                Field(sb, "derived", "true", false);
                sb.Append("}").Append(NL);
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[OSMRoads] nie zapisalem " + path + ": " + ex.Message);
            }
        }

        private const string NL = "\n";

        private static void Field(System.Text.StringBuilder sb, string key,
                                  string rawValue, bool comma)
        {
            sb.Append("  \"").Append(key).Append("\": ").Append(rawValue);
            if (comma) sb.Append(",");
            sb.Append(NL);
        }

        private const string TsvName = "packs.tsv";

        /// <summary>Paczki z serwera: lista z packs.tsv (swiezy albo kopia z dysku,
        /// gdy sieci brak). Zadnych indeksow tu nie pobieramy - to dzieje sie
        /// leniwie, przy pierwszym odczycie danego kwadratu.</summary>
        private static int AddRemote(string line, HashSet<string> names, List<string> missing)
        {
            RemoteSource src;
            try { src = RemoteSource.Parse(line); }
            catch (Exception e) { missing.Add(line + " (" + e.Message + ")"); return 0; }

            if (src.Refresh(TsvName) < 0) { missing.Add(src.BaseUrl); return 0; }
            Dictionary<string, BBox> list = ReadTsv(src.CachePath(TsvName));
            if (list == null) { missing.Add(src.BaseUrl); return 0; }

            int n = 0;
            foreach (KeyValuePair<string, BBox> kv in list)
            {
                if (!names.Add(kv.Key)) continue;
                packs.Add(new Pack
                {
                    Name = kv.Key,
                    Box = kv.Value,
                    HasBox = true,
                    IdxPath = src.CachePath(kv.Key + ".idx"),
                    Remote = src
                });
                n++;
            }
            src.TrimInBackground();
            return n;
        }

        /// <summary>nazwa, S, W, N, E - jedna paczka na linie.</summary>
        private static Dictionary<string, BBox> ReadTsv(string path)
        {
            try
            {
                var d = new Dictionary<string, BBox>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(path))
                {
                    string[] f = line.Split('\t');
                    if (f.Length < 5 || f[0].StartsWith("#")) continue;
                    BBox b;
                    if (!double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b.South) ||
                        !double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out b.West) ||
                        !double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out b.North) ||
                        !double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out b.East))
                        continue;
                    d[f[0]] = b;
                }
                return d;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[OSMRoads] " + path + ": " + e.Message);
                return null;
            }
        }

        /// <summary>Zapis tylko paczek z tego katalogu. Blad zapisu (udzial tylko
        /// do odczytu) nie jest problemem - najwyzej nastepny start skanuje po staremu.</summary>
        private static void WriteTsv(string dir, HashSet<string> names)
        {
            try
            {
                var sb = new System.Text.StringBuilder("# nazwa\tpoludnie\tzachod\tpolnoc\twschod - generowane przez OSMRoads\n");
                foreach (Pack p in packs)
                {
                    if (!p.HasBox) continue;
                    if (!string.Equals(Path.GetDirectoryName(p.IdxPath), Path.GetFullPath(dir).TrimEnd('\\', '/'),
                                       StringComparison.OrdinalIgnoreCase)) continue;
                    sb.Append(p.Name).Append('\t')
                      .Append(p.Box.South.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                      .Append(p.Box.West.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                      .Append(p.Box.North.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                      .Append(p.Box.East.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
                }
                File.WriteAllText(Path.Combine(dir, TsvName), sb.ToString());
            }
            catch (Exception e)
            {
                Debug.Log("[OSMRoads] nie zapisalem " + TsvName + " w " + dir + ": " + e.Message);
            }
        }

        private static string NameList()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < packs.Count && i < 6; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(packs[i].Name);
            }
            if (packs.Count > 6) sb.Append(", ...");
            return sb.ToString();
        }

        /// <summary>
        /// Zasieg paczki czytamy z malego .json, a nie z .idx - dzieki temu
        /// wiemy, gdzie ktora paczka siega, nie otwierajac zadnego indeksu.
        /// Parser jest celowo prymitywny: to nasz wlasny plik o znanym ksztalcie,
        /// nie ma sensu ciagnac biblioteki JSON do czterech liczb.
        /// </summary>
        private static void ReadManifest(string path, Pack p)
        {
            if (!File.Exists(path)) return;
            try
            {
                string t = File.ReadAllText(path);
                double s, w, n, e;
                if (Num(t, "south", out s) && Num(t, "west", out w) &&
                    Num(t, "north", out n) && Num(t, "east", out e))
                {
                    p.Box.South = s; p.Box.West = w; p.Box.North = n; p.Box.East = e;
                    p.HasBox = true;
                }
                double bytes;
                if (Num(t, "bytes", out bytes) && bytes > 0)
                {
                    p.Bytes = (long)bytes;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[OSMRoads] manifest " + path + ": " + ex.Message);
            }
        }

        private static bool Num(string json, string key, out double value)
        {
            value = 0;
            int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return false;
            i = json.IndexOf(':', i);
            if (i < 0) return false;

            int j = i + 1;
            while (j < json.Length && (json[j] == ' ' || json[j] == '\t')) j++;
            int start = j;
            while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '-' || json[j] == '.' ||
                                       json[j] == 'e' || json[j] == 'E' || json[j] == '+')) j++;

            return j > start && double.TryParse(json.Substring(start, j - start),
                                                NumberStyles.Float, CultureInfo.InvariantCulture,
                                                out value);
        }

        // ------------------------------------------------------------------
        //  Leniwe wczytanie indeksu paczki
        // ------------------------------------------------------------------

        private static bool Load(Pack p)
        {
            if (p.Loaded) return true;

            if (p.Remote != null && !p.RemoteChecked)
            {
                int r = p.Remote.Refresh(p.Name + ".idx");
                if (r < 0) return false;          // bez sieci i bez kopii - sprobuje przy nastepnym odczycie
                if (r > 0) p.Remote.DropCells(p.Name);
                p.RemoteChecked = true;
            }

            try
            {
                using (var fs = File.OpenRead(p.IdxPath))
                using (var r = new BinaryReader(fs))
                {
                    if (r.ReadUInt32() != PackFormat.Magic)
                        throw new IOException("zly naglowek");
                    int ver = r.ReadInt32();
                    if (ver != PackFormat.Version)
                        throw new IOException("wersja formatu " + ver + ", oczekiwano " + PackFormat.Version);

                    r.ReadDouble();                       // rozmiar komorki

                    int nStr = r.ReadInt32();
                    p.Strings = new string[nStr];
                    for (int i = 0; i < nStr; i++) p.Strings[i] = r.ReadString();

                    int count = r.ReadInt32();
                    p.Offsets = new Dictionary<long, long>(count);
                    p.Lengths = new Dictionary<long, int>(count);
                    p.RawLengths = new Dictionary<long, int>(count);

                    for (int i = 0; i < count; i++)
                    {
                        long key = r.ReadInt64();
                        p.Offsets[key] = r.ReadInt64();
                        p.Lengths[key] = r.ReadInt32();
                        p.RawLengths[key] = r.ReadInt32();
                    }
                }

                Debug.Log(string.Format("[OSMRoads] wczytano indeks '{0}': {1} komorek, {2} stringow",
                                        p.Name, p.Offsets.Count, p.Strings.Length));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[OSMRoads] indeks '" + p.Name + "': " + e.Message);
                p.Offsets = null; p.Lengths = null; p.RawLengths = null; p.Strings = null;
                return false;
            }
        }

        private static void Close(Pack p)
        {
            if (p.Reader != null) { p.Reader.Close(); p.Reader = null; }
            if (p.Stream != null) { p.Stream.Dispose(); p.Stream = null; }
            p.Offsets = null; p.Lengths = null; p.RawLengths = null; p.Strings = null;
        }

        /// <summary>Zwalnia najdawniej uzywane paczki ponad budzet.</summary>
        private static void Evict()
        {
            int loaded = 0;
            foreach (Pack p in packs) if (p.Loaded) loaded++;

            while (loaded > MaxLoadedPacks)
            {
                Pack worst = null;
                foreach (Pack p in packs)
                    if (p.Loaded && (worst == null || p.LastUsed < worst.LastUsed)) worst = p;
                if (worst == null) break;

                Debug.Log("[OSMRoads] zwalniam paczke '" + worst.Name + "'");
                Close(worst);
                loaded--;
            }
        }

        /// <summary>Strumien trzymany otwarty miedzy odczytami - przez SMB
        /// otwieranie pliku przy kazdym kaflu kosztuje kilka round-tripow.</summary>
        private static bool OpenStream(Pack p)
        {
            if (p.Remote != null) return true;
            if (p.Stream != null) return true;
            try
            {
                p.Stream = new FileStream(p.BinPath, FileMode.Open, FileAccess.Read,
                                          FileShare.Read, 1 << 16, false);
                p.Reader = new BinaryReader(p.Stream);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[OSMRoads] nie moge otworzyc " + p.BinPath + ": " + e.Message);
                if (p.Stream != null) { p.Stream.Dispose(); p.Stream = null; }
                p.Reader = null;
                return false;
            }
        }

        // ------------------------------------------------------------------
        //  Odczyt
        // ------------------------------------------------------------------

        public static List<OsmWay> Read(BBox b) { return Read(b, null); }

        /// <summary>Jeden przebieg zwraca i ways, i punkty z nazwami - siedza
        /// w tym samym strumieniu, wiec drugi odczyt bylby marnotrawstwem.</summary>
        public static List<OsmWay> Read(BBox b, List<OsmPlace> places)
        {
            Ensure();
            var result = new List<OsmWay>();
            if (packs == null || packs.Count == 0) return result;

            // Obiekt siedzi w kazdej komorce, ktora dotyka, a przy kilku paczkach
            // moze sie powtorzyc takze miedzy nimi - odsiewamy po id.
            var seen = new HashSet<long>();

            int lat0, lon0, lat1, lon1;
            PackFormat.CellOf(b.South, b.West, out lat0, out lon0);
            PackFormat.CellOf(b.North, b.East, out lat1, out lon1);

            // Pod blokada tylko to, co dotyka wspolnych struktur: wczytanie indeksu,
            // odczyt z lokalnego strumienia (jeden na paczke) i eksmisja.
            // Pobieranie z serwera i dekompresja ida juz bez niej - inaczej planer
            // i wczytywanie kafli czekalyby na siebie przy kazdym zapytaniu HTTP.
            var jobs = new List<CellJob>();

            lock (gate)
            {
                float now = Time.realtimeSinceStartup;

                foreach (Pack p in packs)
                {
                    if (!p.Intersects(b)) continue;
                    if (!Load(p)) continue;
                    if (!OpenStream(p)) continue;

                    p.LastUsed = now;

                    try
                    {
                        for (int y = lat0; y <= lat1; y++)
                        {
                            for (int x = lon0; x <= lon1; x++)
                            {
                                long key = PackFormat.CellKey(y, x);
                                long off;
                                if (!p.Offsets.TryGetValue(key, out off)) continue;

                                var job = new CellJob
                                {
                                    Pack = p, Key = key, Offset = off,
                                    Length = p.Lengths[key], RawLength = p.RawLengths[key],
                                    Strings = p.Strings
                                };

                                if (p.Remote == null)
                                {
                                    p.Stream.Seek(off, SeekOrigin.Begin);
                                    job.Packed = p.Reader.ReadBytes(job.Length);
                                }
                                jobs.Add(job);
                            }
                        }
                    }
                    catch (IOException e)
                    {
                        // Dysk sieciowy potrafi zniknac w trakcie - zamykamy strumien,
                        // nastepny odczyt sprobuje otworzyc go od nowa zamiast sypac
                        // tym samym bledem w kolko.
                        Debug.LogWarning("[OSMRoads] '" + p.Name + "' odczyt przerwany: " + e.Message);
                        if (p.Reader != null) { p.Reader.Close(); p.Reader = null; }
                        if (p.Stream != null) { p.Stream.Dispose(); p.Stream = null; }
                    }
                }

                Evict();
            }

            // Poza blokada. Tablica stringow jest przechwycona w zadaniu, wiec
            // eksmisja paczki w miedzyczasie nie wyrwie jej spod dekodowania.
            foreach (CellJob job in jobs)
            {
                byte[] packed = job.Packed ??
                                job.Pack.Remote.Cell(job.Pack.Name, job.Key, job.Offset, job.Length);
                if (packed == null) continue;

                byte[] raw = PackFormat.Inflate(packed, job.RawLength);
                using (var cs = new MemoryStream(raw))
                using (var cr = new BinaryReader(cs))
                    ReadCell(cr, job.Strings, result, places, seen, b);
            }

            return result;
        }

        /// <summary>
        /// Dekoduje komorke i zostawia TYLKO to, co dotyka zadanego prostokata.
        ///
        /// Filtr jest konieczny, bo komorka ma 0.05 stopnia (ok. 5.5 x 3.5 km),
        /// a kafel terenu 1.5 km. Bez tego kazdy kafel dostawal zawartosc obszaru
        /// kilkanascie razy wiekszego od siebie i budowal ja w calosci - sasiednie
        /// kafle renderowaly te same budynki jeden na drugim.
        /// </summary>
        private static void ReadCell(BinaryReader r, string[] strings, List<OsmWay> into,
                                     List<OsmPlace> places, HashSet<long> seen, BBox b)
        {
            int count = (int)PackFormat.ReadVarInt(r);
            for (int i = 0; i < count; i++)
            {
                byte kind = r.ReadByte();
                long id = PackFormat.ReadVarInt(r);

                if (kind == PackFormat.KindPlace)
                {
                    var pl = new OsmPlace
                    {
                        Name = Str(r, strings),
                        Kind = Str(r, strings)
                    };
                    pl.Lat = PackFormat.ReadVarInt(r) / PackFormat.CoordScale;
                    pl.Lon = PackFormat.ReadVarInt(r) / PackFormat.CoordScale;
                    if (places != null && seen.Add(id) &&
                        pl.Lat >= b.South && pl.Lat <= b.North &&
                        pl.Lon >= b.West && pl.Lon <= b.East)
                        places.Add(pl);
                    continue;
                }

                var w = new OsmWay();
                if (kind == PackFormat.KindRoad)
                {
                    w.Highway = Str(r, strings);
                    w.Name = Str(r, strings);
                }
                else if (kind == PackFormat.KindBuilding)
                {
                    w.Building = Str(r, strings);
                    w.HeightM = r.ReadSingle();
                    w.Levels = (int)PackFormat.ReadVarInt(r);
                    w.RoofShape = Str(r, strings);
                    w.Name = Str(r, strings);
                }
                else
                {
                    w.Area = (AreaKind)r.ReadSByte();
                    w.Name = Str(r, strings);
                }

                int n = (int)PackFormat.ReadVarInt(r);
                long lat = 0, lon = 0;
                for (int j = 0; j < n; j++)
                {
                    lat += PackFormat.ReadVarInt(r);
                    lon += PackFormat.ReadVarInt(r);
                    w.Points.Add(new GeoPoint(lat / PackFormat.CoordScale,
                                              lon / PackFormat.CoordScale));
                }

                if (!seen.Add(id)) continue;

                w.EnsureBounds();
                if (!w.Overlaps(b.South, b.North, b.West, b.East)) continue;

                into.Add(w);
            }
        }

        /// <summary>Stringi sa internowane w naglowku indeksu KAZDEJ paczki -
        /// w strumieniu siedzi tylko indeks do tablicy tej paczki.</summary>
        private static string Str(BinaryReader r, string[] strings)
        {
            int i = (int)PackFormat.ReadVarInt(r);
            return i >= 0 && i < strings.Length ? strings[i] : null;
        }
    }
}
