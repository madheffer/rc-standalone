namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// Polygon splits that keep connections (navsystem 20261015): the edge-connection transfer, polygon
/// removal, the corner, corner-to-edge and two-edge splits, and the stages built on them
/// (SplitBottleneckPolys, SplitPolysWithMultipleConnections).
/// </summary>
public sealed partial class NavGenPolys
{
    private const double SplitEps = 1.1920928955078125e-05;

    // 18004c650: a new polygon at the end, the creation defaults; the source's +0x62, flags, kind, +0x64.
    private int AddPoly(NavPoly source, List<int> corners)
    {
        Polys.Add(new NavPoly
        {
            Indices = corners, Field62 = source.Field62, Flags = source.Flags, Kind = source.Kind, Field64 = source.Field64,
            Field60 = 5, Field4 = -1,
        });
        return Polys.Count - 1;
    }

    // 18004b990 / 18004ca90 for map input (no vertex attributes): the position onto the pool.
    private int AddVertex((double X, double Y, double Z) p)
    {
        Pool.Add(p);
        return Pool.Count - 1;
    }

    private static bool HasLink(NavLinks? links, int polyB, int edgeB, int edgeA)
    {
        if (links is null)
            return false;
        foreach (var c in links.Connections)
            if (c.PolyB == polyB && c.EdgeB == edgeB && c.EdgeA == edgeA)
                return true;
        return false;
    }

    /// <summary>
    /// 18004db20: unless dst's edge already reaches other's edge, a connection dst-edge -> other-edge
    /// (180053170 at the overlap), kept when the overlap is 0 or both sides are valid.
    /// </summary>
    private void LinkTo(int dst, int dstEdge, int other, int otherEdge, byte kind, double overlap)
    {
        var d = Polys[dst];
        if (HasLink(d.Links, other, otherEdge, dstEdge))
            return;
        d.Links ??= new NavLinks();
        var c = Connect(dst, dstEdge, other, otherEdge, overlap, kind);
        if (overlap == 0.0 || (c.OverlapA != 0.0 && c.OverlapB != 0.0))
            d.Links.Add(c);
    }

    /// <summary>
    /// 18004e040: src's connections on srcEdge, made from dst's dstEdge (18004db20); then every polygon
    /// connecting to src's srcEdge that dst connects back to gets a connection to dst's dstEdge too.
    /// </summary>
    private void TransferEdge(int src, int srcEdge, int dst, int dstEdge, double overlap)
    {
        if (Polys[src].Links is { } s)
            for (var k = 0; k < Polys[src].Links!.Count; k++)
            {
                var c = s.Connections[k];
                if (c.EdgeA == srcEdge)
                    LinkTo(dst, dstEdge, c.PolyB, c.EdgeB, c.Kind, overlap);
            }
        for (var i = 0; i < Polys.Count; i++)
        {
            var links = Polys[i].Links;
            if (links is null)
                continue;
            for (var j = 0; j < links.Count; j++)
            {
                var c = links.Connections[j];
                if (c.PolyB != src || c.EdgeB != srcEdge || Polys[dst].Links is not { } dl)
                    continue;
                if (!HasLink(dl, i, c.EdgeA, dstEdge))
                    continue;
                if (HasLink(links, dst, dstEdge, c.EdgeA))
                    continue;
                var n = Connect(i, c.EdgeA, dst, dstEdge, overlap, c.Kind);
                if (overlap == 0.0 || (n.OverlapA != 0.0 && n.OverlapB != 0.0))
                    links.Add(n);
            }
        }
    }

    // The neighbours' connections back to polygon p's connections, removed (the first on the edge).
    private void Unlink(NavLinks links)
    {
        for (var k = 0; k < links.Count; k++)
        {
            var c = links.Connections[k];
            var other = Polys[c.PolyB].Links!;
            foreach (var j in other.PerEdge[c.EdgeB])
                if (other.Connections[j].PolyB == c.PolyA)
                {
                    other.RemoveAt(j);
                    break;
                }
        }
    }

