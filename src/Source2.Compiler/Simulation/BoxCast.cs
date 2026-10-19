using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Source2.Compiler.Physics;

namespace Source2.Compiler.Simulation;

/// <summary>
/// vphysics2's box cast (vphysics2 20261015, code identical to 20260924 at these addresses): world vfn
/// 0x558 (180065360) -> CRnWorld::CastShape (1801f5080), shape type 2 -> CRnWorld::CastBox
/// (1801f29e0), every broadphase tree (1801d64a0), each shape's vfn 0x40. Written with the same SSE
/// operations lane for lane, as <see cref="MeshSweep"/> is.
/// </summary>
public static class BoxCast
{
    /// <summary>What a shape cast reports: whether it hit, the fraction, and whether the box started inside.</summary>
    public readonly record struct Hit(bool Found, float Fraction, bool StartOverlap);

    private static Vector128<float> L3(System.Numerics.Vector3 v) => Vector128.Create(v.X, v.Y, v.Z, 0f);

    private static Vector128<float> Splat(Vector128<float> v, byte lane) => lane switch
    {
        0 => Sse.Shuffle(v, v, 0x00),
        1 => Sse.Shuffle(v, v, 0x55),
        _ => Sse.Shuffle(v, v, 0xaa),
    };

    private static readonly Vector128<float> AbsMask = Vector128.Create(0x7fffffffu).AsSingle();

    /// <summary>
    /// The swept box against a box grown by its own extents (the node and triangle tests of
    /// 180245a10): the cross-axis separations of the segment from <paramref name="p"/> along
    /// <paramref name="d"/> within the projected radius on all three axes.
    /// </summary>
    private static bool Crosses(Vector128<float> max, Vector128<float> min, Vector128<float> p, Vector128<float> d0x12, Vector128<float> d09,
                                Vector128<float> absC9, Vector128<float> absD2)
    {
        var c = Sse.Multiply(Sse.Add(max, min), Vector128.Create(0.5f));
        var e = Sse.Subtract(max, c);
        var rel = Sse.Subtract(p, c);
        var r = Sse.Add(Sse.Multiply(Sse.Shuffle(e, e, 0xd2), absC9), Sse.Multiply(Sse.Shuffle(e, e, 0xc9), absD2));
        var x = Sse.Subtract(Sse.Multiply(Sse.Shuffle(rel, rel, 0x12), d09), Sse.Multiply(Sse.Shuffle(rel, rel, 9), d0x12));
        var test = Sse.CompareLessThanOrEqual(Sse.Subtract(Sse.And(x, AbsMask), r), Vector128<float>.Zero);
        return (Sse.MoveMask(test) & 7) == 7;
    }

