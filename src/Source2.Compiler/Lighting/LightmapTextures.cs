using System.Buffers.Binary;
using Source2.Compiler.Compression;
using Source2.Compiler.RcTexture;
using ValveKeyValue;
using ValveKeyValue.KeyValues3;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Lighting;

/// <summary>
/// The map's <c>lightmaps/&lt;channel&gt;.vtex_c</c> files from vrad3's images, as
/// resourcecompiler's SerializeLightMap (lightbaker.cpp, 1801f3f40) writes them.
/// <list type="bullet">
/// <item>Each channel's float bitmap is halved with <c>FloatBitMap_t::QuarterSize</c>
/// (180c43450) while its shorter side is over max(the channel's
/// <c>LightmapChannels/&lt;channel&gt;/MaxResolution</c>, the atlas' shorter side
/// &gt;&gt; (mips - 1)), one mip off each time (<see cref="Vtex"/>).</item>
/// <item>It becomes a <c>2DARRAY</c>, linear, child-resource texture, with Box mips
/// while more than one mip is left.</item>
/// <item>The format is picked by channel (compressed / uncompressed):
///   <list type="bullet">
///   <item>irradiance and sh2_dc: BC6H / RGBA16161616F;</item>
///   <item>directional_irradiance, sh2_r/g/b and debug_chart_color: BC7 / RGBA16161616F;</item>
///   <item>direct_light_shadows, from the channels that hold a value under 1: ATI1N (only
///   the first), ATI2N (the second too), BC7 (the third or fourth) / RGBA8888.</item>
///   </list>
/// gameinfo's <c>LightmapChannels/&lt;channel&gt;/CompressedFormat</c> (or
/// <c>UncompressedFormat</c>) replaces the choice by name: CS2 sets DXT1 for
/// directional_irradiance and debug_chart_color.</item>
/// </list>
/// The pixels are encoded as the texture compiler encodes them:
/// <list type="bullet">
/// <item>BC6H by ispc_texcomp's slow profile, a row of blocks per call;</item>
/// <item>DXT1 and ATI1N/ATI2N by <see cref="AtiCompress"/> with weights 1 from the
/// 8-bit writer (<see cref="RcMipChain.WriteRgba8"/>, linear).</item>
/// </list>
/// The header's reflectivity is the image's mean in four lanes. Mip 0 is LZ4 HC
/// level 10 when that saves 5% and 32 KB. The DATA block has COMPRESSED_MIP_SIZE
/// as its only extra data. On probe01 every block of all four 2D textures equals
/// Valve's, and so do the headers. RED2's two fingerprints are derived:
/// <list type="bullet">
/// <item><c>___OverrideInputData___</c>, the CRC32 of the texture descriptor as KV3
/// text (<see cref="Descriptor"/>, <see cref="DescriptorFingerprint"/>).</item>
/// <item>The ImageArg, a CRC32 over the float bitmap (<see cref="ImageFingerprint"/>);
/// the ambient cube atlas passes the literal 1.</item>
/// </list>
/// </summary>
public static class LightmapTextures
{
    /// <summary>The image formats SerializeLightMap names (tier0 ImageFormat, 182fe4eb0's table).</summary>
    public enum ImageFormat
    {
        Rgba8888 = 0,
        Rgba16161616F = 0xf,
        Dxt1 = 0x14,
        Ati1n = 0x18,
        Ati2n = 0x19,
        Bc6h = 0x1a,
        Bc7 = 0x1b,
    }

    /// <summary>One channel's settings from gameinfo's ResourceCompiler/BakedLighting/LightmapChannels.</summary>
    public sealed record ChannelSettings(int MaxResolution = 0, string? CompressedFormat = null, string? UncompressedFormat = null);

    /// <summary>RED2's two hashes, when a caller passes them instead of deriving them (see the class summary).</summary>
    public readonly record struct Fingerprints(uint OverrideInputData, uint Image);

    /// <summary>The channels CS2 bakes: gameinfo's LightmapChannels block.</summary>
    public static readonly IReadOnlyDictionary<string, ChannelSettings> Cs2Channels = new Dictionary<string, ChannelSettings>
    {
        ["irradiance"] = new(),
        ["direct_light_shadows"] = new(),
        ["directional_irradiance"] = new(4096, "DXT1"),
        ["debug_chart_color"] = new(4096, "DXT1"),
    };

    private const int CompressMinRatio = 95, Lz4Level = 10;
    private const ushort FlagVolumeTexture = 0x20, FlagTextureArray = 0x40;
    private const uint ExtraCompressedMipSize = 4;

