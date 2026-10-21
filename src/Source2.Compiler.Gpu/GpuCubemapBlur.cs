using System.Buffers.Binary;
using System.Numerics;
using Silk.NET.Vulkan;
using Source2.Compiler.Cubemaps;
using ValvePak;

namespace Source2.Compiler.Gpu;

/// <summary>
/// The GPU half of the GGXCubeMapBlur mip algorithm (docs/CUBEMAPS.md) on Vulkan with Valve's own
/// programs: core's convolve_environment_map (vulkan_50, its one static and dynamic combo). Per cube:
/// the six RGBA32F faces go up as a cube whose mips are blitted down level by level (rendersystemvulkan
/// with r_mipgen_compute_shader 0); then for each mip from 1 and each face the quad of
/// <see cref="GgxCubeMapBlur.Quad"/> is drawn as a strip into an RGBA32F target the mip's size, with
/// the face's rows (g_vMatRow0..2), g_nSampleCount 2048 and the mip's tap table
/// (FilterTapConstantBuffer_t), and read back. Mip 0 is the input itself and is not drawn.
/// </summary>
public sealed unsafe class GpuCubemapBlur : IDisposable
{
    private readonly GpuDevice _gpu = new();
    private readonly ProgramRunner _runner;
    private readonly ValveProgram _vs, _ps;
    private readonly Sampler _sampler;
    private readonly SpirvReflection.Resource _vsGlobals, _psGlobals, _taps, _cubemap, _filter;

    public string DeviceName => _gpu.Name;

