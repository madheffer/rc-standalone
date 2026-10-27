using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Cubemaps;

/// <summary>
/// Every constant block and CPU-written buffer one BuildCubemaps face reads, by role (docs/CUBEMAPS.md "Whole cubes
/// from our blocks"; ledgers 147-154): the face's and the sky's PerViewConstantBuffer_t, PerViewConstantBufferCsgo_t,
/// BakedLightingConstantBuffer_t, the four cascades' per-view blocks, PerViewLightingConstantBufferGpu_t, the light
/// binner's CullParams_t, item records and edge lines, and the sky's g_matSkyWsToOs. Each is word for word the game's
/// on every face of probe01's two cubes and s2c_l5probe's cube 0 (CubemapFaceReplayTests).
/// </summary>
public static class CubemapFaceBlocks
{
    /// <summary>
    /// What the blocks take from the map: the cube's origin, the light probe volumes, env_cubemap_fog, the
    /// light_environment (its angles, colour times brightness, the sky colour and intensity, fades), the world's
    /// lightmap uv scale and size, env_sky's angles and origin. <paramref name="ScriptedCamera"/> is whether the
    /// build saw a scripted camera (UI state, ledger 149: cascade 0's distance).
    /// </summary>
    public sealed record Scene(
        Vector3 Origin,
        IReadOnlyList<CubemapFaceConstants.LightProbeVolume> Volumes,
        CubemapFaceConstants.CubeFog? Fog,
        Vector3 SunAngles,
        Vector3 SunColour,
        float SunCrossFade,
        float SunDistanceFade,
        Vector3 SkyColourLinear,
        float SkyIntensity,
        Vector2 LightmapUvScale,
        int LightmapWidth,
        int LightmapHeight,
        Vector3 SkyAngles,
        Vector3 SkyOrigin,
        bool ScriptedCamera,
        float MinSpecLightmapSize = 4096f);

    /// <summary>One face's blocks.</summary>
    public sealed record Face(
        CubemapFaceViews.View View,
        byte[] WorldView,
        byte[] SkyView,
        byte[] Csgo,
        byte[] Baked,
        byte[][] CascadeViews,
        byte[] Lights,
        byte[] BinnerParams,
        byte[] BinnerRecords,
        byte[] BinnerEdges,
        byte[] SkyWsToOs,
        int BinnerMaskWords);

    /// <summary>The view's near and far planes (the buildcubemaps record's, client 1801ea0a0; ledger 118).</summary>
    public const float Near = 1f, Far = 10000f;

    /// <summary>
    /// The light binner's lb_tile_pixels and lb_bin_slices defaults (scenesystem 1801a8150, as
    /// <see cref="CubemapFaceConstants.LightBinnerCullParams"/> takes them).
    /// </summary>
    public const int BinnerTilePixels = 8, BinnerBinSlices = 8192;

    /// <summary>One cube of the map: its scene and the record's handshake (its EXR is env_cubemap_&lt;handshake&gt;.exr).</summary>
    public sealed record Cube(Scene Scene, uint Handshake, string Class);

