using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The pieces FUN_180eea510 builds a light record from, each with its float
/// operations in resourcecompiler's order.
/// </summary>
public static class LightBuild
{
    private const float Epsilon = 1.1920929e-07f;
    private const float Degrees = 0.017453292f;

    /// <summary>
    /// FUN_18129a760: a barn's frustum. The light looks down its x axis; the
    /// axes are swapped into (-y, z, x), sheared by (shear / range) and
    /// scaled by 1 / size, with the near distance size z folded into a
    /// projective w, the far plane at 1 / range (at least |size z| epsilon).
    /// </summary>
    public static float[] BarnMatrix(Vector3 size, float range, Vector2 shear)
    {
        var f = 1f / range;
        var floor = MathF.Abs(size.Z) * Epsilon;
        if (1f / range <= floor)
            f = floor;
        float[] swap = LightMath.Transpose4([-0f, 0, 1, 0, -1, 0, 0, 0, -0f, 1, 0, 0, 0, 0, 0, 1]);
        var s = LightMath.Identity4();
        s[2] = f * shear.X;
        s[6] = -shear.Y * f;
        var p = LightMath.Identity4();
        p[14] = size.Z;
        p[0] = 1f / size.X;
        p[5] = 1f / size.Y;
        p[10] = size.Z + f;
        return LightMath.Mul4(p, LightMath.Mul4(s, swap));
    }

    /// <summary>
    /// FUN_181297ed0: a barn's position, homogeneous: a unit direction for a
    /// barn with no near distance (w 0), else the apex behind the aperture.
    /// </summary>
    public static Vector4 BarnPosition(Vector3 size, float range, Vector2 shear)
    {
        var f = 1f / range;
        var a = f * shear.X;
        var b = f * shear.Y;
        var d = new Vector3((a * 0f + 1f) + b * 0f, (a + 0f) + b * 0f, (a * 0f + 0f) + b);
        LightMath.Normalize(ref d);
        var r = new Vector4(-d.X, -d.Y, -d.Z, 0f);
        if (0f < size.Z)
        {
            var c = -1f / size.Z;
            var k = c * f;
            var x = k * shear.X;
            var y = k * shear.Y;
            var w = ((x * x + y * y) + c * c) + 0f;
            r = new Vector4((c + x * 0f) + y * 0f, (c * 0f + x) + y * 0f, (c * 0f + x * 0f) + y, w);
        }
        return r;
    }

    /// <summary>
    /// FUN_181292ce0: the plane through the volume's opening, from its inverse:
    /// the clip-space corners (-1, -1, 0), (1, -1, 0) and (1, 1, 0) taken back
    /// to light space, their normal, and its distance through the first.
    /// </summary>
    public static Vector4 Plane(ReadOnlySpan<float> m)
    {
        var a13 = m[13] * -1f;
        var a14 = m[14] * 0f;
        var a6 = m[6] * 0f;
        var a10 = m[10] * 0f;
        var a2 = m[2] * 0f;
        var a5 = m[5] * -1f;
        var a9 = m[9] * -1f;
        var w0 = 1f / (((a13 - m[12] * 1f) + a14) + m[15]);
        var p0x = (((m[1] * -1f - m[0] * 1f) + a2) + m[3]) * w0;
        var p0y = (((a5 - m[4] * 1f) + a6) + m[7]) * w0;
        var p0z = (((a9 - m[8] * 1f) + a10) + m[11]) * w0;
        var w1 = 1f / (((m[12] + a13) + a14) + m[15]);
        var w2 = 1f / (((m[12] + m[13]) + a14) + m[15]);
        var a = (((a5 + m[4]) + a6) + m[7]) * w1 - p0y;
        var b = (((m[0] + m[1] * -1f) + a2) + m[3]) * w1 - p0x;
        var c = (((a9 + m[8]) + a10) + m[11]) * w1 - p0z;
        var d = (((m[0] + m[1]) + a2) + m[3]) * w2 - p0x;
        var e = (((m[8] + m[9]) + a10) + m[11]) * w2 - p0z;
        var g = (((m[4] + m[5]) + a6) + m[7]) * w2 - p0y;
        var n = new Vector3(g * c - e * a, b * e - c * d, a * d - b * g);
        LightMath.Normalize(ref n);
        return new Vector4(n, (p0z * n.Z + p0y * n.Y) + p0x * n.X);
    }

    /// <summary>FUN_18129a5b0: the barn's shape key into [0.125, 1]; 0 or less stays 0.</summary>
    public static float ShapeCurve(float shape)
    {
        if (!(0f < shape))
            return 0f;
        shape -= 0f;
        if (shape <= 0f)
            shape = 0f;
        if (1f <= shape)
            shape = 1f;
        return shape * 0.875f + 0.125f;
    }

