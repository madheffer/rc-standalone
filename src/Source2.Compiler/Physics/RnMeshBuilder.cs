using System.Numerics;

namespace Source2.Compiler.Physics;

/// <summary>A cooked triangle-mesh shape, laid out as <c>RnMesh_t</c>.</summary>
public sealed class RnMesh
{
    /// <summary>One BVH node: a box, the right child's distance (or a leaf's triangle count) with the split axis in the top two bits (3 = leaf), and a leaf's first triangle.</summary>
    public readonly record struct Node(Vector3 Min, uint Children, Vector3 Max, uint TriangleOffset);

    public Vector3 Min;
    public Vector3 Max;
    public Node[] Nodes = [];
    public Vector3[] Vertices = [];
    public (int A, int B, int C)[] Triangles = [];
    /// <summary>Per triangle, when the mesh was given materials.</summary>
    public byte[] Materials = [];
    public Vector3 OrthographicAreas;
    public float SurfaceArea;
    public uint Flags;
    /// <summary>+0xb8: bit 0 when a mesh that would be closed touches itself.</summary>
    public uint DebugFlags;
}

/// <summary>
/// vphysics2's <c>RnMeshCreate</c>: a triangle soup into an <see cref="RnMesh"/>.
///
/// <para>The vertices are welded at 1/32 (the same CMesh weld as
/// <see cref="MeshWeld"/>, its clusters kept in the order they formed)
/// unless the options say not to. A triangle with a
/// repeated corner or a cross product whose squared length is 1e-10 or less
/// is dropped. Each kept triangle's box, grown by 1/32 each way, goes into a
/// BVH (FUN_1801a4070): four triangles or fewer make a leaf; otherwise the
/// centroids are binned 32 ways on each axis at least 1/16 wide and the split
/// with the least area-weighted count wins (FUN_1801aab10); failing that,
/// eight or fewer make a leaf and more are halved in place across the widest
/// axis (FUN_1801a93e0). Nodes are stored depth first, left child next.</para>
///
/// <para>Unused vertices are dropped in order, triangles follow the leaves,
/// and each triangle is rotated by its edge lengths. Then the orthographic
/// areas (FUN_180166ff0).</para>
///
/// <para>Flags (FUN_1801673a0): a mesh of more than three triangles whose
/// every edge, direction aside, is shared by exactly two triangles
/// (FUN_180165750's adjacency has no negative entry) is closed (1), and
/// inverted (2) when its signed volume about the box centre is negative.</para>
///
/// <para>A closed mesh must also not touch itself: two triangles that share
/// no vertex may not come within 1.19e-7 (FUN_180164000 walks the BVH
/// against itself; FUN_180165350 skips pairs sharing a vertex and measures
/// the rest). One that does gets no flags and bit 0 of the second flag word
/// (+0xb8). The distance here is exact, not vphysics2's GJK, so a pair
/// right at the threshold could come out differently.</para>
///
/// <para>Not ported: the simplifier the options can ask for.</para>
/// </summary>
public static class RnMeshBuilder
{
    /// <summary>RnMeshCreate's options: weld, weld tolerance (below 0: 1/32), simplify tolerance and target.</summary>
    public sealed record Options(bool Weld = true, float WeldTolerance = -1f, float SimplifyTolerance = 0f, int SimplifyTarget = 0);

    // A triangle's grown box, as FUN_1801a4070 sorts them.
    private struct Record
    {
        public int Triangle;
        public Vector3 Min;
        public Vector3 Max;
    }

    private readonly record struct Split(int Axis, int Left, Vector3 LeftMin, Vector3 LeftMax, Vector3 RightMin, Vector3 RightMax);

