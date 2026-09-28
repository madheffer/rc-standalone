using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Source2.Compiler.Physics;

namespace Source2.Compiler.Simulation;

public static partial class RoundCollision
{
    /// <summary>A point of a clipped segment (0x14 bytes): its position and the feature it keeps.</summary>
    private struct ClipPoint
    {
        public Vec3 P;
        public int Feature;
    }

    /// <summary>A segment being clipped, its two points (FUN_1803bb1c0: features 0 and 0x01000100).</summary>
    private struct ClipSegment
    {
        public ClipPoint A, B;

        public static ClipSegment Of(Vec3 a, Vec3 b)
            => new() { A = new ClipPoint { P = a }, B = new ClipPoint { P = b, Feature = 0x01000100 } };
    }

    /// <summary>
    /// FUN_1802efbc0: capsule against hull. GJK (32 iterations, the contact's
    /// cache) from the capsule's segment, a two-point proxy, to the hull: no
    /// touch past the radius plus 1/16, which also clears the cache; else the
    /// manifold (FUN_1802f29a0).
    /// </summary>
    public static bool CapsuleHull(ReadOnlySpan<CachedManifold> old, ref CachedManifold result,
                                   in RnTransform xfA, in Capsule a, in RnTransform xfB, HullRef b, ref GjkCache cache)
    {
        var segment = new GjkProxy { Vertices = [a.A, a.B], Count = 2, Scale = 1f, Radius = 0f };
        var g = Gjk.Distance(xfA, segment, xfB, ContinuousSolve.ProxyOf(b), ref cache, 0x20);
        if (!(g.Distance <= a.Radius + 0.0625f))
        {
            cache = default;
            return false;
        }
        return CapsuleHullManifold(old, ref result, xfA, a, xfB, b, g, ref cache, 0.0625f);
    }

    /// <summary>
    /// FUN_1802f29a0: the capsule-hull manifold from GJK's result. Nearly
    /// touching (under 0.003125) goes to the separating-axis path
    /// (FUN_1802f13a0). Otherwise the hull face most against GJK's direction
    /// clips the segment; ends within the radius plus the margin give points,
    /// their depth shifted up by up to 1/8 so the deepest keeps GJK's
    /// distance. Failing that, GJK's own pair of points.
    /// </summary>
    private static bool CapsuleHullManifold(ReadOnlySpan<CachedManifold> old, ref CachedManifold result,
                                            in RnTransform xfA, in Capsule a, in RnTransform xfB, HullRef b,
                                            in GjkOutput g, ref GjkCache cache, float margin, int subShape = -1)
    {
        var distance = g.Distance;
        if (distance < 0.003125f)
        {
            cache.Count = 0;
            cache.Direction = default;
            return CapsuleHullDeep(old, ref result, xfA, a, xfB, b, -1, margin);
        }
        ref readonly var ra = ref xfA.R;
        ref readonly var rb = ref xfB.R;
        var r = a.Radius;
        var e0 = ThroughYX(xfA, a.A);
        var e1 = ThroughYX(xfA, a.B);
        float dx = g.PointB.X - g.PointA.X, dy = g.PointB.Y - g.PointA.Y, dz = g.PointB.Z - g.PointA.Z;
        float nx = 0f, ny = 0f, nz = 0f;
        var len2 = ((dx * dx) + (dy * dy)) + (dz * dz);
        if (len2 > 1.17549435e-35f)
        {
            var inverse = 1f / MathF.Sqrt(len2);
            (nx, ny, nz) = (inverse * dx, inverse * dy, inverse * dz);
        }
        float mx = -nx, my = -ny, mz = -nz;
        var local = new Vec3(((rb.M0 * mx) + (rb.M1 * my)) + (rb.M2 * mz),
                             ((rb.M3 * mx) + (rb.M4 * my)) + (rb.M5 * mz),
                             ((rb.M6 * mx) + (rb.M7 * my)) + (rb.M8 * mz));
        var face = BestFace(b.Hull, local);
        var (pn, pd) = b.Hull.Planes[face];
        var wx = ((pn.X * rb.M0) + (pn.Y * rb.M3)) + (pn.Z * rb.M6);
        var wy = ((pn.X * rb.M1) + (pn.Y * rb.M4)) + (pn.Z * rb.M7);
        var wz = ((pn.X * rb.M2) + (pn.Y * rb.M5)) + (pn.Z * rb.M8);
        var plane = ((wy * xfB.T.Y) + (wx * xfB.T.X)) + (wz * xfB.T.Z) + (pd * b.Scale);
        var seg = ClipSegment.Of(e0, e1);
        if (ClipToFace(ref seg, xfB, b, face))
        {
            var limit = r + margin;
            var s0 = (((seg.A.P.Z * wz) + (seg.A.P.Y * wy)) + (seg.A.P.X * wx)) - plane;
            var s1 = (((seg.B.P.Z * wz) + (seg.B.P.Y * wy)) + (seg.B.P.X * wx)) - plane;
            var least = s1 <= s0 ? s1 : s0;
            if (least <= limit && (least * 0.98f) - 0.015625f <= distance)
            {
                var shift = Clamp8(distance, least);
                var n = new Vec3(-wx, -wy, -wz);
                return TwoPoints(old, ref result, xfA, xfB, seg, n, r, s0 - shift, s1 - shift, s0 <= limit, s1 <= limit, subShape,
                                 pointOrder: false);
            }
        }
        // GJK's own points.
        var impulse = !old.IsEmpty && old[0].PointCount == 1 ? old[0].P0.Impulse : -1f;
        var pa = new Vec3((nx * r) + g.PointA.X, (ny * r) + g.PointA.Y, (nz * r) + g.PointA.Z);
        var normal = new Vec3(nx, ny, nz);
        result.Centre = pa;
        result.Normal = normal;
        (result.T1, result.T2) = InlineTangents(normal);
        HullCollision.TransferFriction(ref result, old);
        result.PointCount = 1;
        float ax = pa.X - xfA.T.X, ay = pa.Y - xfA.T.Y, az = pa.Z - xfA.T.Z;
        result.P0.LocalA = new Vec3(((ay * ra.M1) + (ax * ra.M0)) + (az * ra.M2),
                                    ((ay * ra.M4) + (ax * ra.M3)) + (az * ra.M5),
                                    ((ay * ra.M7) + (ax * ra.M6)) + (az * ra.M8));
        float bz = g.PointB.Z - xfB.T.Z, bx = g.PointB.X - xfB.T.X, by = g.PointB.Y - xfB.T.Y;
        result.P0.LocalB = new Vec3(((by * rb.M1) + (bx * rb.M0)) + (bz * rb.M2),
                                    ((by * rb.M4) + (bx * rb.M3)) + (bz * rb.M5),
                                    ((by * rb.M7) + (bx * rb.M6)) + (bz * rb.M8));
        SetPoint(ref result.P0, impulse, 0, subShape);
        return true;
    }

