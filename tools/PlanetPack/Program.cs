using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace PlanetPack
{
    /// <summary>
    /// planetpack - paczki moda z calej planety przy ograniczonym RAM.
    ///
    /// Trzy etapy, kazdy wznawialny:
    ///   1. ROZPOZNANIE: jeden przebieg po samych wezlach, histogram na stopien.
    ///   2. PLAN: kwadraty 10x10 st. grupowane w przebiegi mieszczace sie w budzecie.
    ///   3. PRZEBIEGI: dla kazdego - wezly pasa do pamieci, drogi rozwiazane
    ///      i zrzucone na dysk, paczki skladane z zrzutu, zrzut kasowany.
    ///
    /// Przerwanie w dowolnym momencie jest bezpieczne: skonczone przebiegi maja
    /// znacznik, a paczki powstaja atomowo przez .tmp.
    /// </summary>
    internal static class Program
    {
        private static StreamWriter logFile;

        private static void Log(string s)
        {
            string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + s;
            lock (typeof(Program))
            {
                Console.WriteLine(line);
                if (logFile != null) { logFile.WriteLine(line); logFile.Flush(); }
            }
        }

        private static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--check")
                return Check.Run(args[1], args.Length > 3 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 50.92,
                                 args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 22.23);
            if (args.Length < 3)
            {
                Console.WriteLine("uzycie: planetpack <plik.osm.pbf> <katalog-roboczy> <katalog-paczek> [opcje]");
                Console.WriteLine("  --budget-gb N     pamiec na wezly jednego przebiegu (domyslnie 18)");
                Console.WriteLine("  --threads N       watki dekodowania (domyslnie rdzenie - 1)");
                Console.WriteLine("  --margin-deg N    zapas wokol przebiegu w stopniach (domyslnie 1)");
                Console.WriteLine("  --squares a,b     tylko te kwadraty, np. p_n50e020,p_n40e020");
                Console.WriteLine("  --plan-only       rozpoznanie + plan, bez przebiegow");
                return 1;
            }

            string input = args[0], work = args[1], outDir = args[2];
            double budgetGb = 18;
            int threads = Math.Max(1, Environment.ProcessorCount - 1);
            int marginDeg = 1;
            HashSet<int> only = null;
            bool planOnly = false;

            for (int i = 3; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--budget-gb": budgetGb = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--threads": threads = int.Parse(args[++i]); break;
                    case "--margin-deg": marginDeg = int.Parse(args[++i]); break;
                    case "--plan-only": planOnly = true; break;
                    case "--squares":
                        only = new HashSet<int>();
                        foreach (string s in args[++i].Split(','))
                        {
                            int sq = Grid.ParseSquare(s.Trim());
                            if (sq < 0) { Console.WriteLine("zly kwadrat: " + s); return 1; }
                            only.Add(sq);
                        }
                        break;
                    default: Console.WriteLine("nieznana opcja: " + args[i]); return 1;
                }
            }

            Directory.CreateDirectory(work);
            Directory.CreateDirectory(outDir);
            logFile = new StreamWriter(Path.Combine(work, "planetpack.log"), true, Encoding.UTF8);

            try
            {
                return Run(input, work, outDir, budgetGb, threads, marginDeg, only, planOnly);
            }
            catch (Exception e)
            {
                Log("BLAD: " + e);
                return 2;
            }
            finally
            {
                logFile.Dispose();
            }
        }

        private static int Run(string input, string work, string outDir, double budgetGb, int threads,
                               int marginDeg, HashSet<int> only, bool planOnly)
        {
            var total = Stopwatch.StartNew();
            var fi = new FileInfo(input);
            Log(string.Format("=== planetpack: {0} ({1:N1} GB), watki {2}, budzet {3} GB, zapas {4} st.",
                              input, fi.Length / 1073741824.0, threads, budgetGb, marginDeg));

            // --- 1. rozpoznanie ---
            string scanPath = Path.Combine(work, "scan.bin");
            string stamp = fi.Length + ":" + fi.LastWriteTimeUtc.Ticks;
            long[] hist = LoadScan(scanPath, stamp);

            if (hist == null)
            {
                Log("[1/3] rozpoznanie: histogram wezlow ...");
                var pass = new Pass(input, threads, PassMode.Scan, null, null, null, Log);
                pass.Run();
                if (pass.Features.Length > 0) Log("  cechy pliku: " + pass.Features);
                if (pass.Features.Length > 0 && !pass.Features.Contains("Sort.Type_then_ID"))
                    Log("  UWAGA: plik nie deklaruje sortowania - przebiegi moga zglosic blad kolejnosci");

                hist = pass.Histogram;
                Log(string.Format("  wezlow {0:N0}, najwyzsze id {1:N0}", pass.Stats.NodesSeen, pass.Stats.MaxId));
                SaveScan(scanPath, stamp, hist);
            }
            else Log("[1/3] rozpoznanie: z pliku scan.bin");

            // --- 1b. multipoligony (z ogona pliku) ---
            List<RelInfo> rels = RelScan.LoadOrRun(input, Path.Combine(work, "rels.bin"), stamp, Log);

            // --- 2. plan ---
            long budgetNodes = (long)(budgetGb * 1073741824.0 / 10.0);
            List<List<int>> batches = Planner.Plan(hist, budgetNodes, marginDeg, only, Log);

            var plan = new StringBuilder();
            long planned = 0;
            for (int b = 0; b < batches.Count; b++)
            {
                long n = Planner.NodesFor(batches[b], hist, marginDeg);
                planned += n;
                plan.AppendLine(string.Format("{0,3}  {1,14:N0} wezlow  {2,5:N1} GB  {3}",
                                              b, n, n * 10.0 / 1073741824.0, Planner.Describe(batches[b])));
            }
            File.WriteAllText(Path.Combine(work, "plan.txt"), plan.ToString());
            Log(string.Format("[2/3] plan: {0} przebiegow, {1} kwadratow z danymi", batches.Count, CountSquares(batches)));
            foreach (string line in plan.ToString().Split('\n'))
                if (line.Trim().Length > 0) Log("  " + line.TrimEnd());

            if (planOnly) return 0;

            // --- 3. przebiegi ---
            for (int b = 0; b < batches.Count; b++)
            {
                string marker = Path.Combine(work, "batch_" + b + ".done");
                string desc = Planner.Describe(batches[b]);
                if (File.Exists(marker) && File.ReadAllText(marker).Trim() == desc)
                {
                    Log(string.Format("[3/3] przebieg {0}/{1}: juz zrobiony", b + 1, batches.Count));
                    continue;
                }

                Log(string.Format("[3/3] przebieg {0}/{1}: {2}", b + 1, batches.Count, desc));
                RunBatch(input, work, outDir, threads, marginDeg, batches[b], rels);
                File.WriteAllText(marker, desc);
            }

            Log(string.Format("=== GOTOWE w {0:F1} h", total.Elapsed.TotalHours));
            return 0;
        }

        private static void RunBatch(string input, string work, string outDir, int threads,
                                     int marginDeg, List<int> squares, List<RelInfo> rels)
        {
            var sw = Stopwatch.StartNew();
            string spillDir = Path.Combine(work, "spill");

            // Zrzut z przerwanego przebiegu jest niekompletny - od zera.
            if (Directory.Exists(spillDir)) Directory.Delete(spillDir, true);

            var spec = new BatchSpec
            {
                Dilated = Planner.DilatedMask(squares, marginDeg),
                Core = Planner.CoreMask(squares)
            };

            var store = new NodeStore();
            var spill = new Spill(spillDir);
            spec.Rel = rels.Count > 0 ? new RelCapture(rels, Path.Combine(spillDir, "relcap.bin")) : null;
            var pass = new Pass(input, threads, PassMode.Batch, spec, store, spill, Log);
            pass.Run();
            spill.FlushAll();

            Log(string.Format("  przebieg: wezlow {0:N0}, obiektow {1:N0}, rekordow {2:N0}, miejsc {3:N0}, odrzuconych {4:N0}, {5:F1} min",
                              store.Count, pass.Stats.Ways, pass.Stats.Records, pass.Stats.Places,
                              pass.Stats.WaysDropped, sw.Elapsed.TotalMinutes));

            // Magazyn wezlow nie jest juz potrzebny - oddajemy pamiec przed skladaniem.
            store = null;
            pass = null;
            GC.Collect(2, GCCollectionMode.Forced, true, true);

            if (spec.Rel != null)
            {
                var rs = new PassStats();
                spec.Rel.Assemble(rels, spill, spec, rs, Log);
                spill.FlushAll();
                spec.Rel = null;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
            }

            var bw = Stopwatch.StartNew();
            int built = 0;
            foreach (int sq in squares)
            {
                long objs;
                if (PackBuilder.Build(sq, spill, outDir, threads, Log, out objs)) built++;
            }
            Log(string.Format("  paczek: {0}, skladanie {1:F1} min", built, bw.Elapsed.TotalMinutes));

            Directory.Delete(spillDir, true);
        }

        private static int CountSquares(List<List<int>> batches)
        {
            int n = 0;
            foreach (var b in batches) n += b.Count;
            return n;
        }

        private static long[] LoadScan(string path, string stamp)
        {
            if (!File.Exists(path)) return null;
            using (var br = new BinaryReader(File.OpenRead(path)))
            {
                if (br.ReadString() != stamp) return null;   // inny plik wejsciowy
                var h = new long[Grid.Deg1Count];
                for (int i = 0; i < h.Length; i++) h[i] = br.ReadInt64();
                return h;
            }
        }

        private static void SaveScan(string path, string stamp, long[] hist)
        {
            using (var bw = new BinaryWriter(File.Create(path)))
            {
                bw.Write(stamp);
                foreach (long v in hist) bw.Write(v);
            }
        }
    }
}
