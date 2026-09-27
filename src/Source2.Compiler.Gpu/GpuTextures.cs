using Silk.NET.Vulkan;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace Source2.Compiler.Gpu;

/// <summary>
/// Compiled textures (.vtex_c) as Vulkan images, in their stored format so
/// the GPU decodes the blocks itself. Formats stored as PNG, JPEG or WebP
/// are decoded to RGBA8 by VRF.
/// </summary>
public static unsafe class GpuTextures
{
    /// <summary>
    /// The render system's preload cap (RenderSystem/MaxPreloadTextureResolution,
    /// default 512): a texture loads from the first mip whose longer side is at
    /// most this, and a compile never streams in more. The material sampler's
    /// reads of 4096 textures match mip 3 exactly.
    /// </summary>
    // rendersystemvulkan 1800f7ccc reads the key (default 0x200) and 180104cd0
    // compares max(width, height) of the resident top mip with it.
    public const int MaxPreloadResolution = 512;

    /// <summary>The first mip level a texture preloads at.</summary>
    public static int PreloadLevel(int width, int height, int mips, int cap = MaxPreloadResolution)
    {
        var level = 0;
        while (level < mips - 1 && Math.Max(width >> level, height >> level) > cap)
            level++;
        return level;
    }

    /// <summary>The Vulkan format a stored format uploads as, sRGB or linear; null when it must be decoded first.</summary>
    public static Format? VulkanFormat(VTexFormat format, bool srgb) => format switch
    {
        VTexFormat.DXT1 => srgb ? Format.BC1RgbaSrgbBlock : Format.BC1RgbaUnormBlock,
        VTexFormat.DXT5 => srgb ? Format.BC3SrgbBlock : Format.BC3UnormBlock,
        VTexFormat.ATI1N => Format.BC4UnormBlock,
        VTexFormat.ATI2N => Format.BC5UnormBlock,
        VTexFormat.BC6H => Format.BC6HUfloatBlock,
        VTexFormat.BC7 => srgb ? Format.BC7SrgbBlock : Format.BC7UnormBlock,
        VTexFormat.RGBA8888 => srgb ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm,
        VTexFormat.BGRA8888 => srgb ? Format.B8G8R8A8Srgb : Format.B8G8R8A8Unorm,
        VTexFormat.I8 => Format.R8Unorm,
        VTexFormat.IA88 => Format.R8G8Unorm,
        VTexFormat.R16 => Format.R16Unorm,
        VTexFormat.RG1616 => Format.R16G16Unorm,
        VTexFormat.RGBA16161616 => Format.R16G16B16A16Unorm,
        VTexFormat.R16F => Format.R16Sfloat,
        VTexFormat.RG1616F => Format.R16G16Sfloat,
        VTexFormat.RGBA16161616F => Format.R16G16B16A16Sfloat,
        VTexFormat.R32F => Format.R32Sfloat,
        VTexFormat.RG3232F => Format.R32G32Sfloat,
        VTexFormat.RGBA32323232F => Format.R32G32B32A32Sfloat,
        _ => null,
    };

    /// <summary>Reads a compiled texture from the packages (first hit) or a loose folder.</summary>
    public static Texture? Read(IEnumerable<Package> packages, string path, string? looseRoot = null)
    {
        var name = path.Replace('\\', '/');
        if (!name.EndsWith("_c", StringComparison.Ordinal))
            name += "_c";
        byte[]? bytes = null;
        if (looseRoot != null && File.Exists(Path.Combine(looseRoot, name)))
            bytes = File.ReadAllBytes(Path.Combine(looseRoot, name));
        foreach (var p in packages)
        {
            if (bytes != null)
                break;
            if (p.FindEntry(name) is { } entry)
                p.ReadEntry(entry, out bytes);
        }
        if (bytes == null)
            return null;
        var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        return resource.DataBlock as Texture;
    }

    /// <summary>
    /// A 2D texture's image ready for sampling, from its preload level down
    /// (<paramref name="cap"/> 0 uploads every level).
    /// </summary>
    public static GpuDevice.GpuImage Upload(GpuDevice gpu, Texture texture, bool srgb, int cap = MaxPreloadResolution)
    {
        var format = VulkanFormat(texture.Format, srgb);
        var mips = (uint)Math.Max(1, (int)texture.NumMipLevels);
        var levels = new List<(uint Level, int Width, int Height, byte[] Data)>();
        if (format == null)
        {
            // Decoded by VRF to RGBA8, the top level only.
            using var bitmap = texture.GenerateBitmap();
            var rgba = new byte[bitmap.Width * bitmap.Height * 4];
            for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    var i = ((y * bitmap.Width) + x) * 4;
                    (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (c.Red, c.Green, c.Blue, c.Alpha);
                }
            levels.Add((0, bitmap.Width, bitmap.Height, rgba));
            format = srgb ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm;
            mips = 1;
        }
        else
        {
            // VRF lists the levels smallest first; a level's extent is the image's
            // shifted down, not VRF's block-rounded size.
            var first = cap > 0 ? PreloadLevel(texture.Width, texture.Height, texture.NumMipLevels, cap) : 0;
            foreach (var (level, _, _, _, size) in texture.GetEveryMipLevelMetrics().Where(m => m.Level >= first).OrderBy(m => m.Level))
            {
                var data = new byte[size];
                texture.ReadTextureMipLevel(data, level);
                levels.Add((level - (uint)first, Math.Max(1, texture.Width >> (int)level), Math.Max(1, texture.Height >> (int)level), data));
            }
            mips = (uint)levels.Count;
        }
        var image = gpu.CreateImage((uint)levels[0].Width, (uint)levels[0].Height, mips, format.Value, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);
        var total = levels.Sum(l => l.Data.Length);
        var staging = gpu.CreateBuffer((ulong)total, BufferUsageFlags.TransferSrcBit);
        var offsets = new ulong[levels.Count];
        ulong at = 0;
        for (var i = 0; i < levels.Count; i++)
        {
            offsets[i] = at;
            levels[i].Data.CopyTo(new Span<byte>((byte*)staging.Mapped + (long)at, levels[i].Data.Length));
            at += (ulong)levels[i].Data.Length;
        }
        gpu.Submit(cmd =>
        {
            gpu.Transition(cmd, image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
            for (var i = 0; i < levels.Count; i++)
            {
                var region = new BufferImageCopy(offsets[i], 0, 0, new ImageSubresourceLayers(ImageAspectFlags.ColorBit, levels[i].Level, 0, 1),
                    new Offset3D(0, 0, 0), new Extent3D((uint)levels[i].Width, (uint)levels[i].Height, 1));
                gpu.Vk.CmdCopyBufferToImage(cmd, staging.Buffer, image.Image, ImageLayout.TransferDstOptimal, 1, &region);
            }
            gpu.Transition(cmd, image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        });
        gpu.Destroy(staging);
        return image;
    }
}
