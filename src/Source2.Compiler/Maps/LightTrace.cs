using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>What the light precompute traces against: how far along a segment the first blocking hit is.</summary>
public interface ILightTracer
{
    /// <summary>The distance from start towards end to the first blocking hit, or null for none.</summary>
    float? Trace(Vector3 start, Vector3 end);
}

/// <summary>A scene with nothing in it.</summary>
public sealed class EmptyLightScene : ILightTracer
{
    public static readonly EmptyLightScene Instance = new();

    public float? Trace(Vector3 start, Vector3 end) => null;
}

/// <summary>
/// The light precompute's sampling and fitting (LightPrecompute_TraceSamples
/// FUN_180f19840 and LightPrecompute_TraceRay FUN_180f18820): rays spread over
/// the light by Halton numbers, kept where the light reaches, cut short where
/// the scene blocks them; the box and oriented box of what they reach.
/// </summary>
public static class LightTrace
{
    /// <summary>What a light reaches: its bounds (grown by 1/16) and oriented box (half extents grown by 1/16).</summary>
    public readonly record struct Result(Vector3 Mins, Vector3 Maxs, LightObb.Box Box, int Failed);

    /// <summary>FUN_180251040: a point through the 4x4 at 0, divided by w.</summary>
    public static Vector3 Project(LightShape l, Vector3 p)
    {
        var m = l.Matrix;
        var w = 1f / (((p.X * m[12] + p.Y * m[13]) + p.Z * m[14]) + m[15]);
        return new Vector3(
            (((p.Y * m[1] + p.X * m[0]) + p.Z * m[2]) + m[3]) * w,
            (((p.X * m[4] + p.Y * m[5]) + p.Z * m[6]) + m[7]) * w,
            (((p.X * m[8] + p.Y * m[9]) + p.Z * m[10]) + m[11]) * w);
    }

    private static float Smooth(float x)
    {
        if (x <= 0f)
            x = 0f;
        if (1f <= x)
            x = 1f;
        return (3f - (x + x)) * (x * x);
    }

    /// <summary>
    /// FUN_1812933f0: how much of the light reaches <paramref name="mid"/>:
    /// the skirts at the near and far ends (by clip depth), the inverse square
    /// held flat inside the position's w, the distance cut-off, and a cone
    /// about the forward axis from the nearest point of the capsule segment.
    /// </summary>
    public static float Falloff(LightShape l, Vector3 clip, Vector3 mid, bool force = false)
    {
        bool square, cone;
        if (force)
            (square, cone) = (true, true);
        else
            (square, cone) = l.Type switch { 0 => (true, true), 1 => (false, false), 2 => (false, false), _ => (false, true) };
        var f = 1f;
        if (0f < l[0xac])
            f = Smooth(l[0xac] * clip.Z - 0f);
        if (0f < l[0xb0])
            f *= Smooth((1f - clip.Z) * l[0xb0] - 0f);
        var w = l[0x9c];
        if (w == 0f)
            return f;
        var pos = l.V3(0x90);
        var dx = pos.X - mid.X;
        var dy = pos.Y - mid.Y;
        var dz = pos.Z - mid.Z;
        var d2 = (dz * dz + dy * dy) + dx * dx;
        if (square)
            f *= w / (d2 > w ? d2 : w);
        var dist = MathF.Sqrt(d2);
        f *= Smooth((dist * l[0xcc] + l[0xc8]) - 0f);
        if (cone)
        {
            var left = LightMath.Left(l.Orientation);
            var half = l[0xe8];
            float ax = left.X * half, ay = left.Y * half, az = left.Z * half;
            float bx = dx - ax, by = dy - ay, bz = dz - az;
            float sx = (dx + ax) - bx, sy = (dy + ay) - by, sz = (dz + az) - bz;
            var num = (((0f - bz) * sz) + ((0f - by) * sy)) + ((0f - bx) * sx);
            var len2 = (sy * sy + sx * sx) + sz * sz;
            var t = num / (1.17549435e-38f > len2 ? 1.17549435e-38f : len2);
            t = 1f < t ? 1f : t;
            t = 0f > t ? 0f : t;
            var p = new Vector3(sx * t + bx, sy * t + by, sz * t + bz);
            var length = MathF.Sqrt((p.Y * p.Y + p.Z * p.Z) + p.X * p.X);
            if (1e-17f <= length && length <= 1e17f)
            {
                var inverse = 1f / length;
                p = new Vector3(p.X * inverse, p.Y * inverse, p.Z * inverse);
            }
            else if (length == 0f)
                p = Vector3.Zero;
            else
                throw new NotSupportedException("normalising a vector longer than 1e17 or shorter than 1e-17 (FUN_18125d000) is not ported");
            var fwd = LightMath.Forward(l.Orientation);
            var c = ((p.Z * fwd.Z + p.Y * fwd.Y) + p.X * fwd.X) * l[0xe4] + l[0xe0];
            c = c > 0f ? c : 0f;
            c = c < 1f ? c : 1f;
            f *= c;
        }
        return f;
    }

