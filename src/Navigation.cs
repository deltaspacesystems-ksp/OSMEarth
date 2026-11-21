using System;
using System.Threading;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Stan nawigacji wspolny dla HUD-u, strzalek 3D, planera i ekranow RPM.
    ///
    /// Trase liczy watek w tle (RoadRouter), a Tick() w watku gry przejmuje wynik,
    /// rzutuje statek na trase i pilnuje zjechania z niej - po kilku sekundach poza
    /// trasa liczy nowa od miejsca, w ktorym statek jest.
    /// </summary>
    public static class Navigation
    {
        public static NavRoute Route;
        public static string DestName = "";
        public static double DestLat, DestLon;
        public static bool HasDestination;

        public static volatile bool Busy;
        public static volatile string Status = "";
        public static string Error;

        /// <summary>Rosnie przy kazdej nowej trasie - mapa i strzalki wiedza, ze trzeba przerysowac.</summary>
        public static int Version;

        // --- postep (liczony w Tick) ---
        public static double AlongM, CrossM, RemainingM, DistToStep, EtaS, SpeedMs;
        public static int SegIdx, StepIdx = 1;
        public static bool Arrived, OnRoute;
        public static double VesselLat, VesselLon;

        private static NavRoute pending;
        private static string pendingError;
        private static int seq;
        private static bool firstFix;
        private static float offSince = -1f, lastPlanAt = -100f, arrivedAt = -1f;
        private const double OffRouteM = 45.0;

        public static bool Active { get { return HasDestination; } }

        public static RouteStep NextStep
        {
            get { return Route != null && StepIdx < Route.Steps.Count ? Route.Steps[StepIdx] : null; }
        }

        public static RouteStep StepAfterNext
        {
            get { return Route != null && StepIdx + 1 < Route.Steps.Count ? Route.Steps[StepIdx + 1] : null; }
        }

        public static void Start(double lat, double lon, string name)
        {
            DestLat = lat; DestLon = lon;
            DestName = string.IsNullOrEmpty(name) ? "Destination" : name;
            HasDestination = true;
            Arrived = false; arrivedAt = -1f;
            Route = null; Error = null;
            Version++;

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody == null) { Error = "Navigation works in flight, with an active vessel."; return; }
            Plan(v.latitude, Norm(v.longitude), v.mainBody.Radius);
        }

        public static void Stop()
        {
            Interlocked.Increment(ref seq);
            HasDestination = false;
            Route = null; pending = null; Error = null;
            Busy = false; Status = "";
            Version++;
        }

        public static void Reroute()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (!HasDestination || v == null || v.mainBody == null) return;
            Plan(v.latitude, Norm(v.longitude), v.mainBody.Radius);
        }

        private static void Plan(double lat, double lon, double radius)
        {
            int my = Interlocked.Increment(ref seq);
            Busy = true;
            Status = "Starting";
            lastPlanAt = Time.realtimeSinceStartup;
            double dLat = DestLat, dLon = DestLon;
            RouteProfile prof = OsmSettings.NavProfile;

            var t = new Thread(() =>
            {
                NavRoute r = null;
                string err = null;
                try
                {
                    r = RoadRouter.Plan(lat, lon, dLat, dLon, radius, prof, () => my != seq, s => { if (my == seq) Status = s; });
                    if (r.Error != null) { err = r.Error; r = null; }
                }
                catch (Exception e) { err = "Routing failed: " + e.Message; Debug.LogWarning("[OSMRoads] routing: " + e); }

                if (my != seq) return;
                pendingError = err;
                pending = r;
                Busy = false;
            }) { IsBackground = true, Name = "OSMRoads.Route" };
            t.Start();
        }

        public static double Norm(double lon)
        {
            lon = ((lon + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
            return lon;
        }

        // ------------------------------------------------------------------

        /// <summary>Kierunek jazdy w stopniach od polnocy. Z predkosci, gdy pojazd jedzie;
        /// na postoju zostaje ostatni - os "up" pojazdu lazika czesto celuje w niebo
        /// i z niej kierunek skakal losowo.</summary>
        public static double HeadingDeg;
        public static bool HeadingValid;
        private static Guid headingVessel;

        private static void UpdateHeading(Vessel v)
        {
            if (v == null || v.mainBody == null) return;
            if (v.id != headingVessel) { headingVessel = v.id; HeadingValid = false; }

            Vector3d up = v.upAxis;
            Vector3d north = Vector3d.Exclude(up, v.mainBody.transform.up).normalized;
            Vector3d east = Vector3d.Cross(up, north).normalized;

            Vector3d dir = Vector3d.zero;
            if (v.srfSpeed > 1.5) dir = Vector3d.Exclude(up, v.srf_velocity);
            else if (!HeadingValid)
            {
                // na starcie: nos pojazdu, jesli w ogole lezy poziomo, inaczej kierunek trasy
                Vector3d nose = Vector3d.Exclude(up, v.ReferenceTransform.up);
                if (nose.magnitude > 0.5) dir = nose;
                else if (Route != null && Route.Points.Count > 1)
                {
                    int seg;
                    GeoPoint a = PointAt(Route, AlongM, out seg), b = PointAt(Route, AlongM + 15.0, out seg);
                    HeadingDeg = RoadRouter.Bearing(a.Lat, a.Lon, b.Lat, b.Lon);
                    HeadingValid = true;
                    return;
                }
            }
            if (dir.sqrMagnitude < 1e-6) return;

            double h = Math.Atan2(Vector3d.Dot(dir, east), Vector3d.Dot(dir, north)) * 180.0 / Math.PI;
            if (h < 0) h += 360.0;
            if (!HeadingValid) { HeadingDeg = h; HeadingValid = true; return; }
            double d = h - HeadingDeg;
            while (d > 180) d -= 360;
            while (d < -180) d += 360;
            double k = Math.Min(1.0, Time.deltaTime * 6.0);          // lagodnie, bez drgan na wybojach
            HeadingDeg = (HeadingDeg + d * k + 360.0) % 360.0;
        }

        public static void Tick(Vessel v)
        {
            UpdateHeading(v);
            if (!HasDestination) return;

            if (!Busy && (pending != null || pendingError != null))
            {
                if (pending != null)
                {
                    Route = pending; Error = null;
                    SegIdx = 0; StepIdx = 1; firstFix = true;
                    Version++;
                }
                else if (Route == null) Error = pendingError;
                pending = null; pendingError = null;
            }

            if (v == null || Route == null || Route.Points.Count < 2) return;

            VesselLat = v.latitude; VesselLon = Norm(v.longitude);
            SpeedMs = v.srfSpeed;
            Project(Route, VesselLat, VesselLon, firstFix);
            firstFix = false;

            if (CrossM > OffRouteM)
            {
                // moze statek przeskoczyl dalej (albo wrocil) - najpierw szukamy na calej trasie
                Project(Route, VesselLat, VesselLon, true);
            }
            OnRoute = CrossM <= OffRouteM;

            float now = Time.realtimeSinceStartup;
            if (!OnRoute && !Arrived)
            {
                if (offSince < 0f) offSince = now;
                if (now - offSince > 5f && now - lastPlanAt > 15f && !Busy) { Reroute(); offSince = -1f; }
            }
            else offSince = -1f;

            RemainingM = Math.Max(0.0, Route.LengthM - AlongM);
            while (StepIdx < Route.Steps.Count - 1 && Route.Steps[StepIdx].AtM < AlongM + 4.0) StepIdx++;
            RouteStep ns = NextStep;
            DistToStep = ns != null ? Math.Max(0.0, ns.AtM - AlongM) : 0.0;

            double planned = Route.LengthM > 1 ? Route.TimeS * RemainingM / Route.LengthM : 0.0;
            EtaS = SpeedMs > 3.0 ? RemainingM / SpeedMs : planned;

            if (!Arrived && RemainingM < 20.0 && OnRoute)
            {
                Arrived = true; arrivedAt = now;
                ScreenMessages.PostScreenMessage("Arrived at " + DestName, 5f, ScreenMessageStyle.UPPER_CENTER);
            }
            if (Arrived && arrivedAt > 0f && now - arrivedAt > 12f) Stop();
        }

        /// <summary>Rzut punktu na lamana trasy. Zwykle tylko okno wokol ostatniego odcinka,
        /// zeby trasa zawracajaca obok siebie nie przeskakiwala na drugi pas.</summary>
        private static void Project(NavRoute r, double lat, double lon, bool whole)
        {
            int n = r.Points.Count;
            int i0 = whole ? 0 : Math.Max(0, SegIdx - 4);
            int i1 = whole ? n - 2 : Math.Min(n - 2, SegIdx + 80);
            double best = double.MaxValue, bestAlong = AlongM; int bestI = SegIdx;
            double R = r.BodyRadius;

            for (int i = i0; i <= i1; i++)
            {
                double ax, ay, bx, by;
                Geo.ToLocalMeters(r.Points[i].Lat, r.Points[i].Lon, lat, lon, R, out ax, out ay);
                Geo.ToLocalMeters(r.Points[i + 1].Lat, r.Points[i + 1].Lon, lat, lon, R, out bx, out by);
                double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
                double t = l2 > 1e-9 ? Math.Max(0.0, Math.Min(1.0, -(ax * dx + ay * dy) / l2)) : 0.0;
                double px = ax + t * dx, py = ay + t * dy;
                double d = Math.Sqrt(px * px + py * py);
                if (!whole) d += Math.Abs(i - SegIdx) * 0.01;            // przy remisie blizej poprzedniego
                if (d < best) { best = d; bestI = i; bestAlong = r.Cum[i] + t * (r.Cum[i + 1] - r.Cum[i]); }
            }
            SegIdx = bestI; AlongM = bestAlong; CrossM = best;
        }

        /// <summary>Punkt trasy na danym metrze (interpolowany) i kierunek w stopniach.</summary>
        public static GeoPoint PointAt(NavRoute r, double s, out int seg)
        {
            int n = r.Points.Count;
            s = Math.Max(0.0, Math.Min(r.LengthM, s));
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (r.Cum[mid] <= s) lo = mid; else hi = mid;
            }
            seg = lo;
            double len = r.Cum[hi] - r.Cum[lo];
            double t = len > 1e-6 ? (s - r.Cum[lo]) / len : 0.0;
            GeoPoint a = r.Points[lo], b = r.Points[hi];
            double dl = b.Lon - a.Lon;
            if (dl > 180) dl -= 360; else if (dl < -180) dl += 360;
            return new GeoPoint(a.Lat + (b.Lat - a.Lat) * t, a.Lon + dl * t);
        }
    }
}
