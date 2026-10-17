using System.Numerics;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>A lightmap sample (LightmapSample_t, 96 bytes).</summary>
public struct LightmapSample
{
    public Vector4 Position, ShadingNormal, InterpolatedNormal, FaceNormal, Tangent;
    public Vector2 LightmapUv;
    public int InstanceFlags;
    public uint SampleFlags;
}

/// <summary>The per-luxel accumulator (Luxel_t, 224 bytes) the shader averages pass by pass.</summary>
public struct Luxel
{
    public Vector4 Irradiance, ShR, ShG, ShB, QuadR, QuadG, QuadB, QuadFinal, BakedShadow, BakedShadowUnoccluded;
    public int Id0, Id1, Id2, Id3;
    public Vector4 Tangent, Normal, Ao;

    public static Luxel Empty() => new() { Id0 = 255, Id1 = 255, Id2 = 255, Id3 = 255 };
}

/// <summary>
/// rtx_vrad3_lightmap's ray generation shader (AHD variant) for one luxel and
/// pass, against the CPU scene:
/// <list type="number">
/// <item>a sample picked from the 5 x 5 neighbourhood of the same chart by a
/// Gaussian reservoir over the lightmap uv distance;</item>
/// <item>optionally the material under it (sample flag 2: a ray down the face
/// normal);</item>
/// <item>one path of up to 16 bounces from a cosine (or transmission) sample:
/// sky on a miss (the ground bounce for downward rays, the IBL or uniform
/// sky colour), at each hit unshadowed bounce lights summed, one shadowed
/// light picked by a reservoir and traced, emission, Russian roulette on the
/// albedo and a diffuse, GGX or transmission continuation;</item>
/// <item>direct light at the luxel: unshadowed direct lights summed, one
/// shadowed light by reservoir, its visibility into the baked shadow slots
/// (or the irradiance when it has none);</item>
/// <item>an AO ray of length 16; the running means of every field.</item>
/// </list>
/// Rays read the per-pass Halton volume at the luxel's 32 x 32 tile position:
/// layer 1 the seed, 1 + b a bounce's direction, 1 + dims + b and 1 + 2 dims + b
/// its light reservoir and light sample, 1 + dims and 1 + 2 dims the luxel's.
/// </summary>
public sealed class PathTracer(BakeScene scene, BakeHits hits)
{
    private const float FarT = 56755.84f, InvPi = 0.31830987f, Pi = 3.1415927f;

    /// <summary>The push constants' dimension count (the host passes 4).</summary>
    public int DimensionCount { get; init; } = 4;

    public Vector3 CordonMin { get; init; } = new(float.MinValue);
    public Vector3 CordonMax { get; init; } = new(float.MaxValue);

    // A primary-payload trace: closest hit with the S_TRANSLUCENT any-hit, the payload shaded.
    private Payload Trace(Vector3 o, Vector3 d, float tMin, float tMax, uint flags, uint mask, float opacityRng)
    {
        var p = Payload.Initial(opacityRng);
        var rng = opacityRng;
        var hit = scene.Rays.Trace(o, d, tMin, tMax, flags, mask, (tri, t, u, v, front) =>
        {
            var opacity = hits.Opacity(tri, u, v);
            return BakeHits.StochasticOpacity(opacity, ref rng);
        });
        p.OpacityRng = rng;
        if (hit.Found)
            hits.Shade(ref p, hit);
        return p;
    }

    // A shadow-payload trace: 1 unless an opaque (or accepted translucent) surface lies within tMax.
    private float Visibility(Vector3 o, Vector3 d, float tMax, uint flags, uint mask)
    {
        if (!(tMax > 0f))
            return 1f;
        var visibility = 1f;
        var hit = scene.Rays.Trace(o, d, 0f, tMax, flags | RayScene.TerminateOnFirstHit, mask, (tri, t, u, v, front) =>
        {
            var opacity = hits.Opacity(tri, u, v);
            visibility *= 1f - Math.Clamp(opacity, 0f, 1f);
            return visibility <= 0f;
        });
        return hit.Found ? 0f : visibility;
    }

