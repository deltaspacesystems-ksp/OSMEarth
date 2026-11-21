using System;
using System.Collections.Generic;

namespace PlanetPack
{
    /// <summary>
    /// Podzial planety na przebiegi, z ktorych kazdy miesci sie w budzecie RAM.
    ///
    /// Jednostka jest kwadrat 10x10 st. (= jedna paczka). Kwadraty dokladamy
    /// do przebiegu kolumnami, wiec przebieg to zwarty kawalek mapy - wtedy
    /// zapas wokol niego w wiekszosci pokrywa sie z samym obszarem, zamiast
    /// doliczac wezly z drugiego konca swiata.
    ///
    /// Koszt przebiegu to wezly w jego kwadratach PLUS w pasie zapasu dookola:
    /// droga przecinajaca granice musi miec komplet wezlow, a te po drugiej
    /// stronie tez trzeba trzymac w pamieci.
    /// </summary>
    internal static class Planner
    {
        public static List<List<int>> Plan(long[] hist, long budgetNodes, int marginDeg,
                                           HashSet<int> onlySquares, Action<string> log)
        {
            var squareNodes = new long[Grid.SquareCount];
            for (int d1 = 0; d1 < Grid.Deg1Count; d1++)
                squareNodes[Grid.SquareOfDeg1(d1)] += hist[d1];

            var batches = new List<List<int>>();
            var current = new List<int>();

            for (int lo = 0; lo < 36; lo++)
            {
                for (int la = 0; la < 18; la++)
                {
                    int sq = la * 36 + lo;
                    if (squareNodes[sq] == 0) continue;
                    if (onlySquares != null && !onlySquares.Contains(sq)) continue;

                    current.Add(sq);
                    long need = NodesFor(current, hist, marginDeg);
                    if (need <= budgetNodes || current.Count == 1)
                    {
                        if (current.Count == 1 && need > budgetNodes)
                            log(string.Format("  UWAGA: kwadrat {0} sam potrzebuje {1:N0} wezlow, budzet {2:N0}",
                                              Grid.SquareName(sq), need, budgetNodes));
                        continue;
                    }

                    current.RemoveAt(current.Count - 1);
                    batches.Add(current);
                    current = new List<int> { sq };
                }
            }
            if (current.Count > 0) batches.Add(current);
            return batches;
        }

        public static long NodesFor(List<int> squares, long[] hist, int marginDeg)
        {
            bool[] mask = DilatedMask(squares, marginDeg);
            long sum = 0;
            for (int i = 0; i < Grid.Deg1Count; i++)
                if (mask[i]) sum += hist[i];
            return sum;
        }

        public static bool[] DilatedMask(List<int> squares, int marginDeg)
        {
            var mask = new bool[Grid.Deg1Count];
            foreach (int sq in squares)
            {
                int la0 = (sq / 36) * 10, lo0 = (sq % 36) * 10;
                for (int la = la0 - marginDeg; la < la0 + 10 + marginDeg; la++)
                {
                    if (la < 0 || la >= 180) continue;
                    for (int lo = lo0 - marginDeg; lo < lo0 + 10 + marginDeg; lo++)
                    {
                        int wrapped = ((lo % 360) + 360) % 360;   // poludnik 180 jest ciagly
                        mask[la * 360 + wrapped] = true;
                    }
                }
            }
            return mask;
        }

        public static bool[] CoreMask(List<int> squares)
        {
            var core = new bool[Grid.SquareCount];
            foreach (int sq in squares) core[sq] = true;
            return core;
        }

        public static string Describe(List<int> squares)
        {
            var names = new List<string>();
            foreach (int sq in squares) names.Add(Grid.SquareName(sq));
            return string.Join(",", names);
        }
    }
}
