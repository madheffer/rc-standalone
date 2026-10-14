// The texture DATA layout, decode rules and codec detection follow ValveResourceFormat's Texture and
// TextureDecoders (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat), checked pixel for
// pixel against it over every pak01 texture (docs/TEXTURES.md, "Decoding").

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SkiaSharp;
using Source2.Compiler.Imaging;
using Source2.Compiler.Textures.Decoding;

namespace Source2.Compiler.Textures;

/// <summary>The pixel format of a compiled texture (<c>VTEX</c> DATA byte 26).</summary>
public enum VTexFormat : byte
{
#pragma warning disable CS1591 // the format names are the documentation
    UNKNOWN = 0,
    DXT1 = 1,
    DXT5 = 2,
    I8 = 3,
    RGBA8888 = 4,
    R16 = 5,
    RG1616 = 6,
    RGBA16161616 = 7,
    R16F = 8,
    RG1616F = 9,
    RGBA16161616F = 10,
    R32F = 11,
    RG3232F = 12,
    RGB323232F = 13,
    RGBA32323232F = 14,
    JPEG_RGBA8888 = 15,
    PNG_RGBA8888 = 16,
    JPEG_DXT5 = 17,
    PNG_DXT5 = 18,
    BC6H = 19,
    BC7 = 20,
    ATI2N = 21,
    IA88 = 22,
    ETC2 = 23,
    ETC2_EAC = 24,
    R11_EAC = 25,
    RG11_EAC = 26,
    ATI1N = 27,
    BGRA8888 = 28,
    WEBP_RGBA8888 = 29,
    WEBP_DXT5 = 30,
#pragma warning restore CS1591
}

/// <summary>A compiled texture's flags (<c>VTEX</c> DATA bytes 2-3).</summary>
[Flags]
public enum VTexFlags : ushort
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Clamp S.</summary>
    SUGGEST_CLAMPS = 1 << 0,

    /// <summary>Clamp T.</summary>
    SUGGEST_CLAMPT = 1 << 1,

    /// <summary>Clamp U.</summary>
    SUGGEST_CLAMPU = 1 << 2,

    /// <summary>No LOD.</summary>
    NO_LOD = 1 << 3,

    /// <summary>Six faces per depth slice.</summary>
    CUBE_TEXTURE = 1 << 4,

    /// <summary>Depth is a third dimension that mips.</summary>
    VOLUME_TEXTURE = 1 << 5,

    /// <summary>Depth is an array of slices.</summary>
    TEXTURE_ARRAY = 1 << 6,

    /// <summary>Panorama: dilate colour.</summary>
    PANORAMA_DILATE_COLOR = 1 << 7,

    /// <summary>Panorama: convert to YCoCg DXT5.</summary>
    PANORAMA_CONVERT_TO_YCOCG_DXT5 = 1 << 8,

    /// <summary>Create a linear API texture.</summary>
    CREATE_LINEAR_API_TEXTURE = 1 << 9,
}

/// <summary>The kinds of extra data a compiled texture may carry.</summary>
public enum VTexExtraData : uint
{
#pragma warning disable CS1591
    UNKNOWN = 0,
    FALLBACK_BITS = 1,
    SHEET = 2,
    METADATA = 3,
    COMPRESSED_MIP_SIZE = 4,
    CUBEMAP_RADIANCE_SH = 5,
#pragma warning restore CS1591
}

/// <summary>A cube map face.</summary>
public enum CubemapFace
{
#pragma warning disable CS1591
    PositiveX,
    NegativeX,
    PositiveY,
    NegativeY,
    PositiveZ,
    NegativeZ,
#pragma warning restore CS1591
}

/// <summary>How a texture's stored pixels decode to colours; <see cref="Auto"/> reads it from the edit info.</summary>
[Flags]
public enum TextureCodec
{
    /// <summary>Pixels are colours.</summary>
    None = 0,

    /// <summary>YCoCg in RGB with Y in alpha (panoramas, skyboxes).</summary>
    YCoCg = 1 << 0,

    /// <summary>RGBM (not decoded yet).</summary>
    RGBM = 1 << 1,

    /// <summary>A hemi-octahedral normal in RG, roughness in B.</summary>
    HemiOctRB = 1 << 2,

    /// <summary>A normal in RG; B is reconstructed.</summary>
    NormalizeNormals = 1 << 3,

    /// <summary>DXT5 normal: X in alpha.</summary>
    Dxt5nm = 1 << 4,