    /// <summary>The mesh, or null when every triangle is degenerate.</summary>
    public static RnMesh? Create(int[] indices, Vector3[] vertices, byte[]? materials, Options? options = null)
    {
        options ??= new Options();
        if (indices.Length < 3)
            return null;
        if (options.SimplifyTolerance > 0f && options.SimplifyTarget != 0)
            throw new NotSupportedException("RnMeshCreate's simplifier is not ported");

        var positions = vertices;
        var tris = indices;
        if (options.Weld)
        {
            var tolerance = options.WeldTolerance < 0f ? 0.03125f : options.WeldTolerance;
            var flat = new float[vertices.Length * 3];
            for (var i = 0; i < vertices.Length; i++)
                (flat[i * 3], flat[(i * 3) + 1], flat[(i * 3) + 2]) = (vertices[i].X, vertices[i].Y, vertices[i].Z);
            var (welded, weldedIndices) = MeshWeld.Weld(flat, 3, indices, [new MeshWeld.Stream("position", 0, 3, false, 42)], tolerance, false);
            positions = new Vector3[welded.Length / 3];
            for (var i = 0; i < positions.Length; i++)
                positions[i] = new Vector3(welded[i * 3], welded[(i * 3) + 1], welded[(i * 3) + 2]);
            tris = weldedIndices;
        }

        var triangleCount = tris.Length / 3;
        var used = new bool[positions.Length];
        var records = new List<Record>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(-float.MaxValue);
        for (var t = 0; t < triangleCount; t++)
        {
            int a = tris[t * 3], b = tris[(t * 3) + 1], c = tris[(t * 3) + 2];
            if (a == b || b == c || c == a)
                continue;
            Vector3 p = positions[a], q = positions[b], r = positions[c];
            var cy = ((q.Z - p.Z) * (r.X - p.X)) - ((r.Z - p.Z) * (q.X - p.X));
            var cx = ((r.Z - p.Z) * (q.Y - p.Y)) - ((q.Z - p.Z) * (r.Y - p.Y));
            var cz = ((q.X - p.X) * (r.Y - p.Y)) - ((q.Y - p.Y) * (r.X - p.X));
            if (!(1e-10f < ((cy * cy) + (cx * cx)) + (cz * cz)))
                continue;
            used[a] = used[b] = used[c] = true;
            var lo = Vector3.Min(Vector3.Min(p, q), r) - new Vector3(0.03125f);
            var hi = Vector3.Max(Vector3.Max(p, q), r) + new Vector3(0.03125f);
            records.Add(new Record { Triangle = t, Min = lo, Max = hi });
            min = new Vector3(lo.X <= min.X ? lo.X : min.X, lo.Y <= min.Y ? lo.Y : min.Y, lo.Z <= min.Z ? lo.Z : min.Z);
            max = new Vector3(max.X <= hi.X ? hi.X : max.X, max.Y <= hi.Y ? hi.Y : max.Y, max.Z <= hi.Z ? hi.Z : max.Z);
        }
        if (records.Count == 0)
            return null;

        var nodes = new List<RnMesh.Node>(records.Count * 2);
        var array = records.ToArray();
        Build(array, 0, array.Length, nodes);

        // Unused vertices go; the rest keep their order.
        var remap = new int[positions.Length];
        var kept = new List<Vector3>();
        for (var i = 0; i < positions.Length; i++)
        {
            remap[i] = used[i] ? kept.Count : -1;
            if (used[i])
                kept.Add(positions[i]);
        }
        var mesh = new RnMesh { Min = min, Max = max, Nodes = [.. nodes], Vertices = [.. kept] };
        var outTris = new (int, int, int)[array.Length];
        for (var i = 0; i < array.Length; i++)
        {
            var t = array[i].Triangle;
            outTris[i] = Rotate(remap[tris[t * 3]], remap[tris[(t * 3) + 1]], remap[tris[(t * 3) + 2]], mesh.Vertices);
        }
        mesh.Triangles = outTris;
        if (materials != null)
            mesh.Materials = [.. array.Select(r => materials[r.Triangle])];
        OrthographicAreas(mesh);
        (mesh.Flags, mesh.DebugFlags) = Flags(mesh);
        return mesh;
    }

