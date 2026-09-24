using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

public static partial class MeshCollision
{
    /// <summary>
    /// Reduces a cluster's points to four or fewer (FUN_180300d50), on A's
    /// world points, even when there are four or fewer: the point furthest
    /// along the in-plane tangent turned 15 degrees about the normal, the
    /// point furthest from it (under 1/32: one point), the one making the
    /// largest triangle with those (under 2 |p0 p1| / 32: two), then the one
    /// most outside that triangle (not outside by 2 |p0 p1| / 32: three).
    /// The chosen points are swapped to the front; returns the count.
    /// </summary>
    internal static int ReducePoints(Span<CachedPoint> p, in RnTransform xfA, Vec3 n)
    {
        var count = p.Length;
        if (count == 0)
            return 0;
        var t1 = InPlaneTangent(n);
        float qx, qy, qz, qw;
        if (n.X == 0f && n.Y == 0f && n.Z == 0f)
        {
            (qx, qy, qz, qw) = (0f, 0f, 0f, 1f);
        }
        else
        {
            var half = BitConverter.Int32BitsToSingle(0x3e060a92);
            var (s, c) = RnMath.SinCos(half);
            (qx, qy, qz, qw) = (n.X * s, n.Y * s, n.Z * s, c);
        }
        var cx = t1.Z * qy - t1.Y * qz;
        var cy = t1.X * qz - t1.Z * qx;
        var cz = t1.Y * qx - t1.X * qy;
        var tx = cx + cx;
        var ty = cy + cy;
        var tz = cz + cz;
        var dx = (tz * qy - ty * qz) + (t1.X + qw * tx);
        var dy = (tx * qz - tz * qx) + (t1.Y + qw * ty);
        var dz = (ty * qx - tx * qy) + (t1.Z + qw * tz);

        var w = World(xfA, p[0]);
        var best = (w.Z * dz + w.Y * dy) + w.X * dx;
        var index = 0;
        for (var i = 1; i < count; i++)
        {
            w = World(xfA, p[i]);
            var d = (w.Z * dz + w.Y * dy) + w.X * dx;
            if (d > best)
            {
                best = d;
                index = i;
            }
        }
        Swap(p, 0, index);

        var p0 = World(xfA, p[0]);
        var far = -float.MaxValue;
        index = -1;
        for (var i = 1; i < count; i++)
        {
            w = World(xfA, p[i]);
            float ex = w.X - p0.X, ey = w.Y - p0.Y, ez = w.Z - p0.Z;
            var d = MathF.Sqrt((ex * ex + ey * ey) + ez * ez);
            if (d > far)
            {
                far = d;
                index = i;
            }
        }
        if (0.03125f > far)
            return 1;
        Swap(p, 1, index);

        var p1 = World(xfA, p[1]);
        var area = 0f;
        index = -1;
        for (var i = 2; i < count; i++)
        {
            w = World(xfA, p[i]);
            float ax = p0.X - w.X, ay = p0.Y - w.Y, az = p0.Z - w.Z;
            float bx = p1.X - w.X, by = p1.Y - w.Y, bz = p1.Z - w.Z;
            var x = ay * bz - az * by;
            var y = az * bx - ax * bz;
            var z = ax * by - ay * bx;
            var d = MathF.Sqrt((y * y + x * x) + z * z);
            if (d > area)
            {
                area = d;
                index = i;
            }
        }
        var threshold = (far + far) * 0.03125f;
        if (threshold > area)
            return 2;
        Swap(p, 2, index);

        var p2 = World(xfA, p[2]);
        var m = UnitNormal(p0, p1, p2);
        var min = 0f;
        index = -1;
        for (var i = 3; i < count; i++)
        {
            var a = Outside(p0, p1, p2, World(xfA, p[i]), m);
            if (min > a)
            {
                min = a;
                index = i;
            }
        }
        if (threshold > -min)
            return 3;
        Swap(p, 3, index);
        return 4;
    }

    private static Vec3 World(in RnTransform xf, in CachedPoint p)
        => HullCollision.ToWorld(xf, p.LocalA.X, p.LocalA.Y, p.LocalA.Z);

    private static void Swap(Span<CachedPoint> p, int i, int j) => (p[i], p[j]) = (p[j], p[i]);

    /// <summary>The unit ((p0 - p2) x (p1 - p2)), zero when degenerate.</summary>
    private static Vec3 UnitNormal(Vec3 p0, Vec3 p1, Vec3 p2)
    {
        float ax = p0.X - p2.X, ay = p0.Y - p2.Y, az = p0.Z - p2.Z;
        float bx = p1.X - p2.X, by = p1.Y - p2.Y, bz = p1.Z - p2.Z;
        var x = bz * ay - by * az;
        var y = bx * az - bz * ax;
        var z = by * ax - bx * ay;
        var lengthSquared = (y * y + x * x) + z * z;
        if (!(lengthSquared > TinySquared))
            return default;
        var inv = 1f / MathF.Sqrt(lengthSquared);
        return new Vec3(inv * x, inv * y, inv * z);
    }

