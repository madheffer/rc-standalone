using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler;

/// <summary>
/// Reading shader parameters back out of a compiled material's DATA tree.
///
/// <para>A <c>vmat_c</c> stores its parameters as parallel arrays of
/// <c>{ m_name, m_&lt;kind&gt;Value }</c> objects, one array per value kind
/// (<c>m_intParams</c> / <c>m_floatParams</c> / <c>m_vectorParams</c> /
/// <c>m_textureParams</c> / <c>m_stringAttributes</c>), so looking a parameter
/// up means scanning the right array for a name. The value's KV3 type is not
/// fixed by the parameter, which is why this normalises whatever numeric type
/// it finds to a double instead of assuming one.</para>
/// </summary>
public static class VmatParams
{
    /// <summary>
    /// The numeric value of <paramref name="paramName"/> inside
    /// <paramref name="arrName"/> (e.g. <c>"m_intParams"</c>), read from that
    /// entry's <paramref name="valueKey"/> field (e.g. <c>"m_nValue"</c>).
    /// Null when the array, the parameter or a numeric value is absent.
    /// </summary>
    public static double? ReadParamValue(KVObject root, string arrName, string valueKey, string paramName)
    {
        var arr = root[arrName];
        if (arr is null || !arr.IsArray)
            return null;
        foreach (var entry in arr.Values)
        {
            if (!string.Equals(entry.GetStringProperty("m_name"), paramName, StringComparison.Ordinal))
                continue;
            if (!entry.TryGetValue(valueKey, out var v) || v is null)
                return null;
            return v.ValueType switch
            {
                KVValueType.FloatingPoint or KVValueType.FloatingPoint64 => (double)v,
                KVValueType.Int16 or KVValueType.Int32 or KVValueType.Int64 => (long)v,
                KVValueType.UInt16 or KVValueType.UInt32 or KVValueType.UInt64 => (double)(ulong)v,
                KVValueType.Boolean => (bool)v ? 1.0 : 0.0,
                _ => (double?)null,
            };
        }
        return null;
    }
}
