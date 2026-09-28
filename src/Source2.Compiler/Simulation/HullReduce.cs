using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Source2.Compiler.Simulation;

public static partial class HullCollision
{
    /// <summary>Distance and area tolerance of the reduction (0x1803daa94).</summary>
    private const float ReduceTolerance = 0.03125f;

    /// <summary>
    /// Culls and reduces the clipped polygon (FUN_1803bb850 with keep-few
    /// off, as both callers pass it). Points higher than
    /// <paramref name="tolerance"/> above the plane go (the last point fills
    /// the hole); the rest are projected onto it. Then, even for four or
    /// fewer: the point furthest along a fixed in-plane direction, the one
    /// furthest from it, the one making the largest triangle with those, and
    /// the one most outside that triangle. Returns the new count.
    /// </summary>
    /// <remarks>
    /// The direction is normalised with rsqrtps and one Newton step, so the
    /// first pick depends on the CPU's rsqrt table; this uses the running
    /// CPU's, as the DLL does.
    /// </remarks>
    internal static int Reduce(Span<ClipPoint> p, int count, in ClipPlane plane, float tolerance)
    {
        for (var i = count - 1; i >= 0; i--)
        {
            var w = p[i].W;
            if (w > tolerance)
            {
                if (count > 0)
                {
                    if (i != count - 1)
                        p[i] = p[count - 1];
                    count--;
                }
            }
            else
            {
                p[i].Y = p[i].Y - w * plane.Y;
                p[i].X = p[i].X - plane.X * w;
                p[i].Z = p[i].Z - plane.Z * w;
            }
        }
        if (count <= 1)
            return count;

        // First: furthest along n x axis, axis z when n leans to x, else x.
        var (ax, ay, az) = 0.57735f <= MathF.Abs(plane.X) ? (0f, 0f, 1f) : (1f, 0f, 0f);
        var dx = az * plane.Y - ay * plane.Z;
        var dy = ax * plane.Z - az * plane.X;
        var dz = ay * plane.X - ax * plane.Y;
        var lengthSquared = (dx * dx + dy * dy) + dz * dz;
        var r = ReciprocalSqrt(lengthSquared);
        var scale = (r * (3f - lengthSquared * (r * r))) * 0.5f;
        dx = scale * dx;
        dy = scale * dy;
        dz = scale * dz;
        var best = -float.MaxValue;
        var index = 0;
        for (var i = 0; i < count; i++)
        {
            var d = (p[i].Z * dz + p[i].Y * dy) + p[i].X * dx;
            if (d > best)
            {
                best = d;
                index = i;
            }
        }
        Swap(p, 0, index);

        // Second: furthest from the first.
        var p0 = p[0];
        best = -float.MaxValue;
        index = 0;
        for (var i = 1; i < count; i++)
        {
            var ex = p[i].X - p0.X;
            var ey = p[i].Y - p0.Y;
            var ez = p[i].Z - p0.Z;
            var d = MathF.Sqrt((ez * ez + ey * ey) + ex * ex);
            if (d > best)
            {
                best = d;
                index = i;
            }
        }
        if (ReduceTolerance > best)
            return 1;
        Swap(p, 1, index);

        // Third: the largest triangle with the first two, measured along n.
        var p1 = p[1];
        best = -float.MaxValue;
        index = 0;
        for (var i = 2; i < count; i++)
        {
            var area = MathF.Abs(TripleArea(p0, p1, p[i], plane.X, plane.Y, plane.Z));
            if (area > best)
            {
                best = area;
                index = i;
            }
        }
        var gx = p0.X - p1.X;
        var gy = p0.Y - p1.Y;
        var gz = p0.Z - p1.Z;
        var edge = MathF.Sqrt((gz * gz + gy * gy) + gx * gx);
        if ((edge + edge) * ReduceTolerance > best)
            return 2;
        Swap(p, 2, index);

        // Fourth: the point furthest outside the triangle.
        var p2 = p[2];
        var m = TriangleNormal(p0, p1, p2);
        var min = 0f;
        index = 0;
        for (var i = 3; i < count; i++)
        {
            var a = OutsideArea(p0, p1, p2, p[i], m);
            if (min > a)
            {
                min = a;
                index = i;
            }
        }
        if (min > (edge * -2f) * ReduceTolerance)
            return 3;
        Swap(p, 3, index);
        return 4;
    }

