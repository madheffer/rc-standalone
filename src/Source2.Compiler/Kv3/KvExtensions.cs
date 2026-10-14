// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// ValveResourceFormat/Serialization/KeyValues/KVObjectExtensions.cs at d9926fcb, with this project's
// unchecked GetIntegerArray, so a host moving off VRF keeps the same conversions.
using System.Globalization;
using System.Numerics;
using System.Text;
using ValveKeyValue;
using ValveKeyValue.KeyValues3;

namespace Source2.Compiler.KeyValues;

/// <summary>
/// Typed reads of a <see cref="KVObject"/>'s children, with the semantics ValveResourceFormat's
/// extensions have: a missing child gives the default, a blank string where a number is asked for
/// gives the default too, and a number is read from whatever numeric wire type holds it.
/// </summary>
public static class KvExtensions
{
    /// <summary>The child <paramref name="name"/>, or null.</summary>
    public static KVObject GetSubCollection(this KVObject obj, string name)
        => obj.TryGetValue(name, out var value) ? value : null!;

    /// <summary>The child <paramref name="name"/> when it is a string, else <paramref name="defaultValue"/>.</summary>
    public static string GetStringProperty(this KVObject obj, string name, string? defaultValue = null)
        => obj.TryGetValue(name, out var value) && value.ValueType == KVValueType.String ? (string)value : defaultValue!;

    /// <summary>The child <paramref name="name"/> as an <see cref="int"/>, else <paramref name="defaultValue"/>.</summary>
    public static int GetInt32Property(this KVObject obj, string name, int defaultValue = 0)
        => TryGetScalar(obj, name, out var value) ? (int)value : defaultValue;

    /// <summary>The child <paramref name="name"/> as a <see cref="uint"/>, else <paramref name="defaultValue"/>.</summary>
    public static uint GetUInt32Property(this KVObject obj, string name, uint defaultValue = 0)
        => TryGetScalar(obj, name, out var value) ? (uint)value : defaultValue;

    /// <summary>The child <paramref name="name"/> as a <see cref="long"/>, else <paramref name="defaultValue"/>.</summary>
    public static long GetIntegerProperty(this KVObject obj, string name, long defaultValue = 0)
        => TryGetScalar(obj, name, out var value) ? (long)value : defaultValue;

    /// <summary>The child <paramref name="name"/> as a <see cref="ulong"/>, an INT32 taken bit for bit
    /// (binary KV3 stores some unsigned fields that way), else <paramref name="defaultValue"/>.</summary>
    public static ulong GetUnsignedIntegerProperty(this KVObject obj, string name, ulong defaultValue = 0)
    {
        if (!TryGetScalar(obj, name, out var value))
            return defaultValue;
        return value.ValueType == KVValueType.Int32 ? unchecked((ulong)(int)value) : (ulong)value;
    }

    /// <summary>The child <paramref name="name"/> as a <see cref="double"/>, else <paramref name="defaultValue"/>.</summary>
    public static double GetDoubleProperty(this KVObject obj, string name, double defaultValue = 0)
        => TryGetScalar(obj, name, out var value) ? (double)value : defaultValue;

    /// <summary>The child <paramref name="name"/> as a <see cref="float"/>, else <paramref name="defaultValue"/>.</summary>
    public static float GetFloatProperty(this KVObject obj, string name, float defaultValue = 0)
        => TryGetScalar(obj, name, out var value) ? (float)value : defaultValue;

    /// <summary>The child <paramref name="name"/> as a <see cref="byte"/>, else 0.</summary>
    public static byte GetByteProperty(this KVObject obj, string name)
        => TryGetScalar(obj, name, out var value) ? (byte)value : default;

    /// <summary>The child <paramref name="name"/> as a <see cref="bool"/> (a <c>"true"</c>/<c>"false"</c>
    /// string included), else <paramref name="defaultValue"/>.</summary>
    public static bool GetBooleanProperty(this KVObject obj, string name, bool defaultValue = false)
    {
        if (!TryGetScalar(obj, name, out var value))
            return defaultValue;
        if (value.ValueType == KVValueType.String && bool.TryParse((string)value, out var parsed))
            return parsed;
        return (bool)value;
    }

    private static bool TryGetScalar(KVObject obj, string name, out KVObject value)
        => obj.TryGetValue(name, out value!) && !(value.ValueType == KVValueType.String && string.IsNullOrWhiteSpace((string)value));

    /// <summary>The elements of the array child <paramref name="name"/> (not copied), or null.</summary>
    public static IReadOnlyList<KVObject> GetArray(this KVObject obj, string name)
        => obj.TryGetValue(name, out var child) && child.IsArray ? (IReadOnlyList<KVObject>)child.Values : null!;

