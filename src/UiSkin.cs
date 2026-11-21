using UnityEngine;

// DeltaUI (okna z tabami) musi byc wczytany przed OSMRoads.
[assembly: KSPAssemblyDependency("DeltaUI", 1, 0)]

namespace OSMRoads
{
    /// <summary>
    /// Dwa wyglady okien, jak w innych modach KSP:
    ///   - styl KSP: HighLogic.Skin (jasne ramki, czcionka KSP),
    ///   - styl Unity: wbudowany skin Unity - ciemny, z zaokrzonymi rogami,
    ///     taki sam jak w oknach SmokeScreen czy wielu narzedzi deweloperskich.
    ///
    /// Styl Unity dostaje sie przez GUI.skin = null - Unity wraca wtedy do swojego
    /// domyslnego skina. Wczesniejsza wersja budowala wlasny skin z tekstur,
    /// co w grze wygladalo gorzej niz gotowy wbudowany.
    /// </summary>
    public static class UiSkin
    {
        public static GUISkin Get()
        {
            OsmSettings.EnsureLoaded();
            return OsmSettings.ModernUi ? DeltaUI.Theme.Skin : HighLogic.Skin;
        }
    }
}
