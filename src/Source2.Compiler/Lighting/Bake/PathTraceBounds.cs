using System.Numerics;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// scenesystem's bounds arithmetic for the path trace scene, ported from the 2026-10-15
/// disassembly in its operation order (minss / maxss keep the first operand unless the second is
/// strictly smaller / larger):
/// <list type="bullet">
/// <item><see cref="ClipBounds"/>: a light's AABB, its clip box through FromClipSpace (1804a2b70);</item>
/// <item><see cref="Rotation"/>: a quaternion and translation as a 3 x 4 row matrix (18049d6d0),
/// <see cref="Transform"/> a point through it (18049dd60 / 18049c3a0);</item>
/// <item><see cref="BoxBounds"/>: an oriented box's AABB (18049e080, its eight corners);</item>
/// <item><see cref="IlluminationFromWorld"/>: the barn matrix of an illumination volume (the
/// loader at 18009357c: the inverse rotation, the rotated negative centre, rows times 1 / extent,
/// 18049bbc0);</item>
/// <item><see cref="TransformBounds"/>: a mesh AABB through an instance transform (18049bfb0);</item>
/// <item><see cref="BinGrid"/>: the light bin grid (1804c8280).</item>
/// </list>
/// </summary>
public static class PathTraceBounds
{
    public static float MinSs(float a, float b) => a < b ? a : b;
    public static float MaxSs(float a, float b) => a > b ? a : b;

    /// <summary>1804a2b70: the clip box (x, y in -1..1, z in 0..1) through the row-major FromClipSpace, divided by w.</summary>
    public static (Vector3 Min, Vector3 Max) ClipBounds(float[] from)
    {
        float M(int r, int c) => from[r * 4 + c];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(-float.MaxValue);
        // The x / y part of each row for the corner (sx, sy), in the code's operation order.
        float Xy(int r, int corner) => corner switch
        {
            0 => M(r, 1) * -1f - M(r, 0) * 1f,
            1 => M(r, 0) - M(r, 1) * 1f,
            2 => M(r, 1) + M(r, 0),
            _ => M(r, 1) - M(r, 0) * 1f,
        };
        for (var z = 0; z < 2; z++)
            for (var corner = 0; corner < 4; corner++)
            {
                float Row(int r) => z == 0 ? Xy(r, corner) + M(r, 2) * 0f + M(r, 3) : Xy(r, corner) + M(r, 2) + M(r, 3);
                var inv = 1f / Row(3);
                var p = new Vector3(Row(0) * inv, Row(1) * inv, Row(2) * inv);
                min = new Vector3(MinSs(min.X, p.X), MinSs(min.Y, p.Y), MinSs(min.Z, p.Z));
                max = new Vector3(MaxSs(max.X, p.X), MaxSs(max.Y, p.Y), MaxSs(max.Z, p.Z));
            }
        return (min, max);
    }

