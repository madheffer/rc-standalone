using System.Numerics;

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
        bool ScriptedCamera);

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

    /// <summary>
    /// PerViewConstantBufferCsgo_t's minimum specular lightmap size (4096 in every capture; the writer is not read).
    /// </summary>
    public const float MinSpecularLightmapSize = 4096f;

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
            CubemapFaceConstants.Csgo(view, scene.Fog, MinSpecularLightmapSize),
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