    /// <summary>
    /// 180050400: polygon p's links undone, the last polygon moved into its place (its connections
    /// rebuilt through 18004e040 when transfer is set, its own links undone), the list one shorter.
    /// </summary>
    private void RemovePoly(int p, bool transfer)
    {
        var last = Polys.Count - 1;
        if (Polys.Count > 1)
        {
            if (Polys[p].Links is { } own)
                Unlink(own);
            Polys[p].Links = null;
            var moved = Polys[last].Clone();
            moved.Links = null;
            var keep = Polys[last];
            Polys[p] = moved;
            if (p == last)
                keep = moved;
            if (transfer)
                for (var e = 0; e < moved.Indices.Count; e++)
                    TransferEdge(last, e, p, e, 0.0);
            if (keep.Links is { } lastLinks)
                Unlink(lastLinks);
            keep.Links = null;
        }
        if (Polys.Count != 0)
            Polys.RemoveAt(last);
    }

    // Entries naming the last polygon now name p (it is about to move there).
    private void FixList(List<int>? list, int p)
    {
        if (list is null)
            return;
        var last = Polys.Count - 1;
        for (var i = 0; i < list.Count; i++)
            if (list[i] == last)
                list[i] = p;
    }

    /// <summary>
    /// 1800521d0: polygon p cut between corners i0 and i1 (not equal or adjacent) into two new polygons,
    /// each edge's connections handed over, the cut edges linked to each other; p removed.
    /// </summary>
    private int SplitCorners(int p, int i0, int i1, List<int>? list)
    {
        var src = Polys[p];
        var n = src.Indices.Count;
        if (n < 3 || i0 == i1 || i0 == (i1 + 1) % n || (i0 + 1) % n == i1)
            return 0;
        List<int> Run(int from, int to)
        {
            var r = new List<int> { src.Indices[from % n] };
            var k = from;
            if (from % n != to)
                do
                {
                    k++;
                    r.Add(src.Indices[k % n]);
                }
                while (k % n != to);
            return r;
        }
        var a = AddPoly(src, Run(i0, i1));
        var pa = Polys[a];
        for (var e = 0; e < pa.Indices.Count - 1; e++)
            TransferEdge(p, (e + i0) % n, a, e, 0.0);
        ComputePlane(pa);
        list?.Add(a);
        var b = AddPoly(src, Run(i1, i0));
        var pb = Polys[b];
        for (var e = 0; e < pb.Indices.Count - 1; e++)
            TransferEdge(p, (e + i1) % n, b, e, 0.0);
        ComputePlane(pb);
        list?.Add(b);
        var ea = Polys[a].Indices.Count - 1;
        var eb = Polys[b].Indices.Count - 1;
        LinkTo(a, ea, b, eb, 0, 0.0);
        LinkTo(b, eb, a, ea, 0, 0.0);
        FixList(list, p);
        RemovePoly(p, true);
        return 2;
    }

    /// <summary>
    /// 180051cb0: polygon p cut from corner c0 to a new vertex at t along edge e (t taken through float),
    /// the pieces' edges on the cut edge handed over at overlap 0.1, the rest at 0; p removed.
    /// </summary>
    private int SplitCornerEdge(int p, int c0, int e, double t, List<int>? list)
    {
        var src = Polys[p];
        var n = src.Indices.Count;
        var ie = e % n;
        if (c0 % n == ie || (c0 - 1 + n) % n == ie)
            return 0;
        var p0 = Pool[src.Indices[ie]];
        var p1 = Pool[src.Indices[(e + 1) % n]];
        var tf = (double)(float)t;
        var v = AddVertex(((p1.X - p0.X) * tf + p0.X, (p1.Y - p0.Y) * tf + p0.Y, (p1.Z - p0.Z) * tf + p0.Z));
        var ca = new List<int> { src.Indices[c0 % n] };
        var k = c0;
        do
        {
            k++;
            ca.Add(src.Indices[k % n]);
        }
        while (k % n != ie);
        ca.Add(v);
        var a = AddPoly(src, ca);
        for (var j = 0; j < Polys[a].Indices.Count - 1; j++)
            TransferEdge(p, (j + c0) % n, a, j, j == Polys[a].Indices.Count - 2 ? 0.1 : 0.0);
        ComputePlane(Polys[a]);
        list?.Add(a);
        var cb = new List<int> { v, src.Indices[(e + 1) % n] };
        k = e + 1;
        if (k % n != c0 % n)
            do
            {
                k++;
                cb.Add(src.Indices[k % n]);
            }
            while (k % n != c0 % n);
        var b = AddPoly(src, cb);
        for (var j = 0; j < Polys[b].Indices.Count - 1; j++)
            TransferEdge(p, (j + e) % n, b, j, j == 0 ? 0.1 : 0.0);
        ComputePlane(Polys[b]);
        list?.Add(b);
        var ea = Polys[a].Indices.Count - 1;
        var eb = Polys[b].Indices.Count - 1;
        LinkTo(a, ea, b, eb, 0, 0.0);
        LinkTo(b, eb, a, ea, 0, 0.0);
        FixList(list, p);
        RemovePoly(p, true);
        return 2;
    }

