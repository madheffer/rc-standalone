using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Source2.Compiler.Lighting;
using Source2.Compiler.Lighting.Bake;

namespace Source2.Compiler.Gpu;

/// <summary>
/// vrad3's small-chart sample pass on the GPU (lightmap_compute_block_gpu, 180030100: "Sampling Small
/// Charts"): Valve's lightmap_generate_small_chart_samples (vulkan 60 cs), one 8 x 8 thread group per
/// block tile over the block's whole vertex buffer (BlockGeometry: position, atlas uv, packed frame, 24
/// bytes), the tiles' triangle ranges (TileStart), the triangle areas, the chart info (colour, flags
/// &amp; 0x0fffffff) and the block's chart ids. Every luxel of the block is written: emptied (uv -1, -1)
/// first, then the tile's sample where a small chart lands. g_nRNGOffsetX / Y are the generation's first
/// two SFMT words. lightmap_rasterize then draws the large charts over it.
/// </summary>
public sealed unsafe partial class RtxBakeHost
{
    private Pipeline _smallCharts;
    private PipelineLayout _smallLayout;
    private DescriptorSetLayout _smallSetLayout;
    private DescriptorPool _smallPool;
    private (uint Binding, DescriptorType Type)[] _smallBindings = [];
    private int _dumpedSmall;