    /// <summary>FUN_1803bb830: b - a held to 0..1/8.</summary>
    private static float Clamp8(float a, float b)
    {
        var d = b - a;
        return !(0f <= d) ? 0f : d <= 0.125f ? d : 0.125f;
    }

    /// <summary>
    /// The two points of a clipped segment against a face (FUN_1802f29a0's
    /// and FUN_1802edc30's tail): the centre between the ends pushed out by
    /// the radius, the inline tangents, then each end that is in range, its
    /// impulse taken from the old point of the same feature.
    /// </summary>
    private static bool TwoPoints(ReadOnlySpan<CachedManifold> old, ref CachedManifold result, in RnTransform xfA, in RnTransform xfB,
                                  in ClipSegment seg, Vec3 n, float r, float d0, float d1, bool keep0, bool keep1, int subShape,
                                  bool pointOrder)
    {
        ref readonly var ra = ref xfA.R;
        ref readonly var rb = ref xfB.R;
        Vec3 p0 = seg.A.P, p1 = seg.B.P;
        var q0 = new Vec3(p0.X + (n.X * r), p0.Y + (n.Y * r), p0.Z + (n.Z * r));
        var q1 = new Vec3(p1.X + (n.X * r), p1.Y + (n.Y * r), p1.Z + (n.Z * r));
        result.Centre = new Vec3((q1.X + q0.X) * 0.5f, (q1.Y + q0.Y) * 0.5f, (q0.Z + q1.Z) * 0.5f);
        result.Normal = n;
        (result.T1, result.T2) = InlineTangents(n);
        HullCollision.TransferFriction(ref result, old);
        result.PointCount = 0;
        var points = MemoryMarshal.CreateSpan(ref result.P0, 4);
        if (keep0)
        {
            var impulse = ImpulseOf(old, seg.A.Feature);
            ref var p = ref points[0];
            result.PointCount = 1;
            float ax = q0.X - xfA.T.X, az = q0.Z - xfA.T.Z, ay = q0.Y - xfA.T.Y;
            p.LocalA = pointOrder
                ? new Vec3(((ax * ra.M0) + (ay * ra.M1)) + (az * ra.M2), ((ay * ra.M4) + (ax * ra.M3)) + (az * ra.M5),
                           ((ax * ra.M6) + (ay * ra.M7)) + (az * ra.M8))
                : new Vec3(((ax * ra.M0) + (ay * ra.M1)) + (az * ra.M2), ((ax * ra.M3) + (ay * ra.M4)) + (az * ra.M5),
                           ((ax * ra.M6) + (ay * ra.M7)) + (az * ra.M8));
            float bx = ((d0 * n.X) + p0.X) - xfB.T.X, by = ((d0 * n.Y) + p0.Y) - xfB.T.Y, bz = ((d0 * n.Z) + p0.Z) - xfB.T.Z;
            p.LocalB = pointOrder
                ? new Vec3(((by * rb.M1) + (bx * rb.M0)) + (bz * rb.M2), ((bx * rb.M3) + (by * rb.M4)) + (bz * rb.M5),
                           ((by * rb.M7) + (bx * rb.M6)) + (bz * rb.M8))
                : new Vec3(((by * rb.M1) + (bx * rb.M0)) + (bz * rb.M2), ((by * rb.M4) + (bx * rb.M3)) + (bz * rb.M5),
                           ((bx * rb.M6) + (by * rb.M7)) + (bz * rb.M8));
            SetPoint(ref p, impulse, seg.A.Feature, subShape);
        }
        if (keep1)
        {
            var impulse = ImpulseOf(old, seg.B.Feature);
            ref var p = ref points[result.PointCount];
            result.PointCount++;
            float ax = q1.X - xfA.T.X, ay = q1.Y - xfA.T.Y, az = q1.Z - xfA.T.Z;
            p.LocalA = new Vec3(((ay * ra.M1) + (ax * ra.M0)) + (az * ra.M2), ((ay * ra.M4) + (ax * ra.M3)) + (az * ra.M5),
                                ((ay * ra.M7) + (ax * ra.M6)) + (az * ra.M8));
            float bx = ((d1 * n.X) + p1.X) - xfB.T.X, by = ((d1 * n.Y) + p1.Y) - xfB.T.Y, bz = ((d1 * n.Z) + p1.Z) - xfB.T.Z;
            p.LocalB = new Vec3(((by * rb.M1) + (bx * rb.M0)) + (bz * rb.M2), ((by * rb.M4) + (bx * rb.M3)) + (bz * rb.M5),
                                ((by * rb.M7) + (bx * rb.M6)) + (bz * rb.M8));
            SetPoint(ref p, impulse, seg.B.Feature, subShape);
        }
        return result.PointCount != 0;
    }

