using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Source2.Compiler.Lighting;
using Source2.Compiler.Lighting.Bake;

namespace Source2.Compiler.Gpu;

/// <summary>
/// vrad3's large-chart sample pass on the GPU (lightmap_compute_block_gpu, 180030100: "Sampling Large
/// Charts"): Valve's lightmap_rasterize vertex, geometry and pixel programs (vulkan 50) drawn twice over
/// the block's large-chart triangles into a block-sized depth target. D_PASS 0 writes each triangle's
/// TEA-hashed depth (depth LessEqual, written; the geometry shader's hash, the pixel shader's chart id
/// discard), D_PASS 1 writes the samples where the depth is equal. g_vWindow is the block's uv rect
/// jittered by the pass's two SFMT words (y first, as 180030100 draws them).
/// D_REVERSE_DEPTH stays 0 (rendersystem's choice is not read; it only changes which of two
/// overlapping triangles of one chart owns a luxel).
/// </summary>
public sealed unsafe partial class RtxBakeHost
{
    private Pipeline _rasterDepth, _rasterSamples;
    private PipelineLayout _rasterLayout;
    private readonly DescriptorSetLayout[] _rasterSetLayouts = new DescriptorSetLayout[3];
    private RenderPass _rasterPass;
    private DescriptorPool _rasterPool;

