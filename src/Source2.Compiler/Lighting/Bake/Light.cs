using System.Numerics;
using Source2.Compiler.Maps;
using ValveKeyValue;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// PathTraceBarnLightGPU_t (496 bytes), as scenesystem fills it from a
/// scene_info light (18008beb0 copies the 300-byte LightShape, 18008c110
/// maps it, the loader adds the rest):
/// <list type="bullet">
/// <item>the clip matrices, near plane and position as they are; colour and
/// bounce colour both the record's colour; orientation; the angle fade;
/// m_vDistanceFade_vSkirt (distance fade x, y, skirt x, y);</item>
/// <item>m_vCookieParameters: (inner radius x, y, shape, 0) without a
/// cookie, else (0, 0, 1, 1) and the cookie's bindless index;</item>
/// <item>luminaire vector 0's w the colour-to-radiance factor, vector 1's w
/// the luminaire's area (1804a3a60: a disc pi |t1| |t2|, a sphere 4 pi r^2,
/// a quad its two triangles, a tube its side and caps); the vectors the
/// luminaire's (quad corners; disc centre, tangents, normal; sphere centre
/// and radius; tube ends, axes, radius and cap flag);</item>
/// <item>the illumination volume as m_mBarnIlluminationFromWorld (the box's
/// inverse rotation and translation scaled by 1 / extent,
/// <see cref="PathTraceBounds.IlluminationFromWorld"/>; zero without one,
/// which keeps every point inside), its centre, orientation and extent, and
/// the light's bounds; the path trace flags, baked shadow index and the
/// shadowed light id.</item>
/// </list>
/// </summary>
public sealed class Light
{
    public Matrix4x4 ToClipSpace, FromClipSpace;
    public Vector4 NearPlane, Position, CookieParameters, Color, BounceColor, Orientation, AngleFade, DistanceFadeSkirt;
    public readonly Vector4[] Luminaire = new Vector4[5];
    /// <summary>Rows of the 3x4 world to illumination-box matrix.</summary>
    public Vector4 IlluminationRow0, IlluminationRow1, IlluminationRow2;
    /// <summary>
    /// m_vMinBounds / m_vMaxBounds (w 0): the clip box's AABB (1804a2b70), cut by the illumination
    /// box's AABB when there is one (maxss / minss); the light binner's input.
    /// </summary>
    public Vector4 MinBounds, MaxBounds;
    /// <summary>m_vIlluminationBoundsCenter (w 0), m_qIlluminationBoundsOrient, m_vIlluminationBoundsExtent (w 0); zero without a volume.</summary>
    public Vector4 IlluminationCenter, IlluminationOrientation, IlluminationExtent;
    public int Cookie = -1, LuminaireType, PathTraceFlags, SphericalCookie, BakedShadowIndex = -1, ShadowedLightId = 0xff;

