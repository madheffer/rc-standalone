namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// MergePolys (navsystem 20261015, 180073ba0) and its passes. Inside it the polygons are 0x168-byte
/// records (up to 64 inline corners, +0x60 = 0x40); <see cref="NavPoly"/> holds either kind.
/// </summary>
public sealed partial class NavGenPolys
{
    /// <summary>
    /// RemoveStrayVerticalPolys (180075b90, nav_gen_vertical_limit 88): the polygons whose |nz| is above
    /// cos(limit), copies in order; every polygon when the map has an info_nav_oriented_mesh (the RC nav
    /// input's +0x128, set by 180f20e40) or the generation is not Z-up.
    /// </summary>
    public void RemoveStrayVerticalPolys(float limit, bool orientedMesh)
    {
        if (orientedMesh)
            return;
        var cos = CosDegrees(limit);
        Polys = [.. Polys.Where(p => cos < Math.Abs(p.Nz)).Select(p => p.Clone())];
    }

    /// <summary>One entry of the edge list (180056540, 0x20 bytes): +0 squared length, +8 / +0xc the smaller and larger vertex, +0x10 the polygons, +0x18 their count, +0x1c valid.</summary>
    public record struct Edge(double Len2, int Min, int Max, int P0, int P1, int Count, bool Valid);

    private const int EdgeBuckets = 0xfceb;

    /// <summary>
    /// 180056540: every corner edge of every polygon not removed, hashed on its smaller vertex
    /// (% 0xfceb); each bucket MSVC std::sorted on (min &lt;&lt; 32 | max) (180066b80); a run of equal
    /// keys collapses into its first entry, which gathers the run's polygons, two at most (a third
    /// clears it); buckets in order. With <paramref name="byLength"/>, each edge's squared length,
    /// then std::sort longest first (180067200).
    /// </summary>
    public List<Edge> Edges(bool byLength)
    {
        var buckets = new SortedDictionary<int, List<Edge>>();
        for (var i = 0; i < Polys.Count; i++)
        {
            var p = Polys[i];
            if (p.Removed)
                continue;
            var n = p.Indices.Count;
            for (var k = 0; k < n; k++)
            {
                int a = p.Indices[k], b = p.Indices[(k + 1) % n];
                int lo = Math.Min(a, b), hi = Math.Max(a, b);
                var key = (int)((uint)lo % EdgeBuckets);
                if (!buckets.TryGetValue(key, out var list))
                    buckets[key] = list = [];
                list.Add(new Edge(0, lo, hi, i, 0, 1, true));
            }
        }
        var edges = new List<Edge>();
        foreach (var list in buckets.Values)
        {
            var arr = list.ToArray();
            static ulong Key(in Edge e) => ((ulong)(uint)e.Min << 32) | (uint)e.Max;
            MsvcSort.Sort(arr, (a, b) => Key(a) < Key(b));
            var s = 0;
            while (s < arr.Length)
            {
                var j = s + 1;
                for (; j < arr.Length; j++)
                {
                    if (Key(arr[s]) != Key(arr[j]))
                        break;
                    if (!arr[s].Valid)
                        continue;
                    if (arr[s].Count < 2)
                    {
                        if (arr[s].Count == 0)
                            arr[s].P0 = arr[j].P0;
                        else
                            arr[s].P1 = arr[j].P0;
                        arr[s].Count++;
                    }
                    else
                    {
                        arr[s].Valid = false;
                        arr[s].Count = 0;
                        arr[s].Min = -1;
                        arr[s].Max = -1;
                    }
                }
                if (arr[s].Count != 0)
                    edges.Add(arr[s]);
                s = j;
            }
        }
        if (!byLength)
            return edges;
        var sorted = edges.ToArray();
        for (var i = 0; i < sorted.Length; i++)
        {
            var (a, b) = (Pool[sorted[i].Min], Pool[sorted[i].Max]);
            double dy = b.Y - a.Y, dx = b.X - a.X, dz = b.Z - a.Z;
            sorted[i].Len2 = dy * dy + dx * dx + dz * dz;
        }
        MsvcSort.Sort(sorted, (a, b) => a.Len2 > b.Len2);
        return [.. sorted];
    }

    // The corner k where (k, k + 1) is the edge a-b in either direction; 0 when none.
    private static int EdgeAt(List<int> idx, int a, int b)
    {
        var n = idx.Count;
        for (var k = 0; k < n; k++)
        {
            int u = idx[k], v = idx[(k + 1) % n];
            if ((u == a && v == b) || (u == b && v == a))
                return k;
        }
        return 0;
    }

    // 1 when the polygon runs b -> a, 0 when a -> b or neither.
    private static int Runs(List<int> idx, int a, int b)
    {
        var n = idx.Count;
        for (var k = 0; k < n; k++)
        {
            int u = idx[k], v = idx[(k + 1) % n];
            if (u == a && v == b)
                return 0;
            if (u == b && v == a)
                return 1;
        }
        return 0;
    }

    private static bool HasDuplicates(List<int> idx)
    {
        for (var i = 0; i < idx.Count - 1; i++)
            for (var j = i + 1; j < idx.Count; j++)
                if (idx[i] == idx[j])
                    return true;
        return false;
    }

    private (double X, double Y, double Z) P(int v) => Pool[v];

    private static int IndexOf(List<int> idx, int v)
    {
        for (var i = 0; i < idx.Count; i++)
            if (idx[i] == v)
                return i;
        return -1;
    }

    private static int Mod(int a, int n) => a % n;

