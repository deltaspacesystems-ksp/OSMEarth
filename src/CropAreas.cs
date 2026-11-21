using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>Obszar upraw/laki kafla (lokalne metry kafla, przyciety do kafla).</summary>
    internal sealed class CropArea
    {
        public Vector2[] Poly;
        public float X0, Z0, X1, Z1;
        public bool Farm;          // pole uprawne (inaczej laka / trawnik)
    }

    /// <summary>Wyciecie pol i lak z danych kafla (watek roboczy) - dla CropScatter.</summary>
    internal static class CropAreas
    {
        public static List<CropArea> Areas(List<OsmWay> ways, double lat0, double lon0, double r, float clipE, float clipN)
        {
            var list = new List<CropArea>();
            foreach (OsmWay w in ways)
            {
                if (!w.IsArea || (w.Area != AreaKind.Farmland && w.Area != AreaKind.Grass)) continue;
                int n = w.Points.Count;
                if (n < 4) continue;
                var pts = new List<Vector2>(n);
                for (int i = 0; i + 1 < n; i++)
                {
                    double e, no;
                    Geo.ToLocalMeters(w.Points[i].Lat, w.Points[i].Lon, lat0, lon0, r, out e, out no);
                    pts.Add(new Vector2((float)e, (float)no));
                }
                pts = TreeScatter.ClipRect(pts, clipE + 1f, clipN + 1f);
                if (pts.Count < 3) continue;
                var a = new CropArea { Poly = pts.ToArray(), Farm = w.Area == AreaKind.Farmland, X0 = float.MaxValue, Z0 = float.MaxValue, X1 = float.MinValue, Z1 = float.MinValue };
                foreach (Vector2 p in pts)
                {
                    a.X0 = Math.Min(a.X0, p.x); a.X1 = Math.Max(a.X1, p.x);
                    a.Z0 = Math.Min(a.Z0, p.y); a.Z1 = Math.Max(a.Z1, p.y);
                }
                list.Add(a);
            }
            return list;
        }
    }
}