    /// <summary>
    /// The format SerializeLightMap picks for <paramref name="channel"/>, before
    /// gameinfo's override. Shadows scan the bitmap (every slice, row and column)
    /// for a value under 1 in each channel.
    /// </summary>
    public static ImageFormat Format(string channel, RcMipChain.Level image, bool compressed)
    {
        switch (channel)
        {
            case "direct_light_shadows":
                if (!compressed)
                    return ImageFormat.Rgba8888;
                var used = new bool[4];
                var rgba = image.Rgba;
                for (var i = 0; i < rgba.Length; i++)
                    if (rgba[i] < 1f)
                        used[i & 3] = true;
                if (used[2] || used[3])
                    return ImageFormat.Bc7;
                if (used[1])
                    return ImageFormat.Ati2n;
                if (!used[0])
                    throw new NotSupportedException("direct_light_shadows with no shadow anywhere: RC sizes it to 4x4 (local_1ff0), not ported.");
                return ImageFormat.Ati1n;
            case "irradiance" or "directional_irradiance_sh2_dc":
                return compressed ? ImageFormat.Bc6h : ImageFormat.Rgba16161616F;
            case "debug_chart_color" or "directional_irradiance" or "directional_irradiance_sh2_r"
                or "directional_irradiance_sh2_g" or "directional_irradiance_sh2_b":
                return compressed ? ImageFormat.Bc7 : ImageFormat.Rgba16161616F;
            default:
                throw new ArgumentException($"SerializeLightMap has no lightmap channel '{channel}'.", nameof(channel));
        }
    }

    /// <summary>
    /// What SerializeLightMap takes from the map compile besides the image: the mip count
    /// BakeLighting chose (lightmapMipCount and MinSpecLightmapSize, <see cref="LightmapSettings.MipChain"/>),
    /// lightmapVRadQuality, whether lightmapCompressionDisabled is off, the compile's command-line
    /// arguments (the texture compiler records <c>fast</c> and <c>lightmapMaxResolution</c> from
    /// them) and gameinfo's BakedLighting/BC6HHueShiftFixup (default 1).
    /// </summary>
    public sealed record Serialization(int MipCount = 1, int Quality = 0, bool Compressed = true,
        IReadOnlyDictionary<string, object>? CompileArguments = null, bool Bc6hHueShiftFixup = true)
    {
        public static readonly Serialization Default = new();
    }

    // 182a88554: SerializeLightMap's "final" by lightmapVRadQuality.
    private static readonly byte[] FinalByQuality = [0, 0, 1];

    /// <summary>
    /// The channel's <c>.vtex_c</c>. <paramref name="image"/> is the channel's
    /// float bitmap, RGBA (<see cref="Bitmap"/>), as LoadFromEXR fills it: a channel the EXR lacks is 0,
    /// alpha 1. <paramref name="searchPath"/> is the search path RC records for
    /// the argument file (<c>csgo_addons/&lt;addon&gt;</c>). The BC6H encoder is
    /// loaded from <paramref name="nativeFolder"/> (the install's game/bin/win64).
    /// SerializeLightMap (1801f3f40):
    /// <list type="bullet">
    /// <item>the output's minimum dimension is the atlas' shorter side shifted down by mips - 1,
    /// taken before any halving;</item>
    /// <item>with a channel MaxResolution, the bitmap is halved while its shorter side is over
    /// max(that minimum, MaxResolution), each halving taking one mip off;</item>
    /// <item>the format is picked (shadows scan the unhalved bitmap), then gameinfo's
    /// Compressed/UncompressedFormat replaces it when it names a format (its text kept as written);</item>
    /// <item>every channel but debug_chart_color gets mip algorithm Box&lt;n&gt; while n, the mips left, is over 1;</item>
    /// <item>final = 182a88554[quality] (an IntArg), and hueShiftFixup = 1 (IntArg) on BC6H when
    /// final and gameinfo's BC6HHueShiftFixup are set.</item>
    /// </list>
    /// </summary>
    public static byte[] Vtex(string channel, RcMipChain.Level image, ChannelSettings settings, string searchPath,
        Fingerprints? fingerprints = null, Serialization? options = null, string? nativeFolder = null, Action<string>? note = null)
    {
        options ??= Serialization.Default;
        var original = image;
        var mips = options.MipCount;
        var minDimension = Math.Min(image.Width, image.Height) >> (mips - 1);
        if (settings.MaxResolution > 0)
        {
            var threshold = Math.Max(minDimension, settings.MaxResolution);
            while (Math.Min(image.Width, image.Height) > threshold)
            {
                image = RcMipChain.QuarterSize(image);
                mips--;
            }
        }
        var format = Format(channel, original, options.Compressed);
        var formatName = FormatName(format);
        var overrideName = options.Compressed ? settings.CompressedFormat : settings.UncompressedFormat;
        if (overrideName is not null && FormatByName(overrideName) is { } named)
        {
            format = named;
            formatName = overrideName;
        }
        var algorithm = channel != "debug_chart_color" && mips > 1 ? $"Box{mips}" : "None";
        var final = options.Quality is >= 0 and < 3 && FinalByQuality[options.Quality] != 0;
        var hueShiftFixup = format == ImageFormat.Bc6h && options.Bc6hHueShiftFixup && final;
        if (hueShiftFixup)
            note?.Invoke($"{channel}: BC6H hue-shift fixup (180c46dd0) not ported; encoded without it");

        var derived = fingerprints ?? new Fingerprints(
            DescriptorFingerprint(Descriptor("2DARRAY", LightmapArgument.Name, formatName, minDimension, 0, algorithm)),
            ImageFingerprint(image, 1));
        var levels = algorithm == "None" ? [image] : MipLevels(image, mips);
        // The texture compiler's quality (180133da0): 3 in a console build, 4 when final is set.
        var quality = final ? 4 : 3;
        var args = new TextureArguments(final ? 1 : 0, hueShiftFixup, options.CompileArguments, algorithm == "None" ? "None" : "Box", quality);
        return Write([levels], format, FlagTextureArray, EditInfo(LightmapArgument, searchPath, derived, args), nativeFolder, quality);
    }

