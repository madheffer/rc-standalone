using Silk.NET.Vulkan;

namespace Source2.Compiler.Gpu;

/// <summary>
/// Runs a vertex and pixel program pair (SPIR-V) over a vertex list into an
/// RGBA8 target and reads the target back. Descriptor sets follow the two
/// programs' reflection, merged by set and binding; arrays are partially
/// bound, so only the slots the caller writes must be valid.
/// </summary>
public sealed unsafe class ProgramRunner : IDisposable
{
    /// <summary>A vertex attribute: shader location, vertex buffer binding, format and byte offset.</summary>
    public readonly record struct Attribute(uint Location, uint Binding, Format Format, uint Offset);

    private readonly GpuDevice _gpu;
    private readonly Vk _vk;
    private readonly List<DescriptorSetLayout> _setLayouts = [];
    private readonly Dictionary<(int Set, int Binding), (DescriptorType Type, uint Count)> _bindings = [];
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet[] _sets;
    private readonly PipelineLayout _layout;
    private readonly RenderPass _renderPass;
    private readonly Pipeline _pipeline;
    private readonly ShaderModule _vs, _ps;
    private readonly List<GpuDevice.GpuBuffer> _buffers = [];
    public Format TargetFormat { get; }

    public ProgramRunner(GpuDevice gpu, ValveProgram vs, ValveProgram ps, uint[] strides, Attribute[] attributes, Format targetFormat, CullModeFlags cull = CullModeFlags.None, FrontFace front = FrontFace.CounterClockwise)
    {
        _gpu = gpu;
        _vk = gpu.Vk;
        TargetFormat = targetFormat;
        // Bindings of both stages by set.
        foreach (var r in vs.Reflection.Resources.Concat(ps.Reflection.Resources))
        {
            var type = r.Kind switch
            {
                SpirvReflection.Kind.UniformBuffer => DescriptorType.UniformBuffer,
                SpirvReflection.Kind.StorageBuffer => DescriptorType.StorageBuffer,
                SpirvReflection.Kind.SampledImage => DescriptorType.SampledImage,
                SpirvReflection.Kind.Sampler => DescriptorType.Sampler,
                SpirvReflection.Kind.CombinedImageSampler => DescriptorType.CombinedImageSampler,
                _ => throw new NotSupportedException($"{r.Name}: {r.Kind}"),
            };
            var count = (uint)Math.Max(1, r.ArrayLength == 0 ? 65536 : r.ArrayLength);
            if (_bindings.TryGetValue((r.Set, r.Binding), out var had) && had.Type != type)
                throw new NotSupportedException($"set {r.Set} binding {r.Binding}: {had.Type} and {type}");
            _bindings[(r.Set, r.Binding)] = (type, Math.Max(count, had.Count));
        }
        var maxSet = _bindings.Keys.Max(k => k.Set);
        for (var s = 0; s <= maxSet; s++)
        {
            var list = _bindings.Where(b => b.Key.Set == s).OrderBy(b => b.Key.Binding).ToArray();
            var bindings = stackalloc DescriptorSetLayoutBinding[Math.Max(1, list.Length)];
            var flags = stackalloc DescriptorBindingFlags[Math.Max(1, list.Length)];
            for (var i = 0; i < list.Length; i++)
            {
                bindings[i] = new DescriptorSetLayoutBinding((uint)list[i].Key.Binding, list[i].Value.Type, list[i].Value.Count, ShaderStageFlags.AllGraphics);
                flags[i] = list[i].Value.Count > 1 ? DescriptorBindingFlags.PartiallyBoundBit : 0;
            }
            var flagInfo = new DescriptorSetLayoutBindingFlagsCreateInfo { SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo, BindingCount = (uint)list.Length, PBindingFlags = flags };
            var info = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, PNext = &flagInfo, BindingCount = (uint)list.Length, PBindings = bindings };
            GpuDevice.Check(_vk.CreateDescriptorSetLayout(gpu.Device, &info, null, out var layout), "vkCreateDescriptorSetLayout");
            _setLayouts.Add(layout);
        }
        // One pool for all of it.
        var sizes = _bindings.Values.GroupBy(b => b.Type).Select(g => new DescriptorPoolSize(g.Key, (uint)g.Sum(x => x.Count))).ToArray();
        fixed (DescriptorPoolSize* ps0 = sizes)
        {
            var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = (uint)_setLayouts.Count, PoolSizeCount = (uint)sizes.Length, PPoolSizes = ps0 };
            GpuDevice.Check(_vk.CreateDescriptorPool(gpu.Device, &poolInfo, null, out _pool), "vkCreateDescriptorPool");
        }
        _sets = new DescriptorSet[_setLayouts.Count];
        var layouts = _setLayouts.ToArray();
        fixed (DescriptorSetLayout* pl = layouts)
        fixed (DescriptorSet* pset = _sets)
        {
            var alloc = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _pool, DescriptorSetCount = (uint)layouts.Length, PSetLayouts = pl };
            GpuDevice.Check(_vk.AllocateDescriptorSets(gpu.Device, &alloc, pset), "vkAllocateDescriptorSets");
            var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = (uint)layouts.Length, PSetLayouts = pl };
            GpuDevice.Check(_vk.CreatePipelineLayout(gpu.Device, &layoutInfo, null, out _layout), "vkCreatePipelineLayout");
        }
        _vs = Module(vs.Spirv);
        _ps = Module(ps.Spirv);

        var attachment = new AttachmentDescription(0, targetFormat, SampleCountFlags.Count1Bit, AttachmentLoadOp.Clear, AttachmentStoreOp.Store,
            AttachmentLoadOp.DontCare, AttachmentStoreOp.DontCare, ImageLayout.Undefined, ImageLayout.TransferSrcOptimal);
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var subpass = new SubpassDescription { PipelineBindPoint = PipelineBindPoint.Graphics, ColorAttachmentCount = 1, PColorAttachments = &colorRef };
        var rpInfo = new RenderPassCreateInfo { SType = StructureType.RenderPassCreateInfo, AttachmentCount = 1, PAttachments = &attachment, SubpassCount = 1, PSubpasses = &subpass };
        GpuDevice.Check(_vk.CreateRenderPass(gpu.Device, &rpInfo, null, out _renderPass), "vkCreateRenderPass");

        var vsName = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr(vs.Reflection.EntryPoint);
        var psName = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr(ps.Reflection.EntryPoint);
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = _vs, PName = vsName };
        stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = _ps, PName = psName };
        var vbind = stackalloc VertexInputBindingDescription[strides.Length];
        for (var i = 0; i < strides.Length; i++)
            vbind[i] = new VertexInputBindingDescription((uint)i, strides[i], VertexInputRate.Vertex);
        var vattr = stackalloc VertexInputAttributeDescription[attributes.Length];
        for (var i = 0; i < attributes.Length; i++)
            vattr[i] = new VertexInputAttributeDescription(attributes[i].Location, attributes[i].Binding, attributes[i].Format, attributes[i].Offset);
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = (uint)strides.Length,
            PVertexBindingDescriptions = vbind,
            VertexAttributeDescriptionCount = (uint)attributes.Length,
            PVertexAttributeDescriptions = vattr,
        };
        var assembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
        var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        var raster = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = cull, FrontFace = front, LineWidth = 1f };
        var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
        var blendAttachment = new PipelineColorBlendAttachmentState { ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
        var blend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &blendAttachment };
        var dynamicStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };
        var pipelineInfo = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2,
            PStages = stages,
            PVertexInputState = &vertexInput,
            PInputAssemblyState = &assembly,
            PViewportState = &viewportState,
            PRasterizationState = &raster,
            PMultisampleState = &multisample,
            PColorBlendState = &blend,
            PDynamicState = &dynamic,
            Layout = _layout,
            RenderPass = _renderPass,
        };
        GpuDevice.Check(_vk.CreateGraphicsPipelines(gpu.Device, default, 1, &pipelineInfo, null, out _pipeline), "vkCreateGraphicsPipelines");
        Silk.NET.Core.Native.SilkMarshal.Free((nint)vsName);
        Silk.NET.Core.Native.SilkMarshal.Free((nint)psName);
    }

    private ShaderModule Module(byte[] spirv)
    {
        fixed (byte* p = spirv)
        {
            var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)p };
            GpuDevice.Check(_vk.CreateShaderModule(_gpu.Device, &info, null, out var module), "vkCreateShaderModule");
            return module;
        }
    }

    /// <summary>Whether a set and binding exists in either program.</summary>
    public bool Has(int set, int binding) => _bindings.ContainsKey((set, binding));

    /// <summary>Writes a uniform or storage buffer binding from bytes.</summary>
    public void SetBuffer(int set, int binding, ReadOnlySpan<byte> data)
    {
        var (type, _) = _bindings[(set, binding)];
        var buffer = _gpu.Upload(data, type == DescriptorType.StorageBuffer ? BufferUsageFlags.StorageBufferBit : BufferUsageFlags.UniformBufferBit);
        _buffers.Add(buffer);
        var info = new DescriptorBufferInfo(buffer.Buffer, 0, Vk.WholeSize);
        var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _sets[set], DstBinding = (uint)binding, DescriptorCount = 1, DescriptorType = type, PBufferInfo = &info };
        _vk.UpdateDescriptorSets(_gpu.Device, 1, &write, 0, null);
    }

    /// <summary>Writes one element of a sampled image array.</summary>
    public void SetImage(int set, int binding, uint index, ImageView view)
    {
        var info = new DescriptorImageInfo(default, view, ImageLayout.ShaderReadOnlyOptimal);
        var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _sets[set], DstBinding = (uint)binding, DstArrayElement = index, DescriptorCount = 1, DescriptorType = DescriptorType.SampledImage, PImageInfo = &info };
        _vk.UpdateDescriptorSets(_gpu.Device, 1, &write, 0, null);
    }

    /// <summary>Writes one element of a sampler array.</summary>
    public void SetSampler(int set, int binding, uint index, Sampler sampler)
    {
        var info = new DescriptorImageInfo(sampler, default, ImageLayout.Undefined);
        var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _sets[set], DstBinding = (uint)binding, DstArrayElement = index, DescriptorCount = 1, DescriptorType = DescriptorType.Sampler, PImageInfo = &info };
        _vk.UpdateDescriptorSets(_gpu.Device, 1, &write, 0, null);
    }

    /// <summary>
    /// Draws the vertex buffers (one per binding, <paramref name="vertexCount"/>
    /// vertices) into a cleared width x height target and returns its pixels
    /// (RGBA8, or RGBA32F for that target format), row by row from the top.
    /// </summary>
    public byte[] Draw(byte[][] vertexBuffers, uint vertexCount, uint width, uint height, float[] clear)
    {
        var target = _gpu.CreateImage(width, height, 1, TargetFormat, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit);
        var bytesPerPixel = TargetFormat == Format.R32G32B32A32Sfloat ? 16u : 4u;
        var readback = _gpu.CreateBuffer(width * height * bytesPerPixel, BufferUsageFlags.TransferDstBit);
        var vbs = vertexBuffers.Select(v => _gpu.Upload(v, BufferUsageFlags.VertexBufferBit)).ToArray();
        var view = target.View;
        var fbInfo = new FramebufferCreateInfo { SType = StructureType.FramebufferCreateInfo, RenderPass = _renderPass, AttachmentCount = 1, PAttachments = &view, Width = width, Height = height, Layers = 1 };
        GpuDevice.Check(_vk.CreateFramebuffer(_gpu.Device, &fbInfo, null, out var framebuffer), "vkCreateFramebuffer");
        _gpu.Submit(cmd =>
        {
            var clearValue = new ClearValue(new ClearColorValue(clear[0], clear[1], clear[2], clear[3]));
            var begin = new RenderPassBeginInfo { SType = StructureType.RenderPassBeginInfo, RenderPass = _renderPass, Framebuffer = framebuffer, RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(width, height)), ClearValueCount = 1, PClearValues = &clearValue };
            _vk.CmdBeginRenderPass(cmd, &begin, SubpassContents.Inline);
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
            fixed (DescriptorSet* sets = _sets)
                _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, (uint)_sets.Length, sets, 0, null);
            var buffers = stackalloc Silk.NET.Vulkan.Buffer[vbs.Length];
            var offsets = stackalloc ulong[vbs.Length];
            for (var i = 0; i < vbs.Length; i++)
                buffers[i] = vbs[i].Buffer;
            _vk.CmdBindVertexBuffers(cmd, 0, (uint)vbs.Length, buffers, offsets);
            var viewport = new Viewport(0, 0, width, height, 0, 1);
            var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(width, height));
            _vk.CmdSetViewport(cmd, 0, 1, &viewport);
            _vk.CmdSetScissor(cmd, 0, 1, &scissor);
            _vk.CmdDraw(cmd, vertexCount, 1, 0, 0);
            _vk.CmdEndRenderPass(cmd);
            var region = new BufferImageCopy(0, 0, 0, new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1), new Offset3D(0, 0, 0), new Extent3D(width, height, 1));
            _vk.CmdCopyImageToBuffer(cmd, target.Image, ImageLayout.TransferSrcOptimal, readback.Buffer, 1, &region);
        });
        var pixels = new byte[width * height * bytesPerPixel];
        new ReadOnlySpan<byte>((void*)readback.Mapped, pixels.Length).CopyTo(pixels);
        _vk.DestroyFramebuffer(_gpu.Device, framebuffer, null);
        foreach (var vb in vbs)
            _gpu.Destroy(vb);
        _gpu.Destroy(readback);
        _vk.DestroyImageView(_gpu.Device, target.View, null);
        _vk.DestroyImage(_gpu.Device, target.Image, null);
        _vk.FreeMemory(_gpu.Device, target.Memory, null);
        return pixels;
    }

    public void Dispose()
    {
        _vk.DestroyPipeline(_gpu.Device, _pipeline, null);
        _vk.DestroyRenderPass(_gpu.Device, _renderPass, null);
        _vk.DestroyShaderModule(_gpu.Device, _vs, null);
        _vk.DestroyShaderModule(_gpu.Device, _ps, null);
        _vk.DestroyPipelineLayout(_gpu.Device, _layout, null);
        _vk.DestroyDescriptorPool(_gpu.Device, _pool, null);
        foreach (var l in _setLayouts)
            _vk.DestroyDescriptorSetLayout(_gpu.Device, l, null);
        foreach (var b in _buffers)
            _gpu.Destroy(b);
    }
}
