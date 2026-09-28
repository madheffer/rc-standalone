using System.Runtime.CompilerServices;
using Source2.Compiler.Physics;

namespace Source2.Compiler.Simulation;

public static partial class RoundCollision
{
    /// <summary>
    /// FUN_1802f49d0: sphere against one mesh triangle {v0, v1, v2} at xfB.
    /// The point of the triangle nearest the centre (FUN_18008b720); no
    /// touch past the radius plus 1/16, which clears the triangle's cache.
    /// The normal runs from the centre to that point, or, with the centre on
    /// the triangle, is the triangle's (e2 - e0) x (e1 - e0) order of
    /// products. One new point, sub-shape the triangle, byte 0x64 the
    /// Voronoi region.
    /// </summary>
    public static bool SphereTriangle(ref CachedManifold result, in RnTransform xfA, in Sphere a, in RnTransform xfB,
                                      Vec3 v0, Vec3 v1, Vec3 v2, ref MeshTriangleCache cache, int triangle)
    {
        ref readonly var ra = ref xfA.R;
        ref readonly var rb = ref xfB.R;
        var c = a.Centre;
        var cx = ((c.X * ra.M0) + (c.Y * ra.M3)) + (c.Z * ra.M6) + xfA.T.X;
        var cz = ((c.X * ra.M2) + (c.Y * ra.M5)) + (c.Z * ra.M8) + xfA.T.Z;
        var cy = ((c.X * ra.M1) + (c.Y * ra.M4)) + (c.Z * ra.M7) + xfA.T.Y;
        var w0 = ThroughYX(xfB, v0);
        var w1 = ThroughYX(xfB, v1);
        var w2 = ThroughYX(xfB, v2);
        var q = ClosestOnTriangle(w0, w1, w2, new Vec3(cx, cy, cz), out var region);
        float dx = q.X - cx, dy = q.Y - cy, dz = q.Z - cz;
        var distance = MathF.Sqrt(((dx * dx) + (dy * dy)) + (dz * dz));
        var r = a.Radius;
        if (!(distance <= r + 0.0625f))
        {
            cache = default;
            return false;
        }
        Vec3 n;
        if (distance * distance <= 1.17549435e-35f)
        {
            var fx = ((w2.Y - w0.Y) * (w1.Z - w0.Z)) - ((w2.Z - w0.Z) * (w1.Y - w0.Y));
            var fy = ((w1.X - w0.X) * (w2.Z - w0.Z)) - ((w2.X - w0.X) * (w1.Z - w0.Z));
            var fz = ((w2.X - w0.X) * (w1.Y - w0.Y)) - ((w1.X - w0.X) * (w2.Y - w0.Y));
            n = Unit(fx, fy, fz, ((fy * fy) + (fx * fx)) + (fz * fz));
        }
        else
            n = Unit(dx, dy, dz, ((dx * dx) + (dy * dy)) + (dz * dz));
        var px = (n.X * r) + cx;
        var py = (n.Y * r) + cy;
        var pz = (n.Z * r) + cz;
        result.Centre = new Vec3(px, py, pz);
        result.Normal = n;
        (result.T1, result.T2) = InlineTangents(n);
        result.TwistImpulse = 0f;
        result.Impulse1 = 0f;
        result.Impulse2 = 0f;
        result.PointCount = 1;
        float ax = px - xfA.T.X, ay = py - xfA.T.Y, az = pz - xfA.T.Z;
        result.P0.LocalA = new Vec3(((ax * ra.M0) + (ay * ra.M1)) + (az * ra.M2),
                                    ((ax * ra.M3) + (ay * ra.M4)) + (az * ra.M5),
                                    ((ay * ra.M7) + (ax * ra.M6)) + (az * ra.M8));
        float bx = q.X - xfB.T.X, bz = q.Z - xfB.T.Z, by = q.Y - xfB.T.Y;
        result.P0.LocalB = new Vec3(((by * rb.M1) + (bx * rb.M0)) + (bz * rb.M2),
                                    ((bx * rb.M3) + (by * rb.M4)) + (bz * rb.M5),
                                    ((bx * rb.M6) + (by * rb.M7)) + (bz * rb.M8));
        result.P0.Impulse = 0f;
        result.P0.Feature = 0;
        result.P0.SubShape = triangle;
        ref var flags = ref Unsafe.As<int, byte>(ref result.P0.Reserved);
        flags = (byte)region;
        Unsafe.Add(ref flags, 1) = 1;
        return true;
    }

    /// <summary>n = d / |d| from its length squared, or zero at most 1.17549435e-35.</summary>
    private static Vec3 Unit(float x, float y, float z, float length2)
    {
        if (length2 <= 1.17549435e-35f)
            return default;
        var inverse = 1f / MathF.Sqrt(length2);
        return new Vec3(inverse * x, inverse * y, inverse * z);
    }

