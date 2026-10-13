using Source2.Compiler.Shaders;

namespace Source2.Compiler.Materials;

/// <summary>An output texture of a material: generated from images, or a <c>.vtex</c> the material names.</summary>
/// <param name="Name">The shader variable (<c>g_tColor</c>).</param>
/// <param name="Generated">The texture RC generates, or null when <paramref name="ResourcePath"/> is given directly.</param>
/// <param name="ResourcePath">The <c>.vtex</c> the parameter points at.</param>
public sealed record MaterialTexture(string Name, GeneratedTexture? Generated, string ResourcePath);

/// <summary>
/// Which textures a material compiles and how (docs/MATERIALS.md): the static combo of the
/// material's feature values picks the output texture variables in the shader's pixel program,
/// and each variable's channel processors and input variables make its generated <c>.vtex</c>.
/// </summary>
public static class MaterialTextures
{
    // VfxVariableDescription m_outputTextureFormat, measured on csgo_complex.
    private static readonly Dictionary<uint, string> ImageFormats = new()
    {
        [20] = "DXT1", [24] = "ATI1N", [25] = "ATI2N", [27] = "BC7",
    };

    // The extensions RC tries for a shader's default image, in order (181f18f9f).
    private static readonly string[] DefaultImageExtensions = ["psd", "tga", "tif", "pfm", "exr"];

    /// <summary>
    /// The output textures of a material, ordered by name.
    /// </summary>
    /// <param name="features">The shader's features program (its feature combos).</param>
    /// <param name="pixel">The shader's pixel program.</param>
    /// <param name="parameters">The material's values by name (<c>TextureColor</c>, <c>F_TRANSLUCENT</c>).</param>
    /// <param name="contentExists">Whether a content path exists, for the shader's default images.</param>
    public static IReadOnlyList<MaterialTexture> Resolve(ShaderProgram features, ShaderProgram pixel,
        IReadOnlyDictionary<string, string> parameters, Func<string, bool> contentExists)
    {
        int Feature(int index)
        {
            var combo = features.StaticCombos[index];
            return parameters.TryGetValue(combo.Name, out var v) && int.TryParse(v, out var n) ? n : combo.Min;
        }

        long id = pixel.StaticComboId(Feature);
        var used = pixel.VariablesOf(id)
            ?? throw new InvalidOperationException($"The pixel shader has no static combo {id} for these features.");

        var textures = new List<MaterialTexture>();
        foreach (int index in used)
        {
            var variable = pixel.Variables[index];
            if (!variable.IsMaterialTexture || variable.OutputTextureFormat == uint.MaxValue)
                continue;
            textures.Add(Build(pixel, variable, parameters, contentExists));
        }
        return textures.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    private static MaterialTexture Build(ShaderProgram pixel, ShaderVariable output,
        IReadOnlyDictionary<string, string> parameters, Func<string, bool> contentExists)
    {
        var inputs = new List<GeneratedTextureInput>();
        var inputVariables = new List<int>();
        var channels = new List<GeneratedTextureChannel>();
        foreach (int ci in output.ChannelInfoIndex.Where(i => i >= 0))
        {
            var processor = pixel.ChannelProcessors[ci];
            var refs = new List<int>();
            foreach (int ii in processor.InputTextures.Where(i => i >= 0))
            {
                // Every channel appends its inputs, repeats included, but refers to an input by
                // name, which finds the first one (a translucent colour: inputs color, trans,
                // trans; the alpha channel reads input 1).
                refs.Add(inputVariables.Contains(ii) ? inputVariables.IndexOf(ii) : inputs.Count);
                inputVariables.Add(ii);
                var input = pixel.Variables[ii];
                var file = parameters.TryGetValue(input.Name, out var given) && given.Length > 0
                    ? given.Replace('\\', '/')
                    : DefaultImage(input, contentExists);
                if (file.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase))
                {
                    // A compiled texture named directly stands in for the whole output.
                    if (output.ChannelInfoIndex.Count(i => i >= 0) != 1 || processor.InputTextures.Count(i => i >= 0) != 1)
                        throw new InvalidOperationException(
                            $"{input.Name} names a .vtex, but {output.Name} packs several inputs; RC's handling of that is not measured.");
                    return new MaterialTexture(output.Name, null, file);
                }
                if (input.InputColorSpace is not (0 or 1))
                    throw new InvalidOperationException($"{input.Name}: input colour space {input.InputColorSpace} is not measured.");
                inputs.Add(new GeneratedTextureInput(file, input.InputColorSpace == 1, input.TextureFileEnding,
                    input.MinPrecisionBits, input.InputProcessingCommand));
            }
            var (src, dst) = Channels(processor.ChannelDesc);
            channels.Add(new GeneratedTextureChannel(src, dst, processor.MipProcessingCommand, processor.OutputColorSpace == 1, refs.ToArray()));
        }
        if (!ImageFormats.TryGetValue(output.OutputTextureFormat, out var format))
            throw new InvalidOperationException($"{output.Name}: image format {output.OutputTextureFormat} is not measured.");
        var generated = new GeneratedTexture(inputs, format, channels);
        return new MaterialTexture(output.Name, generated, generated.ChildPath);
    }

    // The shader's default for an input: its own, else materials/default/default_<ending>.<ext>
    // for the first extension that exists in content, else materials/default/default.tga.
    private static string DefaultImage(ShaderVariable input, Func<string, bool> contentExists)
    {
        if (input.DefaultInputTexture.Length > 0)
            return input.DefaultInputTexture;
        foreach (var ext in DefaultImageExtensions)
        {
            var path = input.TextureFileEnding.Length > 0
                ? $"materials/default/default_{input.TextureFileEnding}.{ext}"
                : $"materials/default/default.{ext}";
            if (contentExists(path))
                return path;
        }
        return "materials/default/default.tga";
    }

    // m_nChannelDesc: one byte per channel, destination in the high nibble, source in the low.
    private static (string Src, string Dst) Channels(byte[] desc)
    {
        const string letters = "rgba";
        var src = new System.Text.StringBuilder();
        var dst = new System.Text.StringBuilder();
        foreach (var d in desc)
        {
            if (d == 0xFF)
                continue;
            if ((d >> 4) > 3 || (d & 0xF) > 3)
                throw new InvalidDataException($"Channel descriptor byte {d:X2} is not measured.");
            src.Append(letters[d & 0xF]);
            dst.Append(letters[d >> 4]);
        }
        return (src.ToString(), dst.ToString());
    }
}