    /// <summary>
    /// FUN_181293b70: the barn's cross-section at a clip point: soft square
    /// edges for shape 0, else a superellipse of exponent 2 / shape (powf).
    /// </summary>
    public static float Shape(LightShape l, Vector3 clip)
    {
        var ax = MathF.Abs(clip.X);
        var ay = MathF.Abs(clip.Y);
        float sx = l[0xa0], sy = l[0xa4], shape = l[0xa8];
        if (shape == 0f)
        {
            var y = (ay - 1f) / (sy - 1f);
            var x = (ax - 1f) / (sx - 1f);
            y = 1f < (y > 0f ? y : 0f) ? 1f : (y > 0f ? y : 0f);
            x = 1f < (x > 0f ? x : 0f) ? 1f : (x > 0f ? x : 0f);
            return ((3f - (y + y)) * (y * y)) * ((3f - (x + x)) * (x * x));
        }
        var e = 2f / shape;
        var p1 = LightPow.Pow(ay * sx, e);
        var p2 = LightPow.Pow(ax * sy, e);
        var s = p2 + p1;
        s = s > 1.17549435e-38f ? s : 1.17549435e-38f;
        var k = LightPow.Pow(s, shape * -0.5f) * (sy * sx);
        var p3 = LightPow.Pow(ax, 2f / shape);
        var p4 = LightPow.Pow(ay, 2f / shape);
        var s2 = p3 + p4;
        s2 = s2 > 1.17549435e-38f ? s2 : 1.17549435e-38f;
        var q = LightPow.Pow(s2, shape * -0.5f);
        if (q > k)
        {
            var t = (1f - q) / (k - q);
            t = t > 0f ? t : 0f;
            t = t < 1f ? t : 1f;
            return (3f - (t + t)) * (t * t);
        }
        return q > 1f ? 1f : 0f;
    }

    /// <summary>FUN_18125d8e0: the radical inverse of <paramref name="index"/> in <paramref name="radix"/>, in float.</summary>
    public static float Halton(int radix, int index)
    {
        var fr = (float)radix;
        var r = 0f;
        var f = 1f / fr;
        while (index != 0)
        {
            var digit = index % radix;
            index /= radix;
            r += digit * f;
            f /= fr;
        }
        return r;
    }

    /// <summary>
    /// LightPrecompute_TraceSamples: <paramref name="count"/> rays. Each takes
    /// the next Halton numbers in 2, 3, 5 and 7 (the counters start at 1) until
    /// the light reaches it (the sampler's reach above 0, then falloff times
    /// shape at the segment's middle above 0, the falloff with the skirts off),
    /// at most 102 tries; a ray that never does keeps its last try. Then
    /// LightPrecompute_TraceRay: each ray is cut at its first hit, and the
    /// light's position (the first ray's start) with every other start that
    /// differs from it and every end make the points the boxes are fitted to.
    /// </summary>
    public static Result Run(LightShape light, int count, ILightTracer scene)
    {
        var falloff = light.Clone();
        falloff[0xac] = 0f;
        falloff[0xb0] = 0f;
        var starts = new Vector3[count];
        var ends = new Vector3[count];
        int i2 = 1, i3 = 1, i5 = 1, i7 = 1;
        var failed = 0;
        Span<float> h = stackalloc float[4];
        for (var k = 0; k < count; k++)
        {
            for (var attempt = 0; ; attempt++)
            {
                h[3] = Halton(7, i7++);
                h[2] = Halton(5, i5++);
                h[1] = Halton(3, i3++);
                h[0] = Halton(2, i2++);
                var t = LightSampler.Sample(light, h, out var s, out var d);
                starts[k] = s;
                ends[k] = d;
                var ok = false;
                if (t > 0f)
                {
                    var e = new Vector3(t * d.X + s.X, t * d.Y + s.Y, t * d.Z + s.Z);
                    ends[k] = e;
                    var mid = new Vector3((e.X + s.X) * 0.5f, (s.Y + e.Y) * 0.5f, (s.Z + e.Z) * 0.5f);
                    var clip = Project(light, mid);
                    var f = Falloff(falloff, clip, mid);
                    var shape = Shape(light, clip);
                    ok = !(f * shape <= 0f);
                }
                if (ok)
                    break;
                if (attempt >= 0x65)
                {
                    failed++;
                    break;
                }
            }
        }

        var points = new List<Vector3> { starts[0] };
        for (var k = 0; k < count; k++)
        {
            var s = starts[k];
            var e = ends[k];
            var dx = e.X - s.X;
            var dy = e.Y - s.Y;
            var dz = e.Z - s.Z;
            var length = MathF.Sqrt((dy * dy + dz * dz) + dx * dx);
            Vector3 unit;
            if (1e-17f <= length && length <= 1e17f)
            {
                var inverse = 1f / length;
                unit = new Vector3(dx * inverse, dy * inverse, dz * inverse);
            }
            else if (length == 0f)
                unit = Vector3.Zero;
            else
                throw new NotSupportedException("normalising a vector longer than 1e17 or shorter than 1e-17 (FUN_18125d000) is not ported");
            if (scene.Trace(s, e) is { } hit)
                length = hit <= length ? hit : length;
            if (!(s.X == starts[0].X && s.Y == starts[0].Y && s.Z == starts[0].Z))
                points.Add(s);
            var end = new Vector3(unit.X * length + s.X, unit.Y * length + s.Y, unit.Z * length + s.Z);
            ends[k] = end;
            points.Add(end);
        }

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = -float.MaxValue, maxY = -float.MaxValue, maxZ = -float.MaxValue;
        foreach (var p in points)
        {
            minX = minX < p.X ? minX : p.X;
            minY = minY < p.Y ? minY : p.Y;
            minZ = minZ < p.Z ? minZ : p.Z;
            maxX = maxX > p.X ? maxX : p.X;
            maxY = maxY > p.Y ? maxY : p.Y;
            maxZ = maxZ > p.Z ? maxZ : p.Z;
        }
        var box = LightObb.Fit(points);
        box = box with { Extent = new Vector3(box.Extent.X + 0.0625f, box.Extent.Y + 0.0625f, box.Extent.Z + 0.0625f) };
        return new Result(new Vector3(minX - 0.0625f, minY - 0.0625f, minZ - 0.0625f),
                          new Vector3(maxX + 0.0625f, maxY + 0.0625f, maxZ + 0.0625f), box, failed);
    }
}