    private uint ShadowCull => (scene.Global.SceneFlags & 1) != 0 ? RayScene.CullFrontFacing : (scene.Global.SceneFlags & 2) != 0 ? RayScene.CullBackFacing : 0u;

    /// <summary>
    /// One luxel, one pass. <paramref name="samples"/> and <paramref name="chartIds"/> are the block's
    /// (row-major, <paramref name="width"/> wide); <paramref name="random"/> the pass's Halton volume.
    /// </summary>
    public void Luxel(int x, int y, int width, int height, uint[] chartIds, LightmapSample[] samples, float[] random, int pass,
                      Vector2 blockOffset, Vector2 lightmapSize, ref Luxel luxel)
    {
        if (pass == 0)
            luxel = Bake.Luxel.Empty();
        var tx = x % HaltonSamples.Size;
        var ty = y % HaltonSamples.Size;
        Vector2 R(int layer)
        {
            var at = ((layer * HaltonSamples.Size + ty) * HaltonSamples.Size + tx) * 2;
            return new Vector2(random[at], random[at + 1]);
        }
        var dims = DimensionCount;
        var chart = chartIds[y * width + x];
        if (chart == 0xffffffff)
            return;
        var centre = blockOffset + new Vector2(x, y) + new Vector2(0.5f);
        var seed = R(1);
        // 1. The reservoir over the 5 x 5 neighbourhood.
        var reservoir = 0f;
        var s = default(LightmapSample);
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
            {
                var nx = x + dx;
                var ny = y + dy;
                if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                    continue;
                if (chartIds[ny * width + nx] != chart)
                    continue;
                // The shader indexes the samples y * height + x.
                ref var cand = ref samples[ny * height + nx];
                if (cand.Position.W <= 0f)
                    continue;
                var dv = cand.LightmapUv * lightmapSize - centre;
                var w = 0.7978846f * MathF.Exp(Vector2.Dot(dv, dv) * -2f);
                reservoir += w;
                if (reservoir == 0f)
                    continue;
                if (LightEval.Coin(w / reservoir, ref seed.Y))
                    s = cand;
            }
        if (!(s.Position.W != 0f))
            return;
        var pos = LightEval.V3(s.Position);
        var faceN = LightEval.V3(s.FaceNormal);
        var front = LightEval.Offset(pos, faceN);
        var back = LightEval.Offset(pos, -faceN);
        var shadingN = LightEval.V3(s.ShadingNormal);
        var (bt, bb) = LightEval.Basis(shadingN);
        var instFlags = s.InstanceFlags;
        var transmissive = new Vector3(s.ShadingNormal.W, s.InterpolatedNormal.W, s.FaceNormal.W);
        var acc = luxel;
        var irr = Vector4.Zero;
        Vector4 shr = default, shg = default, shb = default, qr = default, qg = default, qb = default, qf = default;
        Vector4 baked = default, unoccluded = default, ao = default;
        int id0 = 255, id1 = 255, id2 = 255, id3 = 255;
        var normalOut = new Vector4(LightEval.V3(s.InterpolatedNormal), 0f);
        var tangentOut = s.Tangent;
        if (pos.X >= CordonMin.X && pos.Y >= CordonMin.Y && pos.Z >= CordonMin.Z && pos.X <= CordonMax.X && pos.Y <= CordonMax.Y && pos.Z <= CordonMax.Z)
        {
            // 2. The material under the sample.
            var mat = Payload.Initial(0f);
            mat.Normal = shadingN;
            mat.Albedo = Vector3.One;
            if ((s.SampleFlags & 2) != 0)
            {
                var probe = Trace(front + faceN * 0.0375f, -faceN, 0f, 0.075f, 0, 1, 0f);
                if (probe.Instance != -1)
                {
                    mat = probe;
                    shadingN = probe.Normal;
                }
            }
            // 3. The path.
            var sideProb = MathF.Max(transmissive.X, MathF.Max(transmissive.Y, transmissive.Z));
            sideProb /= sideProb + 1f;
            var u0 = seed;
            var backSide = LightEval.Coin(sideProb, ref u0.X);
            var origin = front;
            if (backSide)
                origin = back;
            else
                sideProb = 1f - sideProb;
            var local = LightEval.CosineSample(u0);
            var pdfSide = sideProb;
            if (backSide)
                local.Z = -local.Z;
            var firstDir = LightEval.ToWorld(bt, bb, shadingN, local);
            var dir = firstDir;
            var throughput = new Vector3(1f / (pdfSide * (MathF.Abs(local.Z) * InvPi)));
            var radiance = Vector3.Zero;
            var currentOrigin = origin;
            for (var b = 1; b <= 16; b++)
            {
                const float mis = 1f;
                var arrive = throughput;
                if (arrive == Vector3.Zero)
                    break;
                var u = R(1 + b);
                var p = Trace(currentOrigin, dir, 0f, FarT, 0, 4, u.Y);
                var hitT = p.HitT;
                var uNext = new Vector2(u.X, p.OpacityRng);
                if (p.Instance == -1)
                {
                    radiance += arrive * Sky(dir, R(1 + b + 1), b) * mis;
                    break;
                }
                if (((scene.Instances[p.Instance].Packed >> 16) & 4) != 0)
                    break;
                var vertex = Vertex(currentOrigin, dir, p);
                if (!(Vector3.Dot(vertex.Face, -dir) > 0f))
                {
                    if ((vertex.InstanceFlags & 1024) != 0)
                    {
                        // The shader swaps the previous origin's positions here, which nothing reads after.
                        vertex.Face = -vertex.Face;
                        vertex.M.Normal = -vertex.M.Normal;
                    }
                    else
                    {
                        var ub = R(1 + b);
                        var again = Trace(currentOrigin, dir, hitT * 0.9999f, hitT * 1.0001f, RayScene.CullBackFacing, 4, ub.Y);
                        uNext = new Vector2(ub.X, again.OpacityRng);
                        if (again.Instance == -1)
                            break;
                        vertex = Vertex(currentOrigin, dir, again);
                    }
                }
                var mid = (vertex.Front + vertex.Back) * 0.5f;
                var vertexLight = BounceLights(vertex, mid, dir, R(1 + 2 * dims + b), R(1 + dims + b), b != 0);
                // Russian roulette on the albedo, then the continuation.
                var albedoMax = MathF.Min(0.9f, MathF.Max(vertex.M.Albedo.X, MathF.Max(vertex.M.Albedo.Y, vertex.M.Albedo.Z)));
                var un = uNext;
                throughput = LightEval.Coin(albedoMax, ref un.X) ? Continue(vertex, ref dir, ref currentOrigin, arrive, albedoMax, un) : Vector3.Zero;
                radiance = (radiance + vertexLight * arrive * mis) + vertex.M.Emissive * arrive * mis;
            }
            var am = Vector3.Abs(radiance);
            var amax = MathF.Max(am.X, MathF.Max(am.Y, am.Z));
            var clamped = Vector3.Clamp(radiance / amax, new Vector3(-1f), Vector3.One) * Math.Clamp(amax, -100f, 100f);
            if (amax == 0f)
                clamped = Vector3.Zero;
            var cos0 = Vector3.Dot(firstDir, shadingN);
            var contrib = clamped;
            if (backSide)
            {
                cos0 = -cos0;
                contrib = clamped * transmissive;
            }
            irr += new Vector4(contrib * MathF.Max(0f, cos0) * InvPi, 0f);
            if (cos0 > 0f)
                AddSh(firstDir, contrib, ref shr, ref shg, ref shb, ref qr, ref qg, ref qb, ref qf);
            // 4. Direct light at the luxel.
            DirectLights(pos, front, back, faceN, shadingN, transmissive, mat, R(1 + 2 * dims), R(1 + dims),
                         ref irr, ref shr, ref shg, ref shb, ref qr, ref qg, ref qb, ref qf, ref baked, ref unoccluded, ref id0, ref id1, ref id2, ref id3);
            // 5. AO.
            var aoDir = LightEval.ToWorld(bt, bb, shadingN, LightEval.CosineSample(seed));
            ao.X += Visibility(front, aoDir, 16f, 0, 255);
        }
        irr.W = 1f;
        if (pass > 0)
        {
            var t = 1f / (1f + acc.Irradiance.W);
            var n = Vector3.Lerp(LightEval.V3(acc.Irradiance), LightEval.V3(irr), t);
            irr = new Vector4(n, acc.Irradiance.W + 1f);
            shr = Vector4.Lerp(acc.ShR, shr, t);
            shg = Vector4.Lerp(acc.ShG, shg, t);
            shb = Vector4.Lerp(acc.ShB, shb, t);
            qr = Vector4.Lerp(acc.QuadR, qr, t);
            qg = Vector4.Lerp(acc.QuadG, qg, t);
            qb = Vector4.Lerp(acc.QuadB, qb, t);
            qf = Vector4.Lerp(acc.QuadFinal, qf, t);
            baked = Vector4.Lerp(acc.BakedShadow, baked, t);
            unoccluded = Vector4.Lerp(acc.BakedShadowUnoccluded, unoccluded, t);
            if (acc.Id0 != 255) id0 = acc.Id0;
            if (acc.Id1 != 255) id1 = acc.Id1;
            if (acc.Id2 != 255) id2 = acc.Id2;
            if (acc.Id3 != 255) id3 = acc.Id3;
            tangentOut = Vector4.Lerp(acc.Tangent, tangentOut, t);
            normalOut = Vector4.Lerp(acc.Normal, normalOut, t);
            ao = Vector4.Lerp(acc.Ao, ao, t);
        }
        luxel = new Luxel
        {
            Irradiance = irr, ShR = shr, ShG = shg, ShB = shb, QuadR = qr, QuadG = qg, QuadB = qb, QuadFinal = qf,
            BakedShadow = baked, BakedShadowUnoccluded = unoccluded, Id0 = id0, Id1 = id1, Id2 = id2, Id3 = id3,
            Tangent = tangentOut, Normal = normalOut, Ao = ao,
        };
    }