    // FUN_1801a4070: the node for records [start, start + count), then its subtrees.
    private static int Build(Record[] records, int start, int count, List<RnMesh.Node> nodes)
    {
        if (count < 5)
            return Leaf(records, start, count, nodes);
        var split = Sah(records, start, count);
        if (split == null)
        {
            if (count < 9)
                return Leaf(records, start, count, nodes);
            split = Halve(records, start, count);
        }
        var s = split.Value;
        var index = nodes.Count;
        nodes.Add(default);
        Build(records, start, s.Left, nodes);
        var right = Build(records, start + s.Left, count - s.Left, nodes);
        var lo = new Vector3(s.RightMin.X <= s.LeftMin.X ? s.RightMin.X : s.LeftMin.X, s.RightMin.Y <= s.LeftMin.Y ? s.RightMin.Y : s.LeftMin.Y,
            s.RightMin.Z <= s.LeftMin.Z ? s.RightMin.Z : s.LeftMin.Z);
        var hi = new Vector3(s.LeftMax.X <= s.RightMax.X ? s.RightMax.X : s.LeftMax.X, s.LeftMax.Y <= s.RightMax.Y ? s.RightMax.Y : s.LeftMax.Y,
            s.LeftMax.Z <= s.RightMax.Z ? s.RightMax.Z : s.LeftMax.Z);
        nodes[index] = new RnMesh.Node(lo, (uint)(right - index) | ((uint)s.Axis << 30), hi, 0);
        return index;
    }

    private static int Leaf(Record[] records, int start, int count, List<RnMesh.Node> nodes)
    {
        var (lo, hi) = Bounds(records, start, count);
        nodes.Add(new RnMesh.Node(lo, 0xc0000000u | (uint)count, hi, (uint)start));
        return nodes.Count - 1;
    }

