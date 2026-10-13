using System.Text;

namespace Source2.Compiler.Dmx;

/// <summary>An element of a DMX document in <c>keyvalues2</c> text: its class and its attributes,
/// in source order.</summary>
public sealed class DmxElement(string type)
{
    public string Type { get; } = type;
    public List<(string Name, DmxAttribute Value)> Attributes { get; } = [];

    public DmxAttribute? this[string name]
        => Attributes.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public string? String(string name) => this[name]?.Text;
    public bool Bool(string name, bool fallback = false) => this[name]?.Text is { } t ? t is "1" or "true" : fallback;
    public IReadOnlyList<DmxElement> Elements(string name) => this[name]?.ElementList ?? [];
}

/// <summary>An attribute: a scalar written as text (<c>"string"</c>, <c>"bool"</c>, <c>"int"</c>,
/// <c>"float"</c>, <c>"vector4"</c>...), a string array, an inline element or an element array.</summary>
public sealed record DmxAttribute(string Type, string? Text = null, IReadOnlyList<string>? TextList = null,
    DmxElement? Element = null, IReadOnlyList<DmxElement>? ElementList = null);

/// <summary>
/// Reads DMX <c>keyvalues2</c> / <c>keyvalues2_noids</c> text: the form <c>.vtex</c> sources and
/// other tool documents are saved in. Hostile input fails with <see cref="FormatException"/>
/// (bounded nesting, no unterminated strings).
/// </summary>
public static class Keyvalues2Text
{
    public const int MaxDepth = 64;

    /// <summary>The root element, after the <c>&lt;!-- dmx encoding ... --&gt;</c> header.</summary>
    public static DmxElement Parse(string text)
    {
        var t = new Tokens(text);
        var type = t.Next() ?? throw new FormatException("Empty keyvalues2 document.");
        var root = ReadElement(t, type, 0);
        if (t.Next() is { } extra)
            throw new FormatException($"Unexpected '{extra}' after the root element.");
        return root;
    }

    public static DmxElement Parse(byte[] utf8) => Parse(Encoding.UTF8.GetString(utf8));

    private static DmxElement ReadElement(Tokens t, string type, int depth)
    {
        if (depth > MaxDepth)
            throw new FormatException($"keyvalues2 nests deeper than {MaxDepth} elements.");
        t.Expect("{");
        var element = new DmxElement(type);
        while (true)
        {
            var name = t.Next() ?? throw new FormatException($"Unterminated element '{type}'.");
            if (name == "}")
                return element;
            var attrType = t.Next() ?? throw new FormatException($"Attribute '{name}' has no type.");
            DmxAttribute value;
            if (attrType == "element_array")
                value = new(attrType, ElementList: ReadElementArray(t, depth));
            else if (attrType.EndsWith("_array", StringComparison.Ordinal))
                value = new(attrType, TextList: ReadTextArray(t));
            else if (t.Peek() == "{")
                value = new("element", Element: ReadElement(t, attrType, depth + 1));
            else
                value = new(attrType, Text: t.Next() ?? throw new FormatException($"Attribute '{name}' has no value."));
            element.Attributes.Add((name, value));
        }
    }

    private static List<DmxElement> ReadElementArray(Tokens t, int depth)
    {
        t.Expect("[");
        var list = new List<DmxElement>();
        while (true)
        {
            var tok = t.Next() ?? throw new FormatException("Unterminated element array.");
            if (tok == "]")
                return list;
            if (tok == ",")
                continue;
            list.Add(ReadElement(t, tok, depth + 1));
        }
    }

    private static List<string> ReadTextArray(Tokens t)
    {
        t.Expect("[");
        var list = new List<string>();
        while (true)
        {
            var tok = t.Next() ?? throw new FormatException("Unterminated array.");
            if (tok == "]")
                return list;
            if (tok != ",")
                list.Add(tok);
        }
    }

    private sealed class Tokens(string s)
    {
        private int _i;
        private string? _peeked;

        public string? Peek() => _peeked ??= Read();

        public string? Next()
        {
            var r = Peek();
            _peeked = null;
            return r;
        }

        public void Expect(string tok)
        {
            var got = Next();
            if (got != tok)
                throw new FormatException($"Expected '{tok}' but found '{got ?? "end of file"}'.");
        }

        private string? Read()
        {
            while (_i < s.Length)
            {
                if (char.IsWhiteSpace(s[_i])) { _i++; continue; }
                if (s.AsSpan(_i).StartsWith("<!--"))
                {
                    var end = s.IndexOf("-->", _i, StringComparison.Ordinal);
                    _i = end < 0 ? s.Length : end + 3;
                    continue;
                }
                if (s.AsSpan(_i).StartsWith("//"))
                {
                    var end = s.IndexOf('\n', _i);
                    _i = end < 0 ? s.Length : end + 1;
                    continue;
                }
                break;
            }
            if (_i >= s.Length)
                return null;
            var c = s[_i];
            if (c is '{' or '}' or '[' or ']' or ',')
            {
                _i++;
                return c.ToString();
            }
            if (c != '"')
                throw new FormatException($"Unexpected character '{c}' at offset {_i}.");
            var sb = new StringBuilder();
            for (_i++; _i < s.Length; _i++)
            {
                if (s[_i] == '\\' && _i + 1 < s.Length)
                {
                    _i++;
                    sb.Append(s[_i] switch { 'n' => '\n', 't' => '\t', _ => s[_i] });
                    continue;
                }
                if (s[_i] == '"')
                {
                    _i++;
                    return sb.ToString();
                }
                sb.Append(s[_i]);
            }
            throw new FormatException("Unterminated string.");
        }
    }
}
