using System.Globalization;
using System.Numerics;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.Maps;

/// <summary>
/// LightPrecompute_SampleVolume (FUN_180f19f20) for light_barn and light_omni2:
/// the keys the compile gives a light that casts shadows, the bounds and
/// oriented box of what its rays reach and, for an omni light cut into cube
/// faces, one box per face (FUN_180f1def0, FUN_180f1e0e0).
/// </summary>
public static class LightPrecompute
{
    /// <summary>Rays per record (0x6000).</summary>
    public const int Rays = 0x6000;

    /// <summary>A light's keys as the compile reads them (FUN_180f32050 and friends): case-blind, absent is null.</summary>
    public delegate string? KeyReader(string name);

    /// <summary>
    /// The precomputed keys in the order the lump lists them (the reverse of
    /// the order they are written), or none when the light gets none: not a
    /// barn or omni2, or not casting shadows (with "fog" set, fogshadows too).
    /// <paramref name="world"/> is the light's 3x4, the instance path's matrix
    /// times its own AngleMatrix(angles, origin) (FUN_180ffddd0, slot 0xa0).
    /// </summary>
    public static List<KeyValuePair<string, string>> Keys(string className, KeyReader key, float[] world, ILightTracer scene)
    {
        var written = new List<KeyValuePair<string, string>>();
        var whole = Records(className, key, world, split: false);
        if (whole.Length == 0)
            return written;
        var shadows = Int(key, "castshadows", 0) != 0;
        if (Int(key, "fog", 0) != 0)
            shadows = shadows && Int(key, "fogshadows", 0) != 0;
        if (!shadows)
            return written;
        if (whole[0][0x9c] == 0f)
            throw new NotSupportedException("the light at infinity test (FUN_181296a50 through FUN_181292560) is not ported");

        var r = LightTrace.Run(whole[0], Rays, scene);
        written.Add(new("precomputedfieldsvalid", "1"));
        written.Add(new("precomputedobborigin", Vector(r.Box.Origin)));
        written.Add(new("precomputedobbangles", Angles(r.Box.Rotation)));
        written.Add(new("precomputedobbextent", Vector(r.Box.Extent)));
        written.Add(new("precomputedboundsmins", Vector(r.Mins)));
        written.Add(new("precomputedboundsmaxs", Vector(r.Maxs)));

        var faces = Records(className, key, world, split: true);
        if (faces.Length >= 2)
        {
            for (var i = 0; i < faces.Length; i++)
            {
                LightBuild.SphereLuminaire(faces[i], 1e-6f);
                var f = LightTrace.Run(faces[i], Rays, scene);
                written.Add(new($"precomputedobborigin{i}", Vector(f.Box.Origin)));
                written.Add(new($"precomputedobbangles{i}", Angles(f.Box.Rotation)));
                written.Add(new($"precomputedobbextent{i}", Vector(f.Box.Extent)));
            }
            written.Add(new("precomputedsubfrusta", faces.Length.ToString(CultureInfo.InvariantCulture)));
        }
        written.Reverse();
        return written;
    }

