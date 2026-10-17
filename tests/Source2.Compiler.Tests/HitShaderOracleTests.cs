using System.Buffers.Binary;
using System.Numerics;
using Source2.Compiler.Gpu;
using Source2.Compiler.Lighting.Bake;
using Source2.Compiler.Lighting.Bake.Glsl;
using Source2.Compiler.Maps;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The GPU oracle for the transpiled hit groups: Valve's closest-hit GLSL, rewritten as compute
/// (tools/re/hit_to_compute.py), runs on this PC's GPU with the scene's own buffers and the
/// material's textures uploaded as their stored blocks (BC1/BC4/BC7, sRGB views); the transpiled
/// C# runs the same hits on the CPU. Every payload word is compared.
/// <c>HITORACLE=&lt;vrad3 job folder&gt;</c>, <c>HITORACLE_MATERIAL=&lt;part of a material path&gt;</c>,
/// <c>HITORACLE_HITS</c> (default 4096).
/// </summary>
public class HitShaderOracleTests(ITestOutputHelper output)
{
    private static readonly string[] Fields =
    [
        "smooth.x", "smooth.y", "smooth.z", "face.x", "face.y", "face.z", "interp.x", "interp.y", "interp.z", "hitT", "opacityRng",
        "nInstance", "flipped", "normal.x", "normal.y", "normal.z", "metalness", "albedo.r", "albedo.g", "albedo.b", "roughness",
        "emissive.r", "emissive.g", "emissive.b", "reflectance", "typeAndFlags", "bits.x", "bits.y", "bits.z", "bits.w", "ignored", "pad",
    ];

