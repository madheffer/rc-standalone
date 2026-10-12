using ValveKeyValue;
using ValveKeyValue.KeyValues3;

namespace Source2.Compiler;

/// <summary>
/// Decompile a compiled Source 2 resource's KV3 blocks back to KV3 text - the
/// inverse of <see cref="Kv3SourceCompiler"/>.
///
/// <para>Any block stored as binary KV3 decompiles: a <c>.vdata_c</c>'s or a
/// <c>.vpcf_c</c>'s DATA, a model's <c>PHYS</c> or <c>CTRL</c>, every resource's
/// <c>RED2</c>. Some blocks have no KV3 form at all - a legacy sound's DATA is audio
/// metadata, a texture's is pixels, and resources older than KV3 keep theirs as
/// NTRO. Those return null with a reason.</para>
/// </summary>
public static class ResourceDecompiler
{
    /// <summary>
    /// Render the resource's DATA block as KV3 text. Returns the text on success,
    /// or null with a user-facing <paramref name="reason"/> when the block has no
    /// KV3 representation (or there is no DATA block at all).
    /// </summary>
    public static string? DataBlockToKv3(CompiledResource resource, out string reason)
        => BlockToKv3(resource, "DATA", out reason);

    /// <summary>
    /// Render any KV3-backed block (<paramref name="blockType"/> is its four-character
    /// tag, e.g. <c>"PHYS"</c>) as KV3 text, under the block's own format GUID so that
    /// compiling the text stamps the same one back.
    /// </summary>
    public static string? BlockToKv3(CompiledResource resource, string blockType, out string reason)
    {
        reason = string.Empty;
        if (!resource.Has(blockType))
        {
            reason = $"No {blockType} block in resource.";
            return null;
        }
        KVObject? kv;
        try { kv = resource.Kv3(blockType); }
        catch (InvalidDataException ex)
        {
            reason = $"The {blockType} block is not readable KV3: {ex.Message}";
            return null;
        }
        if (kv is null)
        {
            reason = $"The {blockType} block is not KV3 (a typed or pre-KV3 payload with no KV3 text representation).";
            return null;
        }
        return ToKv3Text(kv, resource.Kv3Format(blockType));
    }

    /// <summary>
    /// KV3 text in ValveKeyValue's layout, with every double written so it reads back as the
    /// same double. ValveKeyValue (0.71) prints six decimals: 0.19607843137254902 came out as
    /// 0.196078 and 1e-30 as 0.0, so a decompile, edit and recompile changed every particle and
    /// soundevent float it touched (<c>s2c recompile-parity</c> on stock vpcf_c / vsndevts_c).
    /// Doubles are swapped for unique integer markers, which keep the numeric array layout, and
    /// each marker's text is then replaced with <see cref="RoundTrip"/>.
    /// </summary>
    public static string ToKv3Text(KVObject root, KV3ID? format = null)
    {
        var used = new HashSet<long>();
        CollectIntegers(root, used);
        var next = long.MinValue + 1_000_003;
        var doubles = new Dictionary<long, double>();
        KVObject Mark(KVObject node)
        {
            if (node.IsCollection)
            {
                var copy = KVObject.Collection();
                foreach (var child in node.Children)
                    copy.Add(child.Key, Mark(child.Value));
                copy.Flag = node.Flag;
                return copy;
            }
            if (node.IsArray)
            {
                var copy = KVObject.Array();
                foreach (var element in node.Values)
                    copy.Add(Mark(element));
                copy.Flag = node.Flag;
                return copy;
            }
            if (node.ValueType != KVValueType.FloatingPoint64 || !double.IsFinite((double)node))
                return node;
            while (used.Contains(next))
                next++;
            doubles[next] = (double)node;
            return new KVObject(next++) { Flag = node.Flag };
        }
        var marked = Mark(root);
        var text = format is { } f ? marked.ToKV3String(format: f) : marked.ToKV3String();
        if (doubles.Count == 0)
            return text;
        return System.Text.RegularExpressions.Regex.Replace(text, @"-9\d{18}",
            m => long.TryParse(m.Value, out var key) && doubles.TryGetValue(key, out var d) ? RoundTrip(d) : m.Value);
    }

    private static void CollectIntegers(KVObject node, HashSet<long> into)
    {
        if (node.IsCollection || node.IsArray)
        {
            foreach (var child in node.IsCollection ? node.Children.Select(c => c.Value) : node.Values)
                CollectIntegers(child, into);
            return;
        }
        if (node.ValueType is KVValueType.Int64)
            into.Add((long)node);
    }

    /// <summary>
    /// The shortest decimal that parses back to <paramref name="value"/>, written out without an
    /// exponent and always with a decimal point (so it is read as a double, not an integer):
    /// 0.19607843137254902, 0.000000000000000000000000000001, 1.0, -0.0.
    /// </summary>
    public static string RoundTrip(double value)
    {
        var r = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var e = r.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            var negative = r[0] == '-';
            var mantissa = r[(negative ? 1 : 0)..e];
            var exponent = int.Parse(r[(e + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            var point = mantissa.IndexOf('.');
            var digits = point < 0 ? mantissa : mantissa.Remove(point, 1);
            var intDigits = (point < 0 ? mantissa.Length : point) + exponent;
            r = intDigits <= 0 ? "0." + new string('0', -intDigits) + digits
                : intDigits >= digits.Length ? digits + new string('0', intDigits - digits.Length)
                : digits[..intDigits] + "." + digits[intDigits..];
            if (negative)
                r = "-" + r;
        }
        if (!r.Contains('.'))
            r += ".0";
        return r;
    }
}
