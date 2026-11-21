using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OSMRoads;

namespace PlanetPack
{
    /// <summary>planetpack --check paczka.idx [lat lon] - statystyka paczki v4 i przyklady atrybutow.</summary>
    internal static class Check
    {
        private static Action AtEnd;
        public static int Run(string idxPath, double lat, double lon)
        {
            string bin = Path.ChangeExtension(idxPath, ".bin");
            string[] strings;
            var cells = new Dictionary<long, long[]>();
            int ver;
            using (var r = new BinaryReader(File.OpenRead(idxPath)))
            {
                if (r.ReadUInt32() != PackFormat.Magic) { Console.WriteLine("zly naglowek"); return 1; }
                ver = r.ReadInt32();
                r.ReadDouble();
                strings = new string[r.ReadInt32()];
                for (int i = 0; i < strings.Length; i++) strings[i] = r.ReadString();
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++) { long k = r.ReadInt64(); cells[k] = new long[] { r.ReadInt64(), r.ReadInt32(), r.ReadInt32() }; }
            }
            Console.WriteLine("wersja {0}, komorek {1}, stringow {2}", ver, cells.Count, strings.Length);
            int relAreas = 0, relForest = 0, relHoles = 0, relShown = 0;
            AtEnd = () => Console.WriteLine("obszary z relacji: {0} (las {1}, dziury {2})", relAreas, relForest, relHoles);

            int cy, cx;
            PackFormat.CellOf(lat, lon, out cy, out cx);
            var kinds = new int[8];
            var surf = new Dictionary<string, int>(); var hw = new Dictionary<string, int>(); var bar = new Dictionary<string, int>();
            int bridges = 0, tunnels = 0, oneway = 0, lanes = 0, width = 0, speed = 0, lit = 0, side = 0, bcol = 0, rcol = 0, rmat = 0;
            Func<BinaryReader, string> S = rr => { long i = PackFormat.ReadVarInt(rr); return i >= 0 && i < strings.Length ? strings[i] : null; };

            using (var fs = File.OpenRead(bin))
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        long[] e;
                        if (!cells.TryGetValue(PackFormat.CellKey(cy + dy, cx + dx), out e)) continue;
                        fs.Seek(e[0], SeekOrigin.Begin);
                        var buf = new byte[e[1]]; fs.Read(buf, 0, buf.Length);
                        using (var r = new BinaryReader(new MemoryStream(PackFormat.Inflate(buf, (int)e[2]))))
                        {
                            int count = (int)PackFormat.ReadVarInt(r);
                            for (int i = 0; i < count; i++)
                            {
                                byte kind = r.ReadByte(); long rid = PackFormat.ReadVarInt(r);
                                kinds[kind]++;
                                if (kind == PackFormat.KindPlace) { S(r); S(r); PackFormat.ReadVarInt(r); PackFormat.ReadVarInt(r); continue; }
                                if (kind == PackFormat.KindTree) { PackFormat.ReadVarInt(r); PackFormat.ReadVarInt(r); continue; }
                                if (kind == PackFormat.KindRoad)
                                {
                                    string h = S(r); Inc(hw, h);
                                    string su = S(r); if (su != null) Inc(surf, su);
                                    int fl = (int)PackFormat.ReadVarInt(r); PackFormat.ReadVarInt(r);
                                    if (PackFormat.ReadVarInt(r) > 0) lanes++;
                                    if (PackFormat.ReadVarInt(r) > 0) width++;
                                    if (PackFormat.ReadVarInt(r) > 0) speed++;
                                    if ((fl & PackFormat.RBridge) != 0) bridges++;
                                    if ((fl & PackFormat.RTunnel) != 0) tunnels++;
                                    if ((fl & (PackFormat.ROneway | PackFormat.ROnewayRev)) != 0) oneway++;
                                    if ((fl & PackFormat.RLit) != 0) lit++;
                                    if ((fl & 0x1E0) != 0) side++;
                                }
                                else if (kind == PackFormat.KindBuilding)
                                {
                                    S(r); r.ReadSingle(); PackFormat.ReadVarInt(r); S(r);
                                    if (S(r) != null) bcol++;
                                    if (S(r) != null) rcol++;
                                    if (S(r) != null) rmat++;
                                    S(r);
                                }
                                else if (kind == PackFormat.KindBarrier) Inc(bar, S(r));
                                else
                                {
                                    sbyte ak = r.ReadSByte();
                                    if (rid < 0) { relAreas++; if (ak == 1) relForest++; if (ak == 8) relHoles++; }
                                }
                                S(r);
                                int np = (int)PackFormat.ReadVarInt(r);
                                long la = 0, lo = 0;
                                for (int j = 0; j < np; j++)
                                {
                                    la += PackFormat.ReadVarInt(r); lo += PackFormat.ReadVarInt(r);
                                    if (j == 0 && rid < 0 && kind == PackFormat.KindArea && relShown < 5)
                                    { relShown++; Console.WriteLine("  relacja {0}: {1} pkt, start {2:F5} {3:F5}", -rid / 64, np, la / 1e7, lo / 1e7); }
                                }
                            }
                        }
                    }

            Console.WriteLine("okolica {0:F3},{1:F3} (25 komorek): drogi {2}, budynki {3}, obszary {4}, miejsca {5}, drzewa {6}, ogrodzenia {7}",
                              lat, lon, kinds[1], kinds[2], kinds[3], kinds[4], kinds[5], kinds[6]);
            Console.WriteLine("  mosty {0}, tunele {1}, jednokierunkowe {2}, z pasami {3}, z szerokoscia {4}, z predkoscia {5}, oswietlone {6}, z chodnikiem {7}",
                              bridges, tunnels, oneway, lanes, width, speed, lit, side);
            Console.WriteLine("  budynki: kolor scian {0}, kolor dachu {1}, material dachu {2}", bcol, rcol, rmat);
            Console.WriteLine("  typy drog: " + string.Join(", ", hw.OrderByDescending(k => k.Value).Take(12).Select(k => k.Key + " " + k.Value)));
            Console.WriteLine("  nawierzchnie: " + string.Join(", ", surf.OrderByDescending(k => k.Value).Take(12).Select(k => k.Key + " " + k.Value)));
            Console.WriteLine("  ogrodzenia: " + string.Join(", ", bar.OrderByDescending(k => k.Value).Take(8).Select(k => k.Key + " " + k.Value)));
            if (AtEnd != null) AtEnd();
            return 0;
        }

        private static void Inc(Dictionary<string, int> d, string k)
        {
            if (k == null) return;
            int n; d.TryGetValue(k, out n); d[k] = n + 1;
        }
    }
}
