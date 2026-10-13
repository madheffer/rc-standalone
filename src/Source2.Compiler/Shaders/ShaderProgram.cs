using ValveKeyValue;

namespace Source2.Compiler.Shaders;

/// <summary>A static or feature combo of a shader program (<c>m_staticComboArray</c>).</summary>
/// <param name="Name">F_ or S_ name.</param>
/// <param name="Min">Lowest value.</param>
/// <param name="Max">Highest value.</param>
/// <param name="FeatureIndex">For a PS/VS combo, the index of the feature combo it follows, or -1.</param>
/// <param name="ComboIndexValue">The combo's stride in a static combo id.</param>
/// <param name="SourceType">0 for a material feature, 2 for a system combo such as S_SHADER_QUALITY.</param>
public sealed record ShaderCombo(string Name, int Min, int Max, int FeatureIndex, long ComboIndexValue, int SourceType);

/// <summary>One variable description (<c>m_variableDescriptionArray</c>): a material parameter,
/// an input texture (<c>TextureColor</c>) or an output texture (<c>g_tColor</c>).</summary>
public sealed record ShaderVariable(
    string Name,
    int Type,
    string SourceString,
    uint OutputTextureFormat,
    int[] ChannelInfoIndex,
    int InputColorSpace,
    int MinPrecisionBits,
    string TextureFileEnding,
    string InputProcessingCommand,
    string DefaultInputTexture)
{
    /// <summary>The variable type of a 2D texture.</summary>
    public const int Texture2D = 14;

    /// <summary>A material texture: a 2D texture the material supplies (no engine source).</summary>
    public bool IsMaterialTexture => Type == Texture2D && SourceString.Length == 0;
}

/// <summary>One texture channel processor (<c>m_textureChannelProcessorArray</c>): which input
/// textures fill which channels of an output texture, and how its mips are made.</summary>
/// <param name="ChannelDesc">Per channel, destination in the high nibble and source in the low (0..3 = r..a); 0xFF unused.</param>
/// <param name="InputTextures">Input variable indices, -1 unused.</param>
/// <param name="OutputColorSpace">1 sRGB, 0 linear.</param>
/// <param name="MipProcessingCommand">The mip algorithm, as the shader names it.</param>
public sealed record TextureChannelProcessor(byte[] ChannelDesc, int[] InputTextures, int OutputColorSpace, string MipProcessingCommand);

/// <summary>
/// A compiled shader program file (<c>&lt;shader&gt;_pc_50_{features,vs,ps}.vcs</c>, VCS v72): a
/// Source 2 resource whose DATA block is KV3, read with <see cref="CompiledResource"/>
/// (docs/MATERIALS.md). Only what a material compile reads is kept.
/// </summary>
public sealed class ShaderProgram
{
    /// <summary><c>m_nVersion</c> of a features file: the shader's RED2 fingerprint. 0 for a VS/PS.</summary>
    public int Version { get; private init; }

    /// <summary>Each mode's name and fallback shader (features only).</summary>
    public IReadOnlyList<(string Name, string Fallback)> Modes { get; private init; } = [];

    /// <summary>Feature combos (features file) or static combos (VS/PS).</summary>
    public IReadOnlyList<ShaderCombo> StaticCombos { get; private init; } = [];

    /// <summary>The variable descriptions, in file order (indices are what everything else refers to).</summary>
    public IReadOnlyList<ShaderVariable> Variables { get; private init; } = [];

    /// <summary>The texture channel processors.</summary>
    public IReadOnlyList<TextureChannelProcessor> ChannelProcessors { get; private init; } = [];

    private readonly Dictionary<long, int[]> _variablesByCombo = [];

    /// <summary>The variables a static combo uses (<c>m_allVars</c>), or null when the program has no such combo.</summary>
    public int[]? VariablesOf(long staticComboId) => _variablesByCombo.GetValueOrDefault(staticComboId);

    /// <summary>Every static combo id the program carries data for.</summary>
    public IEnumerable<long> StaticComboIds => _variablesByCombo.Keys;