    /// <summary>A path vertex: the hit's offset positions, face normal, material, the instance's flags and the view direction in the shading frame.</summary>
    private struct PathVertex
    {
        public Vector3 Front, Back, Face;
        public Payload M;
        public int InstanceFlags;
        public Vector3 T, B, Wo;
        public float SpecProb, TransProb;
    }

    private PathVertex Vertex(Vector3 origin, Vector3 dir, in Payload p)
    {
        var inst = scene.Instances[p.Instance];
        var v = new PathVertex { M = p };
        var o = origin + dir * p.HitT;
        var off = (Vector3.Abs(origin) + new Vector3(MathF.Abs(p.HitT))) * 9.9999997e-05f;
        v.Front = o + p.FaceNormal * off;
        v.Back = o + -p.FaceNormal * off;
        v.Face = p.FaceNormal;
        v.InstanceFlags = (int)(inst.Packed & 0xffff);
        v.M.Roughness = MathF.Max(p.Roughness, 0.08f);
        if ((v.InstanceFlags & 256) != 0)
            v.M.Emissive = p.Emissive * inst.EmissiveBoost;
        else
            v.M.Emissive = Vector3.Zero;
        // The smooth position, when the way to it is clear.
        if (p.SmoothPosition.X != float.MaxValue || p.SmoothPosition.Y != float.MaxValue || p.SmoothPosition.Z != float.MaxValue)
        {
            var to = p.SmoothPosition - v.Front;
            var len = to.Length();
            if (Visibility(v.Front, to / len, len, 0, 255) == 1f)
            {
                v.Front = p.SmoothPosition;
                v.Face = p.Normal;
            }
        }
        var (t, b) = LightEval.Basis(p.Normal);
        v.T = t;
        v.B = b;
        v.Wo = LightEval.ToLocal(t, b, p.Normal, -dir);
        var f0 = Vector3.Lerp(new Vector3(p.Reflectance), p.Albedo, p.Metalness);
        var fresnel = f0 + (Vector3.One - f0) * MathF.Pow(MathF.Max(0f, 1f - v.Wo.Z), 5f);
        v.SpecProb = LightEval.Luminance(fresnel);
        if ((p.MaterialTypeAndFlags & 0xffff) == 1)
        {
            var lb = LightEval.Luminance(LightEval.V3(p.MaterialBits));
            v.TransProb = lb / MathF.Max(9.9999997e-05f, lb + LightEval.Luminance(p.Albedo));
        }
        return v;
    }