    /// <summary>A light of scene_info.kv3's lights array.</summary>
    public static Light FromEntry(KVObject entry)
    {
        var r = Shape(entry["barn"]);
        var l = new Light
        {
            ToClipSpace = Row(r.F[..16]),
            FromClipSpace = Row(r.F[16..32]),
            NearPlane = V4(r, 0x80),
            Position = V4(r, 0x90),
            Orientation = V4(r, 0xd0),
            AngleFade = new Vector4(r[0xe0], r[0xe4], r[0xe8], 0f),
            DistanceFadeSkirt = new Vector4(r[0xc8], r[0xcc], r[0xac], r[0xb0]),
            Cookie = BitConverter.ToInt32(r.Raw, 0xc4),
            LuminaireType = r.Type,
            SphericalCookie = r.Flags >> 1 & 1,
        };
        l.Color = l.BounceColor = new Vector4(r.V3(0xb4), 0f);
        l.CookieParameters = l.Cookie == -1 ? new Vector4(r[0xa0], r[0xa4], r[0xa8], 0f) : new Vector4(0f, 0f, 1f, 1f);
        switch (r.Type)
        {
            case 1:
                for (var i = 0; i < 5; i++)
                    l.Luminaire[i] = new Vector4(r.V3(0xf0 + i * 12), 0f);
                break;
            case 2:
                for (var i = 0; i < 4; i++)
                    l.Luminaire[i] = new Vector4(r.V3(0xf0 + i * 12), 0f);
                break;
            case 3:
                l.Luminaire[0] = new Vector4(r.V3(0xf0), 0f);
                l.Luminaire[1] = new Vector4(r[0xfc], 0f, 0f, 0f);
                break;
            case 4 or 5:
                for (var i = 0; i < 4; i++)
                    l.Luminaire[i] = new Vector4(r.V3(0xf0 + i * 12), 0f);
                l.Luminaire[4] = new Vector4(r[0x120], r[0x124], 0f, 0f);
                break;
            default:
                l.LuminaireType = 0;
                break;
        }
        l.Luminaire[0].W = r[0xc0];
        l.Luminaire[1].W = Area(r);
        l.PathTraceFlags = BakeScene.Opt(entry, "path_trace_light_flags") is { } f ? (int)f.ToInt64(null) : 0;
        l.BakedShadowIndex = BakeScene.Opt(entry, "baked_shadow_index") is { } b ? (int)b.ToInt64(null) : -1;
        l.ShadowedLightId = BakeScene.Opt(entry, "baked_shadow_light_id") is { } s ? (int)s.ToInt64(null) : 0xff;
        var (min, max) = PathTraceBounds.ClipBounds(r.F.Slice(16, 16).ToArray());
        if (BakeScene.Opt(entry, "illumination_volume") is { ValueType: KVValueType.Collection } v)
        {
            var qf = BakeScene.Floats(v["m_qOrientation"]);
            var cf = BakeScene.Floats(v["m_vCenter"]);
            var ef = BakeScene.Floats(v["m_vExtent"]);
            var q = new Vector4(qf[0], qf[1], qf[2], qf[3]);
            var c = new Vector3(cf[0], cf[1], cf[2]);
            var e = new Vector3(ef[0], ef[1], ef[2]);
            var m = PathTraceBounds.IlluminationFromWorld(q, c, e);
            l.IlluminationRow0 = new Vector4(m[0], m[1], m[2], m[3]);
            l.IlluminationRow1 = new Vector4(m[4], m[5], m[6], m[7]);
            l.IlluminationRow2 = new Vector4(m[8], m[9], m[10], m[11]);
            (l.IlluminationCenter, l.IlluminationOrientation, l.IlluminationExtent) = (new Vector4(c, 0f), q, new Vector4(e, 0f));
            var (bmin, bmax) = PathTraceBounds.BoxBounds(q, c, e);
            min = new Vector3(PathTraceBounds.MaxSs(min.X, bmin.X), PathTraceBounds.MaxSs(min.Y, bmin.Y), PathTraceBounds.MaxSs(min.Z, bmin.Z));
            max = new Vector3(PathTraceBounds.MinSs(max.X, bmax.X), PathTraceBounds.MinSs(max.Y, bmax.Y), PathTraceBounds.MinSs(max.Z, bmax.Z));
        }
        else
            // No volume (18009347c): orientation zero, extents FLT_MAX (not yet checked against a capture).
            l.IlluminationExtent = new Vector4(float.MaxValue, float.MaxValue, float.MaxValue, 0f);
        (l.MinBounds, l.MaxBounds) = (new Vector4(min, 0f), new Vector4(max, 0f));
        return l;
    }

    /// <summary>Whether (M p).xyz lies in [-1, 1]^3 (always, for the zero matrix).</summary>
    public bool InsideIllumination(Vector3 p)
    {
        var x = Vector4.Dot(IlluminationRow0, new Vector4(p, 1f));
        var y = Vector4.Dot(IlluminationRow1, new Vector4(p, 1f));
        var z = Vector4.Dot(IlluminationRow2, new Vector4(p, 1f));
        return MathF.Abs(x) <= 1f && MathF.Abs(y) <= 1f && MathF.Abs(z) <= 1f;
    }

    private static Vector4 V4(LightShape r, int o) => new(r[o], r[o + 4], r[o + 8], r[o + 12]);

