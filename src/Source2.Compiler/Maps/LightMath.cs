using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The matrix and quaternion routines the light precompute is built from,
/// each with its float operations in resourcecompiler's order. A 4x4 is 16
/// floats row by row; a 3x4 is 12, the translation in the fourth column.
/// </summary>
public static class LightMath
{
    /// <summary>FUN_181263eb0: a b, each entry ((a0 b0 + a1 b1) + a2 b2) + a3 b3.</summary>
    public static float[] Mul4(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var r = new float[16];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                r[4 * i + j] = ((a[4 * i] * b[j] + a[4 * i + 1] * b[4 + j]) + a[4 * i + 2] * b[8 + j]) + a[4 * i + 3] * b[12 + j];
        return r;
    }

    /// <summary>FUN_1812644b0: the transpose.</summary>
    public static float[] Transpose4(ReadOnlySpan<float> a)
    {
        var r = new float[16];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                r[4 * j + i] = a[4 * i + j];
        return r;
    }

    public static float[] Identity4() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    /// <summary>
    /// FUN_1812636e0: Gauss-Jordan on [A | I] with partial pivoting. A column's
    /// pivot is the first row, in the current row order, whose entry is the
    /// strictly largest above 1e-6; none leaves the output untouched and
    /// returns false. The pivot row is scaled by the reciprocal of its pivot
    /// (which is then set to 1) and subtracted from every other row.
    /// </summary>
    public static bool Inverse4(ReadOnlySpan<float> a, Span<float> result)
    {
        var m = new float[4, 8];
        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < 4; j++)
                m[i, j] = a[4 * i + j];
            m[i, 4 + i] = 1f;
        }
        int[] order = [0, 1, 2, 3];
        for (var k = 0; k < 4; k++)
        {
            var best = 1e-06f;
            var pick = -1;
            for (var r = k; r < 4; r++)
            {
                var v = MathF.Abs(m[order[r], k]);
                if (best < v)
                {
                    best = v;
                    pick = r;
                }
            }
            if (pick < 0)
                return false;
            var p = order[pick];
            order[pick] = order[k];
            order[k] = p;
            var inverse = 1f / m[p, k];
            for (var j = 0; j < 8; j++)
                m[p, j] = m[p, j] * inverse;
            m[p, k] = 1f;
            for (var r = 0; r < 4; r++)
            {
                if (r == k)
                    continue;
                var q = order[r];
                var f = -m[q, k];
                for (var j = 0; j < 8; j++)
                    m[q, j] = f * m[p, j] + m[q, j];
                m[q, k] = 0f;
            }
        }
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                result[4 * i + j] = m[order[i], 4 + j];
        return true;
    }

    /// <summary>FUN_181258890: a b for 3x4s, each row ((a2 B2 + a1 B1) + a0 B0) + (0, 0, 0, a3).</summary>
    public static float[] Concat34(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var r = new float[12];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 4; j++)
                r[4 * i + j] = ((a[4 * i + 2] * b[8 + j] + a[4 * i + 1] * b[4 + j]) + a[4 * i] * b[j]) + (j == 3 ? a[4 * i + 3] : 0f);
        return r;
    }

    /// <summary>FUN_18125b130: the inverse of a rigid 3x4, the rotation transposed and the translation turned back through it.</summary>
    public static float[] InvertRigid34(ReadOnlySpan<float> m)
    {
        var r = new float[12];
        r[0] = m[0];
        r[1] = m[4];
        r[2] = m[8];
        r[4] = m[1];
        r[5] = m[5];
        r[6] = m[9];
        r[8] = m[2];
        r[9] = m[6];
        r[10] = m[10];
        float t1 = m[7], t2 = m[11], t0 = m[3];
        r[3] = -((t2 * r[2] + t1 * r[1]) + t0 * r[0]);
        r[7] = -((r[5] * t1 + t2 * r[6]) + t0 * r[4]);
        r[11] = -((r[9] * t1 + t2 * r[10]) + r[8] * t0);
        return r;
    }

    /// <summary>A 3x4 as a 4x4 with (0, 0, 0, 1) below.</summary>
    public static float[] To4(ReadOnlySpan<float> m) =>
        [m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], 0, 0, 0, 1];

    /// <summary>FUN_18125d1b0 (Matrix3x4_Rotate): each row (x r0 + y r1) + z r2.</summary>
    public static Vector3 Rotate34(ReadOnlySpan<float> m, Vector3 v) => new(
        v.Z * m[2] + (v.Y * m[1] + v.X * m[0]),
        v.Z * m[6] + (v.Y * m[5] + v.X * m[4]),
        v.Z * m[10] + (v.Y * m[9] + v.X * m[8]));

    /// <summary>FUN_18125d1f0 (Matrix3x4_TransformPoint): each row (t + y r1) + (z r2 + x r0).</summary>
    public static Vector3 Transform34(ReadOnlySpan<float> m, Vector3 v) => new(
        (m[3] + v.Y * m[1]) + (v.Z * m[2] + v.X * m[0]),
        (m[7] + v.Y * m[5]) + (v.Z * m[6] + v.X * m[4]),
        (m[11] + v.Y * m[9]) + (v.Z * m[10] + v.X * m[8]));

    /// <summary>FUN_18125f4e0: a quaternion's 3x4 with a translation.</summary>
    public static float[] QuatMatrix34(Quaternion q, Vector3 t)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        float y2 = y + y, z2 = z + z, xx2 = x * (x + x);
        var x2w = (x + x) * w;
        return
        [
            (1f - y * y2) - z * z2, y2 * x - z2 * w, y2 * w + z2 * x, t.X,
            z2 * w + y2 * x, (1f - xx2) - z * z2, z2 * y - x2w, t.Y,
            z2 * x - y2 * w, x2w + z2 * y, (1f - xx2) - y * y2, t.Z,
        ];
    }

    /// <summary>FUN_18125de90: a 3x4's rotation as a quaternion, from its largest diagonal.</summary>
    public static Quaternion MatrixQuat34(ReadOnlySpan<float> m)
    {
        float m00 = m[0], m11 = m[5], m22 = m[10];
        var trace = (m00 + m11) + m22;
        if (0f <= trace)
        {
            var s = MathF.Sqrt(trace + 1f);
            var f = 0.5f / s;
            return new Quaternion((m[9] - m[6]) * f, (m[2] - m[8]) * f, (m[4] - m[1]) * f, s * 0.5f);
        }
        if (m22 > m00)
        {
            if (m22 > m11)
            {
                var s = MathF.Sqrt((m22 - (m00 + m11)) + 1f);
                var f = 0.5f / s;
                return new Quaternion((m[8] + m[2]) * f, (m[9] + m[6]) * f, s * 0.5f, (m[4] - m[1]) * f);
            }
        }
        else if (!(m00 < m11))
        {
            var s = MathF.Sqrt((m00 - (m22 + m11)) + 1f);
            var f = 0.5f / s;
            return new Quaternion(s * 0.5f, (m[4] + m[1]) * f, (m[8] + m[2]) * f, (m[9] - m[6]) * f);
        }
        {
            var s = MathF.Sqrt((m11 - (m22 + m00)) + 1f);
            var f = 0.5f / s;
            return new Quaternion((m[4] + m[1]) * f, s * 0.5f, (m[9] + m[6]) * f, (m[2] - m[8]) * f);
        }
    }

    /// <summary>
    /// FUN_18013e340: scales v to unit length, the length sqrt((y y + z z) + x x);
    /// a zero vector stays zero. Returns the length.
    /// </summary>
    public static float Normalize(ref Vector3 v)
    {
        var length = MathF.Sqrt((v.Y * v.Y + v.Z * v.Z) + v.X * v.X);
        if (1e-17f <= length && length <= 1e17f)
        {
            var inverse = 1f / length;
            v = new Vector3(inverse * v.X, inverse * v.Y, inverse * v.Z);
            return length;
        }
        if (length == 0f)
        {
            v = Vector3.Zero;
            return 0f;
        }
        v = VectorNormalizeSlow.Normalise(v, out var slow);
        return slow;
    }

    /// <summary>
    /// A constant axis a turned by the quaternion (FUN_18125dcc0 and its two
    /// neighbours): c = a x q, doubled, then (t x q) + w t + a, grouped as the
    /// binary groups it.
    /// </summary>
    public static Vector3 QuatAxis(Quaternion q, Vector3 a)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        var cx = a.Z * y - a.Y * z;
        var cy = a.X * z - a.Z * x;
        var cz = a.Y * x - a.X * y;
        cx += cx;
        cy += cy;
        cz += cz;
        return new Vector3(
            (cz * y - cy * z) + w * cx + a.X,
            (cx * z - cz * x) + (w * cy + a.Y),
            (cy * x - cx * y) + (w * cz + a.Z));
    }

    /// <summary>FUN_18125dcc0: the quaternion's forward (x) axis.</summary>
    public static Vector3 Forward(Quaternion q) => QuatAxis(q, Vector3.UnitX);

    /// <summary>FUN_18125dd50: the quaternion's left (y) axis.</summary>
    public static Vector3 Left(Quaternion q) => QuatAxis(q, Vector3.UnitY);

    /// <summary>FUN_18125dde0: the quaternion's up (z) axis.</summary>
    public static Vector3 Up(Quaternion q) => QuatAxis(q, Vector3.UnitZ);
}
