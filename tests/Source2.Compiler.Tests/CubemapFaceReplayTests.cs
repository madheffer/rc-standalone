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
        using var gpu = new GpuDevice(rayTracingShaderFeatures: true);
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

        private GpuDevice.GpuBuffer Buffer(string sha1)
        {
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
            foreach (var b in _buffers.Values)
                gpu.Destroy(b);
            foreach (var i in _images.Values)
                runner.Destroy(i);
            foreach (var s in _samplers.Values)
                gpu.Vk.DestroySampler(gpu.Device, s, null);
        }
    }
}
