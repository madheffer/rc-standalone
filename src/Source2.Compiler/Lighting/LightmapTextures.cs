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
/// (180c43450) while its shorter side is over the channel's
/// <c>LightmapChannels/&lt;channel&gt;/MaxResolution</c>.</item>
/// <item>It becomes a <c>2DARRAY</c>, linear, no mips, child-resource texture.</item>
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
/// Valve's, and so do the headers. RED2's two fingerprints are not yet derived and
/// are passed in:
/// <list type="bullet">
/// <item><c>___OverrideInputData___</c>, the CRC32 of the texture descriptor as KV3
/// text (181e8e7c0). It is the same for every texture with the same descriptor:
/// directional and chart colour share 1275215511.</item>
/// <item>The <c>lightmapTextureArg</c> ImageArg, a hash of the float bitmap.</item>
/// </list>
/// docs/BAKED.md has the open items.
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

    /// <summary>RED2's two hashes that are not derived yet (see the class summary).</summary>
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
    /// The channel's <c>.vtex_c</c>. <paramref name="image"/> is the channel's
    /// float bitmap, RGBA (<see cref="Bitmap"/>), as LoadFromEXR fills it: a channel the EXR lacks is 0,
    /// alpha 1. <paramref name="searchPath"/> is the search path RC records for
    /// the argument file (<c>csgo_addons/&lt;addon&gt;</c>). The BC6H encoder is
    /// loaded from <paramref name="nativeFolder"/> (the install's game/bin/win64).
    /// </summary>
    public static byte[] Vtex(string channel, RcMipChain.Level image, ChannelSettings settings, string searchPath,
        Fingerprints fingerprints, bool compressed = true, string? nativeFolder = null)
    {
        if (settings.MaxResolution > 0)
            while (Math.Min(image.Width, image.Height) > settings.MaxResolution)
                image = RcMipChain.QuarterSize(image);
        var format = Format(channel, image, compressed);
        var overrideName = compressed ? settings.CompressedFormat : settings.UncompressedFormat;
        if (overrideName is not null)
            format = Enum.GetValues<ImageFormat>().FirstOrDefault(f => f.ToString().Equals(overrideName, StringComparison.OrdinalIgnoreCase), (ImageFormat)(-1)) is var named && (int)named >= 0
                ? named
                : throw new NotSupportedException($"LightmapChannels/{channel}: format {overrideName} is not written here.");

        return Write([image], format, FlagTextureArray, EditInfo(LightmapArgument, searchPath, fingerprints), nativeFolder);
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
        string searchPath, Fingerprints fingerprints, string? nativeFolder = null)
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
        return Write(slices, format, FlagVolumeTexture, EditInfo(LightProbeVolumeArgument, searchPath, fingerprints), nativeFolder);
    }

    // The texture: every slice encoded in order as mip 0, LZ4 HC'd when that
    // pays, behind RED2 and DATA.
    private static byte[] Write(RcMipChain.Level[] slices, ImageFormat format, ushort flags, KVObject editInfo, string? nativeFolder)
    {
        var (width, height) = (slices[0].Width, slices[0].Height);
        var mip = slices.SelectMany(slice => Encode(slice, format, nativeFolder)).ToArray();
        var stored = mip;
        var lz4 = false;
        var sizes = Math.Max(width, height) >= 33;
        if (sizes)
        {
            var packed = new byte[Lz4HcStream.CompressBound(mip.Length)];
            var c = Lz4HcStream.Compress(mip, packed, Lz4Level);
            if (!(c == 0 || (long)mip.Length * CompressMinRatio <= (long)c * 100 || mip.Length - 0x8000 <= c))
            {
                stored = packed[..c];
                lz4 = true;
            }
        }

        var volume = slices.Length == 1 ? slices[0] : new RcMipChain.Level(width, height * slices.Length, slices.SelectMany(slice => slice.Rgba).ToArray());
        var data = Data(width, height, slices.Length, flags, VtexFormat(format), Reflectivity(volume), sizes ? stored.Length : null, lz4);
        using var resource = new Resource();
        Source2ContainerAuthor.SetResourceVersion(resource, 1);
        resource.Blocks.Add(AuthoredKv3.Block(editInfo, KV3IDLookup.Get("generic"), BlockType.RED2, resource));
        resource.Blocks.Add(ResourceBuilder.RawBlock(BlockType.DATA, data, resource));
        var container = ValveLayout.Serialize(resource);
        var file = new byte[container.Length + stored.Length];
        container.CopyTo(file, 0);
        stored.CopyTo(file, container.Length);
        return file;
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

    private static byte[] Encode(RcMipChain.Level image, ImageFormat format, string? nativeFolder)
    {
        switch (format)
        {
            case ImageFormat.Bc6h:
                if (nativeFolder is null || !IspcTexcomp.Load(nativeFolder))
                    throw new InvalidOperationException("BC6H needs ispc_texcomp.dll (the CS2 install's game/bin/win64).");
                var half = new Half[image.Rgba.Length];
                for (var i = 0; i < half.Length; i++)
                    half[i] = (Half)image.Rgba[i];
                return IspcTexcomp.CompressBc6h(half, image.Width, image.Height, IspcTexcomp.Bc6hProfile(3));
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
    private static byte[] Data(int width, int height, int depth, ushort flags, byte format, float[] reflectivity, int? storedSize, bool lz4)
    {
        const int header = 40;
        var extras = storedSize is null ? 0 : 1;
        var d = new byte[header + extras * (12 + 12 + 4)];
        var s = d.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(s, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], flags);
        for (var c = 0; c < 4; c++)
            BinaryPrimitives.WriteSingleLittleEndian(s[(4 + 4 * c)..], reflectivity[c]);
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(s[22..], (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(s[24..], (ushort)depth);
        d[26] = format;
        d[27] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(s[32..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(s[36..], (uint)extras);
        if (storedSize is { } size)
        {
            // Entry {type, offset from its field, size}, then the payload
            // {method, offset to the sizes from its field, count}, then the size.
            BinaryPrimitives.WriteUInt32LittleEndian(s[40..], ExtraCompressedMipSize);
            BinaryPrimitives.WriteUInt32LittleEndian(s[44..], 52 - 44);
            BinaryPrimitives.WriteUInt32LittleEndian(s[48..], 12);
            BinaryPrimitives.WriteUInt32LittleEndian(s[52..], lz4 ? 1u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(s[56..], 64 - 56);
            BinaryPrimitives.WriteUInt32LittleEndian(s[60..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(s[64..], (uint)size);
        }
        return d;
    }

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
    public static KVObject EditInfo(TextureArgument argument, string searchPath, Fingerprints fingerprints)
    {
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
        args.Add(Argument("fast", "FloatArg", 0));
        args.Add(Argument("final", argument.FinalType, 0));
        args.Add(Argument("hueShiftFixup", "FloatArg", 0));
        args.Add(Argument("lightmapMaxResolution", "FloatArg", 0));
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
        foreach (var dep in Source2ContainerAuthor.TextureSpecialDeps)
        {
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
