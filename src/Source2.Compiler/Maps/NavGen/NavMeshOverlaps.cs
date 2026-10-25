namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// The analysis' removal of overlapping connections (server.dll 20261015 1814b86f0, called by the shared
/// analysis step 1814cfaa0 inside CSNavMesh::Analyze before the AI data): per area in order, per edge, the
/// connections' intervals along the edge (1814ada50) sorted by start; where an interval starts before the
/// previous one ends, the connection whose area's edge lies farther from the middle of the overlap is
/// removed both ways (behind the bool at 181e81d01, 1 in the .data). The removals change the mesh the
/// .nav saves and the analysis reads (LIGHTING_ROADMAP L24).
/// </summary>
public static class NavMeshOverlaps
{
    // 181e81d18 (18, the .data's step height): the most two edges may lie apart to overlap.
    private const float MaxEdgeGap = 18f;

    private const float Epsilon = 1.1920929e-07f;

    private readonly record struct Interval(float Lo, float Hi, int Index, int BackIndex, NavWriter.Area To, int Edge, int ToEdge);

    private readonly record struct F3(float X, float Y, float Z);

    /// <summary>1814b86f0 over every area's edges, in area order.</summary>
    public static void Remove(NavWriter.Mesh mesh)
    {
        foreach (var a in mesh.Areas)
            for (var e = 0; e < a.Connections.Length; e++)
            {
                var iv = Intervals(mesh, a, e);
                var conns = a.Connections[e];
                for (var i = 0; i < iv.Count - 1; i++)
                {
                    var cur = iv[i];
                    var next = iv[i + 1];
                    if (!(next.Lo + Epsilon < cur.Hi))
                        continue;
                    // The middle of [max start, the next one's end], from the edge's end corner back to its start.
                    var t = (next.Hi + (cur.Lo <= next.Lo ? next.Lo : cur.Lo)) * 0.5f;
                    var p0 = Corner(mesh, a, e);
                    var p1 = Corner(mesh, a, (e + 1) % a.Connections.Length);
                    var p = new F3(p1.X + (p0.X - p1.X) * t, p1.Y + (p0.Y - p1.Y) * t, p1.Z + (p0.Z - p1.Z) * t);
                    var dCur = EdgeDistance(mesh, p, conns[cur.Index]);
                    var dNext = EdgeDistance(mesh, p, conns[next.Index]);
                    var removeCur = dNext < dCur;
                    var pick = removeCur ? cur : next;
                    var back = pick.To.Connections[pick.ToEdge];
                    // Both entries must still be where the interval says (the back index is never renumbered).
                    if (conns[pick.Index].Area != pick.To.Id || pick.BackIndex < 0 || pick.BackIndex >= back.Count || back[pick.BackIndex].Area != a.Id)
                        continue;
                    conns.RemoveAt(pick.Index);
                    back.RemoveAt(pick.BackIndex);
                    iv.RemoveAt(removeCur ? i : i + 1);
                    for (var k = 0; k < iv.Count; k++)
                        if (iv[k].Index > pick.Index)
                            iv[k] = iv[k] with { Index = iv[k].Index - 1 };
                    i--;
                }
            }
    }

    // 1814669d0: the distance from p to the closest point of the connection's edge (from its end corner to its start).
    private static float EdgeDistance(NavWriter.Mesh mesh, F3 p, (uint Area, int Edge) c)
    {
        var to = mesh.Areas[(int)c.Area - 1];
        var q = Closest(p, Corner(mesh, to, (c.Edge + 1) % to.Connections.Length), Corner(mesh, to, c.Edge), out _);
        var dz = p.Z - q.Z;
        var dy = p.Y - q.Y;
        var dx = p.X - q.X;
        return MathF.Sqrt(dz * dz + dy * dy + dx * dx);
    }