    // GenerateMips_Box: each level a QuarterSize of the one above.
    private static RcMipChain.Level[] MipLevels(RcMipChain.Level top, int count)
    {
        var levels = new RcMipChain.Level[count];
        levels[0] = top;
        for (var m = 1; m < count; m++)
            levels[m] = RcMipChain.QuarterSize(levels[m - 1]);
        return levels;
    }

    // FUN_180c490a0: the format a name stands for, ignoring case; null when it names none of the seven here.
    private static ImageFormat? FormatByName(string name)
    {
        foreach (var f in Enum.GetValues<ImageFormat>())
            if (FormatName(f).Equals(name, StringComparison.OrdinalIgnoreCase))
                return f;
        return null;
    }

    /// <summary>
    /// A light probe volume atlas, as 1801eee20 hands it to the texture compiler:
    /// the atlas EXR (lpv_atlas_ambientcube.exr, lpv_atlas_dlshd.exr) loaded as a
    /// 2D image and cut into a <c>3D</c> texture of
    /// <paramref name="sliceWidth"/> x <paramref name="sliceHeight"/> slices,
    /// tiles taken row by row from the top left. The ambient cube has six slices
    /// per atlas layer (one per face) and is BC6H
    /// (<c>WorldRenderer/LightProbeVolumeCompressedFormat</c> otherwise); its
    /// ImageArg fingerprint is the literal 1 RC passes. dlshd has one slice per
    /// layer and is BC7. Both are linear. Reflectivity is the mean over the
    /// slices in order. probe01's two atlases are identical slice for slice.
    /// </summary>
    public static byte[] AtlasVtex(RcMipChain.Level image, int sliceWidth, int sliceHeight, int sliceCount, ImageFormat format,
        string searchPath, Fingerprints? fingerprints = null, string? nativeFolder = null, bool ambientCube = false, string algorithm = "None",
        IReadOnlyDictionary<string, object>? compileArguments = null)
    {
        var columns = image.Width / sliceWidth;
        if (columns == 0 || (sliceCount + columns - 1) / columns * sliceHeight > image.Height)
            throw new ArgumentException($"A {image.Width}x{image.Height} image does not hold {sliceCount} slices of {sliceWidth}x{sliceHeight}.", nameof(image));
        var slices = new RcMipChain.Level[sliceCount];
        for (var z = 0; z < sliceCount; z++)
        {
            var px = new float[sliceWidth * sliceHeight * 4];
            for (var y = 0; y < sliceHeight; y++)
                Array.Copy(image.Rgba, (((z / columns) * sliceHeight + y) * image.Width + (z % columns) * sliceWidth) * 4, px, y * sliceWidth * 4, sliceWidth * 4);
            slices[z] = new RcMipChain.Level(sliceWidth, sliceHeight, px);
        }
        var derived = fingerprints ?? new Fingerprints(
            DescriptorFingerprint(Descriptor("3D", LightProbeVolumeArgument.Name, FormatName(format), 0, 0, "None", sliceCount, sliceWidth, sliceHeight, -1, algorithm)),
            ambientCube ? 1u : ImageFingerprint(image, 1));
        return Write([.. slices.Select(slice => new[] { slice })], format, FlagVolumeTexture,
                     EditInfo(LightProbeVolumeArgument, searchPath, derived, new TextureArguments(null, false, compileArguments)), nativeFolder, 3);
    }

