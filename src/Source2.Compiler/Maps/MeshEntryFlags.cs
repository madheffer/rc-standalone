using System.Text;

namespace Source2.Compiler.Maps;

/// <summary>
/// WRB_MeshEntryFlags (180252ae0): a node mesh entry's attribute flags
/// (+0x1b0), representative texture size (+0x1b8) and skybox slot (+0x1c0)
/// from its material's attributes and the builder record it is made from.
/// Every read is "found and true" (IMaterial2 vf 0x48 for bools, 0x50 for
/// ints, 0x60 for strings). Checked against the binary run under an emulator
/// (tools/re/emu_entry_flags.py, MeshEntryFlagsEmulated).
/// </summary>
public static class MeshEntryFlags
{
    /// <summary>A material's attributes by lowercase MurmurHash2 key.</summary>
    public interface IAttributes
    {
        /// <summary>vf 0x48: the bool, or null when the material has no such attribute.</summary>
        bool? Bool(uint key);

        /// <summary>vf 0x50: the int, or null.</summary>
        int? Int(uint key);

        /// <summary>vf 0x60: the string, or null.</summary>
        string? String(uint key);

        /// <summary>vf 0xa8 (twice): the representative texture's width and height.</summary>
        (int Width, int Height) TextureSize { get; }
    }

    /// <summary>The builder record fields it reads.</summary>
    /// <param name="LightingMode">+0x1b0: 0 to 4.</param>
    /// <param name="BaseFlags">+0x1b8: flags the record already carries.</param>
    /// <param name="FadeMax">+0xb4.</param>
    /// <param name="Baked">+0x98: set, a lightmapped or per-vertex-lit mesh drops NeedsLightProbe.</param>
    /// <param name="LightingOrigin">+0x90: the lighting origin's name, or null.</param>
    /// <param name="PerVertexLighting">The mesh has a pervertexlighting stream.</param>
    public sealed record Record(byte LightingMode, ulong BaseFlags, float FadeMax, bool Baked, string? LightingOrigin, bool PerVertexLighting);

    /// <summary>The gameinfo switches it reads (csgo's values by default).</summary>
    public sealed record Settings(bool UseStaticLightProbes = true, bool UseStaticEnvMapForObjectsWithLightingOrigin = true, bool MergeTranslucents = false);

    public sealed record Result(ulong Flags, int Width, int Height, string SkyboxSlot);

    public static uint Key(string name) => Io.ResourceNames.Hash(Encoding.ASCII.GetBytes(name), name.Length, 0x31415926);

    public static Result Compute(IAttributes m, Record r, Settings? settings = null)
    {
        settings ??= new Settings();
        bool B(string name) => m.Bool(Key(name)) == true;
        var nodraw = B("mapbuilder.nodraw");
        var nonsolid = B("mapbuilder.nonsolid");
        var shadowsOnly = B("mapbuilder.blocklight") || B("ShadowsOnly");
        var mode = r.LightingMode;
        if (B("DoNotCastShadows"))
            mode = 1;
        var dynamicShadows = B("needsdynamicshadows");
        var doNotCollapse = B("mapbuilder.donotcollapse");
        var flag4 = m.Bool(0x531e4075) == true;
        var tools = B("tools.toolsmaterial");
        var forwardOnly = B("ForwardLayerOnly");
        doNotCollapse |= B("mapbuilder_donotcollapse");
        var lightmapRes = B("tools.lightmapres") | B("lightmapres");
        var steamAudio = B("tools.steamaudiogeometry");
        var lightmapping = B("SupportsLightmapping");
        var envMap = false;
        if (B("environmentmapped"))
        {
            var dynamic = B("environmentmappeddynamic");
            envMap = !dynamic || (settings.UseStaticEnvMapForObjectsWithLightingOrigin && !string.IsNullOrEmpty(r.LightingOrigin));
        }
        var fullMaterial = lightmapping && B("LightmapperNeedsFullMaterial");
        var probe = B("NeedsLightProbe");
        if (r.Baked && (lightmapping || r.PerVertexLighting))
            probe = false;
        var visBlocker = B("mapbuilder.visblocker");
        var acceptVis = B("mapbuilder.acceptvis");
        var translucent = B("translucent");
        var blended = B("bShaderBlended");
        var copy = B("bWantsFBCopyTexture");
        var alphaTest = B("alphatest");
        var overlay = B("overlay");
        var sky = B("mapbuilder.sky");
        var worldSpace = B("WorldSpaceTextureCoords");
        var noPrepass = B("NoZPrepass");
        var skybox = m.String(Key("skyboxslot")) ?? "";
        var twoSided = B("DoubleSided") || B("renderbackfaces");
        var lightingDummy = B("mapbuilder.lightingdummy");
        var cull = B("AllowBackfaceCulling") && !twoSided;
        nodraw |= (m.Int(Key("mapbuilder.occluder")) ?? 0) != 0;
        var aggregate = B("SupportsAggregateInstancing");
        var aggregateStream = B("SupportsAggregateInstanceStream");
        var noRaytrace = B("DisableRuntimeRaytraceGeometry");
        var (width, height) = m.TextureSize;

        var f = r.BaseFlags;
        if (nodraw)
            f |= acceptVis ? 0x21UL : 1UL;
        if (nonsolid)
            f |= 0x200;
        if (lightmapRes)
            f |= 0x30603;
        if (steamAudio)
            f |= 0x1000030203;
        if (shadowsOnly)
            f |= 8;
        if (tools)
            f |= 0x800;
        if (mode is 1 or 3)
            f |= 0x20000;
        if (mode is 1 or 2)
            f |= 0x10000;
        if (mode == 4)
            f |= 0x40000;
        if (dynamicShadows)
            f |= 0x80000;
        if (fullMaterial)
            f |= 0x200000000;
        if (twoSided)
            f |= 0x4000000;
        if (overlay)
            f |= 0x1000;
        if (doNotCollapse || 0f < r.FadeMax || (translucent && !overlay && !settings.MergeTranslucents))
            f |= 2;
        if (flag4)
            f |= 4;
        if (visBlocker)
            f |= 0x10;
        if (settings.UseStaticLightProbes)
        {
            if (probe)
                f |= 0x100000;
            if (envMap)
                f |= 0x200002;
        }
        if (translucent)
            f |= 0x800000;
        if (blended)
            f |= 0x1000000;
        if (copy)
            f |= 0x8000000;
        if (alphaTest)
            f |= 0x2000000;
        if (sky)
            f |= 0x100;
        if (worldSpace)
            f |= 0x4000;
        if (skybox.Length > 0)
            f |= 0x80;
        if (noPrepass)
            f |= 0x400000;
        if (lightmapping)
            f |= 0x20000000;
        if (forwardOnly)
            f |= 0x10000000;
        if (aggregate)
            f |= aggregateStream ? 0xc0000000UL : 0x40000000UL;
        if (cull)
            f |= 0x100000000;
        if (lightingDummy)
            f |= 0x400000000;
        if (noRaytrace)
            f |= 0x800000000;
        return new Result(f, width, height, skybox);
    }
}