    /// <summary>
    /// 180081050: of the triangle's corners the first (0, 1, 2) whose two edges make an acute angle,
    /// its nearer and farther neighbour (nearer when |e1|² &lt;= |e2|², summed x, y, z), and whether the
    /// nearer lies on the segment from it to the farther (1800808f0).
    /// </summary>
    private static bool Collinear3((double X, double Y, double Z)[] t, double nx, double ny, double nz, double tol, out int apex, out int near, out int far)
    {
        apex = near = far = -1;
        for (var i = 0; i < 3; i++)
        {
            int ib = (i + 1) % 3, ic = (i + 2) % 3;
            var a = t[i];
            double e1x = t[ib].X - a.X, e1y = t[ib].Y - a.Y, e1z = t[ib].Z - a.Z;
            double e2x = t[ic].X - a.X, e2y = t[ic].Y - a.Y, e2z = t[ic].Z - a.Z;
            if (0.0 < e2y * e1y + e2x * e1x + e2z * e1z)
            {
                apex = i;
                if (e1x * e1x + e1y * e1y + e1z * e1z <= e2x * e2x + e2y * e2y + e2z * e2z)
                    (near, far) = (ib, ic);
                else
                    (near, far) = (ic, ib);
                break;
            }
        }
        if (apex < 0)
            return false;
        return OnSegment(t[apex], t[near], t[far], nx, ny, nz, tol);
    }

    /// <summary>
    /// 180080c10: the plane through a, b and pts[0]; every later point on pts[0]'s side of a-b, or
    /// collinear with a and b within tol (the acute corner of a, b, p with its nearer neighbour on
    /// the segment to the farther, lengths summed y, x, z, nearer only when strictly shorter).
    /// </summary>
    private static bool SameSide((double X, double Y, double Z) a, (double X, double Y, double Z) b,
                                 IReadOnlyList<(double X, double Y, double Z)> pts, double tol)
    {
        double ex = b.X - a.X, ey = b.Y - a.Y, ez = b.Z - a.Z;
        var w = pts[0];
        var nx = (w.Z - a.Z) * ey - (w.Y - a.Y) * ez;
        var ny = (w.X - a.X) * ez - (w.Z - a.Z) * ex;
        var nz = (w.Y - a.Y) * ex - (w.X - a.X) * ey;
        var len = Math.Sqrt(ny * ny + nx * nx + nz * nz);
        if (1.1920928955078125e-07 < len)
        {
            var inv = 1.0 / len;
            nx *= inv;
            ny *= inv;
            nz *= inv;
        }
        for (var i = 1; i < pts.Count; i++)
        {
            var p = pts[i];
            var side = ((p.X - a.X) * ez - ex * (p.Z - a.Z)) * ny + (ey * (p.Z - a.Z) - ez * (p.Y - a.Y)) * nx
                       + ((p.Y - a.Y) * ex - (p.X - a.X) * ey) * nz;
            if (!(side < 0.0))
                continue;
            (double X, double Y, double Z)[] t = [a, b, p];
            int apex = 0, near = 0, far = 0;
            for (var k = 0; k < 3; k++)
            {
                int kb = (k + 1) % 3, kc = (k + 2) % 3;
                var o = t[k];
                double e1x = t[kb].X - o.X, e1y = t[kb].Y - o.Y, e1z = t[kb].Z - o.Z;
                double e2x = t[kc].X - o.X, e2y = t[kc].Y - o.Y, e2z = t[kc].Z - o.Z;
                if (0.0 < e2y * e1y + e2x * e1x + e2z * e1z)
                {
                    apex = k;
                    (near, far) = e2y * e2y + e2x * e2x + e2z * e2z < e1y * e1y + e1x * e1x + e1z * e1z ? (kc, kb) : (kb, kc);
                    break;
                }
            }
            if (!OnSegment(t[apex], t[near], t[far], nx, ny, nz, tol))
                return false;
        }
        return true;
    }

    /// <summary>
    /// 18005e8f0: the last polygon around a that is also around b (adjacency order); it must be live,
    /// share p's flags and lie within the angle of p's normal; then a, v, b collinear in p's plane, or
    /// the polygon holding v with a and b next to each other or one apart through v.
    /// </summary>
    private bool FindThird(out int r, int v, int a, int b, NavPoly p, List<int>[] adj, double cos, double tol)
    {
        r = -1;
        foreach (var x in adj[a])
            foreach (var y in adj[b])
                if (x == y)
                {
                    r = x;
                    break;
                }
        if (r == -1)
            return false;
        var rp = Polys[r];
        if (rp.Removed || p.Flags != rp.Flags || !(cos <= p.Ny * rp.Ny + p.Nx * rp.Nx + p.Nz * rp.Nz))
            return false;
        if (Collinear3([P(a), P(v), P(b)], p.Nx, p.Ny, p.Nz, tol, out _, out _, out _))
            return true;
        if (IndexOf(rp.Indices, v) == -1)
            return false;
        var n = rp.Indices.Count;
        var ia = IndexOf(rp.Indices, a);
        var nextIndex = (ia + 1) % n;
        var next = rp.Indices[nextIndex];
        if (next == b)
            return true;
        if (next == v)
            return rp.Indices[(nextIndex + 1) % n] == b;
        var prevIndex = (n - 1 + ia) % n;
        var prev = rp.Indices[prevIndex];
        if (prev == b)
            return true;
        if (prev != v)
            return false;
        return rp.Indices[(n - 1 + prevIndex) % n] == b;
    }

