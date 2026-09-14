using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace OSMRoads
{
    public class OsmWay
    {
        public string Highway;          // null jesli to nie droga
        public string Building;         // null jesli to nie budynek
        public float HeightM = float.NaN;   // tag 'height'
        public int Levels = -1;             // tag 'building:levels'
        public string RoofShape;            // tag 'roof:shape'
        public AreaKind Area = AreaKind.None;   // landuse / natural / leisure
        public string Name;                     // tag 'name' - nazwa ulicy lub obiektu

        public readonly List<GeoPoint> Points = new List<GeoPoint>();

        // --- zasieg obrysu, liczony raz i pamietany ---
        // Mapa przerysowuje ten sam zestaw obiektow przy kazdym przewinieciu,
        // wiec bez tego kazde przerysowanie przechodzilo po wszystkich punktach
        // wszystkich obiektow, takze tych daleko poza kadrem.
        private bool boundsOk;
        private double bS, bN, bW, bE;

        public void EnsureBounds()
        {
            if (boundsOk) return;
            boundsOk = true;

            bS = double.MaxValue; bN = double.MinValue;
            bW = double.MaxValue; bE = double.MinValue;

            for (int i = 0; i < Points.Count; i++)
            {
                GeoPoint g = Points[i];
                if (g.Lat < bS) bS = g.Lat;
                if (g.Lat > bN) bN = g.Lat;
                if (g.Lon < bW) bW = g.Lon;
                if (g.Lon > bE) bE = g.Lon;
            }
        }

        /// <summary>Czy obrys ma przynajmniej tyle stopni w ktoras strone. Wolac po EnsureBounds.</summary>
        public bool SpansAtLeast(double deg)
        {
            return (bN - bS) >= deg || (bE - bW) >= deg;
        }

        /// <summary>Czy obrys w ogole dotyka prostokata. Wolac po EnsureBounds.</summary>
        public bool Overlaps(double south, double north, double west, double east)
        {
            return !(bN < south || bS > north || bE < west || bW > east);
        }

        public bool IsRoad { get { return Highway != null; } }
        public bool IsBuilding { get { return Building != null && Building != "no"; } }
        public bool IsArea { get { return Area != AreaKind.None; } }

        /// <summary>Wysokosc bryly w metrach: 'height' -> 'building:levels' x 3 -> domyslne 8.</summary>
        public float EffectiveHeight()
        {
            if (!float.IsNaN(HeightM) && HeightM > 0.5f) return HeightM;
            if (Levels > 0) return Levels * 3.0f;
            return 8.0f;
        }
    }

    /// <summary>
    /// Strumieniowy parser OSM XML (odpowiedz Overpass z "out geom;").
    /// Swiadomie XmlReader, a nie XDocument - System.Xml.Linq.dll NIE jest
    /// w KSP_x64_Data/Managed, wiec LINQ to XML wywalilby sie w runtime.
    /// </summary>
    public static class OsmXml
    {
        public static List<OsmWay> ParseWays(string xml)
        {
            var result = new List<OsmWay>();
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreWhitespace = true,
                IgnoreComments = true,
                CheckCharacters = false
            };

            using (var sr = new StringReader(xml))
            using (var r = XmlReader.Create(sr, settings))
            {
                OsmWay cur = null;
                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element)
                    {
                        if (r.Name == "way")
                        {
                            cur = r.IsEmptyElement ? null : new OsmWay();
                        }
                        else if (cur != null && r.Name == "nd")
                        {
                            string la = r.GetAttribute("lat");
                            string lo = r.GetAttribute("lon");
                            double dla, dlo;
                            if (la != null && lo != null &&
                                double.TryParse(la, NumberStyles.Float, CultureInfo.InvariantCulture, out dla) &&
                                double.TryParse(lo, NumberStyles.Float, CultureInfo.InvariantCulture, out dlo))
                            {
                                cur.Points.Add(new GeoPoint(dla, dlo));
                            }
                        }
                        else if (cur != null && r.Name == "tag")
                        {
                            string k = r.GetAttribute("k");
                            if (k == null) continue;
                            string v = r.GetAttribute("v");

                            switch (k)
                            {
                                case "highway": cur.Highway = v; break;
                                case "building": cur.Building = v; break;
                                case "roof:shape": cur.RoofShape = v; break;
                                case "name": cur.Name = v; break;
                                case "landuse": case "natural": case "leisure":
                                    if (cur.Area == AreaKind.None) cur.Area = AreaKinds.From(k, v);
                                    break;
                                case "building:part":
                                    if (cur.Building == null) cur.Building = v;
                                    break;
                                case "height": cur.HeightM = ParseMeters(v); break;
                                case "building:levels":
                                    int lv;
                                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out lv))
                                        cur.Levels = lv;
                                    break;
                            }
                        }
                    }
                    else if (r.NodeType == XmlNodeType.EndElement && r.Name == "way")
                    {
                        if (cur != null && cur.Points.Count >= 2)
                            result.Add(cur);
                        cur = null;
                    }
                }
            }
            return result;
        }

        /// <summary>'12', '12.5', '12 m' -> metry. Zwraca NaN, jesli sie nie da.</summary>
        public static float ParseMeters(string v)
        {
            if (string.IsNullOrEmpty(v)) return float.NaN;

            int end = 0;
            while (end < v.Length &&
                   (char.IsDigit(v[end]) || v[end] == '.' || v[end] == '-' || v[end] == '+'))
                end++;
            if (end == 0) return float.NaN;

            float f;
            if (float.TryParse(v.Substring(0, end), NumberStyles.Float,
                               CultureInfo.InvariantCulture, out f))
                return f;
            return float.NaN;
        }

        /// <summary>Szerokosc jezdni w metrach wg tagu highway.</summary>
        public static float WidthFor(string highway)
        {
            switch (highway)
            {
                case "motorway": case "motorway_link": return 22f;
                case "trunk": case "trunk_link": return 16f;
                case "primary": case "primary_link": return 13f;
                case "secondary": return 11f;
                case "tertiary": return 9f;
                case "residential": case "unclassified": return 7f;
                default: return 6f;
            }
        }
    }
}

namespace OSMRoads
{
    /// <summary>Nazwany punkt z OSM: miasto, wies, dzielnica. Sluzy wylacznie
    /// do etykiet - dlatego trzyma tylko to, co potrzebne do ich narysowania
    /// i do decyzji, przy jakim zoomie je pokazac.</summary>
    public class OsmPlace
    {
        public double Lat, Lon;
        public string Name;
        public string Kind;        // city / town / village / hamlet / suburb

        /// <summary>Im mniejsza liczba, tym wazniejsze miejsce - uzywane do
        /// filtrowania etykiet przy oddaleniu.</summary>
        public int Rank
        {
            get
            {
                switch (Kind)
                {
                    case "city": return 0;
                    case "town": return 1;
                    case "suburb": return 2;
                    case "village": return 3;
                    default: return 4;
                }
            }
        }
    }
}
