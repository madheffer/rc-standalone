using System.Globalization;
using System.Numerics;
using System.Text;

namespace Source2.Compiler;

/// <summary>
/// DMX in the keyvalues2 text encoding ("&lt;!-- dmx encoding keyvalues2 4
/// format vmap 40 --&gt;"), which Hammer can save a map as and resourcecompiler
/// reads as it reads binary. The result is the <see cref="DmxBinary.Document"/>
/// the binary reader gives for the same map: an element's "id" and "name"
/// are its header, not attributes; values take the binary reader's types; the
/// elements are listed in the order the text first defines them (the order
/// dmxconvert's binary output keeps, checked by DmxTextTests).
/// </summary>
public static class DmxText
{
    /// <summary>Whether the bytes start with a keyvalues2 DMX header.</summary>
    public static bool IsText(ReadOnlySpan<byte> bytes)
    {
        var head = Encoding.ASCII.GetString(bytes[..Math.Min(bytes.Length, 64)]);
        return head.StartsWith("<!-- dmx encoding keyvalues2", StringComparison.Ordinal);
    }

    /// <summary>Parse a keyvalues2 DMX file.</summary>
    public static DmxBinary.Document Read(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var newline = text.IndexOf('\n');
        var header = (newline < 0 ? text : text[..newline]).Trim();
        var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var encoding = Array.IndexOf(parts, "encoding");
        var format = Array.IndexOf(parts, "format");
        if (encoding < 0 || format < 0 || encoding + 2 >= parts.Length || format + 2 >= parts.Length)
            throw new InvalidDataException($"Not a DMX header: '{header}'.");
        var parser = new Parser(text, newline < 0 ? text.Length : newline + 1);
        var elements = new List<DmxBinary.Element>();
        var prefix = new List<DmxBinary.Element>();
        var byId = new Dictionary<Guid, DmxBinary.Element>();
        var pending = new List<(DmxBinary.Element Owner, string Name, int Index, Guid Id)>();
        while (parser.Next() is { } type)
        {
            var element = parser.Element(type, elements, byId, pending);
            if (type == "$prefix_element$")
            {
                // The binary file lists the prefix element among the elements too,
                // second, after the root, and again in its prefix section.
                elements.Remove(element);
                prefix.Add(element);
            }
        }
        if (prefix.Count > 0 && elements.Count > 0)
            elements.InsertRange(1, prefix);
        foreach (var (owner, name, index, id) in pending)
        {
            byId.TryGetValue(id, out var target);
            if (index < 0)
                owner.Attributes[name] = target;
            else if (owner.Attributes[name] is object?[] array)
                array[index] = target;
        }
        return new DmxBinary.Document(parts[encoding + 1], int.Parse(parts[encoding + 2], CultureInfo.InvariantCulture),
                                      parts[format + 1], int.Parse(parts[format + 2], CultureInfo.InvariantCulture),
                                      elements, prefix, 0);
    }

    private sealed class Parser(string text, int at)
    {
        private int _at = at;

        /// <summary>The next quoted string, or null at the end of the text.</summary>
        public string? Next()
        {
            Skip();
            if (_at >= text.Length)
                return null;
            if (text[_at] != '"')
                throw new InvalidDataException($"DMX text: expected a quoted string at offset {_at}.");
            var sb = new StringBuilder();
            _at++;
            while (_at < text.Length && text[_at] != '"')
            {
                if (text[_at] == '\\' && _at + 1 < text.Length)
                {
                    _at++;
                    sb.Append(text[_at] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => text[_at] });
                }
                else
                    sb.Append(text[_at]);
                _at++;
            }
            _at++;
            return sb.ToString();
        }

        private void Skip()
        {
            while (_at < text.Length)
            {
                if (char.IsWhiteSpace(text[_at]) || text[_at] == ',')
                    _at++;
                else if (text.AsSpan(_at).StartsWith("<!--"))
                {
                    var end = text.IndexOf("-->", _at, StringComparison.Ordinal);
                    _at = end < 0 ? text.Length : end + 3;
                }
                else
                    return;
            }
        }

        private char Peek()
        {
            Skip();
            return _at < text.Length ? text[_at] : '\0';
        }

        private void Expect(char c)
        {
            if (Peek() != c)
                throw new InvalidDataException($"DMX text: expected '{c}' at offset {_at}.");
            _at++;
        }

