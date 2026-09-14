using System;
using System.IO;
using UnityEngine;

namespace OSMRoads
{
    public enum TrackMode { None = 0, Orbit = 1, Past = 2, Both = 3 }

    /// <summary>
    /// Ustawienia zapamietywane miedzy sesjami: skroty, wyglad, zachowanie planera.
    ///
    /// Plik GameData/OSMRoads/PluginData/settings.cfg w formacie ConfigNode, bo tak
    /// trzymaja ustawienia wszystkie mody KSP i da sie go poprawic recznie.
    /// Zapis tylko przy zmianie, nie co klatke.
    /// </summary>
    public static partial class OsmSettings
    {
        public static KeyCode PlannerKey = KeyCode.P;
        public static bool PlannerModifier = true;
        public static KeyCode RoadsKey = KeyCode.O;
        public static bool RoadsModifier = true;

        public static bool ModernUi = false;
        public static bool FollowVessel = false;
        public static TrackMode Track = TrackMode.Both;
        public static float RefreshSec = 0.5f;

        /// <summary>O ile stopni przesunieta jest tekstura planety w poziomie.
        /// Konwencja zalezy od planet packa - sprawdza sie ja po znacznikach kosmodromow.</summary>
        public static float WorldTexLonOffset = 180f;

        /// <summary>Trwa przechwytywanie klawisza - skroty nie moga wtedy dzialac,
        /// inaczej wcisniecie nowego klawisza od razu zamykaloby okno.</summary>
        public static bool Capturing;
        private static float captureEnded = -10f;

        private static bool loaded;

        private static string FilePath
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/OSMRoads/PluginData/settings.cfg"); }
        }

        public static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                ConfigNode root = ConfigNode.Load(FilePath);
                ConfigNode n = root != null ? (root.GetNode("OSMROADS") ?? root) : null;
                if (n == null) return;

                PlannerKey = Key(n, "plannerKey", PlannerKey);
                PlannerModifier = Bool(n, "plannerModifier", PlannerModifier);
                RoadsKey = Key(n, "roadsKey", RoadsKey);
                RoadsModifier = Bool(n, "roadsModifier", RoadsModifier);
                ModernUi = Bool(n, "modernUi", ModernUi);
                // stary przelacznik ciemnego tla -> styl
                if (n.HasValue("darkMapBackground") && !n.HasValue("mapStyle"))
                    Style = Bool(n, "darkMapBackground", true) ? MapStyle.Dark : MapStyle.Terrain;
                string st = n.GetValue("mapStyle");
                if (st != null) { try { Style = (MapStyle)Enum.Parse(typeof(MapStyle), st); } catch { } }
                Satellite = Bool(n, "satellite", Satellite);
                SatBias = Mathf.Clamp((int)Float(n, "satBias", SatBias), -2, 2);
                SatMaxLevel = Mathf.Clamp((int)Float(n, "satMaxLevel", SatMaxLevel), 0, 14);
                SatAreas = Bool(n, "satAreas", SatAreas);
                Labels = Bool(n, "labels", Labels);
                LabelCityMpp = Float(n, "labelCityMpp", LabelCityMpp);
                LabelTownMpp = Float(n, "labelTownMpp", LabelTownMpp);
                LabelVillageMpp = Float(n, "labelVillageMpp", LabelVillageMpp);
                LabelHamletMpp = Float(n, "labelHamletMpp", LabelHamletMpp);
                LabelRoadMpp = Float(n, "labelRoadMpp", LabelRoadMpp);
                LabelScale = Mathf.Clamp(Float(n, "labelScale", LabelScale), 0.6f, 2f);
                GlobeLabels = Bool(n, "globeLabels", GlobeLabels);
                CountryBorders = Bool(n, "countryBorders", CountryBorders);
                FollowVessel = Bool(n, "followVessel", FollowVessel);
                RefreshSec = Mathf.Clamp(Float(n, "refreshSec", RefreshSec), 0.1f, 5f);
                WorldTexLonOffset = Float(n, "worldTexLonOffset", WorldTexLonOffset);

                string t = n.GetValue("track");
                if (t != null) { try { Track = (TrackMode)Enum.Parse(typeof(TrackMode), t); } catch { } }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[OSMRoads] ustawienia: " + e.Message);
            }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var root = new ConfigNode();
                ConfigNode n = root.AddNode("OSMROADS");
                n.AddValue("plannerKey", PlannerKey.ToString());
                n.AddValue("plannerModifier", PlannerModifier);
                n.AddValue("roadsKey", RoadsKey.ToString());
                n.AddValue("roadsModifier", RoadsModifier);
                n.AddValue("modernUi", ModernUi);
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                n.AddValue("mapStyle", Style.ToString());
                n.AddValue("satellite", Satellite);
                n.AddValue("satBias", SatBias);
                n.AddValue("satMaxLevel", SatMaxLevel);
                n.AddValue("satAreas", SatAreas);
                n.AddValue("labels", Labels);
                n.AddValue("labelCityMpp", LabelCityMpp.ToString(inv));
                n.AddValue("labelTownMpp", LabelTownMpp.ToString(inv));
                n.AddValue("labelVillageMpp", LabelVillageMpp.ToString(inv));
                n.AddValue("labelHamletMpp", LabelHamletMpp.ToString(inv));
                n.AddValue("labelRoadMpp", LabelRoadMpp.ToString(inv));
                n.AddValue("labelScale", LabelScale.ToString(inv));
                n.AddValue("globeLabels", GlobeLabels);
                n.AddValue("countryBorders", CountryBorders);
                n.AddValue("followVessel", FollowVessel);
                n.AddValue("track", Track.ToString());
                n.AddValue("refreshSec", RefreshSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
                n.AddValue("worldTexLonOffset", WorldTexLonOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
                root.Save(FilePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[OSMRoads] zapis ustawien: " + e.Message);
            }
        }

        /// <summary>Czy skrot zostal wlasnie wcisniety. Pol sekundy po przechwyceniu
        /// klawisza skroty milcza - ten sam wcisniety klawisz nie przelaczy okna.</summary>
        public static bool Pressed(KeyCode key, bool needModifier)
        {
            EnsureLoaded();
            if (Capturing || Time.unscaledTime - captureEnded < 0.5f) return false;
            if (key == KeyCode.None || !Input.GetKeyDown(key)) return false;
            return !needModifier || GameSettings.MODIFIER_KEY.GetKey();
        }

        public static void EndCapture()
        {
            Capturing = false;
            captureEnded = Time.unscaledTime;
        }

        public static string Describe(KeyCode key, bool modifier)
        {
            return (modifier ? "Mod+" : "") + key;
        }

        private static KeyCode Key(ConfigNode n, string name, KeyCode def)
        {
            string v = n.GetValue(name);
            if (v == null) return def;
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), v); } catch { return def; }
        }

        private static bool Bool(ConfigNode n, string name, bool def)
        {
            bool b;
            string v = n.GetValue(name);
            return v != null && bool.TryParse(v, out b) ? b : def;
        }

        private static float Float(ConfigNode n, string name, float def)
        {
            float f;
            string v = n.GetValue(name);
            return v != null && float.TryParse(v, System.Globalization.NumberStyles.Float,
                                               System.Globalization.CultureInfo.InvariantCulture, out f) ? f : def;
        }
    }
}