    private static (Vector3 Min, Vector3 Max) Bounds(Record[] records, int start, int count)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(-float.MaxValue);
        for (var i = start; i < start + count; i++)
        {
            var r = records[i];
            lo = new Vector3(r.Min.X <= lo.X ? r.Min.X : lo.X, r.Min.Y <= lo.Y ? r.Min.Y : lo.Y, r.Min.Z <= lo.Z ? r.Min.Z : lo.Z);
            hi = new Vector3(hi.X <= r.Max.X ? r.Max.X : hi.X, hi.Y <= r.Max.Y ? r.Max.Y : hi.Y, hi.Z <= r.Max.Z ? r.Max.Z : hi.Z);
        }
        return (lo, hi);
    }

    private static float Centre(Record r, int axis) => axis switch
    {
        0 => (r.Min.X + r.Max.X) * 0.5f,
        1 => (r.Min.Y + r.Max.Y) * 0.5f,
        _ => (r.Min.Z + r.Max.Z) * 0.5f,
    };

    private static float Get(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    // ((dz dx) + (dy dx)) + dz dy: half a box's surface.
    private static float Area(Vector3 lo, Vector3 hi)
    {
        float dx = hi.X - lo.X, dy = hi.Y - lo.Y, dz = hi.Z - lo.Z;
        return ((dz * dx) + (dy * dx)) + (dz * dy);
    }

    /// <summary>
    /// FUN_1801aab10: centroids binned 32 ways per axis (an axis whose
    /// centroids span less than 1/16 is skipped), each split between bins
    /// priced ((L + L) nL) + ((R + R) nR); the cheapest strictly wins, and
    /// the records are partitioned in place.
    /// </summary>
    private static Split? Sah(Record[] records, int start, int count)
    {
        var cmin = new float[3];
        var cmax = new float[3];
        for (var axis = 0; axis < 3; axis++)
        {
            float lo = float.MaxValue, hi = -float.MaxValue;
            for (var i = start; i < start + count; i++)
            {
                var c = Centre(records[i], axis);
                if (c <= lo) lo = c;
                if (hi <= c) hi = c;
            }
            (cmin[axis], cmax[axis]) = (lo, hi);
        }
        var best = float.MaxValue;
        Split? result = null;
        var bestBin = -1;
        for (var axis = 0; axis < 3; axis++)
        {
            var half = (cmax[axis] - cmin[axis]) * 0.5f;
            if (!(0.03125f < half || half == 0.03125f))
                continue;
            var scale = 31.999996f / (cmax[axis] - cmin[axis]);
            var binCount = new int[32];
            var binMin = new Vector3[32];
            var binMax = new Vector3[32];
            Array.Fill(binMin, new Vector3(float.MaxValue));
            Array.Fill(binMax, new Vector3(-float.MaxValue));
            for (var i = start; i < start + count; i++)
            {
                var r = records[i];
                var bin = (int)((Centre(r, axis) - cmin[axis]) * scale);
                binCount[bin]++;
                binMin[bin] = new Vector3(r.Min.X <= binMin[bin].X ? r.Min.X : binMin[bin].X, r.Min.Y <= binMin[bin].Y ? r.Min.Y : binMin[bin].Y,
                    r.Min.Z <= binMin[bin].Z ? r.Min.Z : binMin[bin].Z);
                binMax[bin] = new Vector3(binMax[bin].X <= r.Max.X ? r.Max.X : binMax[bin].X, binMax[bin].Y <= r.Max.Y ? r.Max.Y : binMax[bin].Y,
                    binMax[bin].Z <= r.Max.Z ? r.Max.Z : binMax[bin].Z);
            }
            // Suffix boxes, from the last bin down.
            var sufCount = new int[33];
            var sufMin = new Vector3[33];
            var sufMax = new Vector3[33];
            sufMin[32] = new Vector3(float.MaxValue);
            sufMax[32] = new Vector3(-float.MaxValue);
            for (var b = 31; b >= 0; b--)
            {
                sufCount[b] = sufCount[b + 1] + binCount[b];
                sufMin[b] = Vector3.Min(sufMin[b + 1], binMin[b]);
                sufMax[b] = Vector3.Max(sufMax[b + 1], binMax[b]);
            }
            var leftCount = 0;
            var leftMin = new Vector3(float.MaxValue);
            var leftMax = new Vector3(-float.MaxValue);
            for (var k = 0; k < 31; k++)
            {
                leftCount += binCount[k];
                leftMin = Vector3.Min(leftMin, binMin[k]);
                leftMax = Vector3.Max(leftMax, binMax[k]);
                var rightCount = sufCount[k + 1];
                if (leftCount <= 0 || rightCount <= 0)
                    continue;
                var l = Area(leftMin, leftMax);
                var r = Area(sufMin[k + 1], sufMax[k + 1]);
                var cost = ((l + l) * leftCount) + ((r + r) * rightCount);
                if (!(best > cost))
                    continue;
                best = cost;
                bestBin = k;
                result = new Split(axis, leftCount, leftMin, leftMax, sufMin[k + 1], sufMax[k + 1]);
            }
        }
        if (result == null)
            return null;
        // Partition: a record whose bin is at or below the split swaps into the next left slot.
        var s = result.Value;
        var sc = 31.999996f / (cmax[s.Axis] - cmin[s.Axis]);
        var next = start;
        for (var i = start; i < start + count; i++)
        {
            if ((int)((Centre(records[i], s.Axis) - cmin[s.Axis]) * sc) <= bestBin)
            {
                (records[i], records[next]) = (records[next], records[i]);
                next++;
            }
        }
        return s;
    }

    // FUN_1801a93e0: halved by count, the axis of the union's widest half-extent.
    private static Split Halve(Record[] records, int start, int count)
    {
        var half = count / 2;
        var (lmin, lmax) = Bounds(records, start, half);
        var (rmin, rmax) = Bounds(records, start + half, count - half);
        var maxY = lmax.Y <= rmax.Y ? rmax.Y : lmax.Y;
        var maxZ = lmax.Z <= rmax.Z ? rmax.Z : lmax.Z;
        var maxX = lmax.X <= rmax.X ? rmax.X : lmax.X;
        var minX = rmin.X <= lmin.X ? rmin.X : lmin.X;
        var minY = rmin.Y <= lmin.Y ? rmin.Y : lmin.Y;
        var minZ = rmin.Z <= lmin.Z ? rmin.Z : lmin.Z;
        var hx = (maxX - minX) * 0.5f;
        var hy = (maxY - minY) * 0.5f;
        var hz = (maxZ - minZ) * 0.5f;
        int axis;
        if (hy <= hx)
            axis = hx < hz ? 2 : 0;
        else
            axis = hy < hz ? 2 : 1;
        return new Split(axis, half, lmin, lmax, rmin, rmax);
    }

    // A triangle turned by its edge lengths |b - a|, |c - b|, |a - c|, as RnMeshCreate turns it.
    private static (int, int, int) Rotate(int a, int b, int c, Vector3[] v)
    {
        static float Length(Vector3 p, Vector3 q)
        {
            float dx = q.X - p.X, dy = q.Y - p.Y, dz = q.Z - p.Z;
            return MathF.Sqrt(((dz * dz) + (dy * dy)) + (dx * dx));
        }
        var e0 = Length(v[a], v[b]);
        var e1 = Length(v[b], v[c]);
        var e2 = Length(v[c], v[a]);
        bool first = false, second = false;
        if (e1 < e0)
            first = true;
        else if (e1 < e2)
        {
            if (e1 <= e0)
                first = true;
            else
                second = true;
        }
        if (first)
        {
            if (e0 < e2)
                second = true;
            else
                return (c, a, b);
        }
        if (second && e1 <= e2 && e0 <= e2)
            return (b, c, a);
        return (a, b, c);
    }

    // FUN_1801673a0 over FUN_180165750's edge adjacency. The adjacency holds,
    // per triangle edge, the far vertex of the one other triangle on that
    // edge: -1 for an edge on one triangle, -2 for more than two. Any negative
    // entry means no flags, so the test is on edge use counts alone, winding
    // aside.
    private static (uint Flags, uint DebugFlags) Flags(RnMesh mesh)
    {
        if (mesh.Triangles.Length <= 3)
            return (0, 0);
        var uses = new Dictionary<(int, int), int>();
        foreach (var (a, b, c) in mesh.Triangles)
        {
            foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
            {
                var e = p < q ? (p, q) : (q, p);
                uses[e] = uses.GetValueOrDefault(e) + 1;
            }
        }
        if (uses.Values.Any(n => n != 2))
            return (0, 0);
        if (TouchesItself(mesh))
            return (0, 1);
        var centre = new Vector3((mesh.Max.X + mesh.Min.X) * 0.5f, (mesh.Max.Y + mesh.Min.Y) * 0.5f, (mesh.Max.Z + mesh.Min.Z) * 0.5f);
        var volume = 0f;
        foreach (var (a, b, c) in mesh.Triangles)
        {
            Vector3 p = mesh.Vertices[a], q = mesh.Vertices[b], r = mesh.Vertices[c];
            var v = Tetra(centre, p, q, r);
            float ez = q.Z - p.Z, ey = q.Y - p.Y, ex = q.X - p.X;
            float fy = r.Y - p.Y, fx = r.X - p.X, fz = r.Z - p.Z;
            var ny = (fx * ez) - (fz * ex);
            var nx = (fz * ey) - (fy * ez);
            var nz = (fy * ex) - (fx * ey);
            var inv = 1f / MathF.Sqrt(((nz * nz) + (ny * ny)) + (nx * nx));
            if (((((p.Z - centre.Z) * inv) * nz) + (((p.Y - centre.Y) * inv) * ny)) + (((p.X - centre.X) * inv) * nx) < 0f)
                v = -v;
            volume += v;
        }
        return (volume < 0f ? 3u : 1u, 0);
    }

    private static bool TouchesItself(RnMesh mesh)
    {
        var tris = mesh.Triangles;
        var v = mesh.Vertices;
        var lo = new Vector3[tris.Length];
        var hi = new Vector3[tris.Length];
        for (var i = 0; i < tris.Length; i++)
        {
            var (a, b, c) = tris[i];
            lo[i] = Vector3.Min(Vector3.Min(v[a], v[b]), v[c]);
            hi[i] = Vector3.Max(Vector3.Max(v[a], v[b]), v[c]);
        }
        for (var i = 0; i < tris.Length; i++)
        {
            for (var j = i + 1; j < tris.Length; j++)
            {
                var (a, b, c) = tris[i];
                var (d, e, f) = tris[j];
                if (a == d || a == e || a == f || b == d || b == e || b == f || c == d || c == e || c == f)
                    continue;
                if (lo[i].X > hi[j].X + 1e-6f || lo[j].X > hi[i].X + 1e-6f || lo[i].Y > hi[j].Y + 1e-6f || lo[j].Y > hi[i].Y + 1e-6f
                    || lo[i].Z > hi[j].Z + 1e-6f || lo[j].Z > hi[i].Z + 1e-6f)
                    continue;
                if (TriangleDistance([v[a], v[b], v[c]], [v[d], v[e], v[f]]) < 1.1920929e-07)
                    return true;
            }
        }
        return false;
    }

    // The distance between two triangles, in double: zero when an edge of
    // one crosses the other, else the least vertex-triangle or edge-edge gap.
    private static double TriangleDistance(Vector3[] p, Vector3[] q)
    {
        static Vector3D D(Vector3 x) => new(x.X, x.Y, x.Z);
        Vector3D[] a = [D(p[0]), D(p[1]), D(p[2])], b = [D(q[0]), D(q[1]), D(q[2])];
        for (var k = 0; k < 3; k++)
        {
            if (SegmentHitsTriangle(a[k], a[(k + 1) % 3], b) || SegmentHitsTriangle(b[k], b[(k + 1) % 3], a))
                return 0;
        }
        var best = double.MaxValue;
        for (var k = 0; k < 3; k++)
        {
            best = Math.Min(best, PointTriangle(a[k], b));
            best = Math.Min(best, PointTriangle(b[k], a));
            for (var m = 0; m < 3; m++)
                best = Math.Min(best, SegmentSegment(a[k], a[(k + 1) % 3], b[m], b[(m + 1) % 3]));
        }
        return best;
    }

    private readonly record struct Vector3D(double X, double Y, double Z)
    {
        public static Vector3D operator -(Vector3D a, Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3D operator *(Vector3D a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public double Dot(Vector3D b) => (X * b.X) + (Y * b.Y) + (Z * b.Z);
        public Vector3D Cross(Vector3D b) => new((Y * b.Z) - (Z * b.Y), (Z * b.X) - (X * b.Z), (X * b.Y) - (Y * b.X));
        public double Length => Math.Sqrt(Dot(this));
    }

    private static bool SegmentHitsTriangle(Vector3D s0, Vector3D s1, Vector3D[] t)
    {
        var n = (t[1] - t[0]).Cross(t[2] - t[0]);
        double d0 = n.Dot(s0 - t[0]), d1 = n.Dot(s1 - t[0]);
        if ((d0 > 0 && d1 > 0) || (d0 < 0 && d1 < 0) || d0 == d1)
            return false;
        var x = s0 + ((s1 - s0) * (d0 / (d0 - d1)));
        return PointTriangle(x, t) == 0;
    }

    private static double PointTriangle(Vector3D x, Vector3D[] t)
    {
        // Ericson, closest point on a triangle.
        Vector3D a = t[0], b = t[1], c = t[2];
        Vector3D ab = b - a, ac = c - a, ap = x - a;
        double d1 = ab.Dot(ap), d2 = ac.Dot(ap);
        if (d1 <= 0 && d2 <= 0) return (x - a).Length;
        var bp = x - b;
        double d3 = ab.Dot(bp), d4 = ac.Dot(bp);
        if (d3 >= 0 && d4 <= d3) return (x - b).Length;
        var vc = (d1 * d4) - (d3 * d2);
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return (x - (a + (ab * (d1 / (d1 - d3))))).Length;
        var cp = x - c;
        double d5 = ab.Dot(cp), d6 = ac.Dot(cp);
        if (d6 >= 0 && d5 <= d6) return (x - c).Length;
        var vb = (d5 * d2) - (d1 * d6);
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return (x - (a + (ac * (d2 / (d2 - d6))))).Length;
        var va = (d3 * d6) - (d5 * d4);
        if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) return (x - (b + ((c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)))))).Length;
        var denom = 1 / (va + vb + vc);
        return (x - (a + (ab * (vb * denom)) + (ac * (vc * denom)))).Length;
    }

    private static double SegmentSegment(Vector3D p1, Vector3D q1, Vector3D p2, Vector3D q2)
    {
        Vector3D d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
        double a = d1.Dot(d1), e = d2.Dot(d2), f = d2.Dot(r);
        double s, t;
        if (a <= 1e-30 && e <= 1e-30) return r.Length;
        if (a <= 1e-30) { s = 0; t = Math.Clamp(f / e, 0, 1); }
        else
        {
            var c = d1.Dot(r);
            if (e <= 1e-30) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
            else
            {
                var b = d1.Dot(d2);
                var denom = (a * e) - (b * b);
                s = denom != 0 ? Math.Clamp(((b * f) - (c * e)) / denom, 0, 1) : 0;
                t = ((b * s) + f) / e;
                if (t < 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
                else if (t > 1) { t = 1; s = Math.Clamp((b - c) / a, 0, 1); }
            }
        }
        return ((p1 + (d1 * s)) - (p2 + (d2 * t))).Length;
    }

    // FUN_18008d0b0: |det| / 6 of the tetrahedron.
    private static float Tetra(Vector3 o, Vector3 b, Vector3 c, Vector3 d)
    {
        var det = ((((d.X - o.X) * (c.Z - o.Z)) - ((d.Z - o.Z) * (c.X - o.X))) * (b.Y - o.Y))
            + ((((d.Y - o.Y) * (c.X - o.X)) - ((d.X - o.X) * (c.Y - o.Y))) * (b.Z - o.Z))
            + ((((d.Z - o.Z) * (c.Y - o.Y)) - ((c.Z - o.Z) * (d.Y - o.Y))) * (b.X - o.X));
        return MathF.Abs(det * 0.16666667f);
    }

    // FUN_180166ff0: each axis's larger of the summed positive and negative
    // projected areas, over the box face across it, capped at 1.
    private static void OrthographicAreas(RnMesh mesh)
    {
        if (mesh.Triangles.Length == 0)
        {
            mesh.OrthographicAreas = Vector3.One;
            mesh.SurfaceArea = 0f;
            return;
        }
        float total = 0f, posX = 0f, negX = 0f, posY = 0f, negY = 0f, posZ = 0f, negZ = 0f;
        foreach (var (a, b, c) in mesh.Triangles)
        {
            Vector3 p = mesh.Vertices[a], q = mesh.Vertices[b], r = mesh.Vertices[c];
            float ex = q.X - p.X, ey = q.Y - p.Y, ez = q.Z - p.Z;
            float fx = r.X - p.X, fy = r.Y - p.Y, fz = r.Z - p.Z;
            var ay = ((fx * ez) - (fz * ex)) * 0.5f;
            var ax = ((fz * ey) - (fy * ez)) * 0.5f;
            var az = ((fy * ex) - (fx * ey)) * 0.5f;
            total += MathF.Sqrt(((az * az) + (ay * ay)) + (ax * ax));
            posY += ay <= 0f ? 0f : ay;
            negY += -ay <= 0f ? 0f : -ay;
            posX += ax <= 0f ? 0f : ax;
            negX += -ax <= 0f ? 0f : -ax;
            posZ += az <= 0f ? 0f : az;
            negZ += -az <= 0f ? 0f : -az;
        }
        var x = posX <= negX ? negX : posX;
        var y = posY <= negY ? negY : posY;
        var z = posZ <= negZ ? negZ : posZ;
        var dz = mesh.Max.Z - mesh.Min.Z;
        var dx = mesh.Max.X - mesh.Min.X;
        var dy = mesh.Max.Y - mesh.Min.Y;
        mesh.SurfaceArea = total;
        x = (1f / (dz * dy)) * x;
        y = (1f / (dz * dx)) * y;
        z = (1f / (dy * dx)) * z;
        mesh.OrthographicAreas = new Vector3(1f <= x ? 1f : x, 1f <= y ? 1f : y, 1f <= z ? 1f : z);
    }
}
