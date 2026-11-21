using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace PlanetPack
{
    /// <summary>
    /// Zrzut rozwiazanych obiektow na dysk, po jednym pliku na stopien.
    ///
    /// Stary konwerter trzymal wszystkie drogi obszaru w RAM az do zapisu paczki,
    /// czyli drugi raz tyle pamieci co wezly. Tu obiekt idzie na dysk w chwili,
    /// gdy znamy juz jego geometrie, a paczke skladamy pozniej stopien po stopniu -
    /// jeden stopien to najwyzej kilkaset MB nawet nad Tokio.
    ///
    /// Bufor na stopien jest w pamieci i leci do pliku po przekroczeniu progu.
    /// Otwieranie pliku przy kazdym obiekcie przez SMB zabiloby przepustowosc.
    /// </summary>
    internal sealed class Spill
    {
        private const int FlushAt = 1 << 19;

        private sealed class Cell
        {
            public readonly object Lock = new object();
            public readonly MemoryStream Buf = new MemoryStream();
        }

        private readonly string dir;
        private readonly ConcurrentDictionary<int, Cell> cells = new ConcurrentDictionary<int, Cell>();
        private long written;

        public Spill(string dir)
        {
            this.dir = dir;
            Directory.CreateDirectory(dir);
        }

        public long BytesWritten { get { return Interlocked.Read(ref written); } }

        public string PathOf(int d1)
        {
            return Path.Combine(dir, "d" + d1 + ".spl");
        }

        public void Append(int d1, byte[] rec, int len)
        {
            Cell c = cells.GetOrAdd(d1, _ => new Cell());
            lock (c.Lock)
            {
                c.Buf.Write(rec, 0, len);
                if (c.Buf.Length >= FlushAt) FlushLocked(d1, c);
            }
        }

        private void FlushLocked(int d1, Cell c)
        {
            if (c.Buf.Length == 0) return;
            using (var fs = new FileStream(PathOf(d1), FileMode.Append, FileAccess.Write,
                                           FileShare.None, 1 << 16))
                fs.Write(c.Buf.GetBuffer(), 0, (int)c.Buf.Length);
            Interlocked.Add(ref written, c.Buf.Length);
            c.Buf.SetLength(0);
        }

        public void FlushAll()
        {
            foreach (var kv in cells)
                lock (kv.Value.Lock) FlushLocked(kv.Key, kv.Value);
        }
    }
}
