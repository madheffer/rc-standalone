using System.Buffers.Binary;
using System.Numerics;
using Source2.Compiler.Maps;

namespace Source2.Compiler.Cubemaps;

/// <summary>
/// A light_barn's light description (0x12c bytes; the scene object's +0xf0, a light entry's +0x18; CUBEMAPS.md A.6)
/// from its entity keys, as client.dll builds it: C_BarnLight's vfunc 0x7d8 (10-15 build 180d81100) per scene
/// object, from the networked fields (m_vSizeParams, m_flRange, m_vShear, m_flSoftX / Y, m_flShape, m_flSkirt /
/// Near, m_Color, m_flBrightness, m_nLuminaireShape / Size / Anisotropy), which are the keys of the same names.
/// <list type="bullet">
/// <item>+0x00 the light's projection (1817658a0: size, range, shear), +0x40 its inverse (181747070), +0x80 a plane
/// from that inverse (181763bf0);</item>
/// <item>+0x90 the light's apex in its own space with w (181764b10), +0xa0 / +0xa4 1 - soft_x / y, +0xa8 the shape
/// term (181765700), +0xac / +0xb0 the skirt (181765730), +0xb4 .. +0xbc the colour (vfunc 0x7f0) times the
/// falloff (181765820, k 100), +0xc4 the cookie (-1: none), +0xc8 1, +0xd0 the identity rotation, +0xe0 1;</item>
/// <item>+0xc0 and +0xec .. +0x128 the luminaire (181764c90 sizes; m_nLuminaireShape 2 a rectangle, type 1,
/// 181764480; 1 a disk, type 2, 1817642b0).</item>
/// </list>
/// +0xed is a flags byte this function does not write (bit 0 cleared only); it is left 0.
/// Checked against the 64 captured light entries (BarnLightDescriptionsTests): every word but +0xed's.
/// Refused: a cookie, a light style, colormode 1 (colour temperature, not captured), and m_bEnabled 0.
/// </summary>
public static class BarnLightDescriptions
{
    public const int Size = 0x12c;

