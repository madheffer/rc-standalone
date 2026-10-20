namespace Source2.Compiler.Maps.NavGen;

public sealed partial class NavAnalysis
{
    /// <summary>1814c1ff0: the corner nearest <paramref name="p"/> in XY, the first of equals.</summary>
    private static int NearestCorner(Area a, V p)
    {
        var best = float.MaxValue;
        var k = -1;
        for (var i = 0; i < a.Corners.Length; i++)
        {
            var dx = p.X - a.Corners[i].X;
            var dy = p.Y - a.Corners[i].Y;
            var d = dx * dx + dy * dy;
            if (d < best)
            {
                best = d;
                k = i;
            }
        }
        return k;
    }

    /// <summary>
    /// 1814c43b0: the area's z under (x, y): the fan triangle (previous, nearest, next corner) by its
    /// barycentric weights; with more than three corners, the triangle two corners on when the first is
    /// degenerate or the point lies outside it, if that one is valid and the point lies less far outside.
    /// </summary>
    public static float ZAt(Area a, float x, float y)
    {
        var c = a.Corners;
        var n = c.Length;
        var k = NearestCorner(a, new V(x, y, 0f));
        V A = c[(n - 1 + k) % n], B = c[k], C = c[(k + 1) % n];
        float cx = C.X - B.X, ax = A.X - B.X, cy = C.Y - B.Y, ay = A.Y - B.Y, px = x - B.X, py = y - B.Y;
        var det = cx * ay - cy * ax;
        float s, t;
        if (!(1.1920929e-07f < MathF.Abs(det)))
        {
            det = -1f;
            s = 0f;
            t = 0f;
        }
        else
        {
            s = (px * ay - py * ax) / det;
            t = MathF.Abs(ax) > MathF.Abs(ay) ? (px - cx * s) / ax : (py - cy * s) / ay;
        }
        if (3 < n)
        {
            var outside = MathF.Max(MathF.Max(0f, -t), MathF.Max(-s, t + s - 1f));
            if (0f > det || 0f < outside)
            {
                var k2 = (k + 2) % n;
                V A2 = c[(n - 1 + k2) % n], B2 = c[k2], C2 = c[(k2 + 1) % n];
                float cx2 = C2.X - B2.X, ax2 = A2.X - B2.X, cy2 = C2.Y - B2.Y, ay2 = A2.Y - B2.Y, px2 = x - B2.X, py2 = y - B2.Y;
                var det2 = cx2 * ay2 - ax2 * cy2;
                if (1.1920929e-07f < MathF.Abs(det2))
                {
                    var s2 = (ay2 * px2 - ax2 * py2) / det2;
                    var t2 = MathF.Abs(ax2) > MathF.Abs(ay2) ? (px2 - cx2 * s2) / ax2 : (py2 - s2 * cy2) / ay2;
                    if (det2 > 0f)
                    {
                        var use2 = 0f > det || MathF.Max(MathF.Max(0f, -t2), MathF.Max(-s2, t2 + s2 - 1f)) < outside;
                        if (use2)
                            return (A2.Z - B2.Z) * t2 + B2.Z + (C2.Z - B2.Z) * s2;
                    }
                }
            }
        }
        return (A.Z - B.Z) * t + B.Z + (C.Z - B.Z) * s;
    }

    /// <summary>1814c5a00: whether (x, y) lies within the area's bounds and every edge's outward half-plane, both grown by <paramref name="tolerance"/>.</summary>
    public static bool ContainsXY(Area a, V p, float tolerance)
    {
        if (!(a.Min.X - tolerance <= p.X && p.X <= tolerance + a.Max.X && a.Min.Y - tolerance <= p.Y && p.Y <= tolerance + a.Max.Y))
            return false;
        var c = a.Corners;
        for (var i = 0; i < c.Length; i++)
        {
            var next = c[(i + 1) % c.Length];
            var fx = p.X - next.X;
            var fy = p.Y - next.Y;
            var ex = c[i].X - next.X;
            var ny = -(c[i].Y - next.Y);
            var len = MathF.Sqrt(ex * ex + 0f + ny * ny);
            float nx, nyy;
            if (len < 1e-17f || 1e+17f < len)
            {
                if (len != 0f)
                    throw new NotSupportedException("normalising an edge outside 1e-17 .. 1e17 (18146bed0) is not ported");
                nx = 0f;
                nyy = 0f;
            }
            else
            {
                nx = 1f / len * ny;
                nyy = 1f / len * ex;
            }
            if (tolerance < fx * nx + fy * nyy)
                return false;
        }
        return true;
    }

