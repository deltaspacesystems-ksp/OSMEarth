using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using OSMRoads;

namespace PlanetPack
{
    internal struct BlockInfo
    {
        public int Gran;
        public long LatOff, LonOff;
        public int StOff, StEnd;
        public bool HasNodes, HasWays, HasRels;
    }

    internal sealed class BatchSpec
    {
        public bool[] Dilated;   // Grid.Deg1Count - wezly, ktore trzymamy (z zapasem)
        public bool[] Core;      // Grid.SquareCount - kwadraty, dla ktorych piszemy paczki
        public RelCapture Rel;   // czlonkowie multipoligonow (null = bez relacji)
    }

    /// <summary>Atrybuty rekordu zrzutu (tag 3) - wspolne dla drog, budynkow i obszarow z relacji.</summary>
    internal sealed class RecAttrs
    {
        public byte Kind;
        public string S1, Roof, Name, Surface, BColour, RColour, RMaterial, BMaterial;
        public float Height = float.NaN;
        public int Levels = -1, Flags, Layer, Lanes, WidthCm, MaxSpeed;
        public sbyte Area = -1;
    }

    internal sealed class PassStats
    {
        public long NodesSeen, NodesKept, Ways, WaysDropped, Records, Places, Blocks;
        public long MaxId;
    }

    /// <summary>Wynik wezlowego bloku. Tablice sa z puli - oddaje je committer.</summary>
    internal sealed class BlockResult
    {
        public long Seq;
        public int NodeCount;
        public long[] Ids;
        public int[] Lat, Lon;

        // Blok z wezlami I drogami naraz: drogi rozwiazuje committer, dopiero
        // po dopisaniu wezlow tego bloku. Trzymamy do tego rozpakowany bufor.
        public byte[] MixedBuf;
        public int MixedLen;
    }

    /// <summary>Bufory jednego watku roboczego - uzywane ponownie dla kazdego bloku.</summary>
    internal sealed class DecodeContext
    {
        public int[] StrOff = new int[8192];
        public int[] StrLen = new int[8192];
        public byte[] KeyCode = new byte[8192];
        public string[] StrCache = new string[8192];
        public int StrCount;

        public readonly List<int> GroupStart = new List<int>();
        public readonly List<int> GroupEnd = new List<int>();

        public long[] Refs = new long[4096];
        public int[] PtLat = new int[4096];
        public int[] PtLon = new int[4096];
        public byte[] Coord = new byte[64 * 1024];

        public long[] CellBuf = new long[256];
        public int[] D1Buf = new int[256];
        public readonly HashSet<long> Touched = new HashSet<long>();

        public readonly MemoryStream Rec = new MemoryStream(1 << 16);
        public readonly BinaryWriter RecW;

        public long[] Hist;
        public long MaxId;
        public long Nodes;

        public DecodeContext()
        {
            RecW = new BinaryWriter(Rec, Encoding.UTF8, true);
        }
    }

    /// <summary>
    /// Dekodowanie PrimitiveBlock z formatu .osm.pbf wprost z bufora.
    ///
    /// Klasyfikacja tagow jest przepisana 1:1 ze starego OsmPack - paczki maja
    /// wyjsc identyczne, bo mod nie powinien zauwazyc, ktore narzedzie je zrobilo.
    /// </summary>
    internal static class BlockDecoder
    {
        private const byte KHighway = 1, KBuilding = 2, KName = 3, KRoof = 4, KHeight = 5,
                           KLevels = 6, KLanduse = 7, KNatural = 8, KLeisure = 9, KPlace = 10,
                           // v4
                           KSurface = 11, KBridge = 12, KTunnel = 13, KLayer = 14, KOneway = 15, KLanes = 16,
                           KWidth = 17, KSidewalk = 18, KSideBoth = 19, KSideLeft = 20, KSideRight = 21,
                           KMaxSpeed = 22, KLit = 23, KBColour = 24, KRColour = 25, KRMaterial = 26,
                           KBMaterial = 27, KBarrier = 28, KJunction = 29,
                           // v5: relacje i lotniska
                           KType = 30, KAeroway = 31;

        private static readonly byte[][] KeyBytes =
        {
            null,
            Encoding.ASCII.GetBytes("highway"),
            Encoding.ASCII.GetBytes("building"),
            Encoding.ASCII.GetBytes("name"),
            Encoding.ASCII.GetBytes("roof:shape"),
            Encoding.ASCII.GetBytes("height"),
            Encoding.ASCII.GetBytes("building:levels"),
            Encoding.ASCII.GetBytes("landuse"),
            Encoding.ASCII.GetBytes("natural"),
            Encoding.ASCII.GetBytes("leisure"),
            Encoding.ASCII.GetBytes("place"),
            Encoding.ASCII.GetBytes("surface"),
            Encoding.ASCII.GetBytes("bridge"),
            Encoding.ASCII.GetBytes("tunnel"),
            Encoding.ASCII.GetBytes("layer"),
            Encoding.ASCII.GetBytes("oneway"),
            Encoding.ASCII.GetBytes("lanes"),
            Encoding.ASCII.GetBytes("width"),
            Encoding.ASCII.GetBytes("sidewalk"),
            Encoding.ASCII.GetBytes("sidewalk:both"),
            Encoding.ASCII.GetBytes("sidewalk:left"),
            Encoding.ASCII.GetBytes("sidewalk:right"),
            Encoding.ASCII.GetBytes("maxspeed"),
            Encoding.ASCII.GetBytes("lit"),
            Encoding.ASCII.GetBytes("building:colour"),
            Encoding.ASCII.GetBytes("roof:colour"),
            Encoding.ASCII.GetBytes("roof:material"),
            Encoding.ASCII.GetBytes("building:material"),
            Encoding.ASCII.GetBytes("barrier"),
            Encoding.ASCII.GetBytes("junction"),
            Encoding.ASCII.GetBytes("type"),
            Encoding.ASCII.GetBytes("aeroway")
        };

        // ------------------------------------------------------------------
        //  Blob -> rozpakowany PrimitiveBlock
        // ------------------------------------------------------------------

