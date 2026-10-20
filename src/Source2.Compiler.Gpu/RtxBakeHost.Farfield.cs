using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Source2.Compiler.Lighting;

namespace Source2.Compiler.Gpu;

/// <summary>
/// The far field's GPU passes. The metrics (1800347e0): Valve's rtx_vrad3_farfield_metrics (vulkan 60
/// cs), $Globals {g_fRayLength, g_nTotalSamples}, g_Grids (binding 30, the 0x8c-byte record), g_OccupiedMask
/// (31), g_CellSamples (159, 16 bytes a sample; the SPIR-V keeps no names), set 3's acceleration structure for its ray queries; 8 samples a
/// cell, dispatched as vrad3 does, 512 wide by ceil(samples / 512).
/// </summary>
public sealed unsafe partial class RtxBakeHost
{
    private Pipeline _ffMetrics;
    private PipelineLayout _ffMetricsLayout;
    private DescriptorSetLayout _ffMetricsSetLayout;
    private DescriptorPool _ffMetricsPool;
    private (uint Binding, DescriptorType Type, string Name)[] _ffMetricsBindings = [];

    private void BuildFarfieldMetrics()
    {
        var program = _rtxShaders!.ProgramFile("rtx_vrad3_farfield_metrics", "cs") ?? throw new FileNotFoundException("rtx_vrad3_farfield_metrics_vulkan_60_cs");
        var spirv = Spirv(program, 0, 0);
        var reflection = SpirvReflection.Parse(spirv);
        _ffMetricsBindings = [.. reflection.Resources.Where(r => r.Set == 0).Select(r => ((uint)r.Binding, r.Kind switch
        {
            SpirvReflection.Kind.UniformBuffer => DescriptorType.UniformBuffer,
            SpirvReflection.Kind.StorageBuffer => DescriptorType.StorageBuffer,
            _ => throw new InvalidDataException($"rtx_vrad3_farfield_metrics: binding {r.Binding} is a {r.Kind}"),
        }, r.Name)).Distinct()];
        if (reflection.Resources.Any(r => r.Set != 0 && !(r.Set == 3 && r.Kind == SpirvReflection.Kind.AccelerationStructure)))
            throw new InvalidDataException("rtx_vrad3_farfield_metrics: a resource outside set 0 other than set 3's acceleration structure");
        if (Environment.GetEnvironmentVariable("FF_DEBUG") == "1")
            Console.Error.WriteLine("ff metrics bindings: " + string.Join(", ", reflection.Resources.Select(r => $"{r.Set}/{r.Binding} {r.Kind} {r.Name} {r.TypeName}")));
        var lb = _ffMetricsBindings.Select(b => new DescriptorSetLayoutBinding(b.Binding, b.Type, 1, ShaderStageFlags.ComputeBit)).ToArray();
        fixed (DescriptorSetLayoutBinding* pb = lb)
        {
            var info = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = (uint)lb.Length, PBindings = pb };
            GpuDevice.Check(_vk.CreateDescriptorSetLayout(_gpu.Device, &info, null, out _ffMetricsSetLayout), "vkCreateDescriptorSetLayout");
        }
        var layouts = stackalloc DescriptorSetLayout[4] { _ffMetricsSetLayout, _setLayouts[1], _setLayouts[2], _setLayouts[3] };
        var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 4, PSetLayouts = layouts };
        GpuDevice.Check(_vk.CreatePipelineLayout(_gpu.Device, &layoutInfo, null, out _ffMetricsLayout), "vkCreatePipelineLayout");
        var entry = (byte*)SilkMarshal.StringToPtr(reflection.EntryPoints.Single(e => e.Model == 5).Name);
        var stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = Module(spirv), PName = entry };
        var pipelineInfo = new ComputePipelineCreateInfo { SType = StructureType.ComputePipelineCreateInfo, Stage = stage, Layout = _ffMetricsLayout };
        GpuDevice.Check(_vk.CreateComputePipelines(_gpu.Device, default, 1, &pipelineInfo, null, out _ffMetrics), "vkCreateComputePipelines");
        SilkMarshal.Free((nint)entry);
        var sizes = _ffMetricsBindings.GroupBy(b => b.Type).Select(g => new DescriptorPoolSize(g.Key, (uint)g.Count())).ToArray();
        fixed (DescriptorPoolSize* ps = sizes)
        {
            var info = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo, Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit, MaxSets = 1, PoolSizeCount = (uint)sizes.Length, PPoolSizes = ps,
            };
            GpuDevice.Check(_vk.CreateDescriptorPool(_gpu.Device, &info, null, out _ffMetricsPool), "vkCreateDescriptorPool");
        }
    }

    /// <summary>rtx_vrad3_farfield_metrics over the grid: 8 samples a cell, read back.</summary>
    public FarfieldBricks.Sample[] FarfieldMetrics(FarfieldBricks.Grid grid, uint[] occupied)
    {
        if (_ffMetrics.Handle == 0)
            BuildFarfieldMetrics();
        var samples = grid.Count * 8;
        var globals = new byte[16];
        BitConverter.TryWriteBytes(globals.AsSpan(0), grid.RayLength());
        BitConverter.TryWriteBytes(globals.AsSpan(4), samples);
        var uniform = _gpu.Upload(globals, BufferUsageFlags.UniformBufferBit);
        var grids = _gpu.Upload(grid.Record(), BufferUsageFlags.StorageBufferBit);
        var mask = _gpu.Upload(Bytes(occupied.Length > 0 ? occupied : [0u]), BufferUsageFlags.StorageBufferBit);
        var output = _gpu.CreateBuffer((ulong)samples * 16, BufferUsageFlags.StorageBufferBit);
        new Span<byte>((void*)output.Mapped, samples * 16).Clear();
        var setLayout = _ffMetricsSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _ffMetricsPool, DescriptorSetCount = 1, PSetLayouts = &setLayout };
        DescriptorSet set;
        GpuDevice.Check(_vk.AllocateDescriptorSets(_gpu.Device, &allocInfo, &set), "vkAllocateDescriptorSets");
        foreach (var (binding, type, name) in _ffMetricsBindings)
        {
            if (type == DescriptorType.UniformBuffer)
                WriteBuffers(set, binding, type, uniform);
            else if (binding == 30)
                WriteBuffers(set, binding, type, grids);
            else if (binding == 31)
                WriteBuffers(set, binding, type, mask);
            else
                WriteBuffers(set, binding, type, output);
        }
        var scene = _scene;
        var pSet = &set;
        var pScene = &scene;
        _gpu.Submit(cmd =>
        {
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _ffMetrics);
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _ffMetricsLayout, 0, 1, pSet, 0, null);
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _ffMetricsLayout, 3, 1, pScene, 0, null);
            // 1800347e0: Dispatch(0x200, (samples + 0x1ff) >> 9, 1) in threads; the shader's group is 64 wide.
            _vk.CmdDispatch(cmd, 0x200 / 64, ((uint)samples + 0x1ff) >> 9, 1);
            var after = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.HostReadBit };
            _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.HostBit, 0, 1, &after, 0, null, 0, null);
        });
        var raw = new ReadOnlySpan<byte>((void*)output.Mapped, samples * 16);
        var r = new FarfieldBricks.Sample[samples];
        for (var i = 0; i < samples; i++)
            r[i] = new FarfieldBricks.Sample(BitConverter.ToSingle(raw[(i * 16)..]), BitConverter.ToSingle(raw[(i * 16 + 4)..]),
                BitConverter.ToSingle(raw[(i * 16 + 8)..]), BitConverter.ToUInt32(raw[(i * 16 + 12)..]));
        _vk.FreeDescriptorSets(_gpu.Device, _ffMetricsPool, 1, &set);
        foreach (var b in new[] { uniform, grids, mask, output })
            _gpu.Destroy(b);
        return r;
    }

    // The voxelize and lighting passes: set 6 is g_vrad3FarfieldSet (180034a80): g_Luxels 158
    // (FarfieldLuxel_t, 48 bytes), g_SolidMaskBits 159, g_LightBins 160, g_RandomSamples 161 and
    // g_Bricks 162, a buffer here, not the LPV's image.
    private DescriptorSetLayout _ffSetLayout;
    private PipelineLayout _ffLayout;
    private (Pipeline Pipeline, GpuDevice.GpuBuffer Sbt, StridedDeviceAddressRegionKHR[] Regions) _ffVoxelize, _ffLighting;

    private void CreateFarfield()
    {
        if (_ffLayout.Handle != 0)
            return;
        const ShaderStageFlags rt = ShaderStageFlags.RaygenBitKhr | ShaderStageFlags.ClosestHitBitKhr | ShaderStageFlags.AnyHitBitKhr | ShaderStageFlags.MissBitKhr;
        DescriptorSetLayoutBinding[] lb =
        [
            new(158, DescriptorType.StorageBuffer, 1, rt), new(159, DescriptorType.StorageBuffer, 1, rt), new(160, DescriptorType.StorageBuffer, 1, rt),
            new(161, DescriptorType.StorageImage, 1, rt), new(162, DescriptorType.StorageBuffer, 1, rt),
        ];
        fixed (DescriptorSetLayoutBinding* pb = lb)
        {
            var info = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = (uint)lb.Length, PBindings = pb };
            GpuDevice.Check(_vk.CreateDescriptorSetLayout(_gpu.Device, &info, null, out _ffSetLayout), "vkCreateDescriptorSetLayout");
        }
        var layouts = stackalloc DescriptorSetLayout[7] { _setLayouts[0], _setLayouts[1], _setLayouts[2], _setLayouts[3], _setLayouts[4], _setLayouts[5], _ffSetLayout };
        var push = new PushConstantRange(ShaderStageFlags.RaygenBitKhr, 0, 80);
        var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 7, PSetLayouts = layouts, PushConstantRangeCount = 1, PPushConstantRanges = &push };
        GpuDevice.Check(_vk.CreatePipelineLayout(_gpu.Device, &layoutInfo, null, out _ffLayout), "vkCreatePipelineLayout");
        var program = _rtxShaders!.Get("rtx_vrad3_farfield") ?? throw new FileNotFoundException("rtx_vrad3_farfield");
        var reorder = program.Rtx.StaticComboArray.Single(c => c.Name == "S_EXECUTION_REORDERING").ComboIndexValue * _executionReordering;
        var pass = program.Rtx.DynamicComboArray.Single().ComboIndexValue;
        _ffVoxelize = Pipeline(Spirv(program.Rtx, reorder, 0), _hitModules, _ffLayout);
        _ffLighting = Pipeline(Spirv(program.Rtx, reorder, pass), _hitModules, _ffLayout);
    }

    /// <summary>
    /// farfield_voxelize_bricks (180035580) and farfield_compute_lighting (1800358b0): the bricks (root
    /// first, 16 bytes: cell offset, level) uploaded, D 0 voxelizes 32^3 samples a brick into the solid
    /// mask, then batches of at most <paramref name="perBatch"/> bricks run D 1 <paramref name="spp"/> times
    /// (the Halton volume regenerated for each pass, m_nEstimate the pass), launch 512 x 64 x bricks.
    /// The result is each brick's 32^3 FarfieldLuxel_t: 12 floats, (dc r, sh1 r xyz), (dc g, sh1 g xyz), (dc b, sh1 b xyz).
    /// </summary>
    public (float[] Luxels, uint[] Solid) FarfieldLighting(FarfieldBricks.Grid grid, IReadOnlyList<FarfieldBricks.Brick> bricks, int spp, int perBatch = 512, Action<string>? log = null)
    {
        CreateFarfield();
        var n = bricks.Count;
        var brickBytes = new byte[n * 16];
        for (var i = 0; i < n; i++)
        {
            BitConverter.TryWriteBytes(brickBytes.AsSpan(i * 16), bricks[i].X);
            BitConverter.TryWriteBytes(brickBytes.AsSpan(i * 16 + 4), bricks[i].Y);
            BitConverter.TryWriteBytes(brickBytes.AsSpan(i * 16 + 8), bricks[i].Z);
            BitConverter.TryWriteBytes(brickBytes.AsSpan(i * 16 + 12), bricks[i].Level);
        }
        var batch = Math.Min(perBatch, n);
        var words = Math.Max((n << 15) >> 5, 1);
        var brickBuffer = _gpu.Upload(brickBytes, BufferUsageFlags.StorageBufferBit);
        var mask = _gpu.CreateBuffer((ulong)words * 4, BufferUsageFlags.StorageBufferBit);
        new Span<byte>((void*)mask.Mapped, words * 4).Clear();
        var luxelBytes = batch * 32768 * 48;
        var luxels = _gpu.CreateBuffer((ulong)luxelBytes, BufferUsageFlags.StorageBufferBit);
        var layout = _ffSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _pool, DescriptorSetCount = 1, PSetLayouts = &layout };
        DescriptorSet set;
        GpuDevice.Check(_vk.AllocateDescriptorSets(_gpu.Device, &allocInfo, &set), "vkAllocateDescriptorSets");
        WriteBuffers(set, 158, DescriptorType.StorageBuffer, luxels);
        WriteBuffers(set, 159, DescriptorType.StorageBuffer, mask);
        WriteBuffers(set, 160, DescriptorType.StorageBuffer, _lightBins);
        WriteImages(set, 161, DescriptorType.StorageImage, [_random.View], ImageLayout.General);
        WriteBuffers(set, 162, DescriptorType.StorageBuffer, brickBuffer);
        var c2w = grid.CellToWorld();
        byte[] Push(int estimate, int first)
        {
            var b = new byte[80];
            BitConverter.TryWriteBytes(b.AsSpan(0), estimate);
            BitConverter.TryWriteBytes(b.AsSpan(4), 4);
            BitConverter.TryWriteBytes(b.AsSpan(8), 1);
            BitConverter.TryWriteBytes(b.AsSpan(12), first);
            BitConverter.TryWriteBytes(b.AsSpan(16), grid.X);
            BitConverter.TryWriteBytes(b.AsSpan(20), grid.Y);
            BitConverter.TryWriteBytes(b.AsSpan(24), grid.Z);
            for (var i = 0; i < 12; i++)
                BitConverter.TryWriteBytes(b.AsSpan(32 + i * 4), c2w[i]);
            return b;
        }
        try
        {
            Trace(_ffVoxelize, set, Push(0, 0), 512, 64, (uint)n, _ffLayout);
            var solid = new ReadOnlySpan<uint>((void*)mask.Mapped, words).ToArray();
            log?.Invoke($"  farfield brick voxelization: {solid.Sum(w => System.Numerics.BitOperations.PopCount(w))} of {n << 15} samples solid");
            var result = new float[n * 32768 * 12];
            for (var first = 0; first < n; first += batch)
            {
                var count = Math.Min(batch, n - first);
                for (var pass = 0; pass < spp; pass++)
                {
                    GenerateHalton(pass);
                    Trace(_ffLighting, set, Push(pass, first), 512, 64, (uint)count, _ffLayout);
                }
                new ReadOnlySpan<float>((void*)luxels.Mapped, count * 32768 * 12).CopyTo(result.AsSpan(first * 32768 * 12));
            }
            return (result, solid);
        }
        finally
        {
            _vk.FreeDescriptorSets(_gpu.Device, _pool, 1, &set);
            foreach (var b in new[] { brickBuffer, mask, luxels })
                _gpu.Destroy(b);
        }
    }

    private void DestroyFarfield()
    {
        if (_ffLayout.Handle != 0)
        {
            _vk.DestroyPipeline(_gpu.Device, _ffVoxelize.Pipeline, null);
            _vk.DestroyPipeline(_gpu.Device, _ffLighting.Pipeline, null);
            _vk.DestroyPipelineLayout(_gpu.Device, _ffLayout, null);
            _vk.DestroyDescriptorSetLayout(_gpu.Device, _ffSetLayout, null);
        }
        if (_ffMetrics.Handle == 0)
            return;
        _vk.DestroyPipeline(_gpu.Device, _ffMetrics, null);
        _vk.DestroyPipelineLayout(_gpu.Device, _ffMetricsLayout, null);
        _vk.DestroyDescriptorPool(_gpu.Device, _ffMetricsPool, null);
        _vk.DestroyDescriptorSetLayout(_gpu.Device, _ffMetricsSetLayout, null);
    }
}