    /// <summary>
    /// The light records of FUN_180eea510 with flags 4 (whole) or 5 (split),
    /// in world space: a barn's one frustum, or an omni's whole record or
    /// its cube faces. Empty for any other class.
    /// </summary>
    public static LightShape[] Records(string className, KeyReader key, float[] world, bool split)
    {
        var barn = className.Equals("light_barn", StringComparison.OrdinalIgnoreCase);
        var omni = className.Equals("light_omni2", StringComparison.OrdinalIgnoreCase);
        if (className.Equals("light_rect", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("light_rect's branch of FUN_180eea510 is not ported");
        if (!barn && !omni)
            return [];
        var size = Vector3Key(key, "size_params", new Vector3(16f, 16f, 0.0625f));
        size = new Vector3(size.X <= 1f ? 1f : size.X, size.Y <= 1f ? 1f : size.Y, 0f <= size.Z ? size.Z : 0f);
        var range = Float(key, "range", 256f);
        var shear = Vector3Key(key, "shear", Vector3.Zero);
        var skirtNear = Float(key, "skirt_near", 0f);
        var skirt = Float(key, "skirt", 0f);
        LightShape[] records;
        if (omni)
        {
            var shape = Int(key, "shape", 0);
            var outer = Float(key, "outer_angle", 180f);
            outer = outer <= 1f ? 1f : outer;
            outer = 180f <= outer ? 180f : outer;
            var inner = Float(key, "inner_angle", 180f);
            inner = inner <= 0f ? 0f : inner;
            inner = outer <= inner ? outer : inner;
            if (range <= 1f)
                range = 1f;
            var max = (LightSampler.MaxCoord + LightSampler.MaxCoord) * 1.7320508f;
            if (max <= range)
                range = max;
            var length = shape is 3 or 0 ? 0f : size.Y;
            records = LightOmni.Frusta(split, range, outer, inner, length, skirt, 1f);
            foreach (var l in records)
            {
                if (shape == 0)
                    LightBuild.SphereLuminaire(l, size.X);
                else if (shape is 1 or 2)
                    throw new NotSupportedException("an omni2 capsule luminaire (FUN_181296730) is not ported");
            }
        }
        else
        {
            records =
            [
                LightBuild.Barn(size, range, new Vector2(shear.X, shear.Y), skirtNear, skirt,
                                Float(key, "soft_x", 0.25f), Float(key, "soft_y", 0.25f), Float(key, "shape", 1f),
                                Int(key, "luminaire_shape", 0), Float(key, "luminaire_size", 0f),
                                Float(key, "luminaire_anisotropy", 0f)),
            ];
        }
        foreach (var l in records)
            LightBuild.Transform(l, world);
        return records;
    }

    /// <summary>A light's own 3x4 under no instance: identity times AngleMatrix(angles) with the origin.</summary>
    public static float[] World(Vector3 origin, Vector3 angles)
    {
        var local = MapMeshes.AngleMatrix(angles);
        local[3] = origin.X;
        local[7] = origin.Y;
        local[11] = origin.Z;
        return LightMath.Concat34([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0], local);
    }

    /// <summary>"%f %f %f".</summary>
    public static string Vector(Vector3 v) => $"{F(v.X)} {F(v.Y)} {F(v.Z)}";

    /// <summary>The box's quaternion as pitch, yaw, roll (FUN_181260200), "%f %f %f".</summary>
    public static string Angles(Quaternion q)
    {
        var a = SettleWriteBack.Angles(new Quat(q.X, q.Y, q.Z, q.W));
        return $"{F(a.X)} {F(a.Y)} {F(a.Z)}";
    }

    private static string F(float f) => ((double)f).ToString("F6", CultureInfo.InvariantCulture);

    // FUN_180f32050 (V_atofloat32), FUN_180f32130 (V_atoi) and FUN_180f32390
    // (sscanf "%f %f %f" over the default, so a short value keeps the rest).
    private static float Float(KeyReader key, string name, float fallback)
        => key(name) is not { } s ? fallback
           : float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;

    private static int Int(KeyReader key, string name, int fallback)
    {
        if (key(name) is not { } s)
            return fallback;
        s = s.TrimStart();
        var end = 0;
        if (end < s.Length && (s[end] == '-' || s[end] == '+'))
            end++;
        while (end < s.Length && char.IsAsciiDigit(s[end]))
            end++;
        return int.TryParse(s.AsSpan(0, end), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i) ? i : 0;
    }

    private static Vector3 Vector3Key(KeyReader key, string name, Vector3 fallback)
    {
        if (key(name) is not { } s)
            return fallback;
        var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Span<float> v = [fallback.X, fallback.Y, fallback.Z];
        for (var i = 0; i < 3 && i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                break;
            v[i] = f;
        }
        return new Vector3(v[0], v[1], v[2]);
    }
}