        /// <summary>Zwraca bufor z puli - wolajacy oddaje go przez ArrayPool.</summary>
        public static byte[] Inflate(byte[] blob, int len, out int rawLen)
        {
            int p = 0, rawOff = -1, rawEnd = -1, zOff = -1, zEnd = -1;
            rawLen = -1;

            while (p < len)
            {
                ulong tag = Proto.Varint(blob, ref p);
                int f = (int)(tag >> 3), wire = (int)(tag & 7);
                if (f == 1 && wire == 2) { int l = (int)Proto.Varint(blob, ref p); rawOff = p; rawEnd = p + l; p += l; }
                else if (f == 2 && wire == 0) rawLen = (int)Proto.Varint(blob, ref p);
                else if (f == 3 && wire == 2) { int l = (int)Proto.Varint(blob, ref p); zOff = p; zEnd = p + l; p += l; }
                else if (f == 4 || f == 6 || f == 7)
                    throw new NotSupportedException("kompresja inna niz zlib (pole " + f + ") - nieobslugiwana");
                else Proto.Skip(blob, ref p, wire);
            }

            if (rawOff >= 0)
            {
                rawLen = rawEnd - rawOff;
                byte[] outRaw = ArrayPool<byte>.Shared.Rent(rawLen);
                Buffer.BlockCopy(blob, rawOff, outRaw, 0, rawLen);
                return outRaw;
            }

            if (zOff < 0 || rawLen < 0) throw new InvalidDataException("blob bez danych");

            byte[] outZ = ArrayPool<byte>.Shared.Rent(rawLen);
            using (var ms = new MemoryStream(blob, zOff, zEnd - zOff, false))
            using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionMode.Decompress))
            {
                int done = 0;
                while (done < rawLen)
                {
                    int n = z.Read(outZ, done, rawLen - done);
                    if (n <= 0) throw new InvalidDataException("zlib urwany");
                    done += n;
                }
            }
            return outZ;
        }

        public static BlockInfo ReadInfo(byte[] b, int len, DecodeContext c)
        {
            var info = new BlockInfo { Gran = 100, StOff = -1, StEnd = -1 };
            c.GroupStart.Clear();
            c.GroupEnd.Clear();

            int p = 0;
            while (p < len)
            {
                ulong tag = Proto.Varint(b, ref p);
                int f = (int)(tag >> 3), wire = (int)(tag & 7);
                if (f == 1 && wire == 2) { int l = (int)Proto.Varint(b, ref p); info.StOff = p; info.StEnd = p + l; p += l; }
                else if (f == 2 && wire == 2) { int l = (int)Proto.Varint(b, ref p); c.GroupStart.Add(p); c.GroupEnd.Add(p + l); p += l; }
                else if (f == 17 && wire == 0) info.Gran = (int)Proto.Varint(b, ref p);
                else if (f == 19 && wire == 0) info.LatOff = (long)Proto.Varint(b, ref p);
                else if (f == 20 && wire == 0) info.LonOff = (long)Proto.Varint(b, ref p);
                else Proto.Skip(b, ref p, wire);
            }

            // Plytki przeglad grup: jaki to blok. Tresci jeszcze nie dotykamy.
            for (int g = 0; g < c.GroupStart.Count; g++)
            {
                int q = c.GroupStart[g], end = c.GroupEnd[g];
                while (q < end)
                {
                    ulong tag = Proto.Varint(b, ref q);
                    int f = (int)(tag >> 3), wire = (int)(tag & 7);
                    if (f == 1 || f == 2) info.HasNodes = true;
                    else if (f == 3) info.HasWays = true;
                    else if (f == 4) info.HasRels = true;
                    Proto.Skip(b, ref q, wire);
                }
            }
            return info;
        }

        /// <summary>1e-9 stopnia z pliku -> 1e-7 stopnia paczki. Szybka sciezka dla
        /// domyslnej ziarnistosci 100 bez przesuniecia, czyli dla calej planety.</summary>
        private static int Conv(long v, int gran, long off)
        {
            if (gran == 100 && off == 0) return (int)v;
            return (int)Math.Round((off + (double)gran * v) / 100.0);
        }

        // ------------------------------------------------------------------
        //  Tablica stringow
        // ------------------------------------------------------------------

        private static void ReadStrings(byte[] b, BlockInfo info, DecodeContext c)
        {
            int n = 0;
            if (info.StOff >= 0)
            {
                int p = info.StOff;
                while (p < info.StEnd)
                {
                    ulong tag = Proto.Varint(b, ref p);
                    int f = (int)(tag >> 3), wire = (int)(tag & 7);
                    if (f == 1 && wire == 2)
                    {
                        int l = (int)Proto.Varint(b, ref p);
                        if (n == c.StrOff.Length)
                        {
                            Array.Resize(ref c.StrOff, n * 2);
                            Array.Resize(ref c.StrLen, n * 2);
                            Array.Resize(ref c.KeyCode, n * 2);
                            Array.Resize(ref c.StrCache, n * 2);
                        }
                        c.StrOff[n] = p;
                        c.StrLen[n] = l;
                        n++;
                        p += l;
                    }
                    else Proto.Skip(b, ref p, wire);
                }
            }

            c.StrCount = n;
            for (int i = 0; i < n; i++)
            {
                c.StrCache[i] = null;
                c.KeyCode[i] = MatchKey(b, c.StrOff[i], c.StrLen[i]);
            }
        }

        private static byte MatchKey(byte[] b, int off, int len)
        {
            for (byte k = 1; k < KeyBytes.Length; k++)
            {
                byte[] kb = KeyBytes[k];
                if (kb.Length != len) continue;
                if (new ReadOnlySpan<byte>(b, off, len).SequenceEqual(kb)) return k;
            }
            return 0;
        }

        private static string Str(byte[] b, DecodeContext c, int i)
        {
            if (i < 0 || i >= c.StrCount) return null;
            string s = c.StrCache[i];
            if (s == null)
            {
                s = Encoding.UTF8.GetString(b, c.StrOff[i], c.StrLen[i]);
                c.StrCache[i] = s;
            }
            return s;
        }

        // ------------------------------------------------------------------
        //  Przebieg rozpoznawczy: histogram wezlow na stopien
        // ------------------------------------------------------------------

        public static void ScanNodes(byte[] b, BlockInfo info, DecodeContext c)
        {
            for (int g = 0; g < c.GroupStart.Count; g++)
            {
                int q = c.GroupStart[g], end = c.GroupEnd[g];
                while (q < end)
                {
                    ulong tag = Proto.Varint(b, ref q);
                    int f = (int)(tag >> 3), wire = (int)(tag & 7);
                    if (f == 2 && wire == 2)
                    {
                        int l = (int)Proto.Varint(b, ref q);
                        ScanDense(b, q, q + l, info, c);
                        q += l;
                    }
                    else if (f == 1 && wire == 2)
                    {
                        int l = (int)Proto.Varint(b, ref q);
                        long id; int la, lo;
                        if (ReadPlainNode(b, q, q + l, info, out id, out la, out lo, null, null))
                        {
                            c.Hist[Grid.Deg1OfE7(la, lo)]++;
                            if (id > c.MaxId) c.MaxId = id;
                            c.Nodes++;
                        }
                        q += l;
                    }
                    else Proto.Skip(b, ref q, wire);
                }
            }
        }

        private static void ScanDense(byte[] b, int start, int end, BlockInfo info, DecodeContext c)
        {
            int pId = -1, eId = 0, pLat = -1, eLat = 0, pLon = -1, eLon = 0;
            int p = start;
            while (p < end)
            {
                ulong tag = Proto.Varint(b, ref p);
                int f = (int)(tag >> 3), wire = (int)(tag & 7);
                if (wire == 2 && (f == 1 || f == 8 || f == 9))
                {
                    int l = (int)Proto.Varint(b, ref p);
                    if (f == 1) { pId = p; eId = p + l; }
                    else if (f == 8) { pLat = p; eLat = p + l; }
                    else { pLon = p; eLon = p + l; }
                    p += l;
                }
                else Proto.Skip(b, ref p, wire);
            }
            if (pId < 0 || pLat < 0 || pLon < 0) return;

            long id = 0, la = 0, lo = 0;
            while (pId < eId)
            {
                id += Proto.ZigZag(Proto.Varint(b, ref pId));
                la += Proto.ZigZag(Proto.Varint(b, ref pLat));
                lo += Proto.ZigZag(Proto.Varint(b, ref pLon));

                c.Hist[Grid.Deg1OfE7(Conv(la, info.Gran, info.LatOff),
                                     Conv(lo, info.Gran, info.LonOff))]++;
                if (id > c.MaxId) c.MaxId = id;
                c.Nodes++;
            }
        }

        // ------------------------------------------------------------------
        //  Przebieg wlasciwy: wezly
        // ------------------------------------------------------------------

        public static BlockResult FilterNodes(byte[] b, BlockInfo info, DecodeContext c,
                                              BatchSpec spec, Spill spill, PassStats st)
        {
            ReadStrings(b, info, c);

            var r = new BlockResult
            {
                Ids = ArrayPool<long>.Shared.Rent(8192),
                Lat = ArrayPool<int>.Shared.Rent(8192),
                Lon = ArrayPool<int>.Shared.Rent(8192)
            };

            for (int g = 0; g < c.GroupStart.Count; g++)
            {
                int q = c.GroupStart[g], end = c.GroupEnd[g];
                while (q < end)
                {
                    ulong tag = Proto.Varint(b, ref q);
                    int f = (int)(tag >> 3), wire = (int)(tag & 7);
                    if (f == 2 && wire == 2)
                    {
                        int l = (int)Proto.Varint(b, ref q);
                        FilterDense(b, q, q + l, info, c, spec, spill, st, r);
                        q += l;
                    }
                    else if (f == 1 && wire == 2)
                    {
                        int l = (int)Proto.Varint(b, ref q);
                        FilterPlain(b, q, q + l, info, c, spec, spill, st, r);
                        q += l;
                    }
                    else Proto.Skip(b, ref q, wire);
                }
            }
            return r;
        }

        private static void Keep(BlockResult r, long id, int la, int lo)
        {
            if (r.NodeCount == r.Ids.Length)
            {
                int cap = r.Ids.Length * 2;
                long[] ni = ArrayPool<long>.Shared.Rent(cap);
                int[] nla = ArrayPool<int>.Shared.Rent(cap);
                int[] nlo = ArrayPool<int>.Shared.Rent(cap);
                Array.Copy(r.Ids, ni, r.NodeCount);
                Array.Copy(r.Lat, nla, r.NodeCount);
                Array.Copy(r.Lon, nlo, r.NodeCount);
                ArrayPool<long>.Shared.Return(r.Ids);
                ArrayPool<int>.Shared.Return(r.Lat);
                ArrayPool<int>.Shared.Return(r.Lon);
                r.Ids = ni; r.Lat = nla; r.Lon = nlo;
            }
            r.Ids[r.NodeCount] = id;
            r.Lat[r.NodeCount] = la;
            r.Lon[r.NodeCount] = lo;
            r.NodeCount++;
        }

        private static void FilterDense(byte[] b, int start, int end, BlockInfo info, DecodeContext c,
                                        BatchSpec spec, Spill spill, PassStats st, BlockResult r)
        {
            int pId = -1, eId = 0, pLat = -1, pLon = -1, pKv = -1, eKv = 0;
            int p = start;
            while (p < end)
            {
                ulong tag = Proto.Varint(b, ref p);
                int f = (int)(tag >> 3), wire = (int)(tag & 7);
                if (wire == 2 && (f == 1 || f == 8 || f == 9 || f == 10))
                {
                    int l = (int)Proto.Varint(b, ref p);
                    if (f == 1) { pId = p; eId = p + l; }
                    else if (f == 8) pLat = p;
                    else if (f == 9) pLon = p;
                    else { pKv = p; eKv = p + l; }
                    p += l;
                }
                else Proto.Skip(b, ref p, wire);
            }
            if (pId < 0 || pLat < 0 || pLon < 0) return;

            long id = 0, la = 0, lo = 0;
            int seen = 0;
            while (pId < eId)
            {
                id += Proto.ZigZag(Proto.Varint(b, ref pId));
                la += Proto.ZigZag(Proto.Varint(b, ref pLat));
                lo += Proto.ZigZag(Proto.Varint(b, ref pLon));
                seen++;

                // keys_vals trzeba przejsc dla KAZDEGO wezla, zeby wskaznik
                // zostal zsynchronizowany z kolejnym - nawet jesli wezel odpadnie.
                int placeV = -1, nameV = -1, natV = -1;
                if (pKv >= 0 && pKv < eKv)
                {
                    while (true)
                    {
                        int k = (int)Proto.Varint(b, ref pKv);
                        if (k == 0) break;
                        int v = (int)Proto.Varint(b, ref pKv);
                        if (k < c.StrCount)
                        {
                            byte code = c.KeyCode[k];
                            if (code == KPlace) placeV = v;
                            else if (code == KName) nameV = v;
                            else if (code == KNatural) natV = v;
                        }
                    }
                }

                int latE7 = Conv(la, info.Gran, info.LatOff);
                int lonE7 = Conv(lo, info.Gran, info.LonOff);
                if (!spec.Dilated[Grid.Deg1OfE7(latE7, lonE7)]) continue;

                Keep(r, id, latE7, lonE7);
                if (placeV >= 0 && nameV >= 0)
                    MaybePlace(b, c, spec, spill, st, id, latE7, lonE7, placeV, nameV);
                if (natV >= 0 && Str(b, c, natV) == "tree")
                    MaybeTree(c, spec, spill, st, id, latE7, lonE7);
            }

            Interlocked.Add(ref st.NodesSeen, seen);
        }

        private static void FilterPlain(byte[] b, int start, int end, BlockInfo info, DecodeContext c,
                                        BatchSpec spec, Spill spill, PassStats st, BlockResult r)
        {
            long id; int la, lo;
            int placeV = -1, nameV = -1;
            int[] tags = new int[2] { -1, -1 };
            if (!ReadPlainNode(b, start, end, info, out id, out la, out lo, c, tags)) return;
            placeV = tags[0]; nameV = tags[1];

            Interlocked.Increment(ref st.NodesSeen);
            if (!spec.Dilated[Grid.Deg1OfE7(la, lo)]) return;

            Keep(r, id, la, lo);
            if (placeV >= 0 && nameV >= 0)
                MaybePlace(b, c, spec, spill, st, id, la, lo, placeV, nameV);
        }

        /// <summary>Zwykly (nie-dense) Node. W planecie praktycznie nie wystepuje,
        /// ale ekstrakty z innych narzedzi potrafia go uzywac.</summary>
        private static bool ReadPlainNode(byte[] b, int start, int end, BlockInfo info,
                                          out long id, out int latE7, out int lonE7,
                                          DecodeContext c, int[] placeName)
        {
            id = 0; latE7 = 0; lonE7 = 0;
            long la = 0, lo = 0;
            bool hasId = false, hasLat = false, hasLon = false;
            int kS = -1, kE = 0, vS = -1;

            int p = start;
            while (p < end)
            {
                ulong tag = Proto.Varint(b, ref p);
                int f = (int)(tag >> 3), wire = (int)(tag & 7);
                if (f == 1 && wire == 0) { id = Proto.ZigZag(Proto.Varint(b, ref p)); hasId = true; }
                else if (f == 8 && wire == 0) { la = Proto.ZigZag(Proto.Varint(b, ref p)); hasLat = true; }
                else if (f == 9 && wire == 0) { lo = Proto.ZigZag(Proto.Varint(b, ref p)); hasLon = true; }
                else if (f == 2 && wire == 2) { int l = (int)Proto.Varint(b, ref p); kS = p; kE = p + l; p += l; }
                else if (f == 3 && wire == 2) { int l = (int)Proto.Varint(b, ref p); vS = p; p += l; }
                else Proto.Skip(b, ref p, wire);
            }
            if (!hasId || !hasLat || !hasLon) return false;

            latE7 = Conv(la, info.Gran, info.LatOff);
            lonE7 = Conv(lo, info.Gran, info.LonOff);

            if (c != null && placeName != null && kS >= 0 && vS >= 0)
            {
                int pk = kS, pv = vS;
                while (pk < kE)
                {
                    int k = (int)Proto.Varint(b, ref pk);
                    int v = (int)Proto.Varint(b, ref pv);
                    if (k >= c.StrCount) continue;
                    if (c.KeyCode[k] == KPlace) placeName[0] = v;
                    else if (c.KeyCode[k] == KName) placeName[1] = v;
                }
            }
            return true;
        }

        /// <summary>v4: pojedyncze drzewo (natural=tree) - rekord 4 zrzutu: id, wspolrzedne, komorka.</summary>
        private static void MaybeTree(DecodeContext c, BatchSpec spec, Spill spill, PassStats st,
                                      long id, int latE7, int lonE7)
        {
            long cell = Grid.CellOfE7(latE7, lonE7);
            int d1 = Grid.Deg1OfCell(cell);
            if (!spec.Core[Grid.SquareOfDeg1(d1)]) return;
            c.Rec.SetLength(0);
            BinaryWriter w = c.RecW;
            w.Write((byte)4);
            w.Write(id);
            w.Write(latE7);
            w.Write(lonE7);
            w.Write(cell);
            w.Flush();
            spill.Append(d1, c.Rec.GetBuffer(), (int)c.Rec.Length);
            Interlocked.Increment(ref st.Places);
        }

        private static void MaybePlace(byte[] b, DecodeContext c, BatchSpec spec, Spill spill,
                                       PassStats st, long id, int latE7, int lonE7,
                                       int placeV, int nameV)
        {
            string kind = Str(b, c, placeV);
            switch (kind)
            {
                case "city": case "town": case "village":
                case "hamlet": case "suburb": break;
                default: return;
            }

            string name = Str(b, c, nameV);
            if (string.IsNullOrEmpty(name)) return;

            long cell = Grid.CellOfE7(latE7, lonE7);
            int d1 = Grid.Deg1OfCell(cell);
            if (!spec.Core[Grid.SquareOfDeg1(d1)]) return;   // zapas to nie nasz obszar

            c.Rec.SetLength(0);
            BinaryWriter w = c.RecW;
            w.Write((byte)2);
            w.Write(id);
            w.Write(name);
            w.Write(kind);
            w.Write(latE7);
            w.Write(lonE7);
            w.Write(cell);
            w.Flush();

            spill.Append(d1, c.Rec.GetBuffer(), (int)c.Rec.Length);
            Interlocked.Increment(ref st.Places);
        }

        // ------------------------------------------------------------------
        //  Przebieg wlasciwy: drogi (magazyn wezlow jest juz zamrozony)
        // ------------------------------------------------------------------

        public static void ResolveWays(byte[] b, int len, DecodeContext c, NodeStore store,
                                       Spill spill, BatchSpec spec, PassStats st)
        {
            BlockInfo info = ReadInfo(b, len, c);
            ReadStrings(b, info, c);

            for (int g = 0; g < c.GroupStart.Count; g++)
            {
                int q = c.GroupStart[g], end = c.GroupEnd[g];
                while (q < end)
                {
                    ulong tag = Proto.Varint(b, ref q);
                    int f = (int)(tag >> 3), wire = (int)(tag & 7);
                    if (f == 3 && wire == 2)
                    {
                        int l = (int)Proto.Varint(b, ref q);
                        ResolveWay(b, q, q + l, c, store, spill, spec, st);
                        q += l;
                    }
                    else Proto.Skip(b, ref q, wire);
                }
            }
        }

        private static void ResolveWay(byte[] b, int start, int end, DecodeContext c, NodeStore store,
                                       Spill spill, BatchSpec spec, PassStats st)
        {
            long id = 0;
            int kS = -1, kE = 0, vS = -1, rS = -1, rE = 0;

            int p = start;
            while (p < end)
            {
                ulong tag = Proto.Varint(b, ref p);
                int f = (int)(tag >> 3), wire = (int)(tag & 7);
                if (f == 1 && wire == 0) id = (long)Proto.Varint(b, ref p);
                else if (f == 2 && wire == 2) { int l = (int)Proto.Varint(b, ref p); kS = p; kE = p + l; p += l; }
                else if (f == 3 && wire == 2) { int l = (int)Proto.Varint(b, ref p); vS = p; p += l; }
                else if (f == 8 && wire == 2) { int l = (int)Proto.Varint(b, ref p); rS = p; rE = p + l; p += l; }
                else Proto.Skip(b, ref p, wire);
            }

            // Stary konwerter: brak tagow albo mniej niz dwa wezly -> pomin.
            // Wyjatek: czlonek multipoligonu (zewnetrzne linie lasu zwykle nie maja wlasnych tagow).
            if (rS < 0) return;
            bool member = spec.Rel != null && spec.Rel.IsMember(id);
            if ((kS < 0 || vS < 0) && !member) return;

            int hw = -1, bld = -1, nm = -1, roof = -1, ht = -1, lvl = -1, lu = -1, nat = -1, lei = -1;
            int sfc = -1, brg = -1, tun = -1, lay = -1, ow = -1, lan = -1, wid = -1, sw = -1, swb = -1, swl = -1, swr = -1,
                msp = -1, lit = -1, bcol = -1, rcol = -1, rmat = -1, bmat = -1, bar = -1, jun = -1, aer = -1;
            int pk = kS, pv = vS;
            while (kS >= 0 && vS >= 0 && pk < kE)
            {
                int k = (int)Proto.Varint(b, ref pk);
                int v = (int)Proto.Varint(b, ref pv);
                if (k >= c.StrCount) continue;
                switch (c.KeyCode[k])
                {
                    case KHighway: hw = v; break;
                    case KBuilding: bld = v; break;
                    case KName: nm = v; break;
                    case KRoof: roof = v; break;
                    case KHeight: ht = v; break;
                    case KLevels: lvl = v; break;
                    case KLanduse: lu = v; break;
                    case KNatural: nat = v; break;
                    case KLeisure: lei = v; break;
                    case KSurface: sfc = v; break;
                    case KBridge: brg = v; break;
                    case KTunnel: tun = v; break;
                    case KLayer: lay = v; break;
                    case KOneway: ow = v; break;
                    case KLanes: lan = v; break;
                    case KWidth: wid = v; break;
                    case KSidewalk: sw = v; break;
                    case KSideBoth: swb = v; break;
                    case KSideLeft: swl = v; break;
                    case KSideRight: swr = v; break;
                    case KMaxSpeed: msp = v; break;
                    case KLit: lit = v; break;
                    case KBColour: bcol = v; break;
                    case KRColour: rcol = v; break;
                    case KRMaterial: rmat = v; break;
                    case KBMaterial: bmat = v; break;
                    case KBarrier: bar = v; break;
                    case KJunction: jun = v; break;
                    case KAeroway: aer = v; break;
                }
            }

            // --- klasyfikacja: ta sama kolejnosc regul co w OsmPack.Classify ---
            byte kind = 0;
            string s1 = null, roofS = null;
            float height = float.NaN;
            int levels = -1;
            sbyte area = -1;

            if (hw >= 0)
            {
                string v = Str(b, c, hw);
                if (IsWantedRoad(v)) { kind = PackFormat.KindRoad; s1 = v; }
            }
            if (kind == 0 && aer >= 0)
            {
                // lotniska: pasy i drogi kolowania jak drogi (z szerokoscia i nawierzchnia),
                // plyty postojowe jak utwardzony obszar
                string v = Str(b, c, aer);
                if (v == "runway" || v == "taxiway" || v == "stopway") { kind = PackFormat.KindRoad; s1 = v == "stopway" ? "runway" : v; }
                else if (v == "apron") { kind = PackFormat.KindArea; area = (sbyte)AreaKind.Pavement; }
            }
            if (kind == 0 && bld >= 0)
            {
                string v = Str(b, c, bld);
                if (v != "no")
                {
                    kind = PackFormat.KindBuilding;
                    s1 = v;
                    roofS = roof >= 0 ? Str(b, c, roof) : null;
                    height = ParseMeters(ht >= 0 ? Str(b, c, ht) : null);
                    levels = ParseInt(lvl >= 0 ? Str(b, c, lvl) : null);
                }
            }
            if (kind == 0)
            {
                AreaKind ak = AreaKind.None;
                if (lu >= 0) ak = AreaKinds.From("landuse", Str(b, c, lu));
                if (ak == AreaKind.None && nat >= 0) ak = AreaKinds.From("natural", Str(b, c, nat));
                if (ak == AreaKind.None && lei >= 0) ak = AreaKinds.From("leisure", Str(b, c, lei));
                if (ak != AreaKind.None) { kind = PackFormat.KindArea; area = (sbyte)ak; }
            }
            if (kind == 0 && bar >= 0)
            {
                string v = Str(b, c, bar);
                switch (v)
                {
                    case "fence": case "wall": case "hedge": case "retaining_wall": case "city_wall":
                    case "guard_rail": case "wire_fence": case "wood_fence": case "hedge_bank":
                        kind = PackFormat.KindBarrier; s1 = v; break;
                }
            }
            if (kind == 0 && !member) return;

            // --- v4: atrybuty drogi / budynku ---
            string surface = null, bColour = null, rColour = null, rMaterial = null, bMaterial = null;
            int flags = 0, layer = 0, lanes = 0, widthCm = 0, maxSpeed = 0;
            if (kind == PackFormat.KindRoad)
            {
                surface = sfc >= 0 ? Str(b, c, sfc) : null;
                string bv = brg >= 0 ? Str(b, c, brg) : null;
                if (bv != null && bv != "no") flags |= PackFormat.RBridge;
                string tv = tun >= 0 ? Str(b, c, tun) : null;
                if (tv == "yes" || tv == "building_passage" || tv == "covered") flags |= PackFormat.RTunnel;
                string ov = ow >= 0 ? Str(b, c, ow) : null;
                if (ov == "yes" || ov == "1" || ov == "true") flags |= PackFormat.ROneway;
                else if (ov == "-1" || ov == "reverse") flags |= PackFormat.ROnewayRev;
                else if (ov == null && ((jun >= 0 && Str(b, c, jun) == "roundabout") || s1 == "motorway" || s1 == "motorway_link"))
                    flags |= PackFormat.ROneway;
                if (lit >= 0 && Str(b, c, lit) == "yes") flags |= PackFormat.RLit;
                flags |= Sidewalk(sw >= 0 ? Str(b, c, sw) : null);
                flags |= Sidewalk(swb >= 0 ? Str(b, c, swb) : null);
                if (swl >= 0) { string x = Str(b, c, swl); if (x == "yes") flags |= PackFormat.RSideLeft; else if (x == "separate") flags |= PackFormat.RSideSeparate; }
                if (swr >= 0) { string x = Str(b, c, swr); if (x == "yes") flags |= PackFormat.RSideRight; else if (x == "separate") flags |= PackFormat.RSideSeparate; }
                layer = ParseInt(lay >= 0 ? Str(b, c, lay) : null);
                if (layer == -1 && lay < 0) layer = 0;
                layer = Math.Max(-5, Math.Min(5, layer));
                lanes = Math.Max(0, ParseInt(lan >= 0 ? Str(b, c, lan) : null));
                float wm = ParseMeters(wid >= 0 ? Str(b, c, wid) : null);
                if (!float.IsNaN(wm) && wm > 0.5f && wm < 120f) widthCm = (int)(wm * 100f);
                maxSpeed = Speed(msp >= 0 ? Str(b, c, msp) : null);
            }
            else if (kind == PackFormat.KindBuilding)
            {
                bColour = bcol >= 0 ? Str(b, c, bcol) : null;
                rColour = rcol >= 0 ? Str(b, c, rcol) : null;
                rMaterial = rmat >= 0 ? Str(b, c, rmat) : null;
                bMaterial = bmat >= 0 ? Str(b, c, bmat) : null;
            }

            // --- wezly ---
            int n = Proto.CountVarints(b, rS, rE);
            if (n < 2) return;
            if (n > c.Refs.Length)
            {
                int cap = c.Refs.Length;
                while (cap < n) cap *= 2;
                c.Refs = new long[cap];
                c.PtLat = new int[cap];
                c.PtLon = new int[cap];
                c.Coord = new byte[cap * 20];
            }

            long rid = 0;
            int pr = rS;
            for (int i = 0; i < n; i++)
            {
                rid += Proto.ZigZag(Proto.Varint(b, ref pr));
                c.Refs[i] = rid;
            }

            // Szybkie odrzucenie: droga spoza pasa nie ma nawet pierwszego wezla.
            // To jest los ~80% drog w kazdym przebiegu, wiec liczy sie kazdy krok.
            if (!store.TryGet(c.Refs[0], out c.PtLat[0], out c.PtLon[0])) return;

            for (int i = 1; i < n; i++)
            {
                if (!store.TryGet(c.Refs[i], out c.PtLat[i], out c.PtLon[i]))
                {
                    Interlocked.Increment(ref st.WaysDropped);
                    return;
                }
            }

            if (member) spec.Rel.Capture(id, c.Refs[0], c.Refs[n - 1], n, c.PtLat, c.PtLon);
            if (kind == 0) return;

            var a = new RecAttrs
            {
                Kind = kind, S1 = s1, Roof = roofS, Name = nm >= 0 ? Str(b, c, nm) : null,
                Height = height, Levels = levels, Area = area, Surface = surface, Flags = flags, Layer = layer,
                Lanes = lanes, WidthCm = widthCm, MaxSpeed = maxSpeed,
                BColour = bColour, RColour = rColour, RMaterial = rMaterial, BMaterial = bMaterial
            };
            if (Emit(c, spill, spec, st, id, n, a)) Interlocked.Increment(ref st.Ways);
        }

        /// <summary>
        /// Zapis obiektu (punkty w c.PtLat/c.PtLon) do zrzutu: jeden rekord na stopien,
        /// kazdy z lista komorek 0.05 st., ktorych dotyka. Dla obszarow takze komorki
        /// CALKIEM wewnatrz (bez zadnego wierzcholka) - inaczej srodek wielkiego lasu
        /// nie trafial do zadnej komorki i kafle w glebi puszczy byly puste.
        /// </summary>
        internal static bool Emit(DecodeContext c, Spill spill, BatchSpec spec, PassStats st, long id, int n, RecAttrs a)
        {
            // --- komorki, ktorych dotyka ---
            c.Touched.Clear();
            for (int i = 0; i < n; i++)
                c.Touched.Add(Grid.CellOfE7(c.PtLat[i], c.PtLon[i]));
            if (a.Kind == PackFormat.KindArea && n >= 4 && c.PtLat[0] == c.PtLat[n - 1] && c.PtLon[0] == c.PtLon[n - 1])
                FillInterior(c, n);

            int nCells = 0;
            foreach (long key in c.Touched)
            {
                int d1 = Grid.Deg1OfCell(key);
                if (!spec.Core[Grid.SquareOfDeg1(d1)]) continue;
                if (nCells == c.CellBuf.Length)
                {
                    Array.Resize(ref c.CellBuf, nCells * 2);
                    Array.Resize(ref c.D1Buf, nCells * 2);
                }
                c.CellBuf[nCells] = key;
                c.D1Buf[nCells] = d1;
                nCells++;
            }
            if (nCells == 0) return false;

            // --- geometria raz, dla wszystkich rekordow tej drogi ---
            if (c.Coord.Length < n * 20) c.Coord = new byte[n * 20];
            int cp = 0;
            long pLat = 0, pLon = 0;
            for (int i = 0; i < n; i++)
            {
                Proto.PutZigZag(c.Coord, ref cp, c.PtLat[i] - pLat);
                Proto.PutZigZag(c.Coord, ref cp, c.PtLon[i] - pLon);
                pLat = c.PtLat[i];
                pLon = c.PtLon[i];
            }

            // --- jeden rekord na stopien: kazdy plik zrzutu dostaje tylko swoje komorki ---
            for (int i = 0; i < nCells; i++)
            {
                int d = c.D1Buf[i];
                if (d == int.MinValue) continue;

                int cnt = 0;
                for (int j = i; j < nCells; j++)
                    if (c.D1Buf[j] == d) cnt++;
                cnt = Math.Min(cnt, ushort.MaxValue);

                c.Rec.SetLength(0);
                BinaryWriter w = c.RecW;
                w.Write((byte)3);                 // v4: rekord drogi/budynku z atrybutami
                w.Write(id);
                w.Write(a.Kind);
                w.Write(a.S1 ?? "");
                w.Write(a.Roof ?? "");
                w.Write(a.Name ?? "");
                w.Write(a.Height);
                w.Write(a.Levels);
                w.Write(a.Area);
                w.Write(a.Surface ?? "");
                w.Write(a.Flags);
                w.Write(a.Layer);
                w.Write(a.Lanes);
                w.Write(a.WidthCm);
                w.Write(a.MaxSpeed);
                w.Write(a.BColour ?? "");
                w.Write(a.RColour ?? "");
                w.Write(a.RMaterial ?? "");
                w.Write(a.BMaterial ?? "");
                w.Write((ushort)cnt);
                int put = 0;
                for (int j = i; j < nCells; j++)
                {
                    if (c.D1Buf[j] != d) continue;
                    if (put < cnt) { w.Write(c.CellBuf[j]); put++; }
                    c.D1Buf[j] = int.MinValue;
                }
                w.Write(n);
                w.Write(cp);
                w.Write(c.Coord, 0, cp);
                w.Flush();

                spill.Append(d, c.Rec.GetBuffer(), (int)c.Rec.Length);
                Interlocked.Increment(ref st.Records);
            }
            return true;
        }

        /// <summary>Dokleja do c.Touched komorki lezace wewnatrz zamknietego pierscienia:
        /// dla kazdego rzedu komorek przeciecia krawedzi z rownolegnikiem srodka rzedu,
        /// pary przeciec wyznaczaja odcinki wnetrza.</summary>
        private static void FillInterior(DecodeContext c, int n)
        {
            double cell = PackFormat.CellDeg, sc = PackFormat.CoordScale;
            int minLat = int.MaxValue, maxLat = int.MinValue;
            for (int i = 0; i < n; i++)
            {
                if (c.PtLat[i] < minLat) minLat = c.PtLat[i];
                if (c.PtLat[i] > maxLat) maxLat = c.PtLat[i];
            }
            int r0 = (int)Math.Floor(minLat / sc / cell), r1 = (int)Math.Floor(maxLat / sc / cell);
            var xs = new List<double>();
            for (int r = r0; r <= r1; r++)
            {
                double yl = (r + 0.5) * cell * sc;
                xs.Clear();
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double ya = c.PtLat[j], yb = c.PtLat[i];
                    if ((ya > yl) == (yb > yl)) continue;
                    double t = (yl - ya) / (yb - ya);
                    xs.Add(c.PtLon[j] + t * (c.PtLon[i] - c.PtLon[j]));
                }
                if (xs.Count < 2) continue;
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int ca = (int)Math.Floor(xs[k] / sc / cell), cb = (int)Math.Floor(xs[k + 1] / sc / cell);
                    if (cb - ca > 4000) continue;          // bzdura (np. przez antypoludnik)
                    for (int q = ca; q <= cb; q++) c.Touched.Add(PackFormat.CellKey(r, q));
                }
            }
        }

        // ------------------------------------------------------------------
        //  Relacje (multipoligony) - RelScan
        // ------------------------------------------------------------------

        public static void DecodeRelations(byte[] b, int len, DecodeContext c, List<RelInfo> outList)
        {
            BlockInfo info = ReadInfo(b, len, c);
            if (!info.HasRels) return;
            ReadStrings(b, info, c);
            for (int g = 0; g < c.GroupStart.Count; g++)
            {
                int q = c.GroupStart[g], end = c.GroupEnd[g];
                while (q < end)
                {
                    ulong tag = Proto.Varint(b, ref q);
                    int f = (int)(tag >> 3), wire = (int)(tag & 7);
                    if (f == 4 && wire == 2)
                    {
                        int l = (int)Proto.Varint(b, ref q);
                        RelInfo r = DecodeRelation(b, q, q + l, c);
                        if (r != null) outList.Add(r);
                        q += l;
                    }
                    else Proto.Skip(b, ref q, wire);
                }
            }
        }

        private static RelInfo DecodeRelation(byte[] b, int start, int end, DecodeContext c)
        {
            long id = 0;
            int kS = -1, kE = 0, vS = -1, roS = -1, roE = 0, mS = -1, mE = 0, tS = -1, tE = 0;
            int p = start;
            while (p < end)
            {
                ulong tag = Proto.Varint(b, ref p);
                int f = (int)(tag >> 3), wire = (int)(tag & 7);
                if (f == 1 && wire == 0) id = (long)Proto.Varint(b, ref p);
                else if (f == 2 && wire == 2) { int l = (int)Proto.Varint(b, ref p); kS = p; kE = p + l; p += l; }
                else if (f == 3 && wire == 2) { int l = (int)Proto.Varint(b, ref p); vS = p; p += l; }
                else if (f == 8 && wire == 2) { int l = (int)Proto.Varint(b, ref p); roS = p; roE = p + l; p += l; }
                else if (f == 9 && wire == 2) { int l = (int)Proto.Varint(b, ref p); mS = p; mE = p + l; p += l; }
                else if (f == 10 && wire == 2) { int l = (int)Proto.Varint(b, ref p); tS = p; tE = p + l; p += l; }
                else Proto.Skip(b, ref p, wire);
            }
            if (kS < 0 || vS < 0 || mS < 0 || tS < 0) return null;

            int typ = -1, bld = -1, nm = -1, roof = -1, ht = -1, lvl = -1, lu = -1, nat = -1, lei = -1,
                bcol = -1, rcol = -1, aer = -1;
            int pk = kS, pv = vS;
            while (pk < kE)
            {
                int k = (int)Proto.Varint(b, ref pk);
                int v = (int)Proto.Varint(b, ref pv);
                if (k >= c.StrCount) continue;
                switch (c.KeyCode[k])
                {
                    case KType: typ = v; break;
                    case KBuilding: bld = v; break;
                    case KName: nm = v; break;
                    case KRoof: roof = v; break;
                    case KHeight: ht = v; break;
                    case KLevels: lvl = v; break;
                    case KLanduse: lu = v; break;
                    case KNatural: nat = v; break;
                    case KLeisure: lei = v; break;
                    case KBColour: bcol = v; break;
                    case KRColour: rcol = v; break;
                    case KAeroway: aer = v; break;
                }
            }
            if (typ < 0 || Str(b, c, typ) != "multipolygon") return null;

            var a = new RecAttrs();
            if (bld >= 0 && Str(b, c, bld) != "no")
            {
                a.Kind = PackFormat.KindBuilding;
                a.S1 = Str(b, c, bld);
                a.Roof = roof >= 0 ? Str(b, c, roof) : null;
                a.Height = ParseMeters(ht >= 0 ? Str(b, c, ht) : null);
                a.Levels = ParseInt(lvl >= 0 ? Str(b, c, lvl) : null);
                a.BColour = bcol >= 0 ? Str(b, c, bcol) : null;
                a.RColour = rcol >= 0 ? Str(b, c, rcol) : null;
            }
            else
            {
                AreaKind ak = AreaKind.None;
                if (lu >= 0) ak = AreaKinds.From("landuse", Str(b, c, lu));
                if (ak == AreaKind.None && nat >= 0) ak = AreaKinds.From("natural", Str(b, c, nat));
                if (ak == AreaKind.None && lei >= 0) ak = AreaKinds.From("leisure", Str(b, c, lei));
                if (ak == AreaKind.None && aer >= 0 && Str(b, c, aer) == "apron") ak = AreaKind.Pavement;
                if (ak == AreaKind.None) return null;
                a.Kind = PackFormat.KindArea;
                a.Area = (sbyte)ak;
            }
            a.Name = nm >= 0 ? Str(b, c, nm) : null;

            // czlonkowie: tylko linie (typ 1), rola inner albo reszta (outer / pusta)
            var ways = new List<long>();
            var inner = new List<bool>();
            long mid = 0;
            int pm = mS, pt = tS, pr = roS;
            while (pm < mE && pt < tE)
            {
                mid += Proto.ZigZag(Proto.Varint(b, ref pm));
                int type = (int)Proto.Varint(b, ref pt);
                int role = pr >= 0 && pr < roE ? (int)Proto.Varint(b, ref pr) : -1;
                if (type != 1) continue;
                string rs = role >= 0 ? Str(b, c, role) : null;
                ways.Add(mid);
                inner.Add(rs == "inner");
            }
            if (ways.Count == 0) return null;
            return new RelInfo { Id = id, Attrs = a, Ways = ways.ToArray(), Inner = inner.ToArray() };
        }

        private static bool IsWantedRoad(string v)
        {
            switch (v)
            {
                case "motorway": case "motorway_link":
                case "trunk": case "trunk_link":
                case "primary": case "primary_link":
                case "secondary": case "secondary_link":
                case "tertiary": case "tertiary_link":
                case "residential": case "unclassified":
                // v4: reszta sieci - dojazdowe, polne, deptaki, sciezki i drogi rowerowe
                case "service": case "living_street": case "track": case "pedestrian": case "road":
                case "busway": case "footway": case "cycleway": case "path": case "bridleway":
                    return true;
                default: return false;
            }
        }

        private static int Sidewalk(string v)
        {
            switch (v)
            {
                case "both": case "yes": return PackFormat.RSideLeft | PackFormat.RSideRight;
                case "left": return PackFormat.RSideLeft;
                case "right": return PackFormat.RSideRight;
                case "no": case "none": return PackFormat.RSideNone;
                case "separate": return PackFormat.RSideSeparate;
                default: return 0;
            }
        }

        /// <summary>maxspeed -> km/h: liczba, "50 mph", strefy krajowe (PL:urban, DE:rural...), walk.</summary>
        private static int Speed(string v)
        {
            if (string.IsNullOrEmpty(v)) return 0;
            if (v == "walk") return 7;
            if (v.EndsWith(":urban")) return 50;
            if (v.EndsWith(":rural")) return 90;
            if (v.EndsWith(":motorway")) return 130;
            if (v.EndsWith(":living_street")) return 20;
            int end = 0;
            while (end < v.Length && char.IsDigit(v[end])) end++;
            int n;
            if (end == 0 || !int.TryParse(v.Substring(0, end), out n)) return 0;
            if (v.Contains("mph")) n = (int)Math.Round(n * 1.609);
            return Math.Min(n, 300);
        }

        // Obie ponizej skopiowane ze starego OsmPack bez zmian - roznica choc
        // w jednym znaku (np. '+') dalaby inne wysokosci budynkow w paczkach.
        private static float ParseMeters(string v)
        {
            if (string.IsNullOrEmpty(v)) return float.NaN;
            int end = 0;
            while (end < v.Length && (char.IsDigit(v[end]) || v[end] == '.' || v[end] == '-')) end++;
            float f;
            return end > 0 && float.TryParse(v.Substring(0, end), NumberStyles.Float,
                                             CultureInfo.InvariantCulture, out f)
                   ? f : float.NaN;
        }

        private static int ParseInt(string v)
        {
            int i;
            return int.TryParse(v, out i) ? i : -1;
        }
    }
}
