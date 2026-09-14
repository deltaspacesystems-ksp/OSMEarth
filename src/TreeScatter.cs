using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    public struct TreeInstance
    {
        public Vector3 Pos;      // lokalne metry w ramce kafla
        public float Scale;
        public float YawDeg;
        public byte Kind;        // 0 = dab, 1 = sosna
    }

    /// <summary>
    /// Rozrzuca drzewa wewnatrz poligonow lasu/parku. Jitterowana krata zamiast
    /// czystego losowania - inaczej drzewa robia kepy i luki.
    ///
    /// Bezpieczne watkowo: same struktury Vector, wysokosci z TerrainGrid.
    /// </summary>
    public static class TreeScatter
    {
        public static List<TreeInstance> Build(TerrainGrid grid, double bodyRadius,
                                               List<OsmWay> ways,
                                               double lat0, double lon0, double alt0,
                                               float densityScale, int maxTrees)
        {
            var result = new List<TreeInstance>();
            var ring = new List<Vector2>();

            foreach (OsmWay way in ways)
            {
                if (!way.IsArea) continue;
                float spacing = AreaKinds.TreeSpacing(way.Area);
                if (spacing <= 0f) continue;
                spacing /= Mathf.Max(0.2f, densityScale);

                ring.Clear();
                int n = way.Points.Count;
                if (n >= 2 &&
                    Math.Abs(way.Points[0].Lat - way.Points[n - 1].Lat) < 1e-9 &&
                    Math.Abs(way.Points[0].Lon - way.Points[n - 1].Lon) < 1e-9)
                    n--;
                if (n < 3) continue;

                float minX = float.MaxValue, maxX = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;
                for (int i = 0; i < n; i++)
                {
                    GeoPoint g = way.Points[i];
                    double east, north;
                    Geo.ToLocalMeters(g.Lat, g.Lon, lat0, lon0, bodyRadius, out east, out north);
                    var p = new Vector2((float)east, (float)north);
                    ring.Add(p);
                    if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
                    if (p.y < minZ) minZ = p.y; if (p.y > maxZ) maxZ = p.y;
                }

                bool conifer = way.Area == AreaKind.Forest;

                for (float z = minZ; z <= maxZ && result.Count < maxTrees; z += spacing)
                {
                    for (float x = minX; x <= maxX && result.Count < maxTrees; x += spacing)
                    {
                        uint h = Hash((uint)(int)(x * 4f + 32768), (uint)(int)(z * 4f + 32768));

                        // jitter w obrebie komorki - krata bez tego jest widoczna z powietrza
                        float jx = x + ((h % 1000u) / 1000f - 0.5f) * spacing * 0.85f;
                        float jz = z + (((h >> 10) % 1000u) / 1000f - 0.5f) * spacing * 0.85f;

                        if (!PointInPolygon(ring, jx, jz)) continue;

                        float y = LanduseMeshBuilder.HeightLocal(grid, lat0, lon0, bodyRadius,
                                                                 alt0, jx, jz);

                        result.Add(new TreeInstance
                        {
                            Pos = new Vector3(jx, y, jz),
                            Scale = 0.75f + ((h >> 20) % 100u) / 160f,
                            YawDeg = ((h >> 6) % 360u),
                            Kind = (byte)(conifer && ((h >> 3) % 100u) < 55u ? 1 : 0)
                        });
                    }
                }
            }

            return result;
        }

        private static bool PointInPolygon(List<Vector2> poly, float x, float z)
        {
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if ((poly[i].y > z) != (poly[j].y > z) &&
                    x < (poly[j].x - poly[i].x) * (z - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            }
            return inside;
        }

        private static uint Hash(uint x, uint y)
        {
            uint h = x * 374761393u + y * 668265263u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }
}
