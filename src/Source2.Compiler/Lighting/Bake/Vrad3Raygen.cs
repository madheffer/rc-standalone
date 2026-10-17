using System.Numerics;
using Source2.Compiler.Lighting.Bake.Glsl;
using Source2.Compiler.Lighting.Bake.Glsl.Raygen;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// vrad3's own ray generation shader (rtx_vrad3_lightmap, AHD combo,
/// transpiled from its GLSL by tools/re/glsl_to_cs.py) run on the CPU: the
/// host fills its buffers from the bake scene and answers traceRayEXT with the
/// BVH, the opacity any-hit and the hit groups (BakeHits, with RtxScene's
/// material hit groups when present).
/// <list type="bullet">
/// <item>g_SceneGlobalInfo from <see cref="BakeScene.Globals"/>; the light bins
/// one cell holding every light (vrad3's binning is conservative, so one cell
/// with all of them changes no light that reaches a point).</item>
/// <item>g_Lights as PathTraceBarnLightGPU_t (scenesystem 18008c110's
/// fields from <see cref="Light"/>); g_instanceBuffer from RtxScene.</item>
/// <item>g_LightmapSamples and g_Luxels per block, g_RandomSamples the pass's
/// Halton volume, g_LuxelChartIDs the block's chart ids, nothing converged.</item>
/// <item>A miss leaves the payload as the shader initialised it (vrad3 has no
/// miss shader that writes it); shadow rays (payload 1) multiply the
/// visibility by one minus each translucent candidate's opacity, and any
/// opaque hit makes it 0.</item>
/// </list>
/// One instance per thread; the buffer arrays are shared.
/// </summary>
public sealed class Vrad3Raygen : RtxVrad3LightmapS0F0
{
    private readonly BakeScene _scene;
    private readonly BakeHits _hits;
    private float[] _random = [];
    private uint[] _ids = [];
    private int _w;

    public Vrad3Raygen(BakeScene scene, BakeHits hits) : this(scene, hits, [.. scene.Instances.Select(i => new PackedRtxInstanceData_t_natural
    {
        nSrgbColor = i.SrgbColor, nTriangleGeometry = (uint)i.Mesh, nMaterialFlags_nInstanceFlags = i.Packed, fEmissiveBoost = i.EmissiveBoost, nInstanceFlags = i.InstanceFlags,
    })])
    {
    }

