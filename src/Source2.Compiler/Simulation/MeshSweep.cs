using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Source2.Compiler.Physics;

namespace Source2.Compiler.Simulation;

/// <summary>
/// The swept box query the mesh time of impact runs (FUN_180249e90 with its
/// triangle test FUN_180244380). Both are SSE code that uses rcpps and
/// rsqrtps, whose results the CPU defines; they are written here with the
/// same instructions, lane for lane, so they agree with vphysics2 on the
/// machine they run on.
/// </summary>
public static class MeshSweep
{
    private static readonly Vector128<float> AbsMask = Vector128.Create(0x7fffffffu).AsSingle();

    private static Vector128<float> L3(Vec3 v) => Vector128.Create(v.X, v.Y, v.Z, 0f);

    private static Vector128<float> L3(System.Numerics.Vector3 v) => Vector128.Create(v.X, v.Y, v.Z, 0f);

    private static Vector128<float> Splat(Vector128<float> v, byte lane) => lane switch
    {
        0 => Sse.Shuffle(v, v, 0x00),
        1 => Sse.Shuffle(v, v, 0x55),
        _ => Sse.Shuffle(v, v, 0xaa),
    };

    private static Vector128<float> Abs(Vector128<float> v) => Sse.And(v, AbsMask);

    private static float Lane(Vector128<float> v, int i) => v.GetElement(i);

    /// <summary>
    /// FUN_180249e90: the triangles a box of half extents <paramref name="half"/>
    /// meets while its centre moves from <paramref name="start"/> by
    /// <paramref name="delta"/> (all in scaled mesh space, divided by the scale
    /// through a refined rcpps), walking the BVH front to back along the
    /// motion. With <paramref name="boxesOnly"/> false each triangle whose
    /// grown box passes also goes through <see cref="TriangleHit"/>.
    /// </summary>
    public static void Query(RnMesh mesh, Vec3 scale, Vec3 start, Vec3 delta, Vec3 half, bool boxesOnly, List<int> output)
    {
        var s = L3(scale);
        var r = Sse.Reciprocal(s);
        var inverse = Sse.Subtract(Sse.Add(r, r), Sse.Multiply(Sse.Multiply(r, r), s));
        var d = Sse.Multiply(L3(delta), inverse);
        var p = Sse.Multiply(L3(start), inverse);
        var h = Sse.Multiply(L3(half), inverse);
        var end = Sse.Add(d, p);
        var boxMin = Sse.Min(p, end);
        var boxMax = Sse.Max(p, end);
        var dd = Sse.Multiply(d, d);
        var length = Sse.Sqrt(Sse.Add(Sse.Add(Splat(dd, 0), Splat(dd, 1)), Splat(dd, 2)));
        var direction = Sse41.BlendVariable(Vector128<float>.Zero, Sse.Divide(d, length),
            Sse.CompareLessThan(Vector128.Create(0f, 0f, 0f, float.MaxValue), length));
        if (mesh.Nodes.Length == 0)
            return;
        var absD = Abs(d);
        var margin = Vector128.Create(0.03125f);
        var stack = new List<int>();
        var node = 0;
        while (true)
        {
            var n = mesh.Nodes[node];
            var max = Sse.Add(L3(n.Max), h);
            var min = Sse.Subtract(L3(n.Min), h);
            var apart = Sse.Or(Sse.CompareLessThan(max, boxMin), Sse.CompareLessThan(boxMax, min));
            if ((Sse.MoveMask(apart) & 7) == 0 && Crosses(max, min, p, d, absD))
            {
                var type = n.Children >> 30;
                var children = (int)(n.Children & 0x3fffffff);
                if (type != 3)
                {
                    if (Component(delta, (int)type) > 0f)
                    {
                        stack.Add(node + children);
                        node += 1;
                    }
                    else
                    {
                        stack.Add(node + 1);
                        node += children;
                    }
                    continue;
                }
                var t = (int)n.TriangleOffset;
                for (var k = children; k > 0; k--, t++)
                {
                    var (ia, ib, ic) = mesh.Triangles[t];
                    var v0 = L3(mesh.Vertices[ia]);
                    var v1 = L3(mesh.Vertices[ib]);
                    var v2 = L3(mesh.Vertices[ic]);
                    var lo = Sse.Min(v0, Sse.Min(v1, v2));
                    var hi = Sse.Max(v0, Sse.Max(v1, v2));
                    lo = Sse.Subtract(Sse.Subtract(lo, margin), h);
                    hi = Sse.Add(Sse.Add(margin, hi), h);
                    var off = Sse.Or(Sse.CompareLessThan(boxMax, lo), Sse.CompareLessThan(hi, boxMin));
                    if ((Sse.MoveMask(off) & 7) != 0)
                        continue;
                    if (!boxesOnly && !TriangleHit(p, direction, h, length, v0, v1, v2))
                        continue;
                    output.Add(t);
                }
            }
            if (stack.Count == 0)
                return;
            node = stack[^1];
            stack.RemoveAt(stack.Count - 1);
        }
    }