    /// <summary>
    /// 1800507e0: polygon p cut between the point at t0 on edge e0 and the point at t1 on edge e1 (new
    /// vertices, t through float), refused when either cut lies within 1.2e-5 of an end; the two pieces
    /// take the edges' connections (the cut edges at 1.2e-5, the rest at 0) and link along the cut;
    /// pieces past 4 corners split again (1800521d0 at half); p removed.
    /// </summary>
    private int SplitEdges(int p, int e0, double t0, int e1, double t1, List<int>? list)
    {
        if (t0 < 0.0 || 1.0 < t0 || t1 < 0.0 || 1.0 < t1)
            return 0;
        var src = Polys[p];
        var n = src.Indices.Count;
        if (e0 < 0 || n <= e0 || e1 < 0 || n <= e1)
            return 0;
        var i0n = (e0 + 1) % n;
        var i1n = (e1 + 1) % n;
        var a0 = Pool[src.Indices[e0]];
        var a1 = Pool[src.Indices[i0n]];
        var b0 = Pool[src.Indices[e1]];
        var b1 = Pool[src.Indices[i1n]];
        double ax = a0.X - a1.X, ay = a0.Y - a1.Y, az = a0.Z - a1.Z;
        var len0 = Math.Sqrt(ay * ay + ax * ax + az * az);
        var s0 = 1.0 - t0 <= t0 ? 1.0 - t0 : t0;
        double bx = b0.X - b1.X, by = b0.Y - b1.Y, bz = b0.Z - b1.Z;
        var len1 = Math.Sqrt(by * by + bx * bx + bz * bz);
        var s1 = 1.0 - t1 <= t1 ? 1.0 - t1 : t1;
        var m = len0 * s0;
        if (len1 * s1 <= len0 * s0)
            m = len1 * s1;
        if (m < SplitEps)
            return 0;
        var f0 = (double)(float)t0;
        var q0 = ((a1.X - a0.X) * f0 + a0.X, (a1.Y - a0.Y) * f0 + a0.Y, (a1.Z - a0.Z) * f0 + a0.Z);
        var f1 = (double)(float)t1;
        var q1 = ((b1.X - b0.X) * f1 + b0.X, (b1.Y - b0.Y) * f1 + b0.Y, (b1.Z - b0.Z) * f1 + b0.Z);
        var v0 = AddVertex(q0);
        var v1 = AddVertex(q1);
        var ca = new List<int> { v1, v0, src.Indices[i0n] };
        for (var k = i0n; k != e1;)
        {
            k = (k + 1) % n;
            ca.Add(src.Indices[k]);
        }
        var a = AddPoly(src, ca);
        ComputePlane(Polys[a]);
        TransferEdge(p, e0, a, 1, SplitEps);
        var edge = 2;
        for (var k = i0n; k != e1; k = (k + 1) % n)
            TransferEdge(p, k, a, edge++, 0.0);
        TransferEdge(p, e1, a, Polys[a].Indices.Count - 1, SplitEps);
        var cb = new List<int> { v0, v1, src.Indices[i1n] };
        for (var k = i1n; k != e0;)
        {
            k = (k + 1) % n;
            cb.Add(src.Indices[k]);
        }
        var b = AddPoly(src, cb);
        ComputePlane(Polys[b]);
        TransferEdge(p, e1, b, 1, SplitEps);
        edge = 2;
        for (var k = i1n; k != e0; k = (k + 1) % n)
            TransferEdge(p, k, b, edge++, 0.0);
        TransferEdge(p, e0, b, Polys[b].Indices.Count - 1, SplitEps);
        LinkTo(a, 0, b, 0, 0, 0.0);
        LinkTo(b, 0, a, 0, 0, 0.0);
        int result;
        if (Polys[a].Indices.Count < 5)
        {
            list?.Add(a);
            result = 2;
        }
        else
            result = SplitCorners(a, 0, Polys[a].Indices.Count / 2, list) + 1;
        if (Polys[b].Indices.Count < 5)
            list?.Add(b);
        else
            result = result + SplitCorners(b, 0, Polys[b].Indices.Count / 2, list) - 1;
        FixList(list, p);
        RemovePoly(p, true);
        return result;
    }

