using System.Numerics;
using Source2.Compiler.Shaders;
using ValveKeyValue;

namespace Source2.Compiler.Materials;

/// <summary>
/// A compiled material (<c>.vmat_c</c>) read without ValveResourceFormat: its shader, its parameters
/// and attributes as VRF's <c>Material</c> exposes them (later entries of a name win), and its dynamic
/// parameters decompiled to expression text by <see cref="VfxEval"/>.
/// </summary>
public sealed class CompiledMaterial
{
    /// <summary>The material's DATA tree.</summary>
    public KVObject Data { get; }

    /// <summary><c>m_materialName</c>.</summary>
    public string Name { get; }

    /// <summary><c>m_shaderName</c> (<c>csgo_complex.vfx</c>).</summary>
    public string ShaderName { get; }

    /// <summary><c>m_intParams</c>: features (<c>F_TRANSLUCENT</c>) and integer parameters.</summary>
    public Dictionary<string, long> IntParams { get; } = [];

    /// <summary><c>m_floatParams</c>.</summary>
    public Dictionary<string, float> FloatParams { get; } = [];

    /// <summary><c>m_vectorParams</c>.</summary>
    public Dictionary<string, Vector4> VectorParams { get; } = [];

    /// <summary><c>m_textureParams</c>: shader texture (<c>g_tColor</c>) to compiled texture path.</summary>
    public Dictionary<string, string> TextureParams { get; } = [];

    /// <summary><c>m_intAttributes</c>.</summary>
    public Dictionary<string, long> IntAttributes { get; } = [];

    /// <summary><c>m_floatAttributes</c>.</summary>
    public Dictionary<string, float> FloatAttributes { get; } = [];

    /// <summary><c>m_vectorAttributes</c>.</summary>
    public Dictionary<string, Vector4> VectorAttributes { get; } = [];

    /// <summary><c>m_stringAttributes</c>.</summary>
    public Dictionary<string, string> StringAttributes { get; } = [];

    /// <summary>
    /// <c>m_dynamicParams</c> and <c>m_dynamicTextureParams</c>, each decompiled to its expression
    /// (newlines escaped as <c>\n</c>, as VRF writes them).
    /// </summary>
    public Dictionary<string, string> DynamicExpressions { get; } = [];

    private CompiledMaterial(KVObject data)
    {
        Data = data;
        Name = data.GetStringProperty("m_materialName") ?? "";
        ShaderName = data.GetStringProperty("m_shaderName") ?? "";
        foreach (var p in data.GetArray("m_intParams") ?? [])
            IntParams[p.GetStringProperty("m_name") ?? ""] = p.GetIntegerProperty("m_nValue");
        foreach (var p in data.GetArray("m_floatParams") ?? [])
            FloatParams[p.GetStringProperty("m_name") ?? ""] = p.GetFloatProperty("m_flValue");
        foreach (var p in data.GetArray("m_vectorParams") ?? [])
            VectorParams[p.GetStringProperty("m_name") ?? ""] = p.GetSubCollection("m_value")?.ToVector4() ?? default;
        foreach (var p in data.GetArray("m_textureParams") ?? [])
            TextureParams[p.GetStringProperty("m_name") ?? ""] = p.GetStringProperty("m_pValue") ?? "";
        foreach (var p in data.GetArray("m_intAttributes") ?? [])
            IntAttributes[p.GetStringProperty("m_name") ?? ""] = p.GetIntegerProperty("m_nValue");
        foreach (var p in data.GetArray("m_floatAttributes") ?? [])
            FloatAttributes[p.GetStringProperty("m_name") ?? ""] = p.GetFloatProperty("m_flValue");
        foreach (var p in data.GetArray("m_vectorAttributes") ?? [])
            VectorAttributes[p.GetStringProperty("m_name") ?? ""] = p.GetSubCollection("m_value")?.ToVector4() ?? default;
        foreach (var p in data.GetArray("m_stringAttributes") ?? [])
            StringAttributes[p.GetStringProperty("m_name") ?? ""] = p.GetStringProperty("m_value") ?? "";

        var renderAttributesUsed = data.GetArray<string>("m_renderAttributesUsed") ?? [];
        foreach (var key in new[] { "m_dynamicParams", "m_dynamicTextureParams" })
        {
            foreach (var p in data.GetArray(key) ?? [])
            {
                var expression = new VfxEval(p.GetArray<byte>("m_value") ?? [], renderAttributesUsed);
                DynamicExpressions.Add(p.GetStringProperty("m_name") ?? "", expression.DynamicExpressionResult.Replace("\n", "\\n", StringComparison.Ordinal));
            }
        }
    }

    /// <summary>Open a <c>.vmat_c</c>.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a well-formed material.</exception>
    public static CompiledMaterial Read(byte[] bytes) =>
        new(CompiledResource.Read(bytes).Kv3("DATA") ?? throw new InvalidDataException("The material has no KV3 DATA block."));

    /// <summary>The material's feature values (<c>F_</c> int parameters), as bytes.</summary>
    public IReadOnlyDictionary<string, byte> FeatureState =>
        IntParams.Where(p => p.Key.StartsWith("F_", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => (byte)p.Value);
}