    /// <summary>Stored linear.</summary>
    ColorSpaceLinear = 1 << 5,

    /// <summary>Stored sRGB.</summary>
    ColorSpaceSrgb = 1 << 6,

    /// <summary>Decode a high dynamic range format to 8 bits.</summary>
    ForceLDR = 1 << 7,

    /// <summary>Detect from the texture's edit info.</summary>
    Auto = 1 << 30,
}

/// <summary>
/// A compiled texture (<c>.vtex_c</c>) read without ValveResourceFormat: its DATA header and extra data,
/// its mips (smallest first after the resource, each optionally LZ4 compressed) and their pixels.
/// <see cref="GenerateBitmap"/> returns what VRF's <c>Texture.GenerateBitmap</c> returns, pixel for
/// pixel, for every format but the ETC2/EAC family, which throws <see cref="NotSupportedException"/>.
/// </summary>
public sealed class CompiledTexture
{
    /// <summary>The largest width, height or depth accepted.</summary>
    public const int MaxDimension = 16384;

    /// <summary>The most texels (width x height x depth) accepted.</summary>
    public const long MaxTexels = 268_435_456;

    private readonly byte[] _file;
    private readonly int _dataEnd;
    private readonly bool _mipsCompressed;
    private readonly Dictionary<VTexExtraData, byte[]> _extraData = [];

    /// <summary>The resource, for its edit info and anything else this view does not cover.</summary>
    public CompiledResource Resource { get; }

    /// <summary>The DATA version (always 1).</summary>
    public ushort Version { get; }

    /// <summary>The flags.</summary>
    public VTexFlags Flags { get; }

    /// <summary>The reflectivity (average colour) the compiler stored.</summary>
    public Vector4 Reflectivity { get; }

    /// <summary>The stored width, a multiple of the block size for block formats.</summary>
    public ushort Width { get; }

    /// <summary>The stored height.</summary>
    public ushort Height { get; }

    /// <summary>The depth: slices of an array or volume (1 for a plain texture), per face for a cube.</summary>
    public ushort Depth { get; }

    /// <summary>The pixel format.</summary>
    public VTexFormat Format { get; }

    /// <summary>The number of mips.</summary>
    public byte NumMipLevels { get; }

    /// <summary>The picmip 0 resolution.</summary>
    public uint Picmip0Res { get; }

    /// <summary>The extra data blocks by kind, as stored.</summary>
    public IReadOnlyDictionary<VTexExtraData, byte[]> ExtraData => _extraData;

    /// <summary>The display rect width from METADATA (0 when absent or larger than the texture).</summary>
    public ushort DisplayRectWidth { get; }

    /// <summary>The display rect height from METADATA.</summary>
    public ushort DisplayRectHeight { get; }

    /// <summary>The motion vectors' maximum distance from METADATA.</summary>
    public short MotionVectorsMaxDistance { get; }

    /// <summary>The per-channel minimum from METADATA.</summary>
    public Vector4 RangeMin { get; }

    /// <summary>The per-channel maximum from METADATA.</summary>
    public Vector4 RangeMax { get; }

    /// <summary>Each mip's stored size when the texture lists them (COMPRESSED_MIP_SIZE), by mip.</summary>
    public IReadOnlyList<int>? CompressedMips { get; }

    /// <summary>A cube map's baked radiance as spherical harmonic coefficients (CUBEMAP_RADIANCE_SH).</summary>
    public IReadOnlyList<float>? RadianceCoefficients { get; }

    /// <summary>
    /// The width pixels decode at: the display rect's when it is set, unless it is 1 on a texture that
    /// is not 4 wide (VRF's rule for the 1x1 rects some textures carry).
    /// </summary>
    public ushort ActualWidth => DisplayRectWidth > 0 && (DisplayRectWidth != 1 || Width == 4) ? DisplayRectWidth : Width;

    /// <summary>The height pixels decode at; see <see cref="ActualWidth"/>.</summary>
    public ushort ActualHeight => DisplayRectHeight > 0 && (DisplayRectHeight != 1 || Height == 4) ? DisplayRectHeight : Height;

    /// <summary>The format holds more than 8 bits a channel, so pixels decode to floats.</summary>
    public bool IsHighDynamicRange => Format is VTexFormat.R16 or VTexFormat.RG1616 or VTexFormat.RGBA16161616
        or VTexFormat.R16F or VTexFormat.RG1616F or VTexFormat.RGBA16161616F or VTexFormat.R32F
        or VTexFormat.RG3232F or VTexFormat.RGB323232F or VTexFormat.RGBA32323232F or VTexFormat.BC6H;