    /// <summary>
    /// 180051990: from corner k, the corners' offsets along dir (XY, from k) until one passes tol; then
    /// the first corner on the far side: within tol of the line -> 1800521d0 at that corner, else the
    /// crossing edge cut by 180051cb0 at the interpolated parameter.
    /// </summary>
    private int SplitAlong(int p, int k, double dx, double dy, double tol, List<int>? list)
    {
        var src = Polys[p];
        var n = src.Indices.Count;
        if (n < 2)
            return 0;
        var pk = Pool[src.Indices[k]];
        double Offset(int at)
        {
            var c = Pool[src.Indices[at % n]];
            return (c.Y - pk.Y) * dy + (c.X - pk.X) * dx;
        }
        var j = 1;
        var s = Offset(j + k);
        while (Math.Abs(s) <= tol)
        {
            j++;
            if (n <= j)
                return 0;
            s = Offset(j + k);
        }
        if (s == 0.0)
            return 0;
        j++;
        if (n <= j)
            return 0;
        var at = j + k;
        double s2;
        while (true)
        {
            s2 = Offset(at);
            if (Math.Abs(s2) <= tol && tol != Math.Abs(s2))
            {
                j++;
                if (n <= j)
                    return 0;
                var at2 = j + k;
                while (true)
                {
                    var s3 = Offset(at2);
                    if (!((s <= 0.0 || -tol <= s3) && (0.0 <= s || s3 <= tol)))
                        break;
                    j++;
                    at2++;
                    if (n <= j)
                        return 0;
                }
                return SplitCorners(p, k, at % n, list);
            }
            if ((0.0 < s && s2 < -tol) || (s < 0.0 && tol < s2))
                break;
            j++;
            at++;
            s = s2;
            if (n <= j)
                return 0;
        }
        return SplitCornerEdge(p, k, (n - 1 + j + k) % n, Math.Abs(s) / (Math.Abs(s) + Math.Abs(s2)), list);
    }

    /// <summary>
    /// 180056260: the polygon's connections ordered by (other polygon, edge) (MSVC std::sort, 180068680);
    /// the corner between two consecutive edges that reach the same polygon (n - 1 then 0 gives 0), or -1.
    /// </summary>
    private int MultiConnectionCorner(int p)
    {
        var poly = Polys[p];
        if (poly.Links is not { } links)
            return -1;
        var n = poly.Indices.Count;
        var sorted = links.Connections.ToArray();
        MsvcSort.Sort(sorted, (a, b) => a.PolyB < b.PolyB || (a.PolyB == b.PolyB && a.EdgeA < b.EdgeA));
        for (var i = 0; i < sorted.Length - 1; i++)
        {
            if (sorted[i].PolyB != sorted[i + 1].PolyB)
                continue;
            int e0 = sorted[i].EdgeA, e1 = sorted[i + 1].EdgeA;
            if (e0 == e1)
                continue;
            var wrap = e0 == 0 && e1 == n - 1;
            if (e0 == e1 - 1)
                return e0 + 1;
            if (wrap)
                return 0;
        }
        return -1;
    }

    /// <summary>
    /// 18006cb10: the polygons not marked removed, in order; each kept polygon's connections renumbered
    /// (+0x61 cleared), those to a dropped polygon swap-removed (180050240) and the slot visited again.
    /// </summary>
    public void CompactRemoved()
    {
        var remap = new int[Polys.Count];
        var kept = new List<NavPoly>();
        for (var i = 0; i < Polys.Count; i++)
        {
            if (Polys[i].Removed)
            {
                remap[i] = -1;
                Polys[i].Links = null;
                continue;
            }
            remap[i] = kept.Count;
            kept.Add(Polys[i]);
        }
        Polys = kept;
        foreach (var p in Polys)
        {
            if (p.Links is not { } links)
                continue;
            for (var i = 0; i < links.Count; i++)
            {
                var c = links.Connections[i];
                c.Flag61 = 0;
                c.PolyA = remap[c.PolyA];
                c.PolyB = remap[c.PolyB];
                if (c.PolyB == -1)
                {
                    links.RemoveAt(i);
                    i--;
                }
            }
        }
    }

