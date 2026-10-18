using System.Numerics;
using Source2.Compiler.Lighting.Bake.Glsl.Raygen;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// Where <see cref="LightmapBaker"/> runs vrad3's ray generation shaders when not on the CPU:
/// per block, the sample validation (rtx_vrad3_lightmap_sample_validation) on each new sample set
/// and one rtx_vrad3_lightmap launch per pass, the luxel accumulators kept on the device until
/// the block ends. Source2.Compiler.Gpu's RtxBakeHost runs Valve's own SPIR-V.
/// </summary>
public interface ILightmapDevice
{
    /// <summary>Starts a w x h block: its chart ids (0xffffffff where no chart) and zeroed luxels.</summary>
    void BeginBlock(int w, int h, uint[] ids);

    /// <summary>A new sample set (one per luxel, y w + x), validated in place on the device.</summary>
    void Samples(LightmapSample_t_natural[] samples);

    /// <summary>One estimate per luxel: pass p with its Halton volume (HaltonSamples.Generate's float pairs).</summary>
    void Pass(int pass, int dims, float[] random, Vector2 blockOffset, Vector2 lightmapSize);

    /// <summary>
    /// Device-side sample generation in vrad3's order: <see cref="SmallChartSamples"/> the small
    /// charts' with Valve's compute program (or, when the device has none, <see cref="WriteSamples"/>
    /// the CPU's), <see cref="RasterSamples"/> the large charts' with Valve's raster programs (false
    /// when the device has none: then the CPU's stand), <see cref="ValidateSamples"/>. Both first
    /// passes write every luxel of the block: vrad3 starts each sample generation from an empty buffer.
    /// </summary>
    void WriteSamples(LightmapSample_t_natural[] samples, Func<int, bool> which);

    /// <summary>lightmap_generate_small_chart_samples over the block with the generation's two SFMT words; false when the device has no such pass.</summary>
    bool SmallChartSamples(BlockGeometry geometry, PackingGeometry packing, int ox, int oy, int lightmapWidth, int lightmapHeight, uint rngX, uint rngY);

    bool RasterSamples(BlockGeometry geometry, PackingGeometry packing, int ox, int oy, int lightmapWidth, int lightmapHeight, float jx, float jy);

    void ValidateSamples();

    /// <summary>The block's validated samples, read back (a check).</summary>
    LightmapSample_t_natural[] ReadSamples();

    /// <summary>Ends the block: its luxel accumulators.</summary>
    Luxel_t_natural[] EndBlock();
}
