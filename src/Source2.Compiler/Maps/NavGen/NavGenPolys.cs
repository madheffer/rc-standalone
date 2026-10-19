namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// A CNavGenPoly (0x88 bytes): the fields the stages read and write. Valve leaves the rest of the
/// record uninitialised.
/// </summary>
public sealed class NavPoly
{
    /// <summary>+0, +0x68: the corner indices into the vertex pool.</summary>
    public List<int> Indices = [];

    /// <summary>+4: -1 on creation.</summary>
    public int Field4 = -1;

    /// <summary>+8: set when a weld collapses the polygon; such polygons are dropped by the weld.</summary>
    public bool Removed;

    /// <summary>+0x40: the kind byte (Recast: 1 for area 0x3d, 2 for 0x3e); welds only merge vertices whose polygons agree.</summary>
    public byte Kind;

    /// <summary>+0x44: flags (Recast: 4 for area 0x20, else 1).</summary>
    public int Flags;

    /// <summary>+0x48: the unit normal, doubles.</summary>
    public double Nx, Ny, Nz;

    /// <summary>+0x10 / +0x18, written by GenerateConnections (180077130): the origin's distance to the
    /// polygon's first triangle (quads: the nearer of two) and its farthest corner from the origin.</summary>
    public double SortKey, Reach;

    /// <summary>+0x80 from GenerateConnections on: the connection block.</summary>
    public NavLinks? Links;

    /// <summary>+0x60: 5 on creation.</summary>
    public short Field60 = 5;

    /// <summary>+0x62: the Recast stage's byte.</summary>
    public byte Field62;

    /// <summary>+0x64: -1 on creation.</summary>
    public int Field64 = -1;

    public NavPoly Clone()
    {
        var c = (NavPoly)MemberwiseClone();
        c.Indices = [.. Indices];
        return c;
    }
}

/// <summary>
/// The polygon list and vertex pool the generation stages work on (NavGenVertexData_t: a
/// CUtlVector of Vec3D&lt;double&gt;; the per-vertex attribute vector is empty for map input), and the
/// stages before Recast (navsystem 20261015).
/// </summary>
public sealed partial class NavGenPolys
{
    public List<NavPoly> Polys = [];
    public List<(double X, double Y, double Z)> Pool = [];

    /// <summary>
    /// 18004e4d0: every input vertex onto the pool as doubles, then one triangle per three indices,
    /// its normal from 18004d9a0.
    /// </summary>
    public static NavGenPolys FromTriangles(IReadOnlyList<System.Numerics.Vector3> verts, IReadOnlyList<int> indices)
    {
        var g = new NavGenPolys();
        foreach (var v in verts)
            g.Pool.Add((v.X, v.Y, v.Z));
        for (var t = 0; t + 2 < indices.Count; t += 3)
        {
            var p = new NavPoly { Indices = [indices[t], indices[t + 1], indices[t + 2]] };
            g.ComputePlane(p);
            g.Polys.Add(p);
        }
        return g;
    }

    /// <summary>
    /// The Recast stage's polygons (180082280): each detail triangle through 18004fe30 (three corners,
    /// not removed, its normal from 18004d9a0 over the output pool), its kind and flags; +0x60 5, +0x62 0.
    /// </summary>
    public static NavGenPolys FromRecast(IEnumerable<RecastTiles.OutputPoly> polys, IEnumerable<(double X, double Y, double Z)> pool)
    {
        var g = new NavGenPolys { Pool = [.. pool] };
        foreach (var o in polys)
        {
            var p = new NavPoly { Indices = [o.A, o.B, o.C], Kind = o.Kind, Flags = o.Flags };
            g.ComputePlane(p);
            g.Polys.Add(p);
        }
        return g;
    }

    /// <summary>
    /// 18004d9a0: the normal from corners 0, 1 % n and 2 % n, (e2 x e1) with e1 = v1 - v0 and
    /// e2 = v2 - v0, normalised when its length is above FLT_EPSILON; written unless the length is 0.
    /// </summary>
    public bool ComputePlane(NavPoly p)
    {
        var n = p.Indices.Count;
        var a = Pool[p.Indices[0]];
        var b = Pool[p.Indices[1 % n]];
        var c = Pool[p.Indices[2 % n]];
        double e1x = b.X - a.X, e1y = b.Y - a.Y, e1z = b.Z - a.Z;
        double e2y = c.Y - a.Y, e2z = c.Z - a.Z, e2x = c.X - a.X;
        var nx = e2z * e1y - e2y * e1z;
        var ny = e2x * e1z - e2z * e1x;
        var nz = e2y * e1x - e2x * e1y;
        var len = Math.Sqrt(ny * ny + nx * nx + nz * nz);
        if (len > 1.1920928955078125e-07)
        {
            var inv = 1.0 / len;
            nz *= inv;
            ny *= inv;
            nx *= inv;
        }
        if (len == 0.0)
            return false;
        p.Nx = nx;
        p.Ny = ny;
        p.Nz = nz;
        return true;
    }

