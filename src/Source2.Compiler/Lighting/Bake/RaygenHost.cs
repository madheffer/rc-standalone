using System.Numerics;
using Source2.Compiler.Lighting.Bake.Glsl;
using Source2.Compiler.Lighting.Bake.Glsl.Raygen;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// What every transpiled vrad3 ray generation shader asks of its host, from
/// the bake scene: the scene globals, the lights, the instance table, the
/// Halton volume lookup, the IBL cube, and traceRayEXT.
/// <list type="bullet">
/// <item>The light bins are one cell holding every light (vrad3's binning is
/// conservative, so one cell with all of them changes no light that reaches a
/// point).</item>
/// <item>g_Lights as PathTraceBarnLightGPU_t (scenesystem 18008c110's fields
/// from <see cref="Light"/>).</item>
/// <item>A miss leaves the payload as the shader initialised it (vrad3 has no
/// miss shader that writes it); shadow rays (payload 1) multiply the
/// visibility by one minus each translucent candidate's opacity, and any
/// opaque hit makes it 0.</item>
/// </list>
/// </summary>
internal static class RaygenHost
{
    public static PackedRtxInstanceData_t_natural[] Instances(BakeScene scene) => [.. scene.Instances.Select(i => new PackedRtxInstanceData_t_natural
    {
        nSrgbColor = i.SrgbColor, nTriangleGeometry = (uint)i.Mesh, nMaterialFlags_nInstanceFlags = i.Packed, fEmissiveBoost = i.EmissiveBoost, nInstanceFlags = i.InstanceFlags,
    })];

    /// <summary>One light bin word per 32 lights, every bit set.</summary>
    public static uint[] LightBins(BakeScene scene)
    {
        var bins = new uint[Math.Max(1, (scene.Lights.Count + 31) / 32)];
        for (var i = 0; i < scene.Lights.Count; i++)
            bins[i / 32] |= 1u << (i % 32);
        return bins;
    }

    public static SLANG_ParameterGroup_SubsurfaceProfilesConstantBuffer_t_natural NoSubsurface() => new()
    {
        g_vSubsurfaceSamples = new _Array_natural_vector_float_4_480 { data = new vec4[480] },
        g_SubsurfaceProfile = new _Array_natural_SSProfileConstants32 { data = new SSProfileConstants_natural[32] },
    };

    /// <param name="binned">
    /// The light bin grid and local light bounds as vrad3 has them (the GPU host runs Valve's
    /// rtx_binlights over it); false gives the transpiled raygens one cell holding every light.
    /// </param>
    public static SceneGlobalInfoGPU_t_natural Globals(BakeScene scene, bool binned = false)
    {
        var g = scene.Global;
        var m = g.IblTransformInverse;
        var (lo, hi) = binned ? (new vec4(g.LocalLightMin.X, g.LocalLightMin.Y, g.LocalLightMin.Z, 0f), new vec4(g.LocalLightMax.X, g.LocalLightMax.Y, g.LocalLightMax.Z, 0f))
            : (new vec4(-1e9f), new vec4(1e9f));
        var cells = binned ? new ivec4(g.BinCells.X, g.BinCells.Y, g.BinCells.Z, scene.Lights.Count) : new ivec4(1, 1, 1, scene.Lights.Count);
        return new SceneGlobalInfoGPU_t_natural(V(g.SkyColor), V(g.SkyAmbientBounce), V(g.SunLightIrradiance), lo, hi, cells,
            new mat4x3(new vec3(m.M11, m.M12, m.M13), new vec3(m.M21, m.M22, m.M23), new vec3(m.M31, m.M32, m.M33), new vec3(m.M41, m.M42, m.M43)),
            V(g.IblCubeMapScale), g.IblCubeMap != null ? 0 : -1, g.IblCubeMap != null ? 0 : -1, 0, g.SkyLightFlags, -1, -1, g.SceneFlags, 0xffffffffu);
    }

    public static PathTraceBarnLightGPU_t_natural GpuLight(Light l)
    {
        static mat4 M(Matrix4x4 m) => new(new vec4(m.M11, m.M21, m.M31, m.M41), new vec4(m.M12, m.M22, m.M32, m.M42), new vec4(m.M13, m.M23, m.M33, m.M43), new vec4(m.M14, m.M24, m.M34, m.M44));
        var lum = new vec4[5];
        for (var i = 0; i < 5; i++)
            lum[i] = V(l.Luminaire[i]);
        var box = new mat4x3(new vec3(l.IlluminationRow0.X, l.IlluminationRow1.X, l.IlluminationRow2.X), new vec3(l.IlluminationRow0.Y, l.IlluminationRow1.Y, l.IlluminationRow2.Y),
            new vec3(l.IlluminationRow0.Z, l.IlluminationRow1.Z, l.IlluminationRow2.Z), new vec3(l.IlluminationRow0.W, l.IlluminationRow1.W, l.IlluminationRow2.W));
        return new PathTraceBarnLightGPU_t_natural(M(l.ToClipSpace), M(l.FromClipSpace), V(l.NearPlane), V(l.Position), V(l.CookieParameters), V(l.Color), V(l.BounceColor),
            V(l.Orientation), V(l.AngleFade), V(l.DistanceFadeSkirt), new _Array_natural_vector_float_4_5 { data = lum },
            V(l.MinBounds), V(l.MaxBounds), V(l.IlluminationCenter), V(l.IlluminationOrientation), V(l.IlluminationExtent), box,
            l.Cookie, l.LuminaireType, l.PathTraceFlags, l.SphericalCookie, l.BakedShadowIndex, l.ShadowedLightId, 0, 0);
    }