    private Vrad3Raygen(BakeScene scene, BakeHits hits, PackedRtxInstanceData_t_natural[] instances)
    {
        _scene = scene;
        _hits = hits;
        RandomSamplesSize = new ivec3(HaltonSamples.Size, HaltonSamples.Size, HaltonSamples.Layers);
        g_SceneGlobalInfo._member0 = [Globals(scene)];
        g_Lights_1._member0 = [.. scene.Lights.Select(GpuLight)];
        var words = Math.Max(1, (scene.Lights.Count + 31) / 32);
        g_LightBins_1._member0 = new uint[words];
        for (var i = 0; i < scene.Lights.Count; i++)
            g_LightBins_1._member0[i / 32] |= 1u << (i % 32);
        g_instanceBuffer_1._member0 = instances;
        SubsurfaceProfilesConstantBuffer_t = new()
        {
            g_vSubsurfaceSamples = new _Array_natural_vector_float_4_480 { data = new vec4[480] },
            g_SubsurfaceProfile = new _Array_natural_SSProfileConstants32 { data = new SSProfileConstants_natural[32] },
        };
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

    protected override void TraceRay(uint flags, uint mask, uint sbtOffset, uint sbtStride, uint missIndex, vec3 origin, float tMin, vec3 direction, float tMax, int payload)
    {
        var o = new Vector3(origin.x, origin.y, origin.z);
        var d = new Vector3(direction.x, direction.y, direction.z);
        if (payload == 1)
        {
            var visibility = p_1.m_flVisibility;
            var hit = _scene.Rays.Trace(o, d, tMin, tMax, flags | RayScene.TerminateOnFirstHit, mask, (tri, t, u, v, front) =>
            {
                visibility *= 1f - Math.Clamp(_hits.Opacity(tri, u, v), 0f, 1f);
                return visibility <= 0f;
            });
            p_1.m_flVisibility = hit.Found ? 0f : visibility;
            return;
        }
        var rng = p.flOpacityRng;
        var found = _scene.Rays.Trace(o, d, tMin, tMax, flags, mask, (tri, t, u, v, front) => BakeHits.StochasticOpacity(_hits.Opacity(tri, u, v), ref rng));
        p.flOpacityRng = rng;
        if (_debug)
            Debug!($"  trace flags {flags} mask {mask} o {origin} d {direction} t {tMin}..{tMax}: {(found.Found ? $"hit t {found.T} tri {found.Triangle} front {found.FrontFacing}" : "miss")}");
        if (!found.Found)
            return;
        var ours = new Payload
        {
            SmoothPosition = P(p.vSmoothPositionWs), HitT = p.flHitT, OpacityRng = rng, Instance = p.nInstance, Flipped = p.bFlippedNormal,
            Albedo = new Vector3(1f, 0f, 1f), Roughness = 1f, Reflectance = 0.040008545f,
        };
        _hits.Shade(ref ours, found);
        p = new Payload_t(Vh(ours.SmoothPosition), Vh(ours.FaceNormal), Vh(ours.InterpolatedNormal), ours.HitT, ours.OpacityRng, ours.Instance, ours.Flipped,
            new MaterialDataMinimal_t(GL.f16vec3(Vh(ours.Normal)), GL.f16(ours.Metalness), GL.f16vec3(Vh(ours.Albedo)), GL.f16(ours.Roughness), GL.f16vec3(Vh(ours.Emissive)),
                GL.f16(ours.Reflectance), ours.MaterialTypeAndFlags, GL.f16vec4(new vec4(ours.MaterialBits.X, ours.MaterialBits.Y, ours.MaterialBits.Z, ours.MaterialBits.W))));
        if (_debug)
            Debug!($"    payload smooth {p.vSmoothPositionWs} face {p.vFaceNormalWs} interp {p.vInterpolatedNormalWs} inst {p.nInstance} normal {p.material.vNormalWs} albedo {p.material.vAlbedo} rough {p.material.flRoughness}");
    }

    protected override vec4 ImageLoadRandom(ivec3 c)
    {
        var x = ((c.x % HaltonSamples.Size) + HaltonSamples.Size) % HaltonSamples.Size;
        var y = ((c.y % HaltonSamples.Size) + HaltonSamples.Size) % HaltonSamples.Size;
        var z = Math.Clamp(c.z, 0, HaltonSamples.Layers - 1);
        var at = ((z * HaltonSamples.Size + y) * HaltonSamples.Size + x) * 2;
        return new vec4(_random[at], _random[at + 1], 0f, 1f);
    }

    protected override uvec4 ImageLoadConverged(ivec3 c) => default;

    protected override uvec4 TexelFetchChartIDs(ivec2 c)
    {
        if (c.x < 0 || c.y < 0 || c.x >= _w || c.y >= (int)LaunchSize.y)
            return new uvec4(0xffffffffu, 0u, 0u, 0u);
        return new uvec4(_ids[c.y * _w + c.x], 0u, 0u, 0u);
    }

    protected override vec4 TextureLod2D(int texture, int sampler, vec2 uv, float lod) => new(1f);

    protected override vec4 TextureLodCube(int texture, vec3 direction, float lod)
    {
        if (_scene.Global.IblCubeMap is not { } cube)
            return default;
        var c = cube.Sample(new Vector3(direction.x, direction.y, direction.z));
        return new vec4(c.X, c.Y, c.Z, c.W);
    }

    private static SceneGlobalInfoGPU_t_natural Globals(BakeScene scene)
    {
        var g = scene.Global;
        var m = g.IblTransformInverse;
        return new SceneGlobalInfoGPU_t_natural(V(g.SkyColor), V(g.SkyAmbientBounce), V(g.SunLightIrradiance), new vec4(-1e9f), new vec4(1e9f),
            new ivec4(1, 1, 1, scene.Lights.Count),
            new mat4x3(new vec3(m.M11, m.M12, m.M13), new vec3(m.M21, m.M22, m.M23), new vec3(m.M31, m.M32, m.M33), new vec3(m.M41, m.M42, m.M43)),
            V(g.IblCubeMapScale), g.IblCubeMap != null ? 0 : -1, g.IblCubeMap != null ? 0 : -1, 0, g.SkyLightFlags, -1, -1, g.SceneFlags, 0u);
    }

    private static PathTraceBarnLightGPU_t_natural GpuLight(Light l)
    {
        static mat4 M(Matrix4x4 m) => new(new vec4(m.M11, m.M21, m.M31, m.M41), new vec4(m.M12, m.M22, m.M32, m.M42), new vec4(m.M13, m.M23, m.M33, m.M43), new vec4(m.M14, m.M24, m.M34, m.M44));
        var lum = new vec4[5];
        for (var i = 0; i < 5; i++)
            lum[i] = V(l.Luminaire[i]);
        var box = new mat4x3(new vec3(l.IlluminationRow0.X, l.IlluminationRow1.X, l.IlluminationRow2.X), new vec3(l.IlluminationRow0.Y, l.IlluminationRow1.Y, l.IlluminationRow2.Y),
            new vec3(l.IlluminationRow0.Z, l.IlluminationRow1.Z, l.IlluminationRow2.Z), new vec3(l.IlluminationRow0.W, l.IlluminationRow1.W, l.IlluminationRow2.W));
        return new PathTraceBarnLightGPU_t_natural(M(l.ToClipSpace), M(l.FromClipSpace), V(l.NearPlane), V(l.Position), V(l.CookieParameters), V(l.Color), V(l.BounceColor),
            V(l.Orientation), V(l.AngleFade), V(l.DistanceFadeSkirt), new _Array_natural_vector_float_4_5 { data = lum },
            new vec4(-1e9f), new vec4(1e9f), default, default, default, box,
            l.Cookie, l.LuminaireType, l.PathTraceFlags, l.SphericalCookie, l.BakedShadowIndex, l.ShadowedLightId, -1, 0);
    }

    private static vec4 V(Vector4 v) => new(v.X, v.Y, v.Z, v.W);
    private static vec3 Vh(Vector3 v) => new(v.X, v.Y, v.Z);
    private static Vector3 P(vec3 v) => new(v.x, v.y, v.z);
    private static Vector4 W(vec4 v) => new(v.x, v.y, v.z, v.w);
}