    /// <summary>The old manifold's impulse at the point of this feature, or -1.</summary>
    private static float ImpulseOf(ReadOnlySpan<CachedManifold> old, int feature)
    {
        if (old.IsEmpty)
            return -1f;
        var points = MemoryMarshal.CreateReadOnlySpan(in old[0].P0, 4);
        for (var i = 0; i < old[0].PointCount && i < 4; i++)
            if (points[i].Feature == feature)
                return points[i].Impulse;
        return -1f;
    }

    /// <summary>A point's impulse (at least 0), feature, sub-shape and flags (byte 0x24 cleared, 0x25 new).</summary>
    private static void SetPoint(ref CachedPoint p, float impulse, int feature, int subShape)
    {
        p.Impulse = 0f <= impulse ? impulse : 0f;
        p.Feature = feature;
        p.SubShape = subShape;
        ref var flags = ref Unsafe.As<int, byte>(ref p.Reserved);
        flags = 0;
        Unsafe.Add(ref flags, 1) = (byte)(impulse < 0f ? 1 : 0);
    }

    /// <summary>
    /// The friction basis these paths build inline: t1 across n in the y-z
    /// plane when |n.x| is under 0.57735, else in the x-y plane; t2 = n x t1.
    /// </summary>
    private static (Vec3 T1, Vec3 T2) InlineTangents(Vec3 n)
    {
        Vec3 t1;
        if (MathF.Abs(n.X) < 0.57735f)
        {
            var length = MathF.Sqrt((n.Z * n.Z) + (n.Y * n.Y));
            t1 = new Vec3(0f, n.Z / length, -n.Y / length);
        }
        else
        {
            var length = MathF.Sqrt((n.X * n.X) + (n.Y * n.Y));
            t1 = new Vec3(n.Y / length, -n.X / length, 0f);
        }
        return (t1, new Vec3((t1.Y * n.Z) - (t1.Z * n.Y), (t1.Z * n.X) - (t1.X * n.Z), (t1.X * n.Y) - (t1.Y * n.X)));
    }

