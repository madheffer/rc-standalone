using System.Numerics;
using Source2.Compiler.Maps;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// A material as the bake's hit groups see it. The flags and instance masks
/// are scenesystem's (180098660) from CMaterial2's attributes:
/// <list type="bullet">
/// <item><see cref="Flags"/>: 1 doublesided or renderbackfaces, 2
/// Translucent or alphatest, 5 mapbuilder.blocklight;</item>
/// <item><see cref="MaskAnd"/>: 1 for tools.toolsmaterial, 0xe (with that,
/// 0xf) for DoNotCastShadows, 0x10 for a translucent or alpha-tested
/// material; <see cref="MaskOr"/>: 0x10 for an opaque one. Attributes
/// 0x98bed7f6 (or 0x20, and 0x11) and 0x16458f13 (or 0x400) are not named
/// and taken as unset.</item>
/// </list>
/// The surface is the default hit group's (rtx_default_hitgroup): the colour
/// texture at mip 0, bilinear, times the instance tint (a layered shader's
/// first layer when it has no g_tColor); roughness 1,
/// metalness 0, reflectance 0.04, no emission; a translucent or alpha-tested
/// material's opacity is the texture's alpha (S_TRANSLUCENT). The csgo
/// shaders' own hit groups (csgo_environment_vulkan_60_rtx and the rest) are
/// not ported.
/// </summary>
public sealed class BakeMaterial
{
    public string Name { get; init; } = "";
    public uint Flags { get; init; }
    public uint MaskAnd { get; init; }
    public uint MaskOr { get; init; } = 0x10;
    public BakeTexture? Color { get; init; }
    public Vector3 ColorTint { get; init; } = Vector3.One;
    public float Opacity { get; init; } = 1f;

    /// <summary>Translucent or alpha tested: the instance's candidates go through the any-hit test.</summary>
    public bool NeedsAnyHit => (Flags & 2) != 0;

    public static readonly BakeMaterial Default = new() { Name = "" };

    private static readonly string[] ColorKeys = ["g_tColor", "g_tColor1", "g_tColorA"];

    /// <summary>The material from its attributes and parameters.</summary>
    public static BakeMaterial From(string name, MeshEntryFlags.IAttributes a, SettleWorld.MaterialInfo? info, Func<string, BakeTexture?> texture)
    {
        bool B(string key) => a.Bool(MeshEntryFlags.Key(key)) == true;
        var flags = 0u;
        if (B("doublesided") || B("renderbackfaces"))
            flags |= 1;
        var translucent = B("translucent") || B("alphatest");
        if (translucent)
            flags |= 2;
        if (B("mapbuilder.blocklight"))
            flags |= 5;
        var tools = a.Int(MeshEntryFlags.Key("tools.toolsmaterial")) is { } t && t != 0;
        var and = tools ? 1u : 0u;
        if (B("donotcastshadows"))
            and = (tools ? 1u : 0u) + 0xe;
        var or = 0u;
        if (!translucent)
            or |= 0x10;
        else
            and |= 0x10;
        BakeTexture? color = null;
        var tint = Vector3.One;
        if (info != null)
        {
            // The default hit group's colour; until the csgo hit groups are ported, a layered shader's first layer
            // (csgo_environment's g_tColor1, csgo_simple_2way_blend's g_tColorA) stands in.
            foreach (var key in ColorKeys)
                if (info.Textures != null && info.Textures.TryGetValue(key, out var path))
                {
                    color = texture(path);
                    break;
                }
            if (info.Vectors != null && info.Vectors.TryGetValue("g_vColorTint", out var v))
                tint = new Vector3(v.X, v.Y, v.Z);
        }
        return new BakeMaterial { Name = name, Flags = flags, MaskAnd = and, MaskOr = or, Color = color, ColorTint = tint };
    }
}

/// <summary>A texture's mip 0 as linear floats, sampled bilinearly with wrap (textureLod at 0).</summary>
public sealed class BakeTexture(int width, int height, Vector4[] texels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;

    public Vector4 Sample(Vector2 uv)
    {
        var x = uv.X * Width - 0.5f;
        var y = uv.Y * Height - 0.5f;
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        Vector4 T(int i, int j) => texels[Mod(j, Height) * Width + Mod(i, Width)];
        return Vector4.Lerp(Vector4.Lerp(T(x0, y0), T(x0 + 1, y0), fx), Vector4.Lerp(T(x0, y0 + 1), T(x0 + 1, y0 + 1), fx), fy);
    }

    private static int Mod(int a, int n) => ((a % n) + n) % n;

    /// <summary>8-bit RGBA texels, the colour through the sRGB curve when the texture is sRGB.</summary>
    public static BakeTexture FromRgba8(int width, int height, ReadOnlySpan<byte> rgba, bool srgb)
    {
        var t = new Vector4[width * height];
        for (var i = 0; i < t.Length; i++)
        {
            float C(byte b) => srgb ? SrgbToLinear(b / 255f) : b / 255f;
            t[i] = new Vector4(C(rgba[i * 4]), C(rgba[i * 4 + 1]), C(rgba[i * 4 + 2]), rgba[i * 4 + 3] / 255f);
        }
        return new BakeTexture(width, height, t);
    }

    public static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
}

/// <summary>A cubemap's mip 0 faces (+x, -x, +y, -y, +z, -z), sampled bilinearly within a face.</summary>
public sealed class BakeCubemap(BakeTexture[] faces)
{
    public Vector4 Sample(Vector3 d)
    {
        var a = Vector3.Abs(d);
        int face;
        float sc, tc, ma;
        if (a.X >= a.Y && a.X >= a.Z)
        {
            ma = a.X;
            (face, sc, tc) = d.X > 0 ? (0, -d.Z, -d.Y) : (1, d.Z, -d.Y);
        }
        else if (a.Y >= a.Z)
        {
            ma = a.Y;
            (face, sc, tc) = d.Y > 0 ? (2, d.X, d.Z) : (3, d.X, -d.Z);
        }
        else
        {
            ma = a.Z;
            (face, sc, tc) = d.Z > 0 ? (4, d.X, -d.Y) : (5, -d.X, -d.Y);
        }
        return faces[face].Sample(new Vector2((sc / ma + 1f) * 0.5f, (tc / ma + 1f) * 0.5f));
    }
}
