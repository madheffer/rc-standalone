using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Source2.Compiler.Lighting.Bake;
using Source2.Compiler.Lighting.Bake.Glsl;
using Source2.Compiler.Lighting.Bake.Glsl.Raygen;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.ResourceTypes;

namespace Source2.Compiler.Gpu;

/// <summary>
/// vrad3's lightmap ray tracing on the GPU with Valve's own SPIR-V: the ray generation shaders
/// (rtx_vrad3_lightmap, rtx_vrad3_lightmap_sample_validation) and every material's hit groups
/// (its shader's rtx program, ClosestHitN / AnyHitN for ray type N; rtx_default_hitgroup without
/// one) in one Vulkan ray tracing pipeline each, over the buffers the CPU port builds
/// (RtxScene: SceneVertexLayout's vertex buffers, GeometryInfo, material blocks, the instance
/// table; RaygenHost: scene globals, lights, light bins).
/// <list type="bullet">
/// <item>One BLAS per mesh (scenesystem 1800912d0 'BLAS %d': one triangle geometry, 32-bit
/// indices), built prefer-fast-trace with compaction allowed (its flags 0xa; the rendersystem
/// enum is not yet read, see the ledger); one TLAS 'SceneTLAS' over the instances in scene order:
/// custom index the mesh (g_Geometries), mask the instance's, SBT offset 2 x its hit group,
/// facing cull disabled where RayScene disables it. Nothing is forced opaque: shadow rays
/// (payload 1, visibility 1) are only darkened by the hit groups' AnyHit1, opaque materials
/// included, and the primary rays' opaque groups have no any-hit.</item>
/// <item>The SBT as 18009b330 names it: Miss0 / Miss1 (no shader: vrad3 has none, so a miss
/// leaves the payload), then per material shader and ray type N the group "&lt;shader&gt;:N".</item>
/// <item>Descriptor sets 3 (scene), 4 (bindless textures and samplers), 5 (PerViewConstantBuffer,
/// zeros), 6 (the pass: luxels, samples, light bins, Halton volume, chart ids, convergence).</item>
/// </list>
/// </summary>
public sealed unsafe class RtxBakeHost : ILightmapDevice, IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly Vk _vk;
    private readonly List<GpuDevice.GpuBuffer> _buffers = [];
    private readonly List<GpuDevice.GpuImage> _images = [];
    private readonly List<Sampler> _samplers = [];
    private readonly List<ShaderModule> _modules = [];
    private readonly List<AccelerationStructureKHR> _structures = [];

    // VK_KHR_acceleration_structure / VK_KHR_ray_tracing_pipeline entry points.
    private readonly delegate* unmanaged<Device, AccelerationStructureCreateInfoKHR*, AllocationCallbacks*, AccelerationStructureKHR*, Result> _createAs;
    private readonly delegate* unmanaged<Device, AccelerationStructureKHR, AllocationCallbacks*, void> _destroyAs;
    private readonly delegate* unmanaged<Device, AccelerationStructureBuildTypeKHR, AccelerationStructureBuildGeometryInfoKHR*, uint*, AccelerationStructureBuildSizesInfoKHR*, void> _buildSizes;
    private readonly delegate* unmanaged<CommandBuffer, uint, AccelerationStructureBuildGeometryInfoKHR*, AccelerationStructureBuildRangeInfoKHR**, void> _cmdBuild;
    private readonly delegate* unmanaged<Device, AccelerationStructureDeviceAddressInfoKHR*, ulong> _asAddress;
    private readonly delegate* unmanaged<Device, DeferredOperationKHR, PipelineCache, uint, RayTracingPipelineCreateInfoKHR*, AllocationCallbacks*, Pipeline*, Result> _createPipelines;
    private readonly delegate* unmanaged<Device, Pipeline, uint, uint, nuint, void*, Result> _groupHandles;
    private readonly delegate* unmanaged<CommandBuffer, StridedDeviceAddressRegionKHR*, StridedDeviceAddressRegionKHR*, StridedDeviceAddressRegionKHR*, StridedDeviceAddressRegionKHR*, uint, uint, uint, void> _cmdTraceRays;

    private readonly uint _handleSize, _handleAlignment, _baseAlignment;
    private readonly AccelerationStructureKHR _tlas;
    private readonly DescriptorSetLayout[] _setLayouts = new DescriptorSetLayout[7];
    private readonly PipelineLayout _pipelineLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _scene, _bindless, _view;
    private readonly (Pipeline Pipeline, GpuDevice.GpuBuffer Sbt, StridedDeviceAddressRegionKHR[] Regions) _lightmap, _validation;
    private readonly int _meshes, _textures, _samplerCount;

    /// <summary>What the host could not do as vrad3 does (shown by the caller).</summary>
    public List<string> Notes { get; } = [];

    /// <param name="sh">rtx_vrad3_lightmap's D_SH_LIGHTMAPS (the CPU port runs 0).</param>
    /// <param name="executionReordering">rtx_vrad3_lightmap's S_EXECUTION_REORDERING (VK_NV_ray_tracing_invocation_reorder; not enabled here).</param>
    public RtxBakeHost(GpuDevice gpu, BakeScene scene, RtxScene rtx, RtxShaderLibrary shaders, Func<string, byte[]?> read, int sh = 0, int executionReordering = 0)
    {
        if (!gpu.RayTracing)
            throw new ArgumentException("the device was created without ray tracing", nameof(gpu));
        _gpu = gpu;
        _vk = gpu.Vk;
        _createAs = (delegate* unmanaged<Device, AccelerationStructureCreateInfoKHR*, AllocationCallbacks*, AccelerationStructureKHR*, Result>)gpu.Proc("vkCreateAccelerationStructureKHR");
        _destroyAs = (delegate* unmanaged<Device, AccelerationStructureKHR, AllocationCallbacks*, void>)gpu.Proc("vkDestroyAccelerationStructureKHR");
        _buildSizes = (delegate* unmanaged<Device, AccelerationStructureBuildTypeKHR, AccelerationStructureBuildGeometryInfoKHR*, uint*, AccelerationStructureBuildSizesInfoKHR*, void>)gpu.Proc("vkGetAccelerationStructureBuildSizesKHR");
        _cmdBuild = (delegate* unmanaged<CommandBuffer, uint, AccelerationStructureBuildGeometryInfoKHR*, AccelerationStructureBuildRangeInfoKHR**, void>)gpu.Proc("vkCmdBuildAccelerationStructuresKHR");
        _asAddress = (delegate* unmanaged<Device, AccelerationStructureDeviceAddressInfoKHR*, ulong>)gpu.Proc("vkGetAccelerationStructureDeviceAddressKHR");
        _createPipelines = (delegate* unmanaged<Device, DeferredOperationKHR, PipelineCache, uint, RayTracingPipelineCreateInfoKHR*, AllocationCallbacks*, Pipeline*, Result>)gpu.Proc("vkCreateRayTracingPipelinesKHR");
        _groupHandles = (delegate* unmanaged<Device, Pipeline, uint, uint, nuint, void*, Result>)gpu.Proc("vkGetRayTracingShaderGroupHandlesKHR");
        _cmdTraceRays = (delegate* unmanaged<CommandBuffer, StridedDeviceAddressRegionKHR*, StridedDeviceAddressRegionKHR*, StridedDeviceAddressRegionKHR*, StridedDeviceAddressRegionKHR*, uint, uint, uint, void>)gpu.Proc("vkCmdTraceRaysKHR");

        var rtProps = new PhysicalDeviceRayTracingPipelinePropertiesKHR { SType = StructureType.PhysicalDeviceRayTracingPipelinePropertiesKhr };
        var props2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &rtProps };
        _vk.GetPhysicalDeviceProperties2(gpu.Physical, &props2);
        _handleSize = rtProps.ShaderGroupHandleSize;
        _handleAlignment = rtProps.ShaderGroupHandleAlignment;
        _baseAlignment = rtProps.ShaderGroupBaseAlignment;

        var ctx = rtx.NewContext();
        _meshes = scene.Meshes.Count;

        // Hit groups: one per distinct (rtx program, static combo), in first-use order.
        var groups = new List<HitShader>();
        var groupOf = new int[scene.Instances.Count];
        var missing = new SortedSet<string>();
        for (var i = 0; i < scene.Instances.Count; i++)
        {
            if (rtx.ShaderOf(i) is not { } shader)
            {
                missing.Add(scene.Instances[i].Material.Name);
                continue;
            }
            var g = groups.FindIndex(h => h.Name == shader.Name && h.Combo == shader.Combo);
            if (g < 0)
            {
                g = groups.Count;
                groups.Add(shader);
            }
            groupOf[i] = g;
        }
        if (missing.Count > 0)
            throw new NotSupportedException($"no hit group for {missing.Count} materials: {string.Join(", ", missing.Take(8))}");

        // Geometry: vertex and index buffers as the hit groups read them, positions for the BLAS.
        const BufferUsageFlags asInput = BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.StorageBufferBit;
        var vertexBuffers = new GpuDevice.GpuBuffer[_meshes];
        var indexBuffers = new GpuDevice.GpuBuffer[_meshes];
        var positionBuffers = new GpuDevice.GpuBuffer[_meshes];
        for (var m = 0; m < _meshes; m++)
        {
            vertexBuffers[m] = Keep(gpu.Upload(Bytes(ctx.VerticesU[m]), BufferUsageFlags.StorageBufferBit));
            indexBuffers[m] = Keep(gpu.Upload(Bytes(ctx.Indices[m]), asInput));
            var positions = scene.Meshes[m].Positions;
            var p = new float[positions.Length * 3];
            for (var v = 0; v < positions.Length; v++)
                (p[v * 3], p[v * 3 + 1], p[v * 3 + 2]) = (positions[v].X, positions[v].Y, positions[v].Z);
            positionBuffers[m] = Keep(gpu.Upload(Bytes(p), asInput));
        }
        var blas = new ulong[_meshes];
        for (var m = 0; m < _meshes; m++)
            blas[m] = BuildBlas(positionBuffers[m], scene.Meshes[m].Positions.Length, indexBuffers[m], ctx.Indices[m].Length / 3);
        _tlas = BuildTlas(scene, groupOf);

        // Set 3: the scene.
        var geometries = Keep(gpu.Upload(ctx.Geometries.SelectMany(GeometryBytes).ToArray(), BufferUsageFlags.StorageBufferBit));
        var material = Keep(gpu.Upload(Bytes(ctx.MatU), BufferUsageFlags.StorageBufferBit));
        var instances = Keep(gpu.Upload(Bytes(ctx.Instances), BufferUsageFlags.StorageBufferBit));
        var lights = Keep(gpu.Upload(scene.Lights.Count == 0 ? new byte[496] : scene.Lights.SelectMany(l => LightBytes(RaygenHost.GpuLight(l))).ToArray(), BufferUsageFlags.StorageBufferBit));
        var global = Keep(gpu.Upload(GlobalBytes(RaygenHost.Globals(scene)), BufferUsageFlags.StorageBufferBit));
        var subsurface = Keep(gpu.Upload(new byte[11792], BufferUsageFlags.UniformBufferBit));
        _lightBins = Keep(gpu.Upload(Bytes(RaygenHost.LightBins(scene)), BufferUsageFlags.StorageBufferBit));
        if (scene.Global.IblCubeMap != null)
            throw new NotSupportedException("an IBL cube map: not uploaded to the GPU yet");
        if (scene.Lights.Any(l => l.Cookie >= 0))
            Notes.Add("light cookies: g_Sampler / g_CookieSampler states not read from rendersystem (linear clamp here)");

        // Set 4: the bindless textures and samplers.
        var views = new List<ImageView>();
        foreach (var source in rtx.TextureSources)
        {
            if (source.Path == null)
            {
                var image = Keep(gpu.CreateImage(1, 1, 1, Format.R32G32B32A32Sfloat, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit));
                var texel = new byte[16];
                for (var c = 0; c < 4; c++)
                    BinaryPrimitives.WriteSingleLittleEndian(texel.AsSpan(c * 4), c switch { 0 => source.Constant.x, 1 => source.Constant.y, 2 => source.Constant.z, _ => source.Constant.w });
                UploadImage(image, texel, 1, 1, 1);
                views.Add(image.View);
                continue;
            }
            using var res = new ValveResourceFormat.Resource();
            res.Read(new MemoryStream(read(source.Path + "_c") ?? throw new FileNotFoundException(source.Path)));
            views.Add(Keep(GpuTextures.Upload(gpu, (Texture)res.DataBlock!, source.Srgb, RtxScene.TextureCap)).View);
        }
        _textures = views.Count;
        var samplers = ctx.Samplers.Select(s => Keep(CreateSampler(s.AddressU, s.AddressV, s.Linear, s.Mipmaps))).ToArray();
        _samplerCount = samplers.Length;
        var sceneSampler = Keep(CreateSampler(2, 2, true, true));
        var cookieSampler = Keep(CreateSampler(2, 2, true, true));
        var perView = Keep(gpu.Upload(new byte[640], BufferUsageFlags.UniformBufferBit));

        // Layouts: sets 0 to 2 empty, 3 to 6 as Valve's programs declare them.
        const ShaderStageFlags rt = ShaderStageFlags.RaygenBitKhr | ShaderStageFlags.ClosestHitBitKhr | ShaderStageFlags.AnyHitBitKhr | ShaderStageFlags.MissBitKhr;
        (int Set, uint Binding, DescriptorType Type, uint Count)[] bindings =
        [
            (3, 0, DescriptorType.UniformBuffer, 1), (3, 14, DescriptorType.Sampler, 1), (3, 15, DescriptorType.Sampler, 1),
            (3, 30, DescriptorType.AccelerationStructureKhr, 1), (3, 31, DescriptorType.StorageBuffer, 1),
            (3, 32, DescriptorType.StorageBuffer, (uint)_meshes), (3, 33, DescriptorType.StorageBuffer, (uint)_meshes),
            (3, 34, DescriptorType.StorageBuffer, 1), (3, 35, DescriptorType.StorageBuffer, 1), (3, 36, DescriptorType.StorageBuffer, 1), (3, 37, DescriptorType.StorageBuffer, 1),
            (4, 29, DescriptorType.Sampler, 2048), (4, 46, DescriptorType.SampledImage, 65536),
            (5, 0, DescriptorType.UniformBuffer, 1),
            (6, 30, DescriptorType.SampledImage, 1), (6, 158, DescriptorType.StorageBuffer, 1), (6, 159, DescriptorType.StorageBuffer, 1),
            (6, 160, DescriptorType.StorageBuffer, 1), (6, 161, DescriptorType.StorageImage, 1), (6, 162, DescriptorType.StorageImage, 1),
        ];
        for (var set = 0; set < 7; set++)
        {
            var mine = bindings.Where(b => b.Set == set).ToArray();
            var lb = mine.Select(b => new DescriptorSetLayoutBinding(b.Binding, b.Type, b.Count, rt)).ToArray();
            var flags = mine.Select(_ => DescriptorBindingFlags.PartiallyBoundBit).ToArray();
            fixed (DescriptorSetLayoutBinding* pb = lb)
            fixed (DescriptorBindingFlags* pf = flags)
            {
                var flagInfo = new DescriptorSetLayoutBindingFlagsCreateInfo { SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo, BindingCount = (uint)lb.Length, PBindingFlags = pf };
                var info = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, PNext = &flagInfo, BindingCount = (uint)lb.Length, PBindings = pb };
                GpuDevice.Check(_vk.CreateDescriptorSetLayout(gpu.Device, &info, null, out _setLayouts[set]), "vkCreateDescriptorSetLayout");
            }
        }
        var push = new PushConstantRange(ShaderStageFlags.RaygenBitKhr, 0, 64);
        fixed (DescriptorSetLayout* pl = _setLayouts)
        {
            var info = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 7, PSetLayouts = pl, PushConstantRangeCount = 1, PPushConstantRanges = &push };
            GpuDevice.Check(_vk.CreatePipelineLayout(gpu.Device, &info, null, out _pipelineLayout), "vkCreatePipelineLayout");
        }
        var poolSizes = bindings.GroupBy(b => b.Type).Select(g => new DescriptorPoolSize(g.Key, (uint)g.Sum(b => b.Count) * 2)).ToArray();
        fixed (DescriptorPoolSize* ps = poolSizes)
        {
            var info = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit, MaxSets = 16, PoolSizeCount = (uint)poolSizes.Length, PPoolSizes = ps };
            GpuDevice.Check(_vk.CreateDescriptorPool(gpu.Device, &info, null, out _pool), "vkCreateDescriptorPool");
        }
        _scene = Allocate(3);
        _bindless = Allocate(4);
        _view = Allocate(5);
        WriteBuffers(_scene, 0, DescriptorType.UniformBuffer, subsurface);
        WriteSamplers(_scene, 14, [sceneSampler]);
        WriteSamplers(_scene, 15, [cookieSampler]);
        var tlas = _tlas;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR { SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1, PAccelerationStructures = &tlas };
        var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstSet = _scene, DstBinding = 30, DescriptorCount = 1, DescriptorType = DescriptorType.AccelerationStructureKhr };
        _vk.UpdateDescriptorSets(gpu.Device, 1, &write, 0, null);
        WriteBuffers(_scene, 31, DescriptorType.StorageBuffer, geometries);
        WriteBuffers(_scene, 32, DescriptorType.StorageBuffer, vertexBuffers);
        WriteBuffers(_scene, 33, DescriptorType.StorageBuffer, indexBuffers);
        WriteBuffers(_scene, 34, DescriptorType.StorageBuffer, material);
        WriteBuffers(_scene, 35, DescriptorType.StorageBuffer, instances);
        WriteBuffers(_scene, 36, DescriptorType.StorageBuffer, lights);
        WriteBuffers(_scene, 37, DescriptorType.StorageBuffer, global);
        if (samplers.Length > 0)
            WriteSamplers(_bindless, 29, samplers);
        if (views.Count > 0)
            WriteImages(_bindless, 46, DescriptorType.SampledImage, [.. views], ImageLayout.ShaderReadOnlyOptimal);
        WriteBuffers(_view, 0, DescriptorType.UniformBuffer, perView);

        // The two pipelines: Valve's raygen, two null misses, the hit groups.
        var hitModules = groups.Select(h =>
        {
            var program = shaders.Get(h.Name.Replace("_vulkan_60_rtx", "", StringComparison.Ordinal)) ?? throw new FileNotFoundException(h.Name);
            return Spirv(program.Rtx, h.Combo, 0);
        }).ToArray();
        var lightmapProgram = shaders.Get("rtx_vrad3_lightmap") ?? throw new FileNotFoundException("rtx_vrad3_lightmap");
        var lightmapStatic = lightmapProgram.Rtx.StaticComboArray.Single(c => c.Name == "S_EXECUTION_REORDERING").ComboIndexValue * executionReordering;
        var lightmapDynamic = lightmapProgram.Rtx.DynamicComboArray.Single(c => c.Name == "D_SH_LIGHTMAPS").ComboIndexValue * sh;
        _lightmap = Pipeline(Spirv(lightmapProgram.Rtx, lightmapStatic, lightmapDynamic), hitModules);
        var validationProgram = shaders.Get("rtx_vrad3_lightmap_sample_validation") ?? throw new FileNotFoundException("rtx_vrad3_lightmap_sample_validation");
        _validation = Pipeline(Spirv(validationProgram.Rtx, 0, 0), hitModules);
    }

    private readonly GpuDevice.GpuBuffer _lightBins;

    private T Keep<T>(T item)
    {
        switch (item)
        {
            case GpuDevice.GpuBuffer b: _buffers.Add(b); break;
            case GpuDevice.GpuImage i: _images.Add(i); break;
            case Sampler s: _samplers.Add(s); break;
        }
        return item;
    }

    private static byte[] Bytes<T>(T[] a) where T : struct => MemoryMarshal.AsBytes(a.AsSpan()).ToArray();

    // The SPIR-V of a program's static and dynamic combo (the file its render state names).
    private static byte[] Spirv(VfxProgramData program, long staticId, long dynamicId)
    {
        var combo = program.GetStaticCombo(staticId);
        var index = combo.GetDynamicComboIndex(dynamicId);
        if (index < 0)
            throw new InvalidDataException($"{program.ShaderName}: static {staticId} has no dynamic combo {dynamicId}");
        var fileId = combo.DynamicComboRenderStates[index].ShaderFileId;
        return (fileId >= 0 && fileId < combo.ShaderFiles.Length ? combo.ShaderFiles[fileId] : combo.ShaderFiles[index]).Bytecode;
    }

    private ShaderModule Module(byte[] spirv)
    {
        fixed (byte* p = spirv)
        {
            var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)p };
            GpuDevice.Check(_vk.CreateShaderModule(_gpu.Device, &info, null, out var module), "vkCreateShaderModule");
            _modules.Add(module);
            return module;
        }
    }

    private static ulong Align(ulong v, ulong a) => (v + a - 1) / a * a;

    // A ray tracing pipeline: group 0 the raygen, then per hit module and ray type 0, 1 a triangles
    // hit group of its ClosestHitN and AnyHitN entries. The SBT: raygen, two zeroed miss records,
    // the hit records in group order (instance SBT offset 2 x hit module).
    private (Pipeline, GpuDevice.GpuBuffer, StridedDeviceAddressRegionKHR[]) Pipeline(byte[] raygen, byte[][] hits)
    {
        var stages = new List<PipelineShaderStageCreateInfo>();
        var names = new List<nint>();
        int Stage(ShaderModule module, ShaderStageFlags kind, string entry)
        {
            var name = SilkMarshal.StringToPtr(entry);
            names.Add(name);
            stages.Add(new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = kind, Module = module, PName = (byte*)name });
            return stages.Count - 1;
        }
        var raygenReflection = SpirvReflection.Parse(raygen);
        var groupInfos = new List<RayTracingShaderGroupCreateInfoKHR>
        {
            new()
            {
                SType = StructureType.RayTracingShaderGroupCreateInfoKhr, Type = RayTracingShaderGroupTypeKHR.GeneralKhr,
                GeneralShader = (uint)Stage(Module(raygen), ShaderStageFlags.RaygenBitKhr, raygenReflection.EntryPoints.Single(e => e.Model == 5313).Name),
                ClosestHitShader = Vk.ShaderUnusedKhr, AnyHitShader = Vk.ShaderUnusedKhr, IntersectionShader = Vk.ShaderUnusedKhr,
            },
        };
        foreach (var spirv in hits)
        {
            var module = Module(spirv);
            var entries = SpirvReflection.Parse(spirv).EntryPoints;
            for (var ray = 0; ray < 2; ray++)
            {
                var closest = entries.FirstOrDefault(e => e.Model == 5316 && e.Name == $"ClosestHit{ray}").Name;
                var any = entries.FirstOrDefault(e => e.Model == 5315 && e.Name == $"AnyHit{ray}").Name;
                groupInfos.Add(new RayTracingShaderGroupCreateInfoKHR
                {
                    SType = StructureType.RayTracingShaderGroupCreateInfoKhr, Type = RayTracingShaderGroupTypeKHR.TrianglesHitGroupKhr,
                    GeneralShader = Vk.ShaderUnusedKhr, IntersectionShader = Vk.ShaderUnusedKhr,
                    ClosestHitShader = closest != null ? (uint)Stage(module, ShaderStageFlags.ClosestHitBitKhr, closest) : Vk.ShaderUnusedKhr,
                    AnyHitShader = any != null ? (uint)Stage(module, ShaderStageFlags.AnyHitBitKhr, any) : Vk.ShaderUnusedKhr,
                });
            }
        }
        Pipeline pipeline;
        var stageArray = stages.ToArray();
        var groupArray = groupInfos.ToArray();
        fixed (PipelineShaderStageCreateInfo* ps = stageArray)
        fixed (RayTracingShaderGroupCreateInfoKHR* pg = groupArray)
        {
            var info = new RayTracingPipelineCreateInfoKHR
            {
                SType = StructureType.RayTracingPipelineCreateInfoKhr, StageCount = (uint)stageArray.Length, PStages = ps,
                GroupCount = (uint)groupArray.Length, PGroups = pg, MaxPipelineRayRecursionDepth = 1, Layout = _pipelineLayout,
            };
            GpuDevice.Check(_createPipelines(_gpu.Device, default, default, 1, &info, null, &pipeline), "vkCreateRayTracingPipelinesKHR");
        }
        foreach (var n in names)
            SilkMarshal.Free(n);

        var handles = new byte[groupArray.Length * _handleSize];
        fixed (byte* ph = handles)
            GpuDevice.Check(_groupHandles(_gpu.Device, pipeline, 0, (uint)groupArray.Length, (nuint)handles.Length, ph), "vkGetRayTracingShaderGroupHandlesKHR");
        var record = Align(_handleSize, _handleAlignment);
        var raygenSize = Align(_handleSize, _baseAlignment);
        var missOffset = raygenSize;
        var missSize = Align(2 * record, _baseAlignment);
        var hitOffset = missOffset + missSize;
        var hitCount = (ulong)(groupArray.Length - 1);
        var sbt = Keep(_gpu.CreateBuffer(hitOffset + hitCount * record + _baseAlignment, BufferUsageFlags.ShaderBindingTableBitKhr | BufferUsageFlags.ShaderDeviceAddressBit));
        var raw = sbt.Mapped + (nint)(Align(_gpu.Address(sbt), _baseAlignment) - _gpu.Address(sbt));
        var baseAddress = Align(_gpu.Address(sbt), _baseAlignment);
        new Span<byte>((void*)sbt.Mapped, (int)sbt.Size).Clear();
        handles.AsSpan(0, (int)_handleSize).CopyTo(new Span<byte>((void*)raw, (int)_handleSize));
        for (var g = 0; g < (int)hitCount; g++)
            handles.AsSpan((g + 1) * (int)_handleSize, (int)_handleSize).CopyTo(new Span<byte>((void*)(raw + (nint)(hitOffset + (ulong)g * record)), (int)_handleSize));
        StridedDeviceAddressRegionKHR[] regions =
        [
            new() { DeviceAddress = baseAddress, Stride = raygenSize, Size = raygenSize },
            new() { DeviceAddress = baseAddress + missOffset, Stride = record, Size = 2 * record },
            new() { DeviceAddress = baseAddress + hitOffset, Stride = record, Size = hitCount * record },
            default,
        ];
        return (pipeline, sbt, regions);
    }

    private ulong BuildBlas(GpuDevice.GpuBuffer positions, int vertexCount, GpuDevice.GpuBuffer indices, int triangles)
    {
        var geometry = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr, GeometryType = GeometryTypeKHR.TrianglesKhr,
            Geometry = new AccelerationStructureGeometryDataKHR
            {
                Triangles = new AccelerationStructureGeometryTrianglesDataKHR
                {
                    SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr, VertexFormat = Format.R32G32B32Sfloat,
                    VertexData = new DeviceOrHostAddressConstKHR { DeviceAddress = _gpu.Address(positions) }, VertexStride = 12,
                    MaxVertex = (uint)Math.Max(0, vertexCount - 1), IndexType = IndexType.Uint32,
                    IndexData = new DeviceOrHostAddressConstKHR { DeviceAddress = _gpu.Address(indices) },
                },
            },
        };
        return Build(AccelerationStructureTypeKHR.BottomLevelKhr, geometry, (uint)triangles,
            BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr | BuildAccelerationStructureFlagsKHR.AllowCompactionBitKhr, out _);
    }

    private AccelerationStructureKHR BuildTlas(BakeScene scene, int[] groupOf)
    {
        var rays = scene.Rays;
        var data = new byte[scene.Instances.Count * 64];
        for (var i = 0; i < scene.Instances.Count; i++)
        {
            var inst = scene.Instances[i];
            var m = inst.ObjectToWorld;
            var f = MemoryMarshal.Cast<byte, float>(data.AsSpan(i * 64, 48));
            // VkTransformMatrixKHR: 3 x 4 row-major, column vectors (System.Numerics' transpose).
            (f[0], f[1], f[2], f[3]) = (m.M11, m.M21, m.M31, m.M41);
            (f[4], f[5], f[6], f[7]) = (m.M12, m.M22, m.M32, m.M42);
            (f[8], f[9], f[10], f[11]) = (m.M13, m.M23, m.M33, m.M43);
            var ray = rays.Instances[i];
            uint flags = 0;
            if (ray.CullDisable)
                flags |= (uint)GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr;
            if (FlipFacing)
                flags |= (uint)GeometryInstanceFlagsKHR.TriangleFlipFacingBitKhr;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 64 + 48), (uint)inst.Mesh & 0xffffff | (uint)ray.Mask << 24);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 64 + 52), (uint)(2 * groupOf[i]) & 0xffffff | flags << 24);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(i * 64 + 56), _blasAddress[inst.Mesh]);
        }
        var buffer = Keep(_gpu.Upload(data, BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.ShaderDeviceAddressBit));
        var geometry = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr, GeometryType = GeometryTypeKHR.InstancesKhr,
            Geometry = new AccelerationStructureGeometryDataKHR
            {
                Instances = new AccelerationStructureGeometryInstancesDataKHR
                {
                    SType = StructureType.AccelerationStructureGeometryInstancesDataKhr, ArrayOfPointers = false,
                    Data = new DeviceOrHostAddressConstKHR { DeviceAddress = _gpu.Address(buffer) },
                },
            },
        };
        Build(AccelerationStructureTypeKHR.TopLevelKhr, geometry, (uint)scene.Instances.Count, BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr, out var tlas);
        return tlas;
    }

    /// <summary>Whether TLAS instances flip the facing (VK_GEOMETRY_INSTANCE_TRIANGLE_FLIP_FACING): set before construction to test the convention.</summary>
    public static bool FlipFacing { get; set; } = Environment.GetEnvironmentVariable("GPUBAKE_FLIP") == "1";

    private readonly List<ulong> _blasAddress = [];

    private ulong Build(AccelerationStructureTypeKHR type, AccelerationStructureGeometryKHR geometry, uint primitives, BuildAccelerationStructureFlagsKHR flags, out AccelerationStructureKHR structure)
    {
        var info = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr, Type = type, Flags = flags,
            Mode = BuildAccelerationStructureModeKHR.BuildKhr, GeometryCount = 1, PGeometries = &geometry,
        };
        var sizes = new AccelerationStructureBuildSizesInfoKHR { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
        _buildSizes(_gpu.Device, AccelerationStructureBuildTypeKHR.DeviceKhr, &info, &primitives, &sizes);
        var storage = Keep(_gpu.CreateBuffer(sizes.AccelerationStructureSize, BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit, hostVisible: false));
        var create = new AccelerationStructureCreateInfoKHR
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr, Buffer = storage.Buffer, Size = sizes.AccelerationStructureSize, Type = type,
        };
        AccelerationStructureKHR handle;
        GpuDevice.Check(_createAs(_gpu.Device, &create, null, &handle), "vkCreateAccelerationStructureKHR");
        _structures.Add(handle);
        var scratch = _gpu.CreateBuffer(sizes.BuildScratchSize + 256, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit, hostVisible: false);
        info.DstAccelerationStructure = handle;
        info.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = Align(_gpu.Address(scratch), 256) };
        var range = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = primitives };
        var pRange = &range;
        var pInfo = &info;
        var ppRange = &pRange;
        _gpu.Submit(cmd => _cmdBuild(cmd, 1, pInfo, ppRange));
        _gpu.Destroy(scratch);
        structure = handle;
        var addressInfo = new AccelerationStructureDeviceAddressInfoKHR { SType = StructureType.AccelerationStructureDeviceAddressInfoKhr, AccelerationStructure = handle };
        var address = _asAddress(_gpu.Device, &addressInfo);
        if (type == AccelerationStructureTypeKHR.BottomLevelKhr)
            _blasAddress.Add(address);
        return address;
    }

    private DescriptorSet Allocate(int set)
    {
        var layout = _setLayouts[set];
        var info = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _pool, DescriptorSetCount = 1, PSetLayouts = &layout };
        DescriptorSet s;
        GpuDevice.Check(_vk.AllocateDescriptorSets(_gpu.Device, &info, &s), "vkAllocateDescriptorSets");
        return s;
    }

    private void WriteBuffers(DescriptorSet set, uint binding, DescriptorType type, params GpuDevice.GpuBuffer[] buffers)
    {
        var infos = buffers.Select(b => new DescriptorBufferInfo(b.Buffer, 0, Vk.WholeSize)).ToArray();
        fixed (DescriptorBufferInfo* p = infos)
        {
            var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = binding, DescriptorCount = (uint)infos.Length, DescriptorType = type, PBufferInfo = p };
            _vk.UpdateDescriptorSets(_gpu.Device, 1, &write, 0, null);
        }
    }

    private void WriteSamplers(DescriptorSet set, uint binding, Sampler[] samplers)
    {
        var infos = samplers.Select(s => new DescriptorImageInfo(s, default, ImageLayout.Undefined)).ToArray();
        fixed (DescriptorImageInfo* p = infos)
        {
            var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = binding, DescriptorCount = (uint)infos.Length, DescriptorType = DescriptorType.Sampler, PImageInfo = p };
            _vk.UpdateDescriptorSets(_gpu.Device, 1, &write, 0, null);
        }
    }

    private void WriteImages(DescriptorSet set, uint binding, DescriptorType type, ImageView[] views, ImageLayout layout)
    {
        var infos = views.Select(v => new DescriptorImageInfo(default, v, layout)).ToArray();
        fixed (DescriptorImageInfo* p = infos)
        {
            var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = binding, DescriptorCount = (uint)infos.Length, DescriptorType = type, PImageInfo = p };
            _vk.UpdateDescriptorSets(_gpu.Device, 1, &write, 0, null);
        }
    }

    private Sampler CreateSampler(int addressU, int addressV, bool linear, bool mipmaps)
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

    // A 2D or 3D image (storage or sampled) in device memory, with its view.
    private GpuDevice.GpuImage Image3D(uint w, uint h, uint d, Format format, ImageUsageFlags usage)
    {
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo, ImageType = d > 1 ? ImageType.Type3D : ImageType.Type2D, Format = format, Extent = new Extent3D(w, h, d),
            MipLevels = 1, ArrayLayers = 1, Samples = SampleCountFlags.Count1Bit, Tiling = ImageTiling.Optimal, Usage = usage,
            SharingMode = SharingMode.Exclusive, InitialLayout = ImageLayout.Undefined,
        };
        GpuDevice.Check(_vk.CreateImage(_gpu.Device, &info, null, out var image), "vkCreateImage");
        _vk.GetImageMemoryRequirements(_gpu.Device, image, out var req);
        var alloc = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = _gpu.MemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit) };
        GpuDevice.Check(_vk.AllocateMemory(_gpu.Device, &alloc, null, out var memory), "vkAllocateMemory");
        _vk.BindImageMemory(_gpu.Device, image, memory, 0);
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo, Image = image, ViewType = d > 1 ? ImageViewType.Type3D : ImageViewType.Type2D, Format = format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        GpuDevice.Check(_vk.CreateImageView(_gpu.Device, &viewInfo, null, out var view), "vkCreateImageView");
        return new GpuDevice.GpuImage(image, memory, view, format, w, h, 1);
    }

    // Copies tightly packed texels into mip 0 and leaves the image in the given layout.
    private void UploadImage(GpuDevice.GpuImage image, byte[] data, uint w, uint h, uint d, ImageLayout final = ImageLayout.ShaderReadOnlyOptimal)
    {
        var staging = _gpu.Upload(data, BufferUsageFlags.TransferSrcBit);
        _gpu.Submit(cmd =>
        {
            _gpu.Transition(cmd, image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
            var region = new BufferImageCopy(0, 0, 0, new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1), new Offset3D(0, 0, 0), new Extent3D(w, h, d));
            _vk.CmdCopyBufferToImage(cmd, staging.Buffer, image.Image, ImageLayout.TransferDstOptimal, 1, &region);
            _gpu.Transition(cmd, image, ImageLayout.TransferDstOptimal, final);
        });
        _gpu.Destroy(staging);
    }

    private void DestroyImage(GpuDevice.GpuImage i)
    {
        _vk.DestroyImageView(_gpu.Device, i.View, null);
        _vk.DestroyImage(_gpu.Device, i.Image, null);
        _vk.FreeMemory(_gpu.Device, i.Memory, null);
    }

    // ---- The block (ILightmapDevice) ----

    private int _w, _h;
    private GpuDevice.GpuBuffer? _luxels, _samples;
    private GpuDevice.GpuImage? _chartIds, _converged, _random;
    private DescriptorSet _passSet, _validationSet;

    public void BeginBlock(int w, int h, uint[] ids)
    {
        EndResources();
        (_w, _h) = (w, h);
        _luxels = _gpu.CreateBuffer((ulong)(w * h * 224), BufferUsageFlags.StorageBufferBit);
        new Span<byte>((void*)_luxels.Mapped, w * h * 224).Clear();
        _samples = _gpu.CreateBuffer((ulong)(w * h * 96), BufferUsageFlags.StorageBufferBit);
        _chartIds = Image3D((uint)w, (uint)h, 1, Format.R32Uint, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);
        UploadImage(_chartIds, Bytes(ids), (uint)w, (uint)h, 1);
        var cw = (uint)(w + 3) / 4;
        var ch = (uint)(h + 3) / 4;
        _converged = Image3D(cw, ch, 2, Format.R32Uint, ImageUsageFlags.StorageBit | ImageUsageFlags.TransferDstBit);
        UploadImage(_converged, new byte[cw * ch * 2 * 4], cw, ch, 2, ImageLayout.General);
        _random = Image3D(HaltonSamples.Size, HaltonSamples.Size, HaltonSamples.Layers, Format.R32G32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.TransferDstBit);
        _passSet = Allocate(6);
        WriteImages(_passSet, 30, DescriptorType.SampledImage, [_chartIds.View], ImageLayout.ShaderReadOnlyOptimal);
        WriteBuffers(_passSet, 158, DescriptorType.StorageBuffer, _luxels);
        WriteBuffers(_passSet, 159, DescriptorType.StorageBuffer, _lightBins);
        WriteBuffers(_passSet, 160, DescriptorType.StorageBuffer, _samples);
        WriteImages(_passSet, 161, DescriptorType.StorageImage, [_random.View], ImageLayout.General);
        WriteImages(_passSet, 162, DescriptorType.StorageImage, [_converged.View], ImageLayout.General);
        _validationSet = Allocate(6);
        WriteBuffers(_validationSet, 158, DescriptorType.StorageBuffer, _samples);
    }

    public void Samples(LightmapSample_t_natural[] samples)
    {
        if (samples.Length != _w * _h)
            throw new ArgumentException("one sample per luxel", nameof(samples));
        Bytes(samples).CopyTo(new Span<byte>((void*)_samples!.Mapped, samples.Length * 96));
        Trace(_validation, _validationSet, null);
    }

    public LightmapSample_t_natural[] ReadSamples() => MemoryMarshal.Cast<byte, LightmapSample_t_natural>(new Span<byte>((void*)_samples!.Mapped, _w * _h * 96)).ToArray();

    public void Pass(int pass, int dims, float[] random, Vector2 blockOffset, Vector2 lightmapSize)
    {
        UploadImage(_random!, Bytes(random), HaltonSamples.Size, HaltonSamples.Size, HaltonSamples.Layers, ImageLayout.General);
        var push = new byte[64];
        var i = MemoryMarshal.Cast<byte, int>(push.AsSpan());
        var f = MemoryMarshal.Cast<byte, float>(push.AsSpan());
        (i[0], i[1]) = (pass, dims);
        (f[4], f[5], f[6], f[7]) = (blockOffset.X, blockOffset.Y, lightmapSize.X, lightmapSize.Y);
        (f[8], f[9], f[10], f[11]) = (-float.MaxValue, -float.MaxValue, -float.MaxValue, -float.MaxValue);
        (f[12], f[13], f[14], f[15]) = (float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue);
        Trace(_lightmap, _passSet, push);
    }

    public Luxel_t_natural[] EndBlock()
    {
        var luxels = MemoryMarshal.Cast<byte, Luxel_t_natural>(new Span<byte>((void*)_luxels!.Mapped, _w * _h * 224)).ToArray();
        EndResources();
        return luxels;
    }

    private void Trace((Pipeline Pipeline, GpuDevice.GpuBuffer Sbt, StridedDeviceAddressRegionKHR[] Regions) p, DescriptorSet pass, byte[]? push)
    {
        _gpu.Submit(cmd =>
        {
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.RayTracingKhr, p.Pipeline);
            var sets = stackalloc DescriptorSet[] { _scene, _bindless, _view, pass };
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.RayTracingKhr, _pipelineLayout, 3, 4, sets, 0, null);
            if (push != null)
                fixed (byte* pp = push)
                    _vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.RaygenBitKhr, 0, 64, pp);
            fixed (StridedDeviceAddressRegionKHR* r = p.Regions)
                _cmdTraceRays(cmd, &r[0], &r[1], &r[2], &r[3], (uint)_w, (uint)_h, 1);
            var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.HostReadBit | AccessFlags.ShaderReadBit };
            _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.RayTracingShaderBitKhr, PipelineStageFlags.HostBit | PipelineStageFlags.RayTracingShaderBitKhr, 0, 1, &barrier, 0, null, 0, null);
        });
    }

    private void EndResources()
    {
        if (_luxels == null)
            return;
        var sets = stackalloc DescriptorSet[] { _passSet, _validationSet };
        _vk.FreeDescriptorSets(_gpu.Device, _pool, 2, sets);
        _gpu.Destroy(_luxels);
        _gpu.Destroy(_samples!);
        DestroyImage(_chartIds!);
        DestroyImage(_converged!);
        DestroyImage(_random!);
        _luxels = null;
    }

    // ---- std430 bytes of the structures the CPU port builds ----

    /// <summary>GeometryInfo_t_natural: five ivec4, then the texcoord and colour field arrays (8 ivec4 each).</summary>
    public static byte[] GeometryBytes(GeometryInfo_t_natural g)
    {
        var all = new List<ivec4> { g.m_nVertexAndIndexBuffers, g.m_nBufferBindOffsetBytes, g.m_nBufferStrideInBytes, g.m_vNormalFieldInfo, g.m_vTangentFieldInfo };
        all.AddRange(g.m_vTexCoordFieldInfo.data);
        all.AddRange(g.m_vColorFieldInfo.data);
        var bytes = new byte[all.Count * 16];
        MemoryMarshal.AsBytes(all.ToArray().AsSpan()).CopyTo(bytes);
        return bytes;
    }

    private sealed class Writer(int size)
    {
        public readonly byte[] Bytes = new byte[size];
        private int _at;
        public void F(float v) { BinaryPrimitives.WriteSingleLittleEndian(Bytes.AsSpan(_at), v); _at += 4; }
        public void I(int v) { BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(_at), v); _at += 4; }
        public void U(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(_at), v); _at += 4; }
        public void V(vec4 v) { F(v.x); F(v.y); F(v.z); F(v.w); }
        public void V(ivec4 v) { I(v.x); I(v.y); I(v.z); I(v.w); }

        // row_major: row r holds component r of every column.
        public void RowMajor(mat4 m)
        {
            vec4[] c = [m.c0, m.c1, m.c2, m.c3];
            F(c[0].x); F(c[1].x); F(c[2].x); F(c[3].x);
            F(c[0].y); F(c[1].y); F(c[2].y); F(c[3].y);
            F(c[0].z); F(c[1].z); F(c[2].z); F(c[3].z);
            F(c[0].w); F(c[1].w); F(c[2].w); F(c[3].w);
        }

        public void RowMajor(mat4x3 m)
        {
            F(m.c0.x); F(m.c1.x); F(m.c2.x); F(m.c3.x);
            F(m.c0.y); F(m.c1.y); F(m.c2.y); F(m.c3.y);
            F(m.c0.z); F(m.c1.z); F(m.c2.z); F(m.c3.z);
        }

        public int At => _at;
    }

    public static byte[] LightBytes(PathTraceBarnLightGPU_t_natural l)
    {
        var w = new Writer(496);
        w.RowMajor(l.m_mToClipSpace);
        w.RowMajor(l.m_mFromClipSpace);
        foreach (var v in new[] { l.m_vNearPlane, l.m_vLightPosition, l.m_vCookieParameters, l.m_vColor, l.m_vBounceColor, l.m_vOrientation, l.m_vAngleFade, l.m_vDistanceFade_vSkirt })
            w.V(v);
        foreach (var v in l.m_vLuminaireVectors.data)
            w.V(v);
        foreach (var v in new[] { l.m_vMinBounds, l.m_vMaxBounds, l.m_vIlluminationBoundsCenter, l.m_qIlluminationBoundsOrient, l.m_vIlluminationBoundsExtent })
            w.V(v);
        w.RowMajor(l.m_mBarnIlluminationFromWorld);
        foreach (var v in new[] { l.m_nCookie, l.m_nLuminaireType, l.m_nPathTraceFlags, l.m_bSphericalCookie, l.m_nBakedShadowIndex, l.m_nShadowedLightId, l.m_nPreviewBakedShadowIndex, l._pad })
            w.I(v);
        if (w.At != 496)
            throw new InvalidOperationException($"PathTraceBarnLightGPU_t is {w.At} bytes");
        return w.Bytes;
    }

    public static byte[] GlobalBytes(SceneGlobalInfoGPU_t_natural g)
    {
        var w = new Writer(192);
        foreach (var v in new[] { g.m_vSkyColor, g.m_vSkyAmbientBounce, g.m_vSunLightIrradiance, g.m_vLocalLightMinBounds, g.m_vLocalLightMaxBounds })
            w.V(v);
        w.V(g.m_vLightBinCellCount_nLightCount);
        w.RowMajor(g.m_mIBLTransformI);
        w.V(g.m_vIBLCubeMapScale);
        w.I(g.m_nIBLTexture);
        w.I(g.m_nIBLCubeMap);
        w.I(g.m_nIBLCubeMapFormat);
        w.U(g.m_nSkyLightFlags);
        w.I(g.m_nBrdfLut);
        w.I(g.m_nBrdfMultiscatterLut);
        w.U(g.m_nSceneGlobalInfoFlags);
        w.U(g._unused0);
        if (w.At != 192)
            throw new InvalidOperationException($"SceneGlobalInfoGPU_t is {w.At} bytes");
        return w.Bytes;
    }

    public void Dispose()
    {
        EndResources();
        foreach (var p in new[] { _lightmap.Pipeline, _validation.Pipeline })
            _vk.DestroyPipeline(_gpu.Device, p, null);
        _vk.DestroyDescriptorPool(_gpu.Device, _pool, null);
        _vk.DestroyPipelineLayout(_gpu.Device, _pipelineLayout, null);
        foreach (var l in _setLayouts)
            _vk.DestroyDescriptorSetLayout(_gpu.Device, l, null);
        foreach (var m in _modules)
            _vk.DestroyShaderModule(_gpu.Device, m, null);
        foreach (var s in _structures)
            _destroyAs(_gpu.Device, s, null);
        foreach (var s in _samplers)
            _vk.DestroySampler(_gpu.Device, s, null);
        foreach (var i in _images)
            DestroyImage(i);
        foreach (var b in _buffers)
            _gpu.Destroy(b);
    }
}
