using System.Runtime.CompilerServices;

namespace Source2.Compiler.Simulation;

/// <summary>
/// Rubikon's narrowphase for the round shapes (vphysics2 2026-09-24): sphere
/// against sphere and capsule, reached through the convex contact's
/// dispatch table (0x1803e4200). Each makes at most one point, with the
/// normal from A to B, the tangents (<see cref="HullCollision.Tangents"/>),
/// the friction carried from the old manifold and the old first point's
/// impulse (-1 without one, which marks the point new). Every sum is in the
/// binary's order.
/// </summary>
public static partial class RoundCollision
{
    /// <summary>A sphere at the shape's scale (+0xb8): centre and radius.</summary>
    public readonly record struct Sphere(Vec3 Centre, float Radius);

    /// <summary>A capsule at the shape's scale (+0xb8): the two centres and the radius.</summary>
    public readonly record struct Capsule(Vec3 A, Vec3 B, float Radius);

    /// <summary>FUN_1802f5240: sphere against sphere, touching when the centres are no farther than the radii.</summary>
    public static bool SphereSphere(ReadOnlySpan<CachedManifold> old, ref CachedManifold result,
                                    in RnTransform xfA, in Sphere a, in RnTransform xfB, in Sphere b)
    {
        ref readonly var ra = ref xfA.R;
        ref readonly var rb = ref xfB.R;
        Vec3 ca = a.Centre, cb = b.Centre;
        var ax = ((ca.Y * ra.M3) + (ca.X * ra.M0)) + (ca.Z * ra.M6) + xfA.T.X;
        var az = ((ca.Y * ra.M5) + (ca.X * ra.M2)) + (ca.Z * ra.M8) + xfA.T.Z;
        var ay = ((ca.Y * ra.M4) + (ca.X * ra.M1)) + (ca.Z * ra.M7) + xfA.T.Y;
        var bx = ((cb.X * rb.M0) + (cb.Y * rb.M3)) + (cb.Z * rb.M6) + xfB.T.X;
        var sum = b.Radius + a.Radius;
        var by = ((cb.X * rb.M1) + (cb.Y * rb.M4)) + (cb.Z * rb.M7) + xfB.T.Y;
        var bz = ((cb.X * rb.M2) + (cb.Y * rb.M5)) + (cb.Z * rb.M8) + xfB.T.Z;
        var d2 = (((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by))) + ((az - bz) * (az - bz));
        if (!(d2 <= sum * sum))
            return false;
        var n = Normal(d2, bx - ax, by - ay, bz - az);
        var rA = a.Radius;
        var px = (n.X * rA) + ax;
        var py = (n.Y * rA) + ay;
        var pz = (n.Z * rA) + az;
        var qx = bx - (n.X * b.Radius);
        var qy = by - (n.Y * b.Radius);
        var qz = bz - (n.Z * b.Radius);
        Begin(ref result, old, new Vec3(px, py, pz), n);
        float dx = px - xfA.T.X, dy = py - xfA.T.Y, dz = pz - xfA.T.Z;
        result.P0.LocalA = new Vec3(((dx * ra.M0) + (dy * ra.M1)) + (dz * ra.M2),
                                    ((dx * ra.M3) + (dy * ra.M4)) + (dz * ra.M5),
                                    ((dx * ra.M6) + (dy * ra.M7)) + (dz * ra.M8));
        float ex = qx - xfB.T.X, ey = qy - xfB.T.Y, ez = qz - xfB.T.Z;
        result.P0.LocalB = new Vec3(((ey * rb.M1) + (ex * rb.M0)) + (ez * rb.M2),
                                    ((ey * rb.M4) + (ex * rb.M3)) + (ez * rb.M5),
                                    ((ey * rb.M7) + (ex * rb.M6)) + (ez * rb.M8));
        End(ref result, old);
        return true;
    }

