using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Source2.Compiler.Gpu;

/// <summary>
/// A headless Vulkan 1.2 device for running Valve's programs: one graphics
/// queue, the descriptor-indexing features the bindless programs need,
/// buffers and images in host-visible or device memory, and one-shot command
/// submission. No window and no swapchain.
/// </summary>
public sealed unsafe class GpuDevice : IDisposable
{
    public Vk Vk { get; } = Vk.GetApi();
    public Instance Instance { get; }
    public PhysicalDevice Physical { get; }
    public Device Device { get; }
    public Queue Queue { get; }
    public uint QueueFamily { get; }
    public CommandPool CommandPool { get; }
    public string Name { get; }
    private readonly PhysicalDeviceMemoryProperties _memory;

    /// <summary>Whether the device runs ray tracing pipelines (VK_KHR_ray_tracing_pipeline, acceleration structures, buffer device addresses).</summary>
    public bool RayTracing { get; }

    /// <param name="rayTracingShaderFeatures">
    /// Also enable what vrad3's ray tracing shaders need when they run as compute (the hit shader
    /// oracle): 16-bit floats and integers, 16-bit storage, non-uniform storage buffer indexing.
    /// </param>
    /// <param name="rayTracing">
    /// Also enable ray tracing itself (RtxBakeHost): VK_KHR_acceleration_structure,
    /// VK_KHR_ray_tracing_pipeline, VK_KHR_deferred_host_operations, buffer device addresses and
    /// formatless storage image access (vrad3's raygens read g_RandomSamples without a format).
    /// Implies <paramref name="rayTracingShaderFeatures"/>.
    /// </param>
    public GpuDevice(bool rayTracingShaderFeatures = false, bool rayTracing = false)
    {
        rayTracingShaderFeatures |= rayTracing;
        RayTracing = rayTracing;
        var appName = (byte*)SilkMarshal.StringToPtr("s2c");
        var app = new ApplicationInfo { SType = StructureType.ApplicationInfo, PApplicationName = appName, ApiVersion = Vk.Version12 };
        var info = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &app };
        Check(Vk.CreateInstance(&info, null, out var instance), "vkCreateInstance");
        Instance = instance;
        SilkMarshal.Free((nint)appName);

