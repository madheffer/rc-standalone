using System.Buffers.Binary;
using SkiaSharp;
using Source2.Compiler.Compiling;
using Source2.Compiler.Compression;
using Source2.Compiler.Dmx;

namespace Source2.Compiler.RcTexture;

/// <summary>
/// <c>.vtex</c> sources to <c>.vtex_c</c>, as resourcecompiler writes them (docs/TEXTURES.md):
/// the float mip chain, the block encoders, reflectivity, the fallback, the extra data, the mip
/// compression rule and the RED2 of a texture compile. Settings it has not been measured on are
/// refused with an error rather than guessed.
/// </summary>
public sealed class VtexCompiler : IResourceCompiler
{
    public IReadOnlyCollection<string> SourceExtensions { get; } = [".vtex"];

    public string OutputPathFor(string sourcePath) => sourcePath + "_c";

    // The format byte of the vtex header, by m_outputFormat.
    private static readonly Dictionary<string, byte> FormatCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DXT1"] = 1, ["DXT5"] = 2, ["RGBA8888"] = 4, ["BC7"] = 20, ["ATI2N"] = 21, ["ATI1N"] = 27,
    };

    private const ushort NoLod = 0x8;
    private const uint ExtraFallbackBits = 1, ExtraCompressedMipSize = 4;

    // gameinfo.gi ResourceCompiler/TextureCompiler: Compression "lz4", CompressMipsOnDisk 1,
    // CompressMinRatio 95. RC as a console app compiles at encode quality 3, LZ4 HC level 10.
    private const int CompressMinRatio = 95, Lz4Level = 10;

    public CompileOutput Compile(CompileRequest request, ICollection<CompileDiagnostic> diagnostics)
    {
        var vtex = Keyvalues2Text.Parse(request.Source);
        var settings = VtexSettings.Read(vtex, request.SourcePath);

        var content = request.Content ?? throw new InvalidOperationException(
            $"{request.SourcePath} refers to {settings.ImagePath}; compile it with a content source to read that from.");
        var imageBytes = content.Read(settings.ImagePath)
            ?? throw new InvalidOperationException($"{settings.ImagePath} (named by {request.SourcePath}) was not found.");
        if (content.Read(Path.ChangeExtension(settings.ImagePath, ".txt")) is not null)
            throw new InvalidOperationException(
                $"{Path.ChangeExtension(settings.ImagePath, ".txt")} exists; RC reads it as image settings, which this compiler has not measured yet.");

        var (rgba, width, height) = DecodeRgba(imageBytes, settings.ImagePath);
        var format = settings.Format;
        // "Found DXT5 texture with solid 255 alpha! Saving as a DXT1!"
        if (format == "DXT5" && AlphaIsSolid(rgba))
            format = "DXT1";
        var options = request.Settings as VtexCompileSettings ?? new VtexCompileSettings();
        if (format == "BC7" && Bc7e.Unavailable(options.Bc7Vendor) is { } why)
            throw new InvalidOperationException(why);

        bool blockCompressed = format != "RGBA8888";
        var levels = RcMipChain.Build(rgba, width, height, settings.Srgb);
        int mips = 1;
        if (blockCompressed)
            while (mips < levels.Count && Math.Min(levels[mips].Width, levels[mips].Height) >= 4)
                mips++;
        else
            mips = levels.Count;

        var mipData = new byte[mips][];
        for (int i = 0; i < mips; i++)
            mipData[i] = EncodeLevel(levels[i], format, settings.Srgb, options.Bc7Vendor);

        ushort flags = 0;
        bool pow2 = (width & (width - 1)) == 0 && (height & (height - 1)) == 0;
        if (settings.NoLod || (blockCompressed && !pow2))
            flags |= NoLod;

        var fallback = AtiCompress.Compress(ToBgra(RcMipChain.FallbackRgba8(levels, settings.Srgb, clamp: false)), 32, 32,
            AtiCompress.Format.Dxt5, perceptual: settings.Srgb);

        // COMPRESSED_MIP_SIZE when the largest side is at least 33. Each mip, smallest first, is
        // LZ4 HC'd and kept compressed only below 95% of its size and more than 32 KB smaller.
        bool mipSizes = Math.Max(width, height) >= 33;
        var stored = new byte[mips][];
        bool anyCompressed = false;
        for (int i = mips - 1; i >= 0; i--)
        {
            stored[i] = mipData[i];
            if (!mipSizes)
                continue;
            var raw = mipData[i];
            var packed = new byte[Lz4HcStream.CompressBound(raw.Length)];
            int c = Lz4HcStream.Compress(raw, packed, Lz4Level);
            bool keepRaw = c == 0 || (long)raw.Length * CompressMinRatio <= (long)c * 100 || raw.Length - 0x8000 <= c;
            if (!keepRaw)
            {
                stored[i] = packed[..c];
                anyCompressed = true;
            }
        }
        if (anyCompressed)
            diagnostics.Add(new(DiagnosticSeverity.Warning,
                "A mip compressed on disk; the COMPRESSED_MIP_SIZE method word for that case is not measured yet.",
                request.SourcePath));

        var reflectivity = levels[^1].Rgba.AsSpan(0, 4).ToArray();
        var data = BuildData(width, height, FormatCodes[format], mips, flags, reflectivity, fallback,
            mipSizes ? stored.Select(s => s.Length).ToArray() : null, anyCompressed ? 1u : 0u);

        var specialDeps = Source2ContainerAuthor.TextureCompileDeps;
        var red2 = Source2ContainerAuthor.BuildTextureSourceEditInfo(request.SourcePath, request.Source,
            [(settings.ImagePath, imageBytes)], specialDeps, request.SearchPath);
        var container = new CompiledResource(new ResourceFile { Version = Source2ContainerAuthor.TextureResourceVersion });
        container.SetKv3("RED2", red2, Kv3Formats.Get("generic"), insertAt: 0);
        container.SetRaw("DATA", data);
        var head = container.Write();

        using var ms = new MemoryStream();
        ms.Write(head);
        for (int i = mips - 1; i >= 0; i--)
            ms.Write(stored[i]);
        return new(ms.ToArray());
    }

    private static byte[] EncodeLevel(RcMipChain.Level level, string format, bool srgb, Bc7Vendor vendor)
    {
        if (format == "BC7")
            return Bc7e.EncodeBlocks(Bc7Blocks(level, srgb), perceptual: srgb, vendor);
        var rgba8 = RcMipChain.WriteRgba8(level, srgb);
        return format switch
        {
            "RGBA8888" => rgba8,
            "DXT1" => AtiCompress.Compress(ToBgra(rgba8), level.Width, level.Height, AtiCompress.Format.Dxt1, srgb),
            "DXT5" => AtiCompress.Compress(ToBgra(rgba8), level.Width, level.Height, AtiCompress.Format.Dxt5, srgb),
            "ATI1N" => AtiCompress.Compress(ToBgra(rgba8), level.Width, level.Height, AtiCompress.Format.Ati1n, false),
            "ATI2N" => AtiCompress.Compress(ToBgra(rgba8), level.Width, level.Height, AtiCompress.Format.Ati2n, false),
            _ => throw new InvalidOperationException($"Output format {format} is not compiled here yet."),
        };
    }

    // bc7e's input: 4x4 blocks in block order, each channel ROUND(clamp(x)*255) to nearest even
    // (sRGB channels encoded first), a block past the edge repeating the last row and column.
    private static byte[] Bc7Blocks(RcMipChain.Level level, bool srgb)
    {
        int bw = (level.Width + 3) / 4, bh = (level.Height + 3) / 4;
        var o = new byte[bw * bh * 64];
        int k = 0;
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int px = Math.Min(bx * 4 + x, level.Width - 1), py = Math.Min(by * 4 + y, level.Height - 1);
                        int src = (py * level.Width + px) * 4;
                        for (int c = 0; c < 4; c++)
                        {
                            float v = level.Rgba[src + c];
                            if (srgb && c < 3) v = RcMipChain.Encode(v);
                            o[k++] = (byte)MathF.Round(MathF.Min(1f, MathF.Max(0f, v)) * 255f, MidpointRounding.ToEven);
                        }
                    }
        return o;
    }

    // DATA: the 40-byte header, the extra-data table, the payloads, then the per-mip sizes that
    // COMPRESSED_MIP_SIZE points at. Offsets in the table count from the field that holds them.
    private static byte[] BuildData(int width, int height, byte format, int mips, ushort flags, float[] reflectivity,
        byte[] fallback, int[]? mipSizes, uint method)
    {
        var extras = new List<(uint Type, byte[] Payload)> { (ExtraFallbackBits, fallback) };
        if (mipSizes is not null)
            extras.Add((ExtraCompressedMipSize, new byte[12]));
        const int header = 40;
        var positions = new int[extras.Count];
        int cursor = header + extras.Count * 12;
        for (int i = 0; i < extras.Count; i++) { positions[i] = cursor; cursor += extras[i].Payload.Length; }
        int sizesPos = cursor;
        int total = sizesPos + (mipSizes?.Length ?? 0) * 4;
        if (mipSizes is not null)
        {
            var cms = extras[^1].Payload;
            BinaryPrimitives.WriteUInt32LittleEndian(cms, method);
            BinaryPrimitives.WriteUInt32LittleEndian(cms.AsSpan(4), (uint)(sizesPos - (positions[^1] + 4)));
            BinaryPrimitives.WriteUInt32LittleEndian(cms.AsSpan(8), (uint)mipSizes.Length);
        }

        var d = new byte[total];
        var s = d.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(s, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], flags);
        for (int c = 0; c < 4; c++)
            BinaryPrimitives.WriteSingleLittleEndian(s[(4 + 4 * c)..], reflectivity[c]);
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(s[22..], (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(s[24..], 1);
        d[26] = format;
        d[27] = (byte)mips;
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[32..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(s[36..], (uint)extras.Count);
        for (int i = 0; i < extras.Count; i++)
        {
            int field = header + i * 12 + 4;
            BinaryPrimitives.WriteUInt32LittleEndian(s[(header + i * 12)..], extras[i].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(s[field..], (uint)(positions[i] - field));
            BinaryPrimitives.WriteUInt32LittleEndian(s[(field + 4)..], (uint)extras[i].Payload.Length);
            extras[i].Payload.CopyTo(s[positions[i]..]);
        }
        if (mipSizes is not null)
            for (int i = 0; i < mipSizes.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(s[(sizesPos + 4 * i)..], (uint)mipSizes[i]);
        return d;
    }

    private static bool AlphaIsSolid(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] != 255)
                return false;
        return true;
    }

    internal static byte[] ToBgra(byte[] rgba)
    {
        var bgra = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = rgba[i + 3];
        }
        return bgra;
    }

    private static (byte[] Rgba, int Width, int Height) DecodeRgba(byte[] bytes, string path)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException($"{path} is not an image this compiler reads.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        if ((long)info.Width * info.Height > Imaging.SafeImageDecode.DefaultMaxPixels)
            throw new InvalidDataException($"{path} is {info.Width}x{info.Height}, over the decode limit.");
        var result = codec.GetPixels(info, out var pixels);
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new InvalidDataException($"{path} did not decode ({result}).");
        return (pixels, info.Width, info.Height);
    }
}

