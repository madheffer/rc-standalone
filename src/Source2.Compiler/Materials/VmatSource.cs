using ValveKeyValue;

namespace Source2.Compiler.Materials;

/// <summary>
/// A <c>.vmat</c> source: KeyValues1 text, <c>Layer0 { shader "x.vfx" Key "value" ... }</c>.
/// Values stay strings, in file order; case of keys is kept as written.
/// </summary>
public sealed class VmatSource
{
    /// <summary>The shader file name (<c>csgo_complex.vfx</c>).</summary>
    public required string Shader { get; init; }

    /// <summary>Every other key of <c>Layer0</c>, in file order.</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Values { get; init; }

    /// <summary>The values by name (the last one wins, as a KV1 lookup does).</summary>
    public IReadOnlyDictionary<string, string> ByName =>
        Values.GroupBy(v => v.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);

    /// <summary>Parse a <c>.vmat</c>.</summary>
    public static VmatSource Parse(byte[] source, string path)
    {
        KVDocument document;
        try
        {
            using var stream = new MemoryStream(source);
            document = KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(stream);
        }
        catch (Exception e) when (e is KeyValueException or InvalidDataException or FormatException)
        {
            throw new InvalidDataException($"{path} is not a KeyValues1 material: {e.Message}", e);
        }
        if (!string.Equals(document.Name, "Layer0", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{path}: the root is '{document.Name}', not Layer0; other layers are not compiled here yet.");

        string? shader = null;
        var values = new List<KeyValuePair<string, string>>();
        foreach (var child in document.Root.Children)
        {
            if (child.Value.ValueType == KVValueType.Collection)
                throw new InvalidOperationException($"{path}: '{child.Key}' is a block; dynamic params and sub-blocks are not compiled here yet.");
            var value = (string)child.Value;
            if (string.Equals(child.Key, "shader", StringComparison.OrdinalIgnoreCase))
                shader = value;
            else
                values.Add(new(child.Key, value));
        }
        return new VmatSource
        {
            Shader = shader ?? throw new InvalidOperationException($"{path} names no shader."),
            Values = values,
        };
    }
}