    /// <summary>FUN_18129a5f0: the skirt fades at the near and far ends, as reciprocal lengths along the frustum's depth.</summary>
    public static Vector2 Skirt(float near, float far, float sizeZ, float range)
    {
        far = 1f - far;
        if (near <= 0f)
            near = 0f;
        if (far <= 0f)
            far = 0f;
        if (1f <= near)
            near = 1f;
        if (1f <= far)
            far = 1f;
        near *= range;
        far *= range;
        var k = 1f / range + sizeZ;
        var n = (k * near) / (near * sizeZ + 1f);
        if (n <= 0f)
            n = 0f;
        var f = 1f - (k * far) / (far * sizeZ + 1f);
        if (1f <= n)
            n = 1f;
        n = n <= 0f ? 0f : 1f / n;
        if (f <= 0f)
            f = 0f;
        if (1f <= f)
            f = 1f;
        return new Vector2(n, 0f < f ? 1f / f : 0f);
    }

    /// <summary>
    /// FUN_181298050: a luminaire's half extents from its size in degrees
    /// (clamped to [1, 90]) and anisotropy, at 100 units plus the square root
    /// of the position's w.
    /// </summary>
    public static Vector2 LuminaireSize(float size, float anisotropy, float w)
    {
        if (size <= 1f)
            size = 1f;
        if (90f <= size)
            size = 90f;
        float a, b;
        if (anisotropy <= 0f)
        {
            var k = 0.01f;
            if (0.01f <= anisotropy + 1f)
                k = anisotropy + 1f;
            a = k * size;
            b = size;
        }
        else
        {
            var k = 0.01f;
            if (0.01f <= 1f - anisotropy)
                k = 1f - anisotropy;
            a = size;
            b = k * size;
        }
        a = Tan(a * 0.5f * Degrees);
        b = Tan(b * 0.5f * Degrees);
        if (0f < w)
        {
            var root = MathF.Sqrt(w);
            a *= root + 100f;
            b *= root + 100f;
        }
        return new Vector2(a, b);
    }

    /// <summary>tier0's V_tanf (<see cref="LightCrt.Tan"/>).</summary>
    public static float Tan(float x) => LightCrt.Tan(x);

    /// <summary>FUN_181296340: a rectangle luminaire, corners (0, +-a, +-b) turned and moved to the position; type 1.</summary>
    public static void RectLuminaire(LightShape l, float a, float b)
    {
        l[0xf8] = -b;
        l[0x10c] = a;
        l[0xf4] = -a;
        l[0x100] = -a;
        l[0x110] = -b;
        l[0x118] = a;
        l[0x104] = b;
        l[0x11c] = b;
        l.Type = 1;
        l[0xf0] = 0f;
        l[0xfc] = 0f;
        l[0x108] = 0f;
        l[0x114] = 0f;
        var pos = l.V3(0x90);
        foreach (var at in new[] { 0xf0, 0xfc, 0x108, 0x114 })
        {
            var r = LightMath.QuatMatrix34(l.Orientation, Vector3.Zero);
            var p = LightMath.Rotate34(r, l.V3(at));
            l.SetV3(at, new Vector3(p.X + pos.X, p.Y + pos.Y, p.Z + pos.Z));
        }
        l.SetV3(0x120, LightMath.Forward(l.Orientation));
        var k = 3.1415927f / ((a * 4f) * b);
        l[0xc0] = k;
        if (l[0x9c] != 0f)
            l[0xc0] = l[0x9c] * k;
    }

    /// <summary>FUN_181296170: a disc luminaire at the position, its axes turned; type 2.</summary>
    public static void DiscLuminaire(LightShape l, float a, float b)
    {
        l.Type = 2;
        l.SetV3(0xf0, l.V3(0x90));
        var r = LightMath.QuatMatrix34(l.Orientation, Vector3.Zero);
        l.SetV3(0xfc, LightMath.Rotate34(r, new Vector3(0f, a, 0f)));
        l.SetV3(0x108, LightMath.Rotate34(r, new Vector3(0f, 0f, b)));
        l.SetV3(0x114, LightMath.Forward(l.Orientation));
        var k = 1f / (a * b);
        l[0xc0] = k;
        if (l[0x9c] != 0f)
            l[0xc0] = l[0x9c] * k;
    }