    /// <summary>A point through the frame, each row summed y, then x, then z.</summary>
    private static Vec3 ThroughYX(in RnTransform xf, Vec3 p)
    {
        ref readonly var m = ref xf.R;
        return new Vec3(((m.M3 * p.Y) + (m.M0 * p.X)) + (m.M6 * p.Z) + xf.T.X,
                        ((m.M4 * p.Y) + (m.M1 * p.X)) + (m.M7 * p.Z) + xf.T.Y,
                        ((m.M5 * p.Y) + (m.M2 * p.X)) + (m.M8 * p.Z) + xf.T.Z);
    }

    /// <summary>FUN_1802f5760: the plane whose normal is furthest along d, the first of equals.</summary>
    private static int BestFace(RnHull hull, Vec3 d)
    {
        var best = -float.MaxValue;
        var index = 0;
        for (var i = 0; i < hull.Planes.Length; i++)
        {
            var n = hull.Planes[i].Normal;
            var dot = ((d.Z * n.Z) + (n.Y * d.Y)) + (d.X * n.X);
            if (best < dot)
            {
                best = dot;
                index = i;
            }
        }
        return index;
    }

    /// <summary>
    /// FUN_1803bb660: the segment kept on the plane's inner side (n.p - d at
    /// most 0), in place; a crossing makes a point at the crossing, with the
    /// feature of the end that was outside. The count left.
    /// </summary>
    private static int ClipByPlane(ref ClipSegment s, Vec3 n, float d)
    {
        var a = s.A;
        var b = s.B;
        var da = (((a.P.Z * n.Z) + (a.P.Y * n.Y)) + (a.P.X * n.X)) - d;
        var db = (((b.P.Z * n.Z) + (b.P.Y * n.Y)) + (b.P.X * n.X)) - d;
        var count = 0;
        ref var first = ref s.A;
        if (da <= 0f)
        {
            first.P = a.P;
            first.Feature = a.Feature;
            count = 1;
        }
        if (db <= 0f)
        {
            ref var at = ref count == 0 ? ref s.A : ref s.B;
            at.P = b.P;
            at.Feature = b.Feature;
            count++;
        }
        if (db * da < 0f)
        {
            var t = da / (da - db);
            var u = 1f - t;
            ref var at = ref count == 0 ? ref s.A : ref s.B;
            at.Feature = 0f < da ? a.Feature : b.Feature;
            at.P = new Vec3((u * a.P.X) + (b.P.X * t), (u * a.P.Y) + (b.P.Y * t), (u * a.P.Z) + (b.P.Z * t));
            count++;
        }
        return count;
    }

    /// <summary>
    /// FUN_180336e70: the segment clipped by the face's side planes, each the
    /// edge's direction crossed with the face normal through its first
    /// vertex; false as soon as fewer than two points are left.
    /// </summary>
    private static bool ClipToFace(ref ClipSegment seg, in RnTransform xfB, HullRef b, int face)
    {
        ref readonly var m = ref xfB.R;
        var hull = b.Hull;
        var (pn, _) = hull.Planes[face];
        var fx = ((pn.Y * m.M3) + (pn.X * m.M0)) + (pn.Z * m.M6);
        var fy = ((pn.X * m.M1) + (pn.Y * m.M4)) + (pn.Z * m.M7);
        var fz = ((pn.Y * m.M5) + (pn.X * m.M2)) + (pn.Z * m.M8);
        var start = hull.Faces[face];
        var edge = (int)start;
        while (true)
        {
            var s = b.Scale;
            var e = hull.Edges[edge];
            var next = hull.Edges[e.Next];
            var v0 = hull.VertexPositions[e.Origin];
            var v1 = hull.VertexPositions[next.Origin];
            float ax = s * v0.X, ay = s * v0.Y, az = s * v0.Z;
            var p0x = ((ax * m.M0) + (ay * m.M3)) + (az * m.M6) + xfB.T.X;
            var p0z = ((ax * m.M2) + (ay * m.M5)) + (az * m.M8) + xfB.T.Z;
            var p0y = ((ax * m.M1) + (ay * m.M4)) + (az * m.M7) + xfB.T.Y;
            float bx = s * v1.X, by = s * v1.Y, bz = s * v1.Z;
            var ey = (((bx * m.M1) + (by * m.M4)) + (bz * m.M7) + xfB.T.Y) - p0y;
            var ez = (((bx * m.M2) + (by * m.M5)) + (bz * m.M8) + xfB.T.Z) - p0z;
            var ex = (((bx * m.M0) + (by * m.M3)) + (bz * m.M6) + xfB.T.X) - p0x;
            var len2 = ((ex * ex) + (ey * ey)) + (ez * ez);
            if (len2 <= 1.17549435e-35f)
                (ex, ey, ez) = (0f, 0f, 0f);
            else
            {
                var inverse = 1f / MathF.Sqrt(len2);
                (ex, ey, ez) = (ex * inverse, ey * inverse, ez * inverse);
            }
            var nx = (ey * fz) - (ez * fy);
            var nz = (ex * fy) - (ey * fx);
            var ny = (ez * fx) - (ex * fz);
            var d = ((nz * p0z) + (ny * p0y)) + (nx * p0x);
            if (ClipByPlane(ref seg, new Vec3(nx, ny, nz), d) < 2)
                return false;
            edge = e.Next;
            if (edge == start)
                return true;
        }
    }

