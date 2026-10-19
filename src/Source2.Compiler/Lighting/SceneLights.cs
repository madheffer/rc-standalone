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
    public static LightShape Barn(LightPrecompute.KeyReader key, float[] world) => Record("light_barn", key, world);

    /// <summary>
    /// A light_barn's, light_rect's or light_omni2's scene record: as
    /// <see cref="Barn"/>, the intensity from the class's unit terms
    /// (<see cref="LightUnits.Terms"/>).
    /// </summary>
    public static LightShape Record(string className, LightPrecompute.KeyReader key, float[] world)
    {
        var r = LightPrecompute.Records(className, key, null, split: false, units: true)[0];
        if (CNumbers.Atoi(key("colormode") ?? "0") != 0)
            throw new NotSupportedException("a light's colortemperature (181007c90) is not ported");
        var color = Color(key, "color", Vector3.Zero);
        var (unitScale, factor, area, solid) = LightUnits.Terms(className, r);
        var intensity = LightUnits.FromUnits(key, unitScale, factor, area, solid);
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

    /// <summary>
    /// A light_environment's (or light_directional's) scene record:
    /// Light_LegacyDescription (180eed570) then FUN_181294010's directional
    /// branch. The description's colour is brightness times brightnessscale
    /// times "color" made linear; its direction is the light's forward axis
    /// negated (MatrixAngles of its matrix, AngleVectors). The record is an
    /// orthographic box of +-262144 by 524288 looking down x (all powers of
    /// two), its near plane, the direction as position (w 0), inner radius
    /// 0.9999999 both ways, cookie -1, distance fade (1, 0), angle fade (1, 0,
    /// 0), the orientation AngleQuaternion(VectorAngles(-direction)), and with
    /// an angular diameter a disc luminaire of tan(diameter / 2) both ways
    /// (FUN_1812a8060, FUN_181296170); then moved by the identity (18129a8d0).
    /// </summary>
    public static LightShape Directional(LightPrecompute.KeyReader key, Vector3 origin, Vector3 angles)
    {
        float Float(string name, float fallback) => key(name) is { } v ? CNumbers.ToFloat32(v) : fallback;
        var brightness = Float("brightness", 1f) * Float("brightnessscale", 1f);
        var c = Color(key, "color", Vector3.Zero);
        var color = new Vector3(brightness * c.X, brightness * c.Y, brightness * c.Z);
        var direction = -Forward(origin, angles);
        const float R = 262144f;
        var r = new LightShape();
        float[] to = [0f, -1f / R, 0f, 0f, 0f, 0f, 1f / R, 0f, 1f / (R + R), 0f, 0f, 0.5f, 0f, 0f, 0f, 1f];
        float[] from = [0f, 0f, R + R, -R, -R, 0f, 0f, 0f, 0f, R, 0f, 0f, 0f, 0f, 0f, 1f];
        to.CopyTo(r.Matrix);
        from.CopyTo(r.Inverse);
        var plane = LightBuild.Plane(r.Inverse);
        r[0x80] = plane.X;
        r[0x84] = plane.Y;
        r[0x88] = plane.Z;
        r[0x8c] = plane.W;
        r.SetV3(0x90, direction);
        r[0x9c] = 0f;
        r[0xa0] = r[0xa4] = BitConverter.Int32BitsToSingle(0x3f7ffffe);
        r.SetV3(0xb4, color);
        BitConverter.TryWriteBytes(r.Raw.AsSpan(0xc4), -1);
        r[0xc8] = 1f;
        r.Orientation = CTransform.AngleQuaternion(VectorAngles(-direction));
        r[0xe0] = 1f;
        if (!string.IsNullOrEmpty(key("lightcookie")))
            throw new NotSupportedException("a light cookie's texture index is not ported");
        var tan = LightCrt.Tan(MathF.Max(Float("angulardiameter", 0f), 0f) * 0.5f * 0.017453292f);
        if (0f < tan)
            LightBuild.DiscLuminaire(r, tan, tan);
        LightBuild.Transform(r, [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0]);
        return r;
    }

    // The light's forward axis as VisConfig.SunOf takes it: its matrix back to
    // angles (MatrixAngles), then AngleVectors.
    private static Vector3 Forward(Vector3 origin, Vector3 angles)
    {
        var local = MapMeshes.AngleMatrix(angles);
        local[3] = origin.X;
        local[7] = origin.Y;
        local[11] = origin.Z;
        var a = SettleWorld.MatrixAngles(MapMeshes.Concat([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0], local));
        const float Radians = 0.017453292f;
        float sp = Simulation.CrtMath.Sin(a.X * Radians), cp = Simulation.CrtMath.Cos(a.X * Radians);
        float sy = Simulation.CrtMath.Sin(a.Y * Radians), cy = Simulation.CrtMath.Cos(a.Y * Radians);
        return new Vector3(cy * cp, sy * cp, -sp);
    }

    /// <summary>FUN_18125b9e0 (VectorAngles): pitch and yaw in [0, 360) degrees, roll 0.</summary>
    public static Vector3 VectorAngles(Vector3 v)
    {
        float yaw, pitch;
        if (v.Y != 0f || v.X != 0f)
        {
            yaw = LightCrt.Atan2(v.Y, v.X) * 57.295776f;
            if (yaw < 0f)
                yaw += 360f;
            pitch = LightCrt.Atan2(-v.Z, MathF.Sqrt(v.X * v.X + v.Y * v.Y)) * 57.295776f;
            if (pitch < 0f)
                pitch += 360f;
        }
        else
        {
            yaw = 0f;
            pitch = v.Z <= 0f ? 90f : 270f;
        }
        return new Vector3(pitch, yaw, 0f);
    }

    /// <summary>
    /// 181113f20's <c>ibl</c> from the scene's sky light (its first
    /// light_environment) and the env_sky its skytexture names: path_trace_light_flags
    /// (0x40 for bouncelight 2, else 1); the env_sky's matrix; scale,
    /// skytexturescale times brightnessscale times the env_sky's brightness
    /// and tint; its skyname; sky_bounce, skyambientbounce made linear with
    /// alpha 1; sky_bounce_scale, skybouncescale times brightnessscale when
    /// bouncelight is 1 (or, unset, directlight 1 or 3), else 0; and
    /// sun_light_min_brightness. Null without a sky light.
    /// </summary>
    /// <remarks>The light's fields are read through their getters (180edb540,
    /// 180edb430, 180edb490, 1801bc490) and named after the keys whose values
    /// they hold on probe01. Not ported: no skytexture (the sky colour,
    /// 180edb4d0), sky_lightingonly_name, a tinted env_sky.</remarks>
    public static KVObject? Ibl(LightPrecompute.KeyReader? light, Func<string, (LightPrecompute.KeyReader Key, float[] Matrix)?> skyNamed)
    {
        if (light is null)
            return null;
        static float Float(LightPrecompute.KeyReader k, string name, float fallback) => k(name) is { } v ? CNumbers.ToFloat32(v) : fallback;
        static int Int(LightPrecompute.KeyReader k, string name, int fallback) => k(name) is { } v ? (int)CNumbers.Atoi(v) : fallback;
        var ibl = KVObject.Collection();
        var bounce = (sbyte)Int(light, "bouncelight", -1);
        ibl.Add("path_trace_light_flags", new KVObject(bounce == 2 ? 0x40 : 1));
        var name = light("skytexture") ?? "";
        if (name.Length == 0 || skyNamed(name) is not { } sky)
            throw new NotSupportedException("a sky light without an env_sky (the sky colour, 180edb4d0) is not ported");
        if (!string.IsNullOrEmpty(sky.Key("sky_lightingonly_name")))
            throw new NotSupportedException("an env_sky's sky_lightingonly_name is not ported");
        var tint = sky.Key("tint_color") ?? "255 255 255";
        if (tint.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is not ["255", "255", "255", ..])
            throw new NotSupportedException("a tinted env_sky is not ported");
        var a = KVObject.Array();
        foreach (var x in sky.Matrix)
            a.Add(new KVObject((double)x));
        ibl.Add("transform", a);
        var scale = Float(light, "brightnessscale", 1f) * Float(light, "skytexturescale", 1f) * Float(sky.Key, "brightnessscale", 1f);
        var sv = KVObject.Array();
        for (var i = 0; i < 3; i++)
            sv.Add(new KVObject((double)scale));
        ibl.Add("scale", sv);
        ibl.Add("material", new KVObject(sky.Key("skyname") ?? "") { Flag = KVFlag.ResourceName });
        var bounceColor = Color(light, "skyambientbounce", Vector3.Zero);
        var bv = KVObject.Array();
        foreach (var x in new[] { bounceColor.X, bounceColor.Y, bounceColor.Z, 1f })
            bv.Add(new KVObject((double)x));
        ibl.Add("sky_bounce", bv);
        var direct = (sbyte)Int(light, "directlight", 2);
        var bounces = bounce == -1 ? direct is 1 or 3 : bounce == 1;
        ibl.Add("sky_bounce_scale", new KVObject((double)(bounces ? Float(light, "skybouncescale", 1f) * Float(light, "brightnessscale", 1f) : 0f)));
        ibl.Add("sun_light_min_brightness", new KVObject((double)Float(light, "sunlightminbrightness", 0f)));
        return ibl;
    }

    /// <summary>
    /// FUN_180eece60's light info: directlight (default 2), bouncelight (-1:
    /// 1 for direct 1 or 3, 0 for 2), bouncescale, castshadows,
    /// bakedshadowindex (a signed byte) and the precomputed OBB.
    /// </summary>
    public sealed record Info(int Direct, int Bounce, float BounceScale, bool CastShadows, int BakedShadowIndex, LightObb.Box? Volume);

    public static Info InfoOf(LightPrecompute.KeyReader key)
    {
        int Int(string name, int fallback) => key(name) is { } v ? (int)CNumbers.Atoi(v) : fallback;
        Vector3 Vec(string name)
        {
            var parts = (key(name) ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            float P(int i) => i < parts.Length ? CNumbers.ToFloat32(parts[i]) : 0f;
            return new Vector3(P(0), P(1), P(2));
        }
        var direct = (int)(sbyte)Int("directlight", 2);
        var bounce = (int)(sbyte)Int("bouncelight", -1);
        if (bounce == -1)
            bounce = direct is 1 or 3 ? 1 : direct == 2 ? 0 : bounce;
        LightObb.Box? volume = Int("precomputedfieldsvalid", 0) != 0
            ? new LightObb.Box(CTransform.AngleQuaternion(Vec("precomputedobbangles")), Vec("precomputedobborigin"), Vec("precomputedobbextent"))
            : null;
        return new Info(direct, bounce, key("bouncescale") is { } b ? CNumbers.ToFloat32(b) : 1f, (sbyte)Int("castshadows", 0) != 0,
                        (sbyte)Int("bakedshadowindex", -1), volume);
    }

    /// <summary>
    /// 181112e80's entry for one light: its barn table, path_trace_light_flags
    /// (8 when it casts shadows, 6 for direct light 1 or 3, then 1 for bounce
    /// 1 at bounce scale 1, 0x40 for bounce 2), baked_shadow_index, with an
    /// index baked_shadow_light_id (the lights so far on that index), and with
    /// precomputed fields its illumination_volume. Null for a light the scene
    /// leaves out (direct 1 and not enabled).
    /// </summary>
    public static KVObject? Entry(LightPrecompute.KeyReader key, LightShape record, Dictionary<int, int> shadowIds)
        => Entries(key, record, shadowIds).LastOrDefault();

    /// <summary>
    /// The light's entries: <see cref="Entry"/>, after it, for a bounce light
    /// whose bounce scale is not 1, a separate bounce entry first: the record
    /// with its colour times the bounce scale, flags 1 (9 when it casts
    /// shadows), baked_shadow_index -1 and the light's illumination volume;
    /// the light's own entry then has no bounce flag.
    /// </summary>
    public static List<KVObject> Entries(LightPrecompute.KeyReader key, LightShape record, Dictionary<int, int> shadowIds)
    {
        var entries = new List<KVObject>();
        var info = InfoOf(key);
        var enabled = key("enabled") is not { } en || CNumbers.Atoi(en) != 0;
        var bounce = info.Bounce;
        if (info.Direct == 1 && !enabled)
            return entries;
        if (info.Direct == 3 && !enabled)
            bounce = 0;
        var flags = info.CastShadows ? 8u : 0u;
        if (info.Direct is 1 or 3)
            flags |= 6;
        if (bounce == 1 || (bounce == -1 && info.Direct is 1 or 3))
        {
            if (info.BounceScale != 1f)
            {
                var separate = record.Clone();
                var c = separate.V3(0xb4);
                separate.SetV3(0xb4, new Vector3(c.X * info.BounceScale, c.Y * info.BounceScale, c.Z * info.BounceScale));
                entries.Add(Write(separate, info.CastShadows ? 9u : 1u, -1, info.Volume, shadowIds));
            }
            else
                flags |= 1;
        }
        else if (bounce == 2)
            flags |= 0x40;
        entries.Add(Write(record, flags, info.BakedShadowIndex, info.Volume, shadowIds));
        return entries;
    }

    // 181113af0: one entry.
    private static KVObject Write(LightShape record, uint flags, int bakedShadowIndex, LightObb.Box? volume, Dictionary<int, int> shadowIds)
    {
        var e = KVObject.Collection();
        e.Add("barn", Table(record));
        e.Add("path_trace_light_flags", new KVObject(flags));
        e.Add("baked_shadow_index", new KVObject(bakedShadowIndex));
        if (bakedShadowIndex != -1)
        {
            var id = shadowIds.GetValueOrDefault(bakedShadowIndex);
            shadowIds[bakedShadowIndex] = id + 1;
            e.Add("baked_shadow_light_id", new KVObject((uint)(byte)id));
        }
        if (volume is { } v)
        {
            KVObject F(params float[] f)
            {
                var a = KVObject.Array();
                foreach (var x in f)
                    a.Add(new KVObject((double)x));
                return a;
            }
            var box = KVObject.Collection();
            box.Add("m_qOrientation", F(v.Rotation.X, v.Rotation.Y, v.Rotation.Z, v.Rotation.W));
            box.Add("m_vCenter", F(v.Origin.X, v.Origin.Y, v.Origin.Z));
            box.Add("m_vExtent", F(v.Extent.X, v.Extent.Y, v.Extent.Z));
            e.Add("illumination_volume", box);
        }
        return e;
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
        else if (r.Type == 1)
        {
            // 1812909e0: the rectangle's four corners and its normal.
            var l = KVObject.Collection();
            var positions = KVObject.Array();
            for (var i = 0; i < 4; i++)
                positions.Add(Floats(0xf0 + i * 12, 3));
            l.Add("m_Positions", positions);
            l.Add("m_vNormal", Floats(0x120, 3));
            t.Add("m_Luminaire", l);
        }
        else if (r.Type == 3)
        {
            // 181290c70: the sphere's position and radius.
            var l = KVObject.Collection();
            l.Add("m_vPosition", Floats(0xf0, 3));
            l.Add("m_flRadius", new KVObject((double)r[0xfc]));
            t.Add("m_Luminaire", l);
        }
        else if (r.Type is 4 or 5)
        {
            // 181296d40: the capsule's two ends, its two axes, radius and end-cap share.
            var l = KVObject.Collection();
            var ends = KVObject.Array();
            ends.Add(Floats(0xf0, 3));
            ends.Add(Floats(0xfc, 3));
            l.Add("m_Endpoints", ends);
            var axes = KVObject.Array();
            axes.Add(Floats(0x108, 3));
            axes.Add(Floats(0x114, 3));
            l.Add("m_Tangents", axes);
            l.Add("m_flRadius", new KVObject((double)r[0x120]));
            l.Add("m_flEndcapAreaPercentage", new KVObject((double)r[0x124]));
            t.Add("m_Luminaire", l);
        }
        else if (r.Type != 0)
            throw new NotSupportedException($"the {LuminaireTypes[r.Type]} luminaire's table (181297030) is not ported");
        return t;
    }
}