    private static bool TransmissiveMaterial(in Payload m)
        => ((m.MaterialTypeAndFlags & 0xffff) == 1 && (m.MaterialBits.X > 0f || m.MaterialBits.Y > 0f || m.MaterialBits.Z > 0f))
        || ((m.MaterialTypeAndFlags & 0xffff) == 2 && m.MaterialBits.X > 0f);

    // The lights at a bounce vertex: the unshadowed bounce lights summed, one shadowed light by reservoir.
    private Vector3 BounceLights(in PathVertex v, Vector3 mid, Vector3 dir, Vector2 lightU, Vector2 reservoirU, bool bounce)
    {
        var sum = Vector3.Zero;
        var anyShadowed = false;
        var transmissiveMat = TransmissiveMaterial(v.M);
        foreach (var l in scene.Lights)
        {
            if ((l.PathTraceFlags & 1) == 0)
                continue;
            if ((l.PathTraceFlags & 8) != 0)
            {
                anyShadowed = true;
                continue;
            }
            var intensity = LightEval.Intensity(l, mid, bounce);
            if (!(intensity.X > 0f || intensity.Y > 0f || intensity.Z > 0f))
                continue;
            var (ld, factor) = LightEval.Sample(l, mid, lightU);
            var L = LightEval.V3(ld);
            var backside = !(Vector3.Dot(v.Face, L) > 0f);
            var wi = LightEval.ToLocal(v.T, v.B, v.M.Normal, L);
            var brdf = LightEval.Brdf(backside, v.M, wi, v.Wo, v.M.Roughness);
            var cos = Vector3.Dot(v.M.Normal, L);
            if (backside)
                cos = -cos;
            sum += intensity / factor * (brdf * Pi) * MathF.Max(0f, cos);
        }
        if (!anyShadowed)
            return sum;
        var wsum = 0f;
        var chosenW = 0f;
        Light? chosen = null;
        var chosenI = Vector3.Zero;
        var chosenDir = Vector4.Zero;
        var u = reservoirU.X;
        foreach (var l in scene.Lights)
        {
            if ((l.PathTraceFlags & 1) == 0 || (l.PathTraceFlags & 8) == 0)
                continue;
            var intensity = LightEval.Intensity(l, mid, bounce);
            var lum = LightEval.Luminance(intensity);
            if (lum == 0f)
                continue;
            var (ld, factor) = LightEval.Sample(l, mid, lightU);
            var L = LightEval.V3(ld);
            var cos = Vector3.Dot(v.M.Normal, L);
            if (!(Vector3.Dot(v.Face, L) > 0f) && transmissiveMat)
                cos = -cos;
            var w = lum / factor * MathF.Max(0f, cos);
            wsum += w;
            if (wsum == 0f)
                continue;
            if (LightEval.Coin(w / wsum, ref u))
            {
                chosen = l;
                chosenI = intensity / factor;
                chosenDir = ld;
                chosenW = w;
            }
        }
        if (chosen == null)
            return sum;
        return sum + ShadowedLight(v, mid, chosen, chosenI * (wsum / chosenW), chosenDir);
    }