    /// <summary>
    /// 180245a10: an unscaled mesh at the identity (CRnMeshShape's cast 1802421c0 takes it when the
    /// shape has no scale flag (+0xd1) and the transform is the identity). The BVH is walked from
    /// the root with a stack: a node is entered when its box grown by <paramref name="half"/> meets
    /// the box swept from <paramref name="start"/> to start + best * delta and passes the cross-axis
    /// test; an inner node goes to its first child (node + 1) first when that child's max minus the
    /// start is positive on the split axis, else to its second (node + children); a leaf's triangles
    /// are grown by half + 1/32, tested the same way, then cast (180244380; 180243770 for meshes with
    /// neighbour indices, which map meshes do not have). A result is kept when its fraction is below
    /// the best so far, or when it starts overlapping and the kept one does not; then the swept box
    /// shrinks to it, and a start overlap at fraction 0 ends the walk. A closed mesh (+0xb4 bit 0) that holds the start is hit at
    /// 0 (180248a30).
    /// </summary>
    public static Hit MeshUnscaled(RnMesh mesh, System.Numerics.Vector3 start, System.Numerics.Vector3 delta, System.Numerics.Vector3 half,
                                   float maxFraction = 1f)
    {
        var p = L3(start);
        var d = L3(delta);
        var h = L3(half);
        var hTri = Sse.Add(Vector128.Create(0.03125f), h);
        var best = maxFraction;
        var swept = Sse.Add(Sse.Multiply(Vector128.Create(best), d), p);
        var boxMin = Sse.Min(p, swept);
        var boxMax = Sse.Max(p, swept);
        var dd = Sse.Multiply(d, d);
        var lengthSq = Sse.Add(Sse.Add(Sse.Shuffle(dd, dd, 0), Sse.Shuffle(dd, dd, 0x55)), Sse.Shuffle(dd, dd, 0xaa));
        var length = Sse.Sqrt(lengthSq);
        var direction = Sse41.BlendVariable(Vector128<float>.Zero, Sse.Divide(d, length),
            Sse.CompareLessThan(Vector128.Create(0f, 0f, 0f, float.MaxValue), length));
        var absD = Sse.And(d, AbsMask);
        var absC9 = Sse.Shuffle(absD, absD, 0xc9);
        var absD2 = Sse.Shuffle(absD, absD, 0xd2);
        var d0x12 = Sse.Shuffle(d, d, 0x12);
        var d09 = Sse.Shuffle(d, d, 9);
        var found = false;
        var keptOverlap = false;
        if (mesh.Nodes.Length == 0)
            return new Hit(false, 1f, false);
        var stack = new List<int>();
        var node = 0;
        while (true)
        {
            var n = mesh.Nodes[node];
            var max = Sse.Add(L3(n.Max), h);
            var min = Sse.Subtract(L3(n.Min), h);
            var apart = Sse.Or(Sse.CompareLessThan(max, boxMin), Sse.CompareLessThan(boxMax, min));
            if ((Sse.MoveMask(apart) & 7) == 0 && Crosses(max, min, p, d0x12, d09, absC9, absD2))
            {
                var type = n.Children >> 30;
                var count = (int)(n.Children & 0x3fffffff);
                if (type != 3)
                {
                    var first = node + 1;
                    var second = node + count;
                    var axisGap = Sse.Subtract(L3(mesh.Nodes[first].Max), p).GetElement((int)type);
                    if (axisGap > 0f)
                    {
                        stack.Add(second);
                        node = first;
                    }
                    else
                    {
                        stack.Add(first);
                        node = second;
                    }
                    continue;
                }
                for (var k = 0; k < count; k++)
                {
                    var (ia, ib, ic) = mesh.Triangles[(int)n.TriangleOffset + k];
                    var v0 = L3(mesh.Vertices[ia]);
                    var v1 = L3(mesh.Vertices[ib]);
                    var v2 = L3(mesh.Vertices[ic]);
                    var tmax = Sse.Add(Sse.Max(Sse.Max(v0, v1), v2), hTri);
                    var tmin = Sse.Subtract(Sse.Min(Sse.Min(v0, v1), v2), hTri);
                    var off = Sse.Or(Sse.CompareLessThan(tmax, boxMin), Sse.CompareLessThan(boxMax, tmin));
                    if ((Sse.MoveMask(off) & 7) != 0 || !Crosses(tmax, tmin, p, d0x12, d09, absC9, absD2))
                        continue;
                    if (!MeshSweep.TriangleCast(p, direction, h, length, v0, v1, v2, out var f, out var overlap))
                        continue;
                    if (!(best > f) && !(overlap && !keptOverlap))
                        continue;
                    found = true;
                    keptOverlap = overlap;
                    best = f;
                    if (f == 0f && overlap)
                        return new Hit(true, best, keptOverlap);
                    var at = Sse.Add(Sse.Multiply(Vector128.Create(f), d), p);
                    boxMin = Sse.Min(p, at);
                    boxMax = Sse.Max(p, at);
                }
            }
            if (stack.Count == 0)
            {
                // 180245a10's end: a closed mesh holding the start (unit scale) is hit at 0.
                if ((mesh.Flags & 1) != 0 && best != 0f && InsideClosed(mesh, System.Numerics.Vector3.One, start))
                    return new Hit(true, 0f, true);
                return new Hit(found, found ? best : 1f, keptOverlap);
            }
            node = stack[^1];
            stack.RemoveAt(stack.Count - 1);
        }
    }