    /// <summary>
    /// The array (or binary blob) child <paramref name="name"/> as values of <typeparamref name="T"/>,
    /// or null. Elements that are null, objects or arrays read as the default.
    /// </summary>
    public static T[] GetArray<T>(this KVObject obj, string name)
    {
        if (!obj.TryGetValue(name, out var child))
            return null!;

        if (child.ValueType == KVValueType.BinaryBlob)
        {
            if (typeof(T) == typeof(byte))
                return (T[])(object)child.AsBlob();
            var bytes = child.AsBlob();
            var converted = new T[bytes.Length];
            for (var i = 0; i < bytes.Length; i++)
                converted[i] = (T)Convert.ChangeType(bytes[i], typeof(T), CultureInfo.InvariantCulture);
            return converted;
        }

        if (!child.IsArray)
            return null!;

        var span = child.AsArraySpan();
        var result = new T[span.Length];
        for (var i = 0; i < span.Length; i++)
        {
            var element = span[i];
            result[i] = element.ValueType is KVValueType.Null or KVValueType.Collection or KVValueType.Array
                ? default!
                : (T)Convert.ChangeType(element, typeof(T), CultureInfo.InvariantCulture);
        }
        return result;
    }

    /// <summary>The array child <paramref name="name"/> as <see cref="ulong"/>s, signed values taken bit
    /// for bit; empty when missing.</summary>
    public static ulong[] GetUnsignedIntegerArray(this KVObject obj, string name)
    {
        if (!obj.TryGetValue(name, out var child) || !child.IsArray)
            return [];
        var span = child.AsArraySpan();
        var result = new ulong[span.Length];
        for (var i = 0; i < span.Length; i++)
        {
            var element = span[i];
            result[i] = element.ValueType switch
            {
                KVValueType.Int32 => unchecked((ulong)(int)element),
                KVValueType.Int64 => unchecked((ulong)(long)element),
                _ => (ulong)element,
            };
        }
        return result;
    }

    /// <summary>
    /// The array child <paramref name="name"/> as <see cref="long"/>s, a UINT64 taken bit for bit
    /// (community models store <c>m_refMeshGroupMasks</c> as 0xFFFFFFFFFFFFFFFF, "every group", which a
    /// checked conversion refuses); empty when missing.
    /// </summary>
    public static long[] GetIntegerArray(this KVObject obj, string name)
    {
        if (!obj.TryGetValue(name, out var child) || !child.IsArray)
            return obj.GetArray<long>(name) ?? [];
        var span = child.AsArraySpan();
        var result = new long[span.Length];
        for (var i = 0; i < span.Length; i++)
        {
            var element = span[i];
            if (element.ValueType is KVValueType.Null or KVValueType.Collection or KVValueType.Array)
                continue;
            result[i] = element.ValueType == KVValueType.UInt64 ? unchecked((long)(ulong)element) : (long)element;
        }
        return result;
    }

    /// <summary>The array child <paramref name="name"/> as <see cref="float"/>s; empty when missing.</summary>
    public static float[] GetFloatArray(this KVObject obj, string name) => obj.GetArray<float>(name) ?? [];

    /// <summary>Whether the child <paramref name="key"/> is an array (not a binary blob).</summary>
    public static bool IsNotBlobType(this KVObject obj, string key)
        => obj.TryGetValue(key, out var value) && value.ValueType == KVValueType.Array;

    /// <summary>A two-element array as a <see cref="Vector2"/>.</summary>
    public static Vector2 ToVector2(this KVObject obj) => new((float)obj[0], (float)obj[1]);

    /// <summary>A three-element array as a <see cref="Vector3"/>.</summary>
    public static Vector3 ToVector3(this KVObject obj) => new((float)obj[0], (float)obj[1], (float)obj[2]);

    /// <summary>A four-element array as a <see cref="Vector4"/>.</summary>
    public static Vector4 ToVector4(this KVObject obj) => new((float)obj[0], (float)obj[1], (float)obj[2], (float)obj[3]);

    /// <summary>A four-element array (X, Y, Z, W) as a <see cref="Quaternion"/>.</summary>
    public static Quaternion ToQuaternion(this KVObject obj) => new((float)obj[0], (float)obj[1], (float)obj[2], (float)obj[3]);

    /// <summary>A transform array: position, a uniform scale (absent in the 7-element form, where it is
    /// 1), then the rotation.</summary>
    public static (Vector3 Position, float Scale, Quaternion Rotation) ToTransform(this KVObject obj)
    {
        var position = new Vector3((float)obj[0], (float)obj[1], (float)obj[2]);
        var compact = obj.Count == 7;
        var scale = compact ? 1f : (float)obj[3];
        var at = compact ? 3 : 4;
        return (position, scale, new Quaternion((float)obj[at], (float)obj[at + 1], (float)obj[at + 2], (float)obj[at + 3]));
    }

