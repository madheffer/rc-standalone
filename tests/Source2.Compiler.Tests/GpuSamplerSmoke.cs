using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Source2.Compiler.Gpu;
using ValvePak;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: csgo_environment_blend's ToolsVis programs (Vulkan build) run
/// through <see cref="ProgramRunner"/> in mode 80 with neutral inputs, one
/// sample per pixel laid out as physicsbuilder lays them (FUN_18064dfb0),
/// and the read-back colours decoded to layers. Without new blending every
/// sample must read layer 0. <c>GPUSMOKE=&lt;samples&gt;[|&lt;S_X=v,...&gt;]</c>.
/// </summary>
public class GpuSamplerSmoke(ITestOutputHelper output)
{
    [Fact]
    public unsafe void ToolsVisMode80()
    {
        if (Environment.GetEnvironmentVariable("GPUSMOKE") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var count = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        var extra = parts.Length > 1 ? parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(kv => kv.Split('=')).ToDictionary(kv => kv[0], kv => int.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture)) : [];
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game", "csgo");
        using var package = new Package();
        package.Read(Path.Combine(game, "shaders_vulkan_dir.vpk"));
        var vsStatics = new Dictionary<string, int>(extra) { ["S_MODE_TOOLS_VIS"] = 1 };
        var psStatics = new Dictionary<string, int>(extra) { ["S_MODE_TOOLS_VIS"] = 1, ["S_SHADER_QUALITY"] = 1 };
        var vs = ValveProgram.Load(package, "csgo_environment_blend", "vulkan_50", "vs", vsStatics, new Dictionary<string, int> { ["D_COMPRESSED_NORMALS_AND_TANGENTS"] = 1 });
        var ps = ValveProgram.Load(package, "csgo_environment_blend", "vulkan_50", "ps", psStatics, new Dictionary<string, int>());
        output.WriteLine($"{vs.Name}; {ps.Name}");

        using var gpu = new GpuDevice();
        output.WriteLine($"device {gpu.Name}");
        // Pixel positions (binding 0), sample records (binding 1), zeros for the inputs the layout leaves out (binding 2).
        ProgramRunner.Attribute[] attributes =
        [
            new(0, 0, Format.R32G32B32Sfloat, 0),
            new(1, 1, Format.R32G32Sfloat, 0),
            new(2, 1, Format.R32G32Sfloat, 8),
            new(3, 1, Format.R32Uint, 28),
            new(4, 2, Format.R32G32B32A32Uint, 0),
            new(5, 2, Format.R32Uint, 16),
            new(6, 2, Format.R32G32B32A32Sfloat, 0),
            new(7, 1, Format.R8G8B8A8Unorm, 32),
        ];
        using var runner = new ProgramRunner(gpu, vs, ps, [12, 40, 32], attributes, Format.R8G8B8A8Unorm);

        byte[] Block(ValveProgram p, int set, int binding, Action<byte[]>? fill = null)
        {
            var r = p.Reflection.Resources.First(x => x.Set == set && x.Binding == binding);
            var b = new byte[Math.Max(16, r.BlockSize)];
            fill?.Invoke(b);
            return b;
        }
        void Globals(ValveProgram p, byte[] b)
        {
            foreach (var (name, offset, _) in p.Constants)
                if (name == "g_nToolsVisMode")
                    BitConverter.TryWriteBytes(b.AsSpan(offset), 80);
        }
        // vs: set 0 _Globals_ (0), view (1), csgo view (3); set 4 transforms (30), instances (32).
        foreach (var r in vs.Reflection.Resources.Concat(ps.Reflection.Resources).Where(r => r.Kind is SpirvReflection.Kind.UniformBuffer or SpirvReflection.Kind.StorageBuffer).DistinctBy(r => (r.Set, r.Binding)))
        {
            var owner = vs.Reflection.Resources.Contains(r) ? vs : ps;
            byte[] data = (r.Set, r.Binding) switch
            {
                (0, 0) or (1, 0) => Block(owner, r.Set, r.Binding, b => Globals(owner, b)),
                // View: the camera at the origin looking along +X, so screen right is -Y
                // and up is +Z: clip = (-y, z, 0.5, 1) as this runner lands it, sample i
                // on pixel i. The matrix is RowMajor, used as v * M: memory row r holds
                // input r's share of each clip component.
                (0, 1) => Block(owner, 0, 1, b => { float[] m = [0, 0, 0, 0, -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0.5f, 1]; MemoryMarshal.AsBytes(m.AsSpan()).CopyTo(b); }),
                // One identity transform, rows of a 3x4.
                (4, 30) => MemoryMarshal.AsBytes(new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 }.AsSpan()).ToArray(),
                // One instance: white tint, transform 0.
                (4, 32) => MemoryMarshal.AsBytes(new uint[] { 0xffffffff, 0, 0, 0, 0, 0, 0, 0 }.AsSpan()).ToArray(),
                _ => Block(owner, r.Set, r.Binding),
            };
            runner.SetBuffer(r.Set, r.Binding, data);
        }
        // A 1x1 grey texture and a linear sampler in the first slots.
        var tex = gpu.CreateImage(1, 1, 1, Format.R8G8B8A8Unorm, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);
        var staging = gpu.Upload([128, 128, 128, 255], BufferUsageFlags.TransferSrcBit);
        gpu.Submit(cmd =>
        {
            gpu.Transition(cmd, tex, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
            var region = new BufferImageCopy(0, 0, 0, new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1), new Offset3D(0, 0, 0), new Extent3D(1, 1, 1));
            gpu.Vk.CmdCopyBufferToImage(cmd, staging.Buffer, tex.Image, ImageLayout.TransferDstOptimal, 1, &region);
            gpu.Transition(cmd, tex, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        });
        var samplerInfo = new SamplerCreateInfo { SType = StructureType.SamplerCreateInfo, MagFilter = Filter.Linear, MinFilter = Filter.Linear, MaxLod = 16 };
        gpu.Vk.CreateSampler(gpu.Device, &samplerInfo, null, out var sampler);
        for (uint i = 0; i < 16; i++)
        {
            runner.SetImage(4, 46, i, tex.View);
            runner.SetSampler(4, 29, i, sampler);
        }

        // One sample per pixel: FUN_18064dfb0's small triangle around the pixel centre.
        var side = (uint)Physics.MaterialSampler.TargetSide(count);
        var positions = new float[count * 9];
        var records = new byte[count * 3 * 40];
        var zeros = new byte[count * 3 * 32];
        float w = side, h = side;
        float top = -0f / h + 1f, left = -0f / w + 1f;
        var dx = ((1f - (w + w) / w) - left) / w;
        var dy = (top - (1f - (h + h) / h)) / h;
        for (var i = 0; i < count; i++)
        {
            var row = i / (int)side;
            var cy = top - (row + 0.5f) * dy;
            var cx = ((i - row * (int)side) + 0.5f) * dx + left;
            float[] tri = [0, cx, (dy * 0.75f) + cy, 0, cx - (dx * 0.75f), cy - (dy * 0.375f), 0, (dx * 0.75f) + cx, cy - (dy * 0.375f)];
            tri.CopyTo(positions, i * 9);
            var s = new Physics.MaterialSampler.Sample(new(0.5f, 0.5f), new(0.5f, 0.5f), new(0.25f, 0, 0, 0), default);
            for (var k = 0; k < 3; k++)
                Physics.MaterialSampler.Pack(s, 0, records.AsSpan(((i * 3) + k) * 40, 40));
        }
        var pixels = runner.Draw([MemoryMarshal.AsBytes(positions.AsSpan()).ToArray(), records, zeros], (uint)(count * 3), side, side, [0, 0, 0, 0]);
        var layers = new int[4];
        var shown = 0;
        for (var i = 0; i < count; i++)
        {
            byte r = pixels[i * 4], g = pixels[(i * 4) + 1], b = pixels[(i * 4) + 2];
            var layer = Physics.MaterialSampler.Vote([Physics.MaterialSampler.Weights(r, g, b)]);
            layers[layer]++;
            if (shown++ < 4)
                output.WriteLine($"sample {i}: rgba {r},{g},{b},{pixels[(i * 4) + 3]} -> layer {layer}");
        }
        var drawn = Enumerable.Range(0, (int)(side * side)).Count(i => pixels[i * 4 + 3] != 0 || pixels[i * 4] != 0 || pixels[i * 4 + 1] != 0 || pixels[i * 4 + 2] != 0);
        output.WriteLine($"target {side}x{side}, {drawn} pixels written; layers {string.Join(" ", layers)}");
        var written = Enumerable.Range(0, (int)(side * side)).Where(i => pixels[i * 4 + 3] != 0 || pixels[i * 4] != 0 || pixels[i * 4 + 1] != 0 || pixels[i * 4 + 2] != 0).ToList();
        output.WriteLine($"written pixels first {string.Join(" ", written.Take(8).Select(i => $"{i}({i % side},{i / side})=({pixels[i * 4]},{pixels[i * 4 + 1]},{pixels[i * 4 + 2]},{pixels[i * 4 + 3]})"))} last {written.LastOrDefault()}");
    }
}