    /// <summary>A separating-axis query's answer: the separation, the face or 0, the end or edge.</summary>
    private readonly record struct Query(float Separation, int Index, int Other);

    /// <summary>
    /// FUN_1802f13a0: capsule against hull by separating axes, when GJK finds
    /// them (nearly) touching. Faces first (FUN_18028c730), then edges
    /// (FUN_18028b420); either past the radius plus the margin is no touch.
    /// The face contact (FUN_1802edc30) stands unless it fails or an edge's
    /// separation beats it by the usual 0.98 and 1/64, when the edge contact
    /// (FUN_1802eca60) replaces it if it succeeds.
    /// </summary>
    private static bool CapsuleHullDeep(ReadOnlySpan<CachedManifold> old, ref CachedManifold result,
                                        in RnTransform xfA, in Capsule a, in RnTransform xfB, HullRef b, int subShape, float margin)
    {
        var face = FaceQuery(xfA, a, xfB, b);
        if (margin + a.Radius < face.Separation)
            return false;
        var edge = EdgeQuery(xfA, a, xfB, b);
        if (margin + a.Radius < edge.Separation)
            return false;
        if (!FaceContact(old, ref result, xfA, a, xfB, b, face, subShape, margin))
            return EdgeContact(old, ref result, xfA, a, xfB, b, edge, subShape);
        var faceSeparation = face.Separation - a.Radius;
        if (result.PointCount > 1)
            faceSeparation = HullCollision.MinSeparation(xfA, xfB, result);
        if (((faceSeparation - margin) * 0.98f) + 0.015625f < (edge.Separation - a.Radius) - margin)
        {
            var edgeResult = result;
            if (EdgeContact(old, ref edgeResult, xfA, a, xfB, b, edge, subShape))
                result = edgeResult;
        }
        return true;
    }

    /// <summary>
    /// FUN_18028c730: each hull face against the capsule, in A's frame: the
    /// face normal turned by A^T B, the capsule end deeper along it, its
    /// height over the plane. The greatest, the face, and whether the second
    /// end was the one (1) or the first (0).
    /// </summary>
    private static Query FaceQuery(in RnTransform xfA, in Capsule a, in RnTransform xfB, HullRef b)
    {
        ref readonly var ma = ref xfA.R;
        ref readonly var mb = ref xfB.R;
        float tx = xfB.T.X - xfA.T.X, ty = xfB.T.Y - xfA.T.Y, tz = xfB.T.Z - xfA.T.Z;
        var best = new Query(-float.MaxValue, 0, 0);
        var planes = b.Hull.Planes;
        for (var i = 0; i < b.Hull.Faces.Length; i++)
        {
            var (n, d) = planes[i];
            var nx = ((n.Y * (((ma.M0 * mb.M3) + (ma.M1 * mb.M4)) + (ma.M2 * mb.M5)))
                      + (n.X * (((ma.M0 * mb.M0) + (mb.M1 * ma.M1)) + (mb.M2 * ma.M2))))
                     + (n.Z * (((ma.M0 * mb.M6) + (ma.M1 * mb.M7)) + (ma.M2 * mb.M8)));
            var ny = ((n.X * (((mb.M0 * ma.M3) + (mb.M1 * ma.M4)) + (mb.M2 * ma.M5)))
                      + (n.Y * (((mb.M3 * ma.M3) + (mb.M4 * ma.M4)) + (mb.M5 * ma.M5))))
                     + (n.Z * (((mb.M6 * ma.M3) + (mb.M7 * ma.M4)) + (mb.M8 * ma.M5)));
            var nz = ((n.X * (((mb.M0 * ma.M6) + (mb.M1 * ma.M7)) + (mb.M2 * ma.M8)))
                      + (n.Y * (((mb.M3 * ma.M6) + (mb.M4 * ma.M7)) + (mb.M5 * ma.M8))))
                     + (n.Z * (((mb.M6 * ma.M6) + (mb.M7 * ma.M7)) + (mb.M8 * ma.M8)));
            var d0 = ((a.A.Z * -nz) + (-ny * a.A.Y)) + (-nx * a.A.X);
            var d1 = ((a.B.Z * -nz) + (-ny * a.B.Y)) + (-nx * a.B.X);
            var c = d1 < d0 ? a.A : a.B;
            var offset = (((nx * (((ma.M0 * tx) + (ma.M1 * ty)) + (ma.M2 * tz)))
                           + ((((ma.M4 * ty) + (ma.M3 * tx)) + (ma.M5 * tz)) * ny))
                          + ((((ma.M7 * ty) + (ma.M6 * tx)) + (ma.M8 * tz)) * nz))
                         + (d * b.Scale);
            var sep = (((c.Y * ny) + (c.Z * nz)) + (c.X * nx)) - offset;
            if (best.Separation < sep)
                best = new Query(sep, i, d0 <= d1 ? 1 : 0);
        }
        return best;
    }

