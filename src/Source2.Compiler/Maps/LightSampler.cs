using System.Numerics;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.Maps;

/// <summary>
/// One ray of a light (FUN_181294fc0): from a point on its luminaire, or its
/// position, or its near plane, in a direction across its volume, and how far
/// the volume reaches along it. Float operations are in resourcecompiler's
/// order.
/// </summary>
public static class LightSampler
{
    /// <summary>g_flConfigMaxCoord.</summary>
    public const float MaxCoord = 16384f;

    private static float MaxDistance => (MaxCoord + MaxCoord) * 1.7320508f;

    private static float Sin(float x) => CrtMath.Sin(x);

    private static float Cos(float x) => CrtMath.Cos(x);

    /// <summary>
    /// FUN_18129a0e0: a point on the luminaire for (u, v) and its normal;
    /// false for a light without one (type 0).
    /// </summary>
    public static bool Luminaire(LightShape l, float u, float v, out Vector3 point, out Vector3 normal)
    {
        switch (l.Type)
        {
            case 1:
            {
                Vector3 c0 = l.V3(0xf0), c1 = l.V3(0xfc), c2 = l.V3(0x108), c3 = l.V3(0x114);
                var ax = (c1.X - c0.X) * u + c0.X;
                var az = (c1.Z - c0.Z) * u + c0.Z;
                var ay = (c1.Y - c0.Y) * u + c0.Y;
                point = new Vector3(
                    (((c3.X - c2.X) * u + c2.X) - ax) * v + ax,
                    (((c3.Y - c2.Y) * u + c2.Y) - ay) * v + ay,
                    (((c3.Z - c2.Z) * u + c2.Z) - az) * v + az);
                normal = l.V3(0x120);
                return true;
            }
            case 2:
            {
                var r = MathF.Sqrt(u);
                var a = v * 6.2831855f;
                var s = Sin(a) * r;
                var c = r * Cos(a);
                Vector3 centre = l.V3(0xf0), e1 = l.V3(0xfc), e2 = l.V3(0x108);
                point = new Vector3(
                    (s * e2.X + e1.X * c) + centre.X,
                    (e2.Y * s + e1.Y * c) + centre.Y,
                    (e2.Z * s + e1.Z * c) + centre.Z);
                normal = l.V3(0x114);
                return true;
            }
            case 3:
            {
                var q = v - v * v;
                var s = 0f <= q ? q : 0f;
                s = MathF.Sqrt(s);
                var a = u * 6.2831855f;
                var sin = Sin(a) * (s + s);
                var cos = Cos(a) * (s + s);
                var z = 1f - (v + v);
                var radius = l[0xfc];
                var centre = l.V3(0xf0);
                point = new Vector3(cos * radius + centre.X, sin * radius + centre.Y, radius * z + centre.Z);
                normal = new Vector3(cos, sin, z);
                return true;
            }
            case 4:
            case 5:
                (point, normal) = Capsule(l, u, v);
                return true;
            default:
                point = default;
                normal = default;
                return false;
        }
    }