    // One shadowed light at a bounce vertex: its BRDF-weighted contribution times the shadow ray's visibility.
    private Vector3 ShadowedLight(in PathVertex v, Vector3 mid, Light chosen, Vector3 c, Vector4 chosenDir)
    {
        var Lc = LightEval.V3(chosenDir);
        var bs = !(Vector3.Dot(v.Face, Lc) > 0f);
        var wic = LightEval.ToLocal(v.T, v.B, v.M.Normal, Lc);
        var brdfc = LightEval.Brdf(bs, v.M, wic, v.Wo, v.M.Roughness);
        var cosc = Vector3.Dot(v.M.Normal, Lc);
        if (bs)
            cosc = -cosc;
        var contribution = c * (brdfc * Pi) * MathF.Max(0f, cosc);
        // Subsurface transmission (material type 2) is not ported: the profiles' constant buffer is not read.
        var dist = float.IsInfinity(chosenDir.W) || float.IsNaN(chosenDir.W) ? FarT : chosenDir.W;
        var toNear = Vector3.Dot(Lc, LightEval.V3(chosen.NearPlane));
        var nearT = toNear == 0f ? 0f : (chosen.NearPlane.W - Vector3.Dot(mid, LightEval.V3(chosen.NearPlane))) * (1f / toNear);
        if (nearT > 0f)
            dist = MathF.Min(dist, nearT);
        var origin = Vector3.Dot(Lc, v.Face) < 0f ? v.Back : v.Front;
        var rel = origin - mid;
        var w2 = dist + Vector3.Dot(Lc, rel);
        var aim = Vector3.Normalize(Lc * w2 + rel);
        var nonzero = contribution != Vector3.Zero;
        var vis = Visibility(origin, aim, nonzero ? w2 : 0f, ShadowCull, 2);
        return contribution * (nonzero ? vis : 0f);
    }

