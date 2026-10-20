namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// CS2's nav analysis: the .nav's custom data (hiding spots, encounter spots, approach areas, earliest
/// occupy times), computed by server.dll's CSNavMesh::Analyze (vfn 0x60, 18039a7c0) after the build
/// (ledger 103). The mesh as the analysis sees it: each area of <see cref="NavWriter.Mesh"/> with the
/// centre, bounds and grid server.dll 20261015 derives from its polygon; the .nav's corner pool,
/// polygons and area order are the in-memory mesh's.
/// </summary>
public sealed partial class NavAnalysis
{
    public readonly struct V(float x, float y, float z)
    {
        public readonly float X = x, Y = y, Z = z;
        public override string ToString() => $"({X:R}, {Y:R}, {Z:R})";
    }

    /// <summary>A CCSNavArea: polygon index (+8), centre (+0xc), bounds (+0x24 / +0x30), id (+0x60).</summary>
    public sealed class Area
    {
        public int Index;
        public uint Id;
        public int Polygon;
        public V[] Corners = [];
        public V Center, Min, Max;
        public ulong Attributes;
        public uint Flags;
        public byte Hull;
        public byte Blocked;
        public List<Link>[] Links = [];
    }

    /// <summary>Per edge: the connected area and the edge on it that comes back (the connection block's entries in order).</summary>
    public sealed class Link(Area to, int backEdge)
    {
        public readonly Area To = to;
        public readonly int BackEdge = backEdge;
    }

    /// <summary>The grid (CSNavMesh +0xb8): cell size +0xc8, cells +0xcc x +0xd0 from +0xd4 / +0xd8.</summary>
    public const float CellSize = 300f;

    public readonly List<Area> Areas = [];
    public int GridX, GridY;
    public float OriginX, OriginY;

    /// <summary>Per hull (area +0x58), each cell's areas in build order.</summary>
    private readonly Dictionary<int, List<Area>[]> _grids = [];

    public NavAnalysis(NavWriter.Mesh mesh)
    {
        foreach (var a in mesh.Areas)
        {
            var poly = mesh.Polygons[a.Polygon];
            var area = new Area
            {
                Index = Areas.Count, Id = a.Id, Polygon = a.Polygon, Attributes = a.Attributes, Hull = 0,
                Corners = [.. poly.Select(k => new V(mesh.Corners[k].X, mesh.Corners[k].Y, mesh.Corners[k].Z))]
            };
            area.Center = Centroid(area.Corners);
            (area.Min, area.Max) = Bounds(area.Corners);
            Areas.Add(area);
        }
        var byId = Areas.ToDictionary(a => a.Id);
        for (var i = 0; i < Areas.Count; i++)
            Areas[i].Links = [.. mesh.Areas[i].Connections.Select(list => list.Where(c => byId.ContainsKey(c.Area)).Select(c => new Link(byId[c.Area], c.Edge)).ToList())];
        BuildGrid();
    }

    /// <summary>1814c15d0: the triangle fan (v0, v[i], v[i-1]), i from n-1 down to 2, weighted by area; v0 when it is below 1e-6.</summary>
    public static V Centroid(V[] c)
    {
        var n = c.Length;
        float x0 = c[0].X, y0 = c[0].Y, z0 = c[0].Z;
        float cx = 0f, cy = 0f, cz = 0f, total = 0f;
        var i = n - 1;
        if (i != 1)
        {
            while (true)
            {
                var a = c[i];
                var b = c[i - 1];
                var f11 = b.Z - z0;
                var f14 = (a.Y - y0) * (b.X - x0);
                var f16 = (a.Y - y0) * f11 - (a.Z - z0) * (b.Y - y0);
                var f17 = (a.X - x0) * (b.Y - y0) - f14;
                var f15 = (a.Z - z0) * (b.X - x0) - (a.X - x0) * f11;
                f15 *= f15;
                var len = MathF.Sqrt(f17 * f17 + f15 + f16 * f16) * 0.5f;
                total += len;
                cx = (x0 + a.X + b.X) * 0.33333334f * len + cx;
                cy = (y0 + a.Y + b.Y) * 0.33333334f * len + cy;
                cz = (z0 + a.Z + b.Z) * 0.33333334f * len + cz;
                i--;
                if (i == 1)
                    break;
            }
            if (1e-06f < total)
                return new V(cx / total, cy / total, cz / total);
        }
        return c[0];
    }

