

namespace OSMRoads
{
    /// <summary>Klasy pokrycia terenu. Kolejnosc = indeks submesha w LanduseMeshBuilder.</summary>
    public enum AreaKind
    {
        None = -1,
        Water = 0,
        Forest = 1,
        Grass = 2,
        Farmland = 3,
        Sand = 4,
        Residential = 5,
        Industrial = 6,
        Pavement = 7
    }

    public static class AreaKinds
    {
        public const int Count = 8;

        public static AreaKind From(string key, string v)
        {
            if (v == null) return AreaKind.None;

            if (key == "landuse")
            {
                switch (v)
                {
                    case "forest": return AreaKind.Forest;
                    case "grass": case "meadow": case "village_green":
                    case "recreation_ground": return AreaKind.Grass;
                    case "farmland": case "farmyard": case "orchard":
                    case "vineyard": case "allotments": return AreaKind.Farmland;
                    case "residential": return AreaKind.Residential;
                    case "industrial": case "railway": case "quarry": return AreaKind.Industrial;
                    case "commercial": case "retail": return AreaKind.Pavement;
                    // Wody NIE generujemy w ogole. Scatterer i Mirage renderuja ja
                    // poprawnie, a plaski poligon OSM lezy dokladnie na poziomie morza
                    // i migotal z z-fightingu (wielki lsniacy placek nad laguna KSC).
                    case "reservoir": case "basin": return AreaKind.None;
                    case "cemetery": return AreaKind.Grass;
                }
            }
            else if (key == "natural")
            {
                switch (v)
                {
                    case "wood": case "scrub": return AreaKind.Forest;
                    case "grassland": case "heath": return AreaKind.Grass;
                    case "water": case "wetland": return AreaKind.None;
                    case "sand": case "beach": case "bare_rock": return AreaKind.Sand;
                }
            }
            else if (key == "leisure")
            {
                switch (v)
                {
                    case "park": case "garden": case "golf_course":
                    case "pitch": case "playground": return AreaKind.Grass;
                }
            }

            return AreaKind.None;
        }

        /// <summary>
        /// Male przesuniecie w gore, rozne dla kazdej klasy. Poligony OSM naklada sie
        /// na siebie (park wewnatrz dzielnicy mieszkaniowej), a bez tego walczylyby
        /// o piksele. Kolejnosc = co ma byc na wierzchu.
        /// </summary>
        public static float HeightOffset(AreaKind k)
        {
            switch (k)
            {
                case AreaKind.Residential: return 0.10f;
                case AreaKind.Industrial: return 0.12f;
                case AreaKind.Farmland: return 0.14f;
                case AreaKind.Sand: return 0.16f;
                case AreaKind.Grass: return 0.18f;
                case AreaKind.Forest: return 0.20f;
                case AreaKind.Pavement: return 0.22f;
                case AreaKind.Water: return 0.24f;
                default: return 0.10f;
            }
        }


        /// <summary>Srednia odleglosc miedzy drzewami w metrach. 0 = bez drzew.</summary>
        public static float TreeSpacing(AreaKind k)
        {
            switch (k)
            {
                case AreaKind.Forest: return 9f;
                case AreaKind.Grass: return 26f;     // parki - rzadko
                case AreaKind.Residential: return 38f;
                default: return 0f;
            }
        }
    }
}