    // CResourceCompilerTexture's LZ4 HC level by texture quality (180133da0): 3, 4, 9, 10, 12.
    private static int Lz4LevelFor(int quality) => quality switch { 0 => 3, 1 => 4, 3 => 10, 4 => 12, _ => 9 };

    // The texture: each mip level (every slice in order) encoded, LZ4 HC'd when that pays,
    // stored smallest first behind RED2 and DATA; the mip sizes are listed from mip 0.
    private static byte[] Write(RcMipChain.Level[][] slices, ImageFormat format, ushort flags, KVObject editInfo, string? nativeFolder, int quality)
    {
        var (width, height) = (slices[0][0].Width, slices[0][0].Height);
        var mipCount = slices[0].Length;
        var sizes = Math.Max(width, height) >= 33;
        var stored = new byte[mipCount][];
        var lz4 = false;
        for (var m = 0; m < mipCount; m++)
        {
            var mip = slices.SelectMany(slice => Encode(slice[m], format, nativeFolder, quality)).ToArray();
            stored[m] = mip;
            if (!sizes)
                continue;
            var packed = new byte[Lz4HcStream.CompressBound(mip.Length)];
            var c = Lz4HcStream.Compress(mip, packed, Lz4LevelFor(quality));
            if (!(c == 0 || (long)mip.Length * CompressMinRatio <= (long)c * 100 || mip.Length - 0x8000 <= c))
            {
                stored[m] = packed[..c];
                lz4 = true;
            }
        }

        // The reflectivity is the smallest mip's (maxres8192's Box2 lightmaps).
        var last = mipCount - 1;
        var volume = slices.Length == 1 ? slices[0][last]
            : new RcMipChain.Level(slices[0][last].Width, slices[0][last].Height * slices.Length, slices.SelectMany(slice => slice[last].Rgba).ToArray());
        var data = Data(width, height, slices.Length, mipCount, flags, VtexFormat(format), Reflectivity(volume), sizes ? [.. stored.Select(x => x.Length)] : null, lz4);
        using var resource = new Resource();
        Source2ContainerAuthor.SetResourceVersion(resource, 1);
        resource.Blocks.Add(AuthoredKv3.Block(editInfo, KV3IDLookup.Get("generic"), BlockType.RED2, resource));
        resource.Blocks.Add(ResourceBuilder.RawBlock(BlockType.DATA, data, resource));
        var container = ValveLayout.Serialize(resource);
        var file = new List<byte>(container.Length + stored.Sum(x => x.Length));
        file.AddRange(container);
        for (var m = mipCount - 1; m >= 0; m--)
            file.AddRange(stored[m]);
        return [.. file];
    }

    /// <summary>
    /// The float bitmap of an EXR as FloatBitMap_t::LoadFromInMemoryEXR
    /// (180c40c20) holds it: R, G, B and A interleaved, alpha 1 when the EXR has
    /// none. RC refuses an EXR without R, G or B; here such a channel reads 0.
    /// </summary>
    public static RcMipChain.Level Bitmap(Vrad3Exr exr)
    {
        var n = exr.Width * exr.Height;
        var rgba = new float[n * 4];
        var planes = new[] { "R", "G", "B", "A" }.Select(c => exr.Channels.Contains(c) ? exr.Plane(c) : null).ToArray();
        for (var i = 0; i < n; i++)
            for (var c = 0; c < 4; c++)
                rgba[i * 4 + c] = planes[c] is { } p ? (float)p[i] : c == 3 ? 1f : 0f;
        return new RcMipChain.Level(exr.Width, exr.Height, rgba);
    }

    /// <summary>The header's reflectivity: each channel's sum in four lanes by pixel index, the lanes added pairwise, over the pixel count.</summary>
    public static float[] Reflectivity(RcMipChain.Level image)
    {
        var n = image.Width * image.Height;
        var result = new float[4];
        Span<float> lanes = stackalloc float[4];
        for (var c = 0; c < 4; c++)
        {
            lanes.Clear();
            for (var i = 0; i < n; i++)
                lanes[i & 3] += image.Rgba[i * 4 + c];
            result[c] = ((lanes[0] + lanes[1]) + (lanes[2] + lanes[3])) / n;
        }
        return result;
    }