    /// <summary>1814c1a80: the corners' least and greatest coordinates.</summary>
    public static (V Min, V Max) Bounds(V[] c)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = -float.MaxValue, maxY = -float.MaxValue, maxZ = -float.MaxValue;
        foreach (var v in c)
        {
            if (v.X < minX)
                minX = v.X;
            if (maxX < v.X)
                maxX = v.X;
            if (v.Y < minY)
                minY = v.Y;
            if (maxY < v.Y)
                maxY = v.Y;
            if (v.Z < minZ)
                minZ = v.Z;
            if (maxZ < v.Z)
                maxZ = v.Z;
        }
        return (new V(minX, minY, minZ), new V(maxX, maxY, maxZ));
    }

    // ---- the grid ----

    /// <summary>
    /// 1814ac9c0 then 1814ab950 per area (mesh vfn 0xb8): the origin is the least area bound, the
    /// cell count (int)((max - min) / 300) + 1; an area goes into every cell its polygon overlaps
    /// (1814c5510), the edge cells reaching to infinity.
    /// </summary>
    private void BuildGrid()
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = -float.MaxValue, maxY = -float.MaxValue;
        foreach (var a in Areas)
        {
            if (a.Min.X <= minX)
                minX = a.Min.X;
            if (maxY <= a.Max.Y)
                maxY = a.Max.Y;
            if (a.Min.Y <= minY)
                minY = a.Min.Y;
            if (maxX <= a.Max.X)
                maxX = a.Max.X;
        }
        OriginX = minX;
        OriginY = minY;
        GridX = (int)((maxX - minX) / CellSize) + 1;
        GridY = (int)((maxY - minY) / CellSize) + 1;
        foreach (var a in Areas)
        {
            if (!_grids.TryGetValue(a.Hull, out var cells))
            {
                cells = new List<Area>[GridX * GridY];
                for (var k = 0; k < cells.Length; k++)
                    cells[k] = [];
                _grids[a.Hull] = cells;
            }
            var lastX = GridX - 1;
            var lastY = GridY - 1;
            var x0 = Clamp((int)((a.Min.X - OriginX) / CellSize), lastX);
            var y0 = Clamp((int)((a.Min.Y - OriginY) / CellSize), lastY);
            var x1 = Clamp((int)((a.Max.X - OriginX) / CellSize), lastX);
            var y1 = Clamp((int)((a.Max.Y - OriginY) / CellSize), lastY);
            for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var (cmin, cmax) = CellBox(x, y);
                if (OverlapsRect(a, cmin.X, cmin.Y, cmax.X, cmax.Y))
                    cells[y * GridX + x].Add(a);
            }
        }
    }

    private static int Clamp(int v, int last)
    {
        if (v < 1)
            v = 0;
        return v < last ? v : last;
    }

    /// <summary>A cell's box; the first and last cells reach to infinity on their outer side.</summary>
    private (V Min, V Max) CellBox(int x, int y)
    {
        var lastX = GridX - 1;
        var lastY = GridY - 1;
        var cx = Clamp(x, lastX);
        var cy = Clamp(y, lastY);
        var minX = cx * CellSize + OriginX;
        var maxX = CellSize + minX;
        var minY = cy * CellSize + OriginY;
        var maxY = CellSize + minY;
        if (cx == 0)
            minX = -float.MaxValue;
        else if (cx == lastX)
            maxX = float.MaxValue;
        if (cy == 0)
            minY = -float.MaxValue;
        else if (cy == lastY)
            maxY = float.MaxValue;
        return (new V(minX, minY, 0f), new V(maxX, maxY, 0f));
    }

    /// <summary>1814c5510: the area's bounds, then each fan triangle (v0, v[k-1], v[k]) against the rectangle (separating axes).</summary>
    private static bool OverlapsRect(Area a, float minX, float minY, float maxX, float maxY)
    {
        if (!(minX <= a.Max.X && a.Min.X <= maxX && minY <= a.Max.Y && a.Min.Y <= maxY))
            return false;
        var c = a.Corners;
        var A = c[0];
        for (var k = c.Length - 1; 1 < k; k--)
        {
            var B = c[k - 1];
            var C = c[k];
            var lo = C.X;
            if (B.X <= C.X)
                lo = B.X;
            var lo2 = A.X;
            if (lo <= A.X)
                lo2 = lo;
            if (!(lo2 <= maxX))
                continue;
            var hi = C.X;
            if (C.X <= B.X)
                hi = B.X;
            var hi2 = A.X;
            if (A.X <= hi)
                hi2 = hi;
            if (!(minX <= hi2))
                continue;
            var loy = C.Y;
            if (B.Y <= C.Y)
                loy = B.Y;
            var loy2 = A.Y;
            if (loy <= A.Y)
                loy2 = loy;
            if (!(loy2 <= maxY))
                continue;
            var hiy = C.Y;
            if (C.Y <= B.Y)
                hiy = B.Y;
            var hiy2 = A.Y;
            if (A.Y <= hiy)
                hiy2 = hiy;
            if (!(minY <= hiy2))
                continue;
            var f14 = C.X - A.X;
            var f15 = -(B.Y - C.Y);
            var f18 = A.X - B.X;
            var f13 = -(C.Y - A.Y);
            var f17 = B.X - C.X;
            var f16 = A.Y - B.Y;
            var f10 = -f16;
            if ((B.X - A.X) * f13 + f14 * (B.Y - A.Y) < 0f)
            {
                f13 = -f13;
                f14 = -f14;
                f15 = -f15;
                f17 = -f17;
                f18 = -f18;
                f10 = f16;
            }
            var ry = 0f <= f14 ? maxY : minY;
            var rx = 0f <= f13 ? maxX : minX;
            if (!(0f <= (ry - A.Y) * f14 + (rx - A.X) * f13))
                continue;
            ry = 0f <= f17 ? maxY : minY;
            rx = 0f <= f15 ? maxX : minX;
            if (!(0f <= (ry - C.Y) * f17 + (rx - C.X) * f15))
                continue;
            ry = f18 < 0f ? minY : maxY;
            rx = 0f <= f10 ? maxX : minX;
            if (0f <= (ry - B.Y) * f18 + (rx - B.X) * f10)
                return true;
        }
        return false;
    }

    // ---- distances ----

    /// <summary>181467040: the squared distance from p to the box.</summary>
    private static float BoxDistSq(V min, V max, V p)
    {
        var f1 = min.X - p.X;
        var f4 = min.Y - p.Y;
        if (f1 <= 0f)
            f1 = 0f;
        var f2 = min.Z - p.Z;
        var f5 = p.Z - max.Z;
        if (f4 <= 0f)
            f4 = 0f;
        var f6 = p.X - max.X;
        if (f2 <= 0f)
            f2 = 0f;
        if (f5 <= 0f)
            f5 = 0f;
        if (f6 <= 0f)
            f6 = 0f;
        var f3 = p.Y - max.Y;
        if (f3 <= 0f)
            f3 = 0f;
        return (f4 + f3) * (f4 + f3) + (f2 + f5) * (f2 + f5) + (f6 + f1) * (f6 + f1);
    }

    /// <summary>181466fc0: the squared distance in XY from p to the rectangle.</summary>
    private static float RectDistSq(float minX, float minY, float maxX, float maxY, V p)
    {
        var f1 = p.X - maxX;
        var f4 = minX - p.X;
        if (f1 <= 0f)
            f1 = 0f;
        if (f4 <= 0f)
            f4 = 0f;
        var f3 = p.Y - maxY;
        var f2 = minY - p.Y;
        if (f3 <= 0f)
            f3 = 0f;
        if (f2 <= 0f)
            f2 = 0f;
        return (f4 + f1) * (f4 + f1) + (f3 + f2) * (f3 + f2);
    }

    /// <summary>1814762f0: the closest point of triangle abc to p (Ericson).</summary>
    public static V ClosestOnTriangle(V p, V a, V b, V c)
    {
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var abz = b.Z - a.Z;
        var acx = c.X - a.X;
        var acy = c.Y - a.Y;
        var acz = c.Z - a.Z;
        var apy = p.Y - a.Y;
        var d1 = apy * aby + (p.Z - a.Z) * abz + (p.X - a.X) * abx;
        var d2 = (p.Z - a.Z) * acz + apy * acy + (p.X - a.X) * acx;
        if (!(0f < d1 || 0f < d2))
            return a;
        var bpx = p.X - b.X;
        var bpy = p.Y - b.Y;
        var d3 = (p.Z - b.Z) * abz + bpy * aby + bpx * abx;
        var d4 = (p.Z - b.Z) * acz + bpy * acy + bpx * acx;
        if (!(d3 < 0f || d3 < d4))
            return b;
        var vc = d4 * d1 - d3 * d2;
        if (!(0f < vc || d1 < 0f || 0f < d3 || (d1 == 0f && d3 == 0f)))
        {
            var v0 = d1 / (d1 - d3);
            return new V(abx * v0 + a.X, aby * v0 + a.Y, abz * v0 + a.Z);
        }
        var cpx = p.X - c.X;
        var cpy = p.Y - c.Y;
        var d5 = (p.Z - c.Z) * abz + cpy * aby + cpx * abx;
        var d6 = (p.Z - c.Z) * acz + cpy * acy + cpx * acx;
        if (0f <= d6 && d5 <= d6)
            return c;
        var vb = d5 * d2 - d6 * d1;
        if (vb <= 0f && 0f <= d2 && d6 <= 0f)
        {
            var w0 = d2 / (d2 - d6);
            return new V(w0 * acx + a.X, w0 * acy + a.Y, w0 * acz + a.Z);
        }
        var va = d6 * d3 - d5 * d4;
        var t1 = d4 - d3;
        if (va <= 0f && 0f <= t1 && 0f <= d5 - d6)
        {
            var w1 = t1 / (d5 - d6 + t1);
            return new V((c.X - b.X) * w1 + b.X, (c.Y - b.Y) * w1 + b.Y, (c.Z - b.Z) * w1 + b.Z);
        }
        var denom = 1f / (va + vb + vc);
        var v = denom * vc;
        var w = denom * vb;
        return new V(abx * w + a.X + acx * v, aby * w + a.Y + acy * v, abz * w + a.Z + acz * v);
    }

    /// <summary>1814c2ee0 (through 1802c33d0): the closest point of the area, over its fan triangles (v0, v[i], v[i-1]).</summary>
    public static V ClosestOnArea(Area a, V p)
    {
        var c = a.Corners;
        var best = new V(0f, 0f, 0f);
        var bestD = float.MaxValue;
        for (var i = c.Length - 1; 1 < i; i--)
        {
            var q = ClosestOnTriangle(p, c[0], c[i], c[i - 1]);
            var dx = p.X - q.X;
            var dy = p.Y - q.Y;
            var dz = p.Z - q.Z;
            var d = dy * dy + dx * dx + dz * dz;
            if (d < bestD)
            {
                best = q;
                bestD = d;
            }
        }
        return best;
    }

    /// <summary>1814c3010: the squared distance to the area when below <paramref name="limit"/>, else -1.</summary>
    private static float AreaDistSq(Area a, V p, float limit, out V closest)
    {
        closest = default;
        if (!(BoxDistSq(a.Min, a.Max, p) < limit))
            return -1f;
        var q = ClosestOnArea(a, p);
        var d = (p.Y - q.Y) * (p.Y - q.Y) + (p.X - q.X) * (p.X - q.X) + (p.Z - q.Z) * (p.Z - q.Z);
        if (!(d < limit))
            return -1f;
        closest = q;
        return d;
    }

    // ---- the nearest area ----

    /// <summary>The hull record 1802b11b0 fills (0x48 bytes) that the nearest-area search reads.</summary>
    public sealed record NearestQuery(float Above = 80f, float Below = 18f, float Horizontal = 16f, float Fuzz = 0f,
                                      ulong RequireMask = ulong.MaxValue, ulong ExcludeMask = 0x20000, bool StopAtFirstRing = true);

    /// <summary>
    /// 1814b3a10 with no flags: the grid rings out from the point's cell to ceil(max distance / 300),
    /// cells farther than the best so far skipped; per area (each once): not flagged +0x48 bit 3, not
    /// blocked (+0x6c), the closest point within the best distance and at most <c>Below</c> above
    /// the point (every area counts as flat: the corner records default to flag 1), no excluded
    /// attribute; the nearest wins, ties to the first seen. With StopAtFirstRing the search ends one
    /// ring past the first hit.
    /// </summary>
    public Area? NearestArea(V pos, float maxDistance, NearestQuery q, int hull = 0)
    {
        if (!_grids.TryGetValue(hull, out var cells))
            return null;
        if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z))
            return null;
        var best = maxDistance * maxDistance;
        var limit = maxDistance * maxDistance;
        var lastX = GridX - 1;
        var lastY = GridY - 1;
        var cx = Clamp((int)((pos.X - OriginX) / CellSize), lastX);
        var cy = Clamp((int)((pos.Y - OriginY) / CellSize), lastY);
        var maxRing = (int)MathF.Ceiling(maxDistance / CellSize);
        Area? found = null;
        var seen = new HashSet<Area>();
        for (var ring = 0; ring <= maxRing; ring++)
        {
            int x0 = cx - ring, x1 = cx + ring, y0 = cy - ring, y1 = cy + ring;
            var xe = x1 <= lastX ? x1 : lastX;
            var ye = y1 <= lastY ? y1 : lastY;
            for (var x = x0 < 0 ? 0 : x0; x <= xe; x++)
            for (var y = y0 < 0 ? 0 : y0; y <= ye; y++)
            {
                if (!(x <= x0 || x1 <= x || y <= y0 || y1 <= y))
                    continue;
                var (cmin, cmax) = CellBox(x, y);
                var cd = RectDistSq(cmin.X, cmin.Y, cmax.X, cmax.Y, pos);
                if (best < cd || limit < cd)
                    continue;
                foreach (var a in cells[Clamp(y, lastY) * GridX + Clamp(x, lastX)])
                {
                    if ((a.Flags & 8) != 0 || a.Blocked != 0)
                        continue;
                    if (!seen.Add(a))
                        continue;
                    var d = AreaDistSq(a, pos, best, out var close);
                    if (d < 0f)
                        continue;
                    if (!(close.Z <= q.Below + pos.Z))
                        continue;
                    if (q.RequireMask != ulong.MaxValue && (a.Attributes & q.RequireMask) == 0)
                        continue;
                    if (q.ExcludeMask != 0 && (a.Attributes & q.ExcludeMask) != 0)
                        continue;
                    found = a;
                    best = d;
                    if (q.StopAtFirstRing)
                        maxRing = ring + 1 < maxRing ? ring + 1 : maxRing;
                }
            }
        }
        return found;
    }

    // ---- paths ----

    /// <summary>
    /// 1814eabb0: the stretch of edge a-b that edge c-d covers once both are projected on a-b in XY;
    /// <paramref name="hi"/> is the far end, <paramref name="lo"/> the near one.
    /// </summary>
    private static void EdgeOverlap(V a, V b, V c, V d, out V lo, out V hi)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var inv = 0f;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len <= 1.1920929e-07f)
        {
            dx = 1f;
            dy = 0f;
        }
        else
        {
            inv = 1f / len;
            dx *= inv;
            dy *= inv;
        }
        var tc = (c.X - a.X) * dx + (c.Y - a.Y) * dy;
        var td = (d.Y - a.Y) * dy + (d.X - a.X) * dx;
        if (tc <= 0f)
            tc = 0f;
        if (td <= 0f)
            td = 0f;
        if (len <= tc)
            tc = len;
        if (len <= td)
            td = len;
        tc *= inv;
        td *= inv;
        var h = td;
        var l = tc;
        if (td < tc)
        {
            h = tc;
            l = td;
        }
        lo = new V((b.X - a.X) * l + a.X, (b.Y - a.Y) * l + a.Y, (b.Z - a.Z) * l + a.Z);
        hi = new V((b.X - a.X) * h + a.X, (b.Y - a.Y) * h + a.Y, (b.Z - a.Z) * h + a.Z);
    }

    /// <summary>1814c76a0 (walk links): the portal between the neighbour's back edge and the current area's edge.</summary>
    private static (V P0, V P1) Portal(Area to, Area from, int backEdge, int edge)
    {
        if (backEdge == 8)
            throw new NotSupportedException("nav portal of kind 8 (1814e5480) is not ported");
        if (((to.Attributes >> 15) & 1) != 0 || ((from.Attributes >> 15) & 1) != 0)
            throw new NotSupportedException("nav portal across a 0x8000 area (1814eada0) is not ported");
        var e1 = backEdge != 7 ? backEdge : 0;
        var e2 = edge != 7 ? edge : 2;
        var tc = to.Corners;
        var fc = from.Corners;
        EdgeOverlap(tc[e1], tc[(e1 + 1) % tc.Length], fc[e2], fc[(e2 + 1) % fc.Length], out var lo, out var hi);
        return (hi, lo);
    }

    /// <summary>181465740: the closest point of segment a-b to p.</summary>
    private static V ClosestOnSegment(V p, V a, V b)
    {
        var dy = b.Y - a.Y;
        var dz = b.Z - a.Z;
        var dx = b.X - a.X;
        var l2 = dz * dz + dy * dy + dx * dx;
        var t = 0f;
        if (1e-05f <= l2)
            t = (dz * p.Z + dy * p.Y + dx * p.X - (dy * a.Y + dz * a.Z + dx * a.X)) / l2;
        var f = 0f;
        if (0f <= t)
        {
            f = 1f;
            if (t <= 1f)
                f = t;
        }
        return new V(f * dx + a.X, dy * f + a.Y, dz * f + a.Z);
    }

    /// <summary>
    /// 1814e8f50: where the line from <paramref name="from"/> toward <paramref name="target"/> crosses the
    /// portal in XY (1814824a0), kept 0.25 / max(2.5, portal length) from its ends; the closest point
    /// to the target when the lines are parallel, to <paramref name="from"/> when the portal is behind.
    /// </summary>
    private static V Crossing(V p0, V p1, V from, V target)
    {
        float ax = from.X, ay = from.Y, adx = target.X - from.X, ady = target.Y - from.Y;
        float bx = p0.X, by = p0.Y, bdx = p1.X - p0.X, bdy = p1.Y - p0.Y;
        var den = bdy * adx - bdx * ady;
        if (MathF.Abs(den) < 1e-06f)
            return ClosestOnSegment(target, p0, p1);
        var s = ((bx - ax) * bdy + (ay - by) * bdx) / den;
        float t;
        if (MathF.Abs(bdy) < MathF.Abs(bdx))
            t = (s * adx + (ax - bx)) / bdx;
        else
            t = (s * ady + (ay - by)) / bdy;
        if (!(0f <= s))
            return ClosestOnSegment(from, p0, p1);
        var l = MathF.Sqrt((p0.Z - p1.Z) * (p0.Z - p1.Z) + (p0.Y - p1.Y) * (p0.Y - p1.Y) + (p0.X - p1.X) * (p0.X - p1.X));
        var m = 2.5f;
        if (2.5f <= l)
            m = l;
        if (t <= 0f)
            t = 0f;
        m = 0.25f / m;
        if (1f <= t)
            t = 1f;
        if (t <= m)
            t = m;
        if (1f - m <= t)
            t = 1f - m;
        return new V((p1.X - p0.X) * t + p0.X, (p1.Y - p0.Y) * t + p0.Y, (p1.Z - p0.Z) * t + p0.Z);
    }

    private struct HeapEntry
    {
        public Area Area;
        public float F, G, Len;
    }

    /// <summary>The open heap (min on F, 18039a3c0 / 18039a5d0 / 180397ef0) and the closed set (1814d3980 / 1814d3ac0 / 1814d3c70).</summary>
    private sealed class Search
    {
        public readonly List<HeapEntry> Heap = [];
        public readonly Dictionary<Area, int> Open = [];
        public readonly HashSet<Area> Closed = [];

        private void Swap(int i, int j)
        {
            (Heap[i], Heap[j]) = (Heap[j], Heap[i]);
            Open[Heap[i].Area] = i;
            Open[Heap[j].Area] = j;
        }

        public int Down(int i)
        {
            var n = Heap.Count;
            if (n / 2 <= i)
                return i;
            do
            {
                var best = i;
                var l = 2 * i + 1;
                if (l < n && Heap[l].F <= Heap[i].F)
                    best = l;
                var r = 2 * i + 2;
                if (r < n && Heap[r].F <= Heap[best].F)
                    best = r;
                if (best == i)
                    break;
                Swap(i, best);
                i = best;
            } while (i < n / 2);
            return i;
        }

        public void Up(int i)
        {
            while (i != 0)
            {
                var parent = ((i + 1) >> 1) - 1;
                if (Heap[parent].F <= Heap[i].F)
                    return;
                Swap(i, parent);
                i = parent;
            }
        }

        public void Push(Area a, float f, float g, float len)
        {
            if (!Open.TryGetValue(a, out var idx) || idx < 0)
            {
                Heap.Add(new HeapEntry { Area = a, F = f, G = g, Len = len });
                Open[a] = Heap.Count - 1;
                Up(Heap.Count - 1);
                return;
            }
            Heap[idx] = new HeapEntry { Area = a, F = f, G = g, Len = len };
            if (Down(idx) == idx)
                Up(idx);
        }

        public HeapEntry Pop()
        {
            var root = Heap[0];
            Open[root.Area] = -1;
            var n = Heap.Count;
            if (1 < n)
                Heap[0] = Heap[n - 1];
            Heap.RemoveAt(n - 1);
            if (0 < Heap.Count)
                Open[Heap[0].Area] = 0;
            Down(0);
            return root;
        }
    }

    /// <summary>
    /// An area's A* record (0x30 bytes, 1814d7200: indexed by area +0x64): the search position (+0), the
    /// parent (+0x18), how the parent reached it (+0x28: the edge, 9 for a search's start) and the edge
    /// back (+0x2c, 0xff for a start). Every search shares them and leaves them as they are, so a cost
    /// that reads a neighbour's position before the search sets it sees an earlier search's.
    /// </summary>
    public sealed class Record
    {
        public V Pos;
        public Area? Parent;
        public int How;
        public byte BackEdge;
    }

    /// <summary>Each area's record as the searches so far left it (absent: never searched).</summary>
    public readonly Dictionary<Area, Record> Records = [];

    private Record RecordOf(Area a)
    {
        if (!Records.TryGetValue(a, out var r))
            Records[a] = r = new Record();
        return r;
    }

    private static float Dist(V a, V b)
        => MathF.Sqrt((a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z) + (a.X - b.X) * (a.X - b.X));

    /// <summary>A path cost's edge cost (the CNavPathCost family's vfn 0x90).</summary>
    private interface IPathCost
    {
        /// <summary>The cost of the step (negative refuses it); <paramref name="pos"/> is the search's position variable, which the cost may move.</summary>
        float Cost(NavAnalysis nav, Area from, Area to, Link link, int edge, V goalPos, ref V pos);
    }

    /// <summary>
    /// 1814d3e40 (CNavPathCost): the step crosses the shared edge at <see cref="Crossing"/> toward the
    /// goal, the position moves there and the cost is the straight distance (vfn 0x80 returns it).
    /// </summary>
    private sealed class CrossingCost : IPathCost
    {
        public static readonly CrossingCost Instance = new();

        public float Cost(NavAnalysis nav, Area from, Area to, Link link, int edge, V goalPos, ref V pos)
        {
            var curPos = nav.RecordOf(from).Pos;
            var (p0, p1) = Portal(to, from, link.BackEdge, edge);
            var cross = Crossing(p0, p1, curPos, goalPos);
            pos = cross;
            return MathF.Sqrt((curPos.Y - cross.Y) * (curPos.Y - cross.Y) + (curPos.Z - cross.Z) * (curPos.Z - cross.Z)
                              + (curPos.X - cross.X) * (curPos.X - cross.X));
        }
    }

    /// <summary>
    /// 1814d9810, the A*: from <paramref name="start"/> (its record at <paramref name="startPos"/>, how 9)
    /// to <paramref name="goal"/>. Each neighbour of the hull that is not closed (edge by edge, connections
    /// in order: 1814e1950 with flags 1) costs <paramref name="cost"/>; f = g + |pos - goalPos| *
    /// <paramref name="heuristic"/>. A neighbour already open with an f no greater is left alone; else its
    /// record takes the position variable, the parent and the edge, and it goes on the heap. Whether the
    /// goal was reached, and the path length (the sum of |pos - parent's position| along the way).
    /// </summary>
    private (bool Found, float Len) AStar(Area start, Area goal, V startPos, V goalPos, float heuristic, int hull, IPathCost cost)
    {
        var rs = RecordOf(start);
        rs.Parent = null;
        rs.How = 9;
        rs.BackEdge = 0xff;
        if (start == goal)
        {
            rs.Pos = startPos;
            return (true, Dist(startPos, goalPos));
        }
        var pos = startPos;
        var s = new Search();
        rs.Pos = pos;
        s.Push(start, Dist(pos, goalPos) * heuristic + 0f, 0f, 0f);
        while (s.Heap.Count > 0)
        {
            var cur = s.Pop();
            var area = cur.Area;
            s.Closed.Add(area);
            if (area == goal)
                return (true, cur.Len);
            for (var e = 0; e < area.Links.Length; e++)
                foreach (var link in area.Links[e])
                {
                    var n = link.To;
                    if (n == area || s.Closed.Contains(n))
                        continue;
                    if (hull != -1 && n.Hull != hull)
                        continue;
                    var c = cost.Cost(this, area, n, link, e, goalPos, ref pos);
                    if (!(0f <= c))
                        continue;
                    var g = cur.G + c;
                    var curPos = RecordOf(area).Pos;
                    var len = MathF.Sqrt((pos.Y - curPos.Y) * (pos.Y - curPos.Y) + (pos.Z - curPos.Z) * (pos.Z - curPos.Z)
                                         + (pos.X - curPos.X) * (pos.X - curPos.X)) + cur.Len;
                    var f = MathF.Sqrt((pos.Z - goalPos.Z) * (pos.Z - goalPos.Z) + (pos.Y - goalPos.Y) * (pos.Y - goalPos.Y)
                                       + (pos.X - goalPos.X) * (pos.X - goalPos.X)) * heuristic + g;
                    if (s.Open.TryGetValue(n, out var idx) && idx >= 0 && s.Heap[idx].F <= f)
                        continue;
                    var r = RecordOf(n);
                    r.Parent = area;
                    r.How = e;
                    r.BackEdge = (byte)link.BackEdge;
                    r.Pos = pos;
                    s.Push(n, f, g, len);
                }
        }
        return (false, 0f);
    }

    /// <summary>
    /// 1814d96f0 -> 1814d9810 with CNavPathCost (<see cref="CrossingCost"/>): the path length from
    /// <paramref name="start"/> at <paramref name="startPos"/> to <paramref name="goal"/> at
    /// <paramref name="goalPos"/>, or -1 when the goal is not reached.
    /// </summary>
    public float TravelDistance(Area start, Area goal, V startPos, V goalPos, float heuristic, int hull = 0)
    {
        var (found, len) = AStar(start, goal, startPos, goalPos, heuristic, hull, CrossingCost.Instance);
        return found ? len : -1f;
    }

    // ---- earliest occupy times ----

    /// <summary>
    /// 1803935e0 / 180394e20: per area, each team's earliest arrival in seconds (CCSNavArea +0xc8 T,
    /// +0xcc CT): 120 at most; for each spawn of the team, the travel distance from the area nearest
    /// the spawn (at the spawn) to the area nearest the area's centre (at the centre), over 260 units
    /// a second. Both lookups take 10000 units and the hull record (80, 18, 16).
    /// </summary>
    public (float T, float CT)[] EarliestOccupyTimes(IReadOnlyList<V> terroristSpawns, IReadOnlyList<V> ctSpawns, float heuristic = 1f)
    {
        var q = new NearestQuery();
        var times = new (float, float)[Areas.Count];
        for (var i = 0; i < Areas.Count; i++)
        {
            var area = Areas[i];
            var center = area.Center;
            var goal = NearestArea(center, 10000f, q);
            float Team(IReadOnlyList<V> spawns)
            {
                var t = 120f;
                foreach (var spawn in spawns)
                {
                    var start = NearestArea(spawn, 10000f, q);
                    if (start is null || goal is null)
                        continue;
                    var d = TravelDistance(start, goal, spawn, center, heuristic);
                    if (0f <= d && d / 260f < t)
                        t = d / 260f;
                }
                return t;
            }
            times[i] = (Team(terroristSpawns), Team(ctSpawns));
        }
        return times;
    }
}
