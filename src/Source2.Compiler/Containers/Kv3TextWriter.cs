using System.Globalization;
using System.Text;
using ValveKeyValue;

namespace Source2.Compiler;

/// <summary>
/// KV3 text as tier0's SaveKV3 writes it (header 1800d92a0, nodes 1800d7940 of
/// the 20260923 build), CRLF and no final newline:
/// <list type="bullet">
/// <item>a member is <c>key = value</c>; a table or a multi-line array as a
/// member's value starts on the next line at the member's indent;</item>
/// <item>an array of fewer than five plain scalars (null, bool, int, uint,
/// double) is inline, <c>[ a, b ]</c>; any other is one element per line,
/// a scalar array four to a line, every element followed by a comma;</item>
/// <item>a double that is exactly a float prints as V_FloatToString_NoExponent
/// does: the shortest round-trip digits with any exponent written out, and
/// ".0" on a whole number; a string is quoted with backslashes and quotes
/// escaped, after its flag (<c>resource_name:</c> and the others).</item>
/// </list>
/// </summary>
public static class Kv3TextWriter
{
    private const string Newline = "\r\n";

    public static string Write(KVObject root, Guid format, string formatName = "generic")
    {
        var s = new StringBuilder();
        s.Append("<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:")
         .Append(formatName).Append(":version{").Append(format.ToString("D")).Append("} -->").Append(Newline);
        Table(s, root, 0);
        return s.ToString();
    }

    private static void Indent(StringBuilder s, int depth) => s.Append('\t', depth);

    private static void Table(StringBuilder s, KVObject table, int depth)
    {
        s.Append('{').Append(Newline);
        foreach (var c in table.Children)
        {
            Indent(s, depth + 1);
            s.Append(c.Key).Append(" = ");
            Value(s, c.Value, depth + 1, member: true);
            s.Append(Newline);
        }
        Indent(s, depth);
        s.Append('}');
    }

    private static bool Plain(KVObject v)
        => v.ValueType is KVValueType.Null or KVValueType.Boolean or KVValueType.Int16 or KVValueType.UInt16 or KVValueType.Int32 or KVValueType.UInt32
            or KVValueType.Int64 or KVValueType.UInt64 or KVValueType.FloatingPoint or KVValueType.FloatingPoint64;

    private static void Value(StringBuilder s, KVObject v, int depth, bool member)
    {
        if (v.IsArray)
        {
            var items = v.Values.ToList();
            var plain = items.All(Plain);
            if (items.Count < 5 && plain)
            {
                s.Append("[ ");
                for (var i = 0; i < items.Count; i++)
                {
                    if (i > 0)
                        s.Append(", ");
                    Scalar(s, items[i]);
                }
                s.Append(" ]");
                return;
            }
            if (member)
            {
                s.Append(Newline);
                Indent(s, depth);
            }
            s.Append('[').Append(Newline);
            for (var i = 0; i < items.Count; i++)
            {
                if (plain && i % 4 != 0)
                    s.Append(' ');
                else
                {
                    if (i > 0)
                        s.Append(Newline);
                    Indent(s, depth + 1);
                }
                Value(s, items[i], depth + 1, member: false);
                s.Append(',');
            }
            if (items.Count > 0)
                s.Append(Newline);
            Indent(s, depth);
            s.Append(']');
            return;
        }
        if (v.ValueType == KVValueType.Collection)
        {
            if (member)
            {
                s.Append(Newline);
                Indent(s, depth);
            }
            Table(s, v, depth);
            return;
        }
        Scalar(s, v);
    }

    private static void Scalar(StringBuilder s, KVObject v)
    {
        switch (v.ValueType)
        {
            case KVValueType.Null:
                s.Append("null");
                break;
            case KVValueType.Boolean:
                s.Append(v.ToBoolean(CultureInfo.InvariantCulture) ? "true" : "false");
                break;
            case KVValueType.Int16 or KVValueType.Int32 or KVValueType.Int64:
                s.Append(v.ToInt64(CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                break;
            case KVValueType.UInt16 or KVValueType.UInt32 or KVValueType.UInt64:
                s.Append(v.ToUInt64(CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                break;
            case KVValueType.FloatingPoint or KVValueType.FloatingPoint64:
                s.Append(Number(v.ToDouble(CultureInfo.InvariantCulture)));
                break;
            case KVValueType.String:
                if (v.Flag != KVFlag.None)
                    s.Append(FlagName(v.Flag)).Append(':');
                s.Append('"').Append(v.ToString(CultureInfo.InvariantCulture).Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                break;
            default:
                throw new NotSupportedException($"KV3 text of a {v.ValueType}");
        }
    }

    private static string FlagName(KVFlag flag) => flag switch
    {
        KVFlag.Resource => "resource",
        KVFlag.ResourceName => "resource_name",
        KVFlag.Panorama => "panorama",
        KVFlag.SoundEvent => "soundevent",
        KVFlag.SubClass => "subclass",
        KVFlag.EntityName => "entity_name",
        _ => throw new NotSupportedException($"KV3 text flag {flag}"),
    };

    /// <summary>A double as 1800d7940 prints it.</summary>
    public static string Number(double d)
    {
        if (double.IsNaN(d))
            return "nan";
        if (double.IsInfinity(d))
            return d < 0 ? "-inf" : "inf";
        if (d != (double)(float)d)
            throw new NotSupportedException("a KV3 text double that is not a float (V_DoubleToString_NoExponent is not ported)");
        var text = Expand(((float)d).ToString("R", CultureInfo.InvariantCulture));
        return text.Contains('.') ? text : text + ".0";
    }

    // V_ExpandScientificNotation: "1.2E-05" as "0.000012", "1E+20" as "100000000000000000000".
    private static string Expand(string text)
    {
        var e = text.IndexOfAny(['E', 'e']);
        if (e < 0)
            return text;
        var negative = text[0] == '-';
        var mantissa = text[(negative ? 1 : 0)..e];
        var exponent = int.Parse(text[(e + 1)..], CultureInfo.InvariantCulture);
        var dot = mantissa.IndexOf('.');
        var digits = mantissa.Replace(".", "");
        var point = (dot < 0 ? mantissa.Length : dot) + exponent;
        string body;
        if (point <= 0)
            body = "0." + new string('0', -point) + digits;
        else if (point >= digits.Length)
            body = digits + new string('0', point - digits.Length);
        else
            body = digits[..point] + "." + digits[point..];
        return (negative ? "-" : "") + body;
    }
}
