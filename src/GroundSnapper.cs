using System;
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
    ///
    /// KOMPENSACJA UGIECIA. Dokladna wysokosc PQS to nie to, co widac na ekranie:
    /// teren jest rysowany jako siatka czworokatow, liniowo interpolowana miedzy
    /// wierzcholkami. W zaglebieniu cieciwa lezy NAD prawdziwa powierzchnia o
    /// ok. c * s^2 / 8 (c = wkleslosc terenu, s = odstep wierzcholkow), wiec teren
    /// przykrywa droge polozona na dokladnej wysokosci - stad platy zamiast jezdni.
    /// Czworokaty PQS rosna z odleglocia od kamery, wiec unos rosnie z kwadratem
    /// odleglosci, a tylko tam, gdzie teren jest wklesly (wklesly = siatka nad
    /// prawdziwa powierzchnia; na wypuklosci siatka jest pod nia i nic nie trzeba).
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

            // kompensacja ugiecia (tylko drogi)
            public TerrainGrid Sag;
            public float SagK;
            public double CamE, CamN, CamAlt;
            public double VesE, VesN, VesAlt;
            public bool HasVessel;
            public bool Roads;           // wstega drog: pobocza + trafianie w siatke PQS
            public float[] Role;         // na wierzcholek: unos z uv2
            public float MaxDy;          // najwieksza zmiana wysokosci wierzcholka KOLIDERA przy tym przyklejeniu
            public Vector3[] Phys;       // wierzcholki dla fizyki: bez zapasu na ugiecie (nie zaleza od kamery)
            public Vector3[] PhysOld;    // poprzednia siatka fizyki (null = jeszcze nie bylo)
            public bool[] Ground;        // na wierzcholek: unos liczony od gruntu, nie od jezdni
            public bool[] FixedY;        // na wierzcholek: wysokosc ustalona przy budowie (pas startowy)
            public Action Done;
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
            RequestRoads(go, body, lat0, lon0, alt0, radius, offset, null, 0f, 0, 0, 0, 0, 0, 0, null);
        }

        /// <summary>
        /// Przyklejenie drog. sagGrid + sagK &gt; 0 wlacza kompensacje ugiecia siatki
        /// terenu; kamera i statek to punkty odniesienia (liczy sie blizszy z nich -
        /// wokol statku fizyka i wzrok wymagaja dokladnosci, wokol kamery sam wzrok).
        /// </summary>
        public static void RequestRoads(GameObject go, CelestialBody body,
                                        double lat0, double lon0, double alt0, double radius, float offset,
                                        TerrainGrid sagGrid, float sagK,
                                        double camLat, double camLon, double camAlt,
                                        double vesLat, double vesLon, double vesAlt,
                                        Action done)
        {
            if (go == null || body == null || body.pqsController == null) { if (done != null) done(); return; }

            var mf = go.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) { if (done != null) done(); return; }

            bool[] gnd, fix;
            var job = new Job
            {
                Go = go,
                Mesh = mf.sharedMesh,
                Verts = mf.sharedMesh.vertices,
                Body = body,
                Lat0 = lat0, Lon0 = lon0, Alt0 = alt0, Radius = radius,
                Offset = offset,
                Index = 0,
                Sag = sagGrid,
                SagK = sagK,
                Roads = true,
                Role = Roles(mf.sharedMesh, out gnd, out fix),
                Ground = gnd,
                FixedY = fix,
                Phys = (Vector3[])mf.sharedMesh.vertices.Clone(),
                PhysOld = go.GetComponent<PhysicsMeshHolder>() != null && go.GetComponent<PhysicsMeshHolder>().Mesh != null
                          ? go.GetComponent<PhysicsMeshHolder>().Mesh.vertices : null,
                Done = done
            };

            if (sagGrid != null && sagK > 0f)
            {
                Geo.ToLocalMeters(camLat, camLon, lat0, lon0, radius, out job.CamE, out job.CamN);
                job.CamAlt = camAlt;
                job.HasVessel = !(vesLat == 0 && vesLon == 0 && vesAlt == 0);
                if (job.HasVessel)
                {
                    Geo.ToLocalMeters(vesLat, vesLon, lat0, lon0, radius, out job.VesE, out job.VesN);
                    job.VesAlt = vesAlt;
                }
            }

            queue.Enqueue(job);
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
        /// <summary>true = nie podmieniaj teraz kolidera tego obiektu (pod jadacym pojazdem).</summary>
        public static Func<GameObject, bool> HoldCollider;
        private static readonly List<GameObject> held = new List<GameObject>();

        public static void Step()
        {
            for (int h = held.Count - 1; h >= 0; h--)
            {
                GameObject g = held[h];
                if (g == null) { held.RemoveAt(h); continue; }
                if (HoldCollider == null || !HoldCollider(g)) { ColliderQueue.Refresh(g); held.RemoveAt(h); }
            }

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
                if (j.Done != null) j.Done();
                return;
            }

            clock.Reset();
            clock.Start();
            int i = j.Index;
            bool sag = j.Sag != null && j.SagK > 0f;

            for (; i < j.Verts.Length; i++)
            {
                // Sprawdzanie zegara co 32 wierzcholki - samo pytanie o czas tez kosztuje.
                if ((i & 31) == 0 && i > j.Index && clock.Elapsed.TotalMilliseconds > BudgetMs) break;

                Vector3 v = j.Verts[i];
                if (j.FixedY != null && j.FixedY[i])
                {
                    // pas startowy: plaski, wiec teren go nie dotyczy
                    if (j.Phys != null) PhysSet(j, i, v);
                    continue;
                }

                double lat, lon;
                Geo.FromTangent(v.x, v.z, j.Lat0, j.Lon0, j.Radius + j.Alt0, out lat, out lon);

                double alt = TerrainGrid.RawAltitude(j.Body, lat, lon);
                double pqsAlt = alt;
                double drop = Geo.TangentDrop(v.x, v.z, j.Radius + j.Alt0);

                double lift = j.Offset;
                if (j.Grid != null)
                    lift = v.y - ((j.Grid.HeightAt(lat, lon) - j.Alt0) - drop);

                // Drogi: jesli pod punktem jest kolider kwadratu PQS, to on JEST teren,
                // ktory widac i po ktorym jedzie auto (na Ziemi SOL maxLevel 13 = wierzcholek
                // co ~87 m, plasko miedzy nimi) - kladziemy droge na nim, bez zgadywania ugiecia.
                bool onMesh = false;
                bool groundRole = false;
                if (j.Roads)
                {
                    double meshAlt;
                    if (MeshAltitude(j.Body, lat, lon, alt, out meshAlt)) { alt = meshAlt; onMesh = true; }
                    // wysokosc od gruntu (pobocza, trawniki, wjazdy przy domu) - bez unosu jezdni
                    groundRole = j.Role != null && j.Ground[i];
                }
                if (groundRole) lift = j.Role[i];
                else if (j.Role != null) lift += j.Role[i];

                double sagLift = 0.0;
                if (sag)
                {
                    // odleglosc 3D do kamery i do statku - liczy sie blizszy punkt odniesienia
                    double de = v.x - j.CamE, dn = v.z - j.CamN, dh = j.CamAlt - alt;
                    double d2 = de * de + dn * dn + dh * dh;
                    if (j.HasVessel)
                    {
                        double ve = v.x - j.VesE, vn = v.z - j.VesN, vh = j.VesAlt - alt;
                        double v2 = ve * ve + vn * vn + vh * vh;
                        if (v2 < d2) d2 = v2;
                    }
                    sagLift = SagMath.SagLift(Math.Sqrt(d2), SagMath.Concavity(j.Sag, lat, lon, j.Radius), j.SagK);
                }

                // Na siatce PQS droga lezy na koliderze, ale z daleka (kamera wyzej / dalej)
                // PQS rysuje rzadsza siatke niz kolider - wtedy wygrywa zapas na ugiecie liczony
                // od dokladnej wysokosci. Wyzsza z obu: z bliska kolider, z daleka zapas.
                double y = (alt - j.Alt0) - drop + lift;
                if (j.Phys != null) PhysSet(j, i, new Vector3(v.x, (float)y, v.z));     // fizyka: bez zapasu
                if (sag)
                {
                    double ySag = (pqsAlt - j.Alt0) - drop + lift + sagLift;
                    if (!onMesh || ySag > y) y = ySag;
                }
                j.Verts[i] = new Vector3(v.x, (float)y, v.z);
            }

            j.Index = i;

            if (j.Index >= j.Verts.Length)
            {
                j.Mesh.vertices = j.Verts;
                j.Mesh.RecalculateNormals();
                j.Mesh.RecalculateBounds();

                // Collider zostal upieczony ze STAREJ siatki - Unity nie odswieza go, gdy
                // zmienia sie wierzcholki. Bez tego auto jechaloby po duchu poprzedniej wersji.
                //
                // Drogi maja OSOBNA siatke dla kolidera: dokladnie na terenie, bez zapasu na
                // ugiecie. Zapas zalezy od odleglosci od kamery, wiec przy dojezdzaniu malal i
                // droga pod kolami opadala o 10-20 cm naraz - lazik podskakiwal. Siatka fizyki
                // nie zalezy od kamery, wiec kolejne przyklejenia jej nie ruszaja i kolider
                // nie jest przepiekany pod jadacym pojazdem.
                bool refresh = true;
                if (j.Phys != null)
                {
                    var holder = j.Go.GetComponent<PhysicsMeshHolder>();
                    if (holder == null) holder = j.Go.AddComponent<PhysicsMeshHolder>();
                    bool fresh = holder.Mesh == null;
                    if (fresh)
                    {
                        holder.Mesh = UnityEngine.Object.Instantiate(j.Mesh);
                        // plamy swiatla latarni nie maja fizyki (lezaly 7 cm nad jezdnia - progi pod kolami)
                        if (holder.Mesh.subMeshCount > RoadMeshBuilder.SubLampPool)
                            holder.Mesh.SetTriangles(new int[0], RoadMeshBuilder.SubLampPool);
                    }
                    holder.Mesh.vertices = j.Phys;
                    holder.Mesh.RecalculateBounds();
                    refresh = fresh || j.MaxDy >= 0.03f;
                }
                if (refresh)
                {
                    if (HoldCollider != null && HoldCollider(j.Go)) { if (!held.Contains(j.Go)) held.Add(j.Go); }
                    else ColliderQueue.Refresh(j.Go);
                }

                current = null;
                if (j.Done != null) j.Done();
            }
        }

        /// <summary>
        /// Rola kazdego wierzcholka wstegi. Pobocza (U = SkirtUL/UR) schodza pod teren.
        /// Reszta dostaje maly unos wg klasy: tam, gdzie dwie drogi na siebie zachodza,
        /// wazniejsza lezy 1.5 cm wyzej - zamiast migotac i robic progi, jedna czysto
        /// przykrywa druga. Laty w rogach sa o 1 cm nizej od swojej wstegi (tylko lataja dziury).
        /// </summary>
        /// <summary>Unos kazdego wierzcholka z uv2 (RoadMeshBuilder.Road/Ground):
        /// x = unos [m], y = 1 gdy wzgledem gruntu, 0 gdy wzgledem jezdni.</summary>
        private static float[] Roles(Mesh m, out bool[] ground, out bool[] fixedY)
        {
            ground = null;
            fixedY = null;
            Vector2[] u2 = m.uv2;
            if (u2 == null || u2.Length != m.vertexCount) return null;
            var role = new float[u2.Length];
            ground = new bool[u2.Length];
            fixedY = new bool[u2.Length];
            for (int i = 0; i < u2.Length; i++)
            {
                role[i] = u2[i].x;
                ground[i] = u2[i].y > 0.5f && u2[i].y < 1.5f;
                fixedY[i] = u2[i].y > 1.5f;
            }
            return role;
        }
        private static readonly RaycastHit[] hits = new RaycastHit[8];
        private static readonly Dictionary<int, bool> isQuad = new Dictionary<int, bool>();

        /// <summary>Wysokosc widocznej/fizycznej siatki terenu (kolider kwadratu PQS) pod punktem.
        /// Kolidery drog tez sa na warstwie 15 - pomijamy wszystko, co nie jest kwadratem PQS.</summary>
        private static void PhysSet(Job j, int i, Vector3 p)
        {
            j.Phys[i] = p;
            if (j.PhysOld == null || i >= j.PhysOld.Length) { j.MaxDy = float.MaxValue; return; }
            float dy = Mathf.Abs(p.y - j.PhysOld[i].y);
            if (dy > j.MaxDy) j.MaxDy = dy;
        }

        public static bool MeshAltitude(CelestialBody body, double lat, double lon, double pqsAlt, out double alt)
        {
            alt = pqsAlt;
            const double above = 40.0;
            Vector3d top = body.GetWorldSurfacePosition(lat, lon, pqsAlt + above);
            Vector3 down = -(Vector3)(top - body.position).normalized;
            int n = Physics.RaycastNonAlloc((Vector3)top, down, hits, 90f, 1 << 15, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int k = 0; k < n; k++)
            {
                Collider c = hits[k].collider;
                if (c == null) continue;
                int id = c.GetInstanceID();
                bool q;
                if (!isQuad.TryGetValue(id, out q))
                {
                    q = c.GetComponent<PQ>() != null;
                    if (isQuad.Count > 20000) isQuad.Clear();
                    isQuad[id] = q;
                }
                if (q && hits[k].distance < best) best = hits[k].distance;
            }
            if (best == float.MaxValue) return false;
            alt = pqsAlt + above - best;
            return true;
        }

        public static void Clear()
        {
            queue.Clear();
            lowQueue.Clear();
            current = null;
        }
    }
}
