using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The packed tangent frame a world node vertex carries in its NORMAL
/// element (R32_UINT, <c>OptionallyCompressedTangentFram</c>), as the packed
/// vertex buffer writer (FUN_181365ce0) fills it through
/// <c>CompressTangentFrame</c> (182067e80) on its fast path. Float
/// operations are in the binary's order (read from its disassembly).
/// </summary>
/// <remarks>
/// Bits 22-31 and 12-21 hold the octahedral normal, each axis
/// <c>(int)(clamp(v * 0.5 + 0.5) * 1023 + 0.5)</c>. The normal is rebuilt from
/// those bits and normalised, and bits 1-11 hold the tangent's angle about
/// it, measured from <c>Vector_Basis</c>'s first axis with an approximate
/// asin (FUN_181272450) and scaled to 2047. Bit 0 is set when the tangent's
/// w is not negative. Checked against probe01's cluster model.
/// </remarks>
public static class TangentFrame
{
    public static uint Compress(Vector3 normal, Vector4 tangent)
    {
        float x = normal.X, y = normal.Y, z = normal.Z;
        var inv = 1f / ((MathF.Abs(x) + MathF.Abs(y)) + MathF.Abs(z));
        var oy = y * inv;
        float ox;
        if (0f > z)
        {
            var oya = MathF.Abs(oy);
            var sx = 0f > x ? -1f : 1f;
            var xa = MathF.Abs(x * inv);
            ox = (1f - oya) * sx;
            oy = (1f - xa) * (0f > y ? -1f : 1f);
        }
        else
            ox = x * inv;
        var u = Clamp01(ox * 0.5f + 0.5f);
        var v = Clamp01(oy * 0.5f + 0.5f);
        var qu = (uint)(long)(u * 1023f + 0.5f);
        var qv = (uint)(long)(v * 1023f + 0.5f);

        // The normal those bits decode to.
        var fx = (float)qu / 1023f;
        var fy = (float)qv / 1023f;
        fx = (fx + fx) - 1f;
        fy = (fy + fy) - 1f;
        var ax = MathF.Abs(fx);
        var ay = MathF.Abs(fy);
        var fz = 1f - (ax + ay);
        float nx = fx, ny = fy;
        if (0f > fz)
        {
            nx = (1f - ay) * (0f > fx ? -1f : 1f);
            ny = (1f - ax) * (0f > fy ? -1f : 1f);
        }
        var n = new Vector3(nx, ny, fz);
        var length = MathF.Sqrt((n.Y * n.Y + n.Z * n.Z) + n.X * n.X);
        if (1e-17f <= length && length <= 1e17f)
        {
            var il = 1f / length;
            n = new Vector3(n.X * il, n.Y * il, il * n.Z);
        }
        else if (length == 0f)
            n = Vector3.Zero;
        else
            throw new NotSupportedException("normalising a vector longer than 1e17 or shorter than 1e-17 (FUN_18125d000) is not ported");

        var (b1, b2) = Basis(n);
        var d1 = (b1.Z * tangent.Z + b1.Y * tangent.Y) + b1.X * tangent.X;
        var angle = 1.5707964f - Asin(d1);
        var d2 = (b2.Z * tangent.Z + b2.Y * tangent.Y) + b2.X * tangent.X;
        if (0f > d2)
            angle = 6.2831855f - angle;
        var degrees = angle * 57.295776f;
        if (degrees < -180f || !(180f >= degrees))
        {
            degrees -= MathF.Floor(degrees * 0.0027777778f + 0.5f) * 360f;
            degrees = Max(degrees, -180f);
            degrees = Min(degrees, 180f);
        }
        if (0f > degrees)
            degrees += 360f;
        degrees /= 360f;
        degrees = Clamp01(Clamp01(degrees));
        var q = (uint)(long)(degrees * 2047f + 0.5f);
        return ((q | (((qv << 10) | qu) << 11)) << 1) | (0f <= tangent.W ? 1u : 0u);
    }

    /// <summary>Vector_Basis (18125a6c0): two axes perpendicular to a unit normal.</summary>
    internal static (Vector3 First, Vector3 Second) Basis(Vector3 n)
    {
        var s = n.Z < 0f ? -1f : 1f;
        var k = -1f / (n.Z + s);
        var xy = (n.X * n.Y) * k;
        var first = new Vector3(((n.X * s) * n.X) * k + 1f, xy * s, (-s) * n.X);
        var second = new Vector3(xy, (n.Y * n.Y) * k + s, -n.Y);
        return (first, second);
    }

    /// <summary>FUN_181272450: asin by a rational polynomial, sign of the argument.</summary>
    internal static float Asin(float x)
    {
        var a = MathF.Abs(x);
        var p = 0.430477f - a * 0.0594935f;
        p = p * a - 1.24068f;
        p = p * a + 0.216183f;
        p = p * a + 1.00008f;
        var r = p / (1f - a * 0.779384f) - MathF.Sqrt(1f - a * a);
        return MathF.CopySign(MathF.Abs(r), x);
    }

    // maxss / minss against 0 and 1: the second operand unless the first is strictly greater (less).
    private static float Clamp01(float v) => Min(Max(v, 0f), 1f);

    private static float Max(float a, float b) => a > b ? a : b;

    private static float Min(float a, float b) => a < b ? a : b;
}