    /// <summary>
    /// FUN_1802f38c0: sphere against capsule, the sphere's centre against the
    /// nearest point of the capsule's segment (the parameter clamped to 0..1,
    /// the segment's length squared held above FLT_MIN).
    /// </summary>
    public static bool SphereCapsule(ReadOnlySpan<CachedManifold> old, ref CachedManifold result,
                                     in RnTransform xfA, in Sphere a, in RnTransform xfB, in Capsule b)
    {
        ref readonly var ra = ref xfA.R;
        ref readonly var rb = ref xfB.R;
        var c = a.Centre;
        var cx = ((c.X * ra.M0) + (c.Y * ra.M3)) + (c.Z * ra.M6) + xfA.T.X;
        var cy = ((c.X * ra.M1) + (c.Y * ra.M4)) + (c.Z * ra.M7) + xfA.T.Y;
        var cz = ((c.X * ra.M2) + (c.Y * ra.M5)) + (c.Z * ra.M8) + xfA.T.Z;
        Vec3 p = b.A, q = b.B;
        var p0x = ((p.Y * rb.M3) + (p.X * rb.M0)) + (p.Z * rb.M6) + xfB.T.X;
        var p0y = ((p.Y * rb.M4) + (p.X * rb.M1)) + (p.Z * rb.M7) + xfB.T.Y;
        var p0z = ((p.Y * rb.M5) + (p.X * rb.M2)) + (p.Z * rb.M8) + xfB.T.Z;
        var ex = (((q.Y * rb.M3) + (q.X * rb.M0)) + (q.Z * rb.M6) + xfB.T.X) - p0x;
        var ey = (((q.Y * rb.M4) + (q.X * rb.M1)) + (q.Z * rb.M7) + xfB.T.Y) - p0y;
        var ez = (((q.Y * rb.M5) + (q.X * rb.M2)) + (q.Z * rb.M8) + xfB.T.Z) - p0z;
        var len2 = ((ey * ey) + (ex * ex)) + (ez * ez);
        var floor = 1.1754944e-38f;
        if (floor <= len2)
            floor = len2;
        var t = ((((cz - p0z) * ez) + ((cy - p0y) * ey)) + ((cx - p0x) * ex)) / floor;
        var s = 1f;
        if (t <= 1f)
            s = t;
        t = 0f;
        if (0f <= s)
            t = s;
        var sy = (t * ey) + p0y;
        var sz = (t * ez) + p0z;
        var sx = (t * ex) + p0x;
        float dx = sx - cx, dy = sy - cy, dz = sz - cz;
        var d2 = ((dx * dx) + (dy * dy)) + (dz * dz);
        var rB = b.Radius;
        var rA = a.Radius;
        if (!(d2 <= (rB + rA) * (rB + rA)))
            return false;
        var n = Normal(d2, dx, dy, dz);
        var py = (n.Y * rA) + cy;
        var px = (n.X * rA) + cx;
        var pz = (n.Z * rA) + cz;
        Begin(ref result, old, new Vec3(px, py, pz), n);
        float ax = px - xfA.T.X, ay = py - xfA.T.Y, az = pz - xfA.T.Z;
        result.P0.LocalA = new Vec3(((ay * ra.M1) + (ax * ra.M0)) + (az * ra.M2),
                                    ((ax * ra.M3) + (ay * ra.M4)) + (az * ra.M5),
                                    ((ay * ra.M7) + (ax * ra.M6)) + (az * ra.M8));
        var bz = (sz - (n.Z * rB)) - xfB.T.Z;
        var bx = (sx - (n.X * rB)) - xfB.T.X;
        var by = (sy - (n.Y * rB)) - xfB.T.Y;
        result.P0.LocalB = new Vec3(((by * rb.M1) + (bx * rb.M0)) + (bz * rb.M2),
                                    ((bx * rb.M3) + (by * rb.M4)) + (bz * rb.M5),
                                    ((bx * rb.M6) + (by * rb.M7)) + (bz * rb.M8));
        End(ref result, old);
        return true;
    }

    /// <summary>
    /// FUN_1802f0cf0: capsule against capsule, the nearest points of the two
    /// segments (FUN_180321560, <see cref="SeparationFunction.ClosestPointsOnSegments"/>).
    /// </summary>
    public static bool CapsuleCapsule(ReadOnlySpan<CachedManifold> old, ref CachedManifold result,
                                      in RnTransform xfA, in Capsule a, in RnTransform xfB, in Capsule b)
    {
        ref readonly var ra = ref xfA.R;
        ref readonly var rb = ref xfB.R;
        static Vec3 Through(in RnTransform xf, Vec3 p)
        {
            ref readonly var m = ref xf.R;
            return new Vec3(((m.M3 * p.Y) + (m.M0 * p.X)) + (m.M6 * p.Z) + xf.T.X,
                            ((m.M4 * p.Y) + (m.M1 * p.X)) + (m.M7 * p.Z) + xf.T.Y,
                            ((m.M5 * p.Y) + (m.M2 * p.X)) + (m.M8 * p.Z) + xf.T.Z);
        }
        var (pa, pb) = SeparationFunction.ClosestPointsOnSegments(Through(xfA, a.A), Through(xfA, a.B), Through(xfB, b.A), Through(xfB, b.B));
        float dx = pb.X - pa.X, dy = pb.Y - pa.Y, dz = pb.Z - pa.Z;
        var d2 = ((dy * dy) + (dx * dx)) + (dz * dz);
        float rA = a.Radius, rB = b.Radius;
        if (!(d2 <= (rB + rA) * (rB + rA)))
            return false;
        var n = Normal(d2, dx, dy, dz);
        var px = (n.X * rA) + pa.X;
        var py = (n.Y * rA) + pa.Y;
        var pz = (n.Z * rA) + pa.Z;
        Begin(ref result, old, new Vec3(px, py, pz), n);
        float ax = px - xfA.T.X, ay = py - xfA.T.Y, az = pz - xfA.T.Z;
        result.P0.LocalA = new Vec3(((ax * ra.M0) + (ay * ra.M1)) + (az * ra.M2),
                                    ((ax * ra.M3) + (ay * ra.M4)) + (az * ra.M5),
                                    ((ay * ra.M7) + (ax * ra.M6)) + (az * ra.M8));
        var bz = (pb.Z - (n.Z * rB)) - xfB.T.Z;
        var bx = (pb.X - (n.X * rB)) - xfB.T.X;
        var by = (pb.Y - (n.Y * rB)) - xfB.T.Y;
        result.P0.LocalB = new Vec3(((by * rb.M1) + (bx * rb.M0)) + (bz * rb.M2),
                                    ((by * rb.M4) + (bx * rb.M3)) + (bz * rb.M5),
                                    ((bx * rb.M6) + (by * rb.M7)) + (bz * rb.M8));
        End(ref result, old);
        return true;
    }