    private void BuildRaster(RtxShaderLibrary shaders)
    {
        var vsProgram = shaders.ProgramFile50("lightmap_rasterize", "50_vs") ?? throw new FileNotFoundException("lightmap_rasterize_vulkan_50_vs");
        var gsProgram = shaders.ProgramFile50("lightmap_rasterize", "50_gs") ?? throw new FileNotFoundException("lightmap_rasterize_vulkan_50_gs");
        var psProgram = shaders.ProgramFile50("lightmap_rasterize", "50_ps") ?? throw new FileNotFoundException("lightmap_rasterize_vulkan_50_ps");
        var vs = Module(Spirv(vsProgram, 0, 0));
        var gs = Module(Spirv(gsProgram, 0, 0));
        var pass = psProgram.DynamicComboArray.Single(c => c.Name == "D_PASS").ComboIndexValue;
        var ps0 = Module(Spirv(psProgram, 0, 0));
        var ps1 = Module(Spirv(psProgram, 0, pass));

        (int Set, uint Binding, DescriptorType Type, ShaderStageFlags Stage)[] bindings =
        [
            (0, 0, DescriptorType.UniformBuffer, ShaderStageFlags.VertexBit),
            (1, 0, DescriptorType.UniformBuffer, ShaderStageFlags.FragmentBit),
            (1, 30, DescriptorType.SampledImage, ShaderStageFlags.FragmentBit),
            (1, 31, DescriptorType.StorageBuffer, ShaderStageFlags.FragmentBit),
            (1, 158, DescriptorType.StorageBuffer, ShaderStageFlags.FragmentBit),
            (2, 30, DescriptorType.StorageBuffer, ShaderStageFlags.GeometryBit),
        ];
        for (var set = 0; set < 3; set++)
        {
            var lb = bindings.Where(b => b.Set == set).Select(b => new DescriptorSetLayoutBinding(b.Binding, b.Type, 1, b.Stage)).ToArray();
            fixed (DescriptorSetLayoutBinding* pb = lb)
            {
                var info = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = (uint)lb.Length, PBindings = pb };
                GpuDevice.Check(_vk.CreateDescriptorSetLayout(_gpu.Device, &info, null, out _rasterSetLayouts[set]), "vkCreateDescriptorSetLayout");
            }
        }
        fixed (DescriptorSetLayout* pl = _rasterSetLayouts)
        {
            var info = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 3, PSetLayouts = pl };
            GpuDevice.Check(_vk.CreatePipelineLayout(_gpu.Device, &info, null, out _rasterLayout), "vkCreatePipelineLayout");
        }
        DescriptorPoolSize[] sizes = [new(DescriptorType.UniformBuffer, 8), new(DescriptorType.SampledImage, 4), new(DescriptorType.StorageBuffer, 12)];
        fixed (DescriptorPoolSize* ps = sizes)
        {
            var info = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit, MaxSets = 12, PoolSizeCount = 3, PPoolSizes = ps };
            GpuDevice.Check(_vk.CreateDescriptorPool(_gpu.Device, &info, null, out _rasterPool), "vkCreateDescriptorPool");
        }

        // A depth-only pass (the pixel shader's colour output has no attachment), cleared to 1.
        var depth = new AttachmentDescription
        {
            Format = Format.D32Sfloat, Samples = SampleCountFlags.Count1Bit, LoadOp = AttachmentLoadOp.Load, StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare, StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.DepthStencilAttachmentOptimal, FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };
        var depthRef = new AttachmentReference(0, ImageLayout.DepthStencilAttachmentOptimal);
        var subpass = new SubpassDescription { PipelineBindPoint = PipelineBindPoint.Graphics, PDepthStencilAttachment = &depthRef };
        var rpInfo = new RenderPassCreateInfo { SType = StructureType.RenderPassCreateInfo, AttachmentCount = 1, PAttachments = &depth, SubpassCount = 1, PSubpasses = &subpass };
        GpuDevice.Check(_vk.CreateRenderPass(_gpu.Device, &rpInfo, null, out _rasterPass), "vkCreateRenderPass");

        _rasterDepth = GraphicsPipeline(vs, gs, ps0, write: true, CompareOp.LessOrEqual);
        _rasterSamples = GraphicsPipeline(vs, gs, ps1, write: false, CompareOp.Equal);
    }

    private Pipeline GraphicsPipeline(ShaderModule vs, ShaderModule gs, ShaderModule ps, bool write, CompareOp op)
    {
        var names = new[] { SilkMarshal.StringToPtr("MainVs"), SilkMarshal.StringToPtr("MainGs"), SilkMarshal.StringToPtr("MainPs") };
        var stages = stackalloc PipelineShaderStageCreateInfo[3];
        stages[0] = new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vs, PName = (byte*)names[0] };
        stages[1] = new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.GeometryBit, Module = gs, PName = (byte*)names[1] };
        stages[2] = new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = ps, PName = (byte*)names[2] };
        // BlockGeometry.Vertex as uploaded: position (float3), atlas uv (float2), packed frame (uint), 24 bytes.
        var binding = new VertexInputBindingDescription(0, 24, VertexInputRate.Vertex);
        var attrs = stackalloc VertexInputAttributeDescription[3];
        attrs[0] = new(0, 0, Format.R32G32B32Sfloat, 0);
        attrs[1] = new(1, 0, Format.R32G32Sfloat, 12);
        attrs[2] = new(2, 0, Format.R32Uint, 20);
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo, VertexBindingDescriptionCount = 1, PVertexBindingDescriptions = &binding,
            VertexAttributeDescriptionCount = 3, PVertexAttributeDescriptions = attrs,
        };
        var assembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
        var viewport = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        var raster = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None,
            FrontFace = FrontFace.Clockwise, LineWidth = 1f, DepthClampEnable = false,
        };
        var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
        var depthState = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = true, DepthWriteEnable = write, DepthCompareOp = op,
        };
        var blend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo };
        var dynamicStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };
        var info = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 3, PStages = stages, PVertexInputState = &vertexInput,
            PInputAssemblyState = &assembly, PViewportState = &viewport, PRasterizationState = &raster, PMultisampleState = &multisample,
            PDepthStencilState = &depthState, PColorBlendState = &blend, PDynamicState = &dynamic, Layout = _rasterLayout, RenderPass = _rasterPass,
        };
        GpuDevice.Check(_vk.CreateGraphicsPipelines(_gpu.Device, default, 1, &info, null, out var pipeline), "vkCreateGraphicsPipelines");
        foreach (var n in names)
            SilkMarshal.Free(n);
        return pipeline;
    }

    /// <summary>
    /// The block's large-chart samples (BlockGeometry's triangles before SmallStart) into the device's
    /// sample buffer, with the pass's jitter: g_vWindow = (ox / W + jx, oy / H + jy, (ox + w) / W + jx,
    /// (oy + h) / H + jy). Luxels no triangle covers keep what WriteSamples left (empty or a small chart).
    /// </summary>
    private void RasterBlock(BlockGeometry g, PackingGeometry packing, int ox, int oy, int lightmapWidth, int lightmapHeight, float jx, float jy)
    {
        if (_rasterPass.Handle == 0)
            throw new InvalidOperationException("the host was built without the raster sample pass");
        var triangles = g.SmallStart;
        if (triangles == 0)
            return;
        var vertexBytes = new byte[triangles * 3 * 24];
        for (var v = 0; v < triangles * 3; v++)
        {
            var x = g.Vertices[v];
            var f = MemoryMarshal.Cast<byte, float>(vertexBytes.AsSpan(v * 24, 20));
            (f[0], f[1], f[2], f[3], f[4]) = (x.Position.X, x.Position.Y, x.Position.Z, x.Uv.X, x.Uv.Y);
            BitConverter.TryWriteBytes(vertexBytes.AsSpan(v * 24 + 20), x.Frame);
        }
        var chartInfo = new uint[packing.Charts.Length * 2];
        for (var c = 0; c < packing.Charts.Length; c++)
            (chartInfo[c * 2], chartInfo[c * 2 + 1]) = (packing.Charts[c].Color, packing.Charts[c].Flags & 0x0fffffff);
        float[] window =
        [
            (float)ox / lightmapWidth + jx, (float)oy / lightmapHeight + jy,
            (float)(ox + _w) / lightmapWidth + jx, (float)(oy + _h) / lightmapHeight + jy,
        ];
        var made = new List<GpuDevice.GpuBuffer>
        {
            _gpu.Upload(vertexBytes, BufferUsageFlags.VertexBufferBit),
            _gpu.Upload(Bytes(window), BufferUsageFlags.UniformBufferBit),
            _gpu.Upload(Bytes(new[] { _w, 0, 0, 0 }), BufferUsageFlags.UniformBufferBit),
            _gpu.Upload(Bytes(chartInfo), BufferUsageFlags.StorageBufferBit),
            _gpu.Upload(Bytes(g.Charts[..triangles]), BufferUsageFlags.StorageBufferBit),
        };
        var depth = DepthImage((uint)_w, (uint)_h);
        var view = depth.View;
        var fbInfo = new FramebufferCreateInfo { SType = StructureType.FramebufferCreateInfo, RenderPass = _rasterPass, AttachmentCount = 1, PAttachments = &view, Width = (uint)_w, Height = (uint)_h, Layers = 1 };
        GpuDevice.Check(_vk.CreateFramebuffer(_gpu.Device, &fbInfo, null, out var framebuffer), "vkCreateFramebuffer");
        var sets = new DescriptorSet[3];
        fixed (DescriptorSetLayout* pl = _rasterSetLayouts)
        fixed (DescriptorSet* ps = sets)
        {
            var info = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _rasterPool, DescriptorSetCount = 3, PSetLayouts = pl };
            GpuDevice.Check(_vk.AllocateDescriptorSets(_gpu.Device, &info, ps), "vkAllocateDescriptorSets");
        }
        WriteBuffers(sets[0], 0, DescriptorType.UniformBuffer, made[1]);
        WriteBuffers(sets[1], 0, DescriptorType.UniformBuffer, made[2]);
        WriteImages(sets[1], 30, DescriptorType.SampledImage, [_chartIds!.View], ImageLayout.ShaderReadOnlyOptimal);
        WriteBuffers(sets[1], 31, DescriptorType.StorageBuffer, made[3]);
        WriteBuffers(sets[1], 158, DescriptorType.StorageBuffer, _samples!);
        WriteBuffers(sets[2], 30, DescriptorType.StorageBuffer, made[4]);
        _gpu.Submit(cmd =>
        {
            // Depth cleared to 1 outside the render pass, then the two draws.
            var range = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1);
            var toClear = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier, OldLayout = ImageLayout.Undefined, NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Image = depth.Image, SubresourceRange = range,
                DstAccessMask = AccessFlags.TransferWriteBit,
            };
            _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &toClear);
            var clear = new ClearDepthStencilValue(1f, 0);
            _vk.CmdClearDepthStencilImage(cmd, depth.Image, ImageLayout.TransferDstOptimal, &clear, 1, &range);
            var toDepth = toClear with
            {
                OldLayout = ImageLayout.TransferDstOptimal, NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
                SrcAccessMask = AccessFlags.TransferWriteBit, DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
            };
            _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.EarlyFragmentTestsBit, 0, 0, null, 0, null, 1, &toDepth);
            var viewportBox = new Viewport(0, 0, _w, _h, 0, 1);
            var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)_w, (uint)_h));
            var vb = made[0].Buffer;
            ulong offset = 0;
            foreach (var pipeline in new[] { _rasterDepth, _rasterSamples })
            {
                var begin = new RenderPassBeginInfo { SType = StructureType.RenderPassBeginInfo, RenderPass = _rasterPass, Framebuffer = framebuffer, RenderArea = scissor };
                _vk.CmdBeginRenderPass(cmd, &begin, SubpassContents.Inline);
                _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
                _vk.CmdSetViewport(cmd, 0, 1, &viewportBox);
                _vk.CmdSetScissor(cmd, 0, 1, &scissor);
                fixed (DescriptorSet* ps = sets)
                    _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _rasterLayout, 0, 3, ps, 0, null);
                _vk.CmdBindVertexBuffers(cmd, 0, 1, &vb, &offset);
                _vk.CmdDraw(cmd, (uint)(triangles * 3), 1, 0, 0);
                _vk.CmdEndRenderPass(cmd);
            }
            var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.HostReadBit };
            _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.RayTracingShaderBitKhr | PipelineStageFlags.HostBit, 0, 1, &barrier, 0, null, 0, null);
        });
        fixed (DescriptorSet* ps = sets)
            _vk.FreeDescriptorSets(_gpu.Device, _rasterPool, 3, ps);
        _vk.DestroyFramebuffer(_gpu.Device, framebuffer, null);
        DestroyImage(depth);
        foreach (var b in made)
            _gpu.Destroy(b);
    }

    private GpuDevice.GpuImage DepthImage(uint w, uint h)
    {
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D, Format = Format.D32Sfloat, Extent = new Extent3D(w, h, 1),
            MipLevels = 1, ArrayLayers = 1, Samples = SampleCountFlags.Count1Bit, Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.TransferDstBit, SharingMode = SharingMode.Exclusive, InitialLayout = ImageLayout.Undefined,
        };
        GpuDevice.Check(_vk.CreateImage(_gpu.Device, &info, null, out var image), "vkCreateImage");
        _vk.GetImageMemoryRequirements(_gpu.Device, image, out var req);
        var alloc = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = _gpu.MemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit) };
        GpuDevice.Check(_vk.AllocateMemory(_gpu.Device, &alloc, null, out var memory), "vkAllocateMemory");
        _vk.BindImageMemory(_gpu.Device, image, memory, 0);
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo, Image = image, ViewType = ImageViewType.Type2D, Format = Format.D32Sfloat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1),
        };
        GpuDevice.Check(_vk.CreateImageView(_gpu.Device, &viewInfo, null, out var view), "vkCreateImageView");
        return new GpuDevice.GpuImage(image, memory, view, Format.D32Sfloat, w, h, 1);
    }

    /// <summary>Validates the device's current samples in place (rtx_vrad3_lightmap_sample_validation).</summary>
    public void ValidateSamples() => Trace(_validation, _validationSet, null, (uint)_w, (uint)_h, 1);

    private void DestroyRaster()
    {
        if (_rasterPass.Handle == 0)
            return;
        _vk.DestroyPipeline(_gpu.Device, _rasterDepth, null);
        _vk.DestroyPipeline(_gpu.Device, _rasterSamples, null);
        _vk.DestroyRenderPass(_gpu.Device, _rasterPass, null);
        _vk.DestroyPipelineLayout(_gpu.Device, _rasterLayout, null);
        _vk.DestroyDescriptorPool(_gpu.Device, _rasterPool, null);
        foreach (var l in _rasterSetLayouts)
            _vk.DestroyDescriptorSetLayout(_gpu.Device, l, null);
    }
}
