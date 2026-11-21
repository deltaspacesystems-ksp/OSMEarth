using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace PlanetPack
{
    internal enum PassMode { Scan, Batch }

    /// <summary>
    /// Jeden przebieg przez .osm.pbf: czytnik, pula dekoderow, committer.
    ///
    /// Bloki pliku sa niezalezne (kazdy ma wlasny zlib i wlasna tablice stringow),
    /// wiec dekodowanie idzie rownolegle na wszystkich rdzeniach. OsmSharp robil
    /// to jednym watkiem i to on, a nie dysk, ograniczal stary konwerter.
    ///
    /// Jedyne, co wymaga kolejnosci, to dopisywanie wezlow do magazynu - te wyniki
    /// committer przyjmuje scisle po numerze bloku. Drogi rozwiazuja juz watki
    /// robocze same, ale dopiero gdy magazyn jest kompletny ("zamrozony").
    ///
    /// Zamrozenie nastepuje, gdy committer przyjal wszystkie bloki przed pierwszym
    /// blokiem z drogami. Nie moze czekac na wynik TEGO bloku - jego watek sam
    /// czeka na zamrozenie i obaj staliby w miejscu. Stad osobny kanal, ktorym
    /// watek zglasza "mam drogi" jeszcze przed ich rozwiazaniem.
    /// </summary>
    internal sealed class Pass
    {
        private sealed class Blob
        {
            public long Seq;
            public byte[] Data;
            public int Length;
        }

        private readonly string path;
        private readonly int workers;
        private readonly PassMode mode;
        private readonly BatchSpec spec;
        private readonly NodeStore store;
        private readonly Spill spill;
        private readonly Action<string> log;

        public readonly PassStats Stats = new PassStats();
        public long[] Histogram;
        public string Features = "";

        private BlockingCollection<Blob> input;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();

        private readonly object gate = new object();
        private readonly Dictionary<long, BlockResult> results = new Dictionary<long, BlockResult>();
        private long next;
        private long total = -1;
        private bool readerDone;
        private long minPureWaySeq = long.MaxValue;
        private long stopAfter = long.MaxValue;
        private bool frozen;
        private readonly ManualResetEventSlim freezeEvent = new ManualResetEventSlim(false);
        private Exception fatal;

        private long bytesRead;
        private long fileLen;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long lastLogMs;

        public Pass(string path, int workers, PassMode mode, BatchSpec spec,
                    NodeStore store, Spill spill, Action<string> log)
        {
            this.path = path;
            this.workers = workers;
            this.mode = mode;
            this.spec = spec;
            this.store = store;
            this.spill = spill;
            this.log = log;
        }

        public void Run()
        {
            fileLen = new FileInfo(path).Length;
            input = new BlockingCollection<Blob>(workers * 3);

            var reader = new Thread(ReaderLoop) { Name = "pbf-reader", IsBackground = true };
            reader.Start();

            var contexts = new DecodeContext[workers];
            var threads = new Thread[workers];
            for (int i = 0; i < workers; i++)
            {
                var ctx = new DecodeContext();
                if (mode == PassMode.Scan) ctx.Hist = new long[Grid.Deg1Count];
                contexts[i] = ctx;
                threads[i] = new Thread(() => WorkerLoop(ctx)) { Name = "pbf-worker-" + i, IsBackground = true };
                threads[i].Start();
            }

            if (mode == PassMode.Batch) CommitLoop();
            else
            {
                foreach (Thread t in threads)
                    while (!t.Join(1000)) Progress(false);
            }

            reader.Join();
            foreach (Thread t in threads) t.Join();

            if (fatal != null) throw new Exception("przebieg przerwany: " + fatal.Message, fatal);

            if (mode == PassMode.Scan)
            {
                Histogram = new long[Grid.Deg1Count];
                foreach (DecodeContext ctx in contexts)
                {
                    for (int i = 0; i < Grid.Deg1Count; i++) Histogram[i] += ctx.Hist[i];
                    Stats.NodesSeen += ctx.Nodes;
                    if (ctx.MaxId > Stats.MaxId) Stats.MaxId = ctx.MaxId;
                }
            }

            Progress(true);
        }

        // ------------------------------------------------------------------

        private void ReaderLoop()
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                               1 << 22, FileOptions.SequentialScan))
                {
                    var len4 = new byte[4];
                    long seq = 0;

                    while (!cts.IsCancellationRequested)
                    {
                        if (seq > Interlocked.Read(ref stopAfter)) break;
                        if (!ReadExact(fs, len4, 4)) break;

                        int hl = (len4[0] << 24) | (len4[1] << 16) | (len4[2] << 8) | len4[3];
                        var hdr = new byte[hl];
                        if (!ReadExact(fs, hdr, hl)) throw new EndOfStreamException("urwany BlobHeader");

                        string type = null;
                        int dataSize = -1, p = 0;
                        while (p < hl)
                        {
                            ulong tag = Proto.Varint(hdr, ref p);
                            int f = (int)(tag >> 3), wire = (int)(tag & 7);
                            if (f == 1 && wire == 2)
                            {
                                int l = (int)Proto.Varint(hdr, ref p);
                                type = Encoding.ASCII.GetString(hdr, p, l);
                                p += l;
                            }
                            else if (f == 3 && wire == 0) dataSize = (int)Proto.Varint(hdr, ref p);
                            else Proto.Skip(hdr, ref p, wire);
                        }
                        if (dataSize < 0) throw new InvalidDataException("BlobHeader bez datasize");

                        byte[] data = ArrayPool<byte>.Shared.Rent(dataSize);
                        if (!ReadExact(fs, data, dataSize)) throw new EndOfStreamException("urwany Blob");
                        Interlocked.Exchange(ref bytesRead, fs.Position);

                        if (type == "OSMHeader")
                        {
                            ReadHeader(data, dataSize);
                            ArrayPool<byte>.Shared.Return(data);
                            continue;
                        }
                        if (type != "OSMData")
                        {
                            ArrayPool<byte>.Shared.Return(data);
                            continue;
                        }

                        input.Add(new Blob { Seq = seq, Data = data, Length = dataSize }, cts.Token);
                        seq++;
                    }

                    lock (gate)
                    {
                        total = seq;
                        readerDone = true;
                        Monitor.PulseAll(gate);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Fail(e); }
            finally
            {
                input.CompleteAdding();
                lock (gate) { readerDone = true; if (total < 0) total = 0; Monitor.PulseAll(gate); }
            }
        }

        private void ReadHeader(byte[] data, int len)
        {
            int rawLen;
            byte[] raw = BlockDecoder.Inflate(data, len, out rawLen);
            try
            {
                var sb = new StringBuilder();
                int p = 0;
                while (p < rawLen)
                {
                    ulong tag = Proto.Varint(raw, ref p);
                    int f = (int)(tag >> 3), wire = (int)(tag & 7);
                    if ((f == 4 || f == 5) && wire == 2)
                    {
                        int l = (int)Proto.Varint(raw, ref p);
                        sb.Append(Encoding.UTF8.GetString(raw, p, l)).Append(' ');
                        p += l;
                    }
                    else Proto.Skip(raw, ref p, wire);
                }
                Features = sb.ToString().Trim();
            }
            finally { ArrayPool<byte>.Shared.Return(raw); }
        }

        private static bool ReadExact(Stream s, byte[] buf, int count)
        {
            int done = 0;
            while (done < count)
            {
                int n = s.Read(buf, done, count - done);
                if (n <= 0) return done == 0 && count > 0 ? false : throw new EndOfStreamException();
                done += n;
            }
            return true;
        }

        // ------------------------------------------------------------------

        private void WorkerLoop(DecodeContext ctx)
        {
            try
            {
                foreach (Blob bl in input.GetConsumingEnumerable(cts.Token))
                {
                    if (bl.Seq > Interlocked.Read(ref stopAfter))
                    {
                        ArrayPool<byte>.Shared.Return(bl.Data);
                        if (mode == PassMode.Batch) Publish(new BlockResult { Seq = bl.Seq });
                        continue;
                    }

                    int rawLen;
                    byte[] raw = BlockDecoder.Inflate(bl.Data, bl.Length, out rawLen);
                    ArrayPool<byte>.Shared.Return(bl.Data);

                    BlockInfo info = BlockDecoder.ReadInfo(raw, rawLen, ctx);
                    Interlocked.Increment(ref Stats.Blocks);

                    if (mode == PassMode.Scan)
                    {
                        // Plik jest posortowany: po pierwszym bloku z drogami wezlow
                        // juz nie bedzie, wiec resztę pliku mozna w ogole pominac.
                        if (info.HasWays) LowerStop(bl.Seq);
                        if (info.HasNodes) BlockDecoder.ScanNodes(raw, info, ctx);
                        ArrayPool<byte>.Shared.Return(raw);
                        continue;
                    }

                    if (info.HasRels && !info.HasWays && !info.HasNodes)
                    {
                        // Relacji nie uzywamy - pierwszy taki blok konczy przebieg.
                        LowerStop(bl.Seq);
                        ArrayPool<byte>.Shared.Return(raw);
                        Publish(new BlockResult { Seq = bl.Seq });
                        continue;
                    }

                    if (info.HasWays && !info.HasNodes)
                    {
                        ReportPureWays(bl.Seq);
                        freezeEvent.Wait(cts.Token);
                        BlockDecoder.ResolveWays(raw, rawLen, ctx, store, spill, spec, Stats);
                        ArrayPool<byte>.Shared.Return(raw);
                        Publish(new BlockResult { Seq = bl.Seq });
                        continue;
                    }

                    if (info.HasNodes)
                    {
                        BlockResult r = BlockDecoder.FilterNodes(raw, info, ctx, spec, spill, Stats);
                        r.Seq = bl.Seq;
                        if (info.HasWays)
                        {
                            r.MixedBuf = raw;
                            r.MixedLen = rawLen;
                        }
                        else ArrayPool<byte>.Shared.Return(raw);
                        Publish(r);
                        continue;
                    }

                    ArrayPool<byte>.Shared.Return(raw);
                    Publish(new BlockResult { Seq = bl.Seq });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Fail(e); }
        }

        private void Publish(BlockResult r)
        {
            lock (gate)
            {
                results[r.Seq] = r;
                Monitor.PulseAll(gate);
            }
        }

        private void ReportPureWays(long seq)
        {
            lock (gate)
            {
                if (seq < minPureWaySeq) minPureWaySeq = seq;
                Monitor.PulseAll(gate);
            }
        }

        private void LowerStop(long seq)
        {
            while (true)
            {
                long cur = Interlocked.Read(ref stopAfter);
                if (seq >= cur) return;
                if (Interlocked.CompareExchange(ref stopAfter, seq, cur) == cur) return;
            }
        }

        private void Freeze()
        {
            lock (gate) frozen = true;
            freezeEvent.Set();
        }

        private void Fail(Exception e)
        {
            lock (gate)
            {
                if (fatal == null) fatal = e;
                Monitor.PulseAll(gate);
            }
            cts.Cancel();
        }

        // ------------------------------------------------------------------

        private void CommitLoop()
        {
            var committerCtx = new DecodeContext();

            try
            {
                while (true)
                {
                    BlockResult r = null;
                    lock (gate)
                    {
                        while (true)
                        {
                            if (fatal != null) return;
                            if (!frozen && next >= minPureWaySeq) { frozen = true; freezeEvent.Set(); }
                            if (results.Remove(next, out r)) break;
                            if (readerDone && next >= total) { r = null; break; }
                            Monitor.Wait(gate, 500);
                            Progress(false);
                        }
                    }
                    if (r == null) break;

                    if (r.NodeCount > 0)
                    {
                        if (frozen)
                            throw new InvalidDataException(
                                "wezly po drogach (blok " + r.Seq + ") - plik nie jest posortowany");

                        for (int i = 0; i < r.NodeCount; i++)
                            store.Append(r.Ids[i], r.Lat[i], r.Lon[i]);
                        Interlocked.Add(ref Stats.NodesKept, r.NodeCount);
                    }
                    if (r.Ids != null)
                    {
                        ArrayPool<long>.Shared.Return(r.Ids);
                        ArrayPool<int>.Shared.Return(r.Lat);
                        ArrayPool<int>.Shared.Return(r.Lon);
                    }

                    if (r.MixedBuf != null)
                    {
                        // Wezly i drogi w jednym bloku: wezly tego bloku juz sa, a po nim
                        // w posortowanym pliku wezlow nie bedzie - mozna zamrozic.
                        Freeze();
                        BlockDecoder.ResolveWays(r.MixedBuf, r.MixedLen, committerCtx,
                                                 store, spill, spec, Stats);
                        ArrayPool<byte>.Shared.Return(r.MixedBuf);
                    }

                    lock (gate) next++;
                    Progress(false);
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
            finally
            {
                // Nikt nie moze zostac uspiony na zamrozeniu po koncu przebiegu.
                Freeze();
            }
        }

        private void Progress(bool final)
        {
            long ms = clock.ElapsedMilliseconds;
            if (!final && ms - Interlocked.Read(ref lastLogMs) < 10000) return;
            Interlocked.Exchange(ref lastLogMs, ms);

            double sec = Math.Max(1.0, ms / 1000.0);
            long br = Interlocked.Read(ref bytesRead);
            double mbps = br / 1048576.0 / sec;
            double pct = fileLen > 0 ? 100.0 * br / fileLen : 0;
            double etaMin = br > 0 ? (fileLen - br) / (br / sec) / 60.0 : 0;

            string line = mode == PassMode.Scan
                ? string.Format("  {0,5:F1}%  {1,6:N1}/{2:N1} GB  {3,5:F0} MB/s  blokow {4:N0}  {5:F0} min{6}",
                                pct, br / 1073741824.0, fileLen / 1073741824.0, mbps,
                                Stats.Blocks, sec / 60, final ? "  KONIEC" : string.Format("  (do konca pliku ~{0:F0} min)", etaMin))
                : string.Format("  {0,5:F1}%  {1,6:N1} GB  {2,5:F0} MB/s | wezly {3:N0} ({4:N1} GB) | obiekty {5:N0} | odrzucone {6:N0} | zrzut {7:N1} GB | RAM {8:N1} GB | {9:F0} min{10}",
                                pct, br / 1073741824.0, mbps,
                                store.Count, store.AllocatedBytes / 1073741824.0,
                                Interlocked.Read(ref Stats.Ways), Interlocked.Read(ref Stats.WaysDropped),
                                spill.BytesWritten / 1073741824.0,
                                Process.GetCurrentProcess().WorkingSet64 / 1073741824.0,
                                sec / 60, final ? "  KONIEC" : "");
            log(line);
        }
    }
}
