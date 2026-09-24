using System.Numerics;

namespace Source2.Compiler;

/// <summary>
/// The matrix arithmetic that places a point_template's members in the
/// template's local space (FUN_18024ccd0): each member's origin and angles
/// become a matrix, the template's own inverse is applied, and the result is
/// read back as an origin and a set of angles.
///
/// <para>Reading back through a matrix is what gives Valve's members their
/// signature angles: a yaw of 270 comes out as pitch -0, yaw -90, because
/// atan2 of a negative zero is a negative zero and the yaw is in (-180, 180].
/// Every sum is in the binary's own order, read out of the disassembly: the
/// decompiler drops the parentheses on these chains.</para>
/// </summary>
internal static class TemplateTransform
{
    /// <summary>A member's origin and angles relative to its template.</summary>
    public static (Vector3 Origin, Vector3 Angles) Relative(
        Vector3 templateOrigin, Vector3 templateAngles, Vector3 origin, Vector3 angles)
    {
        var local = Concat(Invert(AngleMatrix(templateOrigin, templateAngles)), AngleMatrix(origin, angles));
        return (new Vector3(local[3], local[7], local[11]), MatrixAngles(local));
    }

    /// <summary>AngleMatrix with a translation (FUN_181264c60): a 3x4 matrix,
    /// row major.</summary>
    public static float[] AngleMatrix(Vector3 origin, Vector3 angles)
    {
        const float DegToRad = 0.017453292f;
        var (sp, cp) = SinCos(angles.X * DegToRad);
        var (sy, cy) = SinCos(angles.Y * DegToRad);
        var (sr, cr) = SinCos(angles.Z * DegToRad);
        return
        [
            cy * cp, sr * sp * cy - cr * sy, cr * sp * cy - (-sy * sr), origin.X,
            sy * cp, sr * sp * sy + cr * cy, cr * sp * sy - sr * cy, origin.Y,
            -sp, sr * cp, cr * cp, origin.Z,
        ];
    }

    /// <summary>The inverse of a rotation and translation (FUN_181263af0): the
    /// rotation transposed, and the translation rotated back and negated.</summary>
    public static float[] Invert(float[] m)
    {
        float t0 = -m[3], t1 = -m[7], t2 = -m[11];
        return
        [
            m[0], m[4], m[8], m[4] * t1 + m[0] * t0 + m[8] * t2,
            m[1], m[5], m[9], m[5] * t1 + m[1] * t0 + m[9] * t2,
            m[2], m[6], m[10], m[6] * t1 + m[2] * t0 + m[10] * t2,
        ];
    }

    /// <summary>ConcatTransforms (FUN_181263eb0): a times b, each term added in
    /// column order, the implied fourth row of b being 0 0 0 1.</summary>
    public static float[] Concat(float[] a, float[] b)
    {
        var c = new float[12];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 4; j++)
            {
                var w = j == 3 ? 1f : 0f;
                c[i * 4 + j] = a[i * 4] * b[j] + a[i * 4 + 1] * b[4 + j] + a[i * 4 + 2] * b[8 + j] + a[i * 4 + 3] * w;
            }
        return c;
    }

    /// <summary>MatrixAngles (FUN_181264350): pitch, yaw and roll in degrees,
    /// with the gimbal case below 0.001 of horizontal forward length.</summary>
    public static Vector3 MatrixAngles(float[] m)
    {
        const float RadToDeg = 57.2957764f;
        var xy = MathF.Sqrt(m[0] * m[0] + m[4] * m[4]);
        return xy > 0.001f
            ? new Vector3(Atan2(-m[8], xy) * RadToDeg, Atan2(m[4], m[0]) * RadToDeg, Atan2(m[9], m[10]) * RadToDeg)
            : new Vector3(Atan2(-m[8], xy) * RadToDeg, Atan2(-m[1], m[5]) * RadToDeg, 0f);
    }

    /// <summary>tier0's V_sincosf, which is the CRT's sinf and cosf.</summary>
    private static (float Sin, float Cos) SinCos(float radians)
        => ((float)Math.Sin(radians), (float)Math.Cos(radians));

    /// <summary>tier0's V_atan2f, the CRT's atan2f: computed in double and
    /// narrowed.</summary>
    private static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
}
