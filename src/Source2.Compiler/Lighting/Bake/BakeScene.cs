using System.Numerics;
using Source2.Compiler.Maps;
using ValveKeyValue;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// vrad3's ray trace scene as scenesystem builds it from scene_info.kv3 and
/// scene_meshes.kv3 (2026-10-15 scenesystem: the world's loader 1800912d0,
/// instances 180094d30, materials 180098660, lights 18008beb0 / 18008c110,
/// the IBL 1800999d0, the scene globals in 180095180 with the constructor's
/// defaults 18008d1a0):
/// <list type="bullet">
/// <item>Per material: flags (bit 0 doublesided or renderbackfaces, 2
/// Translucent or alphatest, 5 mapbuilder.blocklight) and the instance mask
/// filters: tools.toolsmaterial clears bit 0, DoNotCastShadows bits 1 to 3,
/// an opaque material sets 0x10 (a translucent or alpha-tested one clears
/// it).</item>
/// <item>Per instance: the mask (~and &amp; flags) | or, its low byte the ray
/// tracing mask (an instance with none is left out), the material flags in
/// the high half, the tint packed as sRGB bytes, the emissive boost; a
/// doublesided material's instance ignores facing culls.</item>
/// <item>Per light: the GPU record (<see cref="Light"/>).</item>
/// <item>The globals: sky colour (the uniform sky colour; 0 with an IBL
/// cubemap), sky bounce, sun irradiance (0, never set), the sun minimum
/// brightness (FLT_MAX by default), the IBL's inverse transform and scale
/// (times exp2 of the sky material's brightness), sky_bounce_scale, the sky
/// light flags, and scene flag 1 (shadow rays cull front faces) unless
/// gameinfo's ResourceCompiler/BakedLighting/DisableCullingForShadows,
/// which csgo's gameinfo.gi sets: CS2's shadow rays cull nothing (with
/// culling, probe01's sun leaks through the backs of single-sided walls).</item>
/// </list>
/// </summary>
public sealed class BakeScene
{
    /// <summary>A mesh's vertices as the hit shaders read them, in object space.</summary>
    public sealed record Mesh(Vector3[] Positions, Vector3[] Normals, Vector2[] Texcoords, int[] Indices, string Material)
    {
        /// <summary>The vertex blob as scene_meshes.kv3 holds it (all floats), its stride in floats and its fields.</summary>
        public float[] Vertices { get; init; } = [];
        public int Stride { get; init; }
        public IReadOnlyList<MeshField> Fields { get; init; } = [];
    }

    /// <summary>One vertex field of a scene mesh: name, semantic index, offset and size in floats.</summary>
    public readonly record struct MeshField(string Name, int SemanticIndex, int Offset, int Size);

    /// <summary>One placed mesh, PackedRtxInstanceData_t's fields plus what the hit group needs.</summary>
    public sealed record Instance(int Mesh, Matrix4x4 ObjectToWorld, Matrix4x4 WorldToObject, BakeMaterial Material, uint SrgbColor, Vector3 Tint,
                                  float EmissiveBoost, uint InstanceFlags, uint MaterialFlags, byte Mask)
    {
        /// <summary>nMaterialFlags_nInstanceFlags: material flags high, instance flags low.</summary>
        public uint Packed => MaterialFlags << 16 | (InstanceFlags & 0xffff);
    }

    public IReadOnlyList<Mesh> Meshes { get; }
    public IReadOnlyList<Instance> Instances { get; }
    public IReadOnlyList<Light> Lights { get; }
    public Globals Global { get; }
    public RayScene Rays { get; }

    /// <summary>SceneGlobalInfoGPU_t's fields the lightmap shader reads.</summary>
    public sealed class Globals
    {
        public Vector4 SkyColor;
        public Vector4 SkyAmbientBounce;
        public Vector4 SunLightIrradiance = new(0f, 0f, 0f, float.MaxValue);
        public Matrix4x4 IblTransformInverse = Matrix4x4.Identity;
        public Vector4 IblCubeMapScale = new(1f, 1f, 1f, 1f);
        public BakeCubemap? IblCubeMap;
        public uint SkyLightFlags;
        public uint SceneFlags;
    }

