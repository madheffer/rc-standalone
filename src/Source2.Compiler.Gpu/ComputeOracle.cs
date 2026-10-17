using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Source2.Compiler.Gpu;

/// <summary>
/// A compute shader compiled from GLSL (shaderc) and run once over N invocations with the
/// descriptors the caller gives per (set, binding): storage buffers (an array when more than
/// one), sampled image arrays, sampler arrays and uniform buffers. Arrays are partially bound.
/// Used as a GPU oracle for vrad3's ray tracing shaders rewritten as compute
/// (tools/re/hit_to_compute.py): the GPU evaluates what the transpiled C# evaluates on the CPU.
/// </summary>
public sealed unsafe class ComputeOracle : IDisposable
{
    public sealed record Buffers(int Set, int Binding, byte[][] Data);
    public sealed record Images(int Set, int Binding, ImageView[] Views);
    public sealed record Samplers(int Set, int Binding, Sampler[] States);
    public sealed record Uniform(int Set, int Binding, byte[] Data);

    private readonly GpuDevice _gpu;
    private readonly Vk _vk;
    private readonly ShaderModule _module;

    public ComputeOracle(GpuDevice gpu, byte[] spirv)
    {
        _gpu = gpu;
        _vk = gpu.Vk;
        Spirv = spirv;
        fixed (byte* p = spirv)
        {
            var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)p };
            GpuDevice.Check(_vk.CreateShaderModule(gpu.Device, &info, null, out _module), "vkCreateShaderModule");
        }
    }

    public byte[] Spirv { get; }

    /// <summary>GLSL compute source to SPIR-V 1.5 for Vulkan 1.2, unoptimised.</summary>
    public static byte[] Compile(string glsl, string name)
    {
        var sc = Shaderc.GetApi();
        var compiler = sc.CompilerInitialize();
        var options = sc.CompileOptionsInitialize();
        sc.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan12);
        sc.CompileOptionsSetTargetSpirv(options, SpirvVersion.Shaderc15);
        sc.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Zero);
        var source = Encoding.UTF8.GetBytes(glsl);
        CompilationResult* result;
        fixed (byte* src = source)
            result = sc.CompileIntoSpv(compiler, src, (nuint)source.Length, ShaderKind.ComputeShader, name, "main", options);
        try
        {
            if (sc.ResultGetCompilationStatus(result) != CompilationStatus.Success)
                throw new InvalidOperationException($"{name}: {SilkMarshal.PtrToString((nint)sc.ResultGetErrorMessage(result))}");
            var length = (int)sc.ResultGetLength(result);
            return new Span<byte>(sc.ResultGetBytes(result), length).ToArray();
        }
        finally
        {
            sc.ResultRelease(result);
            sc.CompileOptionsRelease(options);
            sc.CompilerRelease(compiler);
        }
    }

    /// <summary>Runs <paramref name="invocations"/> invocations (local size 64) and returns the bytes of the output storage buffer.</summary>
    public byte[] Run(uint invocations, IReadOnlyList<Buffers> buffers, IReadOnlyList<Images> images, IReadOnlyList<Samplers> samplers,
                      IReadOnlyList<Uniform> uniforms, (int Set, int Binding, ulong Size) output)
    {
        // Every (set, binding) the caller fills, with its descriptor type and count.
        var bindings = new List<(int Set, int Binding, DescriptorType Type, uint Count)>();
        foreach (var b in buffers)
            bindings.Add((b.Set, b.Binding, DescriptorType.StorageBuffer, (uint)Math.Max(1, b.Data.Length)));
        bindings.Add((output.Set, output.Binding, DescriptorType.StorageBuffer, 1));
        foreach (var i in images)
            bindings.Add((i.Set, i.Binding, DescriptorType.SampledImage, (uint)Math.Max(1, i.Views.Length)));
        foreach (var s in samplers)
            bindings.Add((s.Set, s.Binding, DescriptorType.Sampler, (uint)Math.Max(1, s.States.Length)));
        foreach (var u in uniforms)
            bindings.Add((u.Set, u.Binding, DescriptorType.UniformBuffer, 1));
        var setCount = bindings.Max(b => b.Set) + 1;

        var layouts = new DescriptorSetLayout[setCount];
        for (var set = 0; set < setCount; set++)
        {
            var mine = bindings.Where(b => b.Set == set).ToArray();
            var lb = new DescriptorSetLayoutBinding[mine.Length];
            var flags = new DescriptorBindingFlags[mine.Length];
            for (var k = 0; k < mine.Length; k++)
            {
                lb[k] = new DescriptorSetLayoutBinding((uint)mine[k].Binding, mine[k].Type, mine[k].Count, ShaderStageFlags.ComputeBit);
                flags[k] = DescriptorBindingFlags.PartiallyBoundBit;
            }
            fixed (DescriptorSetLayoutBinding* pb = lb)
            fixed (DescriptorBindingFlags* pf = flags)
            {
                var flagInfo = new DescriptorSetLayoutBindingFlagsCreateInfo { SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo, BindingCount = (uint)mine.Length, PBindingFlags = pf };
                var info = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, PNext = &flagInfo, BindingCount = (uint)mine.Length, PBindings = pb };
                GpuDevice.Check(_vk.CreateDescriptorSetLayout(_gpu.Device, &info, null, out layouts[set]), "vkCreateDescriptorSetLayout");
            }
        }
        PipelineLayout pipelineLayout;
        fixed (DescriptorSetLayout* pl = layouts)
        {
            var info = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = (uint)setCount, PSetLayouts = pl };
            GpuDevice.Check(_vk.CreatePipelineLayout(_gpu.Device, &info, null, out pipelineLayout), "vkCreatePipelineLayout");
        }
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        var stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = _module, PName = entry };
        var pipelineInfo = new ComputePipelineCreateInfo { SType = StructureType.ComputePipelineCreateInfo, Stage = stage, Layout = pipelineLayout };
        GpuDevice.Check(_vk.CreateComputePipelines(_gpu.Device, default, 1, &pipelineInfo, null, out var pipeline), "vkCreateComputePipelines");
        SilkMarshal.Free((nint)entry);

        var poolSizes = bindings.GroupBy(b => b.Type).Select(g => new DescriptorPoolSize(g.Key, (uint)g.Sum(b => b.Count))).ToArray();
        DescriptorPool pool;
        fixed (DescriptorPoolSize* ps = poolSizes)
        {
            var info = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = (uint)setCount, PoolSizeCount = (uint)poolSizes.Length, PPoolSizes = ps };
            GpuDevice.Check(_vk.CreateDescriptorPool(_gpu.Device, &info, null, out pool), "vkCreateDescriptorPool");
        }
        var sets = new DescriptorSet[setCount];
        fixed (DescriptorSetLayout* pl = layouts)
        fixed (DescriptorSet* pset = sets)
        {
            var info = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = pool, DescriptorSetCount = (uint)setCount, PSetLayouts = pl };
            GpuDevice.Check(_vk.AllocateDescriptorSets(_gpu.Device, &info, pset), "vkAllocateDescriptorSets");
        }

        var created = new List<GpuDevice.GpuBuffer>();
        GpuDevice.GpuBuffer Make(byte[] data, BufferUsageFlags usage)
        {
            var padded = data.Length == 0 ? new byte[16] : data;
            var b = _gpu.Upload(padded, usage);
            created.Add(b);
            return b;
        }
        foreach (var b in buffers)
        {
            var infos = b.Data.Select(d => { var g = Make(d, BufferUsageFlags.StorageBufferBit); return new DescriptorBufferInfo(g.Buffer, 0, g.Size); }).ToArray();
            Write(sets[b.Set], b.Binding, DescriptorType.StorageBuffer, infos, null);
        }
        var outBuffer = _gpu.CreateBuffer(Math.Max(16, output.Size), BufferUsageFlags.StorageBufferBit);
        created.Add(outBuffer);
        new Span<byte>((void*)outBuffer.Mapped, (int)outBuffer.Size).Clear();
        Write(sets[output.Set], output.Binding, DescriptorType.StorageBuffer, [new DescriptorBufferInfo(outBuffer.Buffer, 0, outBuffer.Size)], null);
        foreach (var u in uniforms)
        {
            var g = Make(u.Data, BufferUsageFlags.UniformBufferBit);
            Write(sets[u.Set], u.Binding, DescriptorType.UniformBuffer, [new DescriptorBufferInfo(g.Buffer, 0, g.Size)], null);
        }
        foreach (var i in images)
            if (i.Views.Length > 0)
                Write(sets[i.Set], i.Binding, DescriptorType.SampledImage, null, [.. i.Views.Select(v => new DescriptorImageInfo(default, v, ImageLayout.ShaderReadOnlyOptimal))]);
        foreach (var s in samplers)
            if (s.States.Length > 0)
                Write(sets[s.Set], s.Binding, DescriptorType.Sampler, null, [.. s.States.Select(v => new DescriptorImageInfo(v, default, ImageLayout.Undefined))]);

        _gpu.Submit(cmd =>
        {
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
            fixed (DescriptorSet* pset = sets)
                _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, pipelineLayout, 0, (uint)setCount, pset, 0, null);
            _vk.CmdDispatch(cmd, (invocations + 63) / 64, 1, 1);
            var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.HostReadBit };
            _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.HostBit, 0, 1, &barrier, 0, null, 0, null);
        });
        var bytes = new Span<byte>((void*)outBuffer.Mapped, (int)output.Size).ToArray();

        foreach (var b in created)
            _gpu.Destroy(b);
        _vk.DestroyDescriptorPool(_gpu.Device, pool, null);
        _vk.DestroyPipeline(_gpu.Device, pipeline, null);
        _vk.DestroyPipelineLayout(_gpu.Device, pipelineLayout, null);
        foreach (var l in layouts)
            _vk.DestroyDescriptorSetLayout(_gpu.Device, l, null);
        return bytes;
    }

    private void Write(DescriptorSet set, int binding, DescriptorType type, DescriptorBufferInfo[]? buffers, DescriptorImageInfo[]? images)
    {
        fixed (DescriptorBufferInfo* pb = buffers)
        fixed (DescriptorImageInfo* pi = images)
        {
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = (uint)binding, DescriptorType = type,
                DescriptorCount = (uint)(buffers?.Length ?? images!.Length), PBufferInfo = pb, PImageInfo = pi,
            };
            _vk.UpdateDescriptorSets(_gpu.Device, 1, &write, 0, null);
        }
    }

    /// <summary>A sampler with vrad3's bindless sampler fields: address mode (0 wrap, 1 mirror, 2 clamp, 3 border, 4 mirror once), linear or point, mipmapped or mip 0 only.</summary>
    public Sampler CreateSampler(int addressU, int addressV, bool linear, bool mipmaps)
    {
        static SamplerAddressMode M(int m) => m switch
        {
            1 => SamplerAddressMode.MirroredRepeat,
            2 => SamplerAddressMode.ClampToEdge,
            3 => SamplerAddressMode.ClampToBorder,
            4 => SamplerAddressMode.MirrorClampToEdge,
            _ => SamplerAddressMode.Repeat,
        };
        var filter = linear ? Filter.Linear : Filter.Nearest;
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo, MagFilter = filter, MinFilter = filter,
            MipmapMode = linear ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest,
            AddressModeU = M(addressU), AddressModeV = M(addressV), AddressModeW = SamplerAddressMode.Repeat,
            MinLod = 0, MaxLod = mipmaps ? 1000f : 0f, BorderColor = BorderColor.FloatTransparentBlack,
        };
        GpuDevice.Check(_vk.CreateSampler(_gpu.Device, &info, null, out var sampler), "vkCreateSampler");
        return sampler;
    }

    public void DestroySampler(Sampler s) => _vk.DestroySampler(_gpu.Device, s, null);

    public void Dispose() => _vk.DestroyShaderModule(_gpu.Device, _module, null);
}