    private void BuildSmallCharts(RtxShaderLibrary shaders)
    {
        var program = shaders.ProgramFile("lightmap_generate_small_chart_samples", "cs") ?? throw new FileNotFoundException("lightmap_generate_small_chart_samples_vulkan_60_cs");
        var spirv = Spirv(program, 0, 0);
        var reflection = SpirvReflection.Parse(spirv);
        _smallBindings = [.. reflection.Resources.Select(r => ((uint)r.Binding, r.Kind switch
        {
            SpirvReflection.Kind.UniformBuffer => DescriptorType.UniformBuffer,
            SpirvReflection.Kind.StorageBuffer => DescriptorType.StorageBuffer,
            SpirvReflection.Kind.SampledImage => DescriptorType.SampledImage,
            _ => throw new InvalidDataException($"lightmap_generate_small_chart_samples: binding {r.Binding} is a {r.Kind}"),
        })).Distinct()];
        if (reflection.Resources.Any(r => r.Set != 0))
            throw new InvalidDataException("lightmap_generate_small_chart_samples: a resource outside set 0");
        var lb = _smallBindings.Select(b => new DescriptorSetLayoutBinding(b.Binding, b.Type, 1, ShaderStageFlags.ComputeBit)).ToArray();
        fixed (DescriptorSetLayoutBinding* pb = lb)
        {
            var info = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = (uint)lb.Length, PBindings = pb };
            GpuDevice.Check(_vk.CreateDescriptorSetLayout(_gpu.Device, &info, null, out _smallSetLayout), "vkCreateDescriptorSetLayout");
        }
        var setLayout = _smallSetLayout;
        var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout };
        GpuDevice.Check(_vk.CreatePipelineLayout(_gpu.Device, &layoutInfo, null, out _smallLayout), "vkCreatePipelineLayout");
        var entry = (byte*)SilkMarshal.StringToPtr(reflection.EntryPoints.Single(e => e.Model == 5).Name);
        var stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = Module(spirv), PName = entry };
        var pipelineInfo = new ComputePipelineCreateInfo { SType = StructureType.ComputePipelineCreateInfo, Stage = stage, Layout = _smallLayout };
        GpuDevice.Check(_vk.CreateComputePipelines(_gpu.Device, default, 1, &pipelineInfo, null, out _smallCharts), "vkCreateComputePipelines");
        SilkMarshal.Free((nint)entry);
        var sizes = _smallBindings.GroupBy(b => b.Type).Select(g => new DescriptorPoolSize(g.Key, (uint)g.Count())).ToArray();
        fixed (DescriptorPoolSize* ps = sizes)
        {
            var info = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo, Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit, MaxSets = 1, PoolSizeCount = (uint)sizes.Length, PPoolSizes = ps,
            };
            GpuDevice.Check(_vk.CreateDescriptorPool(_gpu.Device, &info, null, out _smallPool), "vkCreateDescriptorPool");
        }
    }

    public bool SmallChartSamples(BlockGeometry g, PackingGeometry packing, int ox, int oy, int lightmapWidth, int lightmapHeight, uint rngX, uint rngY)
    {
        if (_smallCharts.Handle == 0)
            return false;
        var vertexBytes = new byte[Math.Max(1, g.TriangleCount * 3) * 24];
        for (var v = 0; v < g.TriangleCount * 3; v++)
        {
            var x = g.Vertices[v];
            var f = MemoryMarshal.Cast<byte, float>(vertexBytes.AsSpan(v * 24, 20));
            (f[0], f[1], f[2], f[3], f[4]) = (x.Position.X, x.Position.Y, x.Position.Z, x.Uv.X, x.Uv.Y);
            BitConverter.TryWriteBytes(vertexBytes.AsSpan(v * 24 + 20), x.Frame);
        }
        var chartInfo = new uint[packing.Charts.Length * 2];
        for (var c = 0; c < packing.Charts.Length; c++)
            (chartInfo[c * 2], chartInfo[c * 2 + 1]) = (packing.Charts[c].Color, packing.Charts[c].Flags & 0x0fffffff);
        // $Globals: stride, uv offset, packed frame offset, block rect x, y, width, (a hole, 0), RNG offset x, y, lightmap width, height
        // (capture_smallcharts.py: vrad3's own, word for word).
        int[] globals = [24, 12, 20, ox, oy, _w, 0, (int)rngX, (int)rngY, lightmapWidth, lightmapHeight];
        // GPUBAKE_DUMPSMALL=<prefix>: the pass's inputs, one set of files per call (a check against tools/vrad3/capture_smallcharts.py).
        if (Environment.GetEnvironmentVariable("GPUBAKE_DUMPSMALL") is { Length: > 0 } prefix)
        {
            File.WriteAllBytes($"{prefix}_{_dumpedSmall}_globals.bin", Bytes(globals));
            File.WriteAllBytes($"{prefix}_{_dumpedSmall}_vertices.bin", vertexBytes);
            File.WriteAllBytes($"{prefix}_{_dumpedSmall}_tiles.bin", Bytes(g.TileStart));
            File.WriteAllBytes($"{prefix}_{_dumpedSmall}_areas.bin", Bytes(g.Areas));
            File.WriteAllBytes($"{prefix}_{_dumpedSmall}_chartinfo.bin", Bytes(chartInfo));
            _dumpedSmall++;
        }
        var made = new Dictionary<uint, GpuDevice.GpuBuffer>
        {
            [0] = _gpu.Upload(Bytes(globals), BufferUsageFlags.UniformBufferBit),
            [30] = _gpu.Upload(vertexBytes, BufferUsageFlags.StorageBufferBit),
            [31] = _gpu.Upload(Bytes(g.TileStart), BufferUsageFlags.StorageBufferBit),
            [32] = _gpu.Upload(g.Areas.Length == 0 ? new byte[4] : Bytes(g.Areas), BufferUsageFlags.StorageBufferBit),
            [33] = _gpu.Upload(Bytes(chartInfo), BufferUsageFlags.StorageBufferBit),
        };
        var setLayout = _smallSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _smallPool, DescriptorSetCount = 1, PSetLayouts = &setLayout };
        DescriptorSet set;
        GpuDevice.Check(_vk.AllocateDescriptorSets(_gpu.Device, &allocInfo, &set), "vkAllocateDescriptorSets");
        foreach (var (binding, type) in _smallBindings)
        {
            if (type == DescriptorType.SampledImage)
                WriteImages(set, binding, type, [_chartIds!.View], ImageLayout.ShaderReadOnlyOptimal);
            else
                WriteBuffers(set, binding, type, binding == 158 ? _samples! : made[binding]);
        }
        var pSet = &set;
        _gpu.Submit(cmd =>
        {
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _smallCharts);
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _smallLayout, 0, 1, pSet, 0, null);
            _vk.CmdDispatch(cmd, (uint)(_w / 8), (uint)(_h / 8), 1);
            var barrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.HostReadBit,
            };
            _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.RayTracingShaderBitKhr | PipelineStageFlags.HostBit, 0, 1, &barrier, 0, null, 0, null);
        });
        _vk.FreeDescriptorSets(_gpu.Device, _smallPool, 1, &set);
        foreach (var b in made.Values)
            _gpu.Destroy(b);
        return true;
    }

    private void DestroySmallCharts()
    {
        if (_smallCharts.Handle == 0)
            return;
        _vk.DestroyPipeline(_gpu.Device, _smallCharts, null);
        _vk.DestroyPipelineLayout(_gpu.Device, _smallLayout, null);
        _vk.DestroyDescriptorPool(_gpu.Device, _smallPool, null);
        _vk.DestroyDescriptorSetLayout(_gpu.Device, _smallSetLayout, null);
    }
}