    public BakeScene(IReadOnlyList<Mesh> meshes, IReadOnlyList<Instance> instances, IReadOnlyList<Light> lights, Globals global)
    {
        Meshes = meshes;
        Instances = instances;
        Lights = lights;
        Global = global;
        var vertices = new List<Vector3>();
        var tris = new List<RayScene.Triangle>();
        var rayInstances = new List<RayScene.Instance>();
        for (var i = 0; i < instances.Count; i++)
        {
            var inst = instances[i];
            var m = meshes[inst.Mesh];
            rayInstances.Add(new RayScene.Instance(inst.Mask, !inst.Material.NeedsAnyHit, (inst.MaterialFlags & 1) != 0));
            for (var t = 0; t + 2 < m.Indices.Length; t += 3)
            {
                vertices.Add(Vector3.Transform(m.Positions[m.Indices[t]], inst.ObjectToWorld));
                vertices.Add(Vector3.Transform(m.Positions[m.Indices[t + 1]], inst.ObjectToWorld));
                vertices.Add(Vector3.Transform(m.Positions[m.Indices[t + 2]], inst.ObjectToWorld));
                tris.Add(new RayScene.Triangle(i, t / 3));
            }
        }
        Rays = new RayScene([.. vertices], [.. tris], [.. rayInstances]);
    }

    /// <summary>
    /// The scene from a vrad3 input folder's two files (Valve's or ours).
    /// <paramref name="materials"/> resolves a material path; <paramref name="cubemap"/> an IBL material's cubemap.
    /// </summary>
    public static BakeScene Load(KVObject sceneInfo, KVObject sceneMeshes, Func<string, BakeMaterial> materials, Func<string, (BakeCubemap Cube, float Brightness)?>? cubemap = null,
                                 bool disableCullingForShadows = true)
    {
        var meshes = new List<Mesh>();
        foreach (var m in sceneMeshes["meshes"].Values)
            meshes.Add(ReadMesh(m));
        var instances = new List<Instance>();
        foreach (var o in sceneInfo["instances"].Values)
        {
            var mesh = (int)o["mesh"].ToInt64(null);
            var material = materials(o["material"].ToString(null) ?? "");
            var world = Matrix4x4.Identity;
            if (BakeScene.Opt(o, "transform") is { } t)
            {
                var f = Floats(t);
                world = new Matrix4x4(f[0], f[4], f[8], 0f, f[1], f[5], f[9], 0f, f[2], f[6], f[10], 0f, f[3], f[7], f[11], 1f);
            }
            var tint = Vector4.One;
            if (BakeScene.Opt(o, "tint_color") is { } tc)
            {
                var f = Floats(tc);
                tint = new Vector4(f[0], f[1], f[2], f.Length > 3 ? f[3] : 1f);
            }
            var boost = BakeScene.Opt(o, "emissive_lighting_boost") is { } eb ? (float)eb.ToDouble(null) : 1f;
            var flags = BakeScene.Opt(o, "flags") is { } fl ? (uint)fl.ToInt64(null) : 7u;
            var bits = (~material.MaskAnd & flags) | material.MaskOr;
            if ((byte)bits == 0)
                continue;
            Matrix4x4.Invert(world, out var inverse);
            var srgb = PackSrgb(tint);
            var linear = UnpackSrgb(srgb);
            instances.Add(new Instance(mesh, world, inverse, material, srgb, linear, boost, bits, material.Flags, (byte)bits));
        }
        var lights = new List<Light>();
        foreach (var l in BakeScene.Opt(sceneInfo, "lights")?.Values ?? [])
            lights.Add(Light.FromEntry(l));
        var g = new Globals();
        if (!disableCullingForShadows)
            g.SceneFlags |= 1;
        if (BakeScene.Opt(sceneInfo, "ibl") is { ValueType: KVValueType.Collection } ibl)
        {
            g.SkyLightFlags = BakeScene.Opt(ibl, "path_trace_light_flags") is { } plf ? (uint)plf.ToInt64(null) : 1u;
            if (BakeScene.Opt(ibl, "sky_bounce") is { } sb)
            {
                var f = Floats(sb);
                g.SkyAmbientBounce = new Vector4(f[0], f[1], f[2], f.Length > 3 ? f[3] : 0f);
            }
            if (BakeScene.Opt(ibl, "uniform_sky_color") is { } usc)
            {
                var f = Floats(usc);
                g.SkyColor = new Vector4(f[0], f[1], f[2], 0f);
            }
            g.IblCubeMapScale.W = BakeScene.Opt(ibl, "sky_bounce_scale") is { } sbs ? (float)sbs.ToDouble(null) : 1f;
            g.SunLightIrradiance.W = BakeScene.Opt(ibl, "sun_light_min_brightness") is { } smb ? (float)smb.ToDouble(null) : 0f;
            if (BakeScene.Opt(ibl, "material") is { } mat && cubemap?.Invoke(mat.ToString(null) ?? "") is { } cube)
            {
                var transform = Matrix4x4.Identity;
                if (BakeScene.Opt(ibl, "transform") is { } tr)
                {
                    var f = Floats(tr);
                    transform = new Matrix4x4(f[0], f[4], f[8], 0f, f[1], f[5], f[9], 0f, f[2], f[6], f[10], 0f, f[3], f[7], f[11], 1f);
                }
                Matrix4x4.Invert(transform, out g.IblTransformInverse);
                var scale = BakeScene.Opt(ibl, "scale") is { } sc ? Floats(sc) : [1f, 1f, 1f];
                var exposure = MathF.Pow(2f, cube.Brightness);
                g.IblCubeMapScale.X *= scale[0] * exposure;
                g.IblCubeMapScale.Y *= scale[1] * exposure;
                g.IblCubeMapScale.Z *= scale[2] * exposure;
                g.IblCubeMap = cube.Cube;
                g.SkyColor = Vector4.Zero;
            }
        }
        return new BakeScene(meshes, instances, lights, g);
    }