    private static byte[] Encode(RcMipChain.Level image, ImageFormat format, string? nativeFolder, int quality)
    {
        switch (format)
        {
            case ImageFormat.Bc6h:
                if (nativeFolder is null || !IspcTexcomp.Load(nativeFolder))
                    throw new InvalidOperationException("BC6H needs ispc_texcomp.dll (the CS2 install's game/bin/win64).");
                // The row fill (180c3ef20): each of R, G, B as max(0, x) with the sign cleared through the
                // truncating float16 (1802ea750), alpha 1.0 (0x3c00).
                var half = new Half[image.Rgba.Length];
                for (var i = 0; i < half.Length; i++)
                {
                    if ((i & 3) == 3)
                    {
                        half[i] = BitConverter.UInt16BitsToHalf(0x3c00);
                        continue;
                    }
                    var x = image.Rgba[i];
                    var m = float.IsNaN(x) ? x : MathF.Max(0f, x);
                    half[i] = BitConverter.UInt16BitsToHalf(Maps.WorldNodeModels.ToHalf(BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(m) & 0x7fffffff)));
                }
                // EncodeBC6Blocks (180c46b20): quality 3 is the slow profile, 4 (final) veryslow.
                return IspcTexcomp.CompressBc6h(half, image.Width, image.Height, IspcTexcomp.Bc6hProfile(quality));
            case ImageFormat.Dxt1 or ImageFormat.Ati1n or ImageFormat.Ati2n:
                var bgra = RcMipChain.WriteRgba8(image, srgb: false);
                for (var i = 0; i < bgra.Length; i += 4)
                    (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
                var ati = format switch
                {
                    ImageFormat.Dxt1 => AtiCompress.Format.Dxt1,
                    ImageFormat.Ati1n => AtiCompress.Format.Ati1n,
                    _ => AtiCompress.Format.Ati2n,
                };
                return AtiCompress.Compress(bgra, image.Width, image.Height, ati, perceptual: false);
            case ImageFormat.Bc7:
                return Bc7e.EncodeBlocks(Bc7Blocks(image), perceptual: false, Bc7Vendor.Amd);
            case ImageFormat.Rgba16161616F:
                // Every channel through the truncating float16 (1802ea750).
                var f16 = new byte[image.Rgba.Length * 2];
                for (var i = 0; i < image.Rgba.Length; i++)
                    BinaryPrimitives.WriteUInt16LittleEndian(f16.AsSpan(i * 2), Maps.WorldNodeModels.ToHalf(image.Rgba[i]));
                return f16;
            case ImageFormat.Rgba8888:
                return RcMipChain.WriteRgba8(image, srgb: false);
            default:
                throw new NotSupportedException($"Lightmap format {format} is not encoded here yet.");
        }
    }

    // bc7e's input: 4x4 blocks in block order, each channel ROUND(clamp(x) * 255)
    // to nearest even, a block past the edge repeating the last row and column
    // (docs/TEXTURES.md on main).
    private static byte[] Bc7Blocks(RcMipChain.Level level)
    {
        int bw = (level.Width + 3) / 4, bh = (level.Height + 3) / 4;
        var o = new byte[bw * bh * 64];
        var k = 0;
        for (var by = 0; by < bh; by++)
            for (var bx = 0; bx < bw; bx++)
                for (var y = 0; y < 4; y++)
                    for (var x = 0; x < 4; x++)
                    {
                        var src = (Math.Min(by * 4 + y, level.Height - 1) * level.Width + Math.Min(bx * 4 + x, level.Width - 1)) * 4;
                        for (var c = 0; c < 4; c++)
                            o[k++] = (byte)MathF.Round(MathF.Min(1f, MathF.Max(0f, level.Rgba[src + c])) * 255f, MidpointRounding.ToEven);
                    }
        return o;
    }

