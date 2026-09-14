using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Kolory klas pokrycia terenu. Osobno od AreaKindShared, bo tamten plik
    /// jest wspoldzielony z narzedziem osmpack, ktore nie ma Unity.
    ///
    /// Wartosci sa celowo ciemniejsze i mniej nasycone niz "naturalne" barwy:
    /// poligony leza plasko, bez cieni i mikroreliefu, wiec przy pelnym nasyceniu
    /// swiecilyby mocniej niz teren PQS dookola i odcinaly sie jak naklejki.
    /// </summary>
    public static class AreaKindColors
    {
        public static Color Tint(AreaKind k)
        {
            switch (k)
            {
                case AreaKind.Water: return new Color(0.14f, 0.25f, 0.35f);
                case AreaKind.Forest: return new Color(0.13f, 0.22f, 0.11f);
                case AreaKind.Grass: return new Color(0.29f, 0.41f, 0.18f);
                case AreaKind.Farmland: return new Color(0.47f, 0.43f, 0.26f);
                case AreaKind.Sand: return new Color(0.68f, 0.63f, 0.49f);
                case AreaKind.Residential: return new Color(0.34f, 0.33f, 0.30f);
                case AreaKind.Industrial: return new Color(0.31f, 0.31f, 0.33f);
                case AreaKind.Pavement: return new Color(0.27f, 0.27f, 0.29f);
                default: return Color.gray;
            }
        }
    }
}