    /// <summary>
    /// 180244dc0: a mesh under a transform <paramref name="t"/> (12 floats: the 3x3 M row by row, then
    /// the translation) and a per-axis <paramref name="scale"/> (CRnMeshShape +0xb8). The cast's
    /// start and delta go into the mesh frame (L = M s + M (-t), each row's terms summed in
    /// vphysics2's order) and the box's half extents become |M| h there; the BVH is culled in that
    /// frame against nodes times the scale grown by those extents (the child order compares the
    /// child's max with L / scale). A leaf's triangles are moved to the world (the rows of
    /// 180241820: scale times M's rows, then t; row3 + (row2 z + (row1 y + row0 x))), boxed
    /// ((min - 1/32) - h, (max + 1/32) + h) against the world box swept to the best fraction
    /// (refreshed when a leaf is entered), and cast in the world (180244380). Acceptance and the
    /// shrinking box as in <see cref="MeshUnscaled"/>. Triangles with three wings take 180243770 (180243110),
    /// which writes another normal but the same fraction; a closed mesh holding the start is hit at 0
    /// (18024ac40).
    /// </summary>
    public static Hit MeshTransformed(RnMesh mesh, System.Numerics.Vector3 scale, float[] t, System.Numerics.Vector3 start,
                                      System.Numerics.Vector3 delta, System.Numerics.Vector3 half, float maxFraction = 1f)
    {
        float t0 = t[0], t1 = t[1], t2 = t[2], t3 = t[3], t4 = t[4], t5 = t[5], t6 = t[6], t7 = t[7], t8 = t[8];
        float n9 = -t[9], n10 = -t[10], n11 = -t[11];
        float sx = start.X, sy = start.Y, sz = start.Z;
        var lx = ((sy * t1 + sx * t0) + t2 * sz) + ((t1 * n10 + t0 * n9) + t2 * n11);
        var ly = ((n9 * t3 + t4 * n10) + t5 * n11) + (t5 * sz + (sy * t4 + sx * t3));
        var lz = ((n9 * t6 + t7 * n10) + t8 * n11) + (t8 * sz + (sy * t7 + t6 * sx));
        float dx = delta.X, dy = delta.Y, dz = delta.Z;
        var ldx = (t0 * dx + dy * t1) + t2 * dz;
        var ldy = t5 * dz + (dy * t4 + t3 * dx);
        var ldz = dz * t8 + (dy * t7 + t6 * dx);
        float hx = half.X, hy = half.Y, hz = half.Z;
        var lhx = (MathF.Abs(t0) * hx + MathF.Abs(t1) * hy) + MathF.Abs(t2) * hz;
        var lhy = MathF.Abs(t5) * hz + (MathF.Abs(t4) * hy + MathF.Abs(t3) * hx);
        var lhz = MathF.Abs(t8) * hz + (MathF.Abs(t7) * hy + MathF.Abs(t6) * hx);
        var L = Vector128.Create(lx, ly, lz, 0f);
        var LD = Vector128.Create(ldx, ldy, ldz, 0f);
        var H = Vector128.Create(lhx, lhy, lhz, 0f);
        var S = L3(scale);
        var LS = Sse.Divide(L, S);
        var best = maxFraction;
        var swept = Sse.Add(Sse.Multiply(LD, Vector128.Create(best)), L);
        var boxMin = Sse.Min(L, swept);
        var boxMax = Sse.Max(L, swept);
        var absLD = Sse.And(LD, AbsMask);
        var absC9 = Sse.Shuffle(absLD, absLD, 0xc9);
        var absD2 = Sse.Shuffle(absLD, absLD, 0xd2);
        var d0x12 = Sse.Shuffle(LD, LD, 0x12);
        var d09 = Sse.Shuffle(LD, LD, 9);

        var ws = L3(start);
        var wd = L3(delta);
        var wh = L3(half);
        var dd = Sse.Multiply(wd, wd);
        var length = Sse.Sqrt(Sse.Add(Sse.Add(Sse.Shuffle(dd, dd, 0), Sse.Shuffle(dd, dd, 0x55)), Sse.Shuffle(dd, dd, 0xaa)));
        var direction = Sse41.BlendVariable(Vector128<float>.Zero, Sse.Divide(wd, length),
            Sse.CompareLessThan(Vector128.Create(0f, 0f, 0f, float.MaxValue), length));
        var boxedAt = best;
        var wSwept = Sse.Add(Sse.Multiply(wd, Vector128.Create(best)), ws);
        var wMin = Sse.Min(ws, wSwept);
        var wMax = Sse.Max(ws, wSwept);
        var row0 = Sse.Multiply(Vector128.Create(scale.X), Vector128.Create(t0, t1, t2, 0f));
        var row1 = Sse.Multiply(Vector128.Create(scale.Y), Vector128.Create(t3, t4, t5, 0f));
        var row2 = Sse.Multiply(Vector128.Create(scale.Z), Vector128.Create(t6, t7, t8, 0f));
        var row3 = Vector128.Create(t[9], t[10], t[11], 0f);
        Vector128<float> World(System.Numerics.Vector3 v)
            => Sse.Add(row3, Sse.Add(Sse.Multiply(row2, Vector128.Create(v.Z)),
                                     Sse.Add(Sse.Multiply(row1, Vector128.Create(v.Y)), Sse.Multiply(row0, Vector128.Create(v.X)))));
        var margin = Vector128.Create(0.03125f);

        var found = false;
        var keptOverlap = false;
        if (mesh.Nodes.Length == 0)
            return new Hit(false, 1f, false);
        var stack = new List<int>();
        var node = 0;
        while (true)
        {
            var n = mesh.Nodes[node];
            var max = Sse.Add(Sse.Multiply(L3(n.Max), S), H);
            var min = Sse.Subtract(Sse.Multiply(L3(n.Min), S), H);
            var apart = Sse.Or(Sse.CompareLessThan(max, boxMin), Sse.CompareLessThan(boxMax, min));
            if ((Sse.MoveMask(apart) & 7) == 0 && Crosses(max, min, L, d0x12, d09, absC9, absD2))
            {
                var type = n.Children >> 30;
                var count = (int)(n.Children & 0x3fffffff);
                if (type != 3)
                {
                    var first = node + 1;
                    var second = node + count;
                    var axisGap = Sse.Subtract(L3(mesh.Nodes[first].Max), LS).GetElement((int)type);
                    if (axisGap > 0f)
                    {
                        stack.Add(second);
                        node = first;
                    }
                    else
                    {
                        stack.Add(first);
                        node = second;
                    }
                    continue;
                }
                if (boxedAt != best)
                {
                    wSwept = Sse.Add(Sse.Multiply(wd, Vector128.Create(best)), ws);
                    boxedAt = best;
                    wMin = Sse.Min(ws, wSwept);
                    wMax = Sse.Max(ws, wSwept);
                }
                for (var k = 0; k < count; k++)
                {
                    var (ia, ib, ic) = mesh.Triangles[(int)n.TriangleOffset + k];
                    var v0 = World(mesh.Vertices[ia]);
                    var v1 = World(mesh.Vertices[ib]);
                    var v2 = World(mesh.Vertices[ic]);
                    var tmin = Sse.Subtract(Sse.Subtract(Sse.Min(v0, Sse.Min(v1, v2)), margin), wh);
                    var tmax = Sse.Add(Sse.Add(Sse.Max(v0, Sse.Max(v1, v2)), margin), wh);
                    var off = Sse.Or(Sse.CompareLessThan(wMax, tmin), Sse.CompareLessThan(tmax, wMin));
                    if ((Sse.MoveMask(off) & 7) != 0)
                        continue;
                    if (!MeshSweep.TriangleCast(ws, direction, wh, length, v0, v1, v2, out var f, out var overlap))
                        continue;
                    if (!(best > f) && !(overlap && !keptOverlap))
                        continue;
                    found = true;
                    keptOverlap = overlap;
                    best = f;
                    if (f == 0f && overlap)
                        return new Hit(true, best, keptOverlap);
                    var at = Sse.Add(Sse.Multiply(LD, Vector128.Create(f)), L);
                    boxMin = Sse.Min(L, at);
                    boxMax = Sse.Max(L, at);
                }
            }
            if (stack.Count == 0)
            {
                // 18024ac40: a closed mesh holding the start (L, before the scale) is hit at 0.
                if ((mesh.Flags & 1) != 0 && best != 0f && InsideClosed(mesh, scale, new System.Numerics.Vector3(lx, ly, lz)))
                    return new Hit(true, 0f, true);
                return new Hit(found, found ? best : 1f, keptOverlap);
            }
            node = stack[^1];
            stack.RemoveAt(stack.Count - 1);
        }
    }

