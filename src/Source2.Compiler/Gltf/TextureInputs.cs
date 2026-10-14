// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat):
// CompiledShader/ChannelMapping, IO/ShaderDataProvider (the shader-driven and the basic provider).

using System.Buffers.Binary;
using ValvePak;
using Source2.Compiler.Materials;
using Source2.Compiler.Shaders;

namespace Source2.Compiler.Gltf;

/// <summary>
/// Which channels of a texture something reads: up to four of R, G, B, A in order. Two mappings are
/// equal when their four channel slots are (unused slots read as R), as in VRF.
/// </summary>
public readonly struct ChannelMapping : IEquatable<ChannelMapping>
{
    private readonly uint _channels;

    /// <summary>Channel numbers.</summary>
    public const byte R = 0, G = 1, B = 2, A = 3;

    private ChannelMapping(ReadOnlySpan<byte> channels)
    {
        Count = channels.Length;
        for (var i = 0; i < channels.Length; i++)
            _channels |= (uint)channels[i] << (8 * i);
    }

    /// <summary>The number of channels.</summary>
    public int Count { get; }

    /// <summary>Channel <paramref name="index"/> (0 = R ... 3 = A).</summary>
    public byte this[int index] => (byte)(_channels >> (8 * index));

    /// <summary>Single and combined mappings.</summary>
    public static readonly ChannelMapping Red = new([R]), Green = new([G]), Blue = new([B]), Alpha = new([A]),
        RG = new([R, G]), AG = new([A, G]), RGB = new([R, G, B]), RGBA = new([R, G, B, A]);

    /// <summary>
    /// A shader texture processor's channels (<c>m_nChannelDesc</c>): per byte, the channel in the high
    /// nibble and its destination in the low, up to the first 0xFF.
    /// </summary>
    /// <exception cref="InvalidDataException">A channel or destination is out of range.</exception>
    public static ChannelMapping FromChannelDesc(ReadOnlySpan<byte> desc)
    {
        var packed = desc.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(desc) : uint.MaxValue;
        Span<byte> channels = stackalloc byte[4];
        var count = 0;
        for (var i = 0; i < 4; i++)
        {
            var component = (byte)(packed >> (8 * i));
            if (component == 0xFF)
                break;
            var channel = (byte)(component >> 4);
            if (channel > A || (component & 0x0F) >= 4)
                throw new InvalidDataException($"Channel descriptor 0x{packed:X8} has 0x{component:X2} at {i}.");
            channels[count++] = channel;
        }
        return new ChannelMapping(channels[..count]);
    }

    /// <inheritdoc/>
    public bool Equals(ChannelMapping other) => _channels == other._channels;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ChannelMapping other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => (int)_channels;

    /// <summary>Equal channel slots.</summary>
    public static bool operator ==(ChannelMapping left, ChannelMapping right) => left.Equals(right);

    /// <summary>Different channel slots.</summary>
    public static bool operator !=(ChannelMapping left, ChannelMapping right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString()
    {
        var chars = new char[Count];
        for (var i = 0; i < Count; i++)
            chars[i] = "RGBA"[this[i]];
        return new string(chars);
    }
}

/// <summary>A shader's compiled programs: its features program and the others (vertex, pixel...).</summary>
/// <param name="Features">The features program.</param>
/// <param name="Programs">The other programs, the pixel one first.</param>
public sealed record ShaderPrograms(ShaderProgram Features, IReadOnlyList<ShaderProgram> Programs)
{
    /// <summary>
    /// A loader of shaders from shader packages (CS2's <c>csgo/shaders_pc_dir.vpk</c> and
    /// <c>core/shaders_pc_dir.vpk</c>), searched in order, reading each shader's programs once.
    /// </summary>
    public static Func<string, ShaderPrograms?> FromPackages(params string[] vpkPaths)
    {
        ArgumentNullException.ThrowIfNull(vpkPaths);
        var cache = new Dictionary<string, ShaderPrograms?>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();
        return shader =>
        {
            lock (gate)
            {
                if (cache.TryGetValue(shader, out var known))
                    return known;
                ShaderPrograms? result = null;
                var stem = Path.GetFileNameWithoutExtension(shader);
                foreach (var vpkPath in vpkPaths)
                {
                    using var pak = new Package();
                    pak.Read(vpkPath);
                    ShaderProgram? Load(string kind)
                    {
                        var entry = pak.FindEntry($"shaders/vfx/{stem}_pc_50_{kind}.vcs");
                        if (entry is null)
                            return null;
                        pak.ReadEntry(entry, out var bytes);
                        return ShaderProgram.Read(bytes);
                    }
                    if (Load("features") is { } features)
                    {
                        result = new ShaderPrograms(features, new[] { Load("ps"), Load("vs"), Load("psrs") }.OfType<ShaderProgram>().ToList());
                        break;
                    }
                }
                cache[shader] = result;
                return result;
            }
        };
    }
}

/// <summary>
/// The inputs a material texture is made of, channel by channel (<c>g_tColor</c>: RGB from
/// <c>TextureColor</c>, A from <c>TextureTranslucency</c>): from the shader's texture processors
/// when its programs are at hand, else VRF's table of known shaders, else the texture's own name.
/// </summary>
public static class TextureInputs
{
    /// <summary>The inputs of <paramref name="textureKey"/> in <paramref name="material"/>, as VRF's glTF exporter finds them.</summary>
    public static List<(ChannelMapping Channel, string Name)> For(string textureKey, CompiledMaterial material, ShaderPrograms? shader)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (shader is not null)
        {
            try
            {
                if (FromShader(textureKey, material, shader) is { } inputs)
                    return inputs;
            }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or KeyNotFoundException or NotSupportedException)
            {
                // Shaders are complicated; a texture they cannot explain falls back to the table.
            }
        }
        return [.. Basic(textureKey, material)];
    }

    private static List<(ChannelMapping, string)>? FromShader(string textureKey, CompiledMaterial material, ShaderPrograms shader)
    {
        var features = shader.Features;
        var candidates = features.Variables.Select((v, i) => (Variable: v, Index: i)).Where(p => p.Variable.Name == textureKey).ToList();
        if (candidates.Count == 0)
            return [(ChannelMapping.RGBA, ConvertTextureToInput(textureKey))];   // removed from the shader since the material compiled
        candidates = candidates.Where(p => p.Variable.ChannelCount > 0).ToList();
        if (candidates.Count == 0)
            throw new InvalidDataException($"None of the variants of '{textureKey}' had any channel defined.");
        var (parameter, program) = (candidates[0].Variable, features);
        if (candidates.Count > 1)
        {
            var found = DetermineParameter(shader, material, textureKey, forcedStatic: null);
            if (found is null)
                return [(ChannelMapping.RGBA, ConvertTextureToInput(textureKey))];
            (parameter, program) = found.Value;
        }
        return ParameterInputs(parameter, program);
    }

    // Which of a parameter's variants the material's static combo writes, pixel program first.
    private static (ShaderVariable, ShaderProgram)? DetermineParameter(ShaderPrograms shader, CompiledMaterial material, string name, string? forcedStatic)
    {
        var featureState = material.FeatureState;
        foreach (var program in shader.Programs.Where(p => p.StaticComboIds.Any()))
        {
            var fileParams = program.Variables.Select((v, i) => (Variable: v, Index: i)).Where(p => p.Variable.Name == name).ToList();
            if (fileParams.Count == 0)
                continue;
            var staticState = new Dictionary<string, int> { ["S_MODE_FORWARD"] = 1 };
            if (forcedStatic is not null)
                staticState[forcedStatic] = 1;
            var config = StaticConfiguration(shader.Features, program, featureState, staticState);
            var variables = program.VariablesOf(ComboId(program, config));
            if (variables is null)
            {
                // The feature rules and the static rules can disagree; turn combos off until the rules hold.
                if (!TryReduce(program, config, out var reduced))
                    throw new InvalidOperationException("The feature state points to a missing static combo the rules do not reduce.");
                variables = program.VariablesOf(ComboId(program, reduced))
                    ?? throw new InvalidOperationException("Reducing the static configuration did not reach a combo the program has.");
            }
            foreach (var index in variables)
            {
                var referenced = fileParams.FindIndex(p => p.Index == index);
                if (referenced >= 0)
                    return (fileParams[referenced].Variable, program);
            }
            if (forcedStatic is null)
                return DetermineParameter(shader, material, name, "S_MODE_TOOLS_VIS");
        }
        return null;
    }

    // VRF's ShaderDataProvider.GetStaticConfiguration_ForFeatureState.
    private static int[] StaticConfiguration(ShaderProgram features, ShaderProgram program, IReadOnlyDictionary<string, byte> featureState, Dictionary<string, int> staticState)
    {
        var config = new int[program.StaticCombos.Count];
        for (var i = 0; i < config.Length; i++)
        {
            var combo = program.StaticCombos[i];
            if (combo.FeatureIndex == -1)
            {
                config[i] = staticState.GetValueOrDefault(combo.Name);
                continue;
            }
            var feature = features.StaticCombos[combo.FeatureIndex];
            var featureValue = featureState.GetValueOrDefault(feature.Name);
            // VCS 71 dropped two system values; later source types sit two lower in the file.
            var source = combo.SourceType >= (int)VfxStaticComboSourceType.S_BINDLESS_RUNTIME ? combo.SourceType + 2 : combo.SourceType;
            config[i] = source switch
            {
                (int)VfxStaticComboSourceType.__SET_BY_FEATURE__ => Math.Clamp(featureValue, feature.Min, feature.Max),
                (int)VfxStaticComboSourceType.__SET_BY_FEATURE_EQ__ => featureValue == combo.CompareValue ? 1 : 0,
                (int)VfxStaticComboSourceType.__SET_BY_FEATURE_NE__ => featureValue != combo.CompareValue ? 1 : 0,
                _ => throw new InvalidOperationException($"Unexpected source type {source} for feature-linked combo '{combo.Name}'."),
            };
        }
        return config;
    }

    private static long ComboId(ShaderProgram program, int[] config)
    {
        long id = 0;
        for (var i = 0; i < config.Length; i++)
            id += (config[i] - program.StaticCombos[i].Min) * program.StaticCombos[i].ComboIndexValue;
        return id;
    }

    // VRF's ShaderDataProvider.TryReduceStaticConfiguration: until stable, a combo whose ChildOf or
    // Requires rule is unmet turns off, and AllowNum turns off the combos past the allowed count.
    private static bool TryReduce(ShaderProgram program, int[] config, out int[] reduced)
    {
        var current = (int[])config.Clone();
        for (var loop = 0; loop < 100; loop++)
        {
            var changed = false;
            foreach (var rule in program.StaticComboRules)
            {
                var indexCount = rule.ExtraRuleData[1];
                if (rule.Method is VfxRuleMethod.ChildOf or VfxRuleMethod.Requires)
                {
                    if (!ArgMatches(rule, 0, current))
                        continue;
                    var enabled = 0;
                    for (var i = 1; i < indexCount; i++)
                        if (ArgMatches(rule, i, current))
                            enabled++;
                    if (enabled < rule.ExtraRuleData[0])
                    {
                        current[rule.ArgIndices[0]] = 0;
                        changed = true;
                    }
                }
                else if (rule.Method == VfxRuleMethod.AllowNum)
                {
                    var allowed = rule.ExtraRuleData[0];
                    var enabled = 0;
                    for (var i = 0; i < indexCount; i++)
                    {
                        if (!ArgMatches(rule, i, current))
                            continue;
                        if (enabled >= allowed)
                        {
                            if (rule.ArgTypes[i] == rule.Type)
                            {
                                current[rule.ArgIndices[i]] = 0;
                                changed = true;
                            }
                        }
                        else
                        {
                            enabled++;
                        }
                    }
                }
            }
            if (!changed)
                break;
        }
        reduced = current;
        return !current.AsSpan().SequenceEqual(config);

        static bool ArgMatches(ShaderRule rule, int arg, int[] values)
        {
            if (rule.ArgTypes[arg] != VfxRuleType.Static)
                return false;
            var value = values[rule.ArgIndices[arg]];
            var expected = rule.ArgValues[arg];
            return expected == -1 ? value != 0 : value == expected;
        }
    }

    private static List<(ChannelMapping, string)> ParameterInputs(ShaderVariable parameter, ShaderProgram program)
    {
        var result = new List<(ChannelMapping, string)>();
        for (var i = 0; i < parameter.ChannelCount; i++)
        {
            var processor = program.ChannelProcessors[parameter.ChannelInfoIndex[i]];
            var cutoff = Array.IndexOf(processor.InputTextures, -1);
            var inputs = processor.InputTextures[..cutoff].Select(index => program.Variables[index].Name).ToArray();
            var channel = ChannelMapping.FromChannelDesc(processor.ChannelDesc);
            if (processor.MipProcessingCommand is "HemiOctIsoRoughness_RG_B" or "AnisoNormal")
            {
                result.Add((ChannelMapping.RGB, inputs[0]));
                if (inputs.Length == 2)
                    result.Add((ChannelMapping.Alpha, inputs[1]));
                return result;
            }
            // A compiler-generated texture (VRF issue 630).
            if (processor.MipProcessingCommand == "AnisoRoughness_RG" && inputs.Length > 1)
                return result;
            // The decoder already undoes the AG swizzle (VRF issue 591).
            if (channel == ChannelMapping.AG)
            {
                result.Add((ChannelMapping.RG, inputs[0]));
                return result;
            }
            result.Add((channel, inputs[0]));
        }
        return result;
    }

    /// <summary><c>g_tColor</c> to <c>TextureColor</c>.</summary>
    public static string ConvertTextureToInput(string textureType) => textureType.Replace("g_t", "Texture", StringComparison.Ordinal);

    private static readonly ChannelMapping C_R = ChannelMapping.Red, C_G = ChannelMapping.Green, C_B = ChannelMapping.Blue,
        C_A = ChannelMapping.Alpha, C_AG = ChannelMapping.AG, C_RGB = ChannelMapping.RGB, C_RGBA = ChannelMapping.RGBA;

    // VRF's BasicShaderDataProvider.TextureMappings.
    private static readonly Dictionary<string, Dictionary<string, (ChannelMapping Channel, string Name)[]>> KnownShaders = new()
    {
        ["global_lit_simple"] = new()
        {
            ["g_tColor"] = [(C_RGB, "TextureColor"), (C_A, "TextureTranslucency")],
            ["g_tNormal"] = [(C_RGB, "TextureNormal")],
            ["g_tSpecular"] = [(C_R, "TextureReflectance"), (C_G, "TextureSelfIllum"), (C_B, "TextureBloom")],
        },
        ["multiblend"] = new()
        {
            ["g_tColor0"] = [(C_RGB, "TextureColor0")],
            ["g_tColor1"] = [(C_RGB, "TextureColor1"), (C_A, "TextureRevealMask1")],
            ["g_tColor2"] = [(C_RGB, "TextureColor2"), (C_A, "TextureRevealMask2")],
            ["g_tColor3"] = [(C_RGB, "TextureColor3"), (C_A, "TextureRevealMask3")],
            ["g_tSpecular0"] = [(C_R, "TextureReflectance0"), (C_G, "TextureSelfIllum0"), (C_B, "TextureBloom0")],
            ["g_tSpecular1"] = [(C_R, "TextureReflectance1"), (C_G, "TextureSelfIllum1"), (C_B, "TextureBloom1")],
            ["g_tSpecular2"] = [(C_R, "TextureReflectance2"), (C_G, "TextureSelfIllum2"), (C_B, "TextureBloom2")],
            ["g_tSpecular3"] = [(C_R, "TextureReflectance3"), (C_G, "TextureSelfIllum3"), (C_B, "TextureBloom3")],
            ["g_tTintMasks"] = [(C_R, "TextureTintMask0"), (C_G, "TextureTintMask1"), (C_B, "TextureTintMask2"), (C_A, "TextureTintMask3")],
            ["g_tTint2Masks"] = [(C_R, "TextureTint2Mask0"), (C_G, "TextureTint2Mask1"), (C_B, "TextureTint2Mask2"), (C_A, "TextureTint2Mask3")],
        },
        ["hero"] = new()
        {
            ["g_tColor"] = [(C_RGB, "TextureColor"), (C_A, "TextureTranslucency")],
            ["g_tNormal"] = [(C_RGB, "TextureNormal")],
            ["g_tCubeMap"] = [(C_RGBA, "TextureCubeMap")],
            ["g_tCubeMapSeparateMask"] = [(C_G, "TextureCubeMapSeparateMask")],
            ["g_tFresnelWarp"] = [(C_R, "TextureFresnelWarpRim"), (C_G, "TextureFresnelWarpColor"), (C_B, "TextureFresnelWarpSpec")],
            ["g_tMasks1"] = [(C_R, "TextureDetailMask"), (C_G, "TextureDiffuseWarpMask"), (C_B, "TextureMetalnessMask"), (C_A, "TextureSelfIllumMask")],
            ["g_tMasks2"] = [(C_R, "TextureSpecularMask"), (C_G, "TextureRimMask"), (C_B, "TextureTintByBaseMask"), (C_A, "TextureSpecularExponent")],
            ["g_tDetail"] = [(C_RGBA, "TextureDetail")],
            ["g_tDetail2"] = [(C_RGBA, "TextureDetail2")],
        },
        ["grasstile_preview"] = new()
        {
            ["g_tColor"] = [(C_RGB, "TextureColor"), (C_A, "TextureTranslucency")],
            ["g_tTintMask"] = [(C_G, "TextureTintMask")],
            ["g_tSpecular"] = [(C_G, "TextureReflectance")],
            ["g_tSelfIllum"] = [(C_G, "TextureSelfIllum")],
        },
        ["generic"] = new()
        {
            ["g_tColor"] = [(C_RGB, "TextureColor")],
            ["g_tNormal"] = [(C_RGB, "TextureNormal")],
            ["g_tMetalnessReflectanceFresnel"] = [(C_R, "TextureMetalness"), (C_G, "TextureReflectance"), (C_B, "TextureFresnel")],
            ["g_tRoughness"] = [(C_R, "TextureRoughness")],
        },
        ["vr_standard"] = new()
        {
            ["g_tColor"] = [(C_RGB, "TextureColor"), (C_A, "TextureTranslucency")],
            ["g_tColor1"] = [(C_RGB, "TextureColor")],
            ["g_tColor2"] = [(C_RGB, "TextureColor")],
            ["g_tNormal"] = [(C_RGB, "TextureNormal")],
            ["g_tNormal1"] = [(C_RGB, "TextureNormal")],
            ["g_tNormal2"] = [(C_RGB, "TextureNormal")],
        },
        ["vr_complex"] = new()
        {
            ["g_tColor"] = [(C_RGB, "TextureColor"), (C_A, string.Empty)],
            ["g_tNormal"] = [(C_RGB, "TextureNormal"), (C_A, "TextureRoughness")],
            ["g_tAmbientOcclusion"] = [(C_R, "TextureAmbientOcclusion")],
            ["g_tTintMask"] = [(C_R, "TextureTintMask")],
            ["g_tMetalness"] = [(C_R, "TextureMetalness")],
            ["g_tSelfIllumMask"] = [(C_R, "TextureSelfIllumMask")],
            ["g_tBentNormal"] = [(C_RGB, "TextureBentNormal")],
            ["g_tDetail"] = [(C_RGB, "TextureDetail")],
            ["g_tDetailMask"] = [(C_R, "TextureDetailMask")],
            ["g_tNormalDetail"] = [(C_RGB, "TextureNormalDetail")],
            ["g_tSquishColor"] = [(C_RGB, "TextureSquishColor")],
            ["g_tStretchColor"] = [(C_RGB, "TextureStretchColor")],
            ["g_tSquishNormal"] = [(C_RGB, "TextureSquishNormal")],
            ["g_tStretchNormal"] = [(C_RGB, "TextureStretchNormal")],
            ["g_tSquishAmbientOcclusion"] = [(C_R, "TextureSquishAmbientOcclusion")],
            ["g_tStretchAmbientOcclusion"] = [(C_R, "TextureStretchAmbientOcclusion")],
        },
        ["vr_simple"] = new()
        {
            ["g_tColor"] = [(C_RGB, "TextureColor"), (C_A, string.Empty)],
            ["g_tNormal"] = [(C_RGB, "TextureNormal"), (C_A, "TextureRoughness")],
            ["g_tAmbientOcclusion"] = [(C_R, "TextureAmbientOcclusion")],
            ["g_tTintMask"] = [(C_R, "TextureTintMask")],
        },
        ["vr_simple_2way_blend"] = new()
        {
            ["g_tColorA"] = [(C_RGB, "TextureColorA"), (C_A, "TextureMetalnessA")],
            ["g_tNormalA"] = [(C_RGB, "TextureNormalA"), (C_A, "TextureRoughnessA")],
            ["g_tColorB"] = [(C_RGB, "TextureColorB"), (C_A, "TextureMetalnessB")],
            ["g_tNormalB"] = [(C_RGB, "TextureNormalB"), (C_A, "TextureRoughnessB")],
            ["g_tMask"] = [(C_R, "TextureMask")],
        },
        ["vr_eyeball"] = new()
        {
            ["g_tColor"] = [(C_RGB, "TextureColor"), (C_A, "TextureReflectance")],
            ["g_tIris"] = [(C_RGB, "IrisNormal"), (C_A, "IrisRoughness")],
            ["g_tNormal"] = [(C_AG, "TextureNormal")],
            ["g_tIrisMask"] = [(C_R, "TextureIrisMask")],
            ["g_tSelfIllumMask"] = [(C_R, "TextureSelfIllumMask")],
        },
        ["csgo_weapon"] = new()
        {
            ["g_tColor"] = [(C_RGB, "TextureColor")],
            ["g_tMetalness"] = [(C_R, "TextureRoughness"), (C_G, "TextureMetalness")],
            ["g_tAmbientOcclusion"] = [(C_R, "TextureAmbientOcclusion")],
        },
        ["sky"] = new()
        {
            ["g_tSkyTexture"] = [(C_RGBA, "SkyTexture")],
        },
    };

    // VRF's BasicShaderDataProvider.GetInputsForTexture.
    private static IEnumerable<(ChannelMapping Channel, string Name)> Basic(string textureType, CompiledMaterial material)
    {
        var shaderName = Path.ChangeExtension(material.ShaderName, null);
        if (!(KnownShaders.TryGetValue(shaderName, out var shaderSpecific) && shaderSpecific.TryGetValue(textureType, out var mappings)))
        {
            yield return (ChannelMapping.RGBA, ConvertTextureToInput(textureType));
            yield break;
        }
        foreach (var (channel, name) in mappings)
        {
            var inputName = name;
            if (inputName.Length == 0 && !NonStaticMap(shaderName, textureType, material.IntParams, out inputName))
                continue;
            if (inputName is null)
                continue;
            yield return (channel, inputName);
        }
    }

    private static bool NonStaticMap(string shader, string textureType, Dictionary<string, long> intParams, out string? inputName)
    {
        if (shader == "vr_simple" && textureType == "g_tColor")
        {
            if (intParams.GetValueOrDefault("F_METALNESS_TEXTURE") != 0)
            {
                inputName = "TextureMetalness";
                return true;
            }
            if (intParams.GetValueOrDefault("F_AMBIENT_OCCLUSION_TEXTURE") != 0)
            {
                inputName = "TextureAmbientOcclusion";
                return true;
            }
        }
        else if (shader == "vr_complex" && textureType == "g_tColor")
        {
            inputName = intParams.GetValueOrDefault("F_TRANSLUCENT") != 0 || intParams.GetValueOrDefault("F_ALPHA_TEST") != 0
                ? "TextureTranslucency" : "TextureMetalness";
            return true;
        }
        inputName = null;
        return false;
    }
}
