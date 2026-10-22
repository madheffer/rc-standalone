using Silk.NET.Vulkan;

namespace Source2.Compiler.Gpu;

/// <summary>
/// The fixed-function state of one scene draw, spelled out the way the game's
/// pipelines set it (the cubemap face render's depth prepass, colour pass and
/// sky): the programs, vertex layout, rasterizer, depth and stencil tests,
/// viewport and the target formats.
/// </summary>
public sealed record SceneDrawState
{
    public required byte[] VertexSpirv { get; init; }

    /// <summary>The pixel program; null for a depth-only draw.</summary>
    public byte[]? PixelSpirv { get; init; }

    /// <summary>Each vertex buffer binding's stride and whether it steps per instance.</summary>
    public required (uint Stride, bool PerInstance)[] Bindings { get; init; }

    public required ProgramRunner.Attribute[] Attributes { get; init; }
    public PrimitiveTopology Topology { get; init; } = PrimitiveTopology.TriangleList;
    public CullModeFlags Cull { get; init; }
    public FrontFace Front { get; init; } = FrontFace.CounterClockwise;
    public bool DepthBias { get; init; }
    public float DepthBiasConstant { get; init; }
    public float DepthBiasSlope { get; init; }
    public float DepthBiasClamp { get; init; }
    public bool DepthClamp { get; init; }
    public bool DepthTest { get; init; }
    public bool DepthWrite { get; init; }
    public CompareOp DepthCompare { get; init; } = CompareOp.LessOrEqual;
    public bool StencilTest { get; init; }
    public StencilOpState StencilFront { get; init; }
    public StencilOpState StencilBack { get; init; }
    public required Viewport Viewport { get; init; }
    public required Rect2D Scissor { get; init; }

    /// <summary>The colour target's format; null for no colour target.</summary>
    public Format? ColorFormat { get; init; }

    public ColorComponentFlags ColorWriteMask { get; init; } = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit;

    /// <summary>The depth (and stencil) target's format; null for none.</summary>
    public Format? DepthFormat { get; init; }
}

/// <summary>Draw arguments: indexed when <see cref="Indexed"/>, else a vertex range.</summary>
public readonly record struct SceneDrawArgs(bool Indexed, uint Count, uint Instances, uint First, int VertexOffset, uint FirstInstance);

/// <summary>
/// Runs single scene draws with Valve's programs on our own device: descriptor
/// sets laid out from the programs' reflection (every binding partially bound,
/// so only what the caller writes must be valid), the targets loaded and stored
/// so consecutive draws build on each other, everything in the general layout.
/// One pipeline per draw: this is the reference path, checked draw by draw
/// against the game's capture, not a fast one.
/// </summary>
public sealed unsafe class SceneDrawRunner(GpuDevice gpu)
{
    private readonly Vk _vk = gpu.Vk;

    /// <summary>An image of any type with its view; depth formats view the depth aspect.</summary>
    public sealed class SceneImage
    {
        public required Image Image { get; init; }
        public required DeviceMemory Memory { get; init; }
        public required ImageView View { get; init; }
        public required Format Format { get; init; }
        public required uint Width { get; init; }
        public required uint Height { get; init; }
        public required uint Depth { get; init; }
        public required uint Mips { get; init; }
        public required uint Layers { get; init; }
    }

    public static bool IsDepth(Format f) => f is Format.D16Unorm or Format.D24UnormS8Uint or Format.D32Sfloat or Format.D32SfloatS8Uint or Format.X8D24UnormPack32;
    public static bool HasStencil(Format f) => f is Format.D24UnormS8Uint or Format.D32SfloatS8Uint;

    /// <summary>Bytes per texel of the depth aspect as a buffer copy sees it.</summary>
    public static uint DepthTexelBytes(Format f) => f == Format.D16Unorm ? 2u : 4u;

