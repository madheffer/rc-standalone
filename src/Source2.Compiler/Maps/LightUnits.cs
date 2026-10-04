using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The brightness unit keys a light_barn, light_rect or light_omni2 leaves the
/// preprocess with (Light_SyncBrightnessUnits, 180eee150 in the 09-23 build):
/// its whole record built with flags 2 (no split, no world move; an omni's
/// shape 3 a sphere, a barn's luminaire 0 a disc), its intensity from the key
/// <c>brightness_units</c> names (Light_BrightnessFromUnits 180eed3c0), and
/// the other units written back through "%g".
/// </summary>
public static class LightUnits
{
    /// <summary>
    /// The keys to write, in order: brightness (units other than 0), then
    /// brightness_lumens (rounded; not for units 1), brightness_nits (with a
    /// luminaire area; not for 2), brightness_candelas (with a solid angle;
    /// not for 4), brightness_legacy (not for 3), each "%g" of a float.
    /// None for a class with no record.
    /// </summary>
    public static List<KeyValuePair<string, string>> Sync(string className, LightPrecompute.KeyReader key)
    {
        var written = new List<KeyValuePair<string, string>>();
        var records = LightPrecompute.Records(className, key, null, split: false, units: true);
        if (records.Length != 1)
            return written;
        var r = records[0];
        float scale = 0f, factor = 0f, area = 0f, solid = 0f;
        if (className.Equals("light_barn", StringComparison.OrdinalIgnoreCase))
        {
            scale = BarnScale(r, 100f);
            factor = FrustumSolidAngle(r, BitConverter.Int32BitsToSingle(0x3c026136));
        }
        else if (className.Equals("light_rect", StringComparison.OrdinalIgnoreCase))
        {
            scale = 100f * 100f;
            factor = 0.025f;
            area = LuminaireArea(r);
            solid = SolidAngle(r.Type, 180f, 180f);
        }
        else if (className.Equals("light_omni2", StringComparison.OrdinalIgnoreCase))
        {
            var outer = ConeAngle(r);
            var inner = InnerConeAngle(r);
            solid = SolidAngle(r.Type, inner, outer);
            scale = 100f * 100f;
            factor = solid * 0.007957747f;
            area = LuminaireArea(r);
        }
        var intensity = FromUnits(key, scale, factor, area, solid);
        var lumens = intensity * factor;
        var units = CNumbers.Atoi(key("brightness_units") ?? "0");
        void Write(string name, float value) => written.Add(new(name, CNumbers.FormatG(value)));
        float Round(float x) => MathF.Round(x, MidpointRounding.AwayFromZero);

        // 180eee150's flow: units 0 skips brightness, 1 lumens, 2 nits, 4
        // candelas, 3 legacy.
        if (units != 0)
            Write("brightness", MathF.Floor(MathF.Log2(intensity / scale) * 256f + 0.5f) * 0.00390625f);
        if (units != 1)
            Write("brightness_lumens", Round(lumens));
        if (units != 2 && 0f < area)
            Write("brightness_nits", Round(lumens / (area * 3.1415927f * 0.00064516f)));
        if (units != 4 && 0f < solid)
            Write("brightness_candelas", Round(lumens / solid));
        if (units != 3)
            Write("brightness_legacy", MathF.Floor(intensity / scale * 256f + 0.5f) * 0.00390625f);
        return written;
    }

    /// <summary>
    /// Light_BrightnessFromUnits (180eed3c0): by brightness_units, 0
    /// exp2(brightness) times the scale, 1 lumens over the factor, 2 nits times
    /// the area, pi and 0.00064516 over the factor, 3 legacy times the scale, 4
    /// candelas times the solid angle over the factor; anything else 0.
    /// </summary>
    public static float FromUnits(LightPrecompute.KeyReader key, float scale, float factor, float area, float solid)
    {
        float Read(string name) => key(name) is { } v ? CNumbers.ToFloat32(v) : 0f;
        return CNumbers.Atoi(key("brightness_units") ?? "0") switch
        {
            0 => float.Exp2(Read("brightness")) * scale,
            1 => Read("brightness_lumens") / factor,
            2 => Read("brightness_nits") * area * 3.1415927f * 0.00064516f / factor,
            3 => Read("brightness_legacy") * scale,
            4 => Read("brightness_candelas") * solid / factor,
            _ => 0f,
        };
    }

    /// <summary>LightUnits_SolidAngle (181298c00): pi for a disc or rectangle luminaire (1, 2), the cone between two angles for a sphere or capsule (3 to 5), else 0.</summary>
    public static float SolidAngle(byte type, float a, float b)
    {
        if (type is 1 or 2)
            return 3.1415927f;
        if (type is not (3 or 4 or 5))
            return 0f;
        var ca = LightCrt.Cos(a * 0.017453292f);
        var cb = LightCrt.Cos(b * 0.017453292f);
        return (1f - (cb + ca) * 0.5f) * 6.2831855f;
    }

