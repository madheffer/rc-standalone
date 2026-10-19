namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// The hull capsule AddJumps casts (vphysics2's physics interface vfn 0x1e8, 180071d70): from
/// (0, 0, <see cref="Z0"/>) to (0, 0, <see cref="Z1"/>), radius <see cref="Radius"/>.
/// </summary>
public readonly record struct NavJumpCapsule(float Z0, float Z1, float Radius);

/// <summary>
/// The physics world AddJumps casts through (180072ef0: world vfn 0x580, a type-4 shape cast with
/// identity rotation): whether the capsule moved from <c>from</c> to <c>to</c> hits something before
/// the end (result fraction below 1).
/// </summary>
public interface INavJumpWorld
{
    bool CastBlocked(NavJumpCapsule capsule, (float X, float Y, float Z) from, (float X, float Y, float Z) to);
}

public sealed partial class NavGenPolys
{
    /// <summary>The convars AddJumps reads (csgo/gameinfo.gi sets the ratio; ledger 94).</summary>
    public sealed record JumpSettings(double MinOverlapRatio = 0.1);

    /// <summary>
    /// AddJumps (026, 18006b160) for one hull. The polygons go into a 32-unit grid over their bounds
    /// (as IslandRemoval's). Candidate edges (18006fcf0): every edge of a polygon with a connection
    /// block whose connections' overlaps sum to at most 0, or leave at least twice the radius open.
    /// For each, in order: the edge's outward normal u x n (18004f120), the polygons whose grid cells
    /// meet the box spanned by the edge and the edge moved out by MaxJumpHorizDistBase (18006cf00),
    /// and against every edge of each other polygon (18006bfc0): the edge pair (180053170, overlap
    /// radius times the ratio) kept when it overlaps, drops by more than MaxClimb + 4 and at most
    /// MaxJumpDownDist (a drop onto a polygon of greater kind is skipped), the midpoints lie within
    /// MaxJumpHorizDistBase in XY, the capsule of the source polygon's kind gets through
    /// (180072ef0) and no existing connection on the edge covers the pair's midpoint. The pairs are
    /// sorted by squared midpoint distance (MSVC std::sort, 180068080 with 180073a20); each pair
    /// not marked is added (18004dd20: this side as found, kind 0; the other side's pair with
    /// overlap 1, kind 0 when the drop is shorter than max(MaxClimb, MaxJumpUpDist), else 1; each
    /// only when the polygon has no connection to the other yet), then later pairs whose midpoint
    /// falls within either side's span on the same edge are marked.
    /// </summary>
    public void AddJumps(NavHull hull, INavJumpWorld world, JumpSettings settings)
    {
        float radius = hull.Radius, climb = hull.MaxClimb, jumpDown = hull.MaxJumpDownDist;
        float horiz = hull.MaxJumpHorizDistBase, jumpUp = hull.MaxJumpUpDist;
        var (lo, hi) = ComputeBounds();
        var grid = new IslandGrid(lo.X, lo.Y, hi.X, hi.Y, IslandGridCell);
        for (var i = 0; i < Polys.Count; i++)
        {
            var b = Polys[i].Bounds!;
            grid.Insert(b[0], b[2], b[1], b[3], i);
        }
        var standing = new NavJumpCapsule(climb, hull.Height * 0.5f + climb, radius);
        var shortCapsule = new NavJumpCapsule(climb, hull.ShortHeight * 0.5f + climb, radius);
        var crawl = new NavJumpCapsule(climb, hull.CrawlHeight * 0.5f + climb, radius);

        var pairs = new List<NavConnection>();
        foreach (var (pi, e) in JumpEdges(radius))
        {
            var p = Polys[pi];
            var n = EdgeOutward(p, e);
            var targets = JumpTargets(grid, Corner(p, e), Corner(p, e + 1), n, horiz);
            var capsule = p.Kind == 1 ? shortCapsule : p.Kind == 2 ? crawl : standing;
            JumpPairs(pairs, targets, pi, e, capsule, world, radius * settings.MinOverlapRatio, climb, jumpDown, horiz);
        }
        if (pairs.Count == 0)
            return;
        var sorted = pairs.ToArray();
        MsvcSort.Sort(sorted, static (a, b) =>
        {
            double ay = a.MidA.Y - a.MidB.Y, by = b.MidA.Y - b.MidB.Y;
            double ax = a.MidA.X - a.MidB.X, bx = b.MidA.X - b.MidB.X;
            double az = a.MidA.Z - a.MidB.Z, bz = b.MidA.Z - b.MidB.Z;
            return ay * ay + ax * ax + az * az < by * by + bx * bx + bz * bz;
        });
        var threshold = climb <= jumpUp ? jumpUp : climb;
        for (var i = 0; i < sorted.Length; i++)
        {
            var c = sorted[i];
            if (c.Flag61 != 0)
                continue;
            double dx = c.MidA.X - c.MidB.X, dy = c.MidA.Y - c.MidB.Y, dz = c.MidA.Z - c.MidB.Z;
            var dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            AddJump(c, dist < threshold);
            for (var j = i + 1; j < sorted.Length; j++)
            {
                var o = sorted[j];
                var mid = o.OverlapA * 0.5 + o.StartA;
                if (o.PolyA == c.PolyA && o.EdgeA == c.EdgeA && c.StartA <= mid && mid <= c.StartA + c.OverlapA)
                    o.Flag61 = 1;
                if (o.PolyA == c.PolyB && o.EdgeA == c.EdgeB && c.StartB <= mid && mid <= c.StartB + c.OverlapB)
                    o.Flag61 = 1;
            }
        }
    }