    private static float Component(Vec3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    /// <summary>The segment against the grown node box: |rel x d| within the box's projected radius on the three cross axes.</summary>
    private static bool Crosses(Vector128<float> max, Vector128<float> min, Vector128<float> p, Vector128<float> d, Vector128<float> absD)
    {
        var c = Sse.Multiply(Vector128.Create(0.5f), Sse.Add(max, min));
        var e = Sse.Subtract(max, c);
        var rel = Sse.Subtract(p, c);
        var a0 = Sse.Shuffle(absD, absD, 0xc9);
        var a1 = Sse.Shuffle(absD, absD, 0xd2);
        var r = Sse.Add(Sse.Multiply(Sse.Shuffle(e, e, 0xd2), a0), Sse.Multiply(Sse.Shuffle(e, e, 0xc9), a1));
        var x = Sse.Subtract(Sse.Multiply(Sse.Shuffle(rel, rel, 0x12), Sse.Shuffle(d, d, 9)),
                             Sse.Multiply(Sse.Shuffle(rel, rel, 9), Sse.Shuffle(d, d, 0x12)));
        var test = Sse.CompareLessThanOrEqual(Sse.Subtract(Abs(x), r), Vector128<float>.Zero);
        return (Sse.MoveMask(test) & 7) == 7;
    }

    /// <summary>
    /// FUN_180244380's verdict (the axis and normal it also writes are not
    /// used by the query): whether a box of half extents <paramref name="h"/>
    /// moving from <paramref name="p"/> along <paramref name="direction"/> for
    /// <paramref name="length"/> meets the triangle before the end. Twenty axes,
    /// four at a time: the normal and the box axes, the three box axis cross
    /// edge families, and edge cross motion; each gives an entry and exit time
    /// with a margin of 1/32 of the axis length.
    /// </summary>
    public static bool TriangleHit(Vector128<float> p, Vector128<float> direction, Vector128<float> h, Vector128<float> length,
                                   Vector128<float> v0, Vector128<float> v1, Vector128<float> v2)
    {
        var zero = Vector128<float>.Zero;
        var a = Sse.Subtract(p, v0);
        var e2 = Sse.Subtract(v2, v0);
        var e1 = Sse.Subtract(v1, v0);
        var ne2 = Sse.Subtract(zero, e2);
        var e3 = Sse.Subtract(e2, e1);

        var n = Sse.Subtract(Sse.Multiply(Sse.Shuffle(e1, e1, 0x12), Sse.Shuffle(ne2, ne2, 9)),
                             Sse.Multiply(Sse.Shuffle(e1, e1, 9), Sse.Shuffle(ne2, ne2, 0x12)));
        var nn2 = Sse.Multiply(n, n);
        var nLength = Sse.Sqrt(Sse.Add(Sse.Add(Splat(nn2, 0), Splat(nn2, 1)), Splat(nn2, 2)));
        var unit = Sse41.BlendVariable(zero, Sse.Divide(n, nLength),
            Sse.CompareLessThan(Vector128.Create(0f, 0f, 0f, float.MaxValue), nLength));

        // The edges, one per lane: e1, e2 - e1, -e2, e1 again.
        var x9 = Sse.UnpackLow(e1, e3);
        var x8 = Sse.UnpackHigh(e1, e3);
        var x0 = Sse.UnpackLow(ne2, e1);
        var ex = Sse.Shuffle(x9, x0, 0x44);
        var ey = Sse.Shuffle(x9, x0, 0xee);
        var x7 = Sse.UnpackHigh(ne2, e1);
        var ez = Sse.Shuffle(x8, x7, 0x44);

        var unitX = Vector128.Create(1f, 0f, 0f, 0f);
        var unitY = Vector128.Create(0f, 1f, 0f, 0f);
        var unitZ = Vector128.Create(0f, 0f, 1f, 0f);
        var t4 = Sse.UnpackLow(unit, unitX);
        var t2 = Sse.UnpackLow(unitY, unitZ);
        var t5 = Sse.UnpackHigh(unit, unitX);
        var t3 = Sse.UnpackHigh(unitY, unitZ);
        var axes = new Vector128<float>[15];
        axes[0] = Sse.Shuffle(t4, t2, 0x44);
        axes[1] = Sse.Shuffle(t4, t2, 0xee);
        axes[2] = Sse.Shuffle(t5, t3, 0x44);
        axes[3] = Sse.Subtract(zero, ey);
        axes[4] = ex;
        axes[5] = zero;
        axes[6] = ez;
        axes[7] = zero;
        axes[8] = Sse.Subtract(zero, ex);
        axes[9] = zero;
        axes[10] = Sse.Subtract(zero, ez);
        axes[11] = ey;

        var dx = Splat(direction, 0);
        var dy = Splat(direction, 1);
        var dz = Splat(direction, 2);
        var cz = Sse.Subtract(Sse.Multiply(dy, ex), Sse.Multiply(dx, ey));
        var cy = Sse.Subtract(Sse.Multiply(dx, ez), Sse.Multiply(dz, ex));
        var cx = Sse.Subtract(Sse.Multiply(dz, ey), Sse.Multiply(dy, ez));
        var c2 = Sse.Add(Sse.Multiply(cz, cz), Sse.Add(Sse.Multiply(cy, cy), Sse.Multiply(cx, cx)));
        var rs = Sse.ReciprocalSqrt(c2);
        rs = Sse.Multiply(Sse.Multiply(rs, Sse.Subtract(Vector128.Create(3f), Sse.Multiply(Sse.Multiply(rs, rs), c2))), Vector128.Create(0.5f));
        var motion = Sse.Multiply(Splat(length, 0), direction);
        axes[12] = Sse.Multiply(cx, rs);
        axes[13] = Sse.Multiply(cy, rs);
        axes[14] = Sse.Multiply(rs, cz);

        var b = Sse.Add(motion, a);
        Vector128<float> ax = Splat(a, 0), ay = Splat(a, 1), az = Splat(a, 2);
        Vector128<float> bx = Splat(b, 0), by = Splat(b, 1), bz = Splat(b, 2);
        Vector128<float> e1x = Splat(e1, 0), e1y = Splat(e1, 1), e1z = Splat(e1, 2);
        Vector128<float> e2x = Splat(e2, 0), e2y = Splat(e2, 1), e2z = Splat(e2, 2);
        Vector128<float> hx = Splat(h, 0), hy = Splat(h, 1), hz = Splat(h, 2);

        var overlapping = true;
        var minExit = Vector128.Create(float.MaxValue);
        var maxEnter = Vector128.Create(-float.MaxValue);
        for (var g = 0; g < 5; g++)
        {
            var AX = axes[3 * g];
            var AY = axes[3 * g + 1];
            var AZ = axes[3 * g + 2];
            var s0 = Sse.Add(Sse.Multiply(az, AZ), Sse.Add(Sse.Multiply(ay, AY), Sse.Multiply(ax, AX)));
            var s1 = Sse.Add(Sse.Multiply(bz, AZ), Sse.Add(Sse.Multiply(by, AY), Sse.Multiply(bx, AX)));
            var p1 = Sse.Add(Sse.Multiply(e1z, AZ), Sse.Add(Sse.Multiply(e1y, AY), Sse.Multiply(e1x, AX)));
            var p2 = Sse.Add(Sse.Multiply(e2z, AZ), Sse.Add(Sse.Multiply(e2y, AY), Sse.Multiply(e2x, AX)));
            var radius = Sse.Add(Abs(Sse.Multiply(hz, AZ)), Sse.Add(Abs(Sse.Multiply(hy, AY)), Abs(Sse.Multiply(hx, AX))));
            var axisLength = Sse.Sqrt(Sse.Add(Sse.Add(Sse.Multiply(AY, AY), Sse.Multiply(AZ, AZ)), Sse.Multiply(AX, AX)));
            var lo = Sse.Subtract(Sse.Min(zero, Sse.Min(p1, p2)), radius);
            var hi = Sse.Add(Sse.Max(zero, Sse.Max(p1, p2)), radius);
            var slack = Sse.Multiply(axisLength, Vector128.Create(0.03125f));
            var ds = Sse.Subtract(s1, s0);
            var tiny = Sse.And(Sse.CompareLessThan(Abs(ds), Vector128.Create(1.17549435e-38f)), Vector128.Create(1.1920929e-07f));
            var below = Sse.CompareLessThan(s0, lo);
            var above = Sse.CompareLessThan(hi, s0);
            var inverseDs = Sse.Divide(Vector128.Create(1f), Sse.Or(tiny, ds));
            if (Sse.MoveMask(Sse.Or(below, above)) != 0)
            {
                var inner = Sse.Multiply(slack, Vector128.Create(0.875f));
                overlapping = false;
                var leaves = Sse.Or(
                    Sse.Or(Sse.And(Sse.CompareLessThanOrEqual(s0, s1), above), Sse.And(Sse.CompareLessThanOrEqual(s1, s0), below)),
                    Sse.Or(Sse.And(Sse.CompareLessThanOrEqual(Sse.Add(inner, hi), s1), above),
                           Sse.And(Sse.CompareLessThanOrEqual(s1, Sse.Subtract(lo, inner)), below)));
                if (Sse.MoveMask(leaves) != 0)
                    return false;
            }
            var moving = Sse.CompareLessThan(Vector128.Create(1.1920929e-07f), Abs(ds));
            var tHi = Sse.Multiply(inverseDs, Sse.Subtract(Sse.Add(hi, slack), s0));
            var tLo = Sse.Multiply(inverseDs, Sse.Subtract(Sse.Subtract(lo, slack), s0));
            var enter = Sse41.BlendVariable(Vector128.Create(-float.MaxValue), Sse.Min(tLo, tHi), moving);
            var exit = Sse41.BlendVariable(Vector128.Create(float.MaxValue), Sse.Max(tLo, tHi), moving);
            if (Sse.MoveMask(Sse.CompareLessThan(exit, enter)) != 0)
                return false;
            minExit = Sse.Min(exit, minExit);
            maxEnter = Sse.Max(enter, maxEnter);
            if (Sse.MoveMask(Sse.CompareLessThanOrEqual(minExit, maxEnter)) != 0)
                return false;
        }
        var m = Sse.Min(minExit, Sse.Shuffle(minExit, minExit, 0x39));
        m = Sse.Min(m, Sse.Shuffle(m, m, 0x4e));
        var e = Sse.Max(maxEnter, Sse.Shuffle(maxEnter, maxEnter, 0x39));
        e = Sse.Max(e, Sse.Shuffle(e, e, 0x4e));
        var tEnter = 0f > Lane(e, 0) ? 0f : Lane(e, 0);
        if (tEnter >= Lane(m, 0))
            return false;
        if (tEnter >= 1f)
            return false;
        var len = Lane(length, 0);
        return len != 0f || overlapping;
    }
}