    private static Mesh ReadMesh(KVObject m)
    {
        var stride = (int)m["vertex_stride"].ToInt64(null);
        var count = (int)m["vertex_count"].ToInt64(null);
        var vertices = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(m["vertices"].AsBlob()).ToArray();
        var indices = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(m["indices"].AsBlob()).ToArray();
        int position = -1, normal = -1, texcoord = -1;
        var fields = new List<MeshField>();
        foreach (var a in m["attributes"].Values)
        {
            var name = a["name"].ToString(null);
            var index = a["semantic_index"].ToInt64(null);
            var offset = (int)a["offset"].ToInt64(null);
            fields.Add(new MeshField(name ?? "", (int)index, offset, Opt(a, "size") is { } size ? (int)size.ToInt64(null) : 0));
            if (name == "position" && position < 0)
                position = offset;
            else if (name == "normal" && normal < 0)
                normal = offset;
            else if (name == "texcoord" && index == 0 && texcoord < 0)
                texcoord = offset;
        }
        var p = new Vector3[count];
        var n = new Vector3[count];
        var uv = new Vector2[count];
        for (var v = 0; v < count; v++)
        {
            var at = v * stride;
            p[v] = new Vector3(vertices[at + position], vertices[at + position + 1], vertices[at + position + 2]);
            if (normal >= 0)
                n[v] = new Vector3(vertices[at + normal], vertices[at + normal + 1], vertices[at + normal + 2]);
            if (texcoord >= 0)
                uv[v] = new Vector2(vertices[at + texcoord], vertices[at + texcoord + 1]);
        }
        return new Mesh(p, n, uv, indices, m["material_name"].ToString(null) ?? "") { Vertices = vertices, Stride = stride, Fields = fields };
    }

    /// <summary>A child that may be absent (KVObject's indexer throws for a missing key).</summary>
    internal static KVObject? Opt(KVObject o, string key) => o.ContainsKey(key) ? o[key] : null;

    internal static float[] Floats(KVObject a) => [.. a.Values.Select(v => (float)v.ToDouble(null))];

    /// <summary>18049c8d0: a linear colour as sRGB bytes (r low).</summary>
    public static uint PackSrgb(Vector4 c)
    {
        static uint B(float x)
        {
            x = Math.Clamp(x, 0f, 1f);
            var s = (x > 0.0031308f ? MathF.Pow(x, 0.41666666f) * 1.055f - 0.055f : x * 12.92f) * 255f;
            return (uint)MathF.Round(Math.Clamp(s, 0f, 255f));
        }
        // Alpha is not clamped: (char)(int)round(a * 255).
        return B(c.X) | B(c.Y) << 8 | B(c.Z) << 16 | (uint)(byte)(int)MathF.Round(c.W * 255f) << 24;
    }

    /// <summary>The default hit group's decode of nSrgbColor: bytes over 255, rgb through the sRGB curve.</summary>
    public static Vector3 UnpackSrgb(uint c)
    {
        static float L(uint b)
        {
            var x = b * 0.003921569f;
            return x <= 0.04045f ? x * 0.07739938f : MathF.Pow(x * 0.94786733f + 0.0521327f, 2.4f);
        }
        return new Vector3(L(c & 255), L(c >> 8 & 255), L(c >> 16 & 255));
    }
}
