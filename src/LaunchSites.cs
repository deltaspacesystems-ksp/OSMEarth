using System;

namespace OSMRoads
{
    public class LaunchSite
    {
        public string Name;
        public string Operator;
        public double Lat, Lon;

        public LaunchSite(string name, string op, double lat, double lon)
        {
            Name = name; Operator = op; Lat = lat; Lon = lon;
        }
    }

    /// <summary>
    /// Rzeczywiste kosmodromy - punkty odniesienia na planecie w skali 1:1.
    ///
    /// Wpisane na sztywno, a nie czytane z OSM, z dwoch powodow: kilkadziesiat
    /// pozycji nie jest warte formatu danych, a paczka regionalna z Polski
    /// i tak nie zawiera Bajkonuru. Lista ma dzialac zanim uzytkownik pobierze
    /// jakikolwiek pakiet.
    ///
    /// Wspolrzedne to konkretne stanowiska startowe, nie srodki osrodkow -
    /// przy promieniu Ziemi roznica siega kilku kilometrow.
    /// </summary>
    public static class LaunchSites
    {
        public static readonly LaunchSite[] All =
        {
            // --- Ameryka Polnocna ---
            new LaunchSite("Kennedy LC-39A",        "NASA / SpaceX",  28.6084, -80.6043),
            new LaunchSite("Canaveral SLC-40",      "SpaceX",         28.5619, -80.5772),
            new LaunchSite("Canaveral SLC-41",      "ULA",            28.5833, -80.5833),
            new LaunchSite("Canaveral SLC-37B",     "ULA",            28.5317, -80.5644),
            new LaunchSite("Vandenberg SLC-4E",     "SpaceX",         34.6320, -120.6110),
            new LaunchSite("Wallops Pad 0A",        "Rocket Lab",     37.8337, -75.4877),
            new LaunchSite("Starbase",              "SpaceX",         25.9971, -97.1554),
            new LaunchSite("Kodiak PSCA",           "Alaska Aerospace", 57.4356, -152.3378),

            // --- Ameryka Poludniowa ---
            new LaunchSite("Kourou ELA-3",          "Arianespace",     5.2390, -52.7683),
            new LaunchSite("Kourou ELS",            "Arianespace",     5.3050, -52.8347),
            new LaunchSite("Alcantara",             "AEB",            -2.3733, -44.3967),

            // --- Europa ---
            new LaunchSite("Esrange",               "SSC",            67.8939,  21.1069),
            new LaunchSite("Andoya",                "Andoya Space",   69.2941,  16.0203),
            new LaunchSite("SaxaVord",              "SaxaVord UK",    60.8200,  -0.7700),

            // --- Rosja i Azja Srodkowa ---
            new LaunchSite("Bajkonur 1/5",          "Roskosmos",      45.9200,  63.3422),
            new LaunchSite("Bajkonur 31/6",         "Roskosmos",      45.9961,  63.5644),
            new LaunchSite("Bajkonur 200/39",       "Roskosmos",      46.0393,  63.0325),
            new LaunchSite("Plesieck",              "WKS",            62.9271,  40.5777),
            new LaunchSite("Wostocznyj",            "Roskosmos",      51.8844, 128.3336),
            new LaunchSite("Kapustin Jar",          "WKS",            48.5700,  46.2900),

            // --- Azja Wschodnia i Poludniowa ---
            new LaunchSite("Wenchang",              "CNSA",           19.6144, 110.9510),
            new LaunchSite("Jiuquan",               "CNSA",           40.9583, 100.2917),
            new LaunchSite("Taiyuan",               "CNSA",           38.8489, 111.6083),
            new LaunchSite("Xichang",               "CNSA",           28.2464, 102.0264),
            new LaunchSite("Tanegashima",           "JAXA",           30.4000, 130.9700),
            new LaunchSite("Uchinoura",             "JAXA",           31.2510, 131.0810),
            new LaunchSite("Satish Dhawan",         "ISRO",           13.7199,  80.2304),
            new LaunchSite("Naro",                  "KARI",           34.4319, 127.5350),
            new LaunchSite("Sohae",                 "NADA",           39.6600, 124.7050),
            new LaunchSite("Semnan",                "ISA",            35.2347,  53.9210),

            // --- Bliski Wschod i Oceania ---
            new LaunchSite("Palmachim",             "ISA",            31.8844,  34.6806),
            new LaunchSite("Mahia LC-1",            "Rocket Lab",    -39.2617, 177.8644),
            new LaunchSite("Woomera",               "ASA",           -30.9556, 136.5325)
        };

        /// <summary>Najblizszy kosmodrom wraz z odlegloscia po powierzchni w metrach.
        /// Zwraca null tylko przy pustej liscie.</summary>
        public static LaunchSite Nearest(double lat, double lon, double bodyRadius,
                                         out double distanceM)
        {
            LaunchSite best = null;
            double bestD = double.MaxValue;

            for (int i = 0; i < All.Length; i++)
            {
                double d = GreatCircle(lat, lon, All[i].Lat, All[i].Lon, bodyRadius);
                if (d < bestD) { bestD = d; best = All[i]; }
            }

            distanceM = bestD;
            return best;
        }

        /// <summary>Haversine. Przy promieniu Ziemi i odlegloscach rzedu tysiecy
        /// kilometrow wzor z arccos traci precyzje na bliskich punktach - haversine
        /// jest stabilny w calym zakresie, a kosztuje tyle samo.</summary>
        public static double GreatCircle(double lat1, double lon1,
                                         double lat2, double lon2, double radius)
        {
            double p1 = lat1 * Geo.DegToRad, p2 = lat2 * Geo.DegToRad;
            double dp = (lat2 - lat1) * Geo.DegToRad;
            double dl = (lon2 - lon1) * Geo.DegToRad;

            double a = Math.Sin(dp * 0.5) * Math.Sin(dp * 0.5) +
                       Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl * 0.5) * Math.Sin(dl * 0.5);
            if (a < 0.0) a = 0.0;
            if (a > 1.0) a = 1.0;
            return 2.0 * radius * Math.Asin(Math.Sqrt(a));
        }
    }
}