    private static void Swap(Span<ClipPoint> p, int i, int j) => (p[i], p[j]) = (p[j], p[i]);

    /// <summary>rsqrtps on this CPU.</summary>
    private static float ReciprocalSqrt(float x)
    {
        if (!Sse.IsSupported)
            throw new PlatformNotSupportedException("the contact reduction needs SSE's rsqrtps");
        return Sse.ReciprocalSqrt(Vector128.Create(x)).ToScalar();
    }

    /// <summary>n · ((p0 - p) x (p1 - p)), summed z, y, x.</summary>
    private static float TripleArea(in ClipPoint p0, in ClipPoint p1, in ClipPoint p, float nx, float ny, float nz)
    {
        var ax = p0.X - p.X;
        var ay = p0.Y - p.Y;
        var az = p0.Z - p.Z;
        var bx = p1.X - p.X;
        var by = p1.Y - p.Y;
        var bz = p1.Z - p.Z;
        var cx = ay * bz - az * by;
        var cy = az * bx - ax * bz;
        var cz = ax * by - ay * bx;
        return (nz * cz + ny * cy) + nx * cx;
    }

    /// <summary>The unit normal of (p1 - p0) x (p2 - p0), with the 1e-17 / 1e17 guard.</summary>
    private static Vec3 TriangleNormal(in ClipPoint p0, in ClipPoint p1, in ClipPoint p2)
    {
        var ax = p1.X - p0.X;
        var ay = p1.Y - p0.Y;
        var az = p1.Z - p0.Z;
        var bx = p2.X - p0.X;
        var by = p2.Y - p0.Y;
        var bz = p2.Z - p0.Z;
        var cx = ay * bz - az * by;
        var cy = az * bx - ax * bz;
        var cz = ax * by - ay * bx;
        var length = MathF.Sqrt((cz * cz + cy * cy) + cx * cx);
        if (1e-17f > length || length > 1e17f)
            return length == 0f ? default : NormalizeDouble(cx, cy, cz);
        var inv = 1f / length;
        return new(cx * inv, cy * inv, cz * inv);
    }

    /// <summary>Normalisation in double (FUN_18008d4e0), for lengths out of float's comfort.</summary>
    internal static Vec3 NormalizeDouble(float x, float y, float z)
    {
        double dx = x, dy = y, dz = z;
        var inv = 1.0 / Math.Sqrt((dy * dy + dx * dx) + dz * dz);
        return new((float)(inv * dx), (float)(inv * dy), (float)(inv * dz));
    }

    /// <summary>
    /// The smallest of p's signed areas against the triangle's three edges,
    /// along m (FUN_1803bb850's last loop, with its minss order).
    /// </summary>
    private static float OutsideArea(in ClipPoint p0, in ClipPoint p1, in ClipPoint p2, in ClipPoint p, Vec3 m)
    {
        float ax = p0.X - p.X, ay = p0.Y - p.Y, az = p0.Z - p.Z;
        float bx = p1.X - p.X, by = p1.Y - p.Y, bz = p1.Z - p.Z;
        float cx = p2.X - p.X, cy = p2.Y - p.Y, cz = p2.Z - p.Z;
        var a12 = ((cx * bz - cz * bx) * m.Y + (cy * bx - cx * by) * m.Z) + (cz * by - cy * bz) * m.X;
        var a20 = ((cx * ay - cy * ax) * m.Z + (cz * ax - cx * az) * m.Y) + (cy * az - cz * ay) * m.X;
        var a01 = ((bx * az - bz * ax) * m.Y + (by * ax - bx * ay) * m.Z) + (bz * ay - by * az) * m.X;
        var min = a20 < a01 ? a20 : a01;
        return a12 < min ? a12 : min;
    }
}
