using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Doklejanie wstegi drog do terenu z DOKLADNOSCIA PQS.
    ///
    /// Watek roboczy bierze wysokosci z TerrainGrid, czyli z siatki 64x64 na kafel
    /// 1500 m - jeden wezel co 23 m, miedzy nimi interpolacja dwuliniowa. Na plaskim
    /// to wystarcza, ale w wykopie, na nasypie i na grzbiecie roznica siega metrow:
    /// droga tonie w zboczu albo wisi nad nim.
    ///
    /// PQS wolno odpytywac tylko z glownego watku, wiec poprawka idzie tutaj, juz
    /// po zbudowaniu siatki - porcjami po kilkaset wierzcholkow na klatke, zeby
    /// nie bylo zwiechy. Do czasu zakonczenia droga po prostu lezy tam, gdzie
    /// polozyla ja siatka; poprawka jest widoczna jako jednorazowe "przyssanie".
    /// </summary>
    public static class GroundSnapper
    {
        private class Job
        {
            public GameObject Go;
            public Mesh Mesh;
            public Vector3[] Verts;
            public CelestialBody Body;
            public double Lat0, Lon0, Alt0, Radius;
            public float Offset;
            public int Index;
            public TerrainGrid Grid;     // != null: zachowaj unos wzgledem siatki (landuse)
        }

        // Dwie kolejki: drogi przed landuse. Plachta landuse ma dziesiatki tysiecy
        // wierzcholkow i przy wczytywaniu miasta potrafila trzymac droge w kolejce
        // tak dlugo, ze ta lezala nieprzyklejona - czyli z dziurami pod pagorkami.
        private static readonly Queue<Job> queue = new Queue<Job>();
        private static readonly Queue<Job> lowQueue = new Queue<Job>();
        private static Job current;

        /// <summary>Budzet czasu na klatke w milisekundach. Czas, a nie liczba
        /// wierzcholkow, bo koszt GetSurfaceHeight zalezy od planet packa (SOL
        /// ma duzo modyfikatorow PQS) - staly limit albo zwalnial, albo cial klatki.</summary>
        public static float BudgetMs = 3.0f;

        private static readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();

        public static int Pending { get { return queue.Count + lowQueue.Count + (current != null ? 1 : 0); } }

        public static void Request(GameObject go, CelestialBody body,
                                   double lat0, double lon0, double alt0,
                                   double radius, float offset)
        {
            if (go == null || body == null || body.pqsController == null) return;

            var mf = go.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;

            queue.Enqueue(new Job
            {
                Go = go,
                Mesh = mf.sharedMesh,
                Verts = mf.sharedMesh.vertices,
                Body = body,
                Lat0 = lat0, Lon0 = lon0, Alt0 = alt0, Radius = radius,
                Offset = offset,
                Index = 0
            });
        }

        /// <summary>
        /// Przyklejenie siatki, ktora ma wlasny unos na wierzcholek (landuse: kazda
        /// klasa lezy na innej wysokosci, zeby nie walczyc o piksele). Unos liczymy
        /// jako roznice miedzy wierzcholkiem a siatka terenu, z ktorej go zbudowano,
        /// i przenosimy na prawdziwa wysokosc PQS.
        /// </summary>
        public static void RequestKeepingLift(GameObject go, CelestialBody body,
                                              double lat0, double lon0, double alt0,
                                              double radius, TerrainGrid grid)
        {
            if (go == null || body == null || body.pqsController == null || grid == null) return;

            var mf = go.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;

            lowQueue.Enqueue(new Job
            {
                Go = go,
                Mesh = mf.sharedMesh,
                Verts = mf.sharedMesh.vertices,
                Body = body,
                Lat0 = lat0, Lon0 = lon0, Alt0 = alt0, Radius = radius,
                Grid = grid,
                Index = 0
            });
        }

        /// <summary>Wolane raz na klatke z glownego watku.</summary>
        public static void Step()
        {
            if (current == null)
            {
                if (queue.Count > 0) current = queue.Dequeue();
                else if (lowQueue.Count > 0) current = lowQueue.Dequeue();
                else return;
            }

            Job j = current;

            // Kafel mogl zostac wyladowany w trakcie - wtedy nie ma czego poprawiac.
            if (j.Go == null || j.Mesh == null || j.Body.pqsController == null)
            {
                current = null;
                return;
            }

            clock.Reset();
            clock.Start();
            int i = j.Index;

            for (; i < j.Verts.Length; i++)
            {
                // Sprawdzanie zegara co 32 wierzcholki - samo pytanie o czas tez kosztuje.
                if ((i & 31) == 0 && i > j.Index && clock.Elapsed.TotalMilliseconds > BudgetMs) break;

                Vector3 v = j.Verts[i];

                double lat, lon;
                Geo.FromLocalMeters(v.x, v.z, j.Lat0, j.Lon0, j.Radius, out lat, out lon);

                double alt = TerrainGrid.RawAltitude(j.Body, lat, lon);
                double drop = Geo.CurvatureDrop(v.x, v.z, j.Radius);

                double lift = j.Offset;
                if (j.Grid != null)
                    lift = v.y - ((j.Grid.HeightAt(lat, lon) - j.Alt0) - drop);

                double y = (alt - j.Alt0) - drop + lift;

                j.Verts[i] = new Vector3(v.x, (float)y, v.z);
            }

            j.Index = i;

            if (j.Index >= j.Verts.Length)
            {
                j.Mesh.vertices = j.Verts;
                j.Mesh.RecalculateNormals();
                j.Mesh.RecalculateBounds();
                current = null;
            }
        }

        public static void Clear()
        {
            queue.Clear();
            lowQueue.Clear();
            current = null;
        }
    }
}
