using ValveKeyValue;

namespace Source2.Compiler;

/// <summary>
/// The FLCI block: where in the source text each value of a compiled KV3 tree came from.
///
/// <para>resourcecompiler writes it after DATA for a <c>.vdata</c> compile (its writer is
/// resourcecompiler 181eba570, which copies tables the KV3 text parser filled in). The block is
/// a generic-format KV3 tree:</para>
/// <code>
/// flc_file_list = [ "content/&lt;search path&gt;/&lt;relative source path&gt;" ]
/// flc_file_ids  = [ 0, ... ]          one per value
/// flc_lines     = [ ... ]             one per value, delta from the previous value's line
/// flc_columns   = [ ... ]             one per value, delta from the previous value's column
/// flc_count     = number of values
/// </code>
/// <para>A "value" is every node of the tree in document order: the root object, each member's
/// value, each array element. Its position is the first character of the value as written
/// (the <c>{</c> or <c>[</c> of a container, the flag of a flagged string such as
/// <c>resource:"..."</c>), with lines and columns counted from 1, a tab counting one column,
/// and the first delta taken from line 0, column 0. Columns are deltas even across a line
/// change. Measured: both RcReference <c>.vdata</c> probes (5 and 56 values, covering nested and
/// mixed arrays, objects in arrays, floats and a flagged reference) reproduce exactly. Not yet
/// read from the parser itself, and a source that includes another file (several entries in
/// <c>flc_file_list</c>) has no reference compile, so <see cref="Author"/> covers one file.</para>
/// </summary>
public static class Kv3LineColumns
{
    /// <summary>Every value's (line, column) in <paramref name="text"/>, in document order, or null
    /// when the text is not plain enough KV3 for this scanner to follow.</summary>
    public static List<(int Line, int Column)>? Scan(string text)
    {
        var s = new Scanner(text);
        try
        {
            s.Value();
            s.Skip();
            return s.Position == text.Length ? s.Found : null;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The FLCI tree for a single-file compile of <paramref name="text"/>, or null when the
    /// scan does not account for exactly <paramref name="valueCount"/> values (the parsed tree's
    /// node count), so a scanner gap can never write a map that points at the wrong lines.
    /// </summary>
    /// <param name="text">The source text as compiled.</param>
    /// <param name="sourcePath">The path RC lists, e.g. <c>content/csgo_addons/addon/scripts/x.vdata</c>.</param>
    /// <param name="valueCount">Nodes in the parsed tree, root included.</param>
    public static KVObject? Author(string text, string sourcePath, int valueCount)
    {
        if (Scan(text) is not { } found || found.Count != valueCount)
            return null;

        var files = KVObject.Array();
        files.Add(new KVObject(sourcePath));
        var ids = KVObject.Array();
        var lines = KVObject.Array();
        var columns = KVObject.Array();
        var (line, column) = (0, 0);
        foreach (var (l, c) in found)
        {
            ids.Add(new KVObject(0));
            lines.Add(new KVObject(l - line));
            columns.Add(new KVObject(c - column));
            (line, column) = (l, c);
        }

        var root = KVObject.Collection();
        root.Add("flc_file_list", files);
        root.Add("flc_file_ids", ids);
        root.Add("flc_lines", lines);
        root.Add("flc_columns", columns);
        root.Add("flc_count", new KVObject(found.Count));
        return root;
    }

    /// <summary>
    /// Give back their string type to values the source wrote in quotes. ValveKeyValue's KV3 text
    /// reader (0.71) types a quoted <c>"false"</c>, <c>"12"</c> or <c>"null"</c> as the literal it
    /// spells; resourcecompiler keeps it a string (stock <c>soundevents_from_tools.vsndevts_c</c>:
    /// <c>_auto_calc_vnsd_duration_ = "false"</c> is a STRING). Only one-line quoted values that
    /// came back as something other than a string are touched, with their flag kept, and nothing
    /// is touched when the scan does not line up with the tree. Returns how many were restored.
    /// </summary>
    public static int RestoreQuotedStrings(string text, KVObject root)
    {
        var s = new Scanner(text);
        try
        {
            s.Value();
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or FormatException)
        {
            return 0;
        }
        if (s.Quoted.Count != CountValues(root))
            return 0;
        var index = 0;
        var restored = 0;
        KVObject Walk(KVObject node)
        {
            var quoted = s.Quoted[index++];
            if (node.IsCollection || node.IsArray)
            {
                if (node.IsCollection)
                {
                    foreach (var key in node.Children.Select(c => c.Key).ToList())
                        node[key] = Walk(node[key]!);
                    return node;
                }
                // Array elements have no setter: rebuild the array.
                var array = KVObject.Array();
                foreach (var element in node.Values.ToList())
                    array.Add(Walk(element));
                array.Flag = node.Flag;
                return array;
            }
            if (quoted is null || node.ValueType == KVValueType.String)
                return node;
            restored++;
            return new KVObject(quoted) { Flag = node.Flag };
        }
        Walk(root);
        return restored;
    }

    /// <summary>Deepest nesting a KV3 text compile accepts, as the patched VRF binary reader does.</summary>
    public const int MaxDepth = 512;

    /// <summary>
    /// How deep <paramref name="text"/> nests its objects and arrays, counted without recursion and
    /// outside strings and comments. ValveKeyValue's text reader builds a tree of any depth, and
    /// everything after it (integer narrowing, reference collection, the writer) recurses per
    /// level, so 200,000 nested brackets ended the whole process with a stack overflow that no
    /// catch can stop. Callers refuse text deeper than <see cref="MaxDepth"/> before parsing.
    /// </summary>
    public static int NestingDepth(string text) => PreParse(text).Depth;

    /// <summary>
    /// What a KV3 text must be checked for before ValveKeyValue (0.71) parses it: how deep it
    /// nests (see <see cref="NestingDepth"/>), and where the first control character sits outside
    /// strings and comments, or -1. A semicolon, or any C0 control other than tab, line feed,
    /// vertical tab, form feed and carriage return, there sends the reader into an endless
    /// allocation (a 3 GB cap throws OutOfMemoryException; without one it took 12.7 GB): measured
    /// for U+0000 to U+001F and every ASCII punctuation mark, as a value separator, inside a key
    /// and as a value; ';' is the only printable one. Inside strings and comments they are
    /// harmless. Such text can never parse, so refusing it costs nothing.
    /// </summary>
    public static (int Depth, int ControlAt) PreParse(string text)
    {
        int depth = 0, max = 0, control = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                if (string.CompareOrdinal(text, i, "\"\"\"", 0, 3) == 0)
                {
                    var end = text.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 2;
                    continue;
                }
                for (i++; i < text.Length && text[i] != '"'; i++)
                    if (text[i] == '\\')
                        i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 1;
            }
            else if (c == '<' && string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
            {
                var end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
            }
            else if (c is '{' or '[')
            {
                max = Math.Max(max, ++depth);
            }
            else if (c is '}' or ']')
            {
                depth--;
            }
            else if ((c == ';' || (c < 0x20 && c is not ('\t' or '\n' or '\v' or '\f' or '\r'))) && control < 0)
            {
                control = i;
            }
        }
        return (max, control);
    }

    /// <summary>Nodes in a parsed tree, root included: what <see cref="Author"/> checks against.</summary>
    public static int CountValues(KVObject node)
    {
        var n = 1;
        if (node.IsCollection || node.IsArray)
            foreach (var child in node.Children)
                n += CountValues(child.Value);
        return n;
    }

    private sealed class Scanner(string text)
    {
        public readonly List<(int Line, int Column)> Found = [];
        /// <summary>Per value, the text between the quotes when it was written as a one-line
        /// quoted string, else null.</summary>
        public readonly List<string?> Quoted = [];
        public int Position;
        private int line = 1, column = 1, depth;

        private char At => text[Position];

        private void Advance(int count)
        {
            for (var k = 0; k < count; k++)
            {
                if (text[Position] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
                Position++;
            }
        }

        private void AdvanceTo(int end) => Advance(end - Position);

        private int IndexOrThrow(string what, int from)
        {
            var at = text.IndexOf(what, from, StringComparison.Ordinal);
            return at >= 0 ? at : throw new FormatException($"unterminated {what}");
        }

        /// <summary>Whitespace and the three comment forms.</summary>
        public void Skip()
        {
            while (Position < text.Length)
            {
                if (At is ' ' or '\t' or '\r' or '\n')
                    Advance(1);
                else if (string.CompareOrdinal(text, Position, "<!--", 0, 4) == 0)
                    AdvanceTo(IndexOrThrow("-->", Position + 4) + 3);
                else if (string.CompareOrdinal(text, Position, "//", 0, 2) == 0)
                    while (Position < text.Length && At != '\n')
                        Advance(1);
                else if (string.CompareOrdinal(text, Position, "/*", 0, 2) == 0)
                    AdvanceTo(IndexOrThrow("*/", Position + 2) + 2);
                else
                    break;
            }
        }

        public void Value()
        {
            Skip();
            Found.Add((line, column));
            Quoted.Add(null);
            var self = Quoted.Count - 1;
            // A flag ("resource:", "soundevent:", ...) is part of the value it marks.
            var flagEnd = Position;
            while (flagEnd < text.Length && (char.IsAsciiLetterOrDigit(text[flagEnd]) || text[flagEnd] == '_'))
                flagEnd++;
            if (flagEnd > Position && flagEnd < text.Length && text[flagEnd] == ':')
                AdvanceTo(flagEnd + 1);

            if (At is '{' or '[' && ++depth > MaxDepth)
                throw new FormatException("nested too deep");
            switch (At)
            {
                case '{':
                    Advance(1);
                    while (true)
                    {
                        Skip();
                        if (At == '}')
                        {
                            Advance(1);
                            depth--;
                            return;
                        }
                        Key();
                        Skip();
                        if (At != '=')
                            throw new FormatException("expected =");
                        Advance(1);
                        Value();
                        Separator();
                    }
                case '[':
                    Advance(1);
                    while (true)
                    {
                        Skip();
                        if (At == ']')
                        {
                            Advance(1);
                            depth--;
                            return;
                        }
                        Value();
                        Separator();
                    }
                case '#':
                    // Binary blob: #[ 00 01 ... ].
                    AdvanceTo(IndexOrThrow("]", Position) + 1);
                    return;
                case '"':
                    var open = Position;
                    String();
                    if (text[open + 1] != '"' || Position - open == 2)
                        Quoted[self] = text[(open + 1)..(Position - 1)];
                    return;
                default:
                    // A bare literal: number, true, false, null.
                    var end = Position;
                    while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not (',' or ']' or '}'))
                        end++;
                    if (end == Position)
                        throw new FormatException("empty value");
                    AdvanceTo(end);
                    return;
            }
        }

        private void Key()
        {
            if (At == '"')
            {
                String();
                return;
            }
            var end = Position;
            while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] is '_' or '.'))
                end++;
            if (end == Position)
                throw new FormatException("expected a key");
            AdvanceTo(end);
        }

        private void String()
        {
            if (string.CompareOrdinal(text, Position, "\"\"\"", 0, 3) == 0)
            {
                AdvanceTo(IndexOrThrow("\"\"\"", Position + 3) + 3);
                return;
            }
            var j = Position + 1;
            while (text[j] != '"')
                j += text[j] == '\\' ? 2 : 1;
            AdvanceTo(j + 1);
        }

        private void Separator()
        {
            Skip();
            if (Position < text.Length && At == ',')
                Advance(1);
        }
    }
}
