namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// MergePolys (navsystem 20261015, 180073ba0) and its passes. Inside it the polygons are 0x168-byte
/// records (up to 64 inline corners, +0x60 = 0x40); <see cref="NavPoly"/> holds either kind.
/// </summary>
public sealed partial class NavGenPolys
{
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