    /// <summary>
    /// FUN_18129a8d0 then FUN_180ee9e30: the record moved by a 3x4. The volume
    /// matrix is right-multiplied by the inverse, the inverse left by the
    /// matrix, the plane rebuilt; a directional light's position turns without
    /// moving; the luminaire moves with it; the orientation is composed.
    /// </summary>
    public static void Transform(LightShape l, ReadOnlySpan<float> m34)
    {
        var src = l.Clone();
        var inverse = LightMath.InvertRigid34(m34);
        LightMath.Mul4(src.Matrix, LightMath.To4(inverse)).CopyTo(l.Matrix);
        LightMath.Mul4(LightMath.To4(m34), src.Inverse).CopyTo(l.Inverse);
        var plane = Plane(l.Inverse);
        l[0x80] = plane.X;
        l[0x84] = plane.Y;
        l[0x88] = plane.Z;
        l[0x8c] = plane.W;
        var m = m34.ToArray();
        if (src[0x9c] == 0f)
            m[3] = m[7] = m[11] = 0f;
        l.SetV3(0x90, LightMath.Transform34(m, src.V3(0x90)));
        switch (src.Type)
        {
            case 1:
                l.SetV3(0xf0, LightMath.Transform34(m, src.V3(0xf0)));
                l.SetV3(0xfc, LightMath.Transform34(m, src.V3(0xfc)));
                l.SetV3(0x108, LightMath.Transform34(m, src.V3(0x108)));
                l.SetV3(0x114, LightMath.Transform34(m, src.V3(0x114)));
                l.SetV3(0x120, LightMath.Rotate34(m, src.V3(0x120)));
                break;
            case 2:
                l.SetV3(0xf0, LightMath.Transform34(m, src.V3(0xf0)));
                l.SetV3(0xfc, LightMath.Rotate34(m, src.V3(0xfc)));
                l.SetV3(0x108, LightMath.Rotate34(m, src.V3(0x108)));
                l.SetV3(0x114, LightMath.Rotate34(m, src.V3(0x114)));
                break;
            case 3:
                l.SetV3(0xf0, LightMath.Transform34(m, src.V3(0xf0)));
                break;
            case 4:
            case 5:
                l.SetV3(0xf0, LightMath.Transform34(m, src.V3(0xf0)));
                l.SetV3(0xfc, LightMath.Transform34(m, src.V3(0xfc)));
                l.SetV3(0x108, LightMath.Rotate34(m, src.V3(0x108)));
                l.SetV3(0x114, LightMath.Rotate34(m, src.V3(0x114)));
                break;
        }
        var r = LightMath.QuatMatrix34(src.Orientation, Vector3.Zero);
        l.Orientation = LightMath.MatrixQuat34(LightMath.Concat34(m34, r));
    }

    /// <summary>FUN_1812966c0: a sphere luminaire of the radius at the position; type 3.</summary>
    public static void SphereLuminaire(LightShape l, float radius)
    {
        l.Type = 3;
        l.SetV3(0xf0, l.V3(0x90));
        l[0xfc] = radius;
        var k = 1f / (radius * radius);
        l[0xc0] = k;
        if (l[0x9c] != 0f)
            l[0xc0] = l[0x9c] * k;
    }

    /// <summary>
    /// The barn half of FUN_180eea510, in the light's own space: soft edges,
    /// shape, skirt, position, the flat falloffs, frustum and its inverse and
    /// opening plane, then the luminaire (1 disc, 2 rectangle).
    /// </summary>
    public static LightShape Barn(Vector3 sizeParams, float range, Vector2 shear, float skirtNear, float skirt,
                                  float softX, float softY, float shape, int luminaireShape, float luminaireSize,
                                  float luminaireAnisotropy)
    {
        var l = new LightShape();
        var size = new Vector3(sizeParams.X <= 1f ? 1f : sizeParams.X, sizeParams.Y <= 1f ? 1f : sizeParams.Y,
                               0f <= sizeParams.Z ? sizeParams.Z : 0f);
        if (range <= 1f)
            range = 1f;
        var max = (LightSampler.MaxCoord + LightSampler.MaxCoord) * 1.7320508f;
        if (max <= range)
            range = max;
        var fx = 1f - softX;
        var fy = 1f - softY;
        if (fx <= 0f)
            fx = 0f;
        if (fy <= 0f)
            fy = 0f;
        if (0.9999999f <= fx)
            fx = 0.9999999f;
        if (0.9999999f <= fy)
            fy = 0.9999999f;
        l[0xa0] = fx;
        l[0xa4] = fy;
        l[0xa8] = ShapeCurve(shape);
        var sk = Skirt(skirtNear, skirt, size.Z, range);
        l[0xac] = sk.X;
        l[0xb0] = sk.Y;
        var pos = BarnPosition(size, range, shear);
        l.Flags &= 0xfe;
        l[0x90] = pos.X;
        l[0x94] = pos.Y;
        l[0x98] = pos.Z;
        l[0x9c] = pos.W;
        l[0xc8] = 1f;
        l[0xcc] = 0f;
        l.Orientation = Quaternion.Identity;
        l[0xe0] = 1f;
        l[0xe4] = 0f;
        l[0xe8] = 0f;
        BarnMatrix(size, range, shear).CopyTo(l.Matrix);
        LightMath.Inverse4(l.Matrix, l.Inverse);
        var plane = Plane(l.Inverse);
        l[0x80] = plane.X;
        l[0x84] = plane.Y;
        l[0x88] = plane.Z;
        l[0x8c] = plane.W;
        if (luminaireShape != 0)
        {
            var extent = LuminaireSize(luminaireSize, luminaireAnisotropy, l[0x9c]);
            if (luminaireShape == 1)
                DiscLuminaire(l, extent.X, extent.Y);
            else if (luminaireShape == 2)
                RectLuminaire(l, extent.X, extent.Y);
        }
        return l;
    }
}