    /// <summary>
    /// FUN_18028b420: each hull edge pair (edge and twin) against the
    /// capsule's axis, in B's frame, where their Gauss-map arcs cross: the
    /// axis d x e (skipped when under 0.005 of |d||e|, as -FLT_MAX), turned
    /// away from the hull's centroid, and the second end's height along it.
    /// The greatest, and its edge.
    /// </summary>
    private static Query EdgeQuery(in RnTransform xfA, in Capsule a, in RnTransform xfB, HullRef b)
    {
        ref readonly var ma = ref xfA.R;
        ref readonly var mb = ref xfB.R;
        var m00 = ((mb.M0 * ma.M0) + (ma.M1 * mb.M1)) + (ma.M2 * mb.M2);
        var m10 = ((mb.M3 * ma.M0) + (mb.M4 * ma.M1)) + (mb.M5 * ma.M2);
        var m20 = ((mb.M6 * ma.M0) + (mb.M7 * ma.M1)) + (mb.M8 * ma.M2);
        var m01 = ((ma.M3 * mb.M0) + (ma.M4 * mb.M1)) + (ma.M5 * mb.M2);
        var m11 = ((ma.M3 * mb.M3) + (ma.M4 * mb.M4)) + (ma.M5 * mb.M5);
        var m21 = ((ma.M3 * mb.M6) + (ma.M4 * mb.M7)) + (ma.M5 * mb.M8);
        var m02 = ((ma.M6 * mb.M0) + (ma.M7 * mb.M1)) + (ma.M8 * mb.M2);
        var m12 = ((ma.M6 * mb.M3) + (ma.M7 * mb.M4)) + (ma.M8 * mb.M5);
        var m22 = ((ma.M6 * mb.M6) + (ma.M7 * mb.M7)) + (ma.M8 * mb.M8);
        float tz = xfA.T.Z - xfB.T.Z, ty = xfA.T.Y - xfB.T.Y, tx = xfA.T.X - xfB.T.X;
        var bx = ((tx * mb.M0) + (ty * mb.M1)) + (tz * mb.M2);
        var bz = ((mb.M7 * ty) + (mb.M6 * tx)) + (mb.M8 * tz);
        var by = ((mb.M4 * ty) + (mb.M3 * tx)) + (mb.M5 * tz);
        Vec3 c0 = a.A, c1 = a.B;
        var p1x = ((m01 * c1.Y) + (c1.X * m00)) + (c1.Z * m02) + bx;
        var p1y = ((m11 * c1.Y) + (c1.X * m10)) + (c1.Z * m12) + by;
        var p1z = ((m20 * c1.X) + (c1.Y * m21)) + (m22 * c1.Z) + bz;
        var ex = (((c0.Y * m01) + (c0.X * m00)) + (c0.Z * m02) + bx) - p1x;
        var ey = (((c0.Y * m11) + (c0.X * m10)) + (c0.Z * m12) + by) - p1y;
        var ez = (((c0.Y * m21) + (m20 * c0.X)) + (c0.Z * m22) + bz) - p1z;
        var hull = b.Hull;
        var s = b.Scale;
        var best = new Query(-float.MaxValue, 0, 0);
        for (var i = 0; i < hull.Edges.Length; i += 2)
        {
            var edge = hull.Edges[i];
            var twin = hull.Edges[i + 1];
            var f0 = hull.Planes[edge.Face].Normal;
            var f1 = hull.Planes[twin.Face].Normal;
            if (!((((ex * f1.X) + (ey * f1.Y)) + (ez * f1.Z)) * (((ey * f0.Y) + (ex * f0.X)) + (ez * f0.Z)) <= 0f))
                continue;
            var v0 = hull.VertexPositions[edge.Origin];
            var v1 = hull.VertexPositions[twin.Origin];
            float ax = s * v0.X, ay = s * v0.Y, az = s * v0.Z;
            var dx = (s * v1.X) - ax;
            var dy = (s * v1.Y) - ay;
            var dz = (s * v1.Z) - az;
            var nx = (ez * dy) - (ey * dz);
            var ny = (ex * dz) - (ez * dx);
            var nz = (ey * dx) - (ex * dy);
            var length = MathF.Sqrt(((ny * ny) + (nx * nx)) + (nz * nz));
            var limit = MathF.Sqrt((((dy * dy) + (dx * dx)) + (dz * dz)) * (((ex * ex) + (ey * ey)) + (ez * ez)));
            float sep;
            if (limit * 0.005f < length)
            {
                var inverse = 1f / length;
                nx = inverse * nx;
                ny = inverse * ny;
                nz = inverse * nz;
                var c = hull.Centroid;
                if ((((ay - (s * c.Y)) * ny) + ((ax - (s * c.X)) * nx)) + ((az - (s * c.Z)) * nz) < 0f)
                    (nx, ny, nz) = (-nx, -ny, -nz);
                sep = (((p1y - ay) * ny) + ((p1x - ax) * nx)) + ((p1z - az) * nz);
            }
            else
                sep = -float.MaxValue;
            if (best.Separation <= sep && sep != best.Separation)
                best = new Query(sep, 0, i);
        }
        return best;
    }

