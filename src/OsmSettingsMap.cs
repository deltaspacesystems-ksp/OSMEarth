namespace OSMRoads
{
    public enum MapStyle { Dark = 0, Terrain = 1, Light = 2 }

    /// <summary>
    /// Ustawienia wygladu mapy planera - osobny plik, bo korzysta z nich takze
    /// tester (bez KSP). Wczytywanie i zapis siedza w OsmSettings.cs.
    /// </summary>
    public static partial class OsmSettings
    {
        /// <summary>Tlo i kolory mapy: ciemne (widac braki danych), teren, jasne jak na openstreetmap.org.</summary>
        public static MapStyle Style = MapStyle.Dark;

        /// <summary>Zdjecia satelitarne pod drogami zamiast wypelnien OSM.</summary>
        public static bool Satellite = false;

        /// <summary>Ostrosc satelity: poziom szescianu wzgledem skali mapy. +1 = ostrzej
        /// (wiecej danych na kafel), -1 = bardziej rozmyte, ale szybciej.</summary>
        public static int SatBias = 0;

        /// <summary>Najostrzejszy poziom zdjec: 7 = ok. 300 m/px (Sol, cala Ziemia),
        /// 12 = ok. 10 m/px (Mirage, tam gdzie sie latalo).</summary>
        public static int SatMaxLevel = 12;

        /// <summary>Polprzezroczyste obszary OSM (lasy, pola) na zdjeciu.</summary>
        public static bool SatAreas = false;

        /// <summary>Nazwy miejsc i ulic na mapie.</summary>
        public static bool Labels = true;

        /// <summary>Kontury i nazwy panstw.</summary>
        public static bool CountryBorders = true;

        // Do jakiej skali (m/px) pokazywac dany rodzaj nazwy. Wieksza liczba = widac z dalej.
        public static float LabelCityMpp = 40000f;   // wystarcza na caly widok swiata (max 60000 m/px)
        public static float LabelTownMpp = 500f;
        public static float LabelVillageMpp = 60f;
        public static float LabelHamletMpp = 15f;
        public static float LabelRoadMpp = 6f;

        /// <summary>Wielkosc czcionki etykiet.</summary>
        public static float LabelScale = 1f;

        /// <summary>Nazwy miast na kuli w widoku mapy.</summary>
        public static bool GlobeLabels = true;

        public static bool DarkMapBackground { get { return Style == MapStyle.Dark; } }

        /// <summary>Do jakiej skali pokazywac miejsce danego rodzaju.</summary>
        public static float PlaceMaxMpp(int rank)
        {
            switch (rank)
            {
                case 0: return LabelCityMpp;
                case 1: return LabelTownMpp;
                case 2: case 3: return LabelVillageMpp;
                default: return LabelHamletMpp;
            }
        }
    }
}
