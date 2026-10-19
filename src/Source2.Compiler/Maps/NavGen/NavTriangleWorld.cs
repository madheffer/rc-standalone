using System.Numerics;

namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// A stand-in for vphysics2's capsule cast, not a port of it (the world cast is 180201b00: the hull
/// shapes' cast 180251800, the mesh shapes' 180244dc0, with their margins): the capsule is blocked
/// when the parallelogram its axis sweeps comes within its radius of a triangle of the nav world
/// (the triangles of <see cref="NavGenInput.Extract"/>). It gives Valve's verdict on every cast of
/// the five captured maps that cast (62 casts, 14 blocked, on s2c_prefabprobe5); grazing contacts
/// may still differ.
/// </summary>
public sealed class NavTriangleWorld(Vector3[] verts, int[] tris) : INavJumpWorld
{
    public int Casts, Blocked;

    public bool CastBlocked(NavJumpCapsule c, (float X, float Y, float Z) from, (float X, float Y, float Z) to)
    {
        Casts++;
        var f = new Vector3d(from.X, from.Y, from.Z);
        var t = new Vector3d(to.X, to.Y, to.Z);
        var a0 = f + new Vector3d(0, 0, c.Z0);
        var a1 = f + new Vector3d(0, 0, c.Z1);
        var d = t - f;
        Vector3d[] quad = [a0, a1, a1 + d, a0 + d];
        var lo = Vector3d.Min(Vector3d.Min(quad[0], quad[1]), Vector3d.Min(quad[2], quad[3])) - new Vector3d(c.Radius, c.Radius, c.Radius);
        var hi = Vector3d.Max(Vector3d.Max(quad[0], quad[1]), Vector3d.Max(quad[2], quad[3])) + new Vector3d(c.Radius, c.Radius, c.Radius);
        for (var i = 0; i + 2 < tris.Length; i += 3)
        {
            var p = new Vector3d(verts[tris[i]]);
            var q = new Vector3d(verts[tris[i + 1]]);
            var r = new Vector3d(verts[tris[i + 2]]);
            var tlo = Vector3d.Min(Vector3d.Min(p, q), r);
            var thi = Vector3d.Max(Vector3d.Max(p, q), r);
            if (thi.X < lo.X || hi.X < tlo.X || thi.Y < lo.Y || hi.Y < tlo.Y || thi.Z < lo.Z || hi.Z < tlo.Z)
                continue;
            if (NavGeometry.TriTriDistance(quad[0], quad[1], quad[2], p, q, r) < c.Radius
                || NavGeometry.TriTriDistance(quad[0], quad[2], quad[3], p, q, r) < c.Radius)
            {
                Blocked++;
                return true;
            }
        }
        return false;
    }
}

/// <summary>Double-precision vector for the stand-in cast.</summary>
internal readonly record struct Vector3d(double X, double Y, double Z)
{
    public Vector3d(Vector3 v) : this(v.X, v.Y, v.Z) { }
    public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3d operator *(Vector3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static double Dot(Vector3d a, Vector3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vector3d Cross(Vector3d a, Vector3d b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static Vector3d Min(Vector3d a, Vector3d b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    public static Vector3d Max(Vector3d a, Vector3d b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
    public double Length => Math.Sqrt(Dot(this, this));
}

internal static class NavGeometry
{
    /// <summary>The closest point of triangle abc to p (Ericson 5.1.5).</summary>
    public static Vector3d ClosestOnTriangle(Vector3d p, Vector3d a, Vector3d b, Vector3d c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        double d1 = Vector3d.Dot(ab, ap), d2 = Vector3d.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0)
            return a;
        var bp = p - b;
        double d3 = Vector3d.Dot(ab, bp), d4 = Vector3d.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3)
            return b;
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
            return a + ab * (d1 / (d1 - d3));
        var cp = p - c;
        double d5 = Vector3d.Dot(ab, cp), d6 = Vector3d.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6)
            return c;
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
            return a + ac * (d2 / (d2 - d6));
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));
        var denom = 1.0 / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    /// <summary>The distance between segments p1q1 and p2q2 (Ericson 5.1.9).</summary>
    public static double SegmentSegment(Vector3d p1, Vector3d q1, Vector3d p2, Vector3d q2)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        double a = Vector3d.Dot(d1, d1), e = Vector3d.Dot(d2, d2), f = Vector3d.Dot(d2, r);
        double s, t;
        if (a <= 1e-12 && e <= 1e-12)
            return r.Length;
        if (a <= 1e-12)
        {
            s = 0;
            t = Math.Clamp(f / e, 0, 1);
        }
        else
        {
            var c = Vector3d.Dot(d1, r);
            if (e <= 1e-12)
            {
                t = 0;
                s = Math.Clamp(-c / a, 0, 1);
            }
            else
            {
                var b = Vector3d.Dot(d1, d2);
                var denom = a * e - b * b;
                s = denom != 0 ? Math.Clamp((b * f - c * e) / denom, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0)
                {
                    t = 0;
                    s = Math.Clamp(-c / a, 0, 1);
                }
                else if (t > 1)
                {
                    t = 1;
                    s = Math.Clamp((b - c) / a, 0, 1);
                }
            }
        }
        return ((p1 + d1 * s) - (p2 + d2 * t)).Length;
    }

    private static bool SegmentHitsTriangle(Vector3d p, Vector3d q, Vector3d a, Vector3d b, Vector3d c)
    {
        var n = Vector3d.Cross(b - a, c - a);
        double dp = Vector3d.Dot(p - a, n), dq = Vector3d.Dot(q - a, n);
        if (dp * dq > 0 || dp == dq)
            return false;
        var x = p + (q - p) * (dp / (dp - dq));
        return (ClosestOnTriangle(x, a, b, c) - x).Length < 1e-9;
    }

    /// <summary>The distance between two triangles: 0 when they cross, else the least edge/vertex distance.</summary>
    public static double TriTriDistance(Vector3d a0, Vector3d a1, Vector3d a2, Vector3d b0, Vector3d b1, Vector3d b2)
    {
        Vector3d[] a = [a0, a1, a2], b = [b0, b1, b2];
        for (var i = 0; i < 3; i++)
            if (SegmentHitsTriangle(a[i], a[(i + 1) % 3], b0, b1, b2) || SegmentHitsTriangle(b[i], b[(i + 1) % 3], a0, a1, a2))
                return 0;
        var best = double.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
                best = Math.Min(best, SegmentSegment(a[i], a[(i + 1) % 3], b[j], b[(j + 1) % 3]));
            best = Math.Min(best, (ClosestOnTriangle(a[i], b0, b1, b2) - a[i]).Length);
            best = Math.Min(best, (ClosestOnTriangle(b[i], a0, a1, a2) - b[i]).Length);
        }
        return best;
    }
}
