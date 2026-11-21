using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Materialy wspoldzielone przez wszystkie kafle. Trzymane statycznie, bo
    /// kazdy kafel uzywa tych samych - dzieki temu zmiana emisji na noc to jedna
    /// operacja, a nie przejscie po wszystkich rendererach.
    /// </summary>
    public static class MaterialLib
    {
        public const int FacadeBuckets = MaterialPalette.FacadeBuckets;

        private static Material[] facades;
        private static Material plinth;
        private static Material roofEdge;
        private static Material roofTiles;
        private static Material[] roofColored;
        private static Material[] houseFacades;      // elewacje domow jednorodzinnych       // dachowka w kolorach palety (budynki 3D)
        private static Material roof;
        private static Material church, eaveShadow;
        private static Material[] roads;
        private static Material lampPole;
        private static Material lampHead;

        /// <summary>Materialy z emisja + nazwa wlasciwosci koloru emisji.
        /// Nazwa zalezy od shadera, ktory faktycznie udalo sie znalezc.</summary>
        private static readonly List<KeyValuePair<Material, string>> emissive =
            new List<KeyValuePair<Material, string>>();

        /// <summary>Klosze latarni trzymamy osobno: swieca duzo mocniej niz okna
        /// i innym, chlodniejszym swiatlem, wiec nie moga isc tym samym mnoznikiem.</summary>
        private static string lampEmissionProp;

        public static string ShaderReport = "(not initialized yet)";

        public static Material Roof { get { Ensure(); return roof; } }

        /// <summary>Jeden material na klase drogi; indeks = (int)RoadClass,
        /// czyli dokladnie indeks submesha z RoadMeshBuilder.</summary>
        public static Material[] RoadMaterials() { Ensure(); return roads; }

        /// <summary>0 = slupy, 1 = klosze. Zgadza sie z submeshami siatki latarni.</summary>
        public static Material[] LampMaterials()
        {
            Ensure();
            return new[] { lampPole, lampHead };
        }

        private static Material[] landuse;

        /// <summary>Jeden material na klase pokrycia terenu; indeks = (int)AreaKind,
        /// czyli dokladnie indeks submesha z LanduseMeshBuilder.</summary>
        public static Material[] LanduseMaterials()
        {
            Ensure();
            if (landuse != null) return landuse;

            Shader sh = FindShader();
            landuse = new Material[AreaKinds.Count];
            for (int i = 0; i < AreaKinds.Count; i++)
            {
                var k = (AreaKind)i;
                var m = new Material(sh) { name = "OSMLanduse_" + k };
                m.color = AreaKindColors.Tint(k);
                if (m.HasProperty("_Glossiness"))
                    m.SetFloat("_Glossiness", k == AreaKind.Water ? 0.72f : 0.06f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                landuse[i] = m;
            }
            return landuse;
        }

        /// <summary>Kolejnosc musi sie zgadzac z indeksami submeshy
        /// w BuildingMeshBuilder: 0..N-1 elewacje, N parter, N+1 attyka,
        /// N+2 dach plaski, N+3 dach spadzisty.</summary>
        public static Material[] BuildingMaterials()
        {
            Ensure();
            var m = new Material[BuildingMeshBuilder.SubCount];
            for (int i = 0; i < FacadeBuckets; i++) m[i] = facades[i];
            m[FacadeBuckets] = plinth;
            m[FacadeBuckets + 1] = roofEdge;
            m[FacadeBuckets + 2] = roof;
            m[FacadeBuckets + 3] = roofTiles;
            for (int i = 0; i < MaterialPalette.RoofColors.Length; i++) m[FacadeBuckets + 4 + i] = roofColored[i];
            for (int i = 0; i < houseFacades.Length; i++) m[FacadeBuckets + 4 + MaterialPalette.RoofColors.Length + i] = houseFacades[i];
            m[BuildingMeshBuilder.ChurchSub] = church;
            m[BuildingMeshBuilder.EaveShadowSub] = eaveShadow;
            return m;
        }

        /// <summary>
        /// Unity wycina z builda wbudowane shadery, ktorych scena nie uzywa, wiec
        /// Shader.Find("Standard") w KSP potrafi zwrocic null. Probujemy po kolei
        /// i RAPORTUJEMY, co wyszlo - bez tego nie wiadomo, czemu cos nie ma tekstury.
        /// </summary>
        private static Shader FindShader()
        {
            string[] candidates =
            {
                "Standard",
                "KSP/Emissive/Diffuse",
                "KSP/Diffuse",
                "KSP/Bumped Specular",
                "Legacy Shaders/Diffuse"
            };

            foreach (string name in candidates)
            {
                Shader s = Shader.Find(name);
                if (s != null)
                {
                    ShaderReport = "shader in use: " + s.name;
                    Debug.Log("[OSMRoads] " + ShaderReport);
                    return s;
                }
                Debug.Log("[OSMRoads] Shader.Find(\"" + name + "\") = null");
            }

            ShaderReport = "NO shader - buildings will render pink";
            Debug.LogError("[OSMRoads] " + ShaderReport);
            return null;
        }

        private static void Ensure()
        {
            if (facades != null) return;

            Shader sh = FindShader();

            facades = new Material[FacadeBuckets];
            for (int i = 0; i < FacadeBuckets; i++)
            {
                var m = new Material(sh) { name = "OSMFacade" + i };

                // Tekstura BEZWARUNKOWO - wczesniej siedziala pod ifem na shader
                // Standard i przy fallbacku budynki wychodzily jako gole bryly.
                m.mainTexture = FacadeTexture.Albedo;
                m.color = MaterialPalette.FacadeTints[i];

                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", MaterialPalette.FacadeGloss);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);

                SetupEmission(m, FacadeTexture.Emission);
                facades[i] = m;
            }

            plinth = new Material(sh) { name = "OSMPlinth" };
            plinth.mainTexture = FacadeTexture.PlinthAlbedo;
            plinth.color = MaterialPalette.Plinth;
            if (plinth.HasProperty("_Glossiness")) plinth.SetFloat("_Glossiness", MaterialPalette.PlinthGloss);
            if (plinth.HasProperty("_Metallic")) plinth.SetFloat("_Metallic", 0f);
            SetupEmission(plinth, FacadeTexture.PlinthEmission);

            // Attyka: gladki beton, bez tekstury papy - to jest pionowy gzyms,
            // nie powierzchnia dachu.
            roofEdge = new Material(sh) { name = "OSMRoofEdge" };
            roofEdge.color = MaterialPalette.RoofEdge;
            if (roofEdge.HasProperty("_Glossiness")) roofEdge.SetFloat("_Glossiness", MaterialPalette.RoofEdgeGloss);
            if (roofEdge.HasProperty("_Metallic")) roofEdge.SetFloat("_Metallic", 0f);

            roof = new Material(sh) { name = "OSMRoofFlat" };
            roof.mainTexture = FacadeTexture.RoofFlat;
            roof.color = MaterialPalette.RoofFlat;
            if (roof.HasProperty("_Glossiness")) roof.SetFloat("_Glossiness", MaterialPalette.RoofFlatGloss);
            if (roof.HasProperty("_Metallic")) roof.SetFloat("_Metallic", 0f);

            houseFacades = new Material[BuildingMeshBuilder.HouseBuckets];
            for (int i = 0; i < houseFacades.Length; i++)
            {
                var hm = new Material(sh) { name = "OSMHouse" + i };
                hm.mainTexture = FacadeTexture.HouseAlbedo;
                hm.color = MaterialPalette.HouseTints[i % MaterialPalette.HouseTints.Length];
                if (hm.HasProperty("_Glossiness")) hm.SetFloat("_Glossiness", 0.12f);
                if (hm.HasProperty("_Metallic")) hm.SetFloat("_Metallic", 0f);
                SetupEmission(hm, FacadeTexture.HouseEmission);
                houseFacades[i] = hm;
            }

            roofColored = new Material[MaterialPalette.RoofColors.Length];
            for (int i = 0; i < roofColored.Length; i++)
            {
                var rc = new Material(sh) { name = "OSMRoofColor" + i };
                rc.mainTexture = FacadeTexture.RoofTilesNeutral;
                rc.color = MaterialPalette.RoofColors[i];
                if (rc.HasProperty("_Glossiness")) rc.SetFloat("_Glossiness", MaterialPalette.RoofTilesGloss);
                if (rc.HasProperty("_Metallic")) rc.SetFloat("_Metallic", 0f);
                roofColored[i] = rc;
            }

            church = new Material(sh) { name = "OSMChurch" };
            church.mainTexture = FacadeTexture.ChurchAlbedo;
            church.color = new Color(0.96f, 0.93f, 0.86f);
            if (church.HasProperty("_Glossiness")) church.SetFloat("_Glossiness", 0.08f);
            if (church.HasProperty("_Metallic")) church.SetFloat("_Metallic", 0f);
            SetupEmission(church, FacadeTexture.ChurchEmission);

            eaveShadow = new Material(sh) { name = "OSMEaveShadow" };
            eaveShadow.color = new Color(0.42f, 0.41f, 0.40f);
            if (eaveShadow.HasProperty("_Glossiness")) eaveShadow.SetFloat("_Glossiness", 0f);
            if (eaveShadow.HasProperty("_Metallic")) eaveShadow.SetFloat("_Metallic", 0f);

            roofTiles = new Material(sh) { name = "OSMRoofTiles" };
            roofTiles.mainTexture = FacadeTexture.RoofTiles;
            roofTiles.color = MaterialPalette.RoofTiles;
            if (roofTiles.HasProperty("_Glossiness")) roofTiles.SetFloat("_Glossiness", MaterialPalette.RoofTilesGloss);
            if (roofTiles.HasProperty("_Metallic")) roofTiles.SetFloat("_Metallic", 0f);

            // --- drogi: material na klase, kazdy z wlasna nawierzchnia ---
            // Kolor bialy, bo cala barwa siedzi juz w teksturze. Wczesniejszy
            // ciemny _Color mnozylby sie z nia i zzeral oznakowanie.
            // Za klasami jezdni: kraweznik, kostka (chodniki, wjazdy), trawnik - StreetsideBuilder.
            roads = new Material[RoadMeshBuilder.Submeshes];
            for (int i = 0; i < roads.Length; i++)
            {
                var c = (RoadClass)i;
                var m = new Material(sh) { name = "OSMRoad_" + c };
                m.mainTexture = RoadTexture.For(c);
                m.color = Color.white;
                if (m.HasProperty("_Glossiness"))
                    m.SetFloat("_Glossiness", i < RoadClasses.Count ? MaterialPalette.RoadGloss(c)
                                              : RoadMeshBuilder.PlainSub(RoadMeshBuilder.PlainClass(i)) == i ? MaterialPalette.RoadGloss(RoadMeshBuilder.PlainClass(i))
                                              : i == RoadMeshBuilder.SubGrass ? 0.04f : 0.18f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                roads[i] = m;
            }

            // plamy swiatla latarni: addytywne (czarne za dnia); bez shadera czastek - niewidoczne
            roads[RoadMeshBuilder.SubLampPool] = LampLights.PoolMaterial ?? Invisible(sh);

            lampPole = new Material(sh) { name = "OSMLampPole" };
            lampPole.color = MaterialPalette.LampPole;
            if (lampPole.HasProperty("_Glossiness")) lampPole.SetFloat("_Glossiness", MaterialPalette.LampPoleGloss);
            if (lampPole.HasProperty("_Metallic")) lampPole.SetFloat("_Metallic", MaterialPalette.LampPoleMetallic);

            lampHead = new Material(sh) { name = "OSMLampHead" };
            lampHead.color = MaterialPalette.LampHead;
            if (lampHead.HasProperty("_Glossiness")) lampHead.SetFloat("_Glossiness", MaterialPalette.LampHeadGloss);
            if (lampHead.HasProperty("_Metallic")) lampHead.SetFloat("_Metallic", MaterialPalette.LampHeadMetallic);
            SetupLampEmission(lampHead);
        }

        private static Material Invisible(Shader sh)
        {
            var m = new Material(sh) { name = "OSMInvisible", color = new Color(0, 0, 0, 0) };
            if (m.HasProperty("_Mode"))
            {
                m.SetFloat("_Mode", 2f);
                m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.EnableKeyword("_ALPHABLEND_ON");
                m.renderQueue = 3000;
            }
            return m;
        }

        /// <summary>Podpina mape emisji pod ta nazwe wlasciwosci, ktora ma dany shader.</summary>
        private static void SetupEmission(Material m, Texture2D emissionMap)
        {
            if (m.HasProperty("_EmissionMap") && m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetTexture("_EmissionMap", emissionMap);
                m.SetColor("_EmissionColor", Color.black);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                emissive.Add(new KeyValuePair<Material, string>(m, "_EmissionColor"));
            }
            else if (m.HasProperty("_Emissive") && m.HasProperty("_EmissiveColor"))
            {
                m.SetTexture("_Emissive", emissionMap);
                m.SetColor("_EmissiveColor", Color.black);
                emissive.Add(new KeyValuePair<Material, string>(m, "_EmissiveColor"));
            }
        }

        /// <summary>Klosz swieci calym trojkatem, wiec nie potrzebuje mapy emisji -
        /// wystarczy sam kolor. Zapamietujemy tylko nazwe wlasciwosci.</summary>
        private static void SetupLampEmission(Material m)
        {
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", Color.black);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                lampEmissionProp = "_EmissionColor";
            }
            else if (m.HasProperty("_EmissiveColor"))
            {
                m.SetColor("_EmissiveColor", Color.black);
                lampEmissionProp = "_EmissiveColor";
            }
        }

        /// <summary>0 = pelny dzien, 1 = noc. Wolane raz na klatke.</summary>
        public static void SetNightFactor(float night)
        {
            Ensure();

            Color c = new Color(1.00f, 0.82f, 0.55f) * (night * 1.15f);
            for (int i = 0; i < emissive.Count; i++)
                emissive[i].Key.SetColor(emissive[i].Value, c);

            if (lampEmissionProp != null)
            {
                // Sodowa latarnia jest pomaranczowa i wyraznie jasniejsza od okna -
                // to ona ma rysowac siatke ulic widoczna z powietrza.
                lampHead.SetColor(lampEmissionProp,
                                  new Color(1.00f, 0.72f, 0.36f) * (night * 3.2f));
            }
        }

        public static bool HasEmission { get { Ensure(); return emissive.Count > 0; } }
    }
}
