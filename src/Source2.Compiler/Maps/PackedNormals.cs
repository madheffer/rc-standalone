using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// Model vertex normal and tangent frames as meshsystem.dll unpacks them for
/// the resource compiler's prop geometry (build 10-01).
/// <list type="bullet">
/// <item>R32_UINT (format 0x2a, 180032730): bit 0 the tangent's sign, bits 1
/// to 11 the tangent's angle about the normal, bits 12 to 21 and 22 to 31 the
/// normal octahedron-encoded.</item>
/// <item>R8G8B8A8_UNORM (format 0x1c): at load (180049e20) each is decoded
/// (two bytes a vector, folded by sign twice, over 63, z = 1 - x - y) and
/// re-encoded to 0x2a (180032a20), so it reaches the compiler through the
/// 0x2a decode.</item>
/// </list>
/// </summary>
internal static class PackedNormals
{
    public static (Vector3 Normal, Vector4 Tangent) DecodeR32(uint bits)
    {
        var n = Octahedron((bits >> 12) & 0x3ff, bits >> 22);
        var (t, _) = Basis(n);
        var angle = (float)((bits >> 1) & 0x7ff) / 2047f * 6.2831855f;
        var s = MathF.Sin(angle);
        var c = MathF.Cos(angle);
        var tangent = new Vector4(
            ((((n.Y * t.Z) - (t.Y * n.Z)) * s) + (t.X * c)),
            ((((n.Z * t.X) - (t.Z * n.X)) * s) + (t.Y * c)),
            ((((t.Y * n.X) - (n.Y * t.X)) * s) + (t.Z * c)),
            (bits & 1) != 0 ? 1f : -1f);
        return (n, tangent);
    }

    /// <summary>A 0x1c frame as it reaches the compiler: decoded, re-encoded to 0x2a, decoded.</summary>
    public static (Vector3 Normal, Vector4 Tangent) DecodeR8G8B8A8(uint bits)
    {
        var (n, _) = Old(bits & 0xff, (bits >> 8) & 0xff);
        var (t, w) = Old((bits >> 16) & 0xff, bits >> 24);
        return DecodeR32(Encode(n, new Vector4(t, w)));
    }

    // 180049e20's two-byte decode: the vector and the second byte's sign.
    private static (Vector3, float) Old(uint a, uint b)
    {
        var x = (float)a - 128f;
        var y = (float)b - 128f;
        var s0 = x < 0f ? 1f : 0f;
        var s1 = y < 0f ? 1f : 0f;
        var zSign = -((s0 + s0) - 1f);
        var wSign = -((s1 + s1) - 1f);
        y = ((wSign * y) - s1) - 64f;
        x = ((zSign * x) - s0) - 64f;
        var s2 = x < 0f ? 1f : 0f;
        var s3 = y < 0f ? 1f : 0f;
        var ySign = -((s3 + s3) - 1f);
        var xSign = -((s2 + s2) - 1f);
        var yy = ((ySign * y) - s3) / 63f;
        var xx = ((xSign * x) - s2) / 63f;
        var z = (1f - xx) - yy;
        var inv = 1f / MathF.Sqrt((yy * yy) + (xx * xx) + (z * z));
        return (new Vector3(inv * xSign * xx, inv * ySign * yy, inv * zSign * z), wSign);
    }

    // 180032a20 with its third argument false.
    private static uint Encode(Vector3 n, Vector4 tangent)
    {
        var inv = 1f / (MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z));
        var v = n.Y * inv;
        float u;
        if (0f < n.Z || n.Z == 0f)
            u = n.X * inv;
        else
        {
            u = (1f - MathF.Abs(v)) * (0f <= n.X ? 1f : -1f);
            v = (1f - MathF.Abs(n.X * inv)) * (0f <= n.Y ? 1f : -1f);
        }
        var fu = Math.Clamp((u * 0.5f) + 0.5f, 0f, 1f);
        var fv = Math.Clamp((v * 0.5f) + 0.5f, 0f, 1f);
        var ub = (uint)(ulong)((fu * 1023f) + 0.5f);
        var vb = (uint)(ulong)((fv * 1023f) + 0.5f);
        var back = Octahedron(ub, vb);
        var (t, b) = Basis(back);
        var angle = 1.5707964f - Asin((t.Z * tangent.Z) + (t.Y * tangent.Y) + (t.X * tangent.X));
        if ((b.Z * tangent.Z) + (b.Y * tangent.Y) + (b.X * tangent.X) < 0f)
            angle = 6.2831855f - angle;
        // With SSE's comparisons and maxss/minss (the second operand when either
        // is NaN): a tangent along the basis makes the arcsine's root NaN and
        // the angle comes out at 180 degrees.
        angle *= 57.295776f;
        if (!(angle >= -180f) || !(180f >= angle))
        {
            angle -= MathF.Floor((angle * 0.0027777778f) + 0.5f) * 360f;
            angle = MaxSs(angle, -180f);
            angle = MinSs(angle, 180f);
        }
        if (0f > angle)
            angle += 360f;
        var f = angle / 360f;
        f = MinSs(MaxSs(f, 0f), 1f);
        f = MinSs(MaxSs(f, 0f), 1f);
        var ab = (uint)(long)((f * 2047f) + 0.5f);
        return ((ab | (((vb << 10) | ub) << 11)) * 2) | ((0f < tangent.W || tangent.W == 0f) ? 1u : 0u);
    }

    private static float MaxSs(float a, float b) => a > b ? a : b;

    private static float MinSs(float a, float b) => a < b ? a : b;

    // 180032e90: a fitted arcsine, the sign from the argument.
    private static float Asin(float d)
    {
        var x = MathF.Abs(d);
        var s = MathF.Sqrt(1f - (x * x));
        var r = MathF.Abs((((((((0.430477f - (x * 0.0594935f)) * x) - 1.24068f) * x) + 0.216183f) * x) + 1.00008f) / (1f - (x * 0.779384f)) - s);
        return d < 0f || BitConverter.SingleToInt32Bits(d) < 0 ? -r : r;
    }

    private static Vector3 Octahedron(uint ub, uint vb)
    {
        var x = (float)ub / 1023f;
        var y = (float)vb / 1023f;
        x = (x + x) - 1f;
        y = (y + y) - 1f;
        var ax = MathF.Abs(x);
        var z = 1f - (ax + MathF.Abs(y));
        if (z < 0f)
        {
            x = (1f - MathF.Abs(y)) * (0f <= x ? 1f : -1f);
            y = (1f - ax) * (0f <= y ? 1f : -1f);
        }
        return TJunctionFixNormalise(new Vector3(x, y, z));
    }

    // (z z + y y) + x x, times the reciprocal; outside 1e-17 to 1e17 the double path.
    private static Vector3 TJunctionFixNormalise(Vector3 d)
    {
        var len = MathF.Sqrt((d.Z * d.Z) + (d.Y * d.Y) + (d.X * d.X));
        if (1e-17f <= len && len <= 1e17f)
        {
            var inv = 1f / len;
            return new Vector3(d.X * inv, d.Y * inv, d.Z * inv);
        }
        return len == 0f ? Vector3.Zero : TJunctionFix.Normalise(d);
    }

    // 180030c50: an orthonormal basis about n.
    private static (Vector3 T, Vector3 B) Basis(Vector3 n)
    {
        var s = n.Z < 0f ? -1f : 1f;
        var a = -1f / (n.Z + s);
        var b = n.X * n.Y * a;
        return (new Vector3((n.X * s * n.X * a) + 1f, b * s, -s * n.X), new Vector3(b, (n.Y * n.Y * a) + s, -n.Y));
    }
}