    /// <summary>
    /// 18005dfa0: which way round R lies from P to Q (centroids against R's normal) picks the corners
    /// (o1, o2 after v along P, o3, o4 before v along Q, o5 / o6 beside b / a on R, or the mirror);
    /// P must keep v and its corners on the inside of o6-a, Q of o5-b, and v and b of o1-o2 (else,
    /// when o2 = o3, P and Q, a and b, o5 and o6, o1 and o4 trade places and o1-o2 is tried again).
    /// </summary>
    private bool SplitCorners(out int o1, out int o2, out int o3, out int o4, out int o5, out int o6, ref int a, ref int b,
                              ref NavPoly pp, ref NavPoly qq, int v, NavPoly r, double tol)
    {
        o1 = o2 = o3 = o4 = o5 = o6 = -1;
        var cr = Centroid(r);
        var cp = Centroid(pp);
        var cq = Centroid(qq);
        double rx = r.Nx, ry = r.Ny, rz = r.Nz;
        int ira = IndexOf(r.Indices, a), irb = IndexOf(r.Indices, b), ipv = IndexOf(pp.Indices, v), iqv = IndexOf(qq.Indices, v);
        int nr = r.Indices.Count, np = pp.Indices.Count, nq = qq.Indices.Count;
        var turn = ((cq.Z - cp.Z) * (cr.X - cp.X) - (cq.X - cp.X) * (cr.Z - cp.Z)) * ry
                   + ((cq.Y - cp.Y) * (cr.Z - cp.Z) - (cq.Z - cp.Z) * (cr.Y - cp.Y)) * rx
                   + ((cq.X - cp.X) * (cr.Y - cp.Y) - (cq.Y - cp.Y) * (cr.X - cp.X)) * rz;
        if (turn <= 0.0)
        {
            o5 = r.Indices[(irb - 1 + nr) % nr];
            o6 = r.Indices[(ira + 1) % nr];
            var i2 = (ipv + 1) % np;
            o2 = pp.Indices[i2];
            o1 = pp.Indices[(i2 + 1) % np];
            var i3 = (nq - 1 + iqv) % nq;
            o3 = qq.Indices[i3];
            o4 = qq.Indices[(nq - 1 + i3) % nq];
        }
        else
        {
            o5 = r.Indices[(irb + 1) % nr];
            o6 = r.Indices[(ira - 1 + nr) % nr];
            var i2 = (ipv - 1 + np) % np;
            o2 = pp.Indices[i2];
            o1 = pp.Indices[(np - 1 + i2) % np];
            var i3 = (iqv + 1) % nq;
            o3 = qq.Indices[i3];
            o4 = qq.Indices[(i3 + 1) % nq];
        }
        (double X, double Y, double Z)[] Ring(NavPoly x) => [P(v), .. x.Indices.Select(P)];
        if (!SameSide(P(o6), P(a), Ring(pp), tol) || !SameSide(P(o5), P(b), Ring(qq), tol))
            return false;
        (double X, double Y, double Z)[] vb = [P(v), P(b)];
        if (SameSide(P(o1), P(o2), vb, tol))
            return true;
        if (o2 != o3)
            return false;
        (pp, qq) = (qq, pp);
        (o6, o5) = (o5, o6);
        (a, b) = (b, a);
        (o1, o4) = (o4, o1);
        return SameSide(P(o1), P(o2), vb, tol);
    }

    /// <summary>
    /// 180080750: where the line p1 + t d1 crosses p3 + s d3 in XY; false when |det| &lt; 1e-6f.
    /// </summary>
    private static bool CrossXY(double p1x, double p1y, double d1x, double d1y, double p3x, double p3y, double d3x, double d3y, out double t, out double s)
    {
        t = s = 0;
        var det = d3y * d1x - d3x * d1y;
        if (Math.Abs(det) < (double)1e-6f)
            return false;
        t = ((p3x - p1x) * d3y + (p1y - p3y) * d3x) / det;
        s = Math.Abs(d3y) < Math.Abs(d3x) ? (t * d1x + (p1x - p3x)) / d3x : ((p1y - p3y) + t * d1y) / d3y;
        return true;
    }

    /// <summary>
    /// 18005e510: from o3 (o4 when o2 = o3) along Q towards b, the first edge the ray o1 -> o2 crosses
    /// in XY (t &gt;= 0, 0 &lt;= s &lt;= 1); the point on it (z along the edge) snaps to the edge's start
    /// or end within tol, else joins the pool (18004ca90). out2 = the corner, out3 / out4 the edge.
    /// </summary>
    private void Crossing(out int out2, out int out3, out int out4, int o1, int o2, int o3, int o4, int b, NavPoly q, int dir, double tol)
    {
        out2 = out3 = out4 = -1;
        var start = o2 != o3 ? o3 : o4;
        var origin = P(o1);
        double d1x = P(o2).X - origin.X, d1y = P(o2).Y - origin.Y;
        var n = q.Indices.Count;
        var i = IndexOf(q.Indices, start);
        var end = IndexOf(q.Indices, b);
        int a, c;
        double s;
        while (true)
        {
            if (i == end)
                return;
            var step = dir < 0 ? n - (-dir % n) : dir;
            a = q.Indices[i];
            c = q.Indices[(step + i) % n];
            var pa = P(a);
            var pc = P(c);
            if (CrossXY(origin.X, origin.Y, d1x, d1y, pa.X, pa.Y, pc.X - pa.X, pc.Y - pa.Y, out var t, out s) && 0.0 <= t && 0.0 <= s && s <= 1.0)
                break;
            i = (step + i) % n;
        }
        var A = P(a);
        var C = P(c);
        var x = (C.X - A.X) * s + A.X;
        var y = (C.Y - A.Y) * s + A.Y;
        var z = (C.Z - A.Z) * s + A.Z;
        out3 = a;
        out4 = c;
        double dx = x - A.X, dy = y - A.Y, dz = z - A.Z;
        if (!(tol <= Math.Sqrt(dx * dx + dy * dy + dz * dz)))
        {
            out2 = a;
            return;
        }
        dx = x - C.X;
        dy = y - C.Y;
        dz = z - C.Z;
        if (Math.Sqrt(dx * dx + dy * dy + dz * dz) < tol)
        {
            out2 = c;
            return;
        }
        Pool.Add((x, y, z));
        out2 = Pool.Count - 1;
    }