    /// <summary>18049d6d0: rows of the rotation of q = (x, y, z, w) with the translation t in column 3.</summary>
    public static float[] Rotation(Vector4 q, Vector3 t)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        float x2 = x + x, y2 = y + y, z2 = z + z;
        float xx = x * x2, yy = y * y2, zz = z * z2;
        float d0 = 1f - yy - zz, d1 = 1f - xx - zz, d2 = 1f - xx - yy;
        float a0 = y2 * x, a1 = z2 * y, a2 = z2 * x;
        float b0 = z2 * w, b1 = x2 * w, b2 = y2 * w;
        return
        [
            d0, a0 - b0, b2 + a2, t.X,
            b0 + a0, d1, a1 - b1, t.Y,
            a2 - b2, b1 + a1, d2, t.Z,
        ];
    }

    /// <summary>18049c3a0: (r3 + p.y r1) + (p.z r2 + p.x r0) per row.</summary>
    public static Vector3 Transform(float[] m, Vector3 p)
    {
        float Row(int r) => (m[r * 4 + 3] * 1f + p.Y * m[r * 4 + 1]) + (p.Z * m[r * 4 + 2] + p.X * m[r * 4]);
        return new Vector3(Row(0), Row(1), Row(2));
    }

    /// <summary>18049e080: the AABB of the box (orientation q, centre c, half extents e).</summary>
    public static (Vector3 Min, Vector3 Max) BoxBounds(Vector4 q, Vector3 c, Vector3 e)
    {
        var m = Rotation(q, c);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(-float.MaxValue);
        foreach (var (sx, sy, sz) in new[] { (-1, -1, -1), (-1, -1, 1), (-1, 1, -1), (-1, 1, 1), (1, -1, -1), (1, -1, 1), (1, 1, -1), (1, 1, 1) })
        {
            var p = Transform(m, new Vector3(sx < 0 ? -e.X : e.X, sy < 0 ? -e.Y : e.Y, sz < 0 ? -e.Z : e.Z));
            min = new Vector3(MinSs(min.X, p.X), MinSs(min.Y, p.Y), MinSs(min.Z, p.Z));
            max = new Vector3(MaxSs(max.X, p.X), MaxSs(max.Y, p.Y), MaxSs(max.Z, p.Z));
        }
        return (min, max);
    }

    /// <summary>
    /// The loader's m_mBarnIlluminationFromWorld (18009357c to 1800936dd): the rotation of q's inverse
    /// (conjugate over dpps(q, q), zero when that is 0) with the translation
    /// cross(t2, q) + (v + w t2), v = -c, t2 = 2 cross(v, q); rows times 1 / extent.
    /// </summary>
    public static float[] IlluminationFromWorld(Vector4 q, Vector3 c, Vector3 e)
    {
        var n = (q.X * q.X + q.Y * q.Y) + (q.Z * q.Z + q.W * q.W);
        var qi = n == 0f ? default : new Vector4(-q.X / n, -q.Y / n, -q.Z / n, q.W / n);
        var v = -c;
        var t = new Vector3(q.Z * v.Y - q.Y * v.Z, q.X * v.Z - q.Z * v.X, q.Y * v.X - q.X * v.Y);
        var t2 = new Vector3(t.X + t.X, t.Y + t.Y, t.Z + t.Z);
        var u = new Vector3(v.X + q.W * t2.X, v.Y + q.W * t2.Y, v.Z + q.W * t2.Z);
        var cr = new Vector3(q.Z * t2.Y - q.Y * t2.Z, q.X * t2.Z - q.Z * t2.X, q.Y * t2.X - q.X * t2.Y);
        var m = Rotation(qi, new Vector3(cr.X + u.X, cr.Y + u.Y, cr.Z + u.Z));
        float[] s = [1f / e.X, 1f / e.Y, 1f / e.Z];
        for (var r = 0; r < 3; r++)
            for (var col = 0; col < 4; col++)
                m[r * 4 + col] = s[r] * m[r * 4 + col];
        return m;
    }

    /// <summary>18049bfb0: a local AABB through a 3 x 4 row matrix as centre and absolute extents.</summary>
    public static (Vector3 Min, Vector3 Max) TransformBounds(float[] m, Vector3 min, Vector3 max)
    {
        var c = new Vector3((max.X + min.X) * 0.5f, (max.Y + min.Y) * 0.5f, (max.Z + min.Z) * 0.5f);
        var e = new Vector3(max.X - c.X, max.Y - c.Y, max.Z - c.Z);
        var tc = Transform(m, c);
        float Ext(int r) => (MathF.Abs(e.Y * m[r * 4 + 1]) + MathF.Abs(e.X * m[r * 4])) + MathF.Abs(e.Z * m[r * 4 + 2]);
        var te = new Vector3(Ext(0), Ext(1), Ext(2));
        return (new Vector3(tc.X - te.X, tc.Y - te.Y, tc.Z - te.Z), new Vector3(te.X + tc.X, te.Y + tc.Y, te.Z + tc.Z));
    }

    /// <summary>
    /// 1804c8280: bisects the cell size between 0 and the largest extent while the step is over
    /// 0.01; counts are round-to-nearest-even of extent / size, at least 1; the smaller size is kept
    /// while count x bytes per cell fits the budget.
    /// </summary>
    public static (int X, int Y, int Z) BinGrid(ulong budget, long bytesPerCell, Vector3 min, Vector3 max)
    {
        float ex = max.X - min.X, ey = max.Y - min.Y, ez = max.Z - min.Z;
        static int Count(float v) => Math.Max(1, (int)MathF.Round(v, MidpointRounding.ToEven));
        var hi = MaxSs(ex, MaxSs(ey, ez));
        var lo = 0f;
        if (hi - lo > 0.01f)
        {
            do
            {
                var mid = (hi + lo) * 0.5f;
                var cells = (long)(Count(ex / mid) * Count(ey / mid) * Count(ez / mid)) * bytesPerCell;
                if ((ulong)cells > budget)
                    lo = mid;
                else
                    hi = mid;
            }
            while (hi - lo > 0.01f);
        }
        return (Count(ex / hi), Count(ey / hi), Count(ez / hi));
    }
}