    // Russian roulette survived: the next direction and the throughput update.
    private Vector3 Continue(in PathVertex v, ref Vector3 dir, ref Vector3 origin, Vector3 throughput, float albedoMax, Vector2 u)
    {
        var sideP = 1f;
        var transmit = LightEval.Coin(v.TransProb, ref u.X);
        sideP = transmit ? v.TransProb : 1f - v.TransProb;
        if (transmit)
            origin = v.Back;
        else
            origin = v.Front;
        Vector3 local;
        if (transmit)
        {
            local = LightEval.CosineSample(u);
            local.Z = -local.Z;
        }
        else
        {
            var uu = u;
            if (LightEval.Coin(v.SpecProb, ref uu.X))
            {
                var r = v.M.Roughness;
                var tan = r * r * MathF.Sqrt(uu.X) / MathF.Sqrt(1f - uu.X);
                var cos2 = 1f / (1f + tan * tan);
                var sin = MathF.Sqrt(MathF.Max(0f, 1f - cos2));
                var phi = 6.2831855f * uu.Y;
                var h = new Vector3(MathF.Cos(phi) * sin, MathF.Sin(phi) * sin, MathF.Sqrt(cos2));
                if (!(Vector3.Dot(v.Wo, h) > 0f))
                    h = -h;
                local = -Vector3.Reflect(v.Wo, h);
            }
            else
                local = LightEval.CosineSample(uu);
        }
        var hv = Vector3.Normalize(local + v.Wo);
        float pdf;
        if (transmit)
            pdf = local.Z * -InvPi;
        else
        {
            float diffuseW;
            if (v.SpecProb > 0f)
            {
                var r = v.M.Roughness;
                var a2 = r * r * (r * r);
                var d = (hv.Z * a2 - hv.Z) * hv.Z + 1f;
                diffuseW = 1f - v.SpecProb;
                pdf = v.SpecProb * (a2 / MathF.Max(9.9999999e-09f, d * d) * hv.Z / (12.566371f * MathF.Max(0f, Vector3.Dot(v.Wo, hv))));
            }
            else
            {
                diffuseW = 1f;
                pdf = 0f;
            }
            pdf += diffuseW * (local.Z * InvPi);
        }
        if (pdf == 0f || float.IsNaN(pdf))
            pdf = float.MaxValue;
        var brdf = LightEval.Brdf(transmit, v.M, local, v.Wo, v.M.Roughness);
        var world = LightEval.ToWorld(v.T, v.B, v.M.Normal, local);
        dir = world;
        var cos = Vector3.Dot(v.M.Normal, world);
        if (transmit)
            cos = -cos;
        var valid = (v.InstanceFlags & 1024) != 0 || (Vector3.Dot(v.Face, world) > 0f) != transmit;
        return valid ? throughput * (brdf * MathF.Max(0f, cos)) / (albedoMax * sideP * pdf) : Vector3.Zero;
    }