    /// <summary>FUN_181298b90: the record's cone angle in degrees, acos(+0xe0 / +0xe4) clamped; 180 or 0 when +0xe4 is 0.</summary>
    public static float ConeAngle(LightShape r)
    {
        if (r[0xe4] != 0f)
            return MathF.Acos(Math.Clamp(r[0xe0] / r[0xe4], -1f, 1f)) * 57.295776f;
        return r[0xe0] == 1f ? 180f : 0f;
    }

    /// <summary>FUN_1812969d0: acos(+0xe0 / +0xe4 - 1 / +0xe4) in degrees, clamped; 180 or 0 when +0xe4 is 0.</summary>
    public static float InnerConeAngle(LightShape r)
    {
        var w = r[0xe4];
        if (w != 0f)
            return MathF.Acos(Math.Clamp(r[0xe0] / w - 1f / w, -1f, 1f)) * 57.295776f;
        return r[0xe0] == 1f ? 180f : 0f;
    }

    /// <summary>LightUnits_BarnScale (18129a6e0) on the record's +0x90: 1 / (w / (sqrt(w) + d)^2) with w = +0x9c, or 1.</summary>
    public static float BarnScale(LightShape r, float d)
    {
        var w = r[0x9c];
        if (0f < w)
        {
            var s = MathF.Sqrt(w);
            return 1f / (w / ((s + d) * (s + d)));
        }
        return 1f;
    }

    /// <summary>
    /// LightUnits_LuminaireArea (1812938f0) by the luminaire type (+0xec):
    /// 1 two triangles of the corners, 2 pi times the two half axes'
    /// lengths, 3 4 pi r^2, 4 and 5 a capsule's side (and with +0x124 its
    /// caps).
    /// </summary>
    public static float LuminaireArea(LightShape r)
    {
        switch (r.Type)
        {
            case 1:
            {
                var first = TriangleArea(r.V3(0xf0), r.V3(0x114), r.V3(0x108));
                return TriangleArea(r.V3(0xf0), r.V3(0xfc), r.V3(0x114)) + first;
            }
            case 2:
            {
                var a = r.V3(0xfc);
                var b = r.V3(0x108);
                var la = MathF.Sqrt(r[0x104] * r[0x104] + a.Y * a.Y + a.X * a.X);
                var lb = MathF.Sqrt(r[0x110] * r[0x110] + b.Y * b.Y + b.X * b.X);
                return la * 3.1415927f * lb;
            }
            case 3:
                return r[0xfc] * r[0xfc] * 12.566371f;
            case 4 or 5:
            {
                float dx = r[0xf0] - r[0xfc], dz = r[0xf8] - r[0x104], dy = r[0xf4] - r[0x100];
                var radius = r[0x120];
                var side = MathF.Sqrt(dy * dy + dz * dz + dx * dx) * 0.5f * (radius * 6.2831855f + radius * 6.2831855f);
                return 0f < r[0x124] ? side + radius * radius * 6.2831855f : side;
            }
            default:
                return 0f;
        }
    }

    // FUN_18125ceb0: half the length of (c - a) x (b - a), in the binary's term order.
    private static float TriangleArea(Vector3 a, Vector3 b, Vector3 c)
    {
        var x = (c.X - a.X) * (b.Z - a.Z) - (c.Z - a.Z) * (b.X - a.X);
        var y = (c.Z - a.Z) * (b.Y - a.Y) - (c.Y - a.Y) * (b.Z - a.Z);
        var z = (c.Y - a.Y) * (b.X - a.X) - (c.X - a.X) * (b.Y - a.Y);
        return MathF.Sqrt(z * z + x * x + y * y) * 0.5f;
    }

