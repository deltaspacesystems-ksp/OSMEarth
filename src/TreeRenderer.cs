using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Rysuje drzewa przez Graphics.DrawMeshInstanced - tak jak robi to Parallax.
    /// Zadnych GameObjectow: tysiac drzew to kilka wywolan rysowania, a nie tysiac
    /// obiektow w scenie.
    ///
    /// Macierze instancji trzymamy w ramce LOKALNEJ kafla. Kafel jedzie z obracajaca
    /// sie planeta i floating originem (PlaceTile leci co klatke), wiec w tej samej
    /// klatce mnozenie local * tileToWorld dawaloby wciaz to samo, gdybysmy je robili
    /// co drzewo co klatke - a to byl glowny koszt CPU przy lasach (setki tysiecy
    /// mnozen macierzy 4x4 na sekunde, tak jak w Parallaksie, ale bez GPU do tego).
    ///
    /// Parallax ten koszt zdejmuje z CPU calkowicie - rozmieszczenie i przeliczanie
    /// na swiat liczy compute shader na GPU (Distribute/Evaluate w TerrainScatters.compute),
    /// a CPU tylko czyta gotowa liczbe instancji z licznika bufora. U nas takiej
    /// infrastruktury (AssetBundle z Unity Editora, bo gotowego .compute nie da sie
    /// wrzucic jako sam plik do GameData) jeszcze nie ma, wiec przeliczanie macierzy
    /// zostaje na CPU - ale odswiezamy je rzadziej, nie co klatke. Bliskie kafle czesto,
    /// dalekie (i tak grubszy LOD) rzadko; kazdy kafel ma wlasne przesuniecie w czasie
    /// (seed), zeby odswiezenia nie spadaly wszystkie w tej samej klatce.
    /// </summary>
    public class TileTrees
    {
        // Unity przyjmuje najwyzej 1023 macierze na jedno wywolanie.
        private const int Batch = 1023;

        private readonly List<Matrix4x4[]> localByKind = new List<Matrix4x4[]>();
        private readonly List<int> kindOf = new List<int>();
        // LOD: co FarStep-te drzewo, powiekszone, zeby las z daleka mial podobne pokrycie
        private const int FarStep = 3;
        private readonly List<Matrix4x4[]> farByKind = new List<Matrix4x4[]>();
        private Matrix4x4[] scratch;

        // Cache macierzy juz przemnozonych przez tileToWorld - odswiezany co Cadence() klatek,
        // a nie co klatke. Draw()/DrawFar() tylko kopiuja z cache'a do scratch (Array.Copy, tanie).
        private readonly List<Matrix4x4[]> worldByKind = new List<Matrix4x4[]>();
        private readonly List<Matrix4x4[]> worldFarByKind = new List<Matrix4x4[]>();
        private bool worldBuilt, worldFarBuilt;

        // Przesuniecie w czasie tego kafla, zeby odswiezenia wielu kafli nie zbiegly sie w jednej klatce.
        private static int nextSeed;
        private readonly int seed = (nextSeed = nextSeed + 1) * 977;

        public int Count { get; private set; }

        public TileTrees(List<TreeInstance> trees, TreeSpecies[] species)
        {
            if (trees == null || trees.Count == 0 || species == null || species.Length == 0) return;
            int speciesCount = species.Length;

            var perKind = new List<Matrix4x4>[speciesCount];
            for (int i = 0; i < speciesCount; i++) perKind[i] = new List<Matrix4x4>();

            foreach (TreeInstance t in trees)
            {
                int k = t.Kind < speciesCount ? t.Kind : 0;
                // BaseScale normalizuje model do metrow, t.Scale to wariacja per drzewo.
                float sc = t.Scale * species[k].BaseScale;
                perKind[k].Add(Matrix4x4.TRS(t.Pos,
                                             Quaternion.Euler(0f, t.YawDeg, 0f),
                                             Vector3.one * sc));
            }

            int max = 0;
            for (int i = 0; i < speciesCount; i++)
            {
                if (perKind[i].Count == 0) continue;
                Matrix4x4[] local = perKind[i].ToArray();
                localByKind.Add(local);
                worldByKind.Add(new Matrix4x4[local.Length]);

                var far = new List<Matrix4x4>(perKind[i].Count / FarStep + 1);
                for (int j = 0; j < perKind[i].Count; j += FarStep)
                    far.Add(perKind[i][j] * Matrix4x4.Scale(new Vector3(1.45f, 1.1f, 1.45f)));
                Matrix4x4[] farArr = far.ToArray();
                farByKind.Add(farArr);
                worldFarByKind.Add(new Matrix4x4[farArr.Length]);

                kindOf.Add(i);
                Count += perKind[i].Count;
                if (perKind[i].Count > max) max = perKind[i].Count;
                if (farArr.Length > max) max = farArr.Length;
            }

            scratch = new Matrix4x4[Mathf.Min(max, Batch)];
        }

        /// <summary>Co ile klatek odswiezyc macierze na dana odleglosc - blisko czesto, daleko rzadko.</summary>
        private static int Cadence(float dist, float near, float mid)
        {
            if (dist < near) return 1;
            if (dist < mid) return 4;
            return 12;
        }

        private void RefreshWorld(Matrix4x4 tileToWorld)
        {
            for (int g = 0; g < localByKind.Count; g++)
            {
                Matrix4x4[] local = localByKind[g];
                Matrix4x4[] world = worldByKind[g];
                for (int i = 0; i < local.Length; i++) world[i] = tileToWorld * local[i];
            }
            worldBuilt = true;
        }

        private void RefreshWorldFar(Matrix4x4 tileToWorld)
        {
            for (int g = 0; g < farByKind.Count; g++)
            {
                Matrix4x4[] local = farByKind[g];
                Matrix4x4[] world = worldFarByKind[g];
                for (int i = 0; i < local.Length; i++) world[i] = tileToWorld * local[i];
            }
            worldFarBuilt = true;
        }

        /// <summary>Daleki LOD: bryly koron, co trzecie drzewo.</summary>
        public void DrawFar(Matrix4x4 tileToWorld, TreeSpecies[] species, int layer, float dist)
        {
            if (Count == 0 || scratch == null) return;

            int cadence = Cadence(dist, 6000f, 20000f);
            if (!worldFarBuilt || (Time.frameCount + seed) % cadence == 0) RefreshWorldFar(tileToWorld);

            for (int g = 0; g < worldFarByKind.Count; g++)
            {
                Matrix4x4[] world = worldFarByKind[g];
                TreeSpecies sp = species[kindOf[g]];
                if (sp.FarMesh == null || sp.FarMat == null) continue;
                for (int start = 0; start < world.Length; start += Batch)
                {
                    int len = Mathf.Min(Batch, world.Length - start);
                    System.Array.Copy(world, start, scratch, 0, len);
                    Graphics.DrawMeshInstanced(sp.FarMesh, 0, sp.FarMat, scratch, len, null,
                                               UnityEngine.Rendering.ShadowCastingMode.Off, false, layer);
                }
            }
        }

        public void Draw(Matrix4x4 tileToWorld, TreeSpecies[] species, int layer, float dist)
        {
            if (Count == 0 || scratch == null) return;

            int cadence = Cadence(dist, 400f, 1000f);
            if (!worldBuilt || (Time.frameCount + seed) % cadence == 0) RefreshWorld(tileToWorld);

            for (int g = 0; g < worldByKind.Count; g++)
            {
                Matrix4x4[] world = worldByKind[g];
                TreeSpecies sp = species[kindOf[g]];

                for (int start = 0; start < world.Length; start += Batch)
                {
                    int len = Mathf.Min(Batch, world.Length - start);
                    System.Array.Copy(world, start, scratch, 0, len);

                    if (sp.TrunkMesh != null && sp.TrunkMat != null)
                        Graphics.DrawMeshInstanced(sp.TrunkMesh, 0, sp.TrunkMat,
                                                   scratch, len, null,
                                                   UnityEngine.Rendering.ShadowCastingMode.Off,
                                                   false, layer);

                    if (sp.LeafMesh != null && sp.LeafMat != null)
                        Graphics.DrawMeshInstanced(sp.LeafMesh, 0, sp.LeafMat,
                                                   scratch, len, null,
                                                   UnityEngine.Rendering.ShadowCastingMode.Off,
                                                   false, layer);
                }
            }
        }
    }
}