    /// <summary>The format stores 4x4 blocks.</summary>
    public bool IsBlockCompressed => Format is VTexFormat.DXT1 or VTexFormat.DXT5 or VTexFormat.BC6H or VTexFormat.BC7
        or VTexFormat.ETC2 or VTexFormat.ETC2_EAC or VTexFormat.R11_EAC or VTexFormat.RG11_EAC
        or VTexFormat.ATI1N or VTexFormat.ATI2N;

    /// <summary>The texture is one encoded JPEG, PNG or WebP image rather than mips.</summary>
    public bool IsRawImage => Format is VTexFormat.JPEG_DXT5 or VTexFormat.JPEG_RGBA8888 or VTexFormat.PNG_DXT5
        or VTexFormat.PNG_RGBA8888 or VTexFormat.WEBP_DXT5 or VTexFormat.WEBP_RGBA8888;

    /// <summary>Bytes per pixel, or per 4x4 block for a block format.</summary>
    public int BlockSize => Format switch
    {
        VTexFormat.DXT1 or VTexFormat.ATI1N or VTexFormat.ETC2 or VTexFormat.R11_EAC => 8,
        VTexFormat.DXT5 or VTexFormat.BC6H or VTexFormat.BC7 or VTexFormat.ATI2N or VTexFormat.ETC2_EAC or VTexFormat.RG11_EAC => 16,
        VTexFormat.RGBA8888 or VTexFormat.BGRA8888 or VTexFormat.RG1616 or VTexFormat.RG1616F or VTexFormat.R32F => 4,
        VTexFormat.R16 or VTexFormat.R16F or VTexFormat.IA88 => 2,
        VTexFormat.RGBA16161616 or VTexFormat.RGBA16161616F or VTexFormat.RG3232F => 8,
        VTexFormat.RGB323232F => 12,
        VTexFormat.RGBA32323232F => 16,
        _ => 1,
    };