    /// <summary>imageLoad(g_RandomSamples, c) on the pass's Halton volume.</summary>
    public static vec4 Random(float[] random, ivec3 c)
    {
        var x = ((c.x % HaltonSamples.Size) + HaltonSamples.Size) % HaltonSamples.Size;
        var y = ((c.y % HaltonSamples.Size) + HaltonSamples.Size) % HaltonSamples.Size;
        var z = Math.Clamp(c.z, 0, HaltonSamples.Layers - 1);
        var at = ((z * HaltonSamples.Size + y) * HaltonSamples.Size + x) * 2;
        return new vec4(random[at], random[at + 1], 0f, 1f);
    }

    public static vec4 IblCube(BakeScene scene, vec3 direction)
    {
        if (scene.Global.IblCubeMap is not { } cube)
            return default;
        var c = cube.Sample(new Vector3(direction.x, direction.y, direction.z));
        return new vec4(c.X, c.Y, c.Z, c.W);
    }

    /// <summary>traceRayEXT for payload 0 (closest hit through the hit groups) and 1 (shadow visibility).</summary>
    public static void Trace(BakeScene scene, BakeHits hits, ref Payload_t p, ref PayloadShadow_t p1, uint flags, uint mask, vec3 origin, float tMin, vec3 direction, float tMax, int payload,
        Action<string>? debug = null)
    {
        var o = new Vector3(origin.x, origin.y, origin.z);
        var d = new Vector3(direction.x, direction.y, direction.z);
        if (payload == 1)
        {
            var visibility = p1.m_flVisibility;
            var hit = scene.Rays.Trace(o, d, tMin, tMax, flags | RayScene.TerminateOnFirstHit, mask, (tri, t, u, v, front) =>
            {
                visibility *= 1f - Math.Clamp(hits.Opacity(tri, u, v), 0f, 1f);
                return visibility <= 0f;
            });
            p1.m_flVisibility = hit.Found ? 0f : visibility;
            return;
        }
        var rng = p.flOpacityRng;
        var found = scene.Rays.Trace(o, d, tMin, tMax, flags, mask, (tri, t, u, v, front) => BakeHits.StochasticOpacity(hits.Opacity(tri, u, v), ref rng));
        p.flOpacityRng = rng;
        debug?.Invoke($"  trace flags {flags} mask {mask} o {origin} d {direction} t {tMin}..{tMax}: {(found.Found ? $"hit t {found.T} tri {found.Triangle} front {found.FrontFacing}" : "miss")}");
        if (!found.Found)
            return;
        var ours = new Payload
        {
            SmoothPosition = P(p.vSmoothPositionWs), HitT = p.flHitT, OpacityRng = rng, Instance = p.nInstance, Flipped = p.bFlippedNormal,
            Albedo = new Vector3(1f, 0f, 1f), Roughness = 1f, Reflectance = 0.040008545f,
        };
        hits.Shade(ref ours, found);
        p = new Payload_t(Vh(ours.SmoothPosition), Vh(ours.FaceNormal), Vh(ours.InterpolatedNormal), ours.HitT, ours.OpacityRng, ours.Instance, ours.Flipped,
            new MaterialDataMinimal_t(GL.f16vec3(Vh(ours.Normal)), GL.f16(ours.Metalness), GL.f16vec3(Vh(ours.Albedo)), GL.f16(ours.Roughness), GL.f16vec3(Vh(ours.Emissive)),
                GL.f16(ours.Reflectance), ours.MaterialTypeAndFlags, GL.f16vec4(new vec4(ours.MaterialBits.X, ours.MaterialBits.Y, ours.MaterialBits.Z, ours.MaterialBits.W))));
        debug?.Invoke($"    payload smooth {p.vSmoothPositionWs} face {p.vFaceNormalWs} interp {p.vInterpolatedNormalWs} inst {p.nInstance} normal {p.material.vNormalWs} albedo {p.material.vAlbedo} rough {p.material.flRoughness}");
    }

    public static vec4 V(Vector4 v) => new(v.X, v.Y, v.Z, v.W);
    public static vec3 Vh(Vector3 v) => new(v.X, v.Y, v.Z);
    public static Vector3 P(vec3 v) => new(v.x, v.y, v.z);
    public static Vector4 W(vec4 v) => new(v.x, v.y, v.z, v.w);
}