    /// <summary>
    /// 180248a30: whether a point (the cast's start in the mesh frame, before the scale) is inside a
    /// closed mesh. Outside the scaled bounds it is not. Of the three distances to the low faces the
    /// least is taken (ties to z, then y), the same for the high faces; the ray runs that far toward
    /// the low face when its distance is strictly less, else toward the high face (18023fe00, the
    /// mesh's ray cast); a hit whose unit normal faces along the ray, (d.y n.y + d.x n.x) + d.z n.z
    /// at least 0, puts the point inside. Meshes flagged +0xb4 bit 1 are never inside.
    /// </summary>
    internal static bool InsideClosed(RnMesh mesh, System.Numerics.Vector3 scale, System.Numerics.Vector3 p)
    {
        if (((mesh.Flags >> 1) & 1) != 0)
            return false;
        float loX = scale.X * mesh.Min.X, loY = scale.Y * mesh.Min.Y, loZ = scale.Z * mesh.Min.Z;
        float hiX = scale.X * mesh.Max.X, hiY = scale.Y * mesh.Max.Y, hiZ = scale.Z * mesh.Max.Z;
        if (p.X < loX || p.Y < loY || p.Z < loZ || hiX < p.X || hiY < p.Y || hiZ < p.Z)
            return false;
        float dlx = p.X - loX, dly = p.Y - loY, dlz = p.Z - loZ;
        float dhx = hiX - p.X, dhy = hiY - p.Y, dhz = hiZ - p.Z;
        static (int Axis, float Distance) Least(float x, float y, float z)
        {
            float ax = MathF.Abs(x), ay = MathF.Abs(y), az = MathF.Abs(z);
            if (ay > ax)
                return az > ax ? (0, x) : (2, z);
            return az > ay ? (1, y) : (2, z);
        }
        var (lowAxis, low) = Least(dlx, dly, dlz);
        var (highAxis, high) = Least(dhx, dhy, dhz);
        Span<float> dir = stackalloc float[3];
        if (high > low)
            dir[lowAxis] = -low;
        else
            dir[highAxis] = high;
        var d = new System.Numerics.Vector3(dir[0], dir[1], dir[2]);
        if (!MeshRay(mesh, scale, p, d, 1f, out var n))
            return false;
        return (d.Y * n.Y + d.X * n.X) + d.Z * n.Z >= 0f;
    }