    private CompiledTexture(byte[] file, CompiledResource resource)
    {
        _file = file;
        Resource = resource;
        var (dataStart, dataSize) = DataBlock(file);
        _dataEnd = dataStart + dataSize;
        var data = file.AsSpan(dataStart, dataSize);
        if (data.Length < 40)
            throw new InvalidDataException($"A texture's DATA holds a 40-byte header; this one has {data.Length} bytes.");

        Version = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if (Version != 1)
            throw new InvalidDataException($"Texture DATA version {Version}; only 1 is known.");
        Flags = (VTexFlags)BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        Reflectivity = Vector4(data[4..]);
        Width = BinaryPrimitives.ReadUInt16LittleEndian(data[20..]);
        Height = BinaryPrimitives.ReadUInt16LittleEndian(data[22..]);
        Depth = BinaryPrimitives.ReadUInt16LittleEndian(data[24..]);
        Format = (VTexFormat)data[26];
        NumMipLevels = data[27];
        Picmip0Res = BinaryPrimitives.ReadUInt32LittleEndian(data[28..]);
        if (Width > MaxDimension || Height > MaxDimension || Depth > MaxDimension || (long)Width * Height * Depth > MaxTexels)
            throw new InvalidDataException($"Texture dimensions {Width}x{Height}x{Depth} are out of range.");

        var extraOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[32..]);
        var extraCount = BinaryPrimitives.ReadUInt32LittleEndian(data[36..]);
        // Offsets are relative to their own field, and like VRF this reads them against the file.
        var fileFromData = file.AsSpan(dataStart);
        if (extraCount > 0 && 32L + extraOffset + 12L * extraCount > fileFromData.Length)
            throw new InvalidDataException($"{extraCount} extra data entries at {extraOffset} do not fit.");
        for (var i = 0; i < extraCount; i++)
        {
            var entry = (int)(32 + extraOffset + 12 * i);
            var type = (VTexExtraData)BinaryPrimitives.ReadUInt32LittleEndian(fileFromData[entry..]);
            var start = entry + 4L + BinaryPrimitives.ReadUInt32LittleEndian(fileFromData[(entry + 4)..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(fileFromData[(entry + 8)..]);
            if (start + size > fileFromData.Length)
                throw new InvalidDataException($"Extra data {type} at {start} ({size} bytes) runs past the file.");
            var bytes = fileFromData.Slice((int)start, (int)size);
            _extraData[type] = bytes.ToArray();
            var tail = fileFromData[(int)start..];

            switch (type)
            {
                case VTexExtraData.METADATA:
                    Need(tail, 40, type);
                    var rectWidth = BinaryPrimitives.ReadUInt16LittleEndian(tail[2..]);
                    var rectHeight = BinaryPrimitives.ReadUInt16LittleEndian(tail[4..]);
                    MotionVectorsMaxDistance = BinaryPrimitives.ReadInt16LittleEndian(tail[6..]);
                    // The rect is authored against the source image, so it may exceed the compiled size.
                    if (rectWidth > 0 && rectHeight > 0 && Width >= rectWidth && Height >= rectHeight)
                    {
                        DisplayRectWidth = rectWidth;
                        DisplayRectHeight = rectHeight;
                    }
                    RangeMin = Vector4(tail[8..]);
                    RangeMax = Vector4(tail[24..]);
                    break;
                case VTexExtraData.COMPRESSED_MIP_SIZE:
                {
                    Need(tail, 12, type);
                    var compressed = BinaryPrimitives.ReadUInt32LittleEndian(tail);
                    if (compressed > 1)
                        throw new InvalidDataException($"COMPRESSED_MIP_SIZE's first field is {compressed}, not 0 or 1.");
                    _mipsCompressed = compressed == 1;
                    var at = 4L + BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]);
                    var count = BinaryPrimitives.ReadUInt32LittleEndian(tail[8..]);
                    if (at + 4L * count > tail.Length)
                        throw new InvalidDataException($"{count} compressed mip sizes do not fit.");
                    CompressedMips = MemoryMarshal.Cast<byte, int>(tail.Slice((int)at, (int)count * 4)).ToArray();
                    break;
                }
                case VTexExtraData.CUBEMAP_RADIANCE_SH:
                {
                    Need(tail, 8, type);
                    var at = (long)BinaryPrimitives.ReadUInt32LittleEndian(tail);
                    var count = BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]);
                    if (at + 4L * count > tail.Length)
                        throw new InvalidDataException($"{count} radiance coefficients do not fit.");
                    RadianceCoefficients = MemoryMarshal.Cast<byte, float>(tail.Slice((int)at, (int)count * 4)).ToArray();
                    break;
                }
            }
        }

        static void Need(ReadOnlySpan<byte> tail, int bytes, VTexExtraData type)
        {
            if (tail.Length < bytes)
                throw new InvalidDataException($"Extra data {type} needs {bytes} bytes.");
        }
    }

    private static Vector4 Vector4(ReadOnlySpan<byte> bytes) => new(
        BinaryPrimitives.ReadSingleLittleEndian(bytes), BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[12..]));

    // The DATA block's start and size in the file; the mips follow it.
    private static (int Start, int Size) DataBlock(ReadOnlySpan<byte> file)
    {
        var table = 8 + BinaryPrimitives.ReadInt32LittleEndian(file[8..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(file[12..]);
        for (var i = 0; i < count; i++)
        {
            var entry = table + 12 * i;
            if (file.Slice(entry, 4).SequenceEqual("DATA"u8))
                return (entry + 4 + BinaryPrimitives.ReadInt32LittleEndian(file[(entry + 4)..]),
                    BinaryPrimitives.ReadInt32LittleEndian(file[(entry + 8)..]));
        }
        throw new InvalidDataException("The texture has no DATA block.");
    }

    /// <summary>Open a <c>.vtex_c</c>.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a well-formed texture.</exception>
    public static CompiledTexture Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new CompiledTexture(bytes, CompiledResource.Read(bytes));
    }

    private static int MipLevelSize(int size, uint level) => Math.Max(size >> (int)level, 1);

    /// <summary>A mip's width, height and depth as stored (a cube's depth counts its six faces).</summary>
    public (int Width, int Height, int Depth) MipSize(uint mipLevel)
    {
        var depth = (Flags & VTexFlags.VOLUME_TEXTURE) == 0 ? Depth : MipLevelSize(Depth, mipLevel);
        if ((Flags & VTexFlags.CUBE_TEXTURE) != 0)
            depth *= 6;
        return (MipLevelSize(Width, mipLevel), MipLevelSize(Height, mipLevel), depth);
    }

    /// <summary>The bytes a mip takes uncompressed, every face and slice included.</summary>
    public int MipBufferSize(uint mipLevel)
    {
        var (width, height, depth) = MipSize(mipLevel);
        if (!IsBlockCompressed)
            return width * height * depth * BlockSize;
        width = Math.Max((width + 3) & ~3, 4);
        height = Math.Max((height + 3) & ~3, 4);
        if (depth is > 1 and < 4)
            depth = 4;   // VRF's rule; a 2- or 3-slice block texture reads as 4
        return ((width * height) >> 4) * depth * BlockSize;
    }

    // Where a mip's stored bytes start: past the smaller mips, each at its compressed size when listed.
    private long MipOffset(uint mipLevel)
    {
        long at = _dataEnd;
        for (var j = NumMipLevels - 1; NumMipLevels >= 2 && j > mipLevel; j--)
        {
            long size = MipBufferSize((uint)j);
            if (CompressedMips is { } sizes && j < sizes.Count)
                size = Math.Min(size, sizes[j]);
            at += size;
        }
        return at;
    }

    /// <summary>
    /// One mip's bytes, decompressed: every face (for a cube) and slice, in stored order.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">There is no such mip.</exception>
    /// <exception cref="InvalidDataException">The mip runs past the file or does not decompress.</exception>
    public byte[] ReadMip(uint mipLevel)
    {
        if (IsRawImage)
            throw new InvalidOperationException($"A {Format} texture is one encoded image; read RawImageData.");
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(mipLevel, (uint)NumMipLevels);
        var output = new byte[MipBufferSize(mipLevel)];
        var at = MipOffset(mipLevel);
        var compressedSize = _mipsCompressed && CompressedMips is { } sizes && mipLevel < sizes.Count ? sizes[(int)mipLevel] : int.MaxValue;
        if (compressedSize >= output.Length)
        {
            if (at + output.Length > _file.Length)
                throw new InvalidDataException($"Mip {mipLevel} ({output.Length} bytes at {at}) runs past the file.");
            _file.AsSpan((int)at, output.Length).CopyTo(output);
            return output;
        }
        if (compressedSize < 0 || at + compressedSize > _file.Length)
            throw new InvalidDataException($"Mip {mipLevel} ({compressedSize} compressed bytes at {at}) runs past the file.");
        var written = Compression.Lz4.Decode(_file.AsSpan((int)at, compressedSize), output);
        if (written != output.Length)
            throw new InvalidDataException($"Mip {mipLevel} decompressed to {written} bytes, not {output.Length}.");
        return output;
    }

    /// <summary>The encoded image of a JPEG, PNG or WebP texture; null for any other format.</summary>
    /// <exception cref="InvalidDataException">The image's header is not its format's.</exception>
    public byte[]? RawImageData
    {
        get
        {
            if (!IsRawImage)
                return null;
            var data = _file.AsSpan(_dataEnd);
            var size = Format switch
            {
                VTexFormat.PNG_DXT5 or VTexFormat.PNG_RGBA8888 => PngSize(data),
                VTexFormat.WEBP_DXT5 or VTexFormat.WEBP_RGBA8888 => WebpSize(data),
                _ => data.Length,
            };
            return data[..(int)Math.Min(size, data.Length)].ToArray();
        }
    }

    private static long PngSize(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || !data[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            throw new InvalidDataException("The texture's image is not a PNG.");
        long size = 8;
        while (size + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[(int)size..]);
            var type = data.Slice((int)size + 4, 4);
            size += 12L + length;
            if (type.SequenceEqual("IEND"u8))
                return size;
        }
        throw new InvalidDataException("The texture's PNG has no IEND chunk.");
    }

    private static long WebpSize(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WEBP"u8))
            throw new InvalidDataException("The texture's image is not a WebP.");
        return 8L + BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
    }

    /// <summary>
    /// The codec the texture compiler applied, from the edit info's <c>CompileTexture</c> special
    /// dependencies: YCoCg (sRGB on a cube), normalized normals (as DXT5nm on DXT5), hemi-octahedral
    /// normals; linear for a high dynamic range format.
    /// </summary>
    public TextureCodec CodecFromEditInfo()
    {
        var codec = TextureCodec.None;
        if (IsRawImage)
            return codec;
        var editInfo = Resource.Has("RED2") ? Resource.Kv3("RED2") : null;
        if (editInfo is null)
            return codec;
        foreach (var dep in Source2ContainerAuthor.ReadSpecialDeps(editInfo).Where(d => d.CompilerIdentifier == "CompileTexture"))
        {
            codec |= dep.Name switch
            {
                "Texture Compiler Version Image YCoCg Conversion" => TextureCodec.YCoCg,
                "Texture Compiler Version Image NormalizeNormals" => TextureCodec.NormalizeNormals,
                "Texture Compiler Version Mip HemiOctIsoRoughness_RG_B" => TextureCodec.HemiOctRB,
                "Texture Compiler Version Mip HemiOctAnisoRoughness" => TextureCodec.HemiOctRB,
                _ => TextureCodec.None,
            };
        }
        // Skybox cubes store YCoCg in sRGB gamma; flat panoramas store it raw.
        if (codec.HasFlag(TextureCodec.YCoCg) && (Flags & VTexFlags.CUBE_TEXTURE) != 0)
            codec |= TextureCodec.ColorSpaceSrgb;
        if (Format == VTexFormat.DXT5 && codec.HasFlag(TextureCodec.NormalizeNormals))
            codec |= TextureCodec.Dxt5nm;
        else if (Format == VTexFormat.BC7 && codec.HasFlag(TextureCodec.HemiOctRB) && codec.HasFlag(TextureCodec.NormalizeNormals))
            codec &= ~TextureCodec.NormalizeNormals;
        if (IsHighDynamicRange)
            codec |= TextureCodec.ColorSpaceLinear;
        return codec;
    }

    /// <summary>
    /// Decode one face of one slice of one mip to a bitmap the caller owns: BGRA 8 bits a channel, or
    /// RGBA 32-bit float for a high dynamic range format unless <paramref name="codec"/> has
    /// <see cref="TextureCodec.ForceLDR"/>. It is <see cref="ActualWidth"/> x <see cref="ActualHeight"/>
    /// at the mip. A JPEG, PNG or WebP texture decodes its one image whatever is asked.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">There is no such mip, slice or face.</exception>
    /// <exception cref="ArgumentException">A face of a texture that is not a cube.</exception>
    /// <exception cref="NotSupportedException">An ETC2/EAC or unknown format.</exception>
    /// <exception cref="InvalidDataException">The texture's data is malformed.</exception>
    public SKBitmap GenerateBitmap(uint depth = 0, CubemapFace face = 0, uint mipLevel = 0, TextureCodec codec = TextureCodec.Auto)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(mipLevel, (uint)NumMipLevels);
        var slices = (Flags & VTexFlags.VOLUME_TEXTURE) == 0 ? Depth : MipLevelSize(Depth, mipLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(depth, (uint)slices);
        if (face > 0)
        {
            if ((Flags & VTexFlags.CUBE_TEXTURE) == 0)
                throw new ArgumentException("This is not a cube map texture.", nameof(face));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((int)face, 6, nameof(face));
        }

        if (IsRawImage)
        {
            try
            {
                return SafeImageDecode.Decode(RawImageData!);
            }
            catch (InvalidOperationException e)
            {
                throw new InvalidDataException($"The texture's {Format} image does not decode: {e.Message}", e);
            }
        }

        var hdr = IsHighDynamicRange && !codec.HasFlag(TextureCodec.ForceLDR);
        if (codec == TextureCodec.Auto)
            codec = CodecFromEditInfo() & ~(hdr ? TextureCodec.ColorSpaceLinear | TextureCodec.ColorSpaceSrgb : TextureCodec.None);

        var width = MipLevelSize(ActualWidth, mipLevel);
        var height = MipLevelSize(ActualHeight, mipLevel);
        var mip = ReadMip(mipLevel);
        var span = mip.AsSpan();
        if ((Flags & VTexFlags.CUBE_TEXTURE) != 0)
        {
            var faceSize = mip.Length / (6 * Depth);
            var offset = faceSize * ((int)depth * 6 + (int)face);
            span = span.Slice(offset, faceSize);
        }
        else if (depth > 0)
        {
            var sliceSize = mip.Length / slices;
            span = span.Slice(sliceSize * (int)depth, sliceSize);
        }

        var bitmap = new SKBitmap(width, height, hdr ? SKColorType.RgbaF32 : SKColorType.Bgra8888, SKAlphaType.Unpremul);
        try
        {
            PixelDecoders.Decode(Format, span, MipLevelSize(Width, mipLevel), MipLevelSize(Height, mipLevel), bitmap, hdr);
            if (!hdr)
                PixelDecoders.ApplyCodec(bitmap.GetPixelSpan(), codec);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}