    /// <summary>1800776d0: false when a pool coordinate is infinite or NaN.</summary>
    public bool ValidateVertices()
    {
        foreach (var (x, y, z) in Pool)
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
                return false;
        return true;
    }

    /// <summary>
    /// 1800808f0: whether b lies on the segment from a to c within tol: when (c - b) . (b - a) is not
    /// negative, the component of (c - a) across (b - a), in the polygon's plane, scaled by |b - a|.
    /// A zero-length a-b or b-c is always on it.
    /// </summary>
    public static bool OnSegment((double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c,
                                 double nx, double ny, double nz, double tol)
    {
        double bcx = c.X - b.X, acx = c.X - a.X, bcz = c.Z - b.Z, abx = b.X - a.X, acz = c.Z - a.Z;
        double aby = b.Y - a.Y, bcy = c.Y - b.Y, abz = b.Z - a.Z, acy = c.Y - a.Y;
        if (!(0.0 <= bcy * aby + bcx * abx + bcz * abz))
            return false;
        var lab = Math.Sqrt(aby * aby + abx * abx + abz * abz);
        if (lab > 1.1920928955078125e-07)
        {
            var inv = 1.0 / lab;
            abx *= inv;
            aby *= inv;
            abz *= inv;
        }
        var lbc = Math.Sqrt(bcx * bcx + bcy * bcy + bcz * bcz);
        var lac = Math.Sqrt(acx * acx + acy * acy + acz * acz);
        if (lac > 1.1920928955078125e-07)
        {
            var inv = 1.0 / lac;
            acx *= inv;
            acy *= inv;
            acz *= inv;
        }
        if (lab == 0.0 || lbc == 0.0)
            return true;
        var d = acx * abx + acy * aby + acz * abz;
        var px = (acx * d - abx) * lab;
        var py = (acy * d - aby) * lab;
        var pz = (acz * d - abz) * lab;
        var k = py * ny + px * nx + pz * nz;
        px -= nx * k;
        py -= ny * k;
        pz -= nz * k;
        return Math.Sqrt(px * px + py * py + pz * pz) <= tol;
    }

    /// <summary>
    /// 18005b290 (in place) and 18005b120 (on a copy): drop the middle corner of the first run of
    /// three found on one segment, again until none is; degenerate when fewer than three remain.
    /// </summary>
    public bool CollapseCollinear(List<int> idx, double nx, double ny, double nz, double tol)
    {
        while (true)
        {
            var n = idx.Count;
            if (n < 3)
                return true;
            var hit = -1;
            for (var i = 0; i < n; i++)
            {
                if (OnSegment(Pool[idx[i % n]], Pool[idx[(i + 1) % n]], Pool[idx[(i + 2) % n]], nx, ny, nz, tol))
                {
                    hit = (i + 1) % n;
                    break;
                }
            }
            if (hit < 0)
                return n < 3;
            idx.RemoveAt(hit);
        }
    }

    /// <summary>
    /// 18005af00: a triangle passes; a larger polygon fails when a corner lies outside an edge's
    /// side in its plane, unless it is the corner just after or just before the edge and on the
    /// edge's line within tol.
    /// </summary>
    public bool Convex(List<int> idx, double nx, double ny, double nz, double tol)
    {
        var n = idx.Count;
        if (n < 3)
            return false;
        if (n == 3)
            return true;
        for (var i = 0; i < n; i++)
        {
            var va = Pool[idx[i]];
            var vb = Pool[idx[(i + 1) % n]];
            double ex = vb.X - va.X, ey = vb.Y - va.Y, ez = vb.Z - va.Z;
            var first = (i + 2) % n;
            var last = (i + n - 1) % n;
            for (var j = first; j != i; j = (j + 1) % n)
            {
                var p = Pool[idx[j]];
                var side = (p.Y - va.Y) * (nx * ez - nz * ex) + (p.X - va.X) * (nz * ey - ny * ez) + (p.Z - va.Z) * (ny * ex - nx * ey);
                if (!(0.0 < side))
                    continue;
                if (j == first)
                {
                    if (!OnSegment(va, vb, p, nx, ny, nz, tol))
                        return false;
                }
                else if (j == last)
                {
                    if (!OnSegment(p, va, vb, nx, ny, nz, tol))
                        return false;
                }
                else
                    return false;
            }
        }
        return true;
    }

    /// <summary>18005b5f0: each polygon's collinear corners dropped in place; degenerate polygons removed.</summary>
    public void RemoveDegens(double tol)
    {
        var kept = new List<NavPoly>(Polys.Count);
        foreach (var p in Polys)
            if (!CollapseCollinear(p.Indices, p.Nx, p.Ny, p.Nz, tol))
                kept.Add(p);
        Polys = kept;
    }

    private record struct WeldRecord(double X, double Y, double Z, double Len, int Index, bool Used);

    /// <summary>
    /// 180062410 (tolerances from convar *(180134b20)+0x58 = 0.001, both as doubles):
    /// <list type="number">
    /// <item>each vertex's polygons (18005ee30, one entry per corner, polygon order);</item>
    /// <item>a record per vertex with its distance from the origin; vertices no polygon uses are out;</item>
    /// <item>MSVC std::sort on the distance (180068c60);</item>
    /// <item>in that order, each remaining record opens a cluster and takes every later one closer in
    /// distance than tol whose polygons all share its polygons' kind, within tol of it, and whose
    /// move onto it passes 18005a240;</item>
    /// <item>polygons not collapsed by a move, renumbered; the pool, one vertex per cluster at its
    /// first record's position.</item>
    /// </list>
    /// </summary>
    public void Weld(double tol, double tol2)
    {
        var nverts = Pool.Count;
        var adj = new List<int>[nverts];
        for (var i = 0; i < nverts; i++)
            adj[i] = [];
        for (var p = 0; p < Polys.Count; p++)
            foreach (var v in Polys[p].Indices)
                adj[v].Add(p);
        foreach (var p in Polys)
            p.Removed = false;
        var records = new WeldRecord[nverts];
        for (var i = 0; i < nverts; i++)
        {
            var (x, y, z) = Pool[i];
            records[i] = new WeldRecord(x, y, z, Math.Sqrt(x * x + y * y + z * z), i, false);
        }
        foreach (var p in Polys)
            foreach (var v in p.Indices)
                records[v].Used = true;
        for (var i = 0; i < nverts; i++)
            if (!records[i].Used)
                records[i].Index = -1;
        MsvcSort.Sort(records, (a, b) => a.Len < b.Len);
        var remap = new int[nverts];
        var cluster = 0;
        for (var r = 0; r < nverts; r++)
        {
            if (records[r].Index == -1)
                continue;
            remap[records[r].Index] = cluster;
            for (var s = r + 1; s < nverts; s++)
            {
                var si = records[s].Index;
                if (si == -1)
                    continue;
                if (tol <= records[s].Len - records[r].Len)
                    break;
                var mixed = false;
                foreach (var a in adj[records[r].Index])
                {
                    foreach (var b in adj[si])
                    {
                        if (Polys[a].Kind != Polys[b].Kind)
                        {
                            mixed = true;
                            break;
                        }
                    }
                    if (mixed)
                        break;
                }
                if (mixed)
                    continue;
                var dx = records[r].X - records[s].X;
                var dy = records[r].Y - records[s].Y;
                var dz = records[r].Z - records[s].Z;
                if (Math.Sqrt(dy * dy + dx * dx + dz * dz) < tol && TryMove(si, dx, dy, dz, adj, tol2))
                {
                    remap[si] = cluster;
                    records[s].Index = -1;
                }
            }
            cluster++;
        }
        var polys = new List<NavPoly>(Polys.Count);
        foreach (var p in Polys)
        {
            if (p.Removed)
                continue;
            for (var k = 0; k < p.Indices.Count; k++)
                p.Indices[k] = remap[p.Indices[k]];
            polys.Add(p);
        }
        Polys = polys;
        var pool = new (double, double, double)[cluster];
        foreach (var rec in records)
            if (rec.Index != -1)
                pool[remap[rec.Index]] = (rec.X, rec.Y, rec.Z);
        Pool = [.. pool];
    }

    // 18005a240: move vertex s by delta and check its polygons; undo and refuse on a failure. Polygons
    // the move collapses stay marked even when a later one refuses, and a triangle that passes keeps
    // its recomputed normal.
    private bool TryMove(int s, double dx, double dy, double dz, List<int>[] adj, double tol2)
    {
        var old = Pool[s];
        Pool[s] = (old.X + dx, old.Y + dy, old.Z + dz);
        foreach (var pi in adj[s])
        {
            var p = Polys[pi];
            if (p.Removed)
                continue;
            if (CollapseCollinear([.. p.Indices], p.Nx, p.Ny, p.Nz, tol2))
            {
                p.Removed = true;
                continue;
            }
            if (!Convex(p.Indices, p.Nx, p.Ny, p.Nz, tol2))
            {
                Pool[s] = old;
                return false;
            }
            if (p.Indices.Count == 3)
            {
                double ox = p.Nx, oy = p.Ny, oz = p.Nz;
                if (ComputePlane(p) && oy * p.Ny + ox * p.Nx + oz * p.Nz <= 0.0)
                {
                    p.Nx = ox;
                    p.Ny = oy;
                    p.Nz = oz;
                    Pool[s] = old;
                    return false;
                }
            }
        }
        return true;
    }
}
