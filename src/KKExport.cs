using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Lotniska w Polsce dla Kerbal Konstructs.
    ///
    /// Lista pasow (PluginData/runways_pl.csv) pochodzi z OSM (aeroway=runway). Wysokosc
    /// terenu znamy dopiero w grze (to teren SOL, nie prawdziwy), wiec config powstaje
    /// tutaj: dla kazdego pasa dwa punkty startowe, 80 m od progu, zwrocone wzdluz pasa.
    /// Model to UniversalSpawnPoint z samego KK (wariant niewidzialny) - pas, drogi
    /// kolowania i plyte rysuje OSMRoads. KK czyta configi przy starcie gry, wiec po
    /// wygenerowaniu trzeba zrestartowac KSP.
    /// </summary>
    internal static class KKExport
    {
        public static string OutPath
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/OSMRoads/KK/PolandAirports.cfg"); }
        }

        public static string Run(CelestialBody body)
        {
            if (body == null || body.pqsController == null) return "PQS inactive - be near the surface of Earth.";
            string csv = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/OSMRoads/PluginData/runways_pl.csv");
            if (!File.Exists(csv)) return "Missing " + csv;

            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("// Wygenerowane przez OSMRoads - lotniska w Polsce z OpenStreetMap (ODbL).");
            sb.AppendLine("// Pasy rysuje OSMRoads; KK daje tylko punkty startowe. Po zmianie: restart KSP.");
            int sites = 0;
            var names = new HashSet<string>();
            foreach (string line in File.ReadAllLines(csv, Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] f = line.Split(';');
                if (f.Length < 6) continue;
                double la1 = double.Parse(f[1], ci), lo1 = double.Parse(f[2], ci), la2 = double.Parse(f[3], ci), lo2 = double.Parse(f[4], ci);
                string baseName = f[0].Trim();
                double lenM = Dist(body.Radius, la1, lo1, la2, lo2);
                if (lenM < 250) continue;
                string[] des = DesignatorsOf(baseName);

                for (int end = 0; end < 2; end++)
                {
                    double aLa = end == 0 ? la1 : la2, aLo = end == 0 ? lo1 : lo2;
                    double bLa = end == 0 ? la2 : la1, bLo = end == 0 ? lo2 : lo1;
                    double t = Math.Min(80.0, lenM * 0.15) / lenM;
                    double sLa = aLa + (bLa - aLa) * t, sLo = aLo + (bLo - aLo) * t;
                    double hdg = Bearing(aLa, aLo, bLa, bLo);
                    double alt = TerrainGrid.RawAltitude(body, sLa, sLo);
                    int num = (int)Math.Round(hdg / 10.0); if (num == 0) num = 36; if (num > 36) num -= 36;
                    string site = Town(baseName) + " RWY " + num.ToString("00");
                    while (!names.Add(site)) site += "'";

                    sb.AppendLine("STATIC");
                    sb.AppendLine("{");
                    sb.AppendLine("\tpointername = UniversalSpawnPoint");
                    sb.AppendLine("\tInstances");
                    sb.AppendLine("\t{");
                    sb.AppendLine("\t\tCelestialBody = " + body.bodyName);
                    sb.AppendLine("\t\tRefLatitude = " + sLa.ToString("R", ci));
                    sb.AppendLine("\t\tRefLongitude = " + sLo.ToString("R", ci));
                    sb.AppendLine("\t\tRadiusOffset = " + (alt + 1.2).ToString("F2", ci));
                    sb.AppendLine("\t\tOrientation = 0,1,0");
                    sb.AppendLine("\t\tRotationAngle = " + hdg.ToString("F2", ci));
                    sb.AppendLine("\t\tVariantName = Invisible");
                    sb.AppendLine("\t\tisScanable = False");
                    sb.AppendLine("\t\tModelScale = 1");
                    sb.AppendLine("\t\tVisibilityRange = 5000");
                    sb.AppendLine("\t\tGroup = OSMRoads_PL_" + Safe(Town(baseName)));
                    sb.AppendLine("\t\tLaunchSite");
                    sb.AppendLine("\t\t{");
                    sb.AppendLine("\t\t\tFacilityType = LaunchSite");
                    sb.AppendLine("\t\t\tLaunchPadTransform = LaunchTransform");
                    sb.AppendLine("\t\t\tLaunchSiteName = " + site);
                    sb.AppendLine("\t\t\tLaunchSiteAuthor = OSMRoads");
                    sb.AppendLine("\t\t\tLaunchSiteType = SPH");
                    sb.AppendLine("\t\t\tLaunchSiteDescription = Runway threshold (OpenStreetMap). Length " +
                                  lenM.ToString("F0", ci) + " m, width " + f[5] + " m" + (f.Length > 6 && f[6].Length > 0 ? ", " + f[6] : "") + ".");
                    sb.AppendLine("\t\t\tCategory = Runway");
                    sb.AppendLine("\t\t\tLaunchSiteLength = " + lenM.ToString("F0", ci));
                    sb.AppendLine("\t\t\tLaunchSiteWidth = " + f[5]);
                    sb.AppendLine("\t\t\tRecoveryFactor = 100");
                    sb.AppendLine("\t\t\tRecoveryRange = 20000");
                    sb.AppendLine("\t\t\tOpenCloseState = Open");
                    sb.AppendLine("\t\t\tOpenCost = 0");
                    sb.AppendLine("\t\t}");
                    sb.AppendLine("\t}");
                    sb.AppendLine("}");
                    sites++;
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(OutPath));
            File.WriteAllText(OutPath, sb.ToString(), new UTF8Encoding(false));
            return string.Format("Wrote {0} launch sites to GameData/OSMRoads/KK/PolandAirports.cfg - restart KSP (needs Kerbal Konstructs).", sites);
        }

        private static string Town(string name)
        {
            int i = name.IndexOf(" RWY ", StringComparison.Ordinal);
            return i > 0 ? name.Substring(0, i) : name;
        }

        private static string[] DesignatorsOf(string name)
        {
            int i = name.IndexOf(" RWY ", StringComparison.Ordinal);
            return i > 0 ? name.Substring(i + 5).Split('/') : new string[0];
        }

        private static string Safe(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) && c < 128 ? c : '_');
            return sb.ToString();
        }

        private static double Dist(double r, double la1, double lo1, double la2, double lo2)
        {
            double k = Math.PI / 180, x = (lo2 - lo1) * k * Math.Cos((la1 + la2) * 0.5 * k), y = (la2 - la1) * k;
            return Math.Sqrt(x * x + y * y) * r;
        }

        private static double Bearing(double la1, double lo1, double la2, double lo2)
        {
            double k = Math.PI / 180, x = (lo2 - lo1) * Math.Cos((la1 + la2) * 0.5 * k), y = la2 - la1;
            double h = Math.Atan2(x, y) / k;
            return h < 0 ? h + 360 : h;
        }
    }
}
