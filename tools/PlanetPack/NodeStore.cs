using System.Collections.Generic;
using System.IO;

namespace PlanetPack
{
    /// <summary>
    /// Wspolrzedne wezlow jednego przebiegu - 10 bajtow na wezel.
    ///
    /// Stary konwerter trzymal je w Dictionary&lt;long, KeyValuePair&lt;int,int&gt;&gt;,
    /// co z narzutem .NET daje ok. 36 bajtow na wezel i podwajanie tablicy przy
    /// kazdym rozroscie. Przy planecie to kilkaset GB.
    ///
    /// Tu wykorzystujemy to, ze w pliku .pbf wezly przychodza POSORTOWANE po id:
    ///   - nic nie trzeba wstawiac w srodek, tylko dopisywac na koniec,
    ///   - id dzielimy na kubelek (gorne bity) i 16 dolnych bitow; tablica
    ///     poczatkow kubelkow ma raptem ~200 tys. pozycji, a w samym magazynie
    ///     zostaje tylko ushort na id (2 B) plus dwa inty wspolrzednych (8 B).
    /// Wyszukanie to jeden skok do kubelka i krotkie wyszukiwanie binarne
    /// w ciaglym kawalku pamieci.
    ///
    /// Po zakonczeniu dopisywania magazyn jest tylko do odczytu, wiec wiele
    /// watkow moze go odpytywac jednoczesnie bez zadnych blokad.
    /// </summary>
    internal sealed class NodeStore
    {
        private const int ChunkBits = 24;
        private const int ChunkSize = 1 << ChunkBits;
        private const int ChunkMask = ChunkSize - 1;
        private const int BucketBits = 16;

        private readonly List<ushort[]> low = new List<ushort[]>();
        private readonly List<int[]> lat = new List<int[]>();
        private readonly List<int[]> lon = new List<int[]>();

        private long[] bucketStart = new long[1 << 12];
        private long buckets;
        private long count;
        private long lastId = long.MinValue;

        public long Count { get { return count; } }

        /// <summary>Faktycznie zaalokowana pamiec, nie liczba wezlow razy 10.</summary>
        public long AllocatedBytes { get { return (long)low.Count * ChunkSize * 10L; } }

        public void Append(long id, int latE7, int lonE7)
        {
            if (id <= lastId)
                throw new InvalidDataException(string.Format(
                    "wezel {0} po wezle {1} - plik nie jest posortowany po id", id, lastId));
            lastId = id;

            long b = id >> BucketBits;
            if (b >= buckets) OpenBuckets(b + 1);

            int ci = (int)(count >> ChunkBits);
            int off = (int)(count & ChunkMask);
            if (ci == low.Count)
            {
                low.Add(new ushort[ChunkSize]);
                lat.Add(new int[ChunkSize]);
                lon.Add(new int[ChunkSize]);
            }

            low[ci][off] = (ushort)id;
            lat[ci][off] = latE7;
            lon[ci][off] = lonE7;
            count++;
        }

        /// <summary>Kubelki bez wezlow dostaja ten sam poczatek co nastepny
        /// niepusty - czyli pusty zakres, bez osobnej flagi.</summary>
        private void OpenBuckets(long n)
        {
            if (n > bucketStart.Length)
            {
                long cap = bucketStart.Length;
                while (cap < n) cap *= 2;
                var grown = new long[cap];
                System.Array.Copy(bucketStart, grown, buckets);
                bucketStart = grown;
            }
            for (long i = buckets; i < n; i++) bucketStart[i] = count;
            buckets = n;
        }

        public bool TryGet(long id, out int latE7, out int lonE7)
        {
            latE7 = 0; lonE7 = 0;
            if (id < 0) return false;

            long b = id >> BucketBits;
            if (b >= buckets) return false;

            long lo = bucketStart[b];
            long end = b + 1 < buckets ? bucketStart[b + 1] : count;
            long hi = end;
            ushort key = (ushort)id;

            while (lo < hi)
            {
                long mid = (lo + hi) >> 1;
                ushort v = low[(int)(mid >> ChunkBits)][(int)(mid & ChunkMask)];
                if (v < key) lo = mid + 1; else hi = mid;
            }

            if (lo >= end) return false;
            int ci = (int)(lo >> ChunkBits), off = (int)(lo & ChunkMask);
            if (low[ci][off] != key) return false;

            latE7 = lat[ci][off];
            lonE7 = lon[ci][off];
            return true;
        }
    }
}