    // The corners of x from index `from` stepping dir (as a positive step) until `to`, both included.
    private static void Walk(List<int> into, NavPoly x, int from, int to, int dir)
    {
        var n = x.Indices.Count;
        var i = from;
        into.Add(x.Indices[i % n]);
        while (i != to)
        {
            var step = dir < 0 ? n - (-dir % n) : dir;
            i = (step + i) % n;
            into.Add(x.Indices[i % n]);
        }
    }

    /// <summary>
    /// 18005ca90 (angle 8, tol from MergePolys): sweeps until one merges nothing. Each sweep takes the
    /// polygons around every vertex (18005eb40, removed ones included) and, at a vertex with two or
    /// three, tries each pair: live, same kind and flags, normals within the angle, and the vertex at
    /// a T: its neighbours in the two polygons collinear with it (180081050, first prev/next, then
    /// next/prev). A third polygon R across the T (18005e8f0) and the split corners (18005dfa0), a
    /// crossing on Q (18005e510) when o1-o2 does not clear v and the T's far corners, then R grows
    /// into the gap (R from b to a, P from a to o2, the crossing, Q from the crossing's edge to b) and
    /// Q's remainder (crossing, o2, Q from o3 to the crossing's edge start) becomes a polygon of its
    /// own; P, Q and R are removed. One merge per vertex per sweep. Returns the live polygons.
    /// </summary>
    public List<NavPoly> MergeVerts(double angle, double tol)
    {
        foreach (var p in Polys)
            p.Removed = false;
        var cos = CosDegrees(angle);
        while (true)
        {
            var any = false;
            var adj = new List<int>[Pool.Count];
            for (var i = 0; i < adj.Length; i++)
                adj[i] = [];
            for (var pi = 0; pi < Polys.Count; pi++)
                foreach (var c in Polys[pi].Indices)
                    adj[c].Add(pi);
            if (adj.Length < 1)
                break;
            for (var v = 0; v < adj.Length; v++)
            {
                var around = adj[v];
                var count = around.Count;
                if ((uint)(count - 2) >= 2)
                    continue;
                var done = false;
                for (var i = 0; i < count - 1 && !done; i++)
                    for (var j = i + 1; j < count && !done; j++)
                        done = TryMergeAt(v, Polys[around[i]], Polys[around[j]], adj, cos, tol);
                if (done)
                    any = true;
            }
            if (!any)
                break;
        }
        return [.. Polys.Where(p => !p.Removed)];
    }

    private bool TryMergeAt(int v, NavPoly a0, NavPoly b0, List<int>[] adj, double cos, double tol)
    {
        if (a0.Removed || b0.Removed || a0.Kind != b0.Kind || a0.Flags != b0.Flags
            || !(cos <= a0.Ny * b0.Ny + a0.Nx * b0.Nx + a0.Nz * b0.Nz))
            return false;
        int na = a0.Indices.Count, nb = b0.Indices.Count;
        var ia = IndexOf(a0.Indices, v);
        int aPrev = a0.Indices[(na - 1 + ia) % na], aNext = a0.Indices[(ia + 1) % na];
        var ib = IndexOf(b0.Indices, v);
        int bPrev = b0.Indices[(nb - 1 + ib) % nb], bNext = b0.Indices[(ib + 1) % nb];
        (double X, double Y, double Z)[][] triples = [[P(v), P(aPrev), P(bNext)], [P(v), P(aNext), P(bPrev)]];
        var which = 0;
        int far;
        while (true)
        {
            if (Collinear3(triples[which], a0.Nx, a0.Ny, a0.Nz, tol, out var apex, out _, out far) && apex == 0)
                break;
            if (++which > 1)
                return false;
        }
        var pp = a0;
        var qq = b0;
        int a = aNext, b = bPrev, alt = bNext, prevA = aPrev;
        if (far != 2)
        {
            which = (which + 1) % 2;
            b = aPrev;
            alt = aNext;
            pp = b0;
            qq = a0;
            prevA = bPrev;
            a = bNext;
        }
        if (which != 0)
        {
            b = alt;
            a = prevA;
        }
        if (a == b)
            return false;
        if (!FindThird(out var ri, v, a, b, pp, adj, cos, tol))
            return false;
        var r = Polys[ri];
        if (!SplitCorners(out var o1, out var o2, out var o3, out var o4, out _, out _, ref a, ref b, ref pp, ref qq, v, r, tol))
            return false;
        var nq = qq.Indices.Count;
        var i3 = IndexOf(qq.Indices, o3);
        var i4 = IndexOf(qq.Indices, o4);
        var dir = (i3 + 1) % nq == i4 ? 1 : -1;
        int cross, edgeStart, edgeEnd;
        if (!SameSide(P(o1), P(o2), [P(v), P(o3), P(o4)], tol))
            Crossing(out cross, out edgeStart, out edgeEnd, o1, o2, o3, o4, b, qq, dir, tol);
        else
        {
            if (o2 != o3)
                return false;
            cross = edgeStart = edgeEnd = o2;
        }
        if (cross == -1 || edgeStart == -1 || edgeEnd == -1)
            return false;

        var grown = new NavPoly
        {
            Kind = r.Kind, Flags = r.Flags, Nx = r.Nx, Ny = r.Ny, Nz = r.Nz, Field60 = 0x40, Field62 = r.Field62, Field64 = r.Field64,
        };
        {
            var nr = r.Indices.Count;
            var stop = IndexOf(r.Indices, a);
            for (var i = IndexOf(r.Indices, b); i != stop;)
            {
                grown.Indices.Add(r.Indices[i]);
                var step = dir < 0 ? nr - (-dir % nr) : dir;
                i = (step + i) % nr;
            }
        }
        Walk(grown.Indices, pp, IndexOf(pp.Indices, a), IndexOf(pp.Indices, o2), dir);
        grown.Indices.Add(cross);
        Walk(grown.Indices, qq, IndexOf(qq.Indices, edgeEnd), IndexOf(qq.Indices, b), dir);
        if (dir != 1)
            grown.Indices.Reverse();
        if (grown.Indices.Count > 2)
            CollapseCollinear(grown.Indices, r.Nx, r.Ny, r.Nz, tol);
        if (HasDuplicates(grown.Indices))
            return false;
        if (CollapseCollinear([.. grown.Indices], r.Nx, r.Ny, r.Nz, tol) || !Convex(grown.Indices, r.Nx, r.Ny, r.Nz, tol))
            return false;
        Polys.Add(grown);

        var rest = new NavPoly
        {
            Kind = qq.Kind, Flags = qq.Flags, Nx = qq.Nx, Ny = qq.Ny, Nz = qq.Nz, Field60 = 0x40, Field62 = qq.Field62, Field64 = qq.Field64,
        };
        rest.Indices.Add(cross);
        rest.Indices.Add(o2);
        Walk(rest.Indices, qq, i3, IndexOf(qq.Indices, edgeStart), dir);
        if (dir != 1)
            rest.Indices.Reverse();
        if (rest.Indices.Count > 2)
            CollapseCollinear(rest.Indices, qq.Nx, qq.Ny, qq.Nz, tol);
        if (rest.Indices.Count > 2)
            Polys.Add(rest);
        pp.Removed = true;
        qq.Removed = true;
        r.Removed = true;
        return true;
    }