    // 18006fcf0: (polygon, edge) pairs open enough for a jump, polygon by polygon, edge by edge.
    private IEnumerable<(int Poly, int Edge)> JumpEdges(float radius)
    {
        var sums = new double[5];
        for (var pi = 0; pi < Polys.Count; pi++)
        {
            var p = Polys[pi];
            if (p.Links is not { } links)
                continue;
            Array.Clear(sums);
            foreach (var c in links.Connections)
                sums[c.EdgeA] = c.OverlapA + sums[c.EdgeA];
            for (var e = 0; e < p.Indices.Count; e++)
            {
                // 18004e9e0: |v[e] - v[e+1]|.
                var a = Corner(p, e);
                var b = Corner(p, e + 1);
                double z = a.Z - b.Z, y = a.Y - b.Y, x = a.X - b.X;
                var len = Math.Sqrt(y * y + x * x + z * z);
                if (sums[e] <= 0.0 || (double)radius + radius <= len - sums[e])
                    yield return (pi, e);
            }
        }
    }

    // 18004f120: the edge's unit direction u (left as is at or below FLT_EPSILON), then u x n.
    private (double X, double Y, double Z) EdgeOutward(NavPoly p, int e)
    {
        var a = Corner(p, e);
        var b = Corner(p, e + 1);
        double dz = b.Z - a.Z, dx = b.X - a.X, dy = b.Y - a.Y;
        var len = Math.Sqrt(dy * dy + dx * dx + dz * dz);
        if (1.1920928955078125e-07 < len)
        {
            var inv = 1.0 / len;
            dx *= inv;
            dy *= inv;
            dz *= inv;
        }
        return (p.Nz * dy - p.Ny * dz, p.Nx * dz - p.Nz * dx, p.Ny * dx - p.Nx * dy);
    }

    // 18006cf00: the polygons in grid query order (each once) over the XY box of the edge and the
    // edge moved by n times the distance.
    private static List<int> JumpTargets(IslandGrid grid, (double X, double Y, double Z) cur, (double X, double Y, double Z) next,
                                         (double X, double Y, double Z) n, double dist)
    {
        const double Max = 3.4028234663852886e+38;
        double ox0 = n.X * dist + cur.X, ox1 = n.X * dist + next.X;
        double oy0 = n.Y * dist + cur.Y, oy1 = n.Y * dist + next.Y;
        static double Min(double m, double v) => v <= m ? v : m;
        static double Mx(double m, double v) => m <= v ? v : m;
        var minX = Min(Min(Min(Min(Max, cur.X), next.X), ox0), ox1);
        var maxX = Mx(Mx(Mx(Mx(-Max, cur.X), next.X), ox0), ox1);
        var minY = Min(Min(Min(Min(Max, cur.Y), next.Y), oy0), oy1);
        var maxY = Mx(Mx(Mx(Mx(-Max, cur.Y), next.Y), oy0), oy1);
        return [.. grid.Query(minX, minY, maxX, maxY)];
    }