    /// <summary>
    /// 18023fe00: a ray from <paramref name="p"/> by <paramref name="d"/> (mesh frame, both divided by
    /// the scale) through the BVH (no growth; the cross-axis test; the first child first when the
    /// ray's component on the split axis is positive); a triangle is crossed when the three edge
    /// crossings (from the edge midpoints) agree in sign, at t = ((v0 - o) . n) / (d . n) with n =
    /// (v2 - v0) x (v1 - v0) taken lane by lane, t not above 0 a miss and t clamped to 1; the least t
    /// below the best wins. The hit's unit normal is (v1 - v0) x (v2 - v0) of the scaled edges.
    /// </summary>
    internal static bool MeshRay(RnMesh mesh, System.Numerics.Vector3 scale, System.Numerics.Vector3 p, System.Numerics.Vector3 d,
                                 float maxFraction, out System.Numerics.Vector3 normal)
    {
        normal = default;
        if (mesh.Nodes.Length == 0)
            return false;
        var s = L3(scale);
        var o = Sse.Divide(L3(p), s);
        var dir = Sse.Divide(L3(d), s);
        var best = maxFraction;
        var end = Sse.Add(Sse.Multiply(dir, Vector128.Create(best)), o);
        var boxMin = Sse.Min(o, end);
        var boxMax = Sse.Max(o, end);
        var absD = Sse.And(dir, AbsMask);
        var absC9 = Sse.Shuffle(absD, absD, 0xc9);
        var absD2 = Sse.Shuffle(absD, absD, 0xd2);
        var d0x12 = Sse.Shuffle(dir, dir, 0x12);
        var d09 = Sse.Shuffle(dir, dir, 9);
        var dX = Sse.Shuffle(dir, dir, 0);
        var dY = Sse.Shuffle(dir, dir, 0x55);
        var dZ = Sse.Shuffle(dir, dir, 0xaa);
        var half = Vector128.Create(0.5f);
        var bestTri = -1;
        var stack = new List<int>();
        var node = 0;
        while (true)
        {
            var n = mesh.Nodes[node];
            var max = L3(n.Max);
            var min = L3(n.Min);
            var apart = Sse.Or(Sse.CompareLessThan(max, boxMin), Sse.CompareLessThan(boxMax, min));
            if ((Sse.MoveMask(apart) & 7) == 0 && Crosses(max, min, o, d0x12, d09, absC9, absD2))
            {
                var type = n.Children >> 30;
                var count = (int)(n.Children & 0x3fffffff);
                if (type != 3)
                {
                    if (dir.GetElement((int)type) > 0f)
                    {
                        stack.Add(node + count);
                        node += 1;
                    }
                    else
                    {
                        stack.Add(node + 1);
                        node += count;
                    }
                    continue;
                }
                for (var k = 0; k < count; k++)
                {
                    var tri = (int)n.TriangleOffset + k;
                    var (ia, ib, ic) = mesh.Triangles[tri];
                    var v0 = L3(mesh.Vertices[ia]);
                    var v1 = L3(mesh.Vertices[ib]);
                    var v2 = L3(mesh.Vertices[ic]);
                    var e01 = Sse.Subtract(v1, v0);
                    var e20 = Sse.Subtract(v0, v2);
                    var e12 = Sse.Subtract(v2, v1);
                    var m12 = Sse.Subtract(Sse.Multiply(Sse.Add(v2, v1), half), o);
                    var m02 = Sse.Subtract(Sse.Multiply(Sse.Add(v2, v0), half), o);
                    var m01 = Sse.Subtract(Sse.Multiply(Sse.Add(v1, v0), half), o);
                    var c12 = Sse.Subtract(Sse.Multiply(Sse.Shuffle(m12, m12, 0x12), Sse.Shuffle(e12, e12, 9)),
                                           Sse.Multiply(Sse.Shuffle(m12, m12, 9), Sse.Shuffle(e12, e12, 0x12)));
                    var c20 = Sse.Subtract(Sse.Multiply(Sse.Shuffle(m02, m02, 0x12), Sse.Shuffle(e20, e20, 9)),
                                           Sse.Multiply(Sse.Shuffle(m02, m02, 9), Sse.Shuffle(e20, e20, 0x12)));
                    var c01 = Sse.Subtract(Sse.Multiply(Sse.Shuffle(m01, m01, 0x12), Sse.Shuffle(e01, e01, 9)),
                                           Sse.Multiply(Sse.Shuffle(m01, m01, 9), Sse.Shuffle(e01, e01, 0x12)));
                    var lo = Sse.UnpackLow(c12, c20);
                    var hi = Sse.UnpackHigh(c12, c20);
                    var xs = Sse.Shuffle(lo, c01, 4);
                    var ys = Sse.Shuffle(lo, c01, 0x5e);
                    var zs = Sse.Shuffle(hi, c01, 0xa4);
                    var side = Sse.Add(Sse.Multiply(dZ, zs), Sse.Add(Sse.Multiply(dY, ys), Sse.Multiply(dX, xs)));
                    float t;
                    if ((Sse.MoveMask(Sse.CompareLessThan(side, Vector128<float>.Zero)) & 7) != 0
                        && (Sse.MoveMask(Sse.CompareLessThan(Vector128<float>.Zero, side)) & 7) != 0)
                        t = 1f;
                    else
                    {
                        var e02 = Sse.Subtract(v2, v0);
                        var nn = Sse.Subtract(Sse.Multiply(Sse.Shuffle(e02, e02, 0x12), Sse.Shuffle(e01, e01, 9)),
                                              Sse.Multiply(Sse.Shuffle(e02, e02, 9), Sse.Shuffle(e01, e01, 0x12)));
                        var num = Sse.Multiply(Sse.Subtract(v0, o), nn);
                        var den = Sse.Multiply(nn, dir);
                        var numS = Sse.Add(Sse.Add(Sse.Shuffle(num, num, 0), Sse.Shuffle(num, num, 0x55)), Sse.Shuffle(num, num, 0xaa));
                        var denS = Sse.Add(Sse.Add(Sse.Shuffle(den, den, 0), Sse.Shuffle(den, den, 0x55)), Sse.Shuffle(den, den, 0xaa));
                        var tv = Sse.Divide(numS, denS);
                        t = (Sse.MoveMask(Sse.CompareLessThanOrEqual(tv, Vector128<float>.Zero)) & 7) != 0
                            ? 1f : Sse.Min(tv, Vector128.Create(1f)).GetElement(0);
                    }
                    if (!(best > t))
                        continue;
                    best = t;
                    bestTri = tri;
                    var at = Sse.Add(Sse.Multiply(dir, Vector128.Create(t)), o);
                    boxMin = Sse.Min(o, at);
                    boxMax = Sse.Max(o, at);
                }
            }
            if (stack.Count == 0)
                break;
            node = stack[^1];
            stack.RemoveAt(stack.Count - 1);
        }
        if (!(maxFraction > best) || bestTri < 0)
            return false;
        var (a, b, c) = mesh.Triangles[bestTri];
        System.Numerics.Vector3 va = mesh.Vertices[a], vb = mesh.Vertices[b], vc = mesh.Vertices[c];
        float e1x = (vb.X - va.X) * scale.X, e2x = (vc.X - va.X) * scale.X;
        float e2z = (vc.Z - va.Z) * scale.Z, e2y = (vc.Y - va.Y) * scale.Y;
        float e1y = (vb.Y - va.Y) * scale.Y, e1z = (vb.Z - va.Z) * scale.Z;
        var ny = e2x * e1z - e2z * e1x;
        var nx = e2z * e1y - e2y * e1z;
        var nz = e2y * e1x - e2x * e1y;
        var len = MathF.Sqrt(nz * nz + ny * ny + nx * nx);
        var inv = 1f / len;
        normal = new System.Numerics.Vector3(nx * inv, ny * inv, nz * inv);
        return true;
    }
}