    /// <summary>
    /// FUN_181299bc0: a capsule luminaire's point. Below the cap share (0x124)
    /// u picks a cap, the first half the start end and the second the other
    /// with the normal turned; the point is the end plus the forward and up
    /// axes on a circle of radius sqrt(s), the normal the axis between the
    /// ends. Above it the point is on the side: the ends lerped by v, plus
    /// the axes turned by 2 pi of u's share, which is also the normal. The
    /// binary never scales either by the radius (0x120).
    /// </summary>
    private static (Vector3 Point, Vector3 Normal) Capsule(LightShape l, float u, float v)
    {
        Vector3 p0 = l.V3(0xf0), p1 = l.V3(0xfc), fwd = l.V3(0x108), up = l.V3(0x114);
        var cap = l[0x124];
        if (u < cap)
        {
            var t = u / cap;
            if (t <= 0f)
                t = 0f;
            if (0.9999999f <= t)
                t = 0.9999999f;
            var normal = new Vector3(p0.X - p1.X, p0.Y - p1.Y, p0.Z - p1.Z);
            LightMath.Normalize(ref normal);
            var end = p0;
            var s = t + t;
            if (0.5f <= t)
            {
                end = p1;
                s -= 1f;
                normal = -normal;
            }
            if (s <= 0f)
                s = 0f;
            if (0.9999999f <= s)
                s = 0.9999999f;
            var r = MathF.Sqrt(s);
            var a = v * 6.2831855f;
            var sr = Sin(a) * r;
            var cr = Cos(a) * r;
            return (new Vector3((sr * up.X + fwd.X * cr) + end.X, (up.Y * sr + fwd.Y * cr) + end.Y,
                                (up.Z * sr + fwd.Z * cr) + end.Z), normal);
        }
        var w = (u - cap) / (1f - cap);
        if (w <= 0f)
            w = 0f;
        if (0.9999999f <= w)
            w = 0.9999999f;
        var angle = (w + w) * 3.1415927f;
        var c = Cos(angle);
        var sn = Sin(angle);
        var d = new Vector3(fwd.X * c + up.X * sn, fwd.Y * c + up.Y * sn, fwd.Z * c + up.Z * sn);
        var point = new Vector3(((p1.X - p0.X) * v + p0.X) + d.X, ((p1.Y - p0.Y) * v + p0.Y) + d.Y,
                                ((p1.Z - p0.Z) * v + p0.Z) + d.Z);
        LightMath.Normalize(ref d);
        return (point, d);
    }

    /// <summary>A clip-space point (x, y, z) through the inverse matrix, divided by w.</summary>
    private static Vector3 Unproject(LightShape l, float x, float y, float z, bool zIsZero)
    {
        // The inverse is at 0x40: rows 0x40, 0x50, 0x60 and w at 0x70.
        float m40 = l[0x40], m44 = l[0x44], m48 = l[0x48], m4c = l[0x4c];
        float m50 = l[0x50], m54 = l[0x54], m58 = l[0x58], m5c = l[0x5c];
        float m60 = l[0x60], m64 = l[0x64], m68 = l[0x68], m6c = l[0x6c];
        float m70 = l[0x70], m74 = l[0x74], m78 = l[0x78], m7c = l[0x7c];
        var tz = zIsZero ? 0f : 1f;
        var w = 1f / (((y * m74 + x * m70) + m78 * tz) + m7c);
        return new Vector3(
            (((y * m44 + x * m40) + m48 * tz) + m4c) * w,
            (((x * m50 + y * m54) + m58 * tz) + m5c) * w,
            (((x * m60 + y * m64) + m68 * tz) + m6c) * w);
    }

    /// <summary>A frustum corner: the clip point (sx, sy, sz) with sx, sy = +-1, through the inverse.</summary>
    private static Vector3 Corner(LightShape l, float sx, float sy, float sz)
    {
        float m40 = l[0x40], m44 = l[0x44], m48 = l[0x48], m4c = l[0x4c];
        float m50 = l[0x50], m54 = l[0x54], m58 = l[0x58], m5c = l[0x5c];
        float m60 = l[0x60], m64 = l[0x64], m68 = l[0x68], m6c = l[0x6c];
        float m70 = l[0x70], m74 = l[0x74], m78 = l[0x78], m7c = l[0x7c];
        float Pair(float a, float b) => sx < 0f ? (sy < 0f ? a * -1f + b * -1f : a * -1f + b) : (sy < 0f ? b * -1f + a : a + b);
        var tz = sz == 0f ? m78 * 0f : m78;
        var w = 1f / ((Pair(m70, m74) + tz) + m7c);
        return new Vector3(
            ((Pair(m40, m44) + (sz == 0f ? m48 * 0f : m48)) + m4c) * w,
            ((Pair(m50, m54) + (sz == 0f ? m58 * 0f : m58)) + m5c) * w,
            ((Pair(m60, m64) + (sz == 0f ? m68 * 0f : m68)) + m6c) * w);
    }

