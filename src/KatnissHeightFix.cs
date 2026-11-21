using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Poprawia wysokosc (RadiusOffset) w plikach KK_GroupCenter paczki KatnisssCapeCanaveral-KSRSS
    /// (Truthfulll's fork, folder Modern + Starship LC-39A). Ten config byl robiony pod Sol Quarter Scale,
    /// a nasz Sol stoi na Real - promien planety wychodzi inny, wiec zaszyta wysokosc jest zla.
    ///
    /// Kazdy STATIC w tych paczkach ma RelativePosition (x,y,z w metrach wzgledem jednego punktu grupy),
    /// NIE wlasna szerokosc/dlugosc geograficzna - wiec wystarczy poprawic RadiusOffset w DWOCH plikach
    /// centrum grupy (RefLatitude/RefLongitude zostaja, bo to katy - te sa niezalezne od skali planety),
    /// a wszystkie budynki w grupie podadza za punktem automatycznie.
    /// </summary>
    internal static class KatnissHeightFix
    {
        // Wzgledem GameData - dokladnie tak, jak README paczki every instruuje jej ulozenie.
        private static readonly string[] GroupCenterFiles =
        {
            "KatnisssCapeCanaveral/Instances/Modern/CapeCanaveral_center.cfg",
            "KatnisssCapeCanaveral/Instances/Starship LC-39A/KK_GroupCenter_Kerbin_Kennedy.cfg",
        };

        private static readonly Regex ReLat = new Regex(@"RefLatitude\s*=\s*([-0-9.eE]+)");
        private static readonly Regex ReLon = new Regex(@"RefLongitude\s*=\s*([-0-9.eE]+)");
        private static readonly Regex ReRadius = new Regex(@"(RadiusOffset\s*=\s*)([-0-9.eE]+)");

        public static string Run(CelestialBody body)
        {
            if (body == null || body.pqsController == null) return "PQS inactive - be near the surface of Earth.";
            var ci = CultureInfo.InvariantCulture;
            int fixedCount = 0, missing = 0;

            foreach (string rel in GroupCenterFiles)
            {
                string path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/" + rel);
                if (!File.Exists(path)) { missing++; continue; }

                string text = File.ReadAllText(path);
                Match mLat = ReLat.Match(text), mLon = ReLon.Match(text), mRad = ReRadius.Match(text);
                if (!mLat.Success || !mLon.Success || !mRad.Success)
                {
                    Debug.LogWarning("[OSMRoads] Katniss height fix: couldn't find RefLatitude/RefLongitude/RadiusOffset in " + path);
                    continue;
                }

                double lat = double.Parse(mLat.Groups[1].Value, ci);
                double lon = double.Parse(mLon.Groups[1].Value, ci);
                double newAlt = TerrainGrid.RawAltitude(body, lat, lon);

                text = ReRadius.Replace(text, "${1}" + newAlt.ToString("F3", ci), 1);
                File.WriteAllText(path, text);
                fixedCount++;
                Debug.Log(string.Format("[OSMRoads] Katniss height fix: {0} -> RadiusOffset {1:F2} (lat {2:F5}, lon {3:F5})",
                                        Path.GetFileName(path), newAlt, lat, lon));
            }

            if (fixedCount == 0 && missing == GroupCenterFiles.Length)
                return "Katniss Cape Canaveral (Modern / Starship LC-39A) not installed under GameData yet.";

            return string.Format("Fixed {0} group center height(s){1}. Restart KSP to see it.",
                                 fixedCount, missing > 0 ? string.Format(" ({0} file(s) not found - install that part first)", missing) : "");
        }
    }
}