/// <summary>Options for <see cref="VtexCompiler"/> beyond the <c>.vtex</c> itself.</summary>
/// <param name="Bc7Vendor">Whose <c>rcpps</c> BC7 reproduces: resourcecompiler's BC7 differs
/// between AMD and Intel CPUs, and this picks which one the output matches on any machine.</param>
public sealed record VtexCompileSettings(Bc7Vendor Bc7Vendor = Bc7Vendor.Amd);

/// <summary>The part of a <c>.vtex</c> this compiler implements; anything else is refused.</summary>
internal sealed record VtexSettings(string ImagePath, string Format, bool Srgb, bool NoLod)
{
    private static readonly HashSet<string> KnownRoot = new(StringComparer.OrdinalIgnoreCase)
        { "m_inputTextureArray", "m_outputTypeString", "m_outputFormat", "m_textureOutputChannelArray", "m_bNoLod" };

    public static VtexSettings Read(DmxElement vtex, string sourcePath)
    {
        if (vtex.Type != "CDmeVtex")
            throw new InvalidOperationException($"{sourcePath} is a {vtex.Type}, not a CDmeVtex.");
        foreach (var (name, _) in vtex.Attributes)
            if (!KnownRoot.Contains(name))
                throw new InvalidOperationException($"{sourcePath}: setting '{name}' is not compiled here yet.");

        var inputs = vtex.Elements("m_inputTextureArray");
        if (inputs.Count != 1)
            throw new InvalidOperationException($"{sourcePath}: {inputs.Count} input textures; one is compiled here so far.");
        var input = inputs[0];
        var file = input.String("m_fileName") ?? throw new InvalidOperationException($"{sourcePath}: the input has no m_fileName.");
        if ((input.String("m_typeString") ?? "2D") != "2D" || (vtex.String("m_outputTypeString") ?? "2D") != "2D")
            throw new InvalidOperationException($"{sourcePath}: only 2D textures are compiled here so far.");

        var channels = vtex.Elements("m_textureOutputChannelArray");
        if (channels.Count != 1)
            throw new InvalidOperationException($"{sourcePath}: {channels.Count} output channels; one rgba channel is compiled here so far.");
        var ch = channels[0];
        if (ch.String("m_srcChannels") != "rgba" || ch.String("m_dstChannels") != "rgba")
            throw new InvalidOperationException($"{sourcePath}: channel mapping {ch.String("m_srcChannels")} -> {ch.String("m_dstChannels")} is not compiled here yet.");
        var mip = ch["m_mipAlgorithm"]?.Element;
        if (mip is not null && (mip.String("m_algorithm") ?? "Box") != "Box")
            throw new InvalidOperationException($"{sourcePath}: mip algorithm {mip.String("m_algorithm")} is not compiled here yet.");

        var inSpace = input.String("m_colorSpace") ?? "srgb";
        var outSpace = ch.String("m_outputColorSpace") ?? inSpace;
        if (!inSpace.Equals(outSpace, StringComparison.OrdinalIgnoreCase) || inSpace is not ("srgb" or "linear"))
            throw new InvalidOperationException($"{sourcePath}: colour space {inSpace} -> {outSpace} is not compiled here yet.");

        var format = vtex.String("m_outputFormat") ?? "DXT5";
        if (!new[] { "RGBA8888", "DXT1", "DXT5", "ATI1N", "ATI2N", "BC7" }.Contains(format, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{sourcePath}: output format {format} is not compiled here yet.");

        return new(file.Replace('\\', '/'), format.ToUpperInvariant(), inSpace == "srgb", vtex.Bool("m_bNoLod"));
    }
}
