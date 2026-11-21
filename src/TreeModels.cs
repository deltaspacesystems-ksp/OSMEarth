using UnityEngine;

namespace OSMRoads
{
    /// <summary>Jeden gatunek: pien + korona, kazde z wlasnym meshem i materialem.</summary>
    public class TreeSpecies
    {
        public Mesh TrunkMesh, LeafMesh;
        public Material TrunkMat, LeafMat;
        public Mesh FarMesh;          // LOD: sama bryla korony
        public Material FarMat;
        public float BaseScale = 1f;
        public bool Valid { get { return TrunkMesh != null || LeafMesh != null; } }
    }

    /// <summary>
    /// Dwa gatunki drzew generowane w kodzie przez ProceduralTree.
    ///
    /// Wczesniejsza wersja wyciagala meshe z Parallaxa przez GameDatabase i to sie
    /// nie udalo: te modele sa autorowane pod shader Parallaxa (rozwijanie kart
    /// w vertex shaderze, skala z jego configu, albedo pod niepewna nazwa
    /// wlasciwosci), wiec pod Standard wychodzily gigantyczne biale plachty.
    /// Wlasna geometria jest w metrach i nie zalezy od zadnego innego moda.
    /// </summary>
    public static class TreeModels
    {
        public static string Report = "(not initialized)";
        private static TreeSpecies[] species;

        public static TreeSpecies[] Species { get { Ensure(); return species; } }
        public static bool Ready { get { Ensure(); return species.Length > 0; } }

        private static void Ensure()
        {
            if (species != null) return;

            Texture2D bark = ProceduralTree.MakeBarkTexture();

            species = new[]
            {
                Make(false, bark),   // kind 0 - liscaste
                Make(true, bark)     // kind 1 - iglaste
            };

            Report = string.Format("procedural trees: {0} species, height {1:F0} m",
                                   species.Length, ProceduralTree.TreeHeightM);
            Debug.Log("[OSMRoads] " + Report);
        }

        private static TreeSpecies Make(bool conifer, Texture2D bark)
        {
            var s = new TreeSpecies
            {
                BaseScale = 1f,                     // geometria juz jest w metrach
                TrunkMesh = ProceduralTree.MakeTrunk(conifer),
                LeafMesh = ProceduralTree.MakeFoliage(conifer)
            };

            s.TrunkMat = MakeMat(bark, false, conifer ? "OSMTrunkConifer" : "OSMTrunkBroadleaf");
            s.FarMesh = ProceduralTree.MakeFarCrown(conifer);
            var flat = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            flat.SetPixel(0, 0, conifer ? new Color32(40, 70, 40, 255) : new Color32(62, 98, 40, 255));
            flat.Apply();
            s.FarMat = MakeMat(flat, false, conifer ? "OSMFarConifer" : "OSMFarBroadleaf");
            s.LeafMat = MakeMat(ProceduralTree.MakeFoliageTexture(conifer), true,
                                conifer ? "OSMLeafConifer" : "OSMLeafBroadleaf");
            return s;
        }

        private static Material MakeMat(Texture tex, bool cutout, string name)
        {
            Shader sh = Shader.Find("Standard");
            if (sh == null) sh = Shader.Find("KSP/Diffuse");
            if (sh == null) sh = Shader.Find("Legacy Shaders/Diffuse");

            var m = new Material(sh) { name = name };
            m.mainTexture = tex;
            m.enableInstancing = true;

            if (cutout && m.HasProperty("_Mode"))
            {
                // Standard w trybie Cutout trzeba ustawic recznie - samo _Mode nie
                // wystarcza, shader patrzy na keyword i na kolejke renderowania.
                m.SetFloat("_Mode", 1f);
                m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                m.SetInt("_ZWrite", 1);
                m.EnableKeyword("_ALPHATEST_ON");
                m.DisableKeyword("_ALPHABLEND_ON");
                m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                m.renderQueue = 2450;
                if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", ProceduralTree.LeafCutoff);
            }

            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.08f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            return m;
        }
    }
}