    /// <param name="coreShaderVpk">CS2's game/core/shaders_vulkan_dir.vpk.</param>
    public GpuCubemapBlur(string coreShaderVpk)
    {
        using var package = new Package();
        package.Read(coreShaderVpk);
        var none = new Dictionary<string, int>();
        _vs = ValveProgram.Load(package, "convolve_environment_map", "vulkan_50", "vs", none, none);
        _ps = ValveProgram.Load(package, "convolve_environment_map", "vulkan_50", "ps", none, none);
        // The SPIR-V carries no names: the vs has one uniform block (_Globals_); the ps has _Globals_
        // and the 64 KB tap block, one cube texture (g_tCubemap) and one sampler.
        _vsGlobals = Find(_vs, SpirvReflection.Kind.UniformBuffer, r => r.BlockSize < GgxCubeMapBlur.TableBytes);
        _psGlobals = Find(_ps, SpirvReflection.Kind.UniformBuffer, r => r.BlockSize < GgxCubeMapBlur.TableBytes);
        _taps = Find(_ps, SpirvReflection.Kind.UniformBuffer, r => r.BlockSize == GgxCubeMapBlur.TableBytes);
        _cubemap = Find(_ps, SpirvReflection.Kind.SampledImage, _ => true);
        _filter = Find(_ps, SpirvReflection.Kind.Sampler, _ => true);
        // The quad's 0x30-byte vertices: position (x, y, 0) at 0, direction at 20.
        _runner = new ProgramRunner(_gpu, _vs, _ps, [0x30],
            [new(0, 0, Format.R32G32B32Sfloat, 0), new(1, 0, Format.R32G32B32Sfloat, 20)],
            Format.R32G32B32A32Sfloat, topology: PrimitiveTopology.TriangleStrip);
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Silk.NET.Vulkan.Filter.Linear,
            MinFilter = Silk.NET.Vulkan.Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 1000,
        };
        GpuDevice.Check(_gpu.Vk.CreateSampler(_gpu.Device, &info, null, out _sampler), "vkCreateSampler");
    }

    private static SpirvReflection.Resource Find(ValveProgram program, SpirvReflection.Kind kind, Func<SpirvReflection.Resource, bool> pick)
    {
        var found = program.Reflection.Resources.Where(r => r.Kind == kind && pick(r)).ToList();
        return found.Count == 1 ? found[0] : throw new InvalidDataException($"{program.Name}: {found.Count} {kind} resources where one was expected among "
            + string.Join(", ", program.Reflection.Resources.Select(r => $"{r.Kind} set {r.Set} binding {r.Binding} size {r.BlockSize}")));
    }

    /// <summary>
    /// Filters one cube: <paramref name="faces"/> six RGBA32F faces of size x size, row major; returns
    /// [mip][face] RGBA32F for the full chain's mips from 1 down to 1 x 1 (index 0 left null). The layer's
    /// schedule counts the full chain (<see cref="GgxCubeMapBlur.MipCount"/>), whatever the texture keeps.
    /// </summary>
    public float[][][] Filter(IReadOnlyList<float[]> faces, int size)
    {
        if (faces.Count != 6 || size <= 0 || (size & (size - 1)) != 0)
            throw new ArgumentException("six square power-of-two faces");
        var sourceMips = (uint)(BitOperations.Log2((uint)size) + 1);
        var source = _gpu.CreateImage((uint)size, (uint)size, sourceMips, Format.R32G32B32A32Sfloat,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
            ImageViewType.TypeCube, 6, ImageCreateFlags.CreateCubeCompatibleBit);
        var faceBytes = size * size * 16;
        var staging = _gpu.CreateBuffer((ulong)(faceBytes * 6), BufferUsageFlags.TransferSrcBit);
        for (var f = 0; f < 6; f++)
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(faces[f].AsSpan()).CopyTo(new Span<byte>((byte*)staging.Mapped + f * faceBytes, faceBytes));
        _gpu.Submit(cmd => UploadAndBlit(cmd, source, staging, size, sourceMips));
        _gpu.Destroy(staging);
        _runner.SetImage(_cubemap.Set, _cubemap.Binding, 0, source.View);
        _runner.SetSampler(_filter.Set, _filter.Binding, 0, _sampler);

        var psGlobals = new byte[Math.Max(16, _psGlobals.BlockSize)];
        BinaryPrimitives.WriteInt32LittleEndian(psGlobals.AsSpan(Offset(_ps, _psGlobals, "g_nSampleCount", 0)), GgxCubeMapBlur.SampleCount);
        _runner.SetBuffer(_psGlobals.Set, _psGlobals.Binding, psGlobals);
        var quad = new byte[4 * 0x30];
        for (var v = 0; v < 4; v++)
        {
            var (p, d) = GgxCubeMapBlur.Quad[v];
            var at = quad.AsSpan(v * 0x30);
            BinaryPrimitives.WriteSingleLittleEndian(at, p.X);
            BinaryPrimitives.WriteSingleLittleEndian(at[4..], p.Y);
            BinaryPrimitives.WriteSingleLittleEndian(at[20..], d.X);
            BinaryPrimitives.WriteSingleLittleEndian(at[24..], d.Y);
            BinaryPrimitives.WriteSingleLittleEndian(at[28..], d.Z);
        }

        var mipCount = GgxCubeMapBlur.MipCount(size);
        var result = new float[mipCount][][];
        for (var mip = 1; mip < mipCount; mip++)
        {
            _runner.SetBuffer(_taps.Set, _taps.Binding, GgxCubeMapBlur.Table(mip, mipCount, size));
            var side = (uint)(size >> mip);
            result[mip] = new float[6][];
            for (var face = 0; face < 6; face++)
            {
                var vsGlobals = new byte[Math.Max(48, _vsGlobals.BlockSize)];
                var rows = GgxCubeMapBlur.FaceBasis[face];
                for (var r = 0; r < 3; r++)
                {
                    var row = rows[r];
                    var at = vsGlobals.AsSpan(Offset(_vs, _vsGlobals, $"g_vMatRow{r}", r));
                    BinaryPrimitives.WriteSingleLittleEndian(at, row.X);
                    BinaryPrimitives.WriteSingleLittleEndian(at[4..], row.Y);
                    BinaryPrimitives.WriteSingleLittleEndian(at[8..], row.Z);
                }
                _runner.SetBuffer(_vsGlobals.Set, _vsGlobals.Binding, vsGlobals);
                var pixels = _runner.Draw([quad], 4, side, side, [0, 0, 0, 0]);
                var floats = new float[pixels.Length / 4];
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(pixels).CopyTo(floats);
                result[mip][face] = floats;
            }
        }
        _gpu.Vk.DestroyImageView(_gpu.Device, source.View, null);
        _gpu.Vk.DestroyImage(_gpu.Device, source.Image, null);
        _gpu.Vk.FreeMemory(_gpu.Device, source.Memory, null);
        return result;
    }

    // A variable's byte offset in its _Globals_ block: the combo's write sequence names it, else the
    // block's member in declaration order (the SPIR-V keeps no names).
    private static int Offset(ValveProgram program, SpirvReflection.Resource block, string name, int member)
    {
        foreach (var (n, offset, _) in program.Constants)
            if (n == name)
                return offset;
        return block.Members[member].Offset;
    }

    private void UploadAndBlit(CommandBuffer cmd, GpuDevice.GpuImage image, GpuDevice.GpuBuffer staging, int size, uint mips)
    {
        var vk = _gpu.Vk;
        Barrier(cmd, image, 0, mips, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
        for (uint f = 0; f < 6; f++)
        {
            var region = new BufferImageCopy((ulong)(f * size * size * 16), 0, 0, new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, f, 1),
                new Offset3D(0, 0, 0), new Extent3D((uint)size, (uint)size, 1));
            vk.CmdCopyBufferToImage(cmd, staging.Buffer, image.Image, ImageLayout.TransferDstOptimal, 1, &region);
        }
        for (uint m = 1; m < mips; m++)
        {
            Barrier(cmd, image, m - 1, 1, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal);
            int from = size >> (int)(m - 1), to = size >> (int)m;
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, m - 1, 0, 6),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, m, 0, 6),
            };
            blit.SrcOffsets[1] = new Offset3D(from, from, 1);
            blit.DstOffsets[1] = new Offset3D(to, to, 1);
            vk.CmdBlitImage(cmd, image.Image, ImageLayout.TransferSrcOptimal, image.Image, ImageLayout.TransferDstOptimal, 1, &blit, Silk.NET.Vulkan.Filter.Linear);
        }
        Barrier(cmd, image, mips - 1, 1, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal);
        Barrier(cmd, image, 0, mips, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal);
    }

    private void Barrier(CommandBuffer cmd, GpuDevice.GpuImage image, uint mip, uint count, ImageLayout from, ImageLayout to)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image.Image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, mip, count, 0, 6),
            SrcAccessMask = AccessFlags.MemoryWriteBit,
            DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
        };
        _gpu.Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, 0, 0, null, 0, null, 1, &barrier);
    }

    public void Dispose()
    {
        _runner.Dispose();
        _gpu.Vk.DestroySampler(_gpu.Device, _sampler, null);
        _gpu.Dispose();
    }
}