    /// <summary>
    /// An image with <paramref name="viewType"/>'s view. Square arrays of a multiple of six layers are
    /// created cube compatible.
    /// </summary>
    public SceneImage CreateImage(Format format, uint width, uint height, uint depth, uint mips, uint layers, ImageViewType viewType, ImageUsageFlags usage)
    {
        var cube = viewType is ImageViewType.TypeCube or ImageViewType.TypeCubeArray || (layers % 6 == 0 && width == height && depth == 1 && layers >= 6);
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = depth > 1 || viewType == ImageViewType.Type3D ? ImageType.Type3D : ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, depth),
            MipLevels = mips,
            ArrayLayers = layers,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
            Flags = cube ? ImageCreateFlags.CreateCubeCompatibleBit : 0,
        };
        GpuDevice.Check(_vk.CreateImage(gpu.Device, &info, null, out var image), "vkCreateImage");
        _vk.GetImageMemoryRequirements(gpu.Device, image, out var req);
        var alloc = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = gpu.MemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit) };
        GpuDevice.Check(_vk.AllocateMemory(gpu.Device, &alloc, null, out var memory), "vkAllocateMemory");
        _vk.BindImageMemory(gpu.Device, image, memory, 0);
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = viewType,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(IsDepth(format) ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit, 0, mips, 0, layers),
        };
        GpuDevice.Check(_vk.CreateImageView(gpu.Device, &viewInfo, null, out var view), "vkCreateImageView");
        var made = new SceneImage { Image = image, Memory = memory, View = view, Format = format, Width = width, Height = height, Depth = depth, Mips = mips, Layers = layers };
        gpu.Submit(cmd => Barrier(cmd, made, ImageLayout.Undefined));
        return made;
    }

    private static ImageAspectFlags Aspects(Format f)
        => IsDepth(f) ? ImageAspectFlags.DepthBit | (HasStencil(f) ? ImageAspectFlags.StencilBit : 0) : ImageAspectFlags.ColorBit;

    private void Barrier(CommandBuffer cmd, SceneImage image, ImageLayout from)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = from,
            NewLayout = ImageLayout.General,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image.Image,
            SubresourceRange = new ImageSubresourceRange(Aspects(image.Format), 0, image.Mips, 0, image.Layers),
            SrcAccessMask = AccessFlags.MemoryWriteBit,
            DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, 0, 0, null, 0, null, 1, &barrier);
    }

    private static void MemoryBarrier(Vk vk, CommandBuffer cmd)
    {
        var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.MemoryWriteBit, DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit };
        vk.CmdPipelineBarrier(cmd, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, 0, 1, &barrier, 0, null, 0, null);
    }

    /// <summary>
    /// Writes one subresource (a mip of one layer; a 3D mip whole). Depth formats take the depth
    /// plane first (4 bytes a texel for D24, 2 for D16) then, with stencil, a 1-byte stencil plane.
    /// </summary>
    public void Write(SceneImage image, uint mip, uint layer, ReadOnlySpan<byte> data)
    {
        var w = Math.Max(1, image.Width >> (int)mip);
        var h = Math.Max(1, image.Height >> (int)mip);
        var d = Math.Max(1, image.Depth >> (int)mip);
        var staging = gpu.Upload(data, BufferUsageFlags.TransferSrcBit);
        gpu.Submit(cmd =>
        {
            if (IsDepth(image.Format))
            {
                var region = new BufferImageCopy(0, 0, 0, new ImageSubresourceLayers(ImageAspectFlags.DepthBit, mip, layer, 1), default, new Extent3D(w, h, 1));
                _vk.CmdCopyBufferToImage(cmd, staging.Buffer, image.Image, ImageLayout.General, 1, &region);
                if (HasStencil(image.Format))
                {
                    var stencil = new BufferImageCopy(w * h * DepthTexelBytes(image.Format), 0, 0, new ImageSubresourceLayers(ImageAspectFlags.StencilBit, mip, layer, 1), default, new Extent3D(w, h, 1));
                    _vk.CmdCopyBufferToImage(cmd, staging.Buffer, image.Image, ImageLayout.General, 1, &stencil);
                }
            }
            else
            {
                var region = new BufferImageCopy(0, 0, 0, new ImageSubresourceLayers(ImageAspectFlags.ColorBit, mip, layer, 1), default, new Extent3D(w, h, d));
                _vk.CmdCopyBufferToImage(cmd, staging.Buffer, image.Image, ImageLayout.General, 1, &region);
            }
            MemoryBarrier(_vk, cmd);
        });
        gpu.Destroy(staging);
    }

    /// <summary>Reads mip 0 of layer 0 back, in <see cref="Write"/>'s layout.</summary>
    public byte[] Read(SceneImage image, uint texelBytes)
    {
        var (w, h) = (image.Width, image.Height);
        var depthBytes = IsDepth(image.Format) ? DepthTexelBytes(image.Format) : texelBytes;
        var size = (ulong)w * h * depthBytes + (HasStencil(image.Format) ? (ulong)w * h : 0);
        var readback = gpu.CreateBuffer(size, BufferUsageFlags.TransferDstBit);
        gpu.Submit(cmd =>
        {
            MemoryBarrier(_vk, cmd);
            var aspect = IsDepth(image.Format) ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;
            var region = new BufferImageCopy(0, 0, 0, new ImageSubresourceLayers(aspect, 0, 0, 1), default, new Extent3D(w, h, 1));
            _vk.CmdCopyImageToBuffer(cmd, image.Image, ImageLayout.General, readback.Buffer, 1, &region);
            if (HasStencil(image.Format))
            {
                var stencil = new BufferImageCopy((ulong)w * h * depthBytes, 0, 0, new ImageSubresourceLayers(ImageAspectFlags.StencilBit, 0, 0, 1), default, new Extent3D(w, h, 1));
                _vk.CmdCopyImageToBuffer(cmd, image.Image, ImageLayout.General, readback.Buffer, 1, &stencil);
            }
        });
        var bytes = new byte[size];
        new ReadOnlySpan<byte>((void*)readback.Mapped, bytes.Length).CopyTo(bytes);
        gpu.Destroy(readback);
        return bytes;
    }

    public void Destroy(SceneImage image)
    {
        _vk.DestroyImageView(gpu.Device, image.View, null);
        _vk.DestroyImage(gpu.Device, image.Image, null);
        _vk.FreeMemory(gpu.Device, image.Memory, null);
    }

    /// <summary>A sampler as the game's: filters, address modes, comparison, anisotropy and the LOD range.</summary>
    public Sampler CreateSampler(SamplerCreateInfo info)
    {
        info.SType = StructureType.SamplerCreateInfo;
        GpuDevice.Check(_vk.CreateSampler(gpu.Device, &info, null, out var sampler), "vkCreateSampler");
        return sampler;
    }

    /// <summary>The descriptors a draw binds, by set, binding and array element.</summary>
    public sealed class Bindings
    {
        internal readonly List<(int Set, int Binding, uint Element, GpuDevice.GpuBuffer Buffer, ulong Offset, ulong Range)> Buffers = [];
        internal readonly List<(int Set, int Binding, uint Element, ImageView View)> Images = [];
        internal readonly List<(int Set, int Binding, uint Element, Sampler Sampler)> Samplers = [];

        public void Buffer(int set, int binding, uint element, GpuDevice.GpuBuffer buffer, ulong offset, ulong range) => Buffers.Add((set, binding, element, buffer, offset, range));
        public void Image(int set, int binding, uint element, ImageView view) => Images.Add((set, binding, element, view));
        public void Sampler(int set, int binding, uint element, Sampler sampler) => Samplers.Add((set, binding, element, sampler));
    }

    /// <summary>
    /// Runs one draw into the targets (loaded, then stored). Vertex buffers are bound at their
    /// offsets in binding order; <paramref name="index"/> is a 16- or 32-bit index buffer.
    /// </summary>
    public void Draw(SceneDrawState state, Bindings bindings, (GpuDevice.GpuBuffer Buffer, ulong Offset)[] vertexBuffers,
                     (GpuDevice.GpuBuffer Buffer, ulong Offset, IndexType Type)? index, SceneDrawArgs args, SceneImage? color, SceneImage? depth)
    {
        var vsReflection = SpirvReflection.Parse(state.VertexSpirv);
        var psReflection = state.PixelSpirv is { } ps ? SpirvReflection.Parse(ps) : null;
        using var descriptors = Prepare([vsReflection, .. psReflection != null ? [psReflection] : Array.Empty<SpirvReflection>()], ShaderStageFlags.AllGraphics, bindings);
        var (setCount, sets, pipelineLayout) = (descriptors.Sets.Length, descriptors.Sets, descriptors.Layout);

        // Render pass: whatever targets there are, loaded and stored, in the general layout.
        var attachments = new List<AttachmentDescription>();
        var views = new List<ImageView>();
        if (color != null)
        {
            attachments.Add(new AttachmentDescription(0, color.Format, SampleCountFlags.Count1Bit, AttachmentLoadOp.Load, AttachmentStoreOp.Store,
                AttachmentLoadOp.DontCare, AttachmentStoreOp.DontCare, ImageLayout.General, ImageLayout.General));
            views.Add(color.View);
        }
        ImageView depthAttachmentView = default;
        if (depth != null)
        {
            attachments.Add(new AttachmentDescription(0, depth.Format, SampleCountFlags.Count1Bit, AttachmentLoadOp.Load, AttachmentStoreOp.Store,
                AttachmentLoadOp.Load, AttachmentStoreOp.Store, ImageLayout.General, ImageLayout.General));
            // The sampling view shows the depth aspect only; an attachment views every aspect.
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo, Image = depth.Image, ViewType = ImageViewType.Type2D, Format = depth.Format,
                SubresourceRange = new ImageSubresourceRange(Aspects(depth.Format), 0, 1, 0, 1),
            };
            GpuDevice.Check(_vk.CreateImageView(gpu.Device, &viewInfo, null, out depthAttachmentView), "vkCreateImageView");
            views.Add(depthAttachmentView);
        }
        var colorRef = new AttachmentReference(0, ImageLayout.General);
        var depthRef = new AttachmentReference(color != null ? 1u : 0u, ImageLayout.General);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = color != null ? 1u : 0u,
            PColorAttachments = color != null ? &colorRef : null,
            PDepthStencilAttachment = depth != null ? &depthRef : null,
        };
        RenderPass renderPass;
        Framebuffer framebuffer;
        var target = color ?? depth ?? throw new ArgumentException("a draw needs a target");
        var attachmentArray = attachments.ToArray();
        var viewArray = views.ToArray();
        fixed (AttachmentDescription* pa = attachmentArray)
        fixed (ImageView* pv = viewArray)
        {
            var rpInfo = new RenderPassCreateInfo { SType = StructureType.RenderPassCreateInfo, AttachmentCount = (uint)attachmentArray.Length, PAttachments = pa, SubpassCount = 1, PSubpasses = &subpass };
            GpuDevice.Check(_vk.CreateRenderPass(gpu.Device, &rpInfo, null, out renderPass), "vkCreateRenderPass");
            var fbInfo = new FramebufferCreateInfo { SType = StructureType.FramebufferCreateInfo, RenderPass = renderPass, AttachmentCount = (uint)viewArray.Length, PAttachments = pv, Width = target.Width, Height = target.Height, Layers = 1 };
            GpuDevice.Check(_vk.CreateFramebuffer(gpu.Device, &fbInfo, null, out framebuffer), "vkCreateFramebuffer");
        }

        var vsModule = Module(state.VertexSpirv);
        var psModule = state.PixelSpirv is { } psCode ? Module(psCode) : default;
        var vsName = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr(vsReflection.EntryPoint);
        var psName = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr(psReflection?.EntryPoint ?? "");
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vsModule, PName = vsName };
        stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = psModule, PName = psName };
        var vbind = stackalloc VertexInputBindingDescription[Math.Max(1, state.Bindings.Length)];
        for (var i = 0; i < state.Bindings.Length; i++)
            vbind[i] = new VertexInputBindingDescription((uint)i, state.Bindings[i].Stride, state.Bindings[i].PerInstance ? VertexInputRate.Instance : VertexInputRate.Vertex);
        var vattr = stackalloc VertexInputAttributeDescription[Math.Max(1, state.Attributes.Length)];
        for (var i = 0; i < state.Attributes.Length; i++)
            vattr[i] = new VertexInputAttributeDescription(state.Attributes[i].Location, state.Attributes[i].Binding, state.Attributes[i].Format, state.Attributes[i].Offset);
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = (uint)state.Bindings.Length, PVertexBindingDescriptions = vbind,
            VertexAttributeDescriptionCount = (uint)state.Attributes.Length, PVertexAttributeDescriptions = vattr,
        };
        var assembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = state.Topology };
        var viewport = state.Viewport;
        var scissor = state.Scissor;
        var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, PViewports = &viewport, ScissorCount = 1, PScissors = &scissor };
        var raster = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = state.Cull, FrontFace = state.Front, LineWidth = 1f,
            DepthClampEnable = state.DepthClamp, DepthBiasEnable = state.DepthBias, DepthBiasConstantFactor = state.DepthBiasConstant, DepthBiasSlopeFactor = state.DepthBiasSlope, DepthBiasClamp = state.DepthBiasClamp,
        };
        var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = state.DepthTest, DepthWriteEnable = state.DepthWrite, DepthCompareOp = state.DepthCompare,
            StencilTestEnable = state.StencilTest, Front = state.StencilFront, Back = state.StencilBack, MinDepthBounds = 0, MaxDepthBounds = 1,
        };
        var blendAttachment = new PipelineColorBlendAttachmentState { ColorWriteMask = state.ColorWriteMask };
        var blend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = color != null ? 1u : 0u, PAttachments = &blendAttachment };
        var pipelineInfo = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = state.PixelSpirv != null ? 2u : 1u, PStages = stages,
            PVertexInputState = &vertexInput, PInputAssemblyState = &assembly, PViewportState = &viewportState, PRasterizationState = &raster,
            PMultisampleState = &multisample, PDepthStencilState = &depthStencil, PColorBlendState = &blend,
            Layout = pipelineLayout, RenderPass = renderPass,
        };
        GpuDevice.Check(_vk.CreateGraphicsPipelines(gpu.Device, default, 1, &pipelineInfo, null, out var pipeline), "vkCreateGraphicsPipelines");
        Silk.NET.Core.Native.SilkMarshal.Free((nint)vsName);
        Silk.NET.Core.Native.SilkMarshal.Free((nint)psName);

        gpu.Submit(cmd =>
        {
            MemoryBarrier(_vk, cmd);
            var begin = new RenderPassBeginInfo { SType = StructureType.RenderPassBeginInfo, RenderPass = renderPass, Framebuffer = framebuffer, RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(target.Width, target.Height)) };
            _vk.CmdBeginRenderPass(cmd, &begin, SubpassContents.Inline);
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
            if (setCount > 0)
                fixed (DescriptorSet* ps0 = sets)
                    _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, pipelineLayout, 0, (uint)setCount, ps0, 0, null);
            if (vertexBuffers.Length > 0)
            {
                var buffers = stackalloc Silk.NET.Vulkan.Buffer[vertexBuffers.Length];
                var offsets = stackalloc ulong[vertexBuffers.Length];
                for (var i = 0; i < vertexBuffers.Length; i++)
                    (buffers[i], offsets[i]) = (vertexBuffers[i].Buffer.Buffer, vertexBuffers[i].Offset);
                _vk.CmdBindVertexBuffers(cmd, 0, (uint)vertexBuffers.Length, buffers, offsets);
            }
            if (args.Indexed)
            {
                var (ib, ibOffset, ibType) = index ?? throw new ArgumentException("an indexed draw needs an index buffer");
                _vk.CmdBindIndexBuffer(cmd, ib.Buffer, ibOffset, ibType);
                _vk.CmdDrawIndexed(cmd, args.Count, args.Instances, args.First, args.VertexOffset, args.FirstInstance);
            }
            else
            {
                _vk.CmdDraw(cmd, args.Count, args.Instances, args.First, args.FirstInstance);
            }
            _vk.CmdEndRenderPass(cmd);
            MemoryBarrier(_vk, cmd);
        });

        _vk.DestroyPipeline(gpu.Device, pipeline, null);
        _vk.DestroyShaderModule(gpu.Device, vsModule, null);
        if (psModule.Handle != 0)
            _vk.DestroyShaderModule(gpu.Device, psModule, null);
        _vk.DestroyFramebuffer(gpu.Device, framebuffer, null);
        _vk.DestroyRenderPass(gpu.Device, renderPass, null);
        if (depthAttachmentView.Handle != 0)
            _vk.DestroyImageView(gpu.Device, depthAttachmentView, null);
    }

    /// <summary>Runs one compute dispatch of <paramref name="spirv"/> with the bindings.</summary>
    public void Dispatch(byte[] spirv, Bindings bindings, uint x, uint y, uint z)
    {
        var reflection = SpirvReflection.Parse(spirv);
        using var descriptors = Prepare([reflection], ShaderStageFlags.ComputeBit, bindings);
        var module = Module(spirv);
        var name = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr(reflection.EntryPoint);
        var info = new ComputePipelineCreateInfo
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = module, PName = name },
            Layout = descriptors.Layout,
        };
        GpuDevice.Check(_vk.CreateComputePipelines(gpu.Device, default, 1, &info, null, out var pipeline), "vkCreateComputePipelines");
        Silk.NET.Core.Native.SilkMarshal.Free((nint)name);
        gpu.Submit(cmd =>
        {
            MemoryBarrier(_vk, cmd);
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
            if (descriptors.Sets.Length > 0)
                fixed (DescriptorSet* sets = descriptors.Sets)
                    _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, descriptors.Layout, 0, (uint)descriptors.Sets.Length, sets, 0, null);
            _vk.CmdDispatch(cmd, x, y, z);
            MemoryBarrier(_vk, cmd);
        });
        _vk.DestroyPipeline(gpu.Device, pipeline, null);
        _vk.DestroyShaderModule(gpu.Device, module, null);
    }

    /// <summary>A buffer's bytes (host-visible buffers only, as <see cref="GpuDevice.Upload"/> makes them).</summary>
    public static byte[] Read(GpuDevice.GpuBuffer buffer, ulong offset, ulong size)
    {
        var bytes = new byte[size];
        new ReadOnlySpan<byte>((byte*)buffer.Mapped + (long)offset, (int)size).CopyTo(bytes);
        return bytes;
    }

    /// <summary>A draw's or dispatch's descriptor sets and pipeline layout, destroyed together.</summary>
    private sealed class Descriptors(Vk vk, Device device, DescriptorSetLayout[] setLayouts, DescriptorPool pool, DescriptorSet[] sets, PipelineLayout layout) : IDisposable
    {
        public DescriptorSet[] Sets { get; } = sets;
        public PipelineLayout Layout { get; } = layout;

        public void Dispose()
        {
            vk.DestroyPipelineLayout(device, Layout, null);
            if (pool.Handle != 0)
                vk.DestroyDescriptorPool(device, pool, null);
            foreach (var l in setLayouts)
                vk.DestroyDescriptorSetLayout(device, l, null);
        }
    }

    /// <summary>
    /// Sets laid out from the programs' reflection (merged by set and binding, every binding partially
    /// bound, runtime arrays 4096 long), with <paramref name="bindings"/> written into them.
    /// </summary>
    private Descriptors Prepare(SpirvReflection[] reflections, ShaderStageFlags stages, Bindings bindings)
    {
        var layout = new Dictionary<(int Set, int Binding), (DescriptorType Type, uint Count)>();
        foreach (var r in reflections.SelectMany(r => r.Resources))
        {
            var type = r.Kind switch
            {
                SpirvReflection.Kind.UniformBuffer => DescriptorType.UniformBuffer,
                SpirvReflection.Kind.StorageBuffer => DescriptorType.StorageBuffer,
                SpirvReflection.Kind.SampledImage => DescriptorType.SampledImage,
                SpirvReflection.Kind.StorageImage => DescriptorType.StorageImage,
                SpirvReflection.Kind.Sampler => DescriptorType.Sampler,
                SpirvReflection.Kind.CombinedImageSampler => DescriptorType.CombinedImageSampler,
                _ => throw new NotSupportedException($"{r.Name}: {r.Kind}"),
            };
            var count = (uint)(r.ArrayLength == 0 ? 4096 : Math.Max(1, r.ArrayLength));
            if (layout.TryGetValue((r.Set, r.Binding), out var had) && had.Type != type)
                throw new NotSupportedException($"set {r.Set} binding {r.Binding}: {had.Type} and {type}");
            layout[(r.Set, r.Binding)] = (type, Math.Max(count, had.Count));
        }
        var setCount = layout.Count == 0 ? 0 : layout.Keys.Max(k => k.Set) + 1;
        var setLayouts = new DescriptorSetLayout[setCount];
        for (var s = 0; s < setCount; s++)
        {
            var list = layout.Where(b => b.Key.Set == s).OrderBy(b => b.Key.Binding).ToArray();
            var lb = stackalloc DescriptorSetLayoutBinding[Math.Max(1, list.Length)];
            var flags = stackalloc DescriptorBindingFlags[Math.Max(1, list.Length)];
            for (var i = 0; i < list.Length; i++)
            {
                lb[i] = new DescriptorSetLayoutBinding((uint)list[i].Key.Binding, list[i].Value.Type, list[i].Value.Count, stages);
                flags[i] = DescriptorBindingFlags.PartiallyBoundBit;
            }
            var flagInfo = new DescriptorSetLayoutBindingFlagsCreateInfo { SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo, BindingCount = (uint)list.Length, PBindingFlags = flags };
            var info = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, PNext = &flagInfo, BindingCount = (uint)list.Length, PBindings = lb };
            GpuDevice.Check(_vk.CreateDescriptorSetLayout(gpu.Device, &info, null, out setLayouts[s]), "vkCreateDescriptorSetLayout");
        }
        var sizes = layout.Values.GroupBy(b => b.Type).Select(g => new DescriptorPoolSize(g.Key, (uint)g.Sum(x => x.Count))).ToArray();
        DescriptorPool pool = default;
        var sets = new DescriptorSet[setCount];
        PipelineLayout pipelineLayout;
        fixed (DescriptorPoolSize* psz = sizes)
        fixed (DescriptorSetLayout* pl = setLayouts)
        fixed (DescriptorSet* pset = sets)
        {
            if (setCount > 0)
            {
                var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = (uint)setCount, PoolSizeCount = (uint)sizes.Length, PPoolSizes = psz };
                GpuDevice.Check(_vk.CreateDescriptorPool(gpu.Device, &poolInfo, null, out pool), "vkCreateDescriptorPool");
                var alloc = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = pool, DescriptorSetCount = (uint)setCount, PSetLayouts = pl };
                GpuDevice.Check(_vk.AllocateDescriptorSets(gpu.Device, &alloc, pset), "vkAllocateDescriptorSets");
            }
            var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = (uint)setCount, PSetLayouts = pl };
            GpuDevice.Check(_vk.CreatePipelineLayout(gpu.Device, &layoutInfo, null, out pipelineLayout), "vkCreatePipelineLayout");
        }

        foreach (var (set, binding, element, buffer, offset, range) in bindings.Buffers)
        {
            if (!layout.TryGetValue((set, binding), out var b))
                continue;
            var info = new DescriptorBufferInfo(buffer.Buffer, offset, range);
            var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = sets[set], DstBinding = (uint)binding, DstArrayElement = element, DescriptorCount = 1, DescriptorType = b.Type, PBufferInfo = &info };
            _vk.UpdateDescriptorSets(gpu.Device, 1, &write, 0, null);
        }
        foreach (var (set, binding, element, view) in bindings.Images)
        {
            if (!layout.TryGetValue((set, binding), out var b))
                continue;
            var info = new DescriptorImageInfo(default, view, ImageLayout.General);
            var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = sets[set], DstBinding = (uint)binding, DstArrayElement = element, DescriptorCount = 1, DescriptorType = b.Type, PImageInfo = &info };
            _vk.UpdateDescriptorSets(gpu.Device, 1, &write, 0, null);
        }
        foreach (var (set, binding, element, sampler) in bindings.Samplers)
        {
            if (!layout.TryGetValue((set, binding), out var b))
                continue;
            var info = new DescriptorImageInfo(sampler, default, ImageLayout.Undefined);
            var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = sets[set], DstBinding = (uint)binding, DstArrayElement = element, DescriptorCount = 1, DescriptorType = DescriptorType.Sampler, PImageInfo = &info };
            _vk.UpdateDescriptorSets(gpu.Device, 1, &write, 0, null);
        }

        return new Descriptors(_vk, gpu.Device, setLayouts, pool, sets, pipelineLayout);
    }

    private ShaderModule Module(byte[] spirv)
    {
        fixed (byte* p = spirv)
        {
            var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)p };
            GpuDevice.Check(_vk.CreateShaderModule(gpu.Device, &info, null, out var module), "vkCreateShaderModule");
            return module;
        }
    }
}
