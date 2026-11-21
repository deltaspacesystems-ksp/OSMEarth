using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Zestaw przegladowy drog (ro_&lt;kwadrat&gt;.idx, robi go narzedzie placesindex): autostrady, ekspresowe, krajowe
    /// i wojewodzkie kwadratu 10x10 st., uproszczone. Dzieki niemu planer pokazuje drogi w duzych skalach, gdzie
    /// czytanie pelnych paczek (kafel po kaflu) byloby zbyt ciezkie.
    ///
    /// Kwadrat wczytuje sie w tle (moze isc z serwera); mapa pyta co jakis czas o gotowe kwadraty widoku
    /// i przerysowuje sie, gdy Revision wzrosnie.
    /// </summary>
    internal static class RoadOverview
    {
        internal sealed class Square
        {
            public string Name;
            public byte[] Cls = new byte[0];        // klasa kazdej drogi: 0 autostrada, 1 ekspresowa, 2 krajowa, 3 wojewodzka
            // Jedna plaska tablica punktow (lat, lon na przemian, 1e-5 st.) i indeks poczatku kazdej drogi: kwadrat ma
            // nawet milion drog, a milion malych tablic zjadalby pamiec i wolal GC.
            public int[] Pts = new int[0];
            public int[] Start = new int[1];        // droga i: punkty od Start[i]/2 do Start[i+1]/2 (indeksy w Pts w parach)
            public int[] Box = new int[0];          // na droge: minLat, maxLat, minLon, maxLon
            public volatile bool Ready;
            public bool Loading;
            public float Used;
        }

        private static readonly Dictionary<string, Square> squares = new Dictionary<string, Square>();
        private static readonly object gate = new object();
        private static int revision;
        private const int MaxSquares = 16;

        /// <summary>Rosnie, gdy dojdzie nowy kwadrat - mapa ma sie przerysowac.</summary>
        public static int Revision { get { return revision; } }

        /// <summary>Gotowe kwadraty pokrywajace prostokat; brakujace zaczynaja sie wczytywac w tle.</summary>
        public static List<Square> Squares(BBox box)
        {
            var list = new List<Square>();
            int laMin = (int)Math.Floor(box.South / 10.0), laMax = (int)Math.Floor(box.North / 10.0);
            int loMin = (int)Math.Floor(box.West / 10.0), loMax = (int)Math.Floor(box.East / 10.0);
            if (loMax - loMin > 12 || laMax - laMin > 12) return list;              // za szeroki widok na przeglad drog

            for (int la = laMin; la <= laMax; la++)
            {
                if (la < -9 || la > 8) continue;
                for (int lo = loMin; lo <= loMax; lo++)
                {
                    int wrapped = ((lo + 18) % 36 + 36) % 36 - 18;                  // -18..17 = -180..170 st.
                    string name = PlaceSearch.SquareName(la * 10 + 0.1, wrapped * 10 + 0.1);
                    Square s = Get(name);
                    if (s.Ready) list.Add(s);
                }
            }
            return list;
        }

        private static Square Get(string name)
        {
            lock (gate)
            {
                Square s;
                if (squares.TryGetValue(name, out s)) { s.Used = Time.realtimeSinceStartup; return s; }

                if (squares.Count >= MaxSquares)
                {
                    string oldest = null;
                    float t = float.MaxValue;
                    foreach (KeyValuePair<string, Square> kv in squares)
                        if (kv.Value.Ready && kv.Value.Used < t) { t = kv.Value.Used; oldest = kv.Key; }
                    if (oldest != null) squares.Remove(oldest);
                }

                s = new Square { Name = name, Loading = true, Used = Time.realtimeSinceStartup };
                squares[name] = s;
                ThreadPool.QueueUserWorkItem(_ => Load(s));
                return s;
            }
        }

        private static void Load(Square sq)
        {
            try
            {
                string path = LocalStore.FindShared("ro_" + sq.Name + ".idx");
                if (path != null)
                {
                    using (var r = new BinaryReader(new MemoryStream(File.ReadAllBytes(path))))
                    {
                        if (r.ReadByte() != 0x52 || r.ReadByte() != 0x4F || r.ReadByte() != 0x56 || r.ReadByte() != 0x31)
                            throw new IOException("zly naglowek ro_" + sq.Name);
                        int n = r.ReadInt32();
                        var cls = new byte[n];
                        var start = new int[n + 1];
                        var box = new int[4 * n];
                        var pts = new List<int>(n * 8);
                        for (int i = 0; i < n; i++)
                        {
                            cls[i] = r.ReadByte();
                            int np = (int)PackFormat.ReadVarInt(r);
                            start[i] = pts.Count;
                            long la = 0, lo = 0;
                            int minLa = int.MaxValue, maxLa = int.MinValue, minLo = int.MaxValue, maxLo = int.MinValue;
                            for (int j = 0; j < np; j++)
                            {
                                la += PackFormat.ReadVarInt(r);
                                lo += PackFormat.ReadVarInt(r);
                                pts.Add((int)la); pts.Add((int)lo);
                                if (la < minLa) minLa = (int)la;
                                if (la > maxLa) maxLa = (int)la;
                                if (lo < minLo) minLo = (int)lo;
                                if (lo > maxLo) maxLo = (int)lo;
                            }
                            box[4 * i] = minLa; box[4 * i + 1] = maxLa; box[4 * i + 2] = minLo; box[4 * i + 3] = maxLo;
                        }
                        start[n] = pts.Count;
                        sq.Cls = cls; sq.Pts = pts.ToArray(); sq.Start = start; sq.Box = box;                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[OSMRoads] ro_" + sq.Name + ": " + e.Message); }

            sq.Loading = false;
            sq.Ready = true;
            Interlocked.Increment(ref revision);
        }
    }
}