    /// <summary>
    /// FUN_1802f3f30: sphere against hull. GJK (FUN_1802ec220, 32 iterations,
    /// the contact's cache) from the sphere's centre, a one-point proxy at the
    /// identity, to the hull's proxy: no touch past the radius plus 1/16, which
    /// also clears the cache; the witness points' direction as the normal
    /// while the centre is outside (distance over 1.1920929e-05); inside, the
    /// hull face of greatest separation, its normal turned outward from the
    /// sphere as the normal and the point that far along it on the hull.
    /// </summary>
    public static bool SphereHull(ReadOnlySpan<CachedManifold> old, ref CachedManifold result,
                                  in RnTransform xfA, in Sphere a, in RnTransform xfB, HullRef b, ref GjkCache cache)
    {
        ref readonly var ra = ref xfA.R;
        ref readonly var rb = ref xfB.R;
        var c = a.Centre;
        var cx = ((c.X * ra.M0) + (c.Y * ra.M3)) + (c.Z * ra.M6) + xfA.T.X;
        var cy = ((c.Y * ra.M4) + (c.X * ra.M1)) + (c.Z * ra.M7) + xfA.T.Y;
        var cz = ((c.Y * ra.M5) + (c.X * ra.M2)) + (c.Z * ra.M8) + xfA.T.Z;
        var point = new GjkProxy { Vertices = [new Vec3(cx, cy, cz)], Count = 1, Scale = 1f, Radius = 0f };
        var identity = new RnTransform { R = new Mat3 { M0 = 1f, M4 = 1f, M8 = 1f } };
        var g = Gjk.Distance(identity, point, xfB, ContinuousSolve.ProxyOf(b), ref cache, 0x20);
        var r = a.Radius;
        if (r + 0.0625f < g.Distance)
        {
            cache = default;
            return false;
        }
        Vec3 n, pa, pb;
        if (1.1920929e-05f < g.Distance)
        {
            float dy = g.PointB.Y - g.PointA.Y, dz = g.PointB.Z - g.PointA.Z, dx = g.PointB.X - g.PointA.X;
            var length = MathF.Sqrt(((dz * dz) + (dy * dy)) + (dx * dx));
            if (length < 1e-17f || 1e17f < length)
                n = length != 0f ? HullCollision.NormalizeDouble(dx, dy, dz) : default;
            else
            {
                var inverse = 1f / length;
                n = new Vec3(dx * inverse, dy * inverse, dz * inverse);
            }
            pa = new Vec3((n.X * r) + cx, (n.Y * r) + cy, (n.Z * r) + cz);
            pb = g.PointB;
            Begin(ref result, old, pa, n);
            SetLocalA(ref result, xfA, pa);
            float bz = pb.Z - xfB.T.Z, bx = pb.X - xfB.T.X, by = pb.Y - xfB.T.Y;
            result.P0.LocalB = new Vec3(((by * rb.M1) + (bx * rb.M0)) + (bz * rb.M2),
                                        ((by * rb.M4) + (bx * rb.M3)) + (bz * rb.M5),
                                        ((by * rb.M7) + (bx * rb.M6)) + (bz * rb.M8));
            End(ref result, old);
            return true;
        }
        // The centre in the hull's frame against each face plane (the scaled offset).
        float ux = cx - xfB.T.X, uz = cz - xfB.T.Z, uy = cy - xfB.T.Y;
        var lx = ((rb.M0 * ux) + (rb.M1 * uy)) + (rb.M2 * uz);
        var ly = ((ux * rb.M3) + (rb.M4 * uy)) + (rb.M5 * uz);
        var lz = ((rb.M7 * uy) + (ux * rb.M6)) + (rb.M8 * uz);
        var planes = b.Hull.Planes;
        var faces = b.Hull.Faces.Length;
        var s = b.Scale;
        float Separation(int i)
        {
            var (pn, d) = planes[i];
            return (((lz * pn.Z) + (ly * pn.Y)) + (lx * pn.X)) - (s * d);
        }
        var best = Separation(0);
        var face = 0;
        for (var i = 1; i < faces; i++)
        {
            var sep = Separation(i);
            if (best < sep)
            {
                best = sep;
                face = i;
            }
        }
        var (normal, _) = planes[face];
        float mx = -normal.X, mz = -normal.Z, my = -normal.Y;
        var nx = ((my * rb.M3) + (mx * rb.M0)) + (mz * rb.M6);
        var ny = ((my * rb.M4) + (rb.M1 * mx)) + (mz * rb.M7);
        var nz = ((mx * rb.M2) + (my * rb.M5)) + (rb.M8 * mz);
        n = new Vec3(nx, ny, nz);
        pa = new Vec3((nx * r) + cx, (ny * r) + cy, (nz * r) + cz);
        pb = new Vec3((nx * best) + cx, (ny * best) + cy, (nz * best) + cz);
        Begin(ref result, old, pa, n);
        float ax = pa.X - xfA.T.X, ay = pa.Y - xfA.T.Y, az = pa.Z - xfA.T.Z;
        result.P0.LocalA = new Vec3(((ax * ra.M0) + (ay * ra.M1)) + (az * ra.M2),
                                    ((ay * ra.M4) + (ax * ra.M3)) + (az * ra.M5),
                                    ((ay * ra.M7) + (ax * ra.M6)) + (az * ra.M8));
        float qz = pb.Z - xfB.T.Z, qx = pb.X - xfB.T.X, qy = pb.Y - xfB.T.Y;
        result.P0.LocalB = new Vec3(((qx * rb.M0) + (qy * rb.M1)) + (qz * rb.M2),
                                    ((qy * rb.M4) + (qx * rb.M3)) + (qz * rb.M5),
                                    ((qy * rb.M7) + (qx * rb.M6)) + (qz * rb.M8));
        End(ref result, old);
        return true;
    }

