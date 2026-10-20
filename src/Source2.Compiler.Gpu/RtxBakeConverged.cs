using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Source2.Compiler.Lighting.Bake;

namespace Source2.Compiler.Gpu;

/// <summary>
/// vrad3's lightmap convergence test (lightmap_compute_block_gpu, 180030100, before a pass): Valve's
/// lightmap_test_converged (vulkan 60 cs, static combo 0, the lightmap one) over the block in 4 x 4 thread
/// groups, $Globals {block width, block height, tolerance}, the luxels and the block's converged image. A
/// group is converged when none of its luxels has a sample, not when two or fewer have; otherwise when the
/// luminance's standard deviation (wave sums) is within the mean times the tolerance. A tolerance of 0 or
/// less writes 0. Run on the GPU because the wave sums' order is the hardware's.
/// </summary>
public sealed unsafe partial class RtxBakeHost
{
    private Pipeline _converge;
    private PipelineLayout _convergeLayout;
    private DescriptorSetLayout _convergeSetLayout;
    private DescriptorPool _convergePool;
    private (uint Binding, DescriptorType Type)[] _convergeBindings = [];

    private void BuildTestConverged(RtxShaderLibrary shaders)
    {
        var program = shaders.ProgramFile("lightmap_test_converged", "cs") ?? throw new FileNotFoundException("lightmap_test_converged_vulkan_60_cs");
        var spirv = Spirv(program, 0, 0);
        var reflection = SpirvReflection.Parse(spirv);
        _convergeBindings = [.. reflection.Resources.Select(r => ((uint)r.Binding, r.Kind switch
        {
            SpirvReflection.Kind.UniformBuffer => DescriptorType.UniformBuffer,
            SpirvReflection.Kind.StorageBuffer => DescriptorType.StorageBuffer,
            SpirvReflection.Kind.StorageImage => DescriptorType.StorageImage,
            _ => throw new InvalidDataException($"lightmap_test_converged: binding {r.Binding} is a {r.Kind}"),
        })).Distinct()];
        if (reflection.Resources.Any(r => r.Set != 0))
            throw new InvalidDataException("lightmap_test_converged: a resource outside set 0");
        var lb = _convergeBindings.Select(b => new DescriptorSetLayoutBinding(b.Binding, b.Type, 1, ShaderStageFlags.ComputeBit)).ToArray();
        fixed (DescriptorSetLayoutBinding* pb = lb)
        {
            var info = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = (uint)lb.Length, PBindings = pb };
            GpuDevice.Check(_vk.CreateDescriptorSetLayout(_gpu.Device, &info, null, out _convergeSetLayout), "vkCreateDescriptorSetLayout");
        }
        var setLayout = _convergeSetLayout;
        var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout };
        GpuDevice.Check(_vk.CreatePipelineLayout(_gpu.Device, &layoutInfo, null, out _convergeLayout), "vkCreatePipelineLayout");
        var entry = (byte*)SilkMarshal.StringToPtr(reflection.EntryPoints.Single(e => e.Model == 5).Name);
        var stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = Module(spirv), PName = entry };
        var pipelineInfo = new ComputePipelineCreateInfo { SType = StructureType.ComputePipelineCreateInfo, Stage = stage, Layout = _convergeLayout };
        GpuDevice.Check(_vk.CreateComputePipelines(_gpu.Device, default, 1, &pipelineInfo, null, out _converge), "vkCreateComputePipelines");
        SilkMarshal.Free((nint)entry);
        var sizes = _convergeBindings.GroupBy(b => b.Type).Select(g => new DescriptorPoolSize(g.Key, (uint)g.Count())).ToArray();
        fixed (DescriptorPoolSize* ps = sizes)
        {
            var info = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo, Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit, MaxSets = 1, PoolSizeCount = (uint)sizes.Length, PPoolSizes = ps,
            };
            GpuDevice.Check(_vk.CreateDescriptorPool(_gpu.Device, &info, null, out _convergePool), "vkCreateDescriptorPool");
        }
    }

    public bool TestConverged(float tolerance)
    {
        if (_converge.Handle == 0)
            return false;
        var globals = new byte[16];
        BitConverter.TryWriteBytes(globals.AsSpan(0), _w);
        BitConverter.TryWriteBytes(globals.AsSpan(4), _h);
        BitConverter.TryWriteBytes(globals.AsSpan(8), tolerance);
        var uniform = _gpu.Upload(globals, BufferUsageFlags.UniformBufferBit);
        var setLayout = _convergeSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _convergePool, DescriptorSetCount = 1, PSetLayouts = &setLayout };
        DescriptorSet set;
        GpuDevice.Check(_vk.AllocateDescriptorSets(_gpu.Device, &allocInfo, &set), "vkAllocateDescriptorSets");
        foreach (var (binding, type) in _convergeBindings)
        {
            if (type == DescriptorType.StorageImage)
                WriteImages(set, binding, type, [_converged!.View], ImageLayout.General);
            else if (type == DescriptorType.UniformBuffer)
                WriteBuffers(set, binding, type, uniform);
            else
                WriteBuffers(set, binding, type, _luxels!);
        }
        var pSet = &set;
        _gpu.Submit(cmd =>
        {
            var before = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            };
            _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.RayTracingShaderBitKhr | PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &before, 0, null, 0, null);
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _converge);
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _convergeLayout, 0, 1, pSet, 0, null);
            _vk.CmdDispatch(cmd, (uint)(_w + 3) / 4, (uint)(_h + 3) / 4, 1);
            var after = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            };
            _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.RayTracingShaderBitKhr | PipelineStageFlags.ComputeShaderBit, 0, 1, &after, 0, null, 0, null);
        });
        _vk.FreeDescriptorSets(_gpu.Device, _convergePool, 1, &set);
        _gpu.Destroy(uniform);
        return true;
    }

    private void DestroyTestConverged()
    {
        if (_converge.Handle == 0)
            return;
        _vk.DestroyPipeline(_gpu.Device, _converge, null);
        _vk.DestroyPipelineLayout(_gpu.Device, _convergeLayout, null);
        _vk.DestroyDescriptorPool(_gpu.Device, _convergePool, null);
        _vk.DestroyDescriptorSetLayout(_gpu.Device, _convergeSetLayout, null);
    }
}
