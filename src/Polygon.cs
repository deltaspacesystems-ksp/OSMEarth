using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Triangulacja wielokatow prostych - wspolna dla budynkow, landuse i mapy.
    ///
    /// Wczesniej siedzialy tu dwie identyczne kopie (w BuildingMeshBuilder
    /// i LanduseMeshBuilder), a mapa planera w ogole nie triangulowala, tylko
    /// rysowala wachlarz z pierwszego wierzcholka. Dla wkleslych pol i lasow
    /// wachlarz wychodzil kilometrami poza obrys.
    /// </summary>
    public static class Polygon
    {
        /// <summary>
        /// Ear clipping. Zwraca indeksy w "ring" albo null, gdy sie nie da.
        ///
        /// Trzy rzeczy, bez ktorych na prawdziwych danych OSM algorytm stawal
        /// w polowie i zostawial dziury (na mapie: ciemne szpice przez pola):
        ///
        ///  1. Zdublowane punkty (w tym zamkniecie obrysu: ostatni = pierwszy)
        ///     sa pomijane. Inaczej punkt lezacy DOKLADNIE w wierzcholku ucha
        ///     liczyl sie jako "w srodku" i blokowal kazde ucho obok siebie.
        ///  2. Punkt pokrywajacy sie z wierzcholkiem kandydata nie blokuje ucha.
        ///  3. Gdy zadne ucho nie przechodzi testu (obrys sam sie przecina - po
        ///     przerzedzeniu punktow na mapie to sie zdarza), wycinamy pierwszy
        ///     wypukly wierzcholek mimo wszystko. Mala zakladka jest niewidoczna,
        ///     dziura przez pol pola - bardzo.
        ///
        /// Po wycieciu ucha szukamy DALEJ od tego miejsca, a nie od poczatku:
        /// restart od zera dawal koszt szescienny przy obrysach z setek punktow.
        /// </summary>
        public static List<int> Triangulate(List<Vector2> ring)
        {
            int n = ring.Count;
            if (n < 3) return null;

            // --- 1. bez duplikatow, takze zamykajacego ---
            var idx = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                if (idx.Count > 0 && Same(ring[idx[idx.Count - 1]], ring[i])) continue;
                idx.Add(i);
            }
            while (idx.Count > 1 && Same(ring[idx[0]], ring[idx[idx.Count - 1]]))
                idx.RemoveAt(idx.Count - 1);
            if (idx.Count < 3) return null;

            float signed = 0f;
            for (int i = 0; i < idx.Count; i++)
            {
                Vector2 a = ring[idx[i]], b = ring[idx[(i + 1) % idx.Count]];
                signed += a.x * b.y - b.x * a.y;
            }
            if (signed < 0f) idx.Reverse();

            var outTris = new List<int>((idx.Count - 2) * 3);
            int pos = 0, misses = 0;

            while (idx.Count > 3)
            {
                int count = idx.Count;
                pos %= count;
                int i0 = idx[(pos - 1 + count) % count];
                int i1 = idx[pos];
                int i2 = idx[(pos + 1) % count];
                Vector2 a = ring[i0], b = ring[i1], c = ring[i2];

                bool convex = Cross(a, b, c) > 0f;
                bool ear = convex && Empty(ring, idx, i0, i1, i2, a, b, c);

                if (!ear && misses >= count)
                {
                    // --- 3. wyjscie awaryjne: pierwszy wypukly, bez testu pustosci ---
                    int forced = -1;
                    for (int k = 0; k < count; k++)
                    {
                        int j0 = idx[(k - 1 + count) % count], j1 = idx[k], j2 = idx[(k + 1) % count];
                        if (Cross(ring[j0], ring[j1], ring[j2]) > 0f) { forced = k; break; }
                    }
                    if (forced < 0) break;   // same wierzcholki wklesle - nie ma czego ciac
                    pos = forced;
                    i0 = idx[(pos - 1 + count) % count]; i1 = idx[pos]; i2 = idx[(pos + 1) % count];
                    ear = true;
                }

                if (ear)
                {
                    outTris.Add(i0); outTris.Add(i1); outTris.Add(i2);
                    idx.RemoveAt(pos);
                    misses = 0;
                }
                else
                {
                    pos++;
                    misses++;
                }
            }

            if (idx.Count == 3)
            {
                outTris.Add(idx[0]); outTris.Add(idx[1]); outTris.Add(idx[2]);
            }

            return outTris.Count >= 3 ? outTris : null;
        }

        private static bool Empty(List<Vector2> ring, List<int> idx, int i0, int i1, int i2,
                                  Vector2 a, Vector2 b, Vector2 c)
        {
            for (int j = 0; j < idx.Count; j++)
            {
                int k = idx[j];
                if (k == i0 || k == i1 || k == i2) continue;
                Vector2 p = ring[k];
                // --- 2. punkt w wierzcholku ucha nie jest "w srodku" ---
                if (Same(p, a) || Same(p, b) || Same(p, c)) continue;
                if (PointInTriangle(p, a, b, c)) return false;
            }
            return true;
        }

        private static bool Same(Vector2 p, Vector2 q)
        {
            float dx = p.x - q.x, dy = p.y - q.y;
            return dx * dx + dy * dy < 1e-8f;
        }

        private static float Cross(Vector2 o, Vector2 a, Vector2 b)
        {
            return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(a, b, p), d2 = Cross(b, c, p), d3 = Cross(c, a, p);
            bool neg = (d1 < 0f) || (d2 < 0f) || (d3 < 0f);
            bool pos = (d1 > 0f) || (d2 > 0f) || (d3 > 0f);
            return !(neg && pos);
        }
    }
}