    /// <summary>
    /// 1814b2910 with flags 2 and the hull record of 1802b11b0 (80, 18, 16; tolerance 0.05; single cell):
    /// of the areas in the point's grid cell that contain it in XY, are not flagged 8, 0x20 or 0x200,
    /// carry no 0x20000 attribute and whose z there lies from 80 below to 18 above it, the one nearest
    /// in z (a later area wins a tie). Every area is taken as flat-edged (no corner record flags).
    /// </summary>
    public Area? GetNavArea(V pos, int hull = 0)
    {
        if (!_grids.TryGetValue(hull, out var cells) || !float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z))
            return null;
        var cx = Clamp((int)((pos.X - OriginX) / CellSize), GridX - 1);
        var cy = Clamp((int)((pos.Y - OriginY) / CellSize), GridY - 1);
        Area? best = null;
        var bestZ = -100000000f;
        foreach (var a in cells[cy * GridX + cx])
        {
            if ((a.Flags & 8) != 0 || !ContainsXY(a, pos, 0.05f) || (a.Flags & 0x20) != 0 || (a.Flags & 0x200) != 0)
                continue;
            var z = ZAt(a, pos.X, pos.Y);
            if (z <= pos.Z + 18f && pos.Z - 80f <= z && MathF.Abs(pos.Z - z) <= MathF.Abs(pos.Z - bestZ) && (a.Attributes & 0x20000) == 0)
            {
                best = a;
                bestZ = z;
            }
        }
        return best;
    }

    /// <summary>181467040: the squared distance from <paramref name="p"/> to the box, summed (y, z) then x.</summary>
    private static float BoxGapSq(V min, V max, V p)
    {
        var gx = MathF.Max(p.X - max.X, 0f) + MathF.Max(min.X - p.X, 0f);
        var gy = MathF.Max(min.Y - p.Y, 0f) + MathF.Max(p.Y - max.Y, 0f);
        var gz = MathF.Max(min.Z - p.Z, 0f) + MathF.Max(p.Z - max.Z, 0f);
        return gy * gy + gz * gz + gx * gx;
    }

    /// <summary>1814d3750 for an area: the squared distance from <paramref name="p"/> to its closest point when below <paramref name="limitSq"/>, else -1.</summary>
    private static float AreaWithin(Area a, V p, float limitSq)
    {
        if (!(BoxGapSq(a.Min, a.Max, p) < limitSq))
            return -1f;
        var q = ClosestOnArea(a, p);
        var d = (p.Y - q.Y) * (p.Y - q.Y) + (p.X - q.X) * (p.X - q.X) + (p.Z - q.Z) * (p.Z - q.Z);
        return d < limitSq ? d : -1f;
    }

    /// <summary>
    /// The navsystem surrounding search ClassifySniperSpot runs (1810f5f80 / 1810fb4e0 with a start
    /// area: 1802c9800, 1802cdb70, 180324230, 1802c7fe0): Dijkstra from <paramref name="start"/> at
    /// <paramref name="origin"/> (its record: no parent, how 9). Each neighbour of a popped area (edge by
    /// edge) that is not closed moves to the closest point of their shared edge to the area's position
    /// (1814d4260 with no target: CNavPathCost's crossing toward the position itself) and costs the
    /// straight distance there; a neighbour whose closest point is <paramref name="range"/> or more from
    /// the origin is closed, one whose crossing is farther than the range is skipped, one already open
    /// at no greater cost is left alone; else its record takes the crossing, the parent and the edge.
    /// Every area is expanded (the visit callback always continues).
    /// </summary>
    public void SurroundingSearch(Area start, V origin, float range, int hull = 0)
    {
        var rs = RecordOf(start);
        rs.Parent = null;
        rs.How = 9;
        rs.BackEdge = 0xff;
        rs.Pos = origin;
        var s = new Search();
        s.Push(start, 0f, 0f, 0f);
        var rangeSq = range * range;
        while (s.Heap.Count > 0)
        {
            var cur = s.Pop();
            var area = cur.Area;
            s.Closed.Add(area);
            var curPos = RecordOf(area).Pos;
            for (var e = 0; e < area.Links.Length; e++)
                foreach (var link in area.Links[e])
                {
                    var n = link.To;
                    if (s.Closed.Contains(n))
                        continue;
                    if (hull != -1 && n.Hull != hull)
                        continue;
                    var (p0, p1) = Portal(n, area, link.BackEdge, e);
                    var cross = Crossing(p0, p1, curPos, curPos);
                    var c = MathF.Sqrt((curPos.Y - cross.Y) * (curPos.Y - cross.Y) + (curPos.Z - cross.Z) * (curPos.Z - cross.Z)
                                       + (curPos.X - cross.X) * (curPos.X - cross.X));
                    if (c < 0f)
                        continue;
                    var total = cur.F + c;
                    if (range >= 0f)
                    {
                        if (AreaWithin(n, origin, rangeSq) < 0f)
                        {
                            s.Closed.Add(n);
                            continue;
                        }
                        var dy = cross.Y - origin.Y;
                        var dz = cross.Z - origin.Z;
                        var d2 = dy * dy + (cross.X - origin.X) * (cross.X - origin.X) + dz * dz;
                        if (!(d2 < rangeSq) && !(d2 == rangeSq))
                            continue;
                    }
                    if (s.Open.TryGetValue(n, out var idx) && idx >= 0 && s.Heap[idx].F <= total)
                        continue;
                    var r = RecordOf(n);
                    r.Parent = area;
                    r.How = e;
                    r.BackEdge = (byte)link.BackEdge;
                    r.Pos = cross;
                    s.Push(n, total, total, 0f);
                }
        }
    }
}
