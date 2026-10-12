using ValveKeyValue;
using ValveKeyValue.KeyValues3;

namespace Source2.Compiler;

/// <summary>
/// Typed reads of a <see cref="KVObject"/>'s children and KV3 text output, with the semantics the
/// compiler was written against (ValveResourceFormat's <c>KVObjectExtensions</c>, MIT): a missing
/// child gives the default, and so does a blank string where a number is asked for.
/// </summary>
internal static class KvAccess
{
    /// <summary>The child <paramref name="name"/>, or null.</summary>
    public static KVObject GetSubCollection(this KVObject obj, string name)
        => obj.TryGetValue(name, out var value) ? value : null!;

    /// <summary>The child <paramref name="name"/> when it is a string, else <paramref name="defaultValue"/>.</summary>
    public static string GetStringProperty(this KVObject obj, string name, string? defaultValue = null)
        => obj.TryGetValue(name, out var value) && value.ValueType == KVValueType.String ? (string)value : defaultValue!;

    /// <summary>The child <paramref name="name"/> as an int, else <paramref name="defaultValue"/>.</summary>
    public static int GetInt32Property(this KVObject obj, string name, int defaultValue = 0)
        => TryGetScalar(obj, name, out var value) ? (int)value : defaultValue;

    /// <summary>The child <paramref name="name"/> as a float, else <paramref name="defaultValue"/>.</summary>
    public static float GetFloatProperty(this KVObject obj, string name, float defaultValue = 0)
        => TryGetScalar(obj, name, out var value) ? (float)value : defaultValue;

    private static bool TryGetScalar(KVObject obj, string name, out KVObject value)
        => obj.TryGetValue(name, out value!) && !(value.ValueType == KVValueType.String && string.IsNullOrWhiteSpace((string)value));

    /// <summary>
    /// <paramref name="root"/> as KV3 text (ValveKeyValue's writer), with a header naming the text
    /// encoding and <paramref name="format"/> (generic when null).
    /// </summary>
    public static string ToKV3String(this KVObject root, KV3ID? format = null)
    {
        var doc = new KVDocument(new KVHeader { Encoding = Kv3Formats.Get("text"), Format = format ?? Kv3Formats.Get("generic") }, null, root);
        using var ms = new MemoryStream();
        KVSerializer.Create(KVSerializationFormat.KeyValues3Text).Serialize(ms, doc);
        return System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}
