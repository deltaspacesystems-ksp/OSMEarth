using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace OSMRoads
{
    /// <summary>
    /// Surowe tablice siatki. Powstaja na watku roboczym (Vector2/Vector3 to zwykle
    /// struktury, wiec wolno je tworzyc poza glownym watkiem), a dopiero ToMesh()
    /// - juz na glownym watku - robi z nich obiekt Unity.
    /// </summary>
    public class MeshData
    {
        public string Name;
        public Vector3[] Verts;
        public Vector2[] Uvs;
        public int[][] SubTris;

        public int VertexCount { get { return Verts == null ? 0 : Verts.Length; } }

        public int TriangleCount
        {
            get
            {
                if (SubTris == null) return 0;
                int n = 0;
                for (int i = 0; i < SubTris.Length; i++)
                    if (SubTris[i] != null) n += SubTris[i].Length;
                return n / 3;
            }
        }

        public static MeshData From(List<Vector3> verts, List<Vector2> uvs,
                                    List<int>[] tris, string name)
        {
            if (verts.Count == 0) return null;

            var d = new MeshData
            {
                Name = name,
                Verts = verts.ToArray(),
                Uvs = uvs.ToArray(),
                SubTris = new int[tris.Length][]
            };
            for (int i = 0; i < tris.Length; i++)
                d.SubTris[i] = tris[i].ToArray();
            return d;
        }

        /// <summary>TYLKO glowny watek - tworzy obiekt Unity.</summary>
        public Mesh ToMesh()
        {
            var m = new Mesh { name = Name };
            m.indexFormat = IndexFormat.UInt32;
            m.vertices = Verts;
            m.uv = Uvs;
            m.subMeshCount = SubTris.Length;
            for (int i = 0; i < SubTris.Length; i++)
                m.SetTriangles(SubTris[i], i);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
