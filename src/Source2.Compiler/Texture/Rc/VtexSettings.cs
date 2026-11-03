using Source2.Compiler.Dmx;
using Source2.Compiler.Materials;

namespace Source2.Compiler.RcTexture;

/// <summary>
/// The part of a <c>.vtex</c> (KeyValues2 <c>CDmeVtex</c>) this compiler implements: its inputs
/// (<c>CDmeInputTexture</c>, each with at most one image processor) and output channel groups
/// (<c>CDmeTextureOutputChannel</c>), held as a material's generated texture holds them so both
/// compile through <see cref="RcTextureChannels"/>. Anything else is refused with the setting named.
/// </summary>
/// <param name="Texture">Inputs and output channel groups.</param>
/// <param name="Format">The output image format.</param>
/// <param name="NoLod"><c>m_bNoLod</c>.</param>
internal sealed record VtexSettings(GeneratedTexture Texture, string Format, bool NoLod)
{
    private static readonly HashSet<string> KnownRoot = new(StringComparer.OrdinalIgnoreCase)
        { "m_inputTextureArray", "m_outputTypeString", "m_outputFormat", "m_textureOutputChannelArray", "m_bNoLod" };
    private static readonly HashSet<string> KnownInput = new(StringComparer.OrdinalIgnoreCase)
        { "m_name", "m_fileName", "m_colorSpace", "m_typeString", "m_imageProcessorArray" };
    private static readonly HashSet<string> KnownChannel = new(StringComparer.OrdinalIgnoreCase)
        { "m_inputTextureArray", "m_srcChannels", "m_dstChannels", "m_mipAlgorithm", "m_outputColorSpace" };
    private static readonly HashSet<string> KnownProcessor = new(StringComparer.OrdinalIgnoreCase)
        { "m_algorithm", "m_stringArg", "m_vFloat4Arg" };

    private static readonly string[] Formats = ["RGBA8888", "DXT1", "DXT5", "ATI1N", "ATI2N", "BC7"];

    /// <summary>One input and one rgba channel group with the Box chain and no image processor:
    /// the form a caller's own mip chain (<see cref="VtexCompileSettings.MipChainBgra"/>) stands in for.</summary>
    public bool IsSingleBox => Texture.Inputs.Count == 1 && Texture.Channels.Count == 1
        && Texture.Channels[0] is { SrcChannels: "rgba", DstChannels: "rgba" } c
        && c.MipAlgorithm.Equals("Box", StringComparison.OrdinalIgnoreCase)
        && Texture.Inputs[0].ImageProcessor.Length == 0 && c.Srgb == Texture.Inputs[0].Srgb;

    public static VtexSettings Read(DmxElement vtex, string sourcePath)
    {
        if (vtex.Type != "CDmeVtex")
            throw new InvalidOperationException($"{sourcePath} is a {vtex.Type}, not a CDmeVtex.");
        Known(vtex, KnownRoot, sourcePath);
        if ((vtex.String("m_outputTypeString") ?? "2D") != "2D")
            throw new InvalidOperationException($"{sourcePath}: only 2D textures are compiled here so far.");

        var elements = vtex.Elements("m_inputTextureArray");
        if (elements.Count == 0)
            throw new InvalidOperationException($"{sourcePath} has no input textures.");
        var inputs = new List<GeneratedTextureInput>();
        var names = new List<string>();
        foreach (var input in elements)
        {
            Known(input, KnownInput, sourcePath);
            var file = input.String("m_fileName") ?? throw new InvalidOperationException($"{sourcePath}: an input has no m_fileName.");
            if ((input.String("m_typeString") ?? "2D") != "2D")
                throw new InvalidOperationException($"{sourcePath}: only 2D textures are compiled here so far.");
            var space = input.String("m_colorSpace") ?? "srgb";
            if (space is not ("srgb" or "linear"))
                throw new InvalidOperationException($"{sourcePath}: input colour space {space} is not compiled here yet.");
            var processors = input.Elements("m_imageProcessorArray");
            if (processors.Count > 1)
                throw new InvalidOperationException($"{sourcePath}: {processors.Count} image processors on one input; one is compiled here so far.");
            var processor = processors.Count == 1 ? Algorithm(processors[0], sourcePath) : "";
            inputs.Add(new(file.Replace('\\', '/'), space == "srgb", "", -1, processor));
            names.Add(input.String("m_name") ?? "");
        }

        var channels = new List<GeneratedTextureChannel>();
        foreach (var ch in vtex.Elements("m_textureOutputChannelArray"))
        {
            Known(ch, KnownChannel, sourcePath);
            string src = ch.String("m_srcChannels") ?? "", dst = ch.String("m_dstChannels") ?? "";
            if (src.Length == 0 || src.Length != dst.Length || src.Any(c => !"rgba".Contains(c)) || dst.Any(c => !"rgba".Contains(c))
                || dst.Distinct().Count() != dst.Length)
                throw new InvalidOperationException($"{sourcePath}: channel mapping {src} -> {dst} is not compiled here.");
            // A channel names its inputs; a name finds the first input that has it (RC's children
            // read their inputs the same way, docs/MATERIALS.md).
            var refs = (ch["m_inputTextureArray"]?.TextList ?? []).Select(n =>
            {
                int i = names.IndexOf(n);
                return i >= 0 ? i : throw new InvalidOperationException($"{sourcePath}: a channel reads input '{n}', which the .vtex does not have.");
            }).ToArray();
            if (refs.Length == 0)
                throw new InvalidOperationException($"{sourcePath}: an output channel reads no input.");
            var mip = ch["m_mipAlgorithm"]?.Element;
            var algorithm = mip is null ? "Box" : Algorithm(mip, sourcePath);
            var outSpace = ch.String("m_outputColorSpace") ?? (inputs[refs[0]].Srgb ? "srgb" : "linear");
            if (outSpace is not ("srgb" or "linear"))
                throw new InvalidOperationException($"{sourcePath}: output colour space {outSpace} is not compiled here yet.");
            channels.Add(new(src, dst, algorithm, outSpace == "srgb", refs));
        }
        if (channels.Count == 0)
            throw new InvalidOperationException($"{sourcePath} has no output channels.");
        if (channels.SelectMany(c => c.DstChannels).GroupBy(c => c).Any(g => g.Count() > 1))
            throw new InvalidOperationException($"{sourcePath}: two output channels write the same destination; not measured.");

        var format = (vtex.String("m_outputFormat") ?? "DXT5").ToUpperInvariant();
        if (!Formats.Contains(format) && !RcFormats.Handles(format))
            throw new InvalidOperationException($"{sourcePath}: output format {format} is not compiled here yet.");
        return new(new(inputs, format, channels), format, vtex.Bool("m_bNoLod"));
    }

    private static void Known(DmxElement element, HashSet<string> known, string sourcePath)
    {
        foreach (var (name, _) in element.Attributes)
            if (!known.Contains(name))
                throw new InvalidOperationException($"{sourcePath}: setting '{name}' is not compiled here yet.");
    }

    // A CDmeImageProcessor: its algorithm; arguments other than the defaults are not measured.
    private static string Algorithm(DmxElement processor, string sourcePath)
    {
        Known(processor, KnownProcessor, sourcePath);
        var name = processor.String("m_algorithm") ?? "";
        bool zeroVector = (processor.String("m_vFloat4Arg") ?? "0 0 0 0")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).All(v => v == "0");
        if ((processor.String("m_stringArg") ?? "").Length > 0 || !zeroVector)
            throw new InvalidOperationException($"{sourcePath}: arguments to {name} are not compiled here yet.");
        return name;
    }
}