    public static byte[] Description(LightPrecompute.KeyReader key)
    {
        float F(string name, float fallback = 0f) => key(name) is { Length: > 0 } s ? float.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : fallback;
        Vector3 V(string name) => key(name) is { Length: > 0 } s && s.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 3 } p
            ? new Vector3(float.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture)) : default;
        if (key("lightcookie") is { Length: > 0 })
            throw new NotSupportedException("a barn light cookie (the description's +0xc4) is not ported");
        if (key("light_style") is { Length: > 0 })
            throw new NotSupportedException("a barn light style (vfunc 0x7f0's m_LightStyle) is not ported");
        if (F("colormode") != 0f)
            throw new NotSupportedException("colormode 1 (180d80610, colour temperature) is not ported for barn lights");
        if (F("enabled", 1f) == 0f)
            throw new NotSupportedException("a disabled barn light (vfunc 0x7f0 returns black) is not ported");
        var size = V("size_params");
        var range = F("range");
        var shearKey = (key("shear") ?? "0 0").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shear = new Vector2(float.Parse(shearKey[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(shearKey[1], System.Globalization.CultureInfo.InvariantCulture));

        var d = new byte[Size];
        void Put(int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(o), v);
        var m = Projection(size, range, shear);
        for (var i = 0; i < 16; i++)
            Put(4 * i, m[i]);
        var inv = AggregateCulling.Invert4(m) ?? throw new InvalidOperationException("a barn light's projection is singular");
        for (var i = 0; i < 16; i++)
            Put(0x40 + 4 * i, inv[i]);
        var plane = Plane(inv);
        for (var i = 0; i < 4; i++)
            Put(0x80 + 4 * i, plane[i]);
        var apex = Apex(size, range, shear);
        for (var i = 0; i < 4; i++)
            Put(0x90 + 4 * i, apex[i]);
        Put(0xa0, Clamp(1f - F("soft_x"), 0f, 0.99999988f));
        Put(0xa4, Clamp(1f - F("soft_y"), 0f, 0.99999988f));
        Put(0xa8, ShapeTerm(F("shape")));
        var skirt = Skirt(F("skirt_near"), F("skirt"), size, range);
        Put(0xac, skirt.X);
        Put(0xb0, skirt.Y);
        // vfunc 0x7f0 (180d87da0): m_Color made linear (18174a050) times V_exp2f(m_flBrightness) m_flBrightnessScale.
        var rgb = (key("color") ?? "255 255 255").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => (int)float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var linear = Lighting.SceneLights.SrgbToLinear(rgb[0], rgb[1], rgb[2]);
        var scale = Simulation.CrtMath.Exp2(F("brightness")) * F("brightnessscale", 1f);
        var falloff = BarnLightRecords.Falloff(apex[3], 100f);
        Put(0xb4, (linear.X * scale) * falloff);
        Put(0xb8, (linear.Y * scale) * falloff);
        Put(0xbc, (linear.Z * scale) * falloff);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(0xc4), -1);
        Put(0xc8, 1f);
        Put(0xdc, 1f);
        Put(0xe0, 1f);
        var shapeKind = (int)F("luminaire_shape");
        if (shapeKind != 0)
        {
            var (s, t) = LuminaireSizes(F("luminaire_size"), F("luminaire_anisotropy"), apex[3]);
            if (shapeKind == 1)
                Disk(d, apex, s, t);
            else if (shapeKind == 2)
                Rectangle(d, apex, s, t);
            else
                throw new NotSupportedException($"m_nLuminaireShape {shapeKind} is not ported");
        }
        return d;
    }

    /// <summary>The light's world 3 x 4 (the scene object's +0x30, an entry's +0x144): AngleMatrix of angles at origin.</summary>
    public static float[] Transform(LightPrecompute.KeyReader key)
    {
        float[] P(string name) => (key(name) ?? "0 0 0").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var a = P("angles");
        var o = P("origin");
        var m = CubemapFaceViews.AngleMatrix(new Vector3(a[0], a[1], a[2]), new Vector3(o[0], o[1], o[2]));
        return m;
    }

    private static float Clamp(float v, float lo, float hi)
    {
        // 1801587b0: max with lo first, then min with hi.
        var r = v <= lo ? lo : v;
        return hi <= r ? hi : r;
    }

    /// <summary>
    /// 1817658a0: P (S T), with T the transposed axis swap, S the shear (f shear.x, -shear.y f in column 2) and P the
    /// scale (1 / size.x, 1 / size.y, size.z + f, size.z in row 3 column 2); f = max(1 / range, |size.z| 2^-23).
    /// </summary>
    public static float[] Projection(Vector3 size, float range, Vector2 shear)
    {
        var f = 1f / range;
        var g = MathF.Abs(size.Z) * 1.1920929e-07f;
        if (f <= g)
            f = g;
        float[] t = [-0f, 0, 1, 0, -1, 0, 0, 0, -0f, 1, 0, 0, 0, 0, 0, 1];
        t = [t[0], t[4], t[8], t[12], t[1], t[5], t[9], t[13], t[2], t[6], t[10], t[14], t[3], t[7], t[11], t[15]];
        float[] s = [1, 0, f * shear.X, 0, 0, 1, -shear.Y * f, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        float[] p = [1f / size.X, 0, 0, 0, 0, 1f / size.Y, 0, 0, 0, 0, size.Z + f, 0, 0, 0, size.Z, 1];
        return Mul(p, Mul(s, t));
    }

    /// <summary>181747480: a b for row-major 4 x 4s, ((a0 b0 + a1 b4) + a2 b8) + a3 b12 per element.</summary>
    public static float[] Mul(float[] a, float[] b)
    {
        var r = new float[16];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                r[i * 4 + j] = ((a[i * 4] * b[j] + a[i * 4 + 1] * b[4 + j]) + a[i * 4 + 2] * b[8 + j]) + a[i * 4 + 3] * b[12 + j];
        return r;
    }

    /// <summary>181764b10: the apex direction (normalised, negated) and, with a positive size.z, the apex point and its squared length.</summary>
    public static float[] Apex(Vector3 size, float range, Vector2 shear)
    {
        var r = 1f / range;
        var x0 = r * shear.X * 0f;
        var y0 = r * shear.Y * 0f;
        var v = new Vector3(x0 + 1f + y0, r * shear.X + 0f + y0, x0 + 0f + r * shear.Y);
        v = Normalize(v);
        float[] o = [-v.X, -v.Y, -v.Z, 0f];
        if (0f < size.Z)
        {
            var f = -1f / size.Z;
            var a = f * r * shear.X;
            var b = f * r * shear.Y;
            o[3] = a * a + b * b + f * f + 0f;
            o[0] = f + a * 0f + b * 0f;
            o[1] = f * 0f + a + b * 0f;
            o[2] = f * 0f + a * 0f + b;
        }
        return o;
    }

    /// <summary>18015a140: VectorNormalize (length sqrt((y y + z z) + x x), within 1e-17 .. 1e17 times its reciprocal).</summary>
    private static Vector3 Normalize(Vector3 v)
    {
        var n = MathF.Sqrt(v.Y * v.Y + v.Z * v.Z + v.X * v.X);
        if (n >= 1e-17f && n <= 1e17f)
        {
            var inv = 1f / n;
            return new Vector3(inv * v.X, inv * v.Y, inv * v.Z);
        }
        if (n == 0f)
            return Vector3.Zero;
        throw new NotSupportedException("18015a140's out-of-range path (181743d60) is not ported");
    }

    /// <summary>181765700: clamp(shape, 0, 1) 0.875 + 0.125 for a positive shape, else 0.</summary>
    public static float ShapeTerm(float shape)
    {
        if (!(0f < shape))
            return 0f;
        var v = shape - 0f;
        if (v <= 0f)
            v = 0f;
        if (1f <= v)
            v = 1f;
        return v * 0.875f + 0.125f;
    }

    /// <summary>181765730: the near and far skirt, as the reciprocals of their depths in the projection's z.</summary>
    public static Vector2 Skirt(float near, float skirt, Vector3 size, float range)
    {
        var far = 1f - skirt;
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
        var z = size.Z;
        var c = 1f / range + z;
        var a = (c * near) / (near * z + 1f);
        if (a <= 0f)
            a = 0f;
        var b = 1f - (c * far) / (far * z + 1f);
        if (1f <= a)
            a = 1f;
        a = a <= 0f ? 0f : 1f / a;
        if (b <= 0f)
            b = 0f;
        if (1f <= b)
            b = 1f;
        return new Vector2(a, 0f < b ? 1f / b : 0f);
    }

    /// <summary>
    /// 181763bf0: from the inverse projection, three corner rays at the far plane; their cross product normalised
    /// (18015a140) and its distance through the near corner.
    /// </summary>
    public static float[] Plane(float[] p)
    {
        var f5 = p[15];
        var f11 = p[12];
        var f1 = p[7];
        var f13 = p[13] * -1f;
        var f16 = p[14] * 0f;
        var f17 = p[6] * 0f;
        var f12 = p[0];
        var f2 = p[4];
        var f14 = p[10] * 0f;
        var f10 = 1f / ((f13 - f11 * 1f) + f16 + f5);
        var f6 = p[2] * 0f;
        var f3 = p[11];
        var f7 = p[5] * -1f;
        var f4 = p[8];
        var f15 = ((p[1] * -1f - f12 * 1f) + f6 + p[3]) * f10;
        var f8 = ((f7 - f2 * 1f) + f17 + f1) * f10;
        var f9 = p[9] * -1f;
        f10 = ((f9 - f4 * 1f) + f14 + f3) * f10;
        var g13 = 1f / (f11 + f13 + f16 + f5);
        var g11 = 1f / (f11 + p[13] + f16 + f5);
        var a16 = (f7 + f2 + f17 + f1) * g13 - f8;
        var a7 = (f12 + p[1] * -1f + f6 + p[3]) * g13 - f15;
        var a9 = (f9 + f4 + f14 + f3) * g13 - f10;
        var b5 = (f12 + p[1] + f6 + p[3]) * g11 - f15;
        var b12 = (f4 + p[9] + f14 + f3) * g11 - f10;
        var b11 = (f2 + p[5] + f17 + f1) * g11 - f8;
        var n = Normalize(new Vector3(b11 * a9 - b12 * a16, a7 * b12 - a9 * b5, a16 * b5 - a7 * b11));
        return [n.X, n.Y, n.Z, f10 * n.Z + f8 * n.Y + f15 * n.X];
    }

    /// <summary>181764c90: the luminaire's two half-sizes from its angular size (1 .. 90 degrees) and anisotropy, scaled by sqrt(w) + 100.</summary>
    public static (float S, float T) LuminaireSizes(float size, float anisotropy, float w)
    {
        if (size <= 1f)
            size = 1f;
        if (90f <= size)
            size = 90f;
        float a, b = size;
        if (anisotropy <= 0f)
        {
            var t = 0.01f;
            if (0.01f <= anisotropy + 1f)
                t = anisotropy + 1f;
            a = t * size;
        }
        else
        {
            var t = 0.01f;
            if (0.01f <= 1f - anisotropy)
                t = 1f - anisotropy;
            a = size;
            b = t * size;
        }
        var s = LightCrt.Tan(a * 0.5f * 0.017453292f);
        var u = LightCrt.Tan(b * 0.5f * 0.017453292f);
        if (0f < w)
        {
            var r = MathF.Sqrt(w);
            s *= r + 100f;
            u *= r + 100f;
        }
        return (s, u);
    }

    private static float Get(byte[] d, int o) => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o));

    private static void Set(byte[] d, int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(o), v);

    /// <summary>1817642b0 (m_nLuminaireShape 1): type 2, the apex and the two half-axes (0, s, 0), (0, 0, t), the normal (1, 0, 0).</summary>
    private static void Disk(byte[] d, float[] apex, float s, float t)
    {
        d[0xec] = 2;
        Set(d, 0xf0, apex[0]);
        Set(d, 0xf4, apex[1]);
        Set(d, 0xf8, apex[2]);
        Set(d, 0x100, s);
        Set(d, 0x110, t);
        Set(d, 0x114, 1f);
        var c = 1f / (s * t);
        Set(d, 0xc0, apex[3] != 0f ? apex[3] * c : c);
    }

    /// <summary>181764480 (m_nLuminaireShape 2): type 1, the four corners (0, +-s, +-t) around the apex, the normal (1, 0, 0).</summary>
    private static void Rectangle(byte[] d, float[] apex, float s, float t)
    {
        d[0xec] = 1;
        (float Y, float Z)[] corners = [(-s, -t), (-s, t), (s, -t), (s, t)];
        for (var i = 0; i < 4; i++)
        {
            Set(d, 0xf0 + 12 * i, 0f + apex[0]);
            Set(d, 0xf4 + 12 * i, corners[i].Y + apex[1]);
            Set(d, 0xf8 + 12 * i, corners[i].Z + apex[2]);
        }
        Set(d, 0x120, 1f);
        var c = 3.1415927f / (s * 4f * t);
        Set(d, 0xc0, apex[3] != 0f ? apex[3] * c : c);
    }
}
