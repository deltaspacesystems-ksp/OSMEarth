using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Rysuje drzewa przez Graphics.DrawMeshInstanced - tak jak robi to Parallax.
    /// Zadnych GameObjectow: tysiac drzew to kilka wywolan rysowania, a nie tysiac
    /// obiektow w scenie.
    ///
    /// Macierze instancji trzymamy w ramce LOKALNEJ kafla i mnozymy przez jego
    /// localToWorldMatrix co klatke. Dzieki temu drzewa jada z obracajaca sie
    /// planeta i floating origin dokladnie tak samo jak siatki.
    /// </summary>
    public class TileTrees
    {
        // Unity przyjmuje najwyzej 1023 macierze na jedno wywolanie.
        private const int Batch = 1023;

        private readonly List<Matrix4x4[]> localByKind = new List<Matrix4x4[]>();
        private readonly List<int> kindOf = new List<int>();
        private Matrix4x4[] scratch;

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
                localByKind.Add(perKind[i].ToArray());
                kindOf.Add(i);
                Count += perKind[i].Count;
                if (perKind[i].Count > max) max = perKind[i].Count;
            }

            scratch = new Matrix4x4[Mathf.Min(max, Batch)];
        }

        public void Draw(Matrix4x4 tileToWorld, TreeSpecies[] species, int layer)
        {
            if (Count == 0 || scratch == null) return;

            for (int g = 0; g < localByKind.Count; g++)
            {
                Matrix4x4[] local = localByKind[g];
                TreeSpecies sp = species[kindOf[g]];

                for (int start = 0; start < local.Length; start += Batch)
                {
                    int len = Mathf.Min(Batch, local.Length - start);
                    for (int i = 0; i < len; i++)
                        scratch[i] = tileToWorld * local[start + i];

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