    // A row-major 4x4 as stored (row r is elements 4r..4r+3), kept so that Transform(p) = M p.
    private static Matrix4x4 Row(ReadOnlySpan<float> m) => new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);

    /// <summary>M p for the row-major matrix M (clip space).</summary>
    public static Vector4 Apply(in Matrix4x4 m, Vector3 p) => new(
        m.M11 * p.X + m.M12 * p.Y + m.M13 * p.Z + m.M14,
        m.M21 * p.X + m.M22 * p.Y + m.M23 * p.Z + m.M24,
        m.M31 * p.X + m.M32 * p.Y + m.M33 * p.Z + m.M34,
        m.M41 * p.X + m.M42 * p.Y + m.M43 * p.Z + m.M44);

    // 1804a3a60.
    private static float Area(LightShape r)
    {
        switch (r.Type)
        {
            case 1:
                return TriangleArea(r.V3(0xf0), r.V3(0x114), r.V3(0x108)) + TriangleArea(r.V3(0xf0), r.V3(0xfc), r.V3(0x114));
            case 2:
                return r.V3(0xfc).Length() * 3.1415927f * r.V3(0x108).Length();
            case 3:
                return r[0xfc] * r[0xfc] * 12.566371f;
            case 4 or 5:
                var radius = r[0x120];
                var side = (r.V3(0xf0) - r.V3(0xfc)).Length() * 0.5f * (radius * 6.2831855f + radius * 6.2831855f);
                return 0f < r[0x124] ? side + radius * radius * 6.2831855f : side;
            default:
                return 0f;
        }
    }

    private static float TriangleArea(Vector3 a, Vector3 b, Vector3 c) => Vector3.Cross(b - a, c - a).Length() * 0.5f;

    /// <summary>A barn table (181297030's keys) back into the 300-byte record.</summary>
    public static LightShape Shape(KVObject t)
    {
        var r = new LightShape();
        void Put(string key, int offset)
        {
            if (t[key] is not { } v)
                return;
            var f = BakeScene.Floats(v);
            for (var i = 0; i < f.Length; i++)
                BitConverter.TryWriteBytes(r.Raw.AsSpan(offset + i * 4), f[i]);
        }
        Put("m_mToClipSpace", 0);
        Put("m_mFromClipSpace", 0x40);
        Put("m_vNearPlane", 0x80);
        Put("m_vLightPosition", 0x90);
        Put("m_vInnerRadius", 0xa0);
        if (BakeScene.Opt(t, "m_flShape") is { } shape)
            BitConverter.TryWriteBytes(r.Raw.AsSpan(0xa8), (float)shape.ToDouble(null));
        Put("m_vSkirt", 0xac);
        Put("m_vColor", 0xb4);
        if (BakeScene.Opt(t, "m_flLightColorToRadiance") is { } ctr)
            BitConverter.TryWriteBytes(r.Raw.AsSpan(0xc0), (float)ctr.ToDouble(null));
        BitConverter.TryWriteBytes(r.Raw.AsSpan(0xc4), BakeScene.Opt(t, "m_nCookie") is { } ck ? (int)ck.ToInt64(null) : -1);
        Put("m_vDistanceFade", 0xc8);
        Put("m_qOrientation", 0xd0);
        Put("m_vAngleFade", 0xe0);
        var type = BakeScene.Opt(t, "m_nLuminaireType")?.ToString(null) ?? "PUNCTUAL";
        r.Type = (byte)Array.IndexOf(["PUNCTUAL", "QUAD", "DISK", "SPHERE", "TUBE", "TUBE_WITH_END_CAPS"], type);
        if (BakeScene.Opt(t, "m_bLightInsideFrustum") is { } inside && inside.ToBoolean(null))
            r.Flags |= 1;
        if (BakeScene.Opt(t, "m_bSphericalCookie") is { } sph && sph.ToBoolean(null))
            r.Flags |= 2;
        if (BakeScene.Opt(t, "m_Luminaire") is { ValueType: KVValueType.Collection } lum)
        {
            if (r.Type == 2)
            {
                var c = BakeScene.Floats(lum["m_vCenter"]);
                var tangents = lum["m_ScaledTangents"].Values.Select(BakeScene.Floats).ToList();
                var n = BakeScene.Floats(lum["m_vNormal"]);
                r.SetV3(0xf0, new Vector3(c[0], c[1], c[2]));
                r.SetV3(0xfc, new Vector3(tangents[0][0], tangents[0][1], tangents[0][2]));
                r.SetV3(0x108, new Vector3(tangents[1][0], tangents[1][1], tangents[1][2]));
                r.SetV3(0x114, new Vector3(n[0], n[1], n[2]));
            }
            else
                throw new NotSupportedException($"the {type} luminaire's table is not ported");
        }
        return r;
    }
}