    /// <summary>
    /// 18004d060: twice the fan area from corner 0, each triangle's |cross| (summed y², x², z²) taken
    /// to float and added in float.
    /// </summary>
    public float FanArea(List<int> idx)
    {
        var sum = 0f;
        var n = idx.Count;
        if (n - 1 <= 1)
            return 0f;
        var o = Pool[idx[0]];
        for (var i = 1; i < n - 1; i++)
        {
            var p = Pool[idx[i % n]];
            var q = Pool[idx[(i + 1) % n]];
            double e1x = p.X - o.X, e1y = p.Y - o.Y, e1z = p.Z - o.Z;
            double e2x = q.X - o.X, e2y = q.Y - o.Y, e2z = q.Z - o.Z;
            var cx = e2z * e1y - e2y * e1z;
            var cz = e2y * e1x - e2x * e1y;
            var cy = e2x * e1z - e2z * e1x;
            sum += (float)Math.Sqrt(cy * cy + cx * cx + cz * cz);
        }
        return sum;
    }

    /// <summary>
    /// 1800584f0 (MergePolys' last pass, 4) and 180059240 (LimitMaxPolyEdges, 4): a polygon with more
    /// corners becomes a fan of pieces of up to that many corners from the first anchor corner whose
    /// pieces keep the largest fan area above 0.01 and smallest / largest above 0.05 (floats); the
    /// pieces keep the polygon's normal and fields. No anchor: the triangulation of
    /// <see cref="NavGenTriangulate"/> (1800f3780).
    /// </summary>
    public List<NavPoly> SplitToFans(int maxCorners)
    {
        var result = new List<NavPoly>();
        foreach (var x in Polys)
        {
            var n = x.Indices.Count;
            if (maxCorners >= n)
            {
                result.Add(x.Clone());
                continue;
            }
            List<NavPoly>? pieces = null;
            for (var s = 0; s < n && pieces is null; s++)
            {
                var list = new List<NavPoly>();
                var remaining = n - 2;
                var k = s + 1;
                float max = -float.MaxValue, min = float.MaxValue;
                var ok = true;
                while (remaining != 0)
                {
                    var take = Math.Min(remaining, maxCorners - 2);
                    var piece = x.Clone();
                    piece.Indices = [x.Indices[s]];
                    var at = k % n;
                    piece.Indices.Add(x.Indices[at]);
                    for (var j = 1; j <= take; j++)
                        piece.Indices.Add(x.Indices[++at % n]);
                    k += maxCorners - 2;
                    remaining -= take;
                    var area = FanArea(piece.Indices);
                    if (max <= area)
                        max = area;
                    if (area <= min)
                        min = area;
                    if (MathF.Abs(max) <= 0.01f || min / max <= 0.05f)
                    {
                        ok = false;
                        break;
                    }
                    list.Add(piece);
                }
                if (ok)
                    pieces = list;
            }
            if (pieces is not null)
            {
                result.AddRange(pieces);
                continue;
            }
            // No anchor: triangulate (1800f3780) the float corners about the float normal; each triangle
            // keeps the polygon's fields with its own plane (18004d9a0). Fewer than 3 indices drop it.
            var tris = new List<int>();
            var count = NavGenTriangulate.Triangulate([.. x.Indices.Select(i => ((float)Pool[i].X, (float)Pool[i].Y, (float)Pool[i].Z))],
                ((float)x.Nx, (float)x.Ny, (float)x.Nz), tris, (n - 2) * 3);
            for (var t = 2; t < count; t += 3)
            {
                var tri = x.Clone();
                tri.Indices = [x.Indices[tris[t - 2]], x.Indices[tris[t - 1]], x.Indices[tris[t]]];
                ComputePlane(tri);
                result.Add(tri);
            }
        }
        return result;
    }

