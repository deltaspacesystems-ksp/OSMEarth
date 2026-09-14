using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Kolory i polysk materialow - bez shaderow i bez Material.
    ///
    /// Osobno od MaterialLib, bo z tych samych liczb korzysta tester offline
    /// (OSMView), ktory nie ma Unity. Jedno zrodlo prawdy: zmiana odcienia tutaj
    /// jest widoczna i w grze, i w podgladzie, bez przepisywania wartosci.
    /// </summary>
    public static class MaterialPalette
    {
        public const int FacadeBuckets = 8;

        /// <summary>Osiem odcieni i szerszy rozrzut TEMPERATURY, nie tylko jasnosci:
        /// przy samych szarosciach kwartal czytal sie jednolicie niezaleznie od tego,
        /// ile tonow bylo w palecie.</summary>
        public static readonly Color[] FacadeTints =
        {
            new Color(0.86f, 0.84f, 0.79f),   // jasny tynk
            new Color(0.63f, 0.63f, 0.62f),   // szary beton
            new Color(0.60f, 0.41f, 0.34f),   // cegla czerwona
            new Color(0.72f, 0.74f, 0.78f),   // chlodny beton
            new Color(0.78f, 0.71f, 0.54f),   // piaskowy tynk
            new Color(0.50f, 0.53f, 0.53f),   // ciemny szary
            new Color(0.70f, 0.74f, 0.69f),   // bladozielony tynk
            new Color(0.79f, 0.66f, 0.58f)    // lososiowy tynk
        };

        public const float FacadeGloss = 0.18f;

        public static readonly Color Plinth = new Color(0.80f, 0.79f, 0.77f);
        public const float PlinthGloss = 0.24f;

        public static readonly Color RoofEdge = new Color(0.66f, 0.65f, 0.63f);
        public const float RoofEdgeGloss = 0.14f;

        public static readonly Color RoofFlat = new Color(0.84f, 0.84f, 0.86f);
        public const float RoofFlatGloss = 0.08f;

        public static readonly Color RoofTiles = new Color(0.92f, 0.90f, 0.88f);
        public const float RoofTilesGloss = 0.16f;

        public static float RoadGloss(RoadClass c) { return c == RoadClass.Path ? 0.05f : 0.34f; }

        public static readonly Color LampPole = new Color(0.30f, 0.31f, 0.33f);
        public const float LampPoleGloss = 0.42f;
        public const float LampPoleMetallic = 0.55f;

        public static readonly Color LampHead = new Color(0.62f, 0.62f, 0.60f);
        public const float LampHeadGloss = 0.55f;
        public const float LampHeadMetallic = 0.3f;
    }
}