        /// <summary>An element body after its type: "{ attributes }".</summary>
        public DmxBinary.Element Element(string type, List<DmxBinary.Element> elements, Dictionary<Guid, DmxBinary.Element> byId,
                                         List<(DmxBinary.Element, string, int, Guid)> pending)
        {
            Expect('{');
            var attributes = new List<(string Name, string Type, object? Value, int At)>();
            string name = "";
            var id = Guid.Empty;
            // The element takes its place in the list when it opens, before any
            // element nested inside it.
            var slot = elements.Count;
            elements.Add(null!);
            var holder = new List<(string, int, Guid)>();
            while (Peek() != '}')
            {
                var attr = Next() ?? throw new InvalidDataException("DMX text: element ends early.");
                var attrType = Next() ?? throw new InvalidDataException("DMX text: attribute without a type.");
                if (attrType == "elementid")
                {
                    id = Guid.Parse(Next()!);
                    continue;
                }
                if (attr == "name" && attrType == "string")
                {
                    name = Next()!;
                    continue;
                }
                attributes.Add((attr, attrType, Value(attrType, elements, byId, pending, attr, holder), 0));
            }
            _at++;
            var element = CreateElement(type, name, id);
            foreach (var (attr, _, value, _) in attributes)
                element.Attributes[attr] = value;
            foreach (var (attr, index, target) in holder)
                pending.Add((element, attr, index, target));
            elements[slot] = element;
            if (id != Guid.Empty)
                byId[id] = element;
            return element;
        }

        private object? Value(string type, List<DmxBinary.Element> elements, Dictionary<Guid, DmxBinary.Element> byId,
                              List<(DmxBinary.Element, string, int, Guid)> pending, string attr, List<(string, int, Guid)> holder)
        {
            if (type.EndsWith("_array", StringComparison.Ordinal))
            {
                var scalar = type[..^"_array".Length];
                Expect('[');
                var values = new List<object?>();
                while (Peek() != ']')
                {
                    if (scalar == "element")
                    {
                        var head = Next()!;
                        if (Peek() == '{')
                            values.Add(Element(head, elements, byId, pending));
                        else
                        {
                            // "element" "<guid>": a reference by id.
                            var reference = Next()!;
                            if (Guid.TryParse(reference, out var g))
                                holder.Add((attr, values.Count, g));
                            values.Add(null);
                        }
                    }
                    else
                        values.Add(Scalar(scalar, Next()!));
                }
                _at++;
                return values.ToArray();
            }
            if (type == "element")
            {
                var reference = Next()!;
                if (Guid.TryParse(reference, out var g))
                    holder.Add((attr, -1, g));
                return null;
            }
            if (Peek() == '{')
                return Element(type, elements, byId, pending);
            return Scalar(type, Next()!);
        }

        private static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        private static float[] Floats(string s, int n)
        {
            var p = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != n)
                throw new InvalidDataException($"DMX text: '{s}' is not {n} numbers.");
            return [.. p.Select(F)];
        }

        private static object? Scalar(string type, string s)
        {
            switch (type)
            {
                case "int":
                    return int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
                case "float":
                    return F(s);
                case "bool":
                    return s.Trim() != "0";
                case "string":
                    return s;
                case "binary":
                    return Convert.FromHexString(string.Concat(s.Where(c => !char.IsWhiteSpace(c))));
                case "time":
                    return TimeSpan.FromSeconds(double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture));
                case "color":
                    return s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(x => byte.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                case "vector2":
                    {
                        var v = Floats(s, 2);
                        return new Vector2(v[0], v[1]);
                    }
                case "vector3":
                case "qangle":
                    {
                        var v = Floats(s, 3);
                        return new Vector3(v[0], v[1], v[2]);
                    }
                case "vector4":
                    {
                        var v = Floats(s, 4);
                        return new Vector4(v[0], v[1], v[2], v[3]);
                    }
                case "quaternion":
                    {
                        var v = Floats(s, 4);
                        return new Quaternion(v[0], v[1], v[2], v[3]);
                    }
                case "matrix":
                    {
                        var v = Floats(s, 16);
                        return new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
                    }
                case "uint64":
                    return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? ulong.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                        : ulong.Parse(s, CultureInfo.InvariantCulture);
                case "uint8":
                    return byte.Parse(s, CultureInfo.InvariantCulture);
                default:
                    throw new InvalidDataException($"DMX text: unknown attribute type '{type}'.");
            }
        }
    }

    private static DmxBinary.Element CreateElement(string type, string name, Guid id) => DmxBinary.NewElement(type, name, id);
}