    // 18004fe60: the points of a polygon against the plane n.p = d within eps: front (above eps) and
    // on-plane points to A, back and on-plane points next to back ones to B, crossings (180080820) to
    // both; fewer than 3 points empties a side.
    private static (List<(double X, double Y, double Z)> A, List<(double X, double Y, double Z)> B) ClipByPlane(
        List<(double X, double Y, double Z)> pts, double nx, double ny, double nz, double d, double eps)
    {
        var a = new List<(double X, double Y, double Z)>();
        var b = new List<(double X, double Y, double Z)>();
        double Dist((double X, double Y, double Z) p) => (p.Y * ny + p.X * nx + p.Z * nz) - d;
        int Class(double dist) => dist <= eps ? ((-eps < dist || -eps == dist) ? 2 : 1) : 0;
        (double X, double Y, double Z) Cross((double X, double Y, double Z) p, (double X, double Y, double Z) q)
        {
            var t = (d - (ny * p.Y + nx * p.X + nz * p.Z)) / (ny * (q.Y - p.Y) + nx * (q.X - p.X) + nz * (q.Z - p.Z));
            return ((q.X - p.X) * t + p.X, (q.Y - p.Y) * t + p.Y, (q.Z - p.Z) * t + p.Z);
        }
        var n = pts.Count;
        var prev = pts[n - 1];
        var cls = Class(Dist(prev));
        var prevPrev = 3;
        for (var i = 0; i < n; i++)
        {
            var prevCls = cls;
            var cur = pts[i];
            var dist = Dist(cur);
            if (dist <= eps)
            {
                cls = (-eps < dist || -eps == dist) ? 2 : 1;
                var addB = true;
                if (cls == 1)
                {
                    if (prevCls == 0)
                    {
                        var x = Cross(prev, cur);
                        a.Add(x);
                        b.Add(x);
                    }
                    else if (prevCls == 2 && prevPrev != 1)
                        b.Add(prev);
                }
                else
                {
                    a.Add(cur);
                    if (prevCls != 1)
                        addB = false;
                    else
                    {
                        var next = (i + 1) % n;
                        if (next < i)
                        {
                            var nd = Dist(pts[next]);
                            if (nd <= eps && nd <= -eps && -eps != nd)
                                addB = false;
                        }
                    }
                }
                if (addB)
                    b.Add(cur);
            }
            else
            {
                if (prevCls == 1)
                {
                    var x = Cross(cur, prev);
                    a.Add(x);
                    b.Add(x);
                }
                a.Add(cur);
                cls = 0;
            }
            prevPrev = prevCls;
            prev = cur;
        }
        if (a.Count < 3)
            a.Clear();
        if (b.Count < 3)
            b.Clear();
        return (a, b);
    }

    // 18004bbe0: a point list as a fan of quads (a triangle last when the count is odd), every corner a
    // new pool vertex, normal from corners 0-1-2, flags 0, +0x60 5, +0x62 and +0x64 from the source.
    private void FanFromPoints(List<(double X, double Y, double Z)> pts, NavPoly source, List<NavPoly> into)
    {
        var n = pts.Count;
        for (var l = 1; l < n - 1; l += 2)
        {
            var quad = l + 2 < n;
            var p = new NavPoly { Kind = source.Kind, Flags = 0, Field60 = 5, Field62 = source.Field62, Field64 = source.Field64 };
            foreach (var at in quad ? new[] { 0, l, l + 1, l + 2 } : [0, l, l + 1])
            {
                Pool.Add(pts[at]);
                p.Indices.Add(Pool.Count - 1);
            }
            var o = Pool[p.Indices[0]];
            var q1 = Pool[p.Indices[1]];
            var q2 = Pool[p.Indices[2]];
            double e1x = q1.X - o.X, e1y = q1.Y - o.Y, e1z = q1.Z - o.Z;
            double e2y = q2.Y - o.Y, e2z = q2.Z - o.Z, e2x = q2.X - o.X;
            var nx = e2z * e1y - e2y * e1z;
            var ny = e2x * e1z - e2z * e1x;
            var nz = e2y * e1x - e2x * e1y;
            var len = Math.Sqrt(ny * ny + nx * nx + nz * nz);
            if (1.1920928955078125e-07 < len)
            {
                var inv = 1.0 / len;
                nz *= inv;
                ny *= inv;
                nx *= inv;
            }
            if (len != 0.0)
                (p.Nx, p.Ny, p.Nz) = (nx, ny, nz);
            into.Add(p);
        }
    }

    /// <summary>
    /// LimitMaxPolyEdgeLen (180057b90, nav_gen_max_edge_len 512, nav_gen_max_edge_len_split_tol 24):
    /// each polygon, then each piece queued, in order (180057de0): kept when no edge is longer than
    /// max(limit, 3 tol); else clipped (tolerance tol) by the plane halving its bounding box across
    /// its longest axis (x only when strictly longest, then y when longer than z), each side fanned
    /// into new polygons over new vertices and queued.
    /// </summary>
    public void LimitMaxPolyEdgeLen(double limit, double tol)
    {
        var result = new List<NavPoly>();
        var pending = new List<NavPoly>();
        foreach (var p in Polys)
            LimitEdgeLen(p, result, pending, limit, tol);
        for (var i = 0; i < pending.Count; i++)
            LimitEdgeLen(pending[i], result, pending, limit, tol);
        Polys = result;
    }