    /// <summary>
    /// The map's cubes from its compiled package and gameinfo.gi, in client 1801ea0a0's gather order (env_cubemap,
    /// then env_cubemap_box and env_combined_light_probe_volume, each class in lump order):
    /// <list type="bullet">
    /// <item>light probe volumes: every env_combined_light_probe_volume's origin, box_mins / box_maxs, edge_fade_dists,
    /// array_index and indoor_outdoor_level (the priority);</item>
    /// <item>env_cubemap_fog: cubemapfogstartdistance, cubemapfogenddistance, cubemapfoglodbiase,
    /// cubemapfogfalloffexponent, cubemapfogheightstart / exponent and cubemapfogmaxopacity (C_EnvCubemapFog's schema,
    /// client 1801fd3c0); the matrix is the entity's translation;</item>
    /// <item>light_environment: angles, color x brightness (linear), cascadecrossfade, cascadedistancefade,
    /// skycolor (linear) and skyintensity;</item>
    /// <item>env_sky: angles, origin and skyname (the material);</item>
    /// <item>world.vwrld_c's m_worldLightingInfo m_vLightmapUvScale, the lightmaps' size (lightmaps/irradiance), and
    /// gameinfo's BakedLighting MinSpecLightmapSize (client 181206bfe).</item>
    /// </list>
    /// What is not read throws: rotated volumes or fog, fog with a height range (its struct fill is not read), a
    /// tinted sky, func_ and plain env_light_probe_volume, more than one fog, sun or sky.
    /// </summary>
    public static List<Cube> FromPackage(ValvePak.Package package, string gameinfo, bool scriptedCamera, out string skyMaterial)
    {
        var entities = new List<(string Class, Maps.LightPrecompute.KeyReader Key)>();
        foreach (var entry in (package.Entries!.GetValueOrDefault("vents_c") ?? []).OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            package.ReadEntry(entry, out var bytes);
            entities.AddRange(Lighting.Vrad3Job.LumpEntities(bytes, entry.GetFullPath()));
        }
        static float F(string? text) => float.Parse(text ?? throw new InvalidDataException("a key is missing"), CultureInfo.InvariantCulture);
        static Vector3 V(string? text) => (text ?? throw new InvalidDataException("a key is missing")).Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var x, var y, var z]
            ? new Vector3(F(x), F(y), F(z)) : throw new InvalidDataException($"not a vector: {text}");
        static Vector3 Colour(string? text) => V(text) is var c ? Lighting.SceneLights.SrgbToLinear((int)c.X, (int)c.Y, (int)c.Z) : default;
        Maps.LightPrecompute.KeyReader? One(string cls)
        {
            var all = entities.Where(e => e.Class == cls).ToList();
            return all.Count switch { 0 => null, 1 => all[0].Key, _ => throw new NotSupportedException($"{all.Count} {cls}: which one the view uses is not read") };
        }
        foreach (var unread in new[] { "func_combined_light_probe_volume", "env_light_probe_volume" })
            if (entities.Any(e => e.Class == unread))
                throw new NotSupportedException($"{unread} in the light block is not read");
        var volumes = new List<CubemapFaceConstants.LightProbeVolume>();
        foreach (var (_, key) in entities.Where(e => e.Class == "env_combined_light_probe_volume"))
        {
            if (V(key("angles")) != Vector3.Zero)
                throw new NotSupportedException("a rotated light probe volume");
            volumes.Add(new(V(key("origin")), V(key("box_mins")), V(key("box_maxs")), V(key("edge_fade_dists")),
                int.Parse(key("array_index") ?? "0", CultureInfo.InvariantCulture), int.Parse(key("indoor_outdoor_level") ?? "0", CultureInfo.InvariantCulture)));
        }
        CubemapFaceConstants.CubeFog? fog = null;
        if (One("env_cubemap_fog") is { } f)
        {
            if (V(f("angles")) != Vector3.Zero)
                throw new NotSupportedException("a rotated env_cubemap_fog");
            var range = f("cubemapheightfog") is "1" && F(f("cubemapfogheightstart")) != F(f("cubemapfogheightend"));
            if (range || f("cubemapfogheightwidth") is { } w && F(w) != 0f)
                throw new NotSupportedException("cube height fog with a height range: the fog struct's fill is not read");
            fog = new CubemapFaceConstants.CubeFog(F(f("cubemapfogstartdistance")), F(f("cubemapfogenddistance")), F(f("cubemapfoglodbiase")),
                F(f("cubemapfogfalloffexponent")), new Vector4(0f, F(f("cubemapfogheightstart")), F(f("cubemapfogheightexponent")), F(f("cubemapfogmaxopacity"))),
                Matrix4x4.CreateTranslation(V(f("origin"))));
        }
        var sun = One("light_environment") ?? throw new NotSupportedException("no light_environment: the sunless light block is not read");
        var sky = One("env_sky") ?? throw new NotSupportedException("no env_sky");
        if (sky("tint_color") is { } tint && V(tint) != new Vector3(255, 255, 255) || sky("brightnessscale") is { } scale && F(scale) != 1f)
            throw new NotSupportedException("a tinted env_sky: SkyTint from the entity is not read");
        skyMaterial = sky("skyname") ?? throw new InvalidDataException("env_sky has no skyname");
        using var worldResource = Read(package, package.Entries!.GetValueOrDefault("vwrld_c")!.Single());
        var uvScale = ((World)worldResource.DataBlock!).Data.GetSubCollection("m_worldLightingInfo").GetSubCollection("m_vLightmapUvScale").ToVector2();
        var irradiance = (package.Entries!.GetValueOrDefault("vtex_c") ?? []).Single(e => e.DirectoryName.EndsWith("lightmaps", StringComparison.Ordinal) && e.FileName == "irradiance");
        using var lightmap = Read(package, irradiance);
        var tex = (Texture)lightmap.DataBlock!;
        var minSpec = Regex.Match(gameinfo, @"BakedLighting\s*\{[^}]*?MinSpecLightmapSize\s+(\d+)") is { Success: true } m
            ? float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0f;
        var cubes = new List<Cube>();
        foreach (var cls in new[] { "env_cubemap", "env_cubemap_box", "env_combined_light_probe_volume" })
            foreach (var (_, key) in entities.Where(e => e.Class == cls))
                cubes.Add(new Cube(new Scene(V(key("origin")), volumes, fog, V(sun("angles")), Colour(sun("color")) * F(sun("brightness")),
                    F(sun("cascadecrossfade")), F(sun("cascadedistancefade")), Colour(sun("skycolor")), F(sun("skyintensity")),
                    uvScale, tex.Width, tex.Height, V(sky("angles")), V(sky("origin")), scriptedCamera, minSpec),
                    uint.Parse(key("handshake") ?? throw new InvalidDataException($"{cls} has no handshake"), CultureInfo.InvariantCulture), cls));
        return cubes;
    }

    private static Resource Read(ValvePak.Package package, ValvePak.PackageEntry entry)
    {
        package.ReadEntry(entry, out var bytes);
        var resource = new Resource { FileName = entry.GetFullPath() };
        resource.Read(new MemoryStream(bytes));
        return resource;
    }

    /// <summary>Face <paramref name="face"/> (0 to 5: +X, -X, +Y, -Y, +Z, -Z) of the cube at the scene's origin.</summary>
    public static Face Build(Scene scene, int face)
    {
        var view = CubemapFaceViews.Face(scene.Origin, Vector3.Zero, Near, Far, face);
        var frustum = AggregateCulling.FaceFrustum(view);
        var volumes = CubemapFaceConstants.VisibleVolumes(frustum, scene.Volumes);
        var (toSun, lightDirection, cameraDirection) = CubemapShadowCascades.SunDirections(scene.SunAngles);
        var cascades = CubemapShadowCascades.GameCascades(view, toSun, Near, Far, scene.ScriptedCamera);
        var tiles = CubemapFaceDrawList.CascadeTiles.Select(t => new[] { t.X, t.Y }).ToArray();
        var cascadeViews = Enumerable.Range(0, 4).Select(k => CubemapFaceConstants.CascadePerView(cascades[k], cameraDirection, tiles[k][0], tiles[k][1],
            CubemapShadowCascades.Inputs.Resolution, CubemapShadowCascades.Inputs.AtlasWidth, CubemapShadowCascades.Inputs.AtlasHeight)).ToArray();
        var sun = new CubemapFaceConstants.Sun(lightDirection, scene.SunColour, scene.SunCrossFade, scene.SunDistanceFade, scene.SkyColourLinear, scene.SkyIntensity);
        var stretched = CubemapShadowCascades.Slice(view, Near, CubemapShadowCascades.GameDistances(Near, Far, scene.ScriptedCamera)[^1]).Diameter * 1.42f;
        // The binner's items: no lights or environment maps are binned in the cubemap view, the visible volumes are.
        var bin = new LightBinnerItems.View(view.Origin, new Vector3(view.CameraToWorld[0], view.CameraToWorld[4], view.CameraToWorld[8]),
            AggregateCulling.WorldToProjection(view), CubemapFaceDrawList.FaceSize, CubemapFaceDrawList.FaceSize, BinnerTilePixels);
        var footprints = volumes.Select(v => LightBinnerItems.Bounds(bin, v.BoxMins, v.BoxMaxs, [1f, 0f, 0f, v.Origin.X, 0f, 1f, 0f, v.Origin.Y, 0f, 0f, 1f, v.Origin.Z])
            ?? throw new InvalidOperationException("a visible volume rejected by the binner")).ToList();
        var (records, edges) = LightBinnerItems.Pack(bin, footprints);
        static int Words(int count) => count != 0 ? (count + 31) / 32 : 0;
        return new Face(
            view,
            CubemapFaceConstants.PerView(view, CubemapFaceDrawList.FaceSize, 0f, 0.95f),
            CubemapFaceConstants.PerView(view, CubemapFaceDrawList.FaceSize, 1f, 1f),
            CubemapFaceConstants.Csgo(view, scene.Fog, scene.MinSpecLightmapSize),
            CubemapFaceConstants.BakedLighting(scene.LightmapUvScale, scene.LightmapWidth, scene.LightmapHeight, false),
            cascadeViews,
            CubemapFaceConstants.LightBlock(sun, volumes, cascades, tiles, CubemapShadowCascades.Inputs.Resolution, CubemapShadowCascades.Inputs.AtlasWidth,
                CubemapShadowCascades.Inputs.AtlasHeight, scene.Origin, stretched),
            CubemapFaceConstants.LightBinnerCullParams(CubemapFaceDrawList.FaceSize, CubemapFaceDrawList.FaceSize, Far, 0, 0, volumes.Count, BinnerTilePixels, BinnerBinSlices),
            records,
            edges,
            CubemapFaceConstants.SkyWsToOs(CubemapFaceViews.AngleMatrix(scene.SkyAngles, scene.SkyOrigin)),
            Words(0) + Words(0) + Words(volumes.Count));
    }
}