    // The .vtex_c format byte (VTexFormat) for an image format.
    private static byte VtexFormat(ImageFormat format) => format switch
    {
        ImageFormat.Dxt1 => 1,
        ImageFormat.Rgba8888 => 4,
        ImageFormat.Rgba16161616F => 10,
        ImageFormat.Bc6h => 19,
        ImageFormat.Bc7 => 20,
        ImageFormat.Ati2n => 21,
        ImageFormat.Ati1n => 27,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    // DATA: the 40-byte header (one mip), then
    // COMPRESSED_MIP_SIZE and the stored size of mip 0 when the largest side is
    // at least 33.
    private static byte[] Data(int width, int height, int depth, int mips, ushort flags, byte format, float[] reflectivity, int[]? storedSizes, bool lz4)
    {
        const int header = 40;
        var extras = storedSizes is null ? 0 : 1;
        var d = new byte[header + extras * (12 + 12 + 4 * mips)];
        var s = d.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(s, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], flags);
        for (var c = 0; c < 4; c++)
            BinaryPrimitives.WriteSingleLittleEndian(s[(4 + 4 * c)..], reflectivity[c]);
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(s[22..], (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(s[24..], (ushort)depth);
        d[26] = format;
        d[27] = (byte)mips;
        BinaryPrimitives.WriteUInt32LittleEndian(s[32..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(s[36..], (uint)extras);
        if (storedSizes is { } sizes)
        {
            // Entry {type, offset from its field, size}, then the payload
            // {method, offset to the sizes from its field, count}, then the size.
            BinaryPrimitives.WriteUInt32LittleEndian(s[40..], ExtraCompressedMipSize);
            BinaryPrimitives.WriteUInt32LittleEndian(s[44..], 52 - 44);
            BinaryPrimitives.WriteUInt32LittleEndian(s[48..], 12);
            BinaryPrimitives.WriteUInt32LittleEndian(s[52..], lz4 ? 1u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(s[56..], 64 - 56);
            BinaryPrimitives.WriteUInt32LittleEndian(s[60..], (uint)mips);
            for (var m = 0; m < mips; m++)
                BinaryPrimitives.WriteUInt32LittleEndian(s[(64 + 4 * m)..], (uint)sizes[m]);
        }
        return d;
    }

    /// <summary>
    /// The ImageArg fingerprint resourcecompiler computes for a bitmap passed
    /// with fingerprint -1 (CResourceCompilerContext vf9, 180174ce0): CRC32
    /// over height, width and depth (int32 each, in that order), then, for each
    /// channel present in the bitmap's mask (R, G, B, A), every row of that
    /// channel's float plane, slice by slice. A bitmap loaded from an EXR has all
    /// four: the loader marks the alpha it fills (probe01's irradiance has no
    /// alpha and matches only with it).
    /// </summary>
    public static uint ImageFingerprint(RcMipChain.Level image, int depth, int channelMask = 15)
    {
        var crc = new System.IO.Hashing.Crc32();
        Span<byte> head = stackalloc byte[12];
        var height = image.Height / depth;
        BinaryPrimitives.WriteInt32LittleEndian(head, height);
        BinaryPrimitives.WriteInt32LittleEndian(head[4..], image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(head[8..], depth);
        crc.Append(head);
        var row = new byte[image.Width * 4];
        for (var c = 0; c < 4; c++)
        {
            if ((channelMask & (1 << c)) == 0)
                continue;
            for (var z = 0; z < depth; z++)
                for (var y = 0; y < height; y++)
                {
                    var at = ((z * height + y) * image.Width) * 4 + c;
                    for (var x = 0; x < image.Width; x++)
                        BinaryPrimitives.WriteSingleLittleEndian(row.AsSpan(x * 4), image.Rgba[at + x * 4]);
                    crc.Append(row);
                }
        }
        return crc.GetCurrentHashAsUInt32();
    }

    /// <summary>
    /// The texture descriptor SerializeLightMap and 1801eee20 hand the texture
    /// compiler, as the KV3 text 181e8e7c0 saves it (transfer 181e97100: input
    /// textures 181e969d0 with image processors 181e96830, output channels
    /// 181e96d30), every member written. Its CRC32 is RED2's
    /// <c>___OverrideInputData___</c> fingerprint.
    /// </summary>
    public static KVObject Descriptor(string type, string argument, string format, int minDimension, int maxDimension, string mipAlgorithm,
        int sliceCount = -1, int sliceWidth = -1, int sliceHeight = -1, int minBits = -1, string processor = "none")
    {
        static KVObject Floats(int n)
        {
            var a = KVObject.Array();
            for (var i = 0; i < n; i++)
                a.Add(new KVObject(0.0));
            return a;
        }
        static KVObject Processor(string algorithm)
        {
            var p = KVObject.Collection();
            p.Add("m_algorithm", new KVObject(algorithm));
            p.Add("m_stringArg", new KVObject(""));
            p.Add("m_vFloat4Arg", Floats(4));
            return p;
        }
        var input = KVObject.Collection();
        input.Add("m_name", new KVObject("inputTexture0"));
        input.Add("m_fileName", new KVObject(argument));
        input.Add("m_colorSpace", new KVObject("linear"));
        input.Add("m_fileExt", new KVObject(""));
        input.Add("m_nMinBitsPerChannel", new KVObject(minBits));
        input.Add("m_typeString", new KVObject(type));
        input.Add("m_bPassThroughToCompiledVtex", new KVObject(false));
        input.Add("m_n3DSliceCount", new KVObject(sliceCount));
        input.Add("m_n3DSliceWidth", new KVObject(sliceWidth));
        input.Add("m_n3DSliceHeight", new KVObject(sliceHeight));
        var processors = KVObject.Array();
        processors.Add(Processor(processor));
        input.Add("m_imageProcessorArray", processors);
        var inputs = KVObject.Array();
        inputs.Add(input);

        var channel = KVObject.Collection();
        channel.Add("m_srcChannels", new KVObject("rgba"));
        channel.Add("m_dstChannels", new KVObject("rgba"));
        channel.Add("m_mipAlgorithm", Processor(mipAlgorithm));
        channel.Add("m_outputColorSpace", new KVObject("linear"));
        var channelInputs = KVObject.Array();
        channelInputs.Add(new KVObject("inputTexture0"));
        channel.Add("m_inputTextureArray", channelInputs);
        var channels = KVObject.Array();
        channels.Add(channel);

        var root = KVObject.Collection();
        root.Add("m_inputTextureArray", inputs);
        root.Add("m_outputTypeString", new KVObject(type));
        root.Add("m_outputFormat", new KVObject(format));
        root.Add("m_outputClearColor", Floats(4));
        root.Add("m_nOutputMinDimension", new KVObject(minDimension));
        root.Add("m_nOutputMaxDimension", new KVObject(maxDimension));
        root.Add("m_nOutputDimensionReduce", new KVObject(0));
        root.Add("m_textureOutputChannelArray", channels);
        root.Add("m_vClamp", Floats(3));
        root.Add("m_bNoLod", new KVObject(false));
        root.Add("m_bHiddenAssetFlag", new KVObject(true));
        root.Add("m_bNormalizeRange", new KVObject(false));
        root.Add("m_bVirtualTexture", new KVObject(false));
        root.Add("m_nDisplayRectWidth", new KVObject(0));
        root.Add("m_nDisplayRectHeight", new KVObject(0));
        root.Add("m_nMotionVectorsMaxDistanceInPixels", new KVObject(0));
        return root;
    }

    /// <summary>
    /// RED2's <c>___OverrideInputData___</c> fingerprint: the CRC32 of the
    /// descriptor's KV3 text as the text CUtlBuffer holds it, LF line ends, no
    /// terminator (probe01's three lightmap formats and both atlases).
    /// </summary>
    public static uint DescriptorFingerprint(KVObject descriptor)
        => System.IO.Hashing.Crc32.HashToUInt32(System.Text.Encoding.UTF8.GetBytes(
            Kv3TextWriter.Write(descriptor, new Guid("7412167c-06e9-4698-aff2-e63eb59037e7")).Replace("\r\n", "\n")));

    /// <summary>The ImageFormat name (182fe4eb0's table) the descriptor records.</summary>
    public static string FormatName(ImageFormat format) => format switch
    {
        ImageFormat.Rgba8888 => "RGBA8888",
        ImageFormat.Rgba16161616F => "RGBA16161616F",
        ImageFormat.Dxt1 => "DXT1",
        ImageFormat.Ati1n => "ATI1N",
        ImageFormat.Ati2n => "ATI2N",
        ImageFormat.Bc6h => "BC6H",
        ImageFormat.Bc7 => "BC7",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>The image argument a texture is compiled from, and the type RC records for <c>final</c>.</summary>
    public sealed record TextureArgument(string Name, string FinalType);

    /// <summary>SerializeLightMap's argument (<c>final</c> set as an int).</summary>
    public static readonly TextureArgument LightmapArgument = new("lightmapTextureArg", "IntArg");

    /// <summary>1801eee20's argument (<c>final</c> recorded as a float).</summary>
    public static readonly TextureArgument LightProbeVolumeArgument = new("lightProbeVolumeTextureArg", "FloatArg");

    /// <summary>
    /// RED2 as RC records a lightmap texture: the argument file it probes (the
    /// argument's name lowercased, <c>.txt</c>, absent), the texture compiler's arguments in
    /// name order (maxmipsize's fingerprint and default are 1048576f's bits), the
    /// three texture compiler special dependencies, and a hidden child resource.
    /// </summary>
    /// <summary>
    /// The texture compiler arguments RED2 records besides the image: <c>final</c> as SerializeLightMap
    /// sets it (an IntArg; null when the caller sets none, as 1801eee20), <c>hueShiftFixup</c>
    /// (IntArg 1 when set), and the compile's command-line arguments the texture compiler reads
    /// (<c>fast</c>, <c>lightmapMaxResolution</c>): a float as a FloatArg of its bits, an int as an IntArg.
    /// </summary>
    /// <remarks>
    /// The special dependencies follow the compile too: "Texture Compiler Version Mip &lt;algorithm&gt;"
    /// names the mip algorithm (None, Box), and "Texture Encode Quality" carries the quality as its
    /// user data (3; maxres8192's Box2 lightmaps and quality2's final ones).
    /// </remarks>
    public sealed record TextureArguments(int? Final, bool HueShiftFixup, IReadOnlyDictionary<string, object>? CompileArguments,
        string MipAlgorithm = "None", int Quality = 3)
    {
        public static readonly TextureArguments None = new(null, false, null);
    }

    public static KVObject EditInfo(TextureArgument argument, string searchPath, Fingerprints fingerprints, TextureArguments? textureArguments = null)
    {
        textureArguments ??= TextureArguments.None;
        (string Type, uint Fingerprint) FromCompile(string name) => textureArguments.CompileArguments?.TryGetValue(name, out var v) == true
            ? v switch
            {
                float f => ("FloatArg", BitConverter.SingleToUInt32Bits(f)),
                int i => ("IntArg", (uint)i),
                _ => ("FloatArg", 0u),
            }
            : ("FloatArg", 0u);
        static KVObject Argument(string name, string type, uint fingerprint, uint fingerprintDefault = 0)
        {
            var a = KVObject.Collection();
            a.Add("m_ParameterName", new KVObject(name));
            a.Add("m_ParameterType", new KVObject(type));
            a.Add("m_nFingerprint", new KVObject(fingerprint));
            a.Add("m_nFingerprintDefault", new KVObject(fingerprintDefault));
            return a;
        }
        var maxMip = BitConverter.SingleToUInt32Bits(1048576f);
        var args = KVObject.Array();
        args.Add(Argument("___OverrideInputData___", "BinaryBlobArg", fingerprints.OverrideInputData));
        var fast = FromCompile("fast");
        args.Add(Argument("fast", fast.Type, fast.Fingerprint));
        args.Add(Argument("final", textureArguments.Final is null ? argument.FinalType : "IntArg", (uint)(textureArguments.Final ?? 0)));
        args.Add(textureArguments.HueShiftFixup ? Argument("hueShiftFixup", "IntArg", 1) : Argument("hueShiftFixup", "FloatArg", 0));
        var maxResolution = FromCompile("lightmapMaxResolution");
        args.Add(Argument("lightmapMaxResolution", maxResolution.Type, maxResolution.Fingerprint));
        args.Add(Argument(argument.Name, "ImageArg", fingerprints.Image));
        args.Add(Argument(argument.Name, "BinaryBlobArg", 0));
        args.Add(Argument("maxmipsize", "FloatArg", maxMip, maxMip));
        args.Add(Argument("minmipsize", "FloatArg", 0));

        var probe = KVObject.Collection();
        probe.Add("m_RelativeFilename", new KVObject(argument.Name.ToLowerInvariant() + ".txt"));
        probe.Add("m_SearchPath", new KVObject(searchPath));
        probe.Add("m_nFileCRC", new KVObject(0u));
        probe.Add("m_bOptional", new KVObject(true));
        probe.Add("m_bFileExists", new KVObject(false));
        probe.Add("m_bIsGameFile", new KVObject(false));
        var additional = KVObject.Array();
        additional.Add(probe);

        var special = KVObject.Array();
        foreach (var stock in Source2ContainerAuthor.TextureSpecialDeps)
        {
            var dep = stock.Name switch
            {
                "Texture Compiler Version Mip None" => stock with { Name = $"Texture Compiler Version Mip {textureArguments.MipAlgorithm}" },
                "Texture Encode Quality" => stock with { UserData = textureArguments.Quality },
                _ => stock,
            };
            var d = KVObject.Collection();
            d.Add("m_String", new KVObject(dep.Name));
            d.Add("m_CompilerIdentifier", new KVObject(dep.CompilerIdentifier));
            d.Add("m_nFingerprint", new KVObject((uint)dep.Fingerprint));
            d.Add("m_nUserData", new KVObject((uint)dep.UserData));
            special.Add(d);
        }

        var user = KVObject.Collection();
        user.Add("HiddenAssetFlag", new KVObject(1L));
        user.Add("IsChildResource", new KVObject(1L));
        var root = KVObject.Collection();
        root.Add("m_InputDependencies", KVObject.Array());
        root.Add("m_AdditionalInputDependencies", additional);
        root.Add("m_ArgumentDependencies", args);
        root.Add("m_SpecialDependencies", special);
        foreach (var key in new[] { "m_SpecialInputDependencies", "m_AdditionalRelatedFiles", "m_ChildResourceList", "m_WeakReferenceList" })
            root.Add(key, KVObject.Array());
        root.Add("m_SearchableUserData", user);
        root.Add("m_SubassetReferences", KVObject.Null());
        root.Add("m_SubassetDefinitions", KVObject.Null());
        return root;
    }
}