    // The sky along a missed ray.
    private Vector3 Sky(Vector3 dir, Vector2 u, int bounce)
    {
        var g = scene.Global;
        if ((g.SkyLightFlags & 1) == 0)
            return Vector3.Zero;
        var ground = false;
        if (dir.Z < 0f)
        {
            var uy = u.Y;
            ground = LightEval.Coin(g.SkyAmbientBounce.W, ref uy);
            u.Y = uy;
        }
        Vector3 d2, r;
        if (ground)
        {
            d2 = LightEval.CosineSample(u);
            r = LightEval.V3(g.SunLightIrradiance);
        }
        else
        {
            d2 = dir;
            r = Vector3.Zero;
        }
        if (g.IblCubeMap != null)
        {
            var local = Vector3.Normalize(Vector3.TransformNormal(d2, g.IblTransformInverse));
            var c = LightEval.V3(g.IblCubeMap.Sample(local));
            if (LightEval.Luminance(c) < g.SunLightIrradiance.W)
                r += LightEval.V3(g.IblCubeMapScale) * c;
        }
        else
            r += LightEval.V3(g.SkyColor);
        if (bounce > 1)
            r *= g.IblCubeMapScale.W;
        if (ground)
            r *= LightEval.V3(g.SkyAmbientBounce);
        return r;
    }

