namespace OSMRoads
{
    /// <summary>
    /// Klasa drogi. Kolejnosc = indeks submesha w RoadMeshBuilder i indeks
    /// materialu w MaterialLib.RoadMaterials().
    ///
    /// Piec klas zamiast jednej, bo caly wizualny porzadek miasta bierze sie
    /// z hierarchii dróg: autostrada ma inny asfalt, inne oznakowanie i inne
    /// latarnie niz droga dojazdowa. Przy jednym materiale wszystko czytalo sie
    /// jak jedna szara plansza.
    /// </summary>
    public enum RoadClass
    {
        Motorway = 0,   // autostrada, ekspresowa
        Major = 1,      // krajowa, wojewodzka, powiatowa
        Minor = 2,      // miejska, osiedlowa
        Service = 3,    // dojazdowa, parking
        Path = 4        // piesza, rowerowa, grunt
    }

    public static class RoadClasses
    {
        public const int Count = 5;

        public static RoadClass From(string highway)
        {
            switch (highway)
            {
                case "motorway": case "motorway_link":
                case "trunk": case "trunk_link":
                    return RoadClass.Motorway;

                case "primary": case "primary_link":
                case "secondary": case "secondary_link":
                case "tertiary": case "tertiary_link":
                    return RoadClass.Major;

                case "residential": case "unclassified":
                case "living_street": case "road":
                    return RoadClass.Minor;

                case "service": case "track": case "busway":
                    return RoadClass.Service;

                default:
                    return RoadClass.Path;
            }
        }

        /// <summary>Odstep miedzy latarniami w metrach. 0 = klasa bez oswietlenia.
        /// Sciezki i drogi dojazdowe zostaja ciemne - to one daja kontrast,
        /// dzieki ktoremu oswietlone arterie w ogole widac.</summary>
        public static float LampSpacing(RoadClass c)
        {
            switch (c)
            {
                case RoadClass.Motorway: return 42f;
                case RoadClass.Major: return 34f;
                case RoadClass.Minor: return 52f;
                default: return 0f;
            }
        }

        /// <summary>Wysokosc slupa w metrach.</summary>
        public static float LampHeight(RoadClass c)
        {
            switch (c)
            {
                case RoadClass.Motorway: return 11f;
                case RoadClass.Major: return 9f;
                default: return 7f;
            }
        }

        /// <summary>Ile metrow drogi przypada na jedno powtorzenie tekstury wzdluz.
        /// Osiem metrow to jeden cykl kreski przerywanej (3 m kreska, 5 m przerwa),
        /// wiec podzialka zgadza sie z rzeczywistoscia niezaleznie od szerokosci.</summary>
        public const float TileLengthM = 8f;
    }
}