    /// <summary>
    /// RemoveSmallAreasOnEdge (180075900): unless the settings' +0x58 byte is set, polygons with exactly
    /// one connection and a fan area (18004d060, float) under the settings' SmallAreaOnEdgeRemovalSize
    /// are dropped (18006cb10).
    /// </summary>
    public void RemoveSmallAreasOnEdge(float minArea, bool skip)
    {
        if (skip)
            return;
        foreach (var p in Polys)
        {
            p.Removed = false;
            if (p.Links is { Count: 1 } && FanArea(p.Indices) < minArea)
                p.Removed = true;
        }
        CompactRemoved();
    }

    /// <summary>
    /// SplitBottleneckPolys (180075cc0, nav_gen_max_bottleneck_width 128): each quad whose edges 1 and 3
    /// have no connections and whose edge 0 or 2 is longer than the width is cut from the middle of
    /// edge 0 to the middle of edge 2 (1800507e0). Polygons added on the way are not visited.
    /// </summary>
    public void SplitBottleneckPolys(double width)
    {
        var count = Polys.Count;
        for (var i = 0; i < count; i++)
        {
            var p = Polys[i];
            if (p.Indices.Count != 4)
                continue;
            bool Linked(int e) => p.Links is { } l && l.Connections.Any(c => c.EdgeA == e);
            if (Linked(3) || Linked(1))
                continue;
            var c0 = Pool[p.Indices[0]];
            var c1 = Pool[p.Indices[1]];
            var c2 = Pool[p.Indices[2]];
            var c3 = Pool[p.Indices[3]];
            var len0 = Math.Sqrt((c0.Y - c1.Y) * (c0.Y - c1.Y) + (c0.X - c1.X) * (c0.X - c1.X) + (c0.Z - c1.Z) * (c0.Z - c1.Z));
            if (len0 <= width)
            {
                var len2 = Math.Sqrt((c2.Y - c3.Y) * (c2.Y - c3.Y) + (c2.X - c3.X) * (c2.X - c3.X) + (c2.Z - c3.Z) * (c2.Z - c3.Z));
                if (len2 <= width)
                    continue;
            }
            SplitEdges(i, 0, 0.5, 2, 0.5, null);
        }
    }

    /// <summary>
    /// SplitPolysWithMultipleConnections (18005be20, nav_gen_split_multi_connection_polys_tol 0.01): each
    /// polygon with a corner between two edges reaching one polygon is cut there across the corner's
    /// neighbours' direction (180051990), the same index visited again after a cut (once in a row).
    /// </summary>
    public void SplitPolysWithMultipleConnections(double tol)
    {
        var previous = -1;
        for (var i = 0; i < Polys.Count; i++)
        {
            var k = MultiConnectionCorner(i);
            var next = previous;
            if (k >= 0)
            {
                var poly = Polys[i];
                var n = poly.Indices.Count;
                var a = Pool[poly.Indices[(n - 1 + k) % n]];
                var c = Pool[poly.Indices[(k + 1) % n]];
                double dx = c.X - a.X, dy = c.Y - a.Y, dz = c.Z - a.Z;
                var xy = dy * dy + dx * dx;
                var len = Math.Sqrt(dz * dz + xy);
                next = i;
                if (1.1920928955078125e-07 < len)
                {
                    double ux = dx, uy = dy;
                    var lxy = Math.Sqrt(xy);
                    if (1.1920928955078125e-07 < lxy)
                    {
                        uy = dy * (1.0 / lxy);
                        ux = (1.0 / lxy) * dx;
                    }
                    if (0 < SplitAlong(i, k, ux, uy, tol, null) && previous != i)
                    {
                        i--;
                        next = i;
                    }
                }
            }
            previous = next;
        }
    }
}
