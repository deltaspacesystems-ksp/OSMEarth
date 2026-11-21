using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Punkty startowe dla Kerbal Konstructs z realnych kompleksow startowych, z OpenStreetMap
    /// (tag man_made=launch_pad, punkty i obszary) plus pasy startowe (aeroway=runway) w okolicy -
    /// np. Shuttle Landing Facility kolo Cape Canaveral. Sciaganie danych robi OsmRoadsAddon
    /// (OverpassClient.Fetch, live - to nie jest offline generator jak Polish airports), ten plik
    /// tylko sklada gotowy tekst configu z juz sparsowanych danych.
    ///
    /// Modele: tak jak w KKExport (Poland) - UniversalSpawnPoint, wariant Invisible. OSMRoads i tak
    /// rysuje prawdziwe budynki/drogi z OSM w tym miejscu (to caly swiat, nie tylko Polska), wiec teren
    /// dookola punktu startu nie jest pusty - ale to nie sa odrebne modele wiezy startowej, VAB itd.
    /// jak w recznie robionych paczkach (np. KatnissCapeCanaveral) - takich modeli tu nie mamy.
    /// </summary>
    internal static class KKExportSpaceports
    {
        public sealed class Pad
        {
            public string Name;
            public double Lat, Lon;
            public double HeadingDeg;
            public string Kind;     // "launch_pad" albo "runway"
            public double LengthM;  // tylko runway - do opisu
        }

        /// <summary>Ways z ManMade=="launch_pad" (obszar) albo Aeroway=="runway" -> Pad (srodek + orientacja
        /// z najdluzszej krawedzi obrysu / z osi pasa).</summary>
        public static List<Pad> FromWays(List<OsmWay> ways, double bodyRadius)
        {
            var pads = new List<Pad>();
            foreach (OsmWay w in ways)
            {
                bool isPad = w.ManMade == "launch_pad";
                bool isRunway = w.Aeroway == "runway";
                if (!isPad && !isRunway) continue;
                if (w.Points.Count < 2) continue;

                double cLa = 0, cLo = 0;
                for (int i = 0; i < w.Points.Count; i++) { cLa += w.Points[i].Lat; cLo += w.Points[i].Lon; }
                cLa /= w.Points.Count; cLo /= w.Points.Count;

                // Orientacja: krawedz obrysu o najwiekszej dlugosci (dziala i dla waskich prostokatnych
                // padow, i dla pasow zapisanych jako dwupunktowa linia).
                double bestLen = -1, hdg = 0;
                for (int i = 0; i + 1 < w.Points.Count; i++)
                {
                    GeoPoint a = w.Points[i], b = w.Points[i + 1];
                    double len = Dist(bodyRadius, a.Lat, a.Lon, b.Lat, b.Lon);
                    if (len > bestLen) { bestLen = len; hdg = Bearing(a.Lat, a.Lon, b.Lat, b.Lon); }
                }

                pads.Add(new Pad
                {
                    Name = !string.IsNullOrEmpty(w.Name) ? w.Name : (isPad ? "Launch Pad" : "Runway"),
                    Lat = cLa, Lon = cLo, HeadingDeg = hdg,
                    Kind = isPad ? "launch_pad" : "runway",
                    LengthM = isRunway ? PerimeterLongest(w, bodyRadius) : 0
                });
            }
            return pads;
        }

        /// <summary>Punktowe man_made=launch_pad (mniejsze/nieaktywne kompleksy czesto tak sa mapowane) -
        /// bez obrysu, wiec bez orientacji (0 = polnoc).</summary>
        public static List<Pad> FromNodes(List<GeoTag> nodes)
        {
            var pads = new List<Pad>();
            foreach (GeoTag n in nodes)
                pads.Add(new Pad { Name = !string.IsNullOrEmpty(n.Name) ? n.Name : "Launch Pad", Lat = n.Lat, Lon = n.Lon, HeadingDeg = 0, Kind = "launch_pad" });
            return pads;
        }

        /// <summary>Sklada tekst configu KK. altitudeOf: RawAltitude(lat,lon) do wysokosci punktu.</summary>
        public static string BuildConfig(List<Pad> pads, CelestialBody body, string groupName, Func<double, double, double> altitudeOf)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("// Wygenerowane przez OSMRoads - kompleksy startowe z OpenStreetMap (ODbL).");
            sb.AppendLine("// Tylko punkty startu (UniversalSpawnPoint, niewidzialny) - budynki i drogi w tym miejscu");
            sb.AppendLine("// rysuje sam OSMRoads na biezaco, to nie sa recznie modelowane wieze/VAB. Po zmianie: restart KSP.");

            var names = new HashSet<string>();
            foreach (Pad p in pads)
            {
                string site = p.Name;
                while (!names.Add(site)) site += "'";
                double alt = altitudeOf(p.Lat, p.Lon);

                sb.AppendLine("STATIC");
                sb.AppendLine("{");
                sb.AppendLine("\tpointername = UniversalSpawnPoint");
                sb.AppendLine("\tInstances");
                sb.AppendLine("\t{");
                sb.AppendLine("\t\tCelestialBody = " + body.bodyName);
                sb.AppendLine("\t\tRefLatitude = " + p.Lat.ToString("R", ci));
                sb.AppendLine("\t\tRefLongitude = " + p.Lon.ToString("R", ci));
                sb.AppendLine("\t\tRadiusOffset = " + (alt + 1.2).ToString("F2", ci));
                sb.AppendLine("\t\tOrientation = 0,1,0");
                sb.AppendLine("\t\tRotationAngle = " + p.HeadingDeg.ToString("F2", ci));
                sb.AppendLine("\t\tVariantName = Invisible");
                sb.AppendLine("\t\tisScanable = False");
                sb.AppendLine("\t\tModelScale = 1");
                sb.AppendLine("\t\tVisibilityRange = 5000");
                sb.AppendLine("\t\tGroup = " + Safe(groupName));
                sb.AppendLine("\t\tLaunchSite");
                sb.AppendLine("\t\t{");
                sb.AppendLine("\t\t\tFacilityType = LaunchSite");
                sb.AppendLine("\t\t\tLaunchPadTransform = LaunchTransform");
                sb.AppendLine("\t\t\tLaunchSiteName = " + site);
                sb.AppendLine("\t\t\tLaunchSiteAuthor = OSMRoads");
                // Pad = pionowy start (rakieta) jak stockowy VAB; pas = poziomy jak SPH.
                sb.AppendLine("\t\t\tLaunchSiteType = " + (p.Kind == "runway" ? "SPH" : "VAB"));
                sb.AppendLine("\t\t\tLaunchSiteDescription = " + (p.Kind == "runway"
                    ? "Runway threshold (OpenStreetMap). Length " + p.LengthM.ToString("F0", ci) + " m."
                    : "Real launch complex location (OpenStreetMap man_made=launch_pad)."));
                sb.AppendLine("\t\t\tCategory = " + (p.Kind == "runway" ? "Runway" : "Launch Pad"));
                if (p.Kind == "runway") sb.AppendLine("\t\t\tLaunchSiteLength = " + p.LengthM.ToString("F0", ci));
                sb.AppendLine("\t\t\tRecoveryFactor = 100");
                sb.AppendLine("\t\t\tRecoveryRange = 20000");
                sb.AppendLine("\t\t\tOpenCloseState = Open");
                sb.AppendLine("\t\t\tOpenCost = 0");
                sb.AppendLine("\t\t}");
                sb.AppendLine("\t}");
                sb.AppendLine("}");
            }
            return sb.ToString();
        }

        private static double PerimeterLongest(OsmWay w, double r)
        {
            double best = 0;
            for (int i = 0; i + 1 < w.Points.Count; i++)
            {
                GeoPoint a = w.Points[i], b = w.Points[i + 1];
                double len = Dist(r, a.Lat, a.Lon, b.Lat, b.Lon);
                if (len > best) best = len;
            }
            return best;
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