    /// <summary>
    /// The smallest of p's signed areas against the three edges along m,
    /// summed y, z, x, with the DLL's minss order.
    /// </summary>
    private static float Outside(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p, Vec3 m)
    {
        float ax = p0.X - p.X, ay = p0.Y - p.Y, az = p0.Z - p.Z;
        float bx = p1.X - p.X, by = p1.Y - p.Y, bz = p1.Z - p.Z;
        float cx = p2.X - p.X, cy = p2.Y - p.Y, cz = p2.Z - p.Z;
        var a12 = ((cx * bz - cz * bx) * m.Y + (cy * bx - cx * by) * m.Z) + (cz * by - cy * bz) * m.X;
        var a20 = ((cz * ax - cx * az) * m.Y + (cx * ay - cy * ax) * m.Z) + (cy * az - cz * ay) * m.X;
        var a01 = ((bx * az - bz * ax) * m.Y + (by * ax - bx * ay) * m.Z) + (bz * ay - by * az) * m.X;
        var low = a20 < a01 ? a20 : a01;
        return a12 < low ? a12 : low;
    }

    /// <summary>
    /// Warm starts the new manifolds from the contact's old ones
    /// (FUN_180304620): each takes the first unused old manifold whose normal
    /// is within 0.995. Its points first take the impulse of an old point with
    /// the same triangle and feature, then of the nearest unused old point
    /// within 1/8 on both sides (FUN_180304230); then the friction transfer.
    /// </summary>
    internal static void WarmStart(List<CachedManifold> fresh, List<CachedManifold> old)
    {
        var usedOld = new bool[old.Count];
        var manifolds = CollectionsMarshal.AsSpan(fresh);
        for (var k = 0; k < manifolds.Length; k++)
        {
            ref var m = ref manifolds[k];
            var o = -1;
            for (var i = 0; i < old.Count; i++)
            {
                var on = old[i].Normal;
                if (!usedOld[i] && (m.Normal.X * on.X + m.Normal.Y * on.Y) + m.Normal.Z * on.Z >= SameNormal)
                {
                    o = i;
                    break;
                }
            }
            if (o < 0)
                continue;
            var source = old[o];
            var oldPoints = MemoryMarshal.CreateReadOnlySpan(in source.P0, 4);
            Span<bool> used = stackalloc bool[4];
            Span<int> unmatched = stackalloc int[4];
            var unmatchedCount = 0;
            var points = m.Points;
            for (var i = 0; i < m.PointCount; i++)
            {
                var matched = false;
                for (var j = 0; j < source.PointCount; j++)
                {
                    if (oldPoints[j].SubShape == points[i].SubShape && oldPoints[j].Feature == points[i].Feature)
                    {
                        used[j] = true;
                        points[i].Impulse = oldPoints[j].Impulse;
                        SetIsNew(ref points[i], false);
                        matched = true;
                        break;
                    }
                }
                if (!matched)
                    unmatched[unmatchedCount++] = i;
            }
            for (var u = 0; u < unmatchedCount; u++)
            {
                var i = unmatched[u];
                var j = NearestOldPoint(points[i], source, used);
                if (j < 0)
                    continue;
                used[j] = true;
                points[i].Impulse = oldPoints[j].Impulse;
                SetIsNew(ref points[i], false);
            }
            HullCollision.TransferFriction(ref m, new ReadOnlySpan<CachedManifold>(in source));
            usedOld[o] = true;
        }
    }

    private static void SetIsNew(ref CachedPoint p, bool isNew)
        => Unsafe.Add(ref Unsafe.As<int, byte>(ref p.Reserved), 1) = (byte)(isNew ? 1 : 0);

    /// <summary>
    /// The unused old point nearest the new one, both sides within 1/8, by
    /// the sum of the squared distances (FUN_180304230); -1 for none.
    /// </summary>
    internal static int NearestOldPoint(in CachedPoint p, in CachedManifold old, ReadOnlySpan<bool> used)
    {
        var best = float.MaxValue;
        var index = -1;
        var points = MemoryMarshal.CreateReadOnlySpan(in old.P0, 4);
        for (var j = 0; j < old.PointCount; j++)
        {
            if (used[j])
                continue;
            ref readonly var q = ref points[j];
            float ax = q.LocalA.X - p.LocalA.X, ay = q.LocalA.Y - p.LocalA.Y, az = q.LocalA.Z - p.LocalA.Z;
            float bx = q.LocalB.X - p.LocalB.X, by = q.LocalB.Y - p.LocalB.Y, bz = q.LocalB.Z - p.LocalB.Z;
            var da = (ax * ax + ay * ay) + az * az;
            var db = (bx * bx + by * by) + bz * bz;
            if (0.015625f >= da && 0.015625f >= db)
            {
                var s = db + da;
                if (best > s)
                {
                    best = s;
                    index = j;
                }
            }
        }
        return index;
    }
}