    /// <summary>
    /// FUN_181294fc0: the ray for the four Halton numbers h. Returns how far it
    /// reaches (0 or less: no ray). A directional volume sampled without a
    /// luminaire reaches its range; a luminaire's ray stops at the first of
    /// the frustum's five far and side planes and at the range sphere.
    /// </summary>
    public static float Sample(LightShape l, ReadOnlySpan<float> h, out Vector3 start, out Vector3 dir)
    {
        var lum = Luminaire(l, h[0], h[1], out var lp, out var ln);
        var directional = (l.Flags & 1) != 0;
        if (!lum)
        {
            if (!directional)
            {
                var x = h[0] * 2f - 1f;
                var y = h[1] * 2f - 1f;
                start = Unproject(l, x, y, 0f, true);
                var end = Unproject(l, x, y, 1f, false);
                dir = new Vector3(end.X - start.X, end.Y - start.Y, end.Z - start.Z);
                return LightMath.Normalize(ref dir);
            }
            start = l.V3(0x90);
            var c = l[0xe4] != 0f ? l[0xe0] / l[0xe4] : l[0xe0] > 0f ? -1.1f : 1.1f;
            var u = h[0];
            var o = l.Orientation;
            var up = LightMath.Up(o);
            var left = LightMath.Left(o);
            var fwd = LightMath.Forward(o);
            c = c > -1f ? c : -1f;
            c = c < 1f ? c : 1f;
            var z = c * u + (1f - u);
            var s = MathF.Sqrt(1f - z * z);
            var a = h[1] * 6.2831855f;
            var ss = Sin(a) * s;
            var cs = Cos(a) * s;
            dir = new Vector3(
                (cs * left.X + ss * up.X) + z * fwd.X,
                (ss * up.Y + cs * left.Y) + z * fwd.Y,
                (ss * up.Z + cs * left.Z) + z * fwd.Z);
            if (l[0x9c] == 0f || l[0xcc] == 0f)
                return float.MaxValue;
            return -l[0xc8] / l[0xcc];
        }
        if (!directional)
        {
            var x = (h[2] + h[2]) - 1f;
            var y = (h[3] + h[3]) - 1f;
            var w = 1f / (((x * l[0x70] + y * l[0x74]) + l[0x78] * 0f) + l[0x7c]);
            start = new Vector3(
                (((y * l[0x44] + x * l[0x40]) + l[0x48] * 0f) + l[0x4c]) * w,
                (((x * l[0x50] + y * l[0x54]) + l[0x58] * 0f) + l[0x5c]) * w,
                (((x * l[0x60] + y * l[0x64]) + l[0x68] * 0f) + l[0x6c]) * w);
            if (l[0x9c] == 0f)
                dir = new Vector3(-lp.X, -lp.Y, -lp.Z);
            else
                dir = SafeNormal(new Vector3(start.X - lp.X, start.Y - lp.Y, start.Z - lp.Z));
            if (!(0f < (ln.Z * dir.Z + ln.Y * dir.Y) + dir.X * ln.X))
                return 0f;
        }
        else
        {
            start = lp;
            var t = h[3];
            Basis(ln, out var b1, out var b2);
            var q = 1f - t * t;
            var r = MathF.Sqrt(0f > q ? 0f : q);
            var a = h[2] * 6.2831855f;
            var sr = Sin(a) * r;
            var cr = Cos(a) * r;
            dir = new Vector3(
                (cr * b1.X + sr * b2.X) + ln.X * t,
                (cr * b1.Y + sr * b2.Y) + ln.Y * t,
                (sr * b2.Z + cr * b1.Z) + ln.Z * t);
        }
        return Reach(l, start, dir);
    }

    /// <summary>FUN_1801fea30: the unit vector, or zero when it has no length.</summary>
    private static Vector3 SafeNormal(Vector3 v)
    {
        var length = MathF.Sqrt((v.Z * v.Z + v.Y * v.Y) + v.X * v.X);
        if (length == 0f)
            return Vector3.Zero;
        if (length < 1e-17f || 1e17f < length)
            throw new NotSupportedException("normalising a vector longer than 1e17 or shorter than 1e-17 (FUN_18125d000) is not ported");
        var inverse = 1f / length;
        return new Vector3(v.X * inverse, v.Y * inverse, v.Z * inverse);
    }

    /// <summary>FUN_18125a6c0: two axes perpendicular to a unit normal (Frisvad's, with the sign of z).</summary>
    public static void Basis(Vector3 n, out Vector3 b1, out Vector3 b2)
    {
        var s = n.Z < 0f ? -1f : 1f;
        var k = -1f / (n.Z + s);
        var a = n.X * n.Y * k;
        b1 = new Vector3(n.X * s * n.X * k + 1f, a * s, -s * n.X);
        b2 = new Vector3(a, n.Y * n.Y * k + s, -n.Y);
    }