    private void LimitEdgeLen(NavPoly poly, List<NavPoly> result, List<NavPoly> pending, double limit, double tol)
    {
        if (limit <= tol * 3.0)
            limit = tol * 3.0;
        var lim2 = limit * limit;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = -double.MaxValue, maxY = -double.MaxValue, maxZ = -double.MaxValue;
        var n = poly.Indices.Count;
        var tooLong = false;
        for (var i = 0; i < n; i++)
        {
            var p = Pool[poly.Indices[i]];
            if (p.X <= minX) minX = p.X;
            if (maxX <= p.X) maxX = p.X;
            if (p.Y <= minY) minY = p.Y;
            if (maxY <= p.Y) maxY = p.Y;
            if (p.Z <= minZ) minZ = p.Z;
            if (maxZ <= p.Z) maxZ = p.Z;
            var q = Pool[poly.Indices[(i + 1) % n]];
            double dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
            if (lim2 < dy * dy + dx * dx + dz * dz)
                tooLong = true;
        }
        if (!tooLong)
        {
            result.Add(poly.Clone());
            return;
        }
        double ex = maxX - minX, ey = maxY - minY, ez = maxZ - minZ;
        double nx = 0, ny = 0, nz = 0, d;
        if (ex <= ey || ex <= ez)
        {
            if (ez < ey)
            {
                ny = 1;
                d = maxY + minY;
            }
            else
            {
                nz = 1;
                d = maxZ + minZ;
            }
        }
        else
        {
            nx = 1;
            d = maxX + minX;
        }
        d *= 0.5;
        var (a, b) = ClipByPlane([.. poly.Indices.Select(i => Pool[i])], nx, ny, nz, d, tol);
        if (a.Count != 0)
            FanFromPoints(a, poly, pending);
        if (b.Count != 0)
            FanFromPoints(b, poly, pending);
    }

    /// <summary>
    /// MergePolys (180073ba0, nav_gen_opt_to_quads*): six rounds, the merge tolerance from 1e-5 and the
    /// weld tolerance from 0 stepping a sixth of the way to 0.1 / 0.01 each round; in a round, weld,
    /// degens, edge merges, weld, degens, vertex merges, again while the edge merges shrank the list,
    /// and all of it again while the vertex merges did; then the fan split to 4 corners. Polygons pass
    /// through untouched when the map has an info_nav_oriented_mesh.
    /// </summary>
    public void MergePolys(bool orientedMesh, Action<string, NavGenPolys>? trace = null)
    {
        if (orientedMesh)
            return;
        double tol = NavGenStages.MergeTolStart, weld = NavGenStages.MergeWeldStart;
        var tolStep = (double)NavGenStages.MergeTolEnd - tol;
        var weldStep = (double)NavGenStages.MergeWeldEnd - weld;
        var rounds = NavGenStages.MergeRounds;
        for (var r = 0; r < rounds; r++)
        {
            int before, after;
            do
            {
                int edgesIn, edgesOut;
                do
                {
                    Weld(weld, weld);
                    trace?.Invoke("Weld", this);
                    RemoveDegens(tol);
                    trace?.Invoke("Degens", this);
                    edgesIn = Polys.Count;
                    Polys = MergeEdges(NavGenStages.MergeAngle, NavGenStages.MergeMaxDeviation, tol);
                    trace?.Invoke("MergeEdges", this);
                    edgesOut = Polys.Count;
                    Weld(weld, weld);
                    trace?.Invoke("Weld", this);
                    RemoveDegens(tol);
                    trace?.Invoke("Degens", this);
                    before = Polys.Count;
                    Polys = MergeVerts(NavGenStages.MergeAngle, tol);
                    trace?.Invoke("MergeVerts", this);
                    after = Polys.Count;
                }
                while (edgesOut < edgesIn);
            }
            while (after < before);
            tol += tolStep / rounds;
            weld += weldStep / rounds;
        }
        Polys = SplitToFans(4);
        foreach (var p in Polys)
            p.Field60 = 5;
        trace?.Invoke("Final", this);
    }

    /// <summary>V_cosd((double)((float)degrees * 0.017453292f)), as the merge passes compute their limit.</summary>
    public static double CosDegrees(double degrees) => Math.Cos((double)((float)degrees * 0.017453292f));

    /// <summary>18004ef30: the corners' mean, summed in order and scaled by 1 / n.</summary>
    public (double X, double Y, double Z) Centroid(NavPoly p)
    {
        double x = 0, y = 0, z = 0;
        foreach (var v in p.Indices)
        {
            var q = Pool[v];
            x += q.X;
            y += q.Y;
            z += q.Z;
        }
        var inv = 1.0 / p.Indices.Count;
        return (x * inv, y * inv, z * inv);
    }

    /// <summary>18004cd50: the largest distance from the centroid to a corner (0 for none).</summary>
    public double Radius(NavPoly p)
    {
        if (p.Indices.Count < 1)
            return 0;
        var c = Centroid(p);
        var r = 0.0;
        foreach (var v in p.Indices)
        {
            var q = Pool[v];
            double dx = c.X - q.X, dy = c.Y - q.Y, dz = c.Z - q.Z;
            var d = Math.Sqrt(dy * dy + dx * dx + dz * dz);
            if (r <= d)
                r = d;
        }
        return r;
    }

