using Source2.Compiler.Lighting.Bake.Glsl;
using Source2.Compiler.Lighting.Bake.Glsl.Raygen;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// vrad3's sample validation (rtx_vrad3_lightmap_sample_validation, its ray
/// generation transpiled by tools/re/glsl_to_cs.py) run on the CPU over a
/// block's samples, in place: one launch per luxel of the block, the sample at
/// y * w + x; traceRayEXT answered by <see cref="RaygenHost.Trace"/> with the
/// bake's hit groups. It replaces LightmapBaker's hand port, which kept
/// samples Valve drops (ze_hold_em_p's chart 2074).
/// One instance per thread; the buffers are shared.
/// </summary>
public sealed class SampleValidationRaygen : RtxVrad3LightmapSampleValidationS0F0
{
    private readonly BakeScene _scene;
    private readonly BakeHits _hits;

    public SampleValidationRaygen(BakeScene scene, BakeHits hits, PackedRtxInstanceData_t_natural[] instances)
    {
        _scene = scene;
        _hits = hits;
        g_instanceBuffer._member0 = instances;
    }

    /// <summary>Validates every sample of a w x h block in place.</summary>
    public static void Run(BakeScene scene, BakeHits hits, LightmapSample_t_natural[] samples, int w, int h, int threads)
    {
        var instances = RaygenHost.Instances(scene);
        using var raygens = new ThreadLocal<SampleValidationRaygen>(() =>
        {
            var r = new SampleValidationRaygen(scene, hits, instances) { LaunchSize = new uvec3((uint)w, (uint)h, 1u) };
            r.g_LightmapSamples._member0 = samples;
            return r;
        });
        Parallel.For(0, h, new ParallelOptions { MaxDegreeOfParallelism = threads }, y =>
        {
            var r = raygens.Value!;
            for (var x = 0; x < w; x++)
            {
                r.LaunchID = new uvec3((uint)x, (uint)y, 0u);
                r.Main();
            }
        });
    }

    protected override void TraceRay(uint flags, uint mask, uint sbtOffset, uint sbtStride, uint missIndex, vec3 origin, float tMin, vec3 direction, float tMax, int payload) =>
        RaygenHost.Trace(_scene, _hits, ref p, ref p_1, flags, mask, origin, tMin, direction, tMax, payload);

    protected override vec4 ImageLoadRandom(ivec3 c) => throw new NotSupportedException("the sample validation reads no random samples");

    protected override uvec4 ImageLoadConverged(ivec3 c) => throw new NotSupportedException("the sample validation reads no convergence texture");

    protected override uvec4 TexelFetchChartIDs(ivec2 c) => throw new NotSupportedException("the sample validation reads no chart ids");

    protected override vec4 TextureLod2D(int texture, int sampler, vec2 uv, float lod) => throw new NotSupportedException("the sample validation samples no texture");

    protected override vec4 TextureLodCube(int texture, vec3 direction, float lod) => throw new NotSupportedException("the sample validation samples no cubemap");
}