    /// <summary>Read a <c>.vcs</c> file.</summary>
    public static ShaderProgram Read(byte[] vcs)
    {
        var resource = CompiledResource.Read(vcs);
        if (resource.Version != 72)
            throw new InvalidDataException($"VCS version {resource.Version}; this reader is measured on 72.");
        var root = resource.Kv3("DATA") ?? throw new InvalidDataException("The shader program has no DATA block.");
        var program = Child(root, "m_programData") ?? root;

        var ids = Child(program, "m_staticComboIDs") is { } idList ? Longs(idList).ToList() : [];
        var data = Items(program, "m_staticComboData").ToList();
        var result = new ShaderProgram
        {
            Version = Child(root, "m_nVersion") is { } v ? (int)v : 0,
            Modes = Items(root, "m_modeArray").Select(m => (Str(m, "m_szName"), Str(m, "m_szShaderFallback"))).ToList(),
            StaticCombos = Items(program, "m_staticComboArray").Select(c => new ShaderCombo(
                Str(c, "m_szName"), Int(c, "m_nMin"), Int(c, "m_nMax"), Int(c, "m_iFeatureIndex"),
                (long)Child(c, "m_nComboIndexValue")!, Int(c, "m_shaderComboSourceType"))).ToList(),
            Variables = Items(program, "m_variableDescriptionArray").Select(ReadVariable).ToList(),
            ChannelProcessors = Items(program, "m_textureChannelProcessorArray").Select(p => new TextureChannelProcessor(
                Bytes(Child(p, "m_nChannelDesc")!), Longs(Child(p, "m_nInputTextures")!).Select(x => (int)x).ToArray(),
                Int(p, "m_outputColorSpace"), Str(p, "m_mipProcessingCommand"))).ToList(),
        };
        if (ids.Count != data.Count)
            throw new InvalidDataException($"{ids.Count} static combo ids for {data.Count} static combo data entries.");
        for (int i = 0; i < ids.Count; i++)
        {
            var all = Child(Child(data[i], "m_allVars")!, "m_indexAndRegisterOffsetArray")!;
            // Each entry packs the variable index in bits 16..27 above a 16-bit register offset.
            result._variablesByCombo[ids[i]] = Longs(all).Select(x => (int)((x >> 16) & 0xFFF)).ToArray();
        }
        return result;
    }

    /// <summary>
    /// The static combo id of a program for a material's feature values: each combo that follows
    /// a feature takes that feature's value, every other combo its minimum.
    /// </summary>
    public long StaticComboId(Func<int, int> featureValue)
    {
        long id = 0;
        foreach (var c in StaticCombos)
        {
            int value = c.FeatureIndex >= 0 ? featureValue(c.FeatureIndex) : c.Min;
            id += (long)Math.Clamp(value, c.Min, c.Max) * c.ComboIndexValue;
        }
        return id;
    }

    private static ShaderVariable ReadVariable(KVObject v) => new(
        Str(v, "m_szName"), Int(v, "m_type"), Str(v, "m_pSourceString"),
        // Buffers and other non-texture variables carry no texture fields.
        Child(v, "m_outputTextureFormat") is { } format ? (uint)(long)format : uint.MaxValue,
        Child(v, "m_nChannelInfoIndex") is { } channels ? Longs(channels).Select(x => (int)x).ToArray() : [-1, -1, -1, -1],
        Int(v, "m_inputColorSpace"), Child(v, "m_nMinPrecisionBits") is { } bits ? (int)(long)bits : -1, Str(v, "m_szTextureFileEnding"),
        Str(v, "m_inputProcessingCommand"), Str(v, "m_defaultInputTexture"));

    private static KVObject? Child(KVObject o, string key) => o.Children.FirstOrDefault(c => c.Key == key).Value;

    private static IEnumerable<KVObject> Items(KVObject o, string key) => Child(o, key) is { IsArray: true } a ? a.Values : [];

    private static string Str(KVObject o, string key) => Child(o, key) is { } v ? (string)v : "";

    private static int Int(KVObject o, string key) => Child(o, key) is { } v ? (int)(long)v : 0;

    private static IEnumerable<long> Longs(KVObject o) => o.IsArray ? o.Values.SelectMany(Longs) : [(long)o];

    private static byte[] Bytes(KVObject o) => o.ValueType == KVValueType.BinaryBlob
        ? o.AsBlob().ToArray()
        : Longs(o).Select(x => (byte)x).ToArray();
}
