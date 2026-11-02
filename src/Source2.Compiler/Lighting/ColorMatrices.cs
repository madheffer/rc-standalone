namespace Source2.Compiler.Lighting;

/// <summary>
/// materialsystem2's colour matrix expression functions (the 1015 and 1029 builds, same addresses): MatrixMultiply
/// (18009bf90), MatrixColorTint2 (18009c3c0 -> 1800818d0) and MatrixColorCorrect2 (18009c090 -> 180081260). Matrices
/// are 16 floats row after row, as the functions build them; every operation is single precision in the
/// disassembly's order (Ghidra's decompile regroups some of the products). The 4 x 4 product is 180081100's: each
/// element a dpps of a row of the left with a column of the right, ((p0 + p1) + (p2 + p3)). Checked: ze_hold_em_p's
/// csgo_environment g_mTextureColorAdjust1 is the game's bit for bit (ledger 196).
/// </summary>
public static class ColorMatrices
{
    private static readonly float[] Luminance = [0.2125f, 0.7154f, 0.0721f];

    private static float Dpps(float a0, float a1, float a2, float a3, float b0, float b1, float b2, float b3)
    {
        var p0 = a0 * b0;
        var p1 = a1 * b1;
        var p2 = a2 * b2;
        var p3 = a3 * b3;
        return (p0 + p1) + (p2 + p3);
    }

    /// <summary>180081100: a times b.</summary>
    public static float[] Multiply(float[] a, float[] b)
    {
        var m = new float[16];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                m[i * 4 + j] = Dpps(a[i * 4], a[i * 4 + 1], a[i * 4 + 2], a[i * 4 + 3], b[j], b[4 + j], b[8 + j], b[12 + j]);
        return m;
    }

    private static float[] Diagonal(float x, float y, float z, float w = 1f) => [x, 0, 0, 0, 0, y, 0, 0, 0, 0, z, 0, 0, 0, 0, w];

    private static float[] Translation(float x, float y, float z) => [1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1];

    private static float[] Transpose(float[] m) => [.. Enumerable.Range(0, 16).Select(k => m[(k % 4) * 4 + k / 4])];

    /// <summary>1800828c0: the rotation taking unit vector a onto unit vector b (identity when they are parallel).</summary>
    private static float[] AxisToAxis(float[] a, float[] b)
    {
        var cy = a[2] * b[0] - b[2] * a[0];
        var cx = b[2] * a[1] - a[2] * b[1];
        var cz = b[1] * a[0] - a[1] * b[0];
        var sin = MathF.Sqrt((cz * cz + cy * cy) + cx * cx);
        var cos = (a[2] * b[2] + b[1] * a[1]) + a[0] * b[0];
        var k = 1f - cos;
        if (!(sin > 0f))
            return Diagonal(1, 1, 1);
        var inv = 1f / sin;
        var ax = inv * cx;
        var ay = inv * cy;
        var az = inv * cz;
        var axk = ax * k;
        var ayzk = az * (ay * k);
        var m = new float[16];
        m[0] = ax * axk + cos;
        m[1] = ay * axk - az * sin;
        m[4] = ay * axk + az * sin;
        m[5] = ay * (ay * k) + cos;
        m[2] = az * axk + ay * sin;
        m[8] = az * axk - ay * sin;
        m[9] = ayzk + ax * sin;
        m[6] = ayzk - ax * sin;
        m[10] = az * k * az + cos;
        m[15] = 1f;
        return m;
    }

    /// <summary>180082c50: m's upper 3 x 4 applied to the point t.</summary>
    private static float[] Affine(float[] m, float[] t) =>
    [
        ((t[1] * m[1] + t[0] * m[0]) + t[2] * m[2]) + m[3],
        ((t[0] * m[4] + t[1] * m[5]) + t[2] * m[6]) + m[7],
        ((t[0] * m[8] + t[1] * m[9]) + t[2] * m[10]) + m[11],
    ];

    /// <summary>180083390: (hue in degrees, saturation, value).</summary>
    private static (float H, float S, float V) Hsv(float r, float g, float b)
    {
        var max = r <= g ? g : r;
        var min = g <= r ? g : r;
        if (max <= b)
            max = b;
        if (b <= min)
            min = b;
        var s = max != 0f ? (max - min) / max : 0f;
        if (s == 0f)
            return (0f, s, max);
        var d = max - min;
        var h = r != max ? (g != max ? (r - g) / d + 4f : (b - r) / d + 2f) : (g - b) / d;
        h *= 60f;
        return (h < 0f ? h + 360f : h, s, max);
    }

    /// <summary>The luminance axis normalized: 1800818d0 from the compiler's folded sum of squares, 180080f40 at run time.</summary>
    private static float[] LuminanceAxis(bool folded)
    {
        var (x, y, z) = (Luminance[0], Luminance[1], Luminance[2]);
        var length = MathF.Sqrt(folded ? BitConverter.Int32BitsToSingle(0x3f0fe92e) : (y * y + z * z) + x * x);
        var inv = 1f / length;
        return [inv * x, inv * y, inv * z];
    }

    /// <summary>MatrixColorTint2(tint, amount), 1800818d0.</summary>
    public static float[] Tint2(float[] tint, float amount)
    {
        var (_, s, _) = Hsv(tint[0], tint[1], tint[2]);
        var l = LuminanceAxis(folded: true);
        var r = AxisToAxis(l, [0f, 0f, 1f]);
        var p = Affine(Multiply(r, Diagonal(l[0], l[1], l[2])), tint);
        var keep = 1f - Maps.LightPow.Pow(s, 2f) * amount;
        var m = Multiply(Diagonal(1f / l[0], 1f / l[1], 1f / l[2]), Transpose(r));
        m = Multiply(m, Translation(p[0], p[1], (1f - keep) * p[2]));
        m = Multiply(m, Diagonal(1f - s, 1f - s, keep));
        m = Multiply(m, Translation(-p[0], -p[1], 0f));
        m = Multiply(m, r);
        return Multiply(m, Diagonal(l[0], l[1], l[2]));
    }

    /// <summary>MatrixColorCorrect2(float3(contrast, saturation, brightness), average colour), 180081260.</summary>
    public static float[] Correct2(float contrast, float saturation, float brightness, float[] average)
    {
        var a = Multiply(Translation(average[0], average[1], average[2]), Diagonal(contrast, contrast, contrast));
        a = Multiply(a, Translation(-average[0], -average[1], -average[2]));
        var l = LuminanceAxis(folded: false);
        var r = AxisToAxis(l, [0f, 0f, 1f]);
        var m = Multiply(Diagonal(1f / l[0], 1f / l[1], 1f / l[2]), Transpose(r));
        m = Multiply(m, Diagonal(saturation, saturation, 1f));
        m = Multiply(m, r);
        m = Multiply(m, Diagonal(l[0], l[1], l[2]));
        m = Multiply(m, Diagonal(brightness, brightness, brightness));
        return Multiply(m, a);
    }
}
