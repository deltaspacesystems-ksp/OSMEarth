using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Warstwa OSM naciagnieta na kule planety w widoku mapy.
    ///
    /// Nie podmieniamy materialu scaledBody - ten nalezy do Mirage'a i walka o niego
    /// skonczylaby sie zle. Zamiast tego dokladamy WLASNY plat sfery kawalek nad
    /// powierzchnia: satelita Mirage'a maluje spod spodu, my na wierzchu, chmury
    /// zostaja wyzej.
    ///
    /// Siatka jest na sferze JEDNOSTKOWEJ, a promien wchodzi przez localScale
    /// skompensowana o skale rodzica. Dzieki temu nie musimy wiedziec, czy
    /// scaledBody trzyma promien w skali transformu, czy w samym meshu - a to
    /// roznie bywa miedzy planet packami.
    /// </summary>
    public class ScaledOverlay
    {
        /// <summary>KSP: warstwa scaled space.</summary>
        private const int LayerScaledSpace = 10;

        /// <summary>
        /// O ile podniesc plat ponad kule o promieniu rownikowym.
        ///
        /// 1.0008 przy promieniu Ziemi to ok. 5 km. Poprzednie 1.00005 dawalo
        /// 300 m i to bylo za malo: mesh scaled space NIE jest gladka kula,
        /// tylko niesie rzezbe terenu, wiec plat na 300 m nad poziomem morza
        /// tonal w kazdym wzniesieniu. Chmury EVE siedza ok. 10 km, wiec
        /// 5 km miesci sie miedzy jednym a drugim.
        /// </summary>
        public static float Lift = 1.0008f;

        public GameObject Go;
        public BBox Box;
        public bool HasContent;

        /// <summary>Co poszlo nie tak (albo dobrze) przy budowie - pokazywane
        /// w oknie. Bez tego jedynym objawem bledu byl brak nakladki.</summary>
        public string Report = "(nie zbudowana)";
        public string ShaderReport = "(shader nieszukany)";
        public int Triangles;

        private RenderTexture rt;
        private Material mat;
        private MapView proj;

        public bool Valid { get { return Go != null; } }

        /// <summary>Rzutowanie uzyte do wypalenia tekstury - siatka musi uzyc
        /// dokladnie tego samego, inaczej obraz sie przesunie wzgledem geometrii.</summary>
        public MapView Projection { get { return proj; } }

        public void Build(CelestialBody body, BBox box, int texSize, int subdiv)
        {
            Destroy();
            Box = box;

            if (body == null || body.scaledBody == null) return;

            proj = new MapView
            {
                BodyRadius = body.Radius,
                CenterLat = (box.South + box.North) * 0.5,
                CenterLon = (box.West + box.East) * 0.5,
                Width = texSize,
                Height = texSize
            };

            // skala tak dobrana, zeby bbox wypelnil teksture w poziomie
            double spanLon = box.East - box.West;
            double metersWide = spanLon * proj.MetersPerDegLon;
            proj.MetersPerPixel = (float)(metersWide / texSize);

            rt = new RenderTexture(texSize, texSize, 0) { filterMode = FilterMode.Bilinear };
            rt.Create();

            Shader sh = FindShader();
            if (sh == null)
            {
                Debug.LogError("[OSMRoads] " + ShaderReport);
                return;      // bez shadera nie ma sensu budowac platu
            }

            mat = new Material(sh) { mainTexture = rt };
            mat.renderQueue = 3000;         // po nieprzezroczystej kuli, przed chmurami

            Go = new GameObject("OSMScaledOverlay");
            Go.layer = LayerScaledSpace;
            Go.AddComponent<MeshFilter>().sharedMesh = BuildPatch(body, box, subdiv);

            var mr = Go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            // Rodzic daje nam pozycje i obrot planety za darmo. Kompensujemy tylko
            // jego skale, bo siatka jest jednostkowa.
            Transform parent = body.scaledBody.transform;
            Go.transform.SetParent(parent, false);

            Vector3 ps = parent.localScale;
            float r = Lift;
            Go.transform.localPosition = Vector3.zero;
            Go.transform.localRotation = Quaternion.identity;
            Go.transform.localScale = new Vector3(
                Mathf.Approximately(ps.x, 0f) ? r : r / ps.x,
                Mathf.Approximately(ps.y, 0f) ? r : r / ps.y,
                Mathf.Approximately(ps.z, 0f) ? r : r / ps.z);

            // Jesli rodzic mial skale 1, promien musi wejsc przez sam mesh -
            // czyli mnozymy jeszcze przez promien w scaled space.
            if (Mathf.Abs(ps.x - 1f) < 0.001f)
            {
                float scaledRadius = (float)(body.Radius / ScaledSpace.ScaleFactor);
                Go.transform.localScale *= scaledRadius;
            }

            Report = string.Format("{0}, {1} trojkatow, lift {2:F5}",
                                   ShaderReport, Triangles, Lift);

            Debug.Log(string.Format(
                "[OSMRoads] nakladka scaled: bbox {0}, skala rodzica {1}, localScale {2}, {3}",
                box, ps, Go.transform.localScale, Report));
        }

        /// <summary>
        /// Shader z przezroczystoscia. Ta sama pulapka co przy Standard: Unity
        /// wycina z builda shadery, ktorych scena nie uzywa, wiec Shader.Find
        /// potrafi zwrocic null i material wychodzi pusty - bez zadnego bledu.
        /// </summary>
        private Shader FindShader()
        {
            string[] candidates =
            {
                "Unlit/Transparent",
                "Sprites/Default",
                "Particles/Alpha Blended",
                "Legacy Shaders/Transparent/Diffuse",
                "KSP/Alpha/Unlit Transparent",
                "KSP/Alpha/Translucent",
                "UI/Default",
                "Unlit/Texture"
            };

            foreach (string name in candidates)
            {
                Shader sh = Shader.Find(name);
                if (sh != null)
                {
                    ShaderReport = "shader: " + sh.name;
                    return sh;
                }
            }

            ShaderReport = "BRAK shadera na nakladke";
            return null;
        }

        /// <summary>Plat sfery jednostkowej pokrywajacy bbox, z UV z tego samego
        /// rzutowania, ktorym wypalamy teksture.</summary>
        private Mesh BuildPatch(CelestialBody body, BBox box, int n)
        {
            var verts = new Vector3[(n + 1) * (n + 1)];
            var uvs = new Vector2[verts.Length];
            var tris = new List<int>(n * n * 6);

            for (int j = 0; j <= n; j++)
            {
                double lat = box.South + (box.North - box.South) * j / n;
                for (int i = 0; i <= n; i++)
                {
                    double lon = box.West + (box.East - box.West) * i / n;
                    int k = j * (n + 1) + i;

                    // kierunek w ukladzie ciala - ten sam, ktorego uzywa PQS,
                    // wiec zgadza sie z orientacja scaledBody
                    Vector3d dir = body.GetRelSurfaceNVector(lat, lon);
                    verts[k] = new Vector3((float)dir.x, (float)dir.y, (float)dir.z);

                    Vector2 px = proj.ToPixel(lat, lon);
                    uvs[k] = new Vector2(px.x / proj.Width, 1f - px.y / proj.Height);
                }
            }

            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int a = j * (n + 1) + i;
                    int b = a + 1;
                    int c = a + (n + 1);
                    int d = c + 1;
                    // Obie skretnosci. Kierunek osi w GetRelSurfaceNVector zalezy
                    // od cialla i planet packa, wiec zamiast zgadywac, ktora strona
                    // jest przednia, rysujemy plat dwustronnie. Przy 4.6 tys.
                    // czworokatow podwojenie trojkatow nic nie kosztuje, a znosi
                    // caly problem: zle zgadniete nawijanie = nakladka niewidoczna.
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    tris.Add(b); tris.Add(d); tris.Add(c);
                }
            }

            var m = new Mesh { name = "OSMScaledPatch" };
            m.vertices = verts;
            m.uv = uvs;
            m.SetTriangles(tris, 0);
            Triangles = tris.Count / 3;
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Wypala dane OSM do tekstury platu. Tlo przezroczyste, zeby
        /// satelita Mirage'a byla widoczna wszedzie tam, gdzie nic nie rysujemy.</summary>
        public void Paint(List<OsmWay> ways)
        {
            if (rt == null || proj == null) return;
            MapRenderer.RenderOverlay(rt, proj, ways);
            HasContent = ways != null && ways.Count > 0;
        }

        public void SetVisible(bool on)
        {
            if (Go != null && Go.activeSelf != on) Go.SetActive(on);
        }

        public void Destroy()
        {
            if (Go != null) { Object.Destroy(Go); Go = null; }
            if (rt != null) { rt.Release(); Object.Destroy(rt); rt = null; }
            if (mat != null) { Object.Destroy(mat); mat = null; }
            HasContent = false;
        }
    }
}
