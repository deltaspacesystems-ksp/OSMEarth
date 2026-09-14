using UnityEngine;

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
            return OsmSettings.ModernUi ? null : HighLogic.Skin;
        }
    }
}