    /// <summary>The rest of FUN_181294fc0 for a luminaire: the nearest of the five planes ahead, then the range sphere.</summary>
    private static float Reach(LightShape l, Vector3 start, Vector3 dir)
    {
        var c0 = Corner(l, -1f, -1f, 0f);
        var c1 = Corner(l, 1f, -1f, 0f);
        var c2 = Corner(l, 1f, 1f, 0f);
        var c3 = Corner(l, -1f, 1f, 0f);
        var c4 = Corner(l, -1f, -1f, 1f);
        var c5 = Corner(l, 1f, -1f, 1f);
        var c6 = Corner(l, 1f, 1f, 1f);
        var c7 = Corner(l, -1f, 1f, 1f);
        Span<Vector4> planes =
        [
            Plane(c0, c4, c3), Plane(c1, c5, c0), Plane(c2, c6, c1), Plane(c3, c7, c2), Plane(c4, c5, c6),
        ];
        var reach = MaxDistance;
        foreach (var p in planes)
        {
            var t = RayPlane(start, dir, p);
            if (0f < t)
                reach = reach < t ? reach : t;
        }
        if (l[0x9c] == 0f)
            return reach;
        var radius = l[0xcc] == 0f ? float.MaxValue : -l[0xc8] / l[0xcc];
        var limit = MaxDistance;
        limit = limit < radius ? limit : radius;
        if (RaySphere(start, dir, l.V3(0x90), limit, out _, out var t1))
        {
            var exit = t1 > 0f ? t1 : 0f;
            reach = reach < exit ? reach : exit;
        }
        return reach;
    }

    /// <summary>The plane through a with normal (b - a) x (c - a), unit length, and its distance.</summary>
    private static Vector4 Plane(Vector3 a, Vector3 b, Vector3 c)
    {
        var n = new Vector3(
            (b.Z - a.Z) * (c.Y - a.Y) - (c.Z - a.Z) * (b.Y - a.Y),
            (c.Z - a.Z) * (b.X - a.X) - (b.Z - a.Z) * (c.X - a.X),
            (b.Y - a.Y) * (c.X - a.X) - (c.Y - a.Y) * (b.X - a.X));
        LightMath.Normalize(ref n);
        return new Vector4(n, (a.Y * n.Y + a.Z * n.Z) + a.X * n.X);
    }

    /// <summary>FUN_18129b520: where the ray meets the plane, 0 when parallel.</summary>
    private static float RayPlane(Vector3 start, Vector3 dir, Vector4 p)
    {
        var denominator = (dir.Z * p.Z + dir.Y * p.Y) + dir.X * p.X;
        if (denominator == 0f)
            return 0f;
        return (p.W - ((start.Z * p.Z + p.Y * start.Y) + start.X * p.X)) * (1f / denominator);
    }

    /// <summary>FUN_1812651f0: where the ray enters and leaves the sphere; false when it misses.</summary>
    public static bool RaySphere(Vector3 start, Vector3 dir, Vector3 centre, float radius, out float t0, out float t1)
    {
        var fx = start.X - centre.X;
        var fz = start.Z - centre.Z;
        var fy = start.Y - centre.Y;
        var a = (dir.Z * dir.Z + dir.Y * dir.Y) + dir.X * dir.X;
        if (a == 0f)
        {
            t0 = t1 = 0f;
            return (fx * fx + fy * fy) + fz * fz <= radius * radius;
        }
        var ia = 1f / a;
        var b = (fz * dir.Z + fy * dir.Y) + fx * dir.X;
        b = -(b + b) * ia * 0.5f;
        var px = dir.X * b + fx;
        var py = dir.Y * b + fy;
        var pz = dir.Z * b + fz;
        var disc = radius * radius - ((px * px + py * py) + pz * pz);
        if (!(0f <= disc))
        {
            t0 = t1 = 0f;
            return false;
        }
        var s = MathF.Sqrt(disc * ia);
        t0 = b - s;
        t1 = s + b;
        return true;
    }
}
