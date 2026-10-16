using System.Numerics;
using Source2.Compiler.Maps;
using ValveKeyValue;

namespace Source2.Compiler.Lighting;

/// <summary>
/// The lights of the path-trace scene (scene_info.kv3's <c>lights</c>,
/// 181112e80): each light's record from FUN_180eea510 with flags 6 (the
/// brightness unit pass, moved to the world), serialised as its barn table
/// (181297030), with its path-trace flags and baked shadow ids.
/// </summary>
internal static class SceneLights
{
    /// <summary>18126a5a0: a colour's bytes over 255, clamped, sRGB to linear (V_powf at 2.4).</summary>
    public static Vector3 SrgbToLinear(int r, int g, int b)
    {
        static float Channel(float c)
        {
            c = 0f <= c ? (c <= 1f ? c : 1f) : 0f;
            return 0.04045f < c ? LightPow.Pow((c + 0.055f) / 1.055f, 2.4f) : c / 12.92f;
        }
        return new Vector3(Channel(r * 0.003921569f), Channel(g * 0.003921569f), Channel(b * 0.003921569f));
    }

    /// <summary>FUN_180f32200 for "color": "%d %d %d" as bytes, linear (<see cref="SrgbToLinear"/>); the default when absent.</summary>
    public static Vector3 Color(LightPrecompute.KeyReader key, string name, Vector3 fallback)
    {
        if (key(name) is not { } s)
            return fallback;
        var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int Part(int i) => i < parts.Length ? (byte)CNumbers.Atoi(parts[i]) : 0;
        return SrgbToLinear(Part(0), Part(1), Part(2));
    }

    /// <summary>
    /// A light_barn's scene record: FUN_180eea510's barn branch in the light's
    /// space with the unit pass (luminaire 0 a disc), its colour (colormode 0's
    /// color) times the intensity its brightness units give
    /// (<see cref="LightUnits.FromUnits"/> with BarnScale at 100 and the
    /// frustum's solid angle) times brightnessscale, its cookie (-1 without
    /// one), then moved to the world (18129a8d0).
    /// </summary>
    public static LightShape Barn(LightPrecompute.KeyReader key, float[] world)
    {
        var r = LightPrecompute.Records("light_barn", key, null, split: false, units: true)[0];
        if (CNumbers.Atoi(key("colormode") ?? "0") != 0)
            throw new NotSupportedException("a light's colortemperature (181007c90) is not ported");
        var color = Color(key, "color", Vector3.Zero);
        var intensity = LightUnits.FromUnits(key, LightUnits.BarnScale(r, 100f), LightUnits.FrustumSolidAngle(r, BitConverter.Int32BitsToSingle(0x3c026136)), 0f, 0f);
        color = new Vector3(intensity * color.X, intensity * color.Y, intensity * color.Z);
        var scale = key("brightnessscale") is { } s ? CNumbers.ToFloat32(s) : 1f;
        color = new Vector3(scale * color.X, scale * color.Y, scale * color.Z);
        r.SetV3(0xb4, color);
        if (!string.IsNullOrEmpty(key("lightcookie")))
            throw new NotSupportedException("a light cookie's texture index is not ported");
        BitConverter.TryWriteBytes(r.Raw.AsSpan(0xc4), -1);
        LightBuild.Transform(r, world);
        return r;
    }

    private static readonly string[] LuminaireTypes = ["PUNCTUAL", "QUAD", "DISK", "SPHERE", "TUBE", "TUBE_WITH_END_CAPS"];

    /// <summary>181297030: the record's barn table.</summary>
    public static KVObject Table(LightShape r)
    {
        KVObject Floats(int offset, int count)
        {
            var a = KVObject.Array();
            for (var i = 0; i < count; i++)
                a.Add(new KVObject((double)r[offset + i * 4]));
            return a;
        }
        var t = KVObject.Collection();
        t.Add("m_mToClipSpace", Floats(0, 16));
        t.Add("m_mFromClipSpace", Floats(0x40, 16));
        t.Add("m_vNearPlane", Floats(0x80, 4));
        t.Add("m_vLightPosition", Floats(0x90, 4));
        t.Add("m_vInnerRadius", Floats(0xa0, 2));
        t.Add("m_flShape", new KVObject((double)r[0xa8]));
        t.Add("m_vSkirt", Floats(0xac, 2));
        t.Add("m_vColor", Floats(0xb4, 3));
        t.Add("m_nCookie", new KVObject(BitConverter.ToInt32(r.Raw, 0xc4)));
        t.Add("m_vDistanceFade", Floats(0xc8, 2));
        t.Add("m_qOrientation", Floats(0xd0, 4));
        t.Add("m_vAngleFade", Floats(0xe0, 3));
        t.Add("m_nLuminaireType", r.Type < LuminaireTypes.Length ? new KVObject(LuminaireTypes[r.Type]) : new KVObject((int)r.Type));
        t.Add("m_flLightColorToRadiance", new KVObject((double)r[0xc0]));
        t.Add("m_bLightInsideFrustum", new KVObject((r.Flags & 1) != 0));
        t.Add("m_bSphericalCookie", new KVObject((r.Flags & 2) != 0));
        if (r.Type == 2)
        {
            var l = KVObject.Collection();
            l.Add("m_vCenter", Floats(0xf0, 3));
            var tangents = KVObject.Array();
            tangents.Add(Floats(0xfc, 3));
            tangents.Add(Floats(0x108, 3));
            l.Add("m_ScaledTangents", tangents);
            l.Add("m_vNormal", Floats(0x114, 3));
            t.Add("m_Luminaire", l);
        }
        else if (r.Type != 0)
            throw new NotSupportedException($"the {LuminaireTypes[r.Type]} luminaire's table (181297030) is not ported");
        return t;
    }
}