    /// <summary>
    /// 1800541a0: the corners of the polygons as floats, a least-squares plane z = ax + by + c through
    /// them (1800ee9a0), and the largest distance of a corner above it (0 when below or no fit).
    /// </summary>
    public float PlaneDeviation(params NavPoly[] polys)
    {
        var pts = new List<(float X, float Y, float Z)>();
        foreach (var p in polys)
            foreach (var v in p.Indices)
            {
                var q = Pool[v];
                pts.Add(((float)q.X, (float)q.Y, (float)q.Z));
            }
        if (!FitPlane(pts, out var nx, out var ny, out var nz, out var d))
            return 0f;
        var worst = 0f;
        foreach (var (x, y, z) in pts)
        {
            var e = nz * z + ny * y + nx * x - d;
            if (worst <= e)
                worst = e;
        }
        return worst;
    }

    /// <summary>
    /// 1800ee9a0: sums in float (in order), the centred normal equations for z = ax + by + c, a fit
    /// when |det| &gt; 1e-6; the plane (-a, -b, 1) normalised (18001f7c0) and its offset.
    /// </summary>
    public static bool FitPlane(List<(float X, float Y, float Z)> pts, out float nx, out float ny, out float nz, out float d)
    {
        nx = ny = nz = d = 0f;
        var n = pts.Count;
        if (n < 3)
            return false;
        float sx = 0, sy = 0, sz = 0, sxx = 0, syy = 0, syz = 0, sxz = 0, sxy = 0;
        foreach (var (x, y, z) in pts)
        {
            sx += x;
            sy += y;
            sz += z;
            sxx += x * x;
            syy += y * y;
            syz += y * z;
            sxz += z * x;
            sxy += y * x;
        }
        var fn = (float)n;
        var inv = 1f / fn;
        var my = sy * inv;
        var mz = sz * inv;
        var mx = sx * inv;
        var t = mx * fn;
        sxx -= mx * t;
        sxy -= t * my;
        sxz -= t * mz;
        syy -= my * my * fn;
        syz -= my * fn * mz;
        var det = syy * sxx - sxy * sxy;
        if (!(1e-6f < MathF.Abs(det)))
            return false;
        var id = 1f / det;
        var a = (syy * sxz - syz * sxy) * id;
        var b = (syz * sxx - sxy * sxz) * id;
        nx = -a;
        ny = -b;
        nz = 1f;
        var len = MathF.Sqrt(ny * ny + nz * nz + nx * nx);
        if (!(1e-17f > len) && !(len > 1e17f))
        {
            var il = 1f / len;
            nx *= il;
            ny *= il;
            nz *= il;
        }
        d = ((mz - mx * a) - my * b) / len;
        return true;
    }

    /// <summary>
    /// 18005a570 (angle 8, deviation 4, tol from MergePolys): until a pass merges nothing, the edge list
    /// longest first; each edge two polygons share, wound opposite, both live, same kind and flags,
    /// normals within the angle, becomes one polygon (the first's corners from after the edge, then
    /// the second's) when under 65 corners: collinear corners dropped, 3 to 16 left, convex, no
    /// repeated corner, the radii summing to 200 at most or the corners within the deviation of their
    /// plane, and still convex. The two are marked removed and the merge appended. Returns the live
    /// polygons in order.
    /// </summary>
    public List<NavPoly> MergeEdges(double angle, double maxDeviation, double tol, Action<List<Edge>>? trace = null)
    {
        var cos = CosDegrees(angle);
        foreach (var p in Polys)
            p.Removed = false;
        while (true)
        {
            var changed = false;
            var edges = Edges(true);
            trace?.Invoke(edges);
            foreach (var e in edges)
            {
                if (e.Count != 2)
                    continue;
                var a = Polys[e.P0];
                var b = Polys[e.P1];
                if (Runs(a.Indices, e.Min, e.Max) == Runs(b.Indices, e.Min, e.Max))
                    continue;
                if (a.Removed || b.Removed || a.Kind != b.Kind || a.Flags != b.Flags
                    || !(cos <= a.Ny * b.Ny + a.Nx * b.Nx + a.Nz * b.Nz))
                    continue;
                int ia = EdgeAt(a.Indices, e.Min, e.Max), ib = EdgeAt(b.Indices, e.Min, e.Max);
                int na = a.Indices.Count, nb = b.Indices.Count;
                if (na + nb >= 0x41)
                    continue;
                var merged = new NavPoly
                {
                    Kind = a.Kind, Flags = a.Flags, Nx = a.Nx, Ny = a.Ny, Nz = a.Nz, Field60 = 0x40, Field62 = a.Field62,
                };
                for (var j = 0; j < na; j++)
                    merged.Indices.Add(a.Indices[(ia + 1 + j) % na]);
                for (var j = 0; j < nb; j++)
                    merged.Indices.Add(b.Indices[(ib + 1 + j) % nb]);
                if (CollapseCollinear(merged.Indices, a.Nx, a.Ny, a.Nz, tol))
                    continue;
                if ((uint)(merged.Indices.Count - 3) >= 0xe || !Convex(merged.Indices, a.Nx, a.Ny, a.Nz, tol))
                    continue;
                if (HasDuplicates(merged.Indices))
                    continue;
                if (0.0 <= maxDeviation && !(Radius(b) + Radius(a) <= 200.0) && !((double)PlaneDeviation(a, b) <= maxDeviation))
                    continue;
                if (CollapseCollinear([.. merged.Indices], a.Nx, a.Ny, a.Nz, tol) || !Convex(merged.Indices, a.Nx, a.Ny, a.Nz, tol))
                    continue;
                a.Removed = true;
                b.Removed = true;
                Polys.Add(merged);
                changed = true;
            }
            if (!changed)
                return [.. Polys.Where(p => !p.Removed)];
        }
    }
}
