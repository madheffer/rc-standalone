using ValveKeyValue;
using ValveKeyValue.KeyValues3;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler;

/// <summary>
/// Decompile a compiled Source 2 resource's DATA block back to KV3 text - the
/// inverse of <see cref="Kv3SourceCompiler"/>.
///
/// <para><c>Block.AsKeyValueCollection()</c> handles only the two generic KV
/// blocks, <see cref="BinaryKV3"/> and <see cref="NTRO"/>, which back
/// <c>.vdata_c</c>, <c>.vsndevts_c</c> and <c>.vagrp_c</c>. A <c>.vpcf_c</c>,
/// <c>.vmdl_c</c> or <c>.vmat_c</c> parses into a typed
/// <see cref="KeyValuesOrNTRO"/> subclass instead, which holds the same tree in
/// its <c>.Data</c> property but throws from AsKeyValueCollection(). Reading
/// <c>.Data</c> is what lets those decompile.</para>
///
/// <para>Some DATA blocks have no KV3 form at all - Sound is audio metadata plus
/// a stream, Texture is pixels. Those return null with a reason.</para>
/// </summary>
public static class ResourceDecompiler
{
    /// <summary>
    /// Render the resource's DATA block as KV3 text. Returns the text on success,
    /// or null with a user-facing <paramref name="reason"/> when the block has no
    /// KV3 representation (or there is no DATA block at all).
    /// </summary>
    public static string? DataBlockToKv3(Resource resource, out string reason)
        => BlockToKv3(resource, BlockType.DATA, out reason);

    /// <summary>
    /// Render any KV3-backed block as KV3 text. Most of a map is not in DATA:
    /// a model's collision is <c>PHYS</c>, its buffer layout is <c>CTRL</c>, and
    /// the identity every compile is judged by is <c>RED2</c>.
    /// </summary>
    public static string? BlockToKv3(Resource resource, BlockType blockType, out string reason)
    {
        reason = string.Empty;

        var data = resource.Blocks.FirstOrDefault(b => b.Type == blockType);
        if (data is null)
        {
            reason = $"No {blockType} block in resource.";
            return null;
        }

        // KeyValuesOrNTRO subclasses expose the KV3 tree via .Data (their typed
        // identity is exactly why AsKeyValueCollection() throws on them). Plain
        // BinaryKV3 / NTRO keep the original, proven path so the already-working
        // types (.vdata_c / .vsndevts_c / .vagrp_c) produce byte-identical text.
        // RED2 parses into ResourceEditInfo2, which keeps the tree in its own
        // Data document rather than answering AsKeyValueCollection().
        KVObject? kv = data switch
        {
            KeyValuesOrNTRO typed => typed.Data,
            ResourceEditInfo2 { Data: { } red2 } => red2.Root,
            BinaryKV3 or NTRO => data.AsKeyValueCollection(),
            _ => null,
        };

        if (kv is null)
        {
            reason = $"This resource type uses a typed data block ({data.GetType().Name}) " +
                     "that has no KV3 text representation.";
            return null;
        }

        // The header names the block's own format GUID, so compiling the text
        // stamps the same one back (a particle's DATA is not "generic").
        var format = data switch
        {
            KeyValuesOrNTRO typed => typed.Format,
            ResourceEditInfo2 { Data: { } doc } => doc.Header?.Format,
            BinaryKV3 binary => binary.Data?.Header?.Format,
            _ => null,
        };
        return ToKv3Text(kv, format);
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