    /// <summary>
    /// FUN_1802edc30: the capsule clipped to the query's face, points where
    /// an end is within the radius plus the margin, at the end's own height
    /// (no shift). False when the clip leaves under two points or neither end
    /// is in range.
    /// </summary>
    private static bool FaceContact(ReadOnlySpan<CachedManifold> old, ref CachedManifold result, in RnTransform xfA, in Capsule a,
                                    in RnTransform xfB, HullRef b, in Query query, int subShape, float margin)
    {
        ref readonly var ra = ref xfA.R;
        ref readonly var rb = ref xfB.R;
        var face = query.Index;
        var (pn, pd) = b.Hull.Planes[face];
        var wx = ((pn.Y * rb.M3) + (pn.X * rb.M0)) + (pn.Z * rb.M6);
        var wy = ((pn.X * rb.M1) + (pn.Y * rb.M4)) + (pn.Z * rb.M7);
        var wz = ((pn.X * rb.M2) + (pn.Y * rb.M5)) + (pn.Z * rb.M8);
        var plane = ((wy * xfB.T.Y) + (wx * xfB.T.X)) + (wz * xfB.T.Z) + (pd * b.Scale);
        var e1 = ThroughYX(xfA, a.B);
        var c = a.A;
        var e0 = new Vec3(((c.X * ra.M0) + (c.Y * ra.M3)) + (c.Z * ra.M6) + xfA.T.X,
                          ((c.X * ra.M1) + (c.Y * ra.M4)) + (c.Z * ra.M7) + xfA.T.Y,
                          ((c.X * ra.M2) + (c.Y * ra.M5)) + (c.Z * ra.M8) + xfA.T.Z);
        var seg = ClipSegment.Of(e0, e1);
        if (!ClipToFace(ref seg, xfB, b, face))
            return false;
        var s0 = (((seg.A.P.Y * wy) + (seg.A.P.Z * wz)) + (seg.A.P.X * wx)) - plane;
        var r = a.Radius;
        var s1 = (((seg.B.P.Y * wy) + (seg.B.P.Z * wz)) + (seg.B.P.X * wx)) - plane;
        var least = s1 <= s0 ? s1 : s0;
        if (!((least - r) - margin <= 0f))
            return false;
        var limit = margin + r;
        return TwoPoints(old, ref result, xfA, xfB, seg, new Vec3(-wx, -wy, -wz), r, s0, s1, s0 <= limit, s1 <= limit, subShape,
                         pointOrder: true);
    }