    /// <summary>Three or four column vectors as a <see cref="Matrix4x4"/> (the fourth defaults to 0,0,0,1).</summary>
    public static Matrix4x4 ToMatrix4x4(this IReadOnlyList<KVObject> array)
    {
        var c1 = array[0].ToVector4();
        var c2 = array[1].ToVector4();
        var c3 = array[2].ToVector4();
        var c4 = array.Count > 3 ? array[3].ToVector4() : new Vector4(0, 0, 0, 1);
        return new Matrix4x4(c1.X, c2.X, c3.X, c4.X, c1.Y, c2.Y, c3.Y, c4.Y, c1.Z, c2.Z, c3.Z, c4.Z, c1.W, c2.W, c3.W, c4.W);
    }

    /// <summary>A 12- or 16-element column-major array as a <see cref="Matrix4x4"/>.</summary>
    public static Matrix4x4 ToMatrix4x4(this KVObject array)
    {
        var c4 = array.Count > 12
            ? new Vector4((float)array[12], (float)array[13], (float)array[14], (float)array[15])
            : new Vector4(0, 0, 0, 1);
        return new Matrix4x4(
            (float)array[0], (float)array[4], (float)array[8], c4.X,
            (float)array[1], (float)array[5], (float)array[9], c4.Y,
            (float)array[2], (float)array[6], (float)array[10], c4.Z,
            (float)array[3], (float)array[7], (float)array[11], c4.W);
    }

    /// <summary>
    /// The child <paramref name="name"/> as <typeparamref name="TEnum"/>: an integer as is, a string by
    /// name, with <paramref name="normalize"/> mapping C-style <c>VALVE_ENUM_SOME_VALUE</c> names first
    /// (<see cref="NormalizeEnumName{TEnum}"/>).
    /// </summary>
    /// <exception cref="KeyNotFoundException">There is no such child.</exception>
    /// <exception cref="ArgumentException">The string names no member.</exception>
    public static TEnum GetEnumValue<TEnum>(this KVObject obj, string name, bool normalize = false, string stripExtension = "Flags")
        where TEnum : struct, Enum
    {
        if (!obj.TryGetValue(name, out var value))
            throw new KeyNotFoundException($"Key '{name}' not found");
        if (value.ValueType != KVValueType.String)
            return (TEnum)(object)(int)value;
        var enumString = normalize ? NormalizeEnumName<TEnum>((string)value, stripExtension) : (string)value;
        return Enum.TryParse<TEnum>(enumString, false, out var result)
            ? result
            : throw new ArgumentException($"Unable to map {enumString} to a member of enum {typeof(TEnum).Name}");
    }

    /// <summary>
    /// A C-style <c>VALVE_ENUM_SOME_VALUE</c> as the C# <c>SomeValue</c>: underscores and digits
    /// dropped, words capitalized, and the enum's own name (less <paramref name="stripExtension"/>)
    /// removed from the front.
    /// </summary>
    public static string NormalizeEnumName<TEnum>(string name, string stripExtension = "")
        where TEnum : Enum
    {
        var enumTypeName = typeof(TEnum).Name;
        if (enumTypeName.EndsWith(stripExtension, StringComparison.Ordinal))
            enumTypeName = enumTypeName[..^stripExtension.Length];

        var sb = new StringBuilder(name.Length);
        var i = 0;
        var nextUpper = true;
        var startsWithEnumTypeName = true;
        foreach (var c in name)
        {
            if (c == '_' || char.IsDigit(c))
            {
                nextUpper = true;
                continue;
            }
            var cs = nextUpper ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c);
            sb.Append(cs);
            if (i < enumTypeName.Length && cs != enumTypeName[i])
                startsWithEnumTypeName = false;
            nextUpper = false;
            i++;
        }
        if (startsWithEnumTypeName)
            sb.Remove(0, enumTypeName.Length);
        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="root"/> as KV3 text (ValveKeyValue's writer), with a header naming the text
    /// encoding and <paramref name="format"/> (generic when null). For text that reads back to the
    /// same doubles use <see cref="ResourceDecompiler.ToKv3Text"/>.
    /// </summary>
    public static string ToKV3String(this KVObject root, KV3ID? format = null)
    {
        var doc = new KVDocument(new KVHeader { Encoding = Kv3Formats.Get("text"), Format = format ?? Kv3Formats.Get("generic") }, null, root);
        using var ms = new MemoryStream();
        KVSerializer.Create(KVSerializationFormat.KeyValues3Text).Serialize(ms, doc);
        return System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}
