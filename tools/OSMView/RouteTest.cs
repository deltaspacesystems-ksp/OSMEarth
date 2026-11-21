using System;
using System.Globalization;
using OSMRoads;

namespace OSMView
{
    /// <summary>
    ///   osmview route                              - zestaw tras testowych
    ///   osmview route lat0 lon0 lat1 lon1 [offroad]
    /// Liczy trase PRAWDZIWYM routerem moda na prawdziwych paczkach (promien Ziemi 1:1).
    /// </summary>
    internal static class RouteTest
    {
        private const double R = 6371010.0;

        public static int Run(string[] a)
        {
            LocalStore.Reload();
            var inv = CultureInfo.InvariantCulture;
            if (a.Length >= 5)
            {
                One("custom", double.Parse(a[1], inv), double.Parse(a[2], inv), double.Parse(a[3], inv), double.Parse(a[4], inv),
                    a.Length > 5 && a[5] == "offroad" ? RouteProfile.Offroad : RouteProfile.Car, 0, 0, true);
                return 0;
            }
            int bad = 0;
            // nazwa, start, cel, oczekiwana dlugosc drogowa [km] od-do
            bad += One("Krasnik centrum -> Krasnik Fabryczny", 50.9249, 22.2292, 50.9433, 22.1936, RouteProfile.Car, 2.5, 7, true);
            bad += One("Krasnik -> Lublin", 50.9249, 22.2292, 51.2465, 22.5684, RouteProfile.Car, 45, 60, true);
            bad += One("Warszawa -> Lodz", 52.2297, 21.0122, 51.7592, 19.4560, RouteProfile.Car, 120, 160, false);
            bad += One("Krakow -> Katowice", 50.0614, 19.9366, 50.2649, 19.0238, RouteProfile.Car, 70, 95, false);
            Console.WriteLine(bad == 0 ? "\nWSZYSTKIE TRASY OK" : "\nZLE TRASY: " + bad);
            return bad == 0 ? 0 : 1;
        }

        private static int One(string name, double la0, double lo0, double la1, double lo1, RouteProfile p,
                               double minKm, double maxKm, bool steps)
        {
            Console.WriteLine();
            double straight = RoadRouter.Dist(la0, lo0, la1, lo1, R) / 1000.0;
            NavRoute r = RoadRouter.Plan(la0, lo0, la1, lo1, R, p, null, s => Console.Write("\r  " + s + "        "));
            Console.Write("\r");
            if (r.Error != null) { Console.WriteLine("{0}: BLAD {1}", name, r.Error); return 1; }

            double km = r.LengthM / 1000.0;
            GeoPoint e0 = r.Points[0], e1 = r.Points[r.Points.Count - 1];
            Console.WriteLine("{0}: {1:F1} km drogi (w linii prostej {2:F1}), {3}, {4} pkt, {5} manewrow, graf {6:N0} wezlow, {7:F0} ms",
                              name, km, straight, RoadRouter.FormatTime(r.TimeS), r.Points.Count, r.Steps.Count, r.GraphNodes, r.BuildMs);
            Console.WriteLine("   start dociety {0:F0} m od punktu, cel {1:F0} m",
                              RoadRouter.Dist(la0, lo0, e0.Lat, e0.Lon, R), RoadRouter.Dist(la1, lo1, e1.Lat, e1.Lon, R));
            if (steps)
                foreach (RouteStep s in r.Steps)
                    Console.WriteLine("   {0,8}  {1,-12} {2,5:F0}°  {3}", RoadRouter.FormatDistance(s.AtM), s.Turn, s.Angle, RoadRouter.Describe(s));
            else
                Console.WriteLine("   pierwsze: {0} | ostatnie przed celem: {1}", RoadRouter.Describe(r.Steps[0]),
                                  RoadRouter.Describe(r.Steps[Math.Max(0, r.Steps.Count - 2)]));

            bool ok = minKm <= 0 || (km >= minKm && km <= maxKm);
            if (!ok) Console.WriteLine("   ^^^ BLAD: oczekiwano {0}-{1} km", minKm, maxKm);
            return ok ? 0 : 1;
        }
    }
}