    /// <summary>
    /// FUN_1802eca60: the capsule's axis against the query's hull edge, in
    /// the world: the normal their directions' cross product (turned away
    /// from the hull's centroid), the closest points of the two lines
    /// (FUN_180321270) inside both segments give one point. False for no
    /// edge (-FLT_MAX) or closest points past an end.
    /// </summary>
    private static bool EdgeContact(ReadOnlySpan<CachedManifold> old, ref CachedManifold result, in RnTransform xfA, in Capsule a,
                                     in RnTransform xfB, HullRef b, in Query query, int subShape)
    {
        if (query.Separation == -float.MaxValue)
            return false;
        ref readonly var ra = ref xfA.R;
        ref readonly var rb = ref xfB.R;
        var c0 = ThroughYX(xfA, a.A);
        var c1 = ThroughYX(xfA, a.B);
        var da = new Vec3(c1.X - c0.X, c1.Y - c0.Y, c1.Z - c0.Z);
        var hull = b.Hull;
        var s = b.Scale;
        var edge = hull.Edges[query.Other];
        var v0 = hull.VertexPositions[edge.Origin];
        var v1 = hull.VertexPositions[hull.Edges[edge.Next].Origin];
        float ax = s * v0.X, ay = s * v0.Y, az = s * v0.Z;
        var e0 = new Vec3(((ay * rb.M3) + (ax * rb.M0)) + (az * rb.M6) + xfB.T.X,
                          ((ay * rb.M4) + (ax * rb.M1)) + (az * rb.M7) + xfB.T.Y,
                          ((ay * rb.M5) + (ax * rb.M2)) + (az * rb.M8) + xfB.T.Z);
        float bx = s * v1.X, by = s * v1.Y, bz = s * v1.Z;
        var db = new Vec3((((by * rb.M3) + (bx * rb.M0)) + (bz * rb.M6) + xfB.T.X) - e0.X,
                          (((bx * rb.M1) + (by * rb.M4)) + (bz * rb.M7) + xfB.T.Y) - e0.Y,
                          (((bx * rb.M2) + (by * rb.M5)) + (bz * rb.M8) + xfB.T.Z) - e0.Z);
        var nz = (db.Y * da.X) - (db.X * da.Y);
        var nx = (db.Z * da.Y) - (db.Y * da.Z);
        var ny = (db.X * da.Z) - (db.Z * da.X);
        var len2 = ((ny * ny) + (nx * nx)) + (nz * nz);
        if (len2 <= 1.17549435e-35f)
            (nx, ny, nz) = (0f, 0f, 0f);
        else
        {
            var inverse = 1f / MathF.Sqrt(len2);
            (nx, ny, nz) = (nx * inverse, ny * inverse, nz * inverse);
        }
        var c = hull.Centroid;
        float cx = s * c.X, cy = s * c.Y, cz = s * c.Z;
        var wcx = ((cy * rb.M3) + (cx * rb.M0)) + (cz * rb.M6) + xfB.T.X;
        var wcy = ((cy * rb.M4) + (cx * rb.M1)) + (cz * rb.M7) + xfB.T.Y;
        var wcz = ((cy * rb.M5) + (cx * rb.M2)) + (cz * rb.M8) + xfB.T.Z;
        if (0f < (((e0.Y - wcy) * ny) + ((e0.X - wcx) * nx)) + ((e0.Z - wcz) * nz))
            (nx, ny, nz) = (-nx, -ny, -nz);
        var (pa, sa, pb, tb) = HullCollision.ClosestPoints(c0, da, e0, db);
        if (!(0f <= sa && sa <= 1f && 0f <= tb && tb <= 1f))
            return false;
        var impulse = !old.IsEmpty && old[0].PointCount == 1 ? old[0].P0.Impulse : -1f;
        var r = a.Radius;
        var qx = (nx * r) + pa.X;
        var qy = (ny * r) + pa.Y;
        var qz = (nz * r) + pa.Z;
        var n = new Vec3(nx, ny, nz);
        result.Centre = new Vec3(qx, qy, qz);
        result.Normal = n;
        (result.T1, result.T2) = HullCollision.Tangents(n);
        HullCollision.TransferFriction(ref result, old);
        result.PointCount = 1;
        float lz = qz - xfA.T.Z, lx = qx - xfA.T.X, ly = qy - xfA.T.Y;
        result.P0.LocalA = new Vec3(((lx * ra.M0) + (ly * ra.M1)) + (lz * ra.M2),
                                    ((lx * ra.M3) + (ly * ra.M4)) + (lz * ra.M5),
                                    ((ly * ra.M7) + (lx * ra.M6)) + (lz * ra.M8));
        float hz = pb.Z - xfB.T.Z, hx = pb.X - xfB.T.X, hy = pb.Y - xfB.T.Y;
        result.P0.LocalB = new Vec3(((hy * rb.M1) + (hx * rb.M0)) + (hz * rb.M2),
                                    ((hx * rb.M3) + (hy * rb.M4)) + (hz * rb.M5),
                                    ((hx * rb.M6) + (hy * rb.M7)) + (hz * rb.M8));
        SetPoint(ref result.P0, impulse, 0, subShape);
        return true;
    }
}