    /// <summary>
    /// FUN_18008b720: the point of triangle {a, b, c} nearest p, by Voronoi
    /// region (1-3 the vertices, 4 ab, 6 ac, 5 bc, 7 the face).
    /// </summary>
    internal static Vec3 ClosestOnTriangle(Vec3 a, Vec3 b, Vec3 c, Vec3 p, out int region)
    {
        float abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
        float acx = c.X - a.X, acy = c.Y - a.Y, acz = c.Z - a.Z;
        float apx = p.X - a.X, apy = p.Y - a.Y, apz = p.Z - a.Z;
        var d2 = ((apy * acy) + (apz * acz)) + (apx * acx);
        var d1 = ((apz * abz) + (apy * aby)) + (apx * abx);
        if (!(0f < d1 || 0f < d2))
        {
            region = 1;
            return a;
        }
        float bpx = p.X - b.X, bpy = p.Y - b.Y, bpz = p.Z - b.Z;
        var d3 = ((bpz * abz) + (bpy * aby)) + (bpx * abx);
        var d4 = ((bpz * acz) + (bpy * acy)) + (bpx * acx);
        if (!(d3 < 0f || d3 < d4))
        {
            region = 2;
            return b;
        }
        var vc = (d4 * d1) - (d3 * d2);
        if (!(0f < vc || d1 < 0f || 0f < d3))
        {
            region = 4;
            var v = d1 / (d1 - d3);
            return new Vec3((v * abx) + a.X, (v * aby) + a.Y, (v * abz) + a.Z);
        }
        float cpx = p.X - c.X, cpy = p.Y - c.Y, cpz = p.Z - c.Z;
        var d6 = ((cpz * acz) + (cpy * acy)) + (cpx * acx);
        var d5 = ((cpz * abz) + (cpy * aby)) + (cpx * abx);
        if (!(d6 < 0f || d6 < d5))
        {
            region = 3;
            return c;
        }
        var vb = (d5 * d2) - (d6 * d1);
        if (!(0f < vb || d2 < 0f || 0f < d6))
        {
            region = 6;
            var w = d2 / (d2 - d6);
            return new Vec3((acx * w) + a.X, (acy * w) + a.Y, (acz * w) + a.Z);
        }
        var va = (d6 * d3) - (d5 * d4);
        if (!(0f < va || d4 < d3 || d5 < d6))
        {
            region = 5;
            var w = (d4 - d3) / ((d5 - d6) + (d4 - d3));
            return new Vec3(((c.X - b.X) * w) + b.X, ((c.Y - b.Y) * w) + b.Y, ((c.Z - b.Z) * w) + b.Z);
        }
        region = 7;
        var denominator = 1f / ((va + vb) + vc);
        var sv = denominator * vb;
        var sw = denominator * vc;
        return new Vec3(((abx * sv) + a.X) + (acx * sw), ((aby * sv) + a.Y) + (acy * sw), ((abz * sv) + a.Z) + (acz * sw));
    }

    /// <summary>
    /// FUN_1802efd30: capsule against one mesh triangle, the triangle as a
    /// two-faced hull (FUN_1802f5c20) and a three-point GJK proxy. The
    /// capsule-hull manifold with no old manifold and the triangle as the
    /// sub-shape; nearly touching clears the whole triangle cache before the
    /// separating-axis path.
    /// </summary>
    public static bool CapsuleTriangle(ref CachedManifold result, in RnTransform xfA, in Capsule a, in RnTransform xfB,
                                       Vec3 v0, Vec3 v1, Vec3 v2, ref MeshTriangleCache cache, int triangle)
    {
        var hull = new HullRef(MeshCollision.TriangleHull(v0, v1, v2), 1f);
        var segment = new GjkProxy { Vertices = [a.A, a.B], Count = 2, Scale = 1f, Radius = 0f };
        var corners = new GjkProxy { Vertices = [v0, v1, v2], Count = 3, Scale = 1f, Radius = 0f };
        var g = Gjk.Distance(xfA, segment, xfB, corners, ref cache.Gjk, 0x20);
        if (!(g.Distance <= a.Radius + 0.0625f))
        {
            cache = default;
            return false;
        }
        if (!(0.003125f <= g.Distance))
        {
            cache = default;
            return CapsuleHullDeep(default, ref result, xfA, a, xfB, hull, triangle, 0.0625f);
        }
        return CapsuleHullManifold(default, ref result, xfA, a, xfB, hull, g, ref cache.Gjk, 0.0625f, triangle);
    }
}