    // 1814655e0: the closest point to p on a to b and its parameter, clamped to [0, 1].
    private static F3 Closest(F3 p, F3 a, F3 b, out float t)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var dz = b.Z - a.Z;
        var len2 = dz * dz + dy * dy + dx * dx;
        var s = len2 >= 1e-05f ? ((dz * p.Z + dy * p.Y + dx * p.X) - (dz * a.Z + dy * a.Y + dx * a.X)) / len2 : 0f;
        t = 0f;
        if (s >= 0f)
            t = s <= 1f ? s : 1f;
        return new F3(dx * t + a.X, dy * t + a.Y, dz * t + a.Z);
    }

    /// <summary>
    /// 1814ada50: per connection of the edge whose area's edge holds this area back (1814ca550), with the two
    /// edges within <see cref="MaxEdgeGap"/> (closest points of the segments in double) and an overlap of at
    /// least epsilon (1814c76a0), the overlap's ends projected on the edge; sorted by start (1814a9280: an
    /// insertion sort up to 32 entries).
    /// </summary>
    private static List<Interval> Intervals(NavWriter.Mesh mesh, NavWriter.Area a, int e)
    {
        var list = new List<Interval>();
        var conns = a.Connections[e];
        var n = a.Connections.Length;
        for (var idx = 0; idx < conns.Count; idx++)
        {
            var (id, edgeB) = conns[idx];
            var b = mesh.Areas[(int)id - 1];
            var backList = b.Connections[edgeB];
            if (b != a && !backList.Any(c => c.Area == a.Id))
                continue;
            var backIndex = -1;
            for (var k = 0; k < backList.Count; k++)
                if (backList[k].Area == a.Id)
                    backIndex = k;
            var aEnd = Corner(mesh, a, (e + 1) % n);
            var aStart = Corner(mesh, a, e);
            var m = b.Connections.Length;
            if (!(SegmentGap(aEnd, aStart, Corner(mesh, b, (edgeB + 1) % m), Corner(mesh, b, edgeB)) <= MaxEdgeGap))
                continue;
            var (hiPoint, loPoint, length) = Overlap(mesh, a, e, b, edgeB);
            if (!(length >= Epsilon))
                continue;
            Closest(new F3(hiPoint.X, hiPoint.Y, hiPoint.Z), aEnd, aStart, out var tHi);
            Closest(new F3(loPoint.X, loPoint.Y, loPoint.Z), aEnd, aStart, out var tLo);
            // Either order gives (smaller, larger); Valve warns when the near end's parameter is not the larger.
            var (lo, hi) = tLo <= tHi ? (tLo, tHi) : (tHi, tLo);
            list.Add(new Interval(lo, hi, idx, backIndex, b, e, edgeB));
        }
        if (list.Count > 32)
            throw new NotSupportedException("a nav edge with more than 32 overlapping connections (1814a9280's introsort) is not ported");
        // Insertion sort: stable.
        return [.. list.OrderBy(v => v.Lo)];
    }

    // The segments' closest distance (Ericson's segment pair, in double) as 1814ada50 computes it.
    private static double SegmentGap(F3 p0f, F3 p1f, F3 q0f, F3 q1f)
    {
        double p0x = p0f.X, p0y = p0f.Y, p0z = p0f.Z, q0x = q0f.X, q0y = q0f.Y, q0z = q0f.Z;
        double d1x = (double)p1f.X - p0x, d1y = (double)p1f.Y - p0y, d1z = (double)p1f.Z - p0z;
        double d2x = (double)q1f.X - q0x, d2y = (double)q1f.Y - q0y, d2z = (double)q1f.Z - q0z;
        var a = d1y * d1y + d1x * d1x + d1z * d1z;
        var b = d2y * d1y + d2x * d1x + d2z * d1z;
        var c = d2y * d2y + d2x * d2x + d2z * d2z;
        double ry = p0y - q0y, rx = p0x - q0x, rz = p0z - q0z;
        var f = ry * d2y + rx * d2x + rz * d2z;
        var e = ry * d1y + rx * d1x + rz * d1z;
        var denom = c * a - b * b;
        double tN = f, tD = c, sN, sD;
        if (denom > 1.1920928955078125e-05)
        {
            sD = denom;
            sN = f * b - e * c;
            if (sN < 0)
                sN = 0;
            else
            {
                tN = f * a - e * b;
                tD = denom;
                if (denom < sN)
                {
                    tN = f + b;
                    tD = c;
                    sN = denom;
                }
            }
        }
        else
        {
            sD = 1.0;
            sN = 0;
        }
        var sDen = sD;
        var clampS = false;
        if (tN < 0)
        {
            sN = -e;
            tN = 0;
            clampS = true;
        }
        else if (tD < tN)
        {
            sN = b - e;
            tN = tD;
            clampS = true;
        }
        if (clampS)
        {
            if (sN < 0)
                sN = 0;
            else
            {
                sDen = a;
                if (a < sN)
                {
                    sDen = sD;
                    sN = sD;
                }
            }
        }
        var s = Math.Abs(sN) >= 1.1920928955078125e-07 ? sN / sDen : 0;
        var t = Math.Abs(tN) >= 1.1920928955078125e-07 ? tN / tD : 0;
        var gz = (d1z * s + p0z) - (d2z * t + q0z);
        var gx = (d1x * s + p0x) - (d2x * t + q0x);
        var gy = (d1y * s + p0y) - (d2y * t + q0y);
        return Math.Sqrt(gx * gx + gy * gy + gz * gz);
    }

    /// <summary>
    /// 1814c76a0 for areas without attribute 0x8000 (the oriented path, 1814eada0, is not ported): edge 7 reads
    /// as 0 and the other's 7 as 2; 1814eabb0 projects the other edge onto this one in 2D, clamps to it and
    /// returns its far point, its near point and their 2D distance.
    /// </summary>
    private static (F3 Hi, F3 Lo, float Length) Overlap(NavWriter.Mesh mesh, NavWriter.Area a, int edgeA, NavWriter.Area b, int edgeB)
    {
        if (edgeA == 8)
            throw new NotSupportedException("nav connection direction 8 (1814e5480) is not ported");
        if ((a.Attributes & 0x8000) != 0 || (b.Attributes & 0x8000) != 0)
            throw new NotSupportedException("the oriented connection overlap (1814eada0) is not ported");
        var ea = edgeA == 7 ? 0 : edgeA;
        var eb = edgeB == 7 ? 2 : edgeB;
        var s1a = Corner(mesh, a, ea);
        var s1b = Corner(mesh, a, (ea + 1) % a.Connections.Length);
        var s2a = Corner(mesh, b, eb);
        var s2b = Corner(mesh, b, (eb + 1) % b.Connections.Length);
        var dx = s1b.X - s1a.X;
        var dy = s1b.Y - s1a.Y;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        float inv = 0f, ux, uy;
        if (len <= Epsilon)
        {
            ux = 1f;
            uy = 0f;
        }
        else
        {
            inv = 1f / len;
            ux = dx * inv;
            uy = dy * inv;
        }
        var t0 = (s2a.X - s1a.X) * ux + (s2a.Y - s1a.Y) * uy;
        var t1 = (s2b.Y - s1a.Y) * uy + (s2b.X - s1a.X) * ux;
        if (t0 <= 0f)
            t0 = 0f;
        if (t1 <= 0f)
            t1 = 0f;
        if (len <= t0)
            t0 = len;
        if (len <= t1)
            t1 = len;
        t0 *= inv;
        t1 *= inv;
        var (lo, hi) = t1 < t0 ? (t1, t0) : (t0, t1);
        var near = new F3((s1b.X - s1a.X) * lo + s1a.X, (s1b.Y - s1a.Y) * lo + s1a.Y, (s1b.Z - s1a.Z) * lo + s1a.Z);
        var far = new F3((s1b.X - s1a.X) * hi + s1a.X, (s1b.Y - s1a.Y) * hi + s1a.Y, (s1b.Z - s1a.Z) * hi + s1a.Z);
        var gx = far.X - near.X;
        var gy = far.Y - near.Y;
        return (far, near, MathF.Sqrt(gy * gy + gx * gx));
    }

    private static F3 Corner(NavWriter.Mesh mesh, NavWriter.Area a, int k)
    {
        var (x, y, z) = mesh.Corners[mesh.Polygons[a.Polygon][k]];
        return new F3(x, y, z);
    }
}