    // 18006bfc0.
    private void JumpPairs(List<NavConnection> pairs, List<int> targets, int pi, int e, NavJumpCapsule capsule, INavJumpWorld world,
                           double overlap, float climb, float jumpDown, float horiz)
    {
        foreach (var pj in targets)
        {
            if (pj == pi)
                continue;
            var q = Polys[pj];
            // +0x61 of the pair on the stack: cleared per target polygon, never by 180053170, so a
            // covered midpoint keeps the target's later edges out too.
            var covered = false;
            for (var k = 0; k < q.Indices.Count; k++)
            {
                var c = Connect(pi, e, pj, k, overlap);
                if (c.OverlapA == 0.0)
                    continue;
                var dz = c.MidB.Z - c.MidA.Z;
                if (!(dz <= 0.0) || !(dz <= -((double)climb + 4.0)))
                    continue;
                if (dz < 0.0)
                {
                    var ka = Polys[pi].Kind;
                    if ((q.Kind < ka ? ka : q.Kind) != ka)
                        continue;
                }
                if (!(-jumpDown <= dz))
                    continue;
                double ex = c.MidB.Y - c.MidA.Y, ey = c.MidB.X - c.MidA.X;
                if (!(Math.Sqrt(ex * ex + ey * ey + 0.0) <= horiz))
                    continue;
                if (JumpBlocked(c, capsule, world))
                    continue;
                var mid = c.OverlapA * 0.5 + c.StartA;
                if (Polys[pi].Links is { } links)
                    foreach (var o in links.Connections)
                        if (o.PolyA == pi && o.EdgeA == e && o.StartA <= mid && mid <= o.StartA + o.OverlapA)
                            covered = true;
                if (!covered)
                    pairs.Add(c);
            }
        }
    }

    // 180072ef0: the overlap segments' ends (18004d540), the path (180077e40, t 0.5, floats): a's
    // midpoint, above b's midpoint at a's height, b's midpoint; each leg cast both ways.
    private bool JumpBlocked(NavConnection c, NavJumpCapsule capsule, INavJumpWorld world)
    {
        var (a0, a1) = Segment(Polys[c.PolyA], c.EdgeA, c.StartA, c.OverlapA);
        var (b0, b1) = Segment(Polys[c.PolyB], c.EdgeB, c.StartB, c.OverlapB);
        static (float, float, float) F((double X, double Y, double Z) v) => ((float)v.X, (float)v.Y, (float)v.Z);
        var (p1, p2, p3, p4) = (F(a0), F(a1), F(b1), F(b0));
        const float t = 0.5f, s = 1f - t;
        var z = s * p1.Item3 + t * p2.Item3;
        var x = s * p3.Item1 + t * p4.Item1;
        var y = s * p3.Item2 + t * p4.Item2;
        var start = (s * p1.Item1 + t * p2.Item1, s * p1.Item2 + t * p2.Item2, z);
        var above = (x, y, z);
        var end = (x, y, s * p3.Item3 + t * p4.Item3);
        foreach (var (from, to) in new[] { (start, above), (above, end) })
            if (world.CastBlocked(capsule, from, to) || world.CastBlocked(capsule, to, from))
                return true;
        return false;
    }

    // 18004d540: the edge's points at start and start + overlap along its unit direction.
    private ((double X, double Y, double Z), (double X, double Y, double Z)) Segment(NavPoly p, int e, double start, double overlap)
    {
        var a = Corner(p, e);
        var b = Corner(p, e + 1);
        double dz = b.Z - a.Z, dx = b.X - a.X, dy = b.Y - a.Y;
        var len = Math.Sqrt(dy * dy + dx * dx + dz * dz);
        if (1.1920928955078125e-07 < len)
        {
            var inv = 1.0 / len;
            dx *= inv;
            dy *= inv;
            dz *= inv;
        }
        var end = overlap + start;
        return ((start * dx + a.X, start * dy + a.Y, dz * start + a.Z), (end * dx + a.X, end * dy + a.Y, end * dz + a.Z));
    }

    // 18004dd20: this side unless the polygon already connects to the other, then the other side's
    // pair (overlap 1) unless that polygon already connects back.
    private void AddJump(NavConnection c, bool near)
    {
        var a = Polys[c.PolyA];
        a.Links ??= new NavLinks();
        if (!a.Links.Connections.Any(o => o.PolyB == c.PolyB))
            a.Links.Add(c);
        var back = Connect(c.PolyB, c.EdgeB, c.PolyA, c.EdgeA, 1.0, (byte)(near ? 0 : 1));
        var b = Polys[c.PolyB];
        b.Links ??= new NavLinks();
        if (!b.Links.Connections.Any(o => o.PolyB == c.PolyA))
            b.Links.Add(back);
    }
}