    /// <summary>
    /// FUN_1812918f0: the solid angle a barn's frustum opening subtends from
    /// its apex, times <paramref name="k"/>. The four far corners of the unit
    /// square through the inverse matrix (+0x40), each made a unit direction
    /// from the apex (+0x90), split into two spherical triangles summed by
    /// L'Huilier's theorem, times the apex's w (+0x9c); with w 0 (a parallel
    /// barn) the opening's area along its normal instead.
    /// </summary>
    public static float FrustumSolidAngle(LightShape r, float k)
    {
        Span<float> m = stackalloc float[16];
        for (var i = 0; i < 16; i++)
            m[i] = r[0x40 + i * 4];
        var p = new Vector3(r[0x90], r[0x94], r[0x98]);
        var w = r[0x9c];
        float z14 = m[14] * 0f, z2 = m[2] * 0f, z6 = m[6] * 0f, z10 = m[10] * 0f;
        // Corner (-1, -1).
        var w0 = 1f / (m[12] * -1f + m[13] * -1f + z14 + m[15]);
        var c0 = new Vector3((m[0] * -1f + m[1] * -1f + z2 + m[3]) * w0, (m[4] * -1f + m[5] * -1f + z6 + m[7]) * w0,
                             (m[8] * -1f + m[9] * -1f + z10 + m[11]) * w0);
        // Corner (1, -1).
        var w1 = 1f / (m[12] + m[13] * -1f + z14 + m[15]);
        var c1 = new Vector3((m[0] + m[1] * -1f + z2 + m[3]) * w1, (m[4] + m[5] * -1f + z6 + m[7]) * w1,
                             (m[8] + m[9] * -1f + z10 + m[11]) * w1);
        // Corner (1, 1).
        var w2 = 1f / (m[12] + m[13] + z14 + m[15]);
        var c2 = new Vector3((m[0] + m[1] + z2 + m[3]) * w2, (m[4] + m[5] + z6 + m[7]) * w2, (m[8] + m[9] + z10 + m[11]) * w2);
        // Corner (-1, 1).
        var w3 = 1f / (m[13] + m[12] * -1f + z14 + m[15]);
        var c3 = new Vector3((m[1] + m[0] * -1f + z2 + m[3]) * w3, (m[5] + m[4] * -1f + z6 + m[7]) * w3,
                             (m[9] + m[8] * -1f + z10 + m[11]) * w3);
        float result;
        if (w != 0f)
        {
            var d0 = Unit(c0 - p, zFirst: false);
            var d1 = Unit(c1 - p, zFirst: false);
            var d2 = Unit(c2 - p, zFirst: false);
            var d3 = Unit(c3 - p, zFirst: true);
            float a1 = Angle(d2, d1), b1 = Angle(d2, d0), c1a = Angle(d1, d0);
            var first = 0f;
            if (a1 != 0f && b1 != 0f && c1a != 0f)
            {
                var s = (b1 + a1 + c1a) * 0.5f;
                float ss = LightCrt.Sin(s), sa = LightCrt.Sin(s - a1), sb = LightCrt.Sin(s - b1), sc = LightCrt.Sin(s - c1a);
                var t = MathF.Atan(MathF.Sqrt(sc * sa / (sb * ss)));
                t += MathF.Atan(MathF.Sqrt(sc * sb / (sa * ss)));
                var u = MathF.Atan(MathF.Sqrt(sb * sa / (sc * ss)));
                first = u + t + u + t - 3.1415927f;
            }
            float a2 = Angle(d3, d2), b2 = Angle(d3, d0), c2a = MathF.Acos(Math.Clamp(Dot(d2, d0), -1f, 1f));
            var second = 0f;
            if (a2 != 0f && b2 != 0f && c2a != 0f)
            {
                var s = (b2 + a2 + c2a) * 0.5f;
                float ss = LightCrt.Sin(s), sa = LightCrt.Sin(s - a2), sb = LightCrt.Sin(s - b2), sc = LightCrt.Sin(s - c2a);
                var t1 = MathF.Atan(MathF.Sqrt(sc * sa / (sb * ss)));
                var t2 = MathF.Atan(MathF.Sqrt(sc * sb / (sa * ss)));
                var t3 = MathF.Atan(MathF.Sqrt(sb * sa / (sc * ss)));
                var sum = t3 + t1 + t2;
                second = sum + sum - 3.1415927f;
            }
            result = (second + first) * w;
        }
        else
        {
            // The opening's normal (corners 0, 1, 3), its length the area.
            float ey = c3.Y - c0.Y, ez = c3.Z - c0.Z, ex = c3.X - c0.X;
            var nx = (c1.Y - c0.Y) * ez - (c1.Z - c0.Z) * ey;
            var nz = ey * (c1.X - c0.X) - ex * (c1.Y - c0.Y);
            var ny = ex * (c1.Z - c0.Z) - (c1.X - c0.X) * ez;
            var n = new Vector3(nx, ny, nz);
            var length = MathF.Sqrt(nz * nz + ny * ny + nx * nx);
            Vector3 u;
            if (length < 1e-17f || 1e17f < length)
                u = length != 0f ? VectorNormalizeSlow.Normalise(n, out length) : Vector3.Zero;
            else
            {
                var inv = 1f / length;
                u = new Vector3(inv * nx, inv * ny, inv * nz);
            }
            result = (p.Z * u.Z + p.Y * u.Y + p.X * u.X) * length;
        }
        return result * k;
    }

    private static float Dot(Vector3 a, Vector3 b) => a.Z * b.Z + a.Y * b.Y + a.X * b.X;

    // acos of a clamped dot product, summed z, y, x as the binary does.
    private static float Angle(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Dot(a, b), -1f, 1f));

    // The unit vector (length summed y, z, x, or z, y, x for the last corner),
    // VectorNormalize_Slow outside [1e-17, 1e17], or zero.
    private static Vector3 Unit(Vector3 v, bool zFirst)
    {
        var length = MathF.Sqrt(zFirst ? v.Z * v.Z + v.Y * v.Y + v.X * v.X : v.Y * v.Y + v.Z * v.Z + v.X * v.X);
        if (length < 1e-17f || 1e17f < length)
            return length != 0f ? VectorNormalizeSlow.Normalise(v, out _) : Vector3.Zero;
        var inv = 1f / length;
        return new Vector3(inv * v.X, inv * v.Y, v.Z * inv);
    }
}
