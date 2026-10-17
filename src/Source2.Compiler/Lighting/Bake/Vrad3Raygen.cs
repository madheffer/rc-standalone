using System.Numerics;
using Source2.Compiler.Lighting.Bake.Glsl;
using Source2.Compiler.Lighting.Bake.Glsl.Raygen;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// vrad3's own ray generation shader (rtx_vrad3_lightmap, AHD combo,
/// transpiled from its GLSL by tools/re/glsl_to_cs.py) run on the CPU: the
/// host fills its buffers from the bake scene and answers traceRayEXT with the
/// BVH, the opacity any-hit and the hit groups (BakeHits, with RtxScene's
/// material hit groups when present); <see cref="RaygenHost"/> has the parts
/// every raygen shares. g_LightmapSamples and g_Luxels are per block,
/// g_RandomSamples the pass's Halton volume, g_LuxelChartIDs the block's chart
/// ids, nothing converged.
/// One instance per thread; the buffer arrays are shared.
/// </summary>
public sealed class Vrad3Raygen : RtxVrad3LightmapS0F0
{
    private readonly BakeScene _scene;
    private readonly BakeHits _hits;
    private float[] _random = [];
    private uint[] _ids = [];
    private int _w;

    public Vrad3Raygen(BakeScene scene, BakeHits hits) : this(scene, hits, RaygenHost.Instances(scene))
    {
    }

    private Vrad3Raygen(BakeScene scene, BakeHits hits, PackedRtxInstanceData_t_natural[] instances)
    {
        _scene = scene;
        _hits = hits;
        RandomSamplesSize = new ivec3(HaltonSamples.Size, HaltonSamples.Size, HaltonSamples.Layers);
        g_SceneGlobalInfo._member0 = [RaygenHost.Globals(scene)];
        g_Lights_1._member0 = [.. scene.Lights.Select(RaygenHost.GpuLight)];
        g_LightBins_1._member0 = RaygenHost.LightBins(scene);
        g_instanceBuffer_1._member0 = instances;
        SubsurfaceProfilesConstantBuffer_t = RaygenHost.NoSubsurface();
    }

    /// <summary>Shares another instance's buffers (one raygen per thread).</summary>
    public Vrad3Raygen(Vrad3Raygen other) : this(other._scene, other._hits, other.g_instanceBuffer_1._member0)
    {
        g_SceneGlobalInfo._member0 = other.g_SceneGlobalInfo._member0;
        g_Lights_1._member0 = other.g_Lights_1._member0;
        g_LightBins_1._member0 = other.g_LightBins_1._member0;
    }

    /// <summary>The block's luxel accumulators (Luxel_t, kept across passes).</summary>
    public Luxel_t_natural[] Luxels => g_Luxels._member0;

    public void Block(int w, int h, uint[] ids, Luxel_t_natural[] luxels)
    {
        _w = w;
        _ids = ids;
        LaunchSize = new uvec3((uint)w, (uint)h, 1u);
        g_Luxels._member0 = luxels;
    }

    public void Samples(LightmapSample_t_natural[] samples) => g_LightmapSamples_1._member0 = samples;

    public void Pass(int pass, int dims, float[] random, Vector2 blockOffset, Vector2 lightmapSize)
    {
        _random = random;
        g_PushConstants = new LightmapPushConstants_t_natural(new ivec4(pass, dims, 0, 0), new vec4(blockOffset.X, blockOffset.Y, lightmapSize.X, lightmapSize.Y),
            new vec4(-float.MaxValue), new vec4(float.MaxValue));
    }

    public void Run(int x, int y)
    {
        LaunchID = new uvec3((uint)x, (uint)y, 0u);
        _debug = Debug != null && x == DebugAt.X && y == DebugAt.Y;
        TrapLine = -1;
        Main();
        if (TrapLine >= 0 && Debug != null && Interlocked.Increment(ref _trapsShown) <= 20)
            Debug($"NaN first at GLSL line {TrapLine} (luxel {x},{y}, pass {g_PushConstants.m_nEstimate_DimensionCount.x})");
        var l = g_Luxels._member0[y * _w + x];
        if (float.IsNaN(l.vIrradiance_flSampleCount.x) || float.IsNaN(l.vShr.x))
            Interlocked.Increment(ref NanLuxels);
        if (_debug)
            Debug!($"luxel {x},{y} pass {g_PushConstants.m_nEstimate_DimensionCount.x}: irradiance {l.vIrradiance_flSampleCount} shr {l.vShr} ao {l.flAmbientOcclusion_vPadding} normal {l.vNormal}");
    }

    /// <summary>Checks: the luxels whose accumulators turned NaN, and one luxel's rays logged.</summary>
    public static int NanLuxels;
    public static Action<string>? Debug;
    public static (int X, int Y) DebugAt;
    private bool _debug;
    private static int _trapsShown;

    public static LightmapSample_t_natural Sample(in LightmapSample s) => new(V(s.Position), V(s.ShadingNormal), V(s.InterpolatedNormal), V(s.FaceNormal), V(s.Tangent),
        new vec2(s.LightmapUv.X, s.LightmapUv.Y), s.InstanceFlags, s.SampleFlags);

    /// <summary>The accumulator as the rest of the baker reads it.</summary>
    public static Luxel ToLuxel(in Luxel_t_natural l) => new()
    {
        Irradiance = W(l.vIrradiance_flSampleCount), ShR = W(l.vShr), ShG = W(l.vShg), ShB = W(l.vShb), QuadR = W(l.vQuadraticr), QuadG = W(l.vQuadraticg),
        QuadB = W(l.vQuadraticb), QuadFinal = W(l.vQuadraticFinalrgb), BakedShadow = W(l.vBakedShadow), BakedShadowUnoccluded = W(l.vBakedShadowUnoccluded),
        Id0 = l.vBakedShadowLightId.x, Id1 = l.vBakedShadowLightId.y, Id2 = l.vBakedShadowLightId.z, Id3 = l.vBakedShadowLightId.w,
        Tangent = W(l.vTangent), Normal = W(l.vNormal), Ao = W(l.flAmbientOcclusion_vPadding),
    };

    protected override void TraceRay(uint flags, uint mask, uint sbtOffset, uint sbtStride, uint missIndex, vec3 origin, float tMin, vec3 direction, float tMax, int payload) =>
        RaygenHost.Trace(_scene, _hits, ref p, ref p_1, flags, mask, origin, tMin, direction, tMax, payload, _debug ? Debug : null);

    protected override vec4 ImageLoadRandom(ivec3 c) => RaygenHost.Random(_random, c);

    protected override uvec4 ImageLoadConverged(ivec3 c) => default;

    protected override uvec4 TexelFetchChartIDs(ivec2 c)
    {
        if (c.x < 0 || c.y < 0 || c.x >= _w || c.y >= (int)LaunchSize.y)
            return new uvec4(0xffffffffu, 0u, 0u, 0u);
        return new uvec4(_ids[c.y * _w + c.x], 0u, 0u, 0u);
    }

    protected override vec4 TextureLod2D(int texture, int sampler, vec2 uv, float lod) => new(1f);

    protected override vec4 TextureLodCube(int texture, vec3 direction, float lod) => RaygenHost.IblCube(_scene, direction);

    private static vec4 V(Vector4 v) => RaygenHost.V(v);
    private static Vector4 W(vec4 v) => RaygenHost.W(v);
}