    [Fact]
    public void ClosestHit()
    {
        if (Environment.GetEnvironmentVariable("HITORACLE") is not { } folder || CS2Fixtures.StockPak() is not { } pak)
            return;
        // RayScene.Build recurses deeper than Windows' 1 MB default stack (as CpuBakeTests.Bake).
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { Run(folder, pak); }
            catch (Exception e) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
        }, 256 << 20);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private void Run(string folder, string pak)
    {
        var filter = Environment.GetEnvironmentVariable("HITORACLE_MATERIAL") ?? "";
        var count = int.TryParse(Environment.GetEnvironmentVariable("HITORACLE_HITS"), out var n) ? n : 4096;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var content = new GameContent(pak);
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var materials = new Dictionary<string, BakeMaterial>(StringComparer.OrdinalIgnoreCase);
        BakeMaterial Material(string path)
        {
            if (materials.TryGetValue(path, out var m))
                return m;
            var info = content.Material(path);
            MeshEntryFlags.IAttributes attrs = info is null ? MaterialAttributes.Empty : MaterialAttributes.Of(info, shaders, content.TextureSize);
            return materials[path] = BakeMaterial.From(path, attrs, info, _ => null);
        }
        KVObject sceneInfo;
        using (var s = File.OpenRead(Path.Combine(folder, Environment.GetEnvironmentVariable("HITORACLE_SCENE") ?? "scene_info.kv3")))
            sceneInfo = KVSerializer.Create(KVSerializationFormat.KeyValues3Text).Deserialize(s);
        var scene = BakeScene.Load(sceneInfo, SceneInfoTests.ReadBinary(Path.Combine(folder, "scene_meshes.kv3")), Material, _ => null);
        using var rtxShaders = new RtxShaderLibrary(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"), Path.Combine(game, "core", "shaders_vulkan_dir.vpk"));
        var rtx = RtxScene.Build(scene, content.Material, rtxShaders, content.Read);

        // The hits: random points on the instances of the material, seen head-on from 1 unit away.
        var instances = Enumerable.Range(0, scene.Instances.Count)
            .Where(i => scene.Instances[i].Material.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) && rtx.ShaderOf(i) != null).ToList();
        Assert.NotEmpty(instances);
        var shader = rtx.ShaderOf(instances[0])!;
        Assert.All(instances, i => Assert.Same(shader, rtx.ShaderOf(i)));
        output.WriteLine($"{instances.Count} instances of {scene.Instances[instances[0]].Material.Name}, hit group {shader.Name} combo {shader.Combo}");
        var random = new Random(7);
        var hits = new List<(int Instance, int Primitive, Vector2 Bary, Vector3 Origin, Vector3 Dir)>();
        while (hits.Count < count)
        {
            var i = instances[random.Next(instances.Count)];
            var inst = scene.Instances[i];
            var mesh = scene.Meshes[inst.Mesh];
            var t = random.Next(mesh.Indices.Length / 3);
            float u = random.NextSingle(), v = random.NextSingle();
            if (u + v >= 1f)
                (u, v) = (1f - u, 1f - v);
            var p0 = Vector3.Transform(mesh.Positions[mesh.Indices[t * 3]], inst.ObjectToWorld);
            var p1 = Vector3.Transform(mesh.Positions[mesh.Indices[t * 3 + 1]], inst.ObjectToWorld);
            var p2 = Vector3.Transform(mesh.Positions[mesh.Indices[t * 3 + 2]], inst.ObjectToWorld);
            var face = Vector3.Cross(p1 - p0, p2 - p0);
            if (face.LengthSquared() == 0f)
                continue;
            var dir = -Vector3.Normalize(face);
            var at = p0 * (1f - u - v) + p1 * u + p2 * v;
            hits.Add((i, t, new Vector2(u, v), at - dir, dir));
        }

        // CPU: the transpiled hit group.
        var ctx = rtx.NewContext();
        var cpu = new uint[hits.Count * 32];
        for (var k = 0; k < hits.Count; k++)
        {
            var h = hits[k];
            var inst = scene.Instances[h.Instance];
            ctx.Payload = new Payload_t(new vec3(0f), default, default, 1f, 0.5f, h.Instance, false,
                new MaterialDataMinimal_t(default, 0f, new vec3(1f, 0f, 1f), 1f, default, GL.f16(0.040008545f), 0u, default));
            ctx.Ignored = false;
            ctx.InstanceCustomIndex = inst.Mesh;
            ctx.InstanceID = h.Instance;
            ctx.PrimitiveID = h.Primitive;
            ctx.ObjectToWorld = RtxScene.Mat(inst.ObjectToWorld);
            ctx.WorldToObject = RtxScene.Mat(inst.WorldToObject);
            ctx.RayTmax = 1f;
            ctx.HitKind = 0xFEu;
            ctx.Barycentrics = new vec2(h.Bary.X, h.Bary.Y);
            ctx.WorldRayOrigin = new vec3(h.Origin.X, h.Origin.Y, h.Origin.Z);
            ctx.WorldRayDirection = new vec3(h.Dir.X, h.Dir.Y, h.Dir.Z);
            shader.ClosestHit(ctx);
            Pack(ctx.Payload, ctx.Ignored, cpu.AsSpan(k * 32, 32));
        }

        // GPU: Valve's GLSL as compute.
        var glslPath = Path.Combine(Path.GetTempPath(), $"hitoracle_{shader.Name}_{shader.Combo}.comp");
        var python = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("python",
            [Path.Combine(RepoRoot(), "tools", "re", "hit_to_compute.py"), shader.Name, shader.Combo.ToString(System.Globalization.CultureInfo.InvariantCulture), glslPath])
            { RedirectStandardOutput = true })!;
        python.WaitForExit();
        Assert.Equal(0, python.ExitCode);
        var spirv = ComputeOracle.Compile(File.ReadAllText(glslPath), Path.GetFileName(glslPath));
        using var gpu = new GpuDevice(rayTracingShaderFeatures: true);
        output.WriteLine($"GPU {gpu.Name}");
        using var oracle = new ComputeOracle(gpu, spirv);
        var images = new List<GpuDevice.GpuImage>();
        foreach (var source in rtx.TextureSources)
        {
            if (source.Path == null)
            {
                var image = gpu.CreateImage(1, 1, 1, Silk.NET.Vulkan.Format.R32G32B32A32Sfloat, Silk.NET.Vulkan.ImageUsageFlags.SampledBit | Silk.NET.Vulkan.ImageUsageFlags.TransferDstBit);
                var texel = new byte[16];
                for (var c = 0; c < 4; c++)
                    BinaryPrimitives.WriteSingleLittleEndian(texel.AsSpan(c * 4), c switch { 0 => source.Constant.x, 1 => source.Constant.y, 2 => source.Constant.z, _ => source.Constant.w });
                UploadOne(gpu, image, texel);
                images.Add(image);
                continue;
            }
            using var res = new ValveResourceFormat.Resource();
            res.Read(new MemoryStream(content.Read(source.Path + "_c")!));
            images.Add(GpuTextures.Upload(gpu, (Texture)res.DataBlock!, source.Srgb, RtxScene.TextureCap));
        }
        var hitContext = rtx.NewContext();
        var samplers = hitContext.Samplers.Select(s => oracle.CreateSampler(s.AddressU, s.AddressV, s.Linear, s.Mipmaps)).ToArray();
        var input = new byte[hits.Count * 224];
        for (var k = 0; k < hits.Count; k++)
        {
            var h = hits[k];
            var inst = scene.Instances[h.Instance];
            var w = input.AsSpan(k * 224, 224);
            var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(w);
            var ui = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(w);
            (ui[0], ui[1], ui[2], ui[3]) = ((uint)inst.Mesh, (uint)h.Instance, (uint)h.Primitive, 0xFEu);
            (f[4], f[5], f[6], f[7]) = (h.Bary.X, h.Bary.Y, 1f, 0f);
            Columns(RtxScene.Mat(inst.ObjectToWorld), f[8..24]);
            Columns(RtxScene.Mat(inst.WorldToObject), f[24..40]);
            (f[40], f[41], f[42]) = (h.Origin.X, h.Origin.Y, h.Origin.Z);
            (f[44], f[45], f[46]) = (h.Dir.X, h.Dir.Y, h.Dir.Z);
            f[51] = 1f;                                   // flHitT
            f[52] = 0.5f;                                 // flOpacityRng
            ui[53] = (uint)h.Instance;                    // nInstance
        }
        byte[] Bytes<T>(T[] a) where T : struct => System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()).ToArray();
        var geometries = hitContext.Geometries.SelectMany(g => GeometryBytes(g)).ToArray();
        var result = oracle.Run((uint)hits.Count,
            [
                new ComputeOracle.Buffers(0, 35, [Bytes(hitContext.Instances)]),
                new ComputeOracle.Buffers(0, 34, [Bytes(hitContext.MatU)]),
                new ComputeOracle.Buffers(0, 31, [geometries]),
                new ComputeOracle.Buffers(0, 33, [.. hitContext.Indices.Select(Bytes)]),
                new ComputeOracle.Buffers(0, 32, [.. hitContext.VerticesU.Select(Bytes)]),
                new ComputeOracle.Buffers(0, 60, [input]),
            ],
            [new ComputeOracle.Images(1, 46, [.. images.Select(i => i.View)])],
            [new ComputeOracle.Samplers(1, 29, samplers)],
            [new ComputeOracle.Uniform(2, 0, new byte[1024])],
            (0, 61, (ulong)hits.Count * 32 * 4));
        var gpuWords = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(result).ToArray();

        // Compare field by field.
        var differ = new int[32];
        var maxRel = new double[32];
        var firstDiffer = new int[32];
        Array.Fill(firstDiffer, -1);
        for (var k = 0; k < hits.Count; k++)
            for (var j = 0; j < 32; j++)
            {
                uint a = cpu[k * 32 + j], b = gpuWords[k * 32 + j];
                if (a == b)
                    continue;
                differ[j]++;
                if (firstDiffer[j] < 0)
                    firstDiffer[j] = k;
                if (j is not (11 or 12 or 25 or 30 or 31))
                {
                    double x = BitConverter.UInt32BitsToSingle(a), y = BitConverter.UInt32BitsToSingle(b);
                    maxRel[j] = Math.Max(maxRel[j], Math.Abs(x - y) / Math.Max(1e-6, Math.Abs(y)));
                }
            }
        for (var j = 0; j < 31; j++)
        {
            var line = $"{Fields[j],-14} differ {differ[j],6} of {hits.Count}";
            if (differ[j] > 0)
            {
                var k = firstDiffer[j];
                line += j is 11 or 12 or 25 or 30
                    ? $", first hit {k}: cpu {cpu[k * 32 + j]:x8} gpu {gpuWords[k * 32 + j]:x8}"
                    : $", max rel {maxRel[j]:G3}, first hit {k}: cpu {BitConverter.UInt32BitsToSingle(cpu[k * 32 + j]):G9} gpu {BitConverter.UInt32BitsToSingle(gpuWords[k * 32 + j]):G9}";
            }
            output.WriteLine(line);
        }
        if (Environment.GetEnvironmentVariable("HITORACLE_TEXTURES") == "1")
            ProbeTextures(gpu, rtx, hitContext, images, samplers);
        foreach (var s in samplers)
            oracle.DestroySampler(s);
        foreach (var i in images)
            unsafe
            {
                gpu.Vk.DestroyImageView(gpu.Device, i.View, null);
                gpu.Vk.DestroyImage(gpu.Device, i.Image, null);
                gpu.Vk.FreeMemory(gpu.Device, i.Memory, null);
            }
    }

    private const string TextureProbe = """
        #version 460
        #extension GL_EXT_nonuniform_qualifier : require
        layout(local_size_x = 64) in;
        struct Query { vec4 uvLodMode; uvec4 texSamplerXY; };
        layout(set = 0, binding = 60, std430) readonly buffer Queries { Query q[]; } g_Q;
        layout(set = 0, binding = 61, std430) buffer Results { vec4 r[]; } g_R;
        layout(set = 1, binding = 46) uniform texture2D T[];
        layout(set = 1, binding = 29) uniform sampler S[];
        void main()
        {
            uint i = gl_GlobalInvocationID.x;
            if (i >= uint(g_Q.q.length()))
                return;
            Query q = g_Q.q[i];
            uint t = q.texSamplerXY.x;
            if (q.uvLodMode.w == 0.0)
                g_R.r[i] = texelFetch(sampler2D(T[nonuniformEXT(t)], S[nonuniformEXT(q.texSamplerXY.y)]), ivec2(q.texSamplerXY.zw), 0);
            else
                g_R.r[i] = textureLod(sampler2D(T[nonuniformEXT(t)], S[nonuniformEXT(q.texSamplerXY.y)]), q.uvLodMode.xy, q.uvLodMode.z);
        }
        """;

    /// <summary>
    /// Per bindless texture: texelFetch of 4096 texels (the decode) and textureLod at mip 0 of 4096
    /// random uvs through a linear wrap sampler (the filter), the GPU against HitContext's.
    /// </summary>
    private void ProbeTextures(GpuDevice gpu, RtxScene rtx, HitContext ctx, List<GpuDevice.GpuImage> images, Silk.NET.Vulkan.Sampler[] samplers)
    {
        using var probe = new ComputeOracle(gpu, ComputeOracle.Compile(TextureProbe, "textureprobe.comp"));
        var linear = Array.FindIndex(ctx.Samplers, s => s.Linear && s.AddressU == 0 && s.AddressV == 0);
        Assert.True(linear >= 0, "no linear wrap sampler in the scene");
        var random = new Random(3);
        // HITORACLE_DUMPTEX=<index>: every texel of that texture's resident top mip through texelFetch,
        // as float4 rows, beside the mip's stored bytes (for fitting the GPU's decode).
        if (int.TryParse(Environment.GetEnvironmentVariable("HITORACLE_DUMPTEX"), out var dumpIndex) && rtx.TextureSources[dumpIndex].Path is { } dumpPath)
        {
            var tex = ctx.Textures[dumpIndex];
            var all = tex.Width * tex.Height;
            var q = new byte[all * 32];
            for (var k = 0; k < all; k++)
            {
                var w = q.AsSpan(k * 32, 32);
                BinaryPrimitives.WriteUInt32LittleEndian(w[16..], (uint)dumpIndex);
                BinaryPrimitives.WriteUInt32LittleEndian(w[20..], (uint)linear);
                BinaryPrimitives.WriteInt32LittleEndian(w[24..], k % tex.Width);
                BinaryPrimitives.WriteInt32LittleEndian(w[28..], k / tex.Width);
            }
            var fetched = probe.Run((uint)all, [new ComputeOracle.Buffers(0, 60, [q])],
                [new ComputeOracle.Images(1, 46, [.. images.Select(i => i.View)])], [new ComputeOracle.Samplers(1, 29, samplers)], [], (0, 61, (ulong)all * 16));
            var outDir = Path.Combine(Path.GetTempPath(), "hitoracle_tex");
            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, $"tex{dumpIndex}_gpu.bin"), fetched);
            using var res = new ValveResourceFormat.Resource();
            res.Read(new MemoryStream(rtx.ContentRead!(dumpPath + "_c")!));
            var vtex = (Texture)res.DataBlock!;
            var level = RtxScene.ResidentLevel(vtex.Width, vtex.Height, Math.Max(1, (int)vtex.NumMipLevels));
            var size = vtex.GetEveryMipLevelMetrics().First(m => m.Level == level).BufferSize;
            var data = new byte[size];
            vtex.ReadTextureMipLevel(data, (uint)level);
            File.WriteAllBytes(Path.Combine(outDir, $"tex{dumpIndex}_blocks.bin"), data);
            // and 65536 bilinear samples at random uvs (uv pairs, then the float4 results)
            const int samples = 65536;
            var uv = new float[samples * 2];
            var sq = new byte[samples * 32];
            for (var k = 0; k < samples; k++)
            {
                uv[k * 2] = random.NextSingle() * 3f - 1f;
                uv[k * 2 + 1] = random.NextSingle() * 3f - 1f;
                var w = sq.AsSpan(k * 32, 32);
                BinaryPrimitives.WriteSingleLittleEndian(w, uv[k * 2]);
                BinaryPrimitives.WriteSingleLittleEndian(w[4..], uv[k * 2 + 1]);
                BinaryPrimitives.WriteSingleLittleEndian(w[12..], 1f);
                BinaryPrimitives.WriteUInt32LittleEndian(w[16..], (uint)dumpIndex);
                BinaryPrimitives.WriteUInt32LittleEndian(w[20..], (uint)linear);
            }
            var filtered = probe.Run((uint)samples, [new ComputeOracle.Buffers(0, 60, [sq])],
                [new ComputeOracle.Images(1, 46, [.. images.Select(i => i.View)])], [new ComputeOracle.Samplers(1, 29, samplers)], [], (0, 61, (ulong)samples * 16));
            File.WriteAllBytes(Path.Combine(outDir, $"tex{dumpIndex}_uv.bin"), System.Runtime.InteropServices.MemoryMarshal.AsBytes(uv.AsSpan()).ToArray());
            File.WriteAllBytes(Path.Combine(outDir, $"tex{dumpIndex}_bilinear.bin"), filtered);
            output.WriteLine($"dumped texture {dumpIndex} {dumpPath}: {vtex.Format} level {level} {tex.Width}x{tex.Height} srgb {rtx.TextureSources[dumpIndex].Srgb}");
        }
        for (var t = 0; t < rtx.TextureSources.Count; t++)
        {
            var source = rtx.TextureSources[t];
            if (source.Path == null)
                continue;
            var tex = ctx.Textures[t];
            const int per = 4096;
            var queries = new byte[per * 2 * 32];
            var coords = new (int X, int Y, float U, float V)[per];
            for (var k = 0; k < per; k++)
            {
                coords[k] = (random.Next(tex.Width), random.Next(tex.Height), random.NextSingle() * 4f - 2f, random.NextSingle() * 4f - 2f);
                for (var mode = 0; mode < 2; mode++)
                {
                    var w = queries.AsSpan((mode * per + k) * 32, 32);
                    BinaryPrimitives.WriteSingleLittleEndian(w, coords[k].U);
                    BinaryPrimitives.WriteSingleLittleEndian(w[4..], coords[k].V);
                    BinaryPrimitives.WriteSingleLittleEndian(w[12..], mode);
                    BinaryPrimitives.WriteUInt32LittleEndian(w[16..], (uint)t);
                    BinaryPrimitives.WriteUInt32LittleEndian(w[20..], (uint)linear);
                    BinaryPrimitives.WriteInt32LittleEndian(w[24..], coords[k].X);
                    BinaryPrimitives.WriteInt32LittleEndian(w[28..], coords[k].Y);
                }
            }
            var bytes = probe.Run((uint)(per * 2), [new ComputeOracle.Buffers(0, 60, [queries])],
                [new ComputeOracle.Images(1, 46, [.. images.Select(i => i.View)])], [new ComputeOracle.Samplers(1, 29, samplers)], [],
                (0, 61, (ulong)per * 2 * 16));
            var r = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes);
            for (var mode = 0; mode < 2; mode++)
            {
                double maxAbs = 0;
                var differ = 0;
                var example = "";
                for (var k = 0; k < per; k++)
                {
                    var c = coords[k];
                    var cpu = mode == 0 ? tex.Mips[0][c.Y * tex.Width + c.X] : ctx.TextureLod(t, linear, new vec2(c.U, c.V), 0f);
                    var g = r.Slice((mode * per + k) * 4, 4);
                    var d = Math.Max(Math.Max(Math.Abs(cpu.x - g[0]), Math.Abs(cpu.y - g[1])), Math.Max(Math.Abs(cpu.z - g[2]), Math.Abs(cpu.w - g[3])));
                    if (d > 0)
                    {
                        differ++;
                        if (d > maxAbs)
                            example = $"cpu ({cpu.x:G6} {cpu.y:G6} {cpu.z:G6} {cpu.w:G6}) gpu ({g[0]:G6} {g[1]:G6} {g[2]:G6} {g[3]:G6})";
                    }
                    maxAbs = Math.Max(maxAbs, d);
                }
                output.WriteLine($"  texture {t} {Path.GetFileName(source.Path)} {tex.Width}x{tex.Height}{(source.Srgb ? " srgb" : "")} {(mode == 0 ? "fetch" : "bilinear")}: {differ} of {per} differ, max {maxAbs:G4} {example}");
            }
        }
    }

    /// <summary>
    /// The GPU's BC1 decode, measured (<c>HITORACLE_BC1=1</c>): synthetic blocks enumerate every
    /// endpoint pair of one channel (red 5 bits, green 6, blue 5; the other channels equal in both
    /// endpoints, so the mode follows the enumerated channel) with the four indices on each row,
    /// uploaded as BC1 sRGB and BC1 UNORM, plus an RGBA8 sRGB texture of the 256 codes; every texel
    /// is fetched and written to %TEMP%/hitoracle_tex (bc1_blocks.bin, bc1_srgb.bin, bc1_unorm.bin, srgb8.bin).
    /// </summary>
    [Fact]
    public void Bc1Decode()
    {
        if (Environment.GetEnvironmentVariable("HITORACLE_BC1") != "1")
            return;
        var blocks = new List<byte>();
        void Block(int r0, int g0, int b0, int r1, int g1, int b1)
        {
            ushort c0 = (ushort)(r0 << 11 | g0 << 5 | b0), c1 = (ushort)(r1 << 11 | g1 << 5 | b1);
            blocks.AddRange(BitConverter.GetBytes(c0));
            blocks.AddRange(BitConverter.GetBytes(c1));
            blocks.AddRange(BitConverter.GetBytes(0xE4E4E4E4u)); // each row 0, 1, 2, 3
        }
        for (var a = 0; a < 32; a++)
            for (var b = 0; b < 32; b++)
                Block(a, 7, 9, b, 7, 9);
        for (var a = 0; a < 64; a++)
            for (var b = 0; b < 64; b++)
                Block(9, a, 7, 9, b, 7);
        for (var a = 0; a < 32; a++)
            for (var b = 0; b < 32; b++)
                Block(9, 7, a, 9, 7, b);
        const int blocksWide = 256;
        var blockRows = (blocks.Count / 8 + blocksWide - 1) / blocksWide;
        while (blocks.Count < blocksWide * blockRows * 8)
            Block(0, 0, 0, 0, 0, 0);
        var data = blocks.ToArray();
        using var gpu = new GpuDevice(rayTracingShaderFeatures: true);
        using var probe = new ComputeOracle(gpu, ComputeOracle.Compile(TextureProbe, "textureprobe.comp"));
        var outDir = Path.Combine(Path.GetTempPath(), "hitoracle_tex");
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, "bc1_blocks.bin"), data);
        var sampler = probe.CreateSampler(0, 0, false, false);
        int w = blocksWide * 4, h = blockRows * 4;
        foreach (var (name, format, width, height, bytes) in new[]
        {
            ("bc1_srgb", Silk.NET.Vulkan.Format.BC1RgbaSrgbBlock, w, h, data),
            ("bc1_unorm", Silk.NET.Vulkan.Format.BC1RgbaUnormBlock, w, h, data),
            ("srgb8", Silk.NET.Vulkan.Format.R8G8B8A8Srgb, 256, 1, Enumerable.Range(0, 256).SelectMany(v => new[] { (byte)v, (byte)v, (byte)v, (byte)255 }).ToArray()),
        })
        {
            var image = gpu.CreateImage((uint)width, (uint)height, 1, format, Silk.NET.Vulkan.ImageUsageFlags.SampledBit | Silk.NET.Vulkan.ImageUsageFlags.TransferDstBit);
            UploadRaw(gpu, image, bytes, width, height);
            var count = width * height;
            var q = new byte[count * 32];
            for (var k = 0; k < count; k++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(q.AsSpan(k * 32 + 24), k % width);
                BinaryPrimitives.WriteInt32LittleEndian(q.AsSpan(k * 32 + 28), k / width);
            }
            var fetched = probe.Run((uint)count, [new ComputeOracle.Buffers(0, 60, [q])], [new ComputeOracle.Images(1, 46, [image.View])],
                [new ComputeOracle.Samplers(1, 29, [sampler])], [], (0, 61, (ulong)count * 16));
            File.WriteAllBytes(Path.Combine(outDir, name + ".bin"), fetched);
            output.WriteLine($"{name}: {width}x{height}");
            unsafe
            {
                gpu.Vk.DestroyImageView(gpu.Device, image.View, null);
                gpu.Vk.DestroyImage(gpu.Device, image.Image, null);
                gpu.Vk.FreeMemory(gpu.Device, image.Memory, null);
            }
        }
        probe.DestroySampler(sampler);
    }

    private static unsafe void UploadRaw(GpuDevice gpu, GpuDevice.GpuImage image, byte[] data, int width, int height)
    {
        var staging = gpu.Upload(data, Silk.NET.Vulkan.BufferUsageFlags.TransferSrcBit);
        gpu.Submit(cmd =>
        {
            gpu.Transition(cmd, image, Silk.NET.Vulkan.ImageLayout.Undefined, Silk.NET.Vulkan.ImageLayout.TransferDstOptimal);
            var region = new Silk.NET.Vulkan.BufferImageCopy(0, 0, 0, new Silk.NET.Vulkan.ImageSubresourceLayers(Silk.NET.Vulkan.ImageAspectFlags.ColorBit, 0, 0, 1),
                default, new Silk.NET.Vulkan.Extent3D((uint)width, (uint)height, 1));
            gpu.Vk.CmdCopyBufferToImage(cmd, staging.Buffer, image.Image, Silk.NET.Vulkan.ImageLayout.TransferDstOptimal, 1, &region);
            gpu.Transition(cmd, image, Silk.NET.Vulkan.ImageLayout.TransferDstOptimal, Silk.NET.Vulkan.ImageLayout.ShaderReadOnlyOptimal);
        });
        gpu.Destroy(staging);
    }

    private static void Pack(Payload_t p, bool ignored, Span<uint> w)
    {
        static uint F(float x) => BitConverter.SingleToUInt32Bits(x);
        (w[0], w[1], w[2]) = (F(p.vSmoothPositionWs.x), F(p.vSmoothPositionWs.y), F(p.vSmoothPositionWs.z));
        (w[3], w[4], w[5]) = (F(p.vFaceNormalWs.x), F(p.vFaceNormalWs.y), F(p.vFaceNormalWs.z));
        (w[6], w[7], w[8]) = (F(p.vInterpolatedNormalWs.x), F(p.vInterpolatedNormalWs.y), F(p.vInterpolatedNormalWs.z));
        (w[9], w[10], w[11], w[12]) = (F(p.flHitT), F(p.flOpacityRng), (uint)p.nInstance, p.bFlippedNormal ? 1u : 0u);
        var m = p.material;
        (w[13], w[14], w[15], w[16]) = (F(m.vNormalWs.x), F(m.vNormalWs.y), F(m.vNormalWs.z), F(m.flMetalness));
        (w[17], w[18], w[19], w[20]) = (F(m.vAlbedo.x), F(m.vAlbedo.y), F(m.vAlbedo.z), F(m.flRoughness));
        (w[21], w[22], w[23], w[24]) = (F(m.vEmissive.x), F(m.vEmissive.y), F(m.vEmissive.z), F(m.flReflectance));
        w[25] = m.nMaterialTypeAndFlags;
        (w[26], w[27], w[28], w[29]) = (F(m.vMaterialBits.x), F(m.vMaterialBits.y), F(m.vMaterialBits.z), F(m.vMaterialBits.w));
        w[30] = ignored ? 1u : 0u;
    }

    private static void Columns(mat4x3 m, Span<float> f)
    {
        (f[0], f[1], f[2]) = (m.c0.x, m.c0.y, m.c0.z);
        (f[4], f[5], f[6]) = (m.c1.x, m.c1.y, m.c1.z);
        (f[8], f[9], f[10]) = (m.c2.x, m.c2.y, m.c2.z);
        (f[12], f[13], f[14]) = (m.c3.x, m.c3.y, m.c3.z);
    }

    // GeometryInfo_t_natural as std430: five ivec4, then the texcoord and colour field arrays (8 ivec4 each).
    private static byte[] GeometryBytes(GeometryInfo_t_natural g)
    {
        var all = new List<ivec4> { g.m_nVertexAndIndexBuffers, g.m_nBufferBindOffsetBytes, g.m_nBufferStrideInBytes, g.m_vNormalFieldInfo, g.m_vTangentFieldInfo };
        all.AddRange(g.m_vTexCoordFieldInfo.data);
        all.AddRange(g.m_vColorFieldInfo.data);
        var bytes = new byte[all.Count * 16];
        for (var i = 0; i < all.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 16), all[i].x);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 16 + 4), all[i].y);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 16 + 8), all[i].z);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 16 + 12), all[i].w);
        }
        return bytes;
    }

    private static unsafe void UploadOne(GpuDevice gpu, GpuDevice.GpuImage image, byte[] data)
    {
        var staging = gpu.Upload(data, Silk.NET.Vulkan.BufferUsageFlags.TransferSrcBit);
        gpu.Submit(cmd =>
        {
            gpu.Transition(cmd, image, Silk.NET.Vulkan.ImageLayout.Undefined, Silk.NET.Vulkan.ImageLayout.TransferDstOptimal);
            var region = new Silk.NET.Vulkan.BufferImageCopy(0, 0, 0, new Silk.NET.Vulkan.ImageSubresourceLayers(Silk.NET.Vulkan.ImageAspectFlags.ColorBit, 0, 0, 1),
                default, new Silk.NET.Vulkan.Extent3D(1, 1, 1));
            gpu.Vk.CmdCopyBufferToImage(cmd, staging.Buffer, image.Image, Silk.NET.Vulkan.ImageLayout.TransferDstOptimal, 1, &region);
            gpu.Transition(cmd, image, Silk.NET.Vulkan.ImageLayout.TransferDstOptimal, Silk.NET.Vulkan.ImageLayout.ShaderReadOnlyOptimal);
        });
        gpu.Destroy(staging);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Source2Compiler.slnx")))
            d = d.Parent;
        return d!.FullName;
    }
}
