using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Silk.NET.Vulkan;
using Source2.Compiler.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The cubemap face render, draw by draw, against the game's RenderDoc capture
/// (tools/cubehost/rdc_replay.py with RDC_DUMP=1: each draw's state, the contents
/// it reads and its targets after it). <c>CUBEREPLAY=&lt;dump dir&gt;</c>,
/// <c>CUBEREPLAY_SPIRV=&lt;rdc_spirv.py output&gt;</c>: every draw runs on our own
/// device through <see cref="SceneDrawRunner"/> with Valve's SPIR-V, from the
/// captured "before" targets, and its targets must equal the capture's "after"
/// bit for bit. This checks the GPU state is reproduced (pipelines, bindings,
/// samplers, vertex layouts) before our own inputs replace the captured ones.
/// </summary>
public sealed class CubemapFaceReplayTests(ITestOutputHelper output)
{
    [Fact]
    public void DrawsEqualTheCapture()
    {
        if (Environment.GetEnvironmentVariable("CUBEREPLAY") is not { Length: > 0 } dir
            || Environment.GetEnvironmentVariable("CUBEREPLAY_SPIRV") is not { Length: > 0 } spirvDir)
            return;
        var only = Environment.GetEnvironmentVariable("CUBEREPLAY_EIDS")?.Split(',').Select(int.Parse).ToHashSet();
        var resources = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "resources.json")))!;
        using var gpu = new GpuDevice(rayTracingShaderFeatures: Environment.GetEnvironmentVariable("CUBEREPLAY_BASIC_DEVICE") != "1");
        var runner = new SceneDrawRunner(gpu);
        using var replay = new Replay(dir, spirvDir, resources, gpu, runner);
        var failures = new List<string>();
        foreach (var line in File.ReadLines(Path.Combine(dir, "actions.jsonl")))
        {
            var rec = JsonNode.Parse(line)!;
            if (rec["contents"] is null || (rec["stages"]?["vs"] is null && rec["stages"]?["cs"] is null))
                continue;
            var eid = (int)rec["eid"]!;
            if (only != null && !only.Contains(eid))
                continue;
            var result = rec["stages"]!["cs"] is not null ? replay.RunCompute(rec) : replay.Run(rec);
            output.WriteLine($"{eid} {rec["name"]}: {result}");
            if (!result.StartsWith("equal", StringComparison.Ordinal))
                failures.Add($"{eid}: {result}");
        }
        Assert.Empty(failures);
    }

    /// <summary>
    /// Face 0 of probe01's cube 0 with our constant blocks in place of the captured ones: the per-view blocks
    /// (world, depth range 0 .. 0.95; sky, 1 .. 1), PerViewConstantBufferCsgo_t and
    /// BakedLightingConstantBuffer_t, the transform and instance buffers from <see cref="Cubemaps.CubemapFaceConstants"/>. Every draw that reads them
    /// must still equal the capture.
    /// </summary>
    [Fact]
    public void OurBlocksKeepTheFace()
    {
        if (Environment.GetEnvironmentVariable("CUBEREPLAY") is not { Length: > 0 } dir
            || Environment.GetEnvironmentVariable("CUBEREPLAY_SPIRV") is not { Length: > 0 } spirvDir)
            return;
        var view = Cubemaps.CubemapFaceViews.Face(new System.Numerics.Vector3(512, 96, 112), System.Numerics.Vector3.Zero, 1f, 10000f, 0);
        var fog = new Cubemaps.CubemapFaceConstants.CubeFog(400f, 12000f, 0.5f, 1.2f, new System.Numerics.Vector4(0f, 2000f, 1f, 1f),
            System.Numerics.Matrix4x4.CreateTranslation(512, 112, 80));
        var ours = new Dictionary<string, byte[]>
        {
            ["9cb9e491"] = Cubemaps.CubemapFaceConstants.PerView(view, 1024, 0f, 0.95f),
            ["1b1f6cc0"] = Cubemaps.CubemapFaceConstants.PerView(view, 1024, 1f, 1f),
            ["37c443a8"] = Cubemaps.CubemapFaceConstants.Csgo(view, fog, 4096f),
            ["d831320f"] = Cubemaps.CubemapFaceConstants.BakedLighting(new System.Numerics.Vector2(1.1428397f, 1.1428397f), 4096, 4096, false),
            // Our own slots: two identity transforms, every instance pointing at the second.
            ["40190a9e"] = Cubemaps.CubemapFaceConstants.Transforms([Cubemaps.CubemapFaceConstants.Identity3x4, Cubemaps.CubemapFaceConstants.Identity3x4]),
            ["5708ccca"] = Cubemaps.CubemapFaceConstants.Instances([.. Enumerable.Repeat(1, 63)]),
        };
        // The sun cascades' per-view blocks (face 0: three cascades and the viewmodel one).
        var (sun, _, cameraDirection) = Cubemaps.CubemapShadowCascades.SunDirections(new System.Numerics.Vector3(74.99999f, 180.00002f, 0f));
        var cascades = Cubemaps.CubemapShadowCascades.Fit(view, sun, Cubemaps.CubemapShadowCascades.SplitDistances([75f], 3, 1f, 560f), 1f, 7500f, 7000f, 1280);
        cascades.AddRange(Cubemaps.CubemapShadowCascades.Fit(view, sun, [21f], 0.5f, 1000f, 1500f, 1280));
        string[] cascadeShas = ["c84b0bea", "14ee9dcb", "4db015a3", "f0d466f1"];
        int[][] tiles = [[0, 0], [1280, 0], [2560, 0], [0, 2048]];
        for (var k = 0; k < 4; k++)
            ours[cascadeShas[k]] = Cubemaps.CubemapFaceConstants.CascadePerView(cascades[k], cameraDirection, tiles[k][0], tiles[k][1], 1280, 4352, 5248);
        var resources = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "resources.json")))!;
        using var gpu = new GpuDevice(rayTracingShaderFeatures: Environment.GetEnvironmentVariable("CUBEREPLAY_BASIC_DEVICE") != "1");
        var runner = new SceneDrawRunner(gpu);
        using var replay = new Replay(dir, spirvDir, resources, gpu, runner);
        var users = File.ReadLines(Path.Combine(dir, "actions.jsonl"))
            .Where(l => ours.Keys.Any(k => l.Contains(k, StringComparison.Ordinal))).Select(l => JsonNode.Parse(l)!)
            .Where(r => r["contents"] is not null && r["stages"]?["vs"] is not null).ToList();
        if (replay.Baselines != null)
            foreach (var u in users)
                replay.Run(u);   // this GPU's run with the captured blocks is the reference
        foreach (var (prefix, bytes) in ours)
        {
            var sha = Path.GetFileNameWithoutExtension(Directory.GetFiles(Path.Combine(dir, "blobs"), prefix + "*.bin").Single());
            var theirs = File.ReadAllBytes(Path.Combine(dir, "blobs", sha + ".bin"));
            var same = Enumerable.Range(0, Math.Min(theirs.Length, bytes.Length) / 4).Count(i => BitConverter.ToUInt32(theirs, i * 4) == BitConverter.ToUInt32(bytes, i * 4));
            output.WriteLine($"{prefix}: {same} of {theirs.Length / 4} words the capture's ({bytes.Length} bytes ours, {theirs.Length} theirs)");
            replay.Overrides[sha] = bytes;
        }
        var failures = new List<string>();
        foreach (var rec in users)
        {
            var result = replay.Run(rec);
            output.WriteLine($"{rec["eid"]} {rec["name"]}: {result}");
            if (!result.StartsWith("equal", StringComparison.Ordinal))
                failures.Add($"{rec["eid"]}: {result}");
        }
        Assert.Empty(failures);
    }

    /// <summary>Which instance / transform fields the colour draws read: variants of the two buffers, each replayed.</summary>
    [Fact]
    public void InstanceVariants()
    {
        if (Environment.GetEnvironmentVariable("CUBEREPLAY") is not { Length: > 0 } dir
            || Environment.GetEnvironmentVariable("CUBEREPLAY_SPIRV") is not { Length: > 0 } spirvDir
            || Environment.GetEnvironmentVariable("CUBEREPLAY_INSTANCES") != "1")
            return;
        string Sha(string prefix) => Path.GetFileNameWithoutExtension(Directory.GetFiles(Path.Combine(dir, "blobs"), prefix + "*.bin").Single());
        var (instSha, xfSha) = (Sha("5708ccca"), Sha("40190a9e"));
        var inst = File.ReadAllBytes(Path.Combine(dir, "blobs", instSha + ".bin"));
        var xf = File.ReadAllBytes(Path.Combine(dir, "blobs", xfSha + ".bin"));
        var resources = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "resources.json")))!;
        using var gpu = new GpuDevice(rayTracingShaderFeatures: Environment.GetEnvironmentVariable("CUBEREPLAY_BASIC_DEVICE") != "1");
        var runner = new SceneDrawRunner(gpu);
        using var replay = new Replay(dir, spirvDir, resources, gpu, runner);
        var draws = File.ReadLines(Path.Combine(dir, "actions.jsonl")).Select(l => JsonNode.Parse(l)!)
            .Where(r => r["contents"] is not null && r["stages"]?["vs"] is not null && (int)r["eid"]! >= 354 && (int)r["eid"]! <= 485).ToList();
        void Try(string name, byte[]? i, byte[]? x)
        {
            replay.Overrides.Clear();
            if (i != null) replay.Overrides[instSha] = i;
            if (x != null) replay.Overrides[xfSha] = x;
            var bad = draws.Where(d => !replay.Run(d).StartsWith("equal", StringComparison.Ordinal)).Select(d => (int)d["eid"]!).ToList();
            output.WriteLine($"{name}: {(bad.Count == 0 ? "equal" : "differ at " + string.Join(",", bad))}");
        }
        Try("captured", null, null);
        // Each word of the world records (records 1..63) changed in turn to the null record's value or 0.
        uint[] alt = [0, 0, 0, 0xffffffff, 0xffffffff, 0, 0x3f800000, 0];
        for (var w = 0; w < 8; w++)
        {
            var m = (byte[])inst.Clone();
            for (var r = 1; r < 64; r++)
                if (BitConverter.ToUInt32(m, r * 32 + 4) is 4 or 5)
                    BitConverter.TryWriteBytes(m.AsSpan(r * 32 + w * 4), w == 1 ? (BitConverter.ToUInt32(m, r * 32 + 4) == 4 ? 2u : 3u) : alt[w]);
            var x2 = (byte[])xf.Clone();
            if (w == 1) { xf.AsSpan(192, 96).CopyTo(x2.AsSpan(96)); }   // transforms 4, 5 copied to 2, 3
            Try($"word {w}", m, w == 1 ? x2 : null);
        }
    }

    /// <summary>
    /// <c>CUBEREPLAY_SENSE=&lt;sha1 prefix&gt;</c>: which words of a captured block or buffer the face depends on.
    /// Each non-zero word is zeroed and, separately, has its lowest bit flipped; each all-zero 16-byte row is set
    /// to 1.0s; the actions that read the blob run again and any change to their targets marks the word.
    /// </summary>
    [Fact]
    public void Sensitivity()
    {
        if (Environment.GetEnvironmentVariable("CUBEREPLAY") is not { Length: > 0 } dir
            || Environment.GetEnvironmentVariable("CUBEREPLAY_SPIRV") is not { Length: > 0 } spirvDir
            || Environment.GetEnvironmentVariable("CUBEREPLAY_SENSE") is not { Length: > 0 } prefix)
            return;
        var sha = Directory.GetFiles(Path.Combine(dir, "blobs"), prefix + "*.bin").Select(Path.GetFileNameWithoutExtension).Single()!;
        var data = File.ReadAllBytes(Path.Combine(dir, "blobs", sha + ".bin"));
        var users = File.ReadLines(Path.Combine(dir, "actions.jsonl")).Select(l => JsonNode.Parse(l)!)
            .Where(r => r["contents"] is not null && r["stages"]?["vs"] is not null && r.ToJsonString().Contains(sha, StringComparison.Ordinal)).ToList();
        output.WriteLine($"{sha}: {data.Length} bytes, read by {string.Join(", ", users.Select(u => (int)u["eid"]!))}");
        var resources = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "resources.json")))!;
        using var gpu = new GpuDevice(rayTracingShaderFeatures: Environment.GetEnvironmentVariable("CUBEREPLAY_BASIC_DEVICE") != "1");
        var runner = new SceneDrawRunner(gpu);
        using var replay = new Replay(dir, spirvDir, resources, gpu, runner);
        if (replay.Baselines != null)
            foreach (var u in users)
                replay.Run(u);
        bool Changes(byte[] modified)
        {
            replay.Overrides[sha] = modified;
            var any = users.Any(u => !replay.Run(u).StartsWith("equal", StringComparison.Ordinal));
            replay.Overrides.Remove(sha);
            return any;
        }
        if (Environment.GetEnvironmentVariable("CUBEREPLAY_SENSE_WHOLE") == "1")
        {
            // The whole blob zeroed, and the whole blob set to 0xff bytes.
            output.WriteLine($"zeroed: {(Changes(new byte[data.Length]) ? "changes the face" : "no change")}; all ones: {(Changes(Enumerable.Repeat((byte)0xff, data.Length).ToArray()) ? "changes the face" : "no change")}");
            return;
        }
        var sensitive = new List<string>();
        for (var o = 0; o < data.Length; o += 16)
        {
            if (data.AsSpan(o, Math.Min(16, data.Length - o)).IndexOfAnyExcept((byte)0) < 0)
            {
                var m = (byte[])data.Clone();
                for (var w = o; w < Math.Min(o + 16, data.Length); w += 4)
                    BitConverter.TryWriteBytes(m.AsSpan(w), 1f);
                if (Changes(m))
                    sensitive.Add($"+{o} (zero row)");
                continue;
            }
            for (var w = o; w < Math.Min(o + 16, data.Length); w += 4)
            {
                if (BitConverter.ToUInt32(data, w) == 0)
                    continue;
                var zeroed = (byte[])data.Clone();
                BitConverter.TryWriteBytes(zeroed.AsSpan(w), 0);
                var flipped = (byte[])data.Clone();
                flipped[w] ^= 1;
                var (z, f) = (Changes(zeroed), Changes(flipped));
                if (z || f)
                    sensitive.Add($"+{w} ({BitConverter.ToSingle(data, w):G9} / {BitConverter.ToUInt32(data, w):x8}){(f ? " exact" : "")}");
            }
        }
        output.WriteLine("sensitive: " + string.Join(", ", sensitive));
    }

    /// <summary>A RenderDoc dump turned into <see cref="SceneDrawRunner"/> calls, resources shared by content.</summary>
    private sealed unsafe class Replay(string dir, string spirvDir, JsonNode resources, GpuDevice gpu, SceneDrawRunner runner) : IDisposable
    {
        private readonly Dictionary<string, GpuDevice.GpuBuffer> _buffers = [];
        private readonly Dictionary<string, SceneDrawRunner.SceneImage> _images = [];
        private readonly Dictionary<string, Sampler> _samplers = [];

        /// <summary>
        /// CUBEREPLAY_BLANK: resources uploaded as zeros, to see whether a draw's output depends on them
        /// (an input that changes nothing need not be reproduced).
        /// </summary>
        private static readonly HashSet<string> Blank = [.. (Environment.GetEnvironmentVariable("CUBEREPLAY_BLANK") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)];

        private byte[] Blob(string sha1) => File.ReadAllBytes(Path.Combine(dir, "blobs", sha1 + ".bin"));

        /// <summary>
        /// When set, targets are compared with the first (unperturbed) run of each action on this device instead of the
        /// capture: CUBEREPLAY_BASELINE=1, for sensitivity sweeps on a GPU other than the one that made the capture.
        /// </summary>
        public Dictionary<string, byte[]>? Baselines { get; set; } = Environment.GetEnvironmentVariable("CUBEREPLAY_BASELINE") == "1" ? [] : null;

        /// <summary>Buffer contents to use in place of the captured blob of that hash (constant blocks we build).</summary>
        public Dictionary<string, byte[]> Overrides { get; } = [];
        private readonly List<GpuDevice.GpuBuffer> _overridden = [];

        private GpuDevice.GpuBuffer Buffer(string sha1)
        {
            if (Overrides.TryGetValue(sha1, out var replaced))
            {
                var fresh = gpu.Upload(replaced, BufferUsageFlags.UniformBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit);
                _overridden.Add(fresh);
                return fresh;
            }
            if (!_buffers.TryGetValue(sha1, out var b))
                _buffers[sha1] = b = gpu.Upload(Blob(sha1), BufferUsageFlags.UniformBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit);
            return b;
        }

        public static Format Format(string name) => name switch
        {
            "BC1_SRGB" => Silk.NET.Vulkan.Format.BC1RgbaSrgbBlock,
            "BC1_UNORM" => Silk.NET.Vulkan.Format.BC1RgbaUnormBlock,
            "BC3_SRGB" => Silk.NET.Vulkan.Format.BC3SrgbBlock,
            "BC3_UNORM" => Silk.NET.Vulkan.Format.BC3UnormBlock,
            "BC4_UNORM" => Silk.NET.Vulkan.Format.BC4UnormBlock,
            "BC5_UNORM" => Silk.NET.Vulkan.Format.BC5UnormBlock,
            "BC6_UFLOAT" => Silk.NET.Vulkan.Format.BC6HUfloatBlock,
            "BC7_SRGB" => Silk.NET.Vulkan.Format.BC7SrgbBlock,
            "BC7_UNORM" => Silk.NET.Vulkan.Format.BC7UnormBlock,
            "B8G8R8A8_SRGB" => Silk.NET.Vulkan.Format.B8G8R8A8Srgb,
            "B8G8R8A8_UNORM" => Silk.NET.Vulkan.Format.B8G8R8A8Unorm,
            "R8G8B8A8_SRGB" => Silk.NET.Vulkan.Format.R8G8B8A8Srgb,
            "R8G8B8A8_UNORM" => Silk.NET.Vulkan.Format.R8G8B8A8Unorm,
            "R16G16B16A16_FLOAT" => Silk.NET.Vulkan.Format.R16G16B16A16Sfloat,
            "R32G32B32A32_FLOAT" => Silk.NET.Vulkan.Format.R32G32B32A32Sfloat,
            "R32G32B32_FLOAT" => Silk.NET.Vulkan.Format.R32G32B32Sfloat,
            "R32G32_FLOAT" => Silk.NET.Vulkan.Format.R32G32Sfloat,
            "R32_FLOAT" => Silk.NET.Vulkan.Format.R32Sfloat,
            "R32_UINT" => Silk.NET.Vulkan.Format.R32Uint,
            "R32G32B32A32_UINT" => Silk.NET.Vulkan.Format.R32G32B32A32Uint,
            "R16G16_UNORM" => Silk.NET.Vulkan.Format.R16G16Unorm,
            "R16G16_FLOAT" => Silk.NET.Vulkan.Format.R16G16Sfloat,
            "R8G8B8A8_UINT" => Silk.NET.Vulkan.Format.R8G8B8A8Uint,
            "D16" => Silk.NET.Vulkan.Format.D16Unorm,
            "D24S8" => Silk.NET.Vulkan.Format.D24UnormS8Uint,
            "D32" => Silk.NET.Vulkan.Format.D32Sfloat,
            _ => throw new NotSupportedException(name),
        };

        // RenderDoc's CompareFunction: Never, AlwaysTrue, Less, LessEqual, Greater, GreaterEqual, Equal, NotEqual.
        private static CompareOp Compare(int f) => f switch
        {
            0 => CompareOp.Never, 1 => CompareOp.Always, 2 => CompareOp.Less, 3 => CompareOp.LessOrEqual,
            4 => CompareOp.Greater, 5 => CompareOp.GreaterOrEqual, 6 => CompareOp.Equal, 7 => CompareOp.NotEqual,
            _ => throw new NotSupportedException($"compare {f}"),
        };

        // RenderDoc's AddressMode: Wrap, Mirror, MirrorOnce, ClampEdge, ClampBorder.
        private static SamplerAddressMode Address(int a) => a switch
        {
            0 => SamplerAddressMode.Repeat, 1 => SamplerAddressMode.MirroredRepeat, 2 => SamplerAddressMode.MirrorClampToEdge,
            3 => SamplerAddressMode.ClampToEdge, 4 => SamplerAddressMode.ClampToBorder, _ => throw new NotSupportedException($"address {a}"),
        };

        private static StencilOpState Stencil(JsonNode s) => new(
            (StencilOp)(int)s["failOperation"]!, (StencilOp)(int)s["passOperation"]!, (StencilOp)(int)s["depthFailOperation"]!,
            Compare((int)s["function"]!), (uint)s["compareMask"]!, (uint)s["writeMask"]!, (uint)s["reference"]!);

        private Sampler SamplerFor(JsonNode s)
        {
            var key = s.ToJsonString();
            if (_samplers.TryGetValue(key, out var made))
                return made;
            var f = s["filter"]!;
            // RenderDoc's FilterMode: NoFilter, Point, Linear, Cubic, Anisotropic; FilterFunction: Normal, Comparison, Min, Max.
            Filter Mode(int m) => m == 1 ? Filter.Nearest : Filter.Linear;
            var anisotropic = (int)f["minify"]! == 4 || (int)f["magnify"]! == 4;
            var border = s["borderColorValue"]!["floatValue"]!.AsArray().Select(v => (float)v!).ToArray();
            var info = new SamplerCreateInfo
            {
                MagFilter = Mode((int)f["magnify"]!),
                MinFilter = Mode((int)f["minify"]!),
                MipmapMode = (int)f["mip"]! == 1 ? SamplerMipmapMode.Nearest : SamplerMipmapMode.Linear,
                AddressModeU = Address((int)s["addressU"]!),
                AddressModeV = Address((int)s["addressV"]!),
                AddressModeW = Address((int)s["addressW"]!),
                MipLodBias = (float)s["mipBias"]!,
                AnisotropyEnable = anisotropic,
                MaxAnisotropy = anisotropic ? (float)s["maxAnisotropy"]! : 1f,
                CompareEnable = (int)f["filter"]! == 1,
                CompareOp = Compare((int)s["compareFunction"]!),
                MinLod = (float)s["minLOD"]!,
                MaxLod = (float)s["maxLOD"]!,
                BorderColor = border[3] == 0 ? BorderColor.FloatTransparentBlack : border[0] == 0 ? BorderColor.FloatOpaqueBlack : BorderColor.FloatOpaqueWhite,
            };
            return _samplers[key] = runner.CreateSampler(info);
        }

        private static ImageViewType ViewType(string dim) => dim switch
        {
            "2D" => ImageViewType.Type2D, "2DArray" => ImageViewType.Type2DArray, "3D" => ImageViewType.Type3D,
            "Cube" => ImageViewType.TypeCube, "CubeArray" => ImageViewType.TypeCubeArray, "1D" => ImageViewType.Type1D,
            _ => throw new NotSupportedException(dim),
        };

        /// <summary>A texture's whole contents (every subresource dumped) as an image with the shader's view type.</summary>
        private SceneDrawRunner.SceneImage Image(string name, JsonArray subs, Format format, ImageViewType viewType, ImageUsageFlags usage = 0)
        {
            var key = $"{name}|{format}|{viewType}|{string.Join(",", subs.Select(s => (string)s!["sha1"]!))}";
            if (_images.TryGetValue(key, out var image))
                return image;
            var t = resources["textures"]![name]!;
            var mips = (uint)(int)t["mips"]!;
            var layers = (uint)Math.Max(1, (int)t["arraysize"]!);
            image = runner.CreateImage(format, (uint)(int)t["width"]!, (uint)(int)t["height"]!, (uint)Math.Max(1, (int)t["depth"]!), mips, layers, viewType, usage);
            foreach (var s in subs)
            {
                var data = Blob((string)s!["sha1"]!);
                if (Blank.Contains(name))
                    data = new byte[data.Length];
                runner.Write(image, (uint)(int)s["mip"]!, (uint)(int)s["slice"]!, data);
            }
            return _images[key] = image;
        }

        private static string Sha1(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));

        public string Run(JsonNode rec)
        {
            var before = rec["contents"]!["before"]!;
            var after = rec["contents"]!["after"]!;
            var vs = rec["stages"]!["vs"]!;
            var ps = rec["stages"]!["ps"];
            byte[] Spirv(JsonNode stage) => File.ReadAllBytes(Path.Combine(spirvDir, $"{int.Parse(((string)stage["shader"]!).Split(' ')[^1]):x}.spv"));
            var vsCode = Spirv(vs);
            var psCode = ps != null ? Spirv(ps) : null;
            var reflections = new[] { SpirvReflection.Parse(vsCode) }.Concat(psCode != null ? [SpirvReflection.Parse(psCode)] : Array.Empty<SpirvReflection>()).ToArray();
            SpirvReflection.Resource Reflected(int set, int binding) => reflections.SelectMany(r => r.Resources).First(r => r.Set == set && r.Binding == binding);

            var bindings = new SceneDrawRunner.Bindings();
            foreach (var stage in new[] { vs, ps }.Where(s => s != null))
            {
                foreach (var cb in stage!["constantBlocks"]!.AsArray())
                {
                    var block = cb!["block"]!;
                    var bytes = cb["bytes"]!;
                    bindings.Buffer((int)block["fixedBindSetOrSpace"]!, (int)block["fixedBindNumber"]!, (uint)(int)cb["access"]!["arrayElement"]!,
                        Buffer((string)bytes["sha1"]!), 0, (ulong)(long)bytes["size"]!);
                }
                foreach (var rw in stage["readWrite"]!.AsArray().Concat(stage["readOnly"]!.AsArray()))
                {
                    var bind = rw!["bind"]!;
                    var d = rw["descriptor"]!;
                    var name = (string)d["resource"]!;
                    var (set, binding, element) = ((int)bind["set"]!, (int)bind["binding"]!, (uint)(int)rw["access"]!["arrayElement"]!);
                    if (before[name] is JsonArray subs)
                    {
                        var image = Image(name, subs, Format((string)d["format"]!), ViewType(Reflected(set, binding).Dim));
                        bindings.Image(set, binding, element, image.View);
                    }
                    else
                    {
                        var size = (ulong)d["byteSize"]!;
                        bindings.Buffer(set, binding, element, Buffer((string)before[name]!["sha1"]!), (ulong)d["byteOffset"]!, size >= (1UL << 40) ? Vk.WholeSize : size);
                    }
                }
                foreach (var smp in stage["samplers"]!.AsArray())
                    bindings.Sampler((int)smp!["bind"]!["set"]!, (int)smp["bind"]!["binding"]!, (uint)(int)smp["access"]!["arrayElement"]!, SamplerFor(smp["sampler"]!));
            }

            var vi = rec["vertexInput"]!;
            var vbs = vi["vertexBuffers"]!.AsArray();
            var perInstance = vi["bindings"]!.AsArray().ToDictionary(b => (int)b!["vertexBufferBinding"]!, b => (bool)b!["perInstance"]!);
            var rast = rec["rasterizer"]!;
            var ds = rec["depthStencil"]!;
            var vp = rec["viewportScissor"]!["viewportScissors"]![0]!;
            var blends = rec["colorBlend"]!["blends"]!.AsArray();
            var flags = (string)rec["flags"]!;
            var colorName = rec["outputs"]!.AsArray().Select(o => (string)o!).FirstOrDefault();
            var depthName = (string?)rec["depthOut"];
            var state = new SceneDrawState
            {
                VertexSpirv = vsCode,
                PixelSpirv = psCode,
                Bindings = vbs.Select((b, i) => ((uint)(int)b!["byteStride"]!, perInstance.GetValueOrDefault(i))).ToArray(),
                Attributes = vi["attributes"]!.AsArray().Select(a => new ProgramRunner.Attribute((uint)(int)a!["location"]!, (uint)(int)a["binding"]!, Format((string)a["format"]!), (uint)(int)a["byteOffset"]!)).ToArray(),
                Topology = (int)rec["inputAssembly"]!["topology"]! switch { 1 => PrimitiveTopology.PointList, 2 => PrimitiveTopology.LineList, 3 => PrimitiveTopology.LineStrip, 5 => PrimitiveTopology.TriangleList, 6 => PrimitiveTopology.TriangleStrip, 7 => PrimitiveTopology.TriangleFan, var t => throw new NotSupportedException($"topology {t}") },
                Cull = (CullModeFlags)(int)rast["cullMode"]!,
                Front = (bool)rast["frontCCW"]! ? FrontFace.CounterClockwise : FrontFace.Clockwise,
                DepthBias = (bool)rast["depthBiasEnable"]!,
                DepthBiasConstant = (float)rast["depthBias"]!,
                DepthBiasSlope = (float)rast["slopeScaledDepthBias"]!,
                DepthBiasClamp = (float)rast["depthBiasClamp"]!,
                DepthClamp = (bool)rast["depthClampEnable"]!,
                DepthTest = (bool)ds["depthTestEnable"]!,
                DepthWrite = (bool)ds["depthWriteEnable"]!,
                DepthCompare = Compare((int)ds["depthFunction"]!),
                StencilTest = (bool)ds["stencilTestEnable"]!,
                StencilFront = Stencil(ds["frontFace"]!),
                StencilBack = Stencil(ds["backFace"]!),
                Viewport = new Viewport((float)vp["vp"]!["x"]!, (float)vp["vp"]!["y"]!, (float)vp["vp"]!["width"]!, (float)vp["vp"]!["height"]!, (float)vp["vp"]!["minDepth"]!, (float)vp["vp"]!["maxDepth"]!),
                Scissor = new Rect2D(new Offset2D((int)vp["scissor"]!["x"]!, (int)vp["scissor"]!["y"]!), new Extent2D((uint)(int)vp["scissor"]!["width"]!, (uint)(int)vp["scissor"]!["height"]!)),
                ColorFormat = colorName != null ? Format((string)resources["textures"]![colorName]!["format"]!) : null,
                ColorWriteMask = blends.Count > 0 ? (ColorComponentFlags)(int)blends[0]!["writeMask"]! : ColorComponentFlags.None,
                DepthFormat = depthName != null ? Format((string)resources["textures"]![depthName]!["format"]!) : null,
            };
            if (blends.Any(b => (bool)b!["enabled"]!))
                return "blending not modelled";

            var vertexBuffers = vbs.Select(b => (Buffer((string)before[(string)b!["resourceId"]!]!["sha1"]!), (ulong)b["byteOffset"]!)).ToArray();
            var ib = rec["ibuffer"]!;
            (GpuDevice.GpuBuffer, ulong, IndexType)? index = flags.Contains("Indexed")
                ? (Buffer((string)before[(string)ib["resourceId"]!]!["sha1"]!), (ulong)ib["byteOffset"]!, (int)ib["byteStride"]! == 4 ? IndexType.Uint32 : IndexType.Uint16)
                : null;
            var args = flags.Contains("Indexed")
                ? new SceneDrawArgs(true, (uint)rec["numIndices"]!, (uint)rec["numInstances"]!, (uint)rec["indexOffset"]!, (int)rec["baseVertex"]!, (uint)rec["instanceOffset"]!)
                : new SceneDrawArgs(false, (uint)rec["numIndices"]!, (uint)rec["numInstances"]!, (uint)rec["vertexOffset"]!, 0, (uint)rec["instanceOffset"]!);

            // Fresh targets from the capture's contents before the draw.
            SceneDrawRunner.SceneImage? Target(string? name, ImageUsageFlags usage)
            {
                if (name == null)
                    return null;
                var t = resources["textures"]![name]!;
                var image = runner.CreateImage(Format((string)t["format"]!), (uint)(int)t["width"]!, (uint)(int)t["height"]!, 1, 1, 1, ImageViewType.Type2D, usage);
                runner.Write(image, 0, 0, Blob((string)before[name]![0]!["sha1"]!));
                return image;
            }
            var color = Target(colorName, ImageUsageFlags.ColorAttachmentBit);
            var depth = Target(depthName, ImageUsageFlags.DepthStencilAttachmentBit);
            runner.Draw(state, bindings, vertexBuffers, index, args, color, depth);

            var report = new List<string>();
            var equal = true;
            foreach (var (name, image, texel) in new[] { (colorName, color, 8u), (depthName, depth, 0u) })
            {
                if (name == null || image == null)
                    continue;
                var ours = runner.Read(image, texel);
                var theirs = Blob((string)after[name]![0]!["sha1"]!);
                if (Baselines != null)
                {
                    // Against this device's own unperturbed run (another GPU need not reproduce the capture's bits).
                    var key = $"{rec["eid"]}|{name}";
                    if (!Baselines.TryGetValue(key, out var baseline))
                        Baselines[key] = baseline = ours;
                    theirs = baseline;
                }
                var beforeBytes = Blob((string)before[name]![0]!["sha1"]!);
                var diff = 0;
                for (var i = 0; i < Math.Min(ours.Length, theirs.Length); i++)
                    if (ours[i] != theirs[i])
                        diff++;
                var changed = 0;
                for (var i = 0; i < theirs.Length; i++)
                    if (theirs[i] != beforeBytes[i])
                        changed++;
                equal &= diff == 0 && ours.Length == theirs.Length;
                report.Add($"{name}: {(diff == 0 ? "same" : $"{diff} bytes differ")} ({changed} bytes changed by the draw{(ours.Length != theirs.Length ? $", sizes {ours.Length} / {theirs.Length}" : "")})");
                runner.Destroy(image);
            }
            return (equal ? "equal; " : "DIFFERENT; ") + string.Join("; ", report);
        }

        /// <summary>A dispatch from fresh copies of its read-write buffers; each must equal the capture's after it.</summary>
        public string RunCompute(JsonNode rec)
        {
            var before = rec["contents"]!["before"]!;
            var after = rec["contents"]!["after"]!;
            var cs = rec["stages"]!["cs"]!;
            var code = File.ReadAllBytes(Path.Combine(spirvDir, $"{int.Parse(((string)cs["shader"]!).Split(' ')[^1]):x}.spv"));
            var reflection = SpirvReflection.Parse(code);
            var bindings = new SceneDrawRunner.Bindings();
            foreach (var cb in cs["constantBlocks"]!.AsArray())
            {
                var block = cb!["block"]!;
                bindings.Buffer((int)block["fixedBindSetOrSpace"]!, (int)block["fixedBindNumber"]!, (uint)(int)cb["access"]!["arrayElement"]!,
                    Buffer((string)cb["bytes"]!["sha1"]!), 0, (ulong)(long)cb["bytes"]!["size"]!);
            }
            var written = new List<(string Name, GpuDevice.GpuBuffer Buffer)>();
            foreach (var r in cs["readWrite"]!.AsArray().Concat(cs["readOnly"]!.AsArray()))
            {
                var bind = r!["bind"]!;
                var d = r["descriptor"]!;
                var name = (string)d["resource"]!;
                var (set, binding, element) = ((int)bind["set"]!, (int)bind["binding"]!, (uint)(int)r["access"]!["arrayElement"]!);
                if (before[name] is JsonArray subs)
                {
                    bindings.Image(set, binding, element, Image(name, subs, Format((string)d["format"]!), ViewType(reflection.Resources.First(x => x.Set == set && x.Binding == binding).Dim)).View);
                    continue;
                }
                var buffer = written.FirstOrDefault(w => w.Name == name).Buffer;
                if (buffer == null && after[name] != null)
                {
                    buffer = gpu.Upload(Blob((string)before[name]!["sha1"]!), BufferUsageFlags.StorageBufferBit | BufferUsageFlags.UniformBufferBit);
                    written.Add((name, buffer));
                }
                var size = (ulong)d["byteSize"]!;
                bindings.Buffer(set, binding, element, buffer ?? Buffer((string)before[name]!["sha1"]!), (ulong)d["byteOffset"]!, size >= (1UL << 40) ? Vk.WholeSize : size);
            }
            foreach (var smp in cs["samplers"]!.AsArray())
                bindings.Sampler((int)smp!["bind"]!["set"]!, (int)smp["bind"]!["binding"]!, (uint)(int)smp["access"]!["arrayElement"]!, SamplerFor(smp["sampler"]!));
            var dim = rec["dispatchDimension"]!.AsArray().Select(v => (uint)v!).ToArray();
            runner.Dispatch(code, bindings, dim[0], dim[1], dim[2]);
            var report = new List<string>();
            var equal = true;
            foreach (var (name, buffer) in written)
            {
                var theirs = Blob((string)after[name]!["sha1"]!);
                var beforeBytes = Blob((string)before[name]!["sha1"]!);
                var ours = SceneDrawRunner.Read(buffer, 0, (ulong)theirs.Length);
                var diff = Enumerable.Range(0, theirs.Length).Count(i => ours[i] != theirs[i]);
                var changed = Enumerable.Range(0, theirs.Length).Count(i => beforeBytes[i] != theirs[i]);
                equal &= diff == 0;
                report.Add($"{name}: {(diff == 0 ? "same" : $"{diff} bytes differ")} ({changed} bytes changed by the dispatch)");
                gpu.Destroy(buffer);
            }
            return (equal ? "equal; " : "DIFFERENT; ") + string.Join("; ", report);
        }

        public void Dispose()
        {
            foreach (var b in _overridden)
                gpu.Destroy(b);
            foreach (var b in _buffers.Values)
                gpu.Destroy(b);
            foreach (var i in _images.Values)
                runner.Destroy(i);
            foreach (var s in _samplers.Values)
                gpu.Vk.DestroySampler(gpu.Device, s, null);
        }
    }
}