        uint count = 0;
        Vk.EnumeratePhysicalDevices(Instance, &count, null);
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices)
            Vk.EnumeratePhysicalDevices(Instance, &count, p);
        // A discrete GPU first.
        Physical = devices.OrderBy(d => { Vk.GetPhysicalDeviceProperties(d, out var pr); return pr.DeviceType == PhysicalDeviceType.DiscreteGpu ? 0 : 1; }).First();
        Vk.GetPhysicalDeviceProperties(Physical, out var props);
        Name = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "?";
        Vk.GetPhysicalDeviceMemoryProperties(Physical, out _memory);

        uint families = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(Physical, &families, null);
        var fam = new QueueFamilyProperties[families];
        fixed (QueueFamilyProperties* p = fam)
            Vk.GetPhysicalDeviceQueueFamilyProperties(Physical, &families, p);
        QueueFamily = (uint)Array.FindIndex(fam, f => (f.QueueFlags & QueueFlags.GraphicsBit) != 0);

        var priority = 1f;
        var queueInfo = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = QueueFamily, QueueCount = 1, PQueuePriorities = &priority };
        var v11 = new PhysicalDeviceVulkan11Features
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            StorageBuffer16BitAccess = rayTracingShaderFeatures,
            UniformAndStorageBuffer16BitAccess = rayTracingShaderFeatures,
        };
        var rtPipeline = new PhysicalDeviceRayTracingPipelineFeaturesKHR { SType = StructureType.PhysicalDeviceRayTracingPipelineFeaturesKhr, RayTracingPipeline = true };
        var accel = new PhysicalDeviceAccelerationStructureFeaturesKHR { SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr, PNext = &rtPipeline, AccelerationStructure = true };
        if (rayTracing)
            v11.PNext = &accel;
        var v12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            PNext = rayTracingShaderFeatures ? &v11 : null,
            BufferDeviceAddress = rayTracing,
            ShaderFloat16 = rayTracingShaderFeatures,
            ShaderStorageBufferArrayNonUniformIndexing = rayTracingShaderFeatures,
            DescriptorIndexing = true,
            RuntimeDescriptorArray = true,
            DescriptorBindingPartiallyBound = true,
            DescriptorBindingVariableDescriptorCount = true,
            ShaderSampledImageArrayNonUniformIndexing = true,
            DescriptorBindingSampledImageUpdateAfterBind = true,
            ShaderUniformBufferArrayNonUniformIndexing = rayTracing,
        };
        var features = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &v12,
            Features = new PhysicalDeviceFeatures
            {
                SampleRateShading = true,
                TextureCompressionBC = true,
                ShaderImageGatherExtended = true,
                FragmentStoresAndAtomics = true,
                ImageCubeArray = true,
                SamplerAnisotropy = true,
                ShaderInt16 = rayTracingShaderFeatures,
                ShaderInt64 = rayTracing,
                ShaderStorageImageReadWithoutFormat = rayTracing,
                ShaderStorageImageWriteWithoutFormat = rayTracing,
            },
        };
        string[] extensions = rayTracing ? ["VK_KHR_acceleration_structure", "VK_KHR_ray_tracing_pipeline", "VK_KHR_deferred_host_operations"] : [];
        var extensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions);
        var deviceInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo, PNext = &features, QueueCreateInfoCount = 1, PQueueCreateInfos = &queueInfo,
            EnabledExtensionCount = (uint)extensions.Length, PpEnabledExtensionNames = extensionNames,
        };
        Check(Vk.CreateDevice(Physical, &deviceInfo, null, out var device), "vkCreateDevice");
        SilkMarshal.Free((nint)extensionNames);
        Device = device;
        Vk.GetDeviceQueue(Device, QueueFamily, 0, out var queue);
        Queue = queue;
        var poolInfo = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = QueueFamily, Flags = CommandPoolCreateFlags.ResetCommandBufferBit };
        Check(Vk.CreateCommandPool(Device, &poolInfo, null, out var pool), "vkCreateCommandPool");
        CommandPool = pool;
    }

    public static void Check(Result r, string what)
    {
        if (r != Result.Success)
            throw new InvalidOperationException($"{what}: {r}");
    }

    public uint MemoryType(uint bits, MemoryPropertyFlags flags)
    {
        for (var i = 0; i < _memory.MemoryTypeCount; i++)
            if ((bits & (1u << i)) != 0 && (_memory.MemoryTypes[i].PropertyFlags & flags) == flags)
                return (uint)i;
        throw new InvalidOperationException("no suitable memory type");
    }

    /// <summary>A buffer with its memory; host-visible buffers are mapped at <c>Mapped</c>.</summary>
    public sealed class GpuBuffer(VkBuffer buffer, DeviceMemory memory, ulong size, nint mapped)
    {
        public VkBuffer Buffer { get; } = buffer;
        public DeviceMemory Memory { get; } = memory;
        public ulong Size { get; } = size;
        public nint Mapped { get; } = mapped;
    }

    public GpuBuffer CreateBuffer(ulong size, BufferUsageFlags usage, bool hostVisible = true)
    {
        size = Math.Max(size, 16);
        var info = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive };
        Check(Vk.CreateBuffer(Device, &info, null, out var buffer), "vkCreateBuffer");
        Vk.GetBufferMemoryRequirements(Device, buffer, out var req);
        var flags = hostVisible ? MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit : MemoryPropertyFlags.DeviceLocalBit;
        var addressFlags = new MemoryAllocateFlagsInfo { SType = StructureType.MemoryAllocateFlagsInfo, Flags = MemoryAllocateFlags.DeviceAddressBit };
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = MemoryType(req.MemoryTypeBits, flags),
            PNext = (usage & BufferUsageFlags.ShaderDeviceAddressBit) != 0 ? &addressFlags : null,
        };
        Check(Vk.AllocateMemory(Device, &alloc, null, out var memory), "vkAllocateMemory");
        Vk.BindBufferMemory(Device, buffer, memory, 0);
        void* mapped = null;
        if (hostVisible)
            Vk.MapMemory(Device, memory, 0, size, 0, &mapped);
        return new GpuBuffer(buffer, memory, size, (nint)mapped);
    }

    /// <summary>A buffer's device address (the buffer needs ShaderDeviceAddressBit).</summary>
    public ulong Address(GpuBuffer b)
    {
        var info = new BufferDeviceAddressInfo { SType = StructureType.BufferDeviceAddressInfo, Buffer = b.Buffer };
        return Vk.GetBufferDeviceAddress(Device, &info);
    }

    /// <summary>A device-level entry point (vkGetDeviceProcAddr), for the ray tracing extensions.</summary>
    public nint Proc(string name)
    {
        var p = Vk.GetDeviceProcAddr(Device, name);
        if (p.Handle == null)
            throw new InvalidOperationException($"{name}: not available");
        return (nint)p.Handle;
    }

    public GpuBuffer Upload(ReadOnlySpan<byte> data, BufferUsageFlags usage)
    {
        var b = CreateBuffer((ulong)data.Length, usage);
        data.CopyTo(new Span<byte>((void*)b.Mapped, data.Length));
        return b;
    }

    public void Destroy(GpuBuffer b)
    {
        Vk.DestroyBuffer(Device, b.Buffer, null);
        Vk.FreeMemory(Device, b.Memory, null);
    }

    /// <summary>A 2D image with its memory and a view.</summary>
    public sealed class GpuImage(Image image, DeviceMemory memory, ImageView view, Format format, uint width, uint height, uint mips)
    {
        public Image Image { get; } = image;
        public DeviceMemory Memory { get; } = memory;
        public ImageView View { get; } = view;
        public Format Format { get; } = format;
        public uint Width { get; } = width;
        public uint Height { get; } = height;
        public uint Mips { get; } = mips;
    }

    public GpuImage CreateImage(uint width, uint height, uint mips, Format format, ImageUsageFlags usage, ImageViewType viewType = ImageViewType.Type2D, uint layers = 1, ImageCreateFlags flags = 0)
    {
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
            MipLevels = mips,
            ArrayLayers = layers,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
            Flags = flags,
        };
        Check(Vk.CreateImage(Device, &info, null, out var image), "vkCreateImage");
        Vk.GetImageMemoryRequirements(Device, image, out var req);
        var alloc = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = MemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit) };
        Check(Vk.AllocateMemory(Device, &alloc, null, out var memory), "vkAllocateMemory");
        Vk.BindImageMemory(Device, image, memory, 0);
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = viewType,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, mips, 0, layers),
        };
        Check(Vk.CreateImageView(Device, &viewInfo, null, out var view), "vkCreateImageView");
        return new GpuImage(image, memory, view, format, width, height, mips);
    }

    /// <summary>Records into a fresh command buffer, submits it and waits.</summary>
    public void Submit(Action<CommandBuffer> record)
    {
        var alloc = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = CommandPool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
        Check(Vk.AllocateCommandBuffers(Device, &alloc, out var cmd), "vkAllocateCommandBuffers");
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        Vk.BeginCommandBuffer(cmd, &begin);
        record(cmd);
        Vk.EndCommandBuffer(cmd);
        var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        Check(Vk.CreateFence(Device, &fenceInfo, null, out var fence), "vkCreateFence");
        Check(Vk.QueueSubmit(Queue, 1, &submit, fence), "vkQueueSubmit");
        Check(Vk.WaitForFences(Device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");
        Vk.DestroyFence(Device, fence, null);
        Vk.FreeCommandBuffers(Device, CommandPool, 1, &cmd);
    }

    /// <summary>An image layout transition for all of the image's mips and layers.</summary>
    public void Transition(CommandBuffer cmd, GpuImage image, ImageLayout from, ImageLayout to, uint layers = 1)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image.Image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, image.Mips, 0, layers),
            SrcAccessMask = AccessFlags.MemoryWriteBit,
            DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
        };
        Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, 0, 0, null, 0, null, 1, &barrier);
    }

    public void Dispose()
    {
        Vk.DestroyCommandPool(Device, CommandPool, null);
        Vk.DestroyDevice(Device, null);
        Vk.DestroyInstance(Instance, null);
    }
}