    // Direct light at the luxel.
    private void DirectLights(Vector3 pos, Vector3 front, Vector3 back, Vector3 faceN, Vector3 shadingN, Vector3 transmissive, in Payload mat, Vector2 lightU, Vector2 reservoirU,
                              ref Vector4 irr, ref Vector4 shr, ref Vector4 shg, ref Vector4 shb, ref Vector4 qr, ref Vector4 qg, ref Vector4 qb, ref Vector4 qf,
                              ref Vector4 baked, ref Vector4 unoccluded, ref int id0, ref int id1, ref int id2, ref int id3)
    {
        var mid = (front + back) * 0.5f;
        var transmissiveLuxel = transmissive != Vector3.Zero;
        var wsum = 0f;
        var chosenW = 0f;
        Light? chosen = null;
        var chosenI = Vector3.Zero;
        var chosenDir = Vector4.Zero;
        var chosenBack = false;
        var u = reservoirU.X;
        foreach (var l in scene.Lights)
        {
            if ((l.PathTraceFlags & 2) == 0)
                continue;
            var intensity = LightEval.Intensity(l, mid, false);
            if (intensity == Vector3.Zero)
                continue;
            var (ld, factor) = LightEval.Sample(l, mid, lightU);
            var e = intensity * (Pi / factor);
            var L = LightEval.V3(ld);
            var backside = !(Vector3.Dot(faceN, L) > 0f);
            if ((l.PathTraceFlags & 8) == 0)
            {
                var a = Vector3.Abs(e);
                var m = MathF.Max(a.X, MathF.Max(a.Y, a.Z));
                var c = m == 0f ? Vector3.Zero : Vector3.Clamp(e / m, new Vector3(-1f), Vector3.One) * Math.Clamp(m, -100f, 100f);
                var cos = Vector3.Dot(L, shadingN);
                if (backside)
                {
                    cos = -cos;
                    c *= transmissive;
                }
                irr += new Vector4(c * MathF.Max(0f, cos) * InvPi, 0f);
                if (cos > 0f)
                    AddSh(L, c, ref shr, ref shg, ref shb, ref qr, ref qg, ref qb, ref qf);
                continue;
            }
            if (backside && !transmissiveLuxel)
                continue;
            var w = MathF.Max(e.X, MathF.Max(e.Y, e.Z));
            wsum += w;
            if (wsum == 0f)
                continue;
            if (LightEval.Coin(w / wsum, ref u))
            {
                chosen = l;
                chosenDir = ld;
                chosenW = w;
                chosenBack = backside;
                chosenI = e;
            }
        }
        if (chosen == null || chosenI == Vector3.Zero)
            return;
        var ce = chosenI * (wsum / chosenW);
        var Ld = LightEval.V3(chosenDir);
        var dist = float.IsInfinity(chosenDir.W) || float.IsNaN(chosenDir.W) ? FarT : chosenDir.W;
        var toNear = Vector3.Dot(Ld, LightEval.V3(chosen.NearPlane));
        var nearT = toNear == 0f ? 0f : (chosen.NearPlane.W - Vector3.Dot(mid, LightEval.V3(chosen.NearPlane))) * (1f / toNear);
        if (nearT > 0f)
            dist = MathF.Min(dist, nearT);
        var origin = chosenBack ? back : front;
        var rel = origin - mid;
        var w2 = dist + Vector3.Dot(Ld, rel);
        var aim = Vector3.Normalize(Ld * w2 + rel);
        var vis = Visibility(origin, aim, w2, ShadowCull, 2);
        var slot = chosen.BakedShadowIndex;
        var lum = LightEval.Luminance(ce);
        var mask = slot switch { 0 => new Vector4(1, 0, 0, 0), 1 => new Vector4(0, 1, 0, 0), 2 => new Vector4(0, 0, 1, 0), 3 => new Vector4(0, 0, 0, 1), _ => Vector4.Zero } * lum;
        baked += mask * vis;
        unoccluded += mask;
        if (mask.X != 0f) id0 = chosen.ShadowedLightId;
        if (mask.Y != 0f) id1 = chosen.ShadowedLightId;
        if (mask.Z != 0f) id2 = chosen.ShadowedLightId;
        if (mask.W != 0f) id3 = chosen.ShadowedLightId;
        if (slot != -1)
            return;
        var lit = ce * vis;
        var a2 = Vector3.Abs(lit);
        var m2 = MathF.Max(a2.X, MathF.Max(a2.Y, a2.Z));
        var c2 = m2 == 0f ? Vector3.Zero : Vector3.Clamp(lit / m2, new Vector3(-1f), Vector3.One) * Math.Clamp(m2, -100f, 100f);
        var cos2 = Vector3.Dot(Ld, shadingN);
        if (chosenBack)
        {
            cos2 = -cos2;
            c2 *= transmissive;
        }
        irr += new Vector4(c2 * MathF.Max(0f, cos2) * InvPi, 0f);
        if (cos2 > 0f)
            AddSh(Ld, c2, ref shr, ref shg, ref shb, ref qr, ref qg, ref qb, ref qf);
    }

    private static void AddSh(Vector3 d, Vector3 c, ref Vector4 shr, ref Vector4 shg, ref Vector4 shb, ref Vector4 qr, ref Vector4 qg, ref Vector4 qb, ref Vector4 qf)
    {
        var l1 = new Vector4(0.2820948f, -0.48860252f * d.Y, 0.48860252f * d.Z, -0.48860252f * d.X);
        var l2 = new Vector4(1.0925485f * d.X * d.Y, -1.0925485f * d.Y * d.Z, 0.31539157f * (3f * d.Z * d.Z - 1f), -1.0925485f * d.X * d.Z);
        shr += l1 * c.X;
        qr += l2 * c.X;
        shg += l1 * c.Y;
        qg += l2 * c.Y;
        shb += l1 * c.Z;
        qb += l2 * c.Z;
        var last = 0.54627424f * (d.X * d.X - d.Y * d.Y);
        qf += new Vector4(c * last, 0f);
    }
}