    /// <summary>A world point into A's frame, summed x, y, then z on each row.</summary>
    private static void SetLocalA(ref CachedManifold m, in RnTransform xfA, Vec3 p)
    {
        ref readonly var ra = ref xfA.R;
        float ax = p.X - xfA.T.X, ay = p.Y - xfA.T.Y, az = p.Z - xfA.T.Z;
        m.P0.LocalA = new Vec3(((ax * ra.M0) + (ay * ra.M1)) + (az * ra.M2),
                               ((ax * ra.M3) + (ay * ra.M4)) + (az * ra.M5),
                               ((ax * ra.M6) + (ay * ra.M7)) + (az * ra.M8));
    }

    /// <summary>The unit normal along d, or +z when d is too short (its length squared at most 1.17549435e-35).</summary>
    private static Vec3 Normal(float d2, float dx, float dy, float dz)
    {
        var d = MathF.Sqrt(d2);
        if (!(1.17549435e-35f < d * d))
            return new Vec3(0f, 0f, 1f);
        var inverse = 1f / d;
        return new Vec3(inverse * dx, inverse * dy, inverse * dz);
    }

    /// <summary>The manifold's centre, normal, tangents and carried friction, and its one point.</summary>
    private static void Begin(ref CachedManifold m, ReadOnlySpan<CachedManifold> old, Vec3 centre, Vec3 n)
    {
        m.Centre = centre;
        m.Normal = n;
        (m.T1, m.T2) = HullCollision.Tangents(n);
        HullCollision.TransferFriction(ref m, old);
        m.PointCount = 1;
    }

    /// <summary>The point's impulse, feature 0, no sub-shape, new when no old manifold holds one.</summary>
    private static void End(ref CachedManifold m, ReadOnlySpan<CachedManifold> old)
    {
        var impulse = old.IsEmpty ? -1f : old[0].P0.Impulse;
        m.P0.Feature = 0;
        m.P0.SubShape = -1;
        m.P0.Impulse = 0f <= impulse ? impulse : 0f;
        // Bytes 0x64 and 0x65 of the point: 0, and whether it is new.
        ref var flags = ref Unsafe.As<int, byte>(ref m.P0.Reserved);
        flags = 0;
        Unsafe.Add(ref flags, 1) = (byte)(impulse < 0f ? 1 : 0);
    }
}
