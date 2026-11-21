using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using OSMRoads;

namespace PlanetPack
{
    /// <summary>
    /// Sklada paczke kwadratu z plikow zrzutu - stopien po stopniu.
    ///
    /// Format wyjscia jest DOKLADNIE ten sam, co pisal stary OsmPack.Write:
    /// ta sama kolejnosc pol, te same stringi internowane per paczka, ten sam
    /// deflate na komorke i ten sam naglowek indeksu. Mod nie ma jak odroznic,
    /// ktore narzedzie zrobilo paczke - i o to chodzi.
    ///
    /// Plik powstaje pod nazwa .tmp i dostaje wlasciwa dopiero na koncu, a .idx
    /// jako ostatni: mod szuka paczek po *.idx, wiec nigdy nie zobaczy polowy.
    /// </summary>
    internal static class PackBuilder
    {
        private sealed class CellAcc
        {
            public int Count;
            public readonly MemoryStream Items = new MemoryStream();
        }

        public static bool Build(int sq, Spill spill, string outDir, int threads, Action<string> log,
                                 out long objects)
        {
            objects = 0;
            string name = Grid.SquareName(sq);

            int south, west;
            Grid.SquareCorner(sq, out south, out west);

            var files = new List<string>();
            for (int la = south; la < south + 10; la++)
                for (int lo = west; lo < west + 10; lo++)
                {
                    string p = spill.PathOf((la + 90) * 360 + (lo + 180));
                    if (File.Exists(p)) files.Add(p);
                }
            if (files.Count == 0) return false;

            string bin = Path.Combine(outDir, name + ".bin");
            string idx = Path.Combine(outDir, name + ".idx");
            string json = Path.Combine(outDir, name + ".json");

            var strTable = new List<string>();
            var strIndex = new Dictionary<string, int>();
            Func<string, int> intern = s =>
            {
                if (string.IsNullOrEmpty(s)) return -1;
                int ix;
                if (strIndex.TryGetValue(s, out ix)) return ix;
                ix = strTable.Count;
                strTable.Add(s);
                strIndex[s] = ix;
                return ix;
            };

            var index = new List<long[]>();
            int minLat = int.MaxValue, maxLat = int.MinValue, minLon = int.MaxValue, maxLon = int.MinValue;
            long binBytes;

            using (var bs = new FileStream(bin + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var payload = new MemoryStream(1 << 16);
                var pw = new BinaryWriter(payload, Encoding.UTF8, true);
                var cellKeys = new List<long>(64);

                foreach (string file in files)
                {
                    var cells = new Dictionary<long, CellAcc>();

                    using (var fr = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                    using (var br = new BinaryReader(fr, Encoding.UTF8, true))
                    {
                        long flen = fr.Length;
                        while (fr.Position < flen)
                        {
                            byte tag = br.ReadByte();
                            payload.SetLength(0);
                            cellKeys.Clear();

                            if (tag == 1 || tag == 3)
                            {
                                long id = br.ReadInt64();
                                byte kind = br.ReadByte();
                                string s1 = br.ReadString();
                                string roof = br.ReadString();
                                string nm = br.ReadString();
                                float height = br.ReadSingle();
                                int levels = br.ReadInt32();
                                sbyte area = br.ReadSByte();
                                string surface = "", bcol = "", rcol = "", rmat = "", bmat = "";
                                int flags = 0, layer = 0, lanes = 0, widthCm = 0, maxSpeed = 0;
                                if (tag == 3)
                                {
                                    surface = br.ReadString();
                                    flags = br.ReadInt32(); layer = br.ReadInt32(); lanes = br.ReadInt32();
                                    widthCm = br.ReadInt32(); maxSpeed = br.ReadInt32();
                                    bcol = br.ReadString(); rcol = br.ReadString(); rmat = br.ReadString(); bmat = br.ReadString();
                                }
                                int nc = br.ReadUInt16();
                                for (int i = 0; i < nc; i++) cellKeys.Add(br.ReadInt64());
                                int npts = br.ReadInt32();
                                int clen = br.ReadInt32();
                                byte[] coords = br.ReadBytes(clen);

                                // --- dokladnie jak OsmPack.Write ---
                                pw.Write(kind);
                                PackFormat.WriteVarInt(pw, id);
                                if (kind == PackFormat.KindRoad)
                                {
                                    PackFormat.WriteVarInt(pw, intern(s1));
                                    // v4
                                    PackFormat.WriteVarInt(pw, intern(surface));
                                    PackFormat.WriteVarInt(pw, flags);
                                    PackFormat.WriteVarInt(pw, layer);
                                    PackFormat.WriteVarInt(pw, lanes);
                                    PackFormat.WriteVarInt(pw, widthCm);
                                    PackFormat.WriteVarInt(pw, maxSpeed);
                                }
                                else if (kind == PackFormat.KindBuilding)
                                {
                                    PackFormat.WriteVarInt(pw, intern(s1));
                                    pw.Write(height);
                                    PackFormat.WriteVarInt(pw, levels);
                                    PackFormat.WriteVarInt(pw, intern(roof));
                                    // v4
                                    PackFormat.WriteVarInt(pw, intern(bcol));
                                    PackFormat.WriteVarInt(pw, intern(rcol));
                                    PackFormat.WriteVarInt(pw, intern(rmat));
                                    PackFormat.WriteVarInt(pw, intern(bmat));
                                }
                                else if (kind == PackFormat.KindBarrier)
                                {
                                    PackFormat.WriteVarInt(pw, intern(s1));
                                }
                                else
                                {
                                    pw.Write(area);
                                }
                                PackFormat.WriteVarInt(pw, intern(nm));
                                PackFormat.WriteVarInt(pw, npts);
                                // Delty sa w zrzucie w tym samym kodowaniu co w paczce,
                                // wiec geometrie przepisujemy bez dekodowania.
                                pw.Write(coords);
                            }
                            else if (tag == 2)
                            {
                                long id = br.ReadInt64();
                                string nm = br.ReadString();
                                string kd = br.ReadString();
                                int la = br.ReadInt32();
                                int lo = br.ReadInt32();
                                cellKeys.Add(br.ReadInt64());

                                pw.Write(PackFormat.KindPlace);
                                PackFormat.WriteVarInt(pw, id);
                                PackFormat.WriteVarInt(pw, intern(nm));
                                PackFormat.WriteVarInt(pw, intern(kd));
                                PackFormat.WriteVarInt(pw, la);
                                PackFormat.WriteVarInt(pw, lo);
                            }
                            else if (tag == 4)
                            {
                                // v4: pojedyncze drzewo
                                long id = br.ReadInt64();
                                int la = br.ReadInt32();
                                int lo = br.ReadInt32();
                                cellKeys.Add(br.ReadInt64());
                                pw.Write(PackFormat.KindTree);
                                PackFormat.WriteVarInt(pw, id);
                                PackFormat.WriteVarInt(pw, la);
                                PackFormat.WriteVarInt(pw, lo);
                            }
                            else throw new InvalidDataException("zly rekord zrzutu w " + file);

                            pw.Flush();
                            int plen = (int)payload.Length;
                            foreach (long ck in cellKeys)
                            {
                                CellAcc acc;
                                if (!cells.TryGetValue(ck, out acc)) cells[ck] = acc = new CellAcc();
                                acc.Count++;
                                acc.Items.Write(payload.GetBuffer(), 0, plen);
                            }
                            objects++;
                        }
                    }

                    // Deflate komorek rownolegle - to on, a nie dysk, jest tu waskim gardlem.
                    var keys = new long[cells.Count];
                    cells.Keys.CopyTo(keys, 0);
                    Array.Sort(keys);
                    var packed = new byte[keys.Length][];
                    var rawLens = new int[keys.Length];

                    Parallel.For(0, keys.Length, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
                    {
                        CellAcc acc = cells[keys[i]];
                        using (var raw = new MemoryStream((int)acc.Items.Length + 10))
                        using (var rw = new BinaryWriter(raw))
                        {
                            PackFormat.WriteVarInt(rw, acc.Count);
                            rw.Write(acc.Items.GetBuffer(), 0, (int)acc.Items.Length);
                            rw.Flush();
                            byte[] rawArr = raw.ToArray();
                            rawLens[i] = rawArr.Length;
                            packed[i] = PackFormat.Deflate(rawArr);
                        }
                    });

                    for (int i = 0; i < keys.Length; i++)
                    {
                        long start = bs.Position;
                        bs.Write(packed[i], 0, packed[i].Length);
                        index.Add(new long[] { keys[i], start, packed[i].Length, rawLens[i] });

                        int iLat = (int)(keys[i] >> 32), iLon = (int)(uint)keys[i];
                        if (iLat < minLat) minLat = iLat;
                        if (iLat > maxLat) maxLat = iLat;
                        if (iLon < minLon) minLon = iLon;
                        if (iLon > maxLon) maxLon = iLon;
                    }
                }

                binBytes = bs.Length;
            }

            using (var fs = new FileStream(idx + ".tmp", FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(PackFormat.Magic);
                w.Write(PackFormat.Version);
                w.Write(PackFormat.CellDeg);

                w.Write(strTable.Count);
                foreach (string s in strTable) w.Write(s);

                w.Write(index.Count);
                foreach (long[] e in index)
                {
                    w.Write(e[0]); w.Write(e[1]); w.Write((int)e[2]); w.Write((int)e[3]);
                }
            }

            long idxBytes = new FileInfo(idx + ".tmp").Length;

            // Zasieg z indeksow komorek - dokladnie tak samo liczy go LocalStore
            // w DeriveBox, gdy manifestu brak. Gorna krawedz o komorke dalej.
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"name\": \"").Append(name).Append("\",\n");
            sb.Append("  \"south\": ").Append((minLat * PackFormat.CellDeg).ToString("F5", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"west\": ").Append((minLon * PackFormat.CellDeg).ToString("F5", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"north\": ").Append(((maxLat + 1) * PackFormat.CellDeg).ToString("F5", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"east\": ").Append(((maxLon + 1) * PackFormat.CellDeg).ToString("F5", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"cells\": ").Append(index.Count).Append(",\n");
            sb.Append("  \"bytes\": ").Append(binBytes + idxBytes).Append(",\n");
            sb.Append("  \"built\": \"").Append(DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("\"\n");
            sb.Append("}\n");
            File.WriteAllText(json + ".tmp", sb.ToString());

            // Kolejnosc przemianowania ma znaczenie: .idx jako ostatni plik danych,
            // bo po nim mod rozpoznaje paczke.
            File.Move(bin + ".tmp", bin, true);
            File.Move(json + ".tmp", json, true);
            File.Move(idx + ".tmp", idx, true);

            log(string.Format("    {0}  {1,8:N1} MB + {2,5:N1} MB idx  {3,6:N0} komorek  {4,10:N0} rekordow",
                              name, binBytes / 1048576.0, idxBytes / 1048576.0, index.Count, objects));
            return true;
        }
    }
}
