using System.Text.RegularExpressions;

namespace Source2.Compiler;

/// <summary>
/// The entity schema, read from the game's <c>.fgd</c> files.
///
/// <para>It is needed because Hammer stores every key as a string and the compiled
/// entity lump does not: <c>enabled "1"</c> becomes a boolean, <c>priority "0"</c>
/// becomes an integer, and a choices field stays a string. Nothing in the map
/// source says which is which, so a compiler that does not read the FGD writes a
/// lump the entity system rejects field by field.</para>
///
/// <para>Only what the compiler needs is parsed: each class, the classes it
/// inherits keys from, and the declared type of every key. Inputs, outputs, help
/// text, editor metadata and the tool-only blocks are skipped.</para>
/// </summary>
public sealed partial class FgdSchema
{
    /// <summary>The declared type of a key, reduced to what it means for a value.</summary>
    public enum FieldType
    {
        /// <summary>Anything the lump keeps as text, including choices.</summary>
        String,

        /// <summary>An <c>integer</c>.</summary>
        Integer,

        /// <summary>A <c>flags</c> field. The lump writes these UNSIGNED, which is
        /// the one place an entity value's width is not the ordinary integer rule.</summary>
        Flags,

        /// <summary>A <c>float</c>. The value is a 32-bit float widened to double.</summary>
        Float,

        /// <summary>A <c>boolean</c>, written "0" or "1" by Hammer.</summary>
        Boolean,

        /// <summary>A <c>vector</c> or <c>angle</c>: three floats in one string.</summary>
        Vector,

        /// <summary>A <c>color255</c>: three or four integers in one string.</summary>
        Color,

        /// <summary>
        /// A key that names another entity (<c>target_source</c>,
        /// <c>target_destination</c>). It is a string like any other, but it is the
        /// set the compile rewrites when the map asks for entity-name fixup.
        /// </summary>
        EntityName,
    }

    /// <summary>A key as the schema declares it.</summary>
    /// <param name="Name">The key, spelled as the FGD spells it.</param>
    /// <param name="Type">What the value means.</param>
    /// <param name="Default">The default the FGD states, or null when it states none.</param>
    /// <param name="Removed">The class declared the key as <c>remove_key</c>, which
    /// takes it out of the schema entirely - no type, no default, not inherited.</param>
    public sealed record Key(string Name, FieldType Type, string? Default, bool Removed = false);

    private readonly Dictionary<string, Class> _classes = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Class(string Name, List<string> Bases, Dictionary<string, Key> Keys);

    /// <summary>Every class name the schema knows.</summary>
    public IEnumerable<string> ClassNames => _classes.Keys;

    /// <summary>
    /// Load <paramref name="path"/> and everything it <c>@include</c>s. Includes
    /// resolve beside the including file first, then beside the root, which is how
    /// a mod fgd picks up the engine's.
    /// </summary>
    public static FgdSchema Load(string path, IEnumerable<string>? searchPaths = null)
    {
        var schema = new FgdSchema();
        var roots = new List<string> { Path.GetDirectoryName(Path.GetFullPath(path))! };
        roots.AddRange(searchPaths ?? []);
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        schema.LoadInto(Path.GetFullPath(path), roots, loaded);
        return schema;
    }

    /// <summary>
    /// The type of <paramref name="key"/> on <paramref name="className"/>, following
    /// base classes. Null when the schema has never heard of it, which is a real
    /// answer: a key with no declared type is one the compiler must leave as a string.
    /// </summary>
    public FieldType? TypeOf(string className, string key) => KeyOf(className, key)?.Type;

    /// <summary>The key as declared, following base classes, or null when unknown.</summary>
    public Key? KeyOf(string className, string key)
        => Lookup(className, key, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Every key a class carries, its own first and then its bases', which is the
    /// set resourcecompiler writes into an entity whether or not the source
    /// mentions them.
    /// </summary>
    public IReadOnlyList<Key> KeysOf(string className)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new List<Key>();
        Collect(className, keys, seen, visited);
        return keys;
    }

    private void Collect(string className, List<Key> keys, HashSet<string> seen, HashSet<string> visited)
    {
        if (!visited.Add(className) || !_classes.TryGetValue(className, out var cls))
            return;
        foreach (var key in cls.Keys.Values)
            if (seen.Add(key.Name) && !key.Removed)
                keys.Add(key);
        foreach (var baseName in cls.Bases)
            Collect(baseName, keys, seen, visited);
    }

    private Key? Lookup(string className, string key, HashSet<string> seen)
    {
        if (!seen.Add(className) || !_classes.TryGetValue(className, out var cls))
            return null;
        if (cls.Keys.TryGetValue(key, out var declared))
            return declared.Removed ? null : declared;
        foreach (var baseName in cls.Bases)
            if (Lookup(baseName, key, seen) is { } inherited)
                return inherited;
        return null;
    }

    private void LoadInto(string path, List<string> roots, HashSet<string> loaded)
    {
        if (!loaded.Add(path) || !File.Exists(path))
            return;

        var text = StripComments(File.ReadAllText(path));
        foreach (Match include in IncludeRegex().Matches(text))
        {
            var name = include.Groups[1].Value;
            var resolved = roots.Select(r => Path.Combine(r, name)).FirstOrDefault(File.Exists);
            if (resolved is not null)
                LoadInto(Path.GetFullPath(resolved), roots, loaded);
        }

        foreach (Match cls in ClassRegex().Matches(text))
            ParseClass(cls, text);
    }

    /// <summary>
    /// Blank out <c>//</c> comments, keeping the line structure.
    ///
    /// <para>Not cosmetic: csgo.fgd comments OUT a block of env_sky's fog keys, and
    /// a comment carrying a bracket unbalances the scan that finds a class body, so
    /// keys bleed from one class into the next. Both showed up as entities with keys
    /// Valve's compile does not write.</para>
    /// </summary>
    private static string StripComments(string text)
    {
        var buffer = text.ToCharArray();
        var inString = false;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] == '"')
                inString = !inString;
            else if (!inString && buffer[i] == '/' && i + 1 < buffer.Length && buffer[i + 1] == '/')
                while (i < buffer.Length && buffer[i] != '\n')
                    buffer[i++] = ' ';
        }
        return new string(buffer);
    }

    private void ParseClass(Match kind, string text)
    {
        // The header cannot be matched with a regex: it carries metadata blocks
        // that contain '=' themselves (entity_tool_name = "..."), so the class
        // name has to be found by scanning past balanced brackets and strings.
        var (headerEnd, name) = ScanHeader(text, kind.Index + kind.Length);
        if (name is null)
            return;

        var head = text[(kind.Index + kind.Length)..headerEnd];
        var bases = BaseRegex().Matches(head)
            .SelectMany(m => m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();

        var body = BodyAfter(text, headerEnd);
        var keys = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);
        foreach (Match key in KeyRegex().Matches(body))
        {
            var keyName = key.Groups[1].Value;
            var declaredType = key.Groups[2].Value;
            var declared = new Key(keyName, MapType(declaredType),
                                   DefaultOf(body, key.Index + key.Length),
                                   Removed: declaredType.Equals("remove_key", StringComparison.OrdinalIgnoreCase));
            // A key can be redeclared by a derived class; the first declaration in
            // the file wins here because the derived class is parsed as its own.
            keys.TryAdd(keyName, declared);
        }

        // Two ways a mod fgd revisits a class, and they behave differently.
        //
        // @OverrideClass MERGES: csgo.fgd overrides light_environment to remove
        // five occlusion keys while the class keeps everything lights_base gives
        // it. A plain @PointClass REDECLARES: csgo.fgd restates env_sky with its
        // fog block commented out, and Valve's compile writes none of those keys,
        // so the earlier declaration has to go rather than merge.
        if (kind.Value.Equals("@OverrideClass", StringComparison.OrdinalIgnoreCase)
            && _classes.TryGetValue(name, out var existing))
        {
            foreach (var (keyName, declared) in keys)
                existing.Keys[keyName] = declared;
            foreach (var b in bases.Where(b => !existing.Bases.Contains(b, StringComparer.OrdinalIgnoreCase)))
                existing.Bases.Add(b);
            return;
        }
        _classes[name] = new Class(name, bases, keys);
    }

    /// <summary>
    /// Walk from just after <c>@PointClass</c> to the <c>=</c> that introduces the
    /// class name, ignoring any that sit inside brackets or quotes, and return the
    /// name after it.
    /// </summary>
    private static (int End, string? Name) ScanHeader(string text, int from)
    {
        var depth = 0;
        for (var i = from; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                i = text.IndexOf('"', i + 1);
                if (i < 0) break;
            }
            else if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']') depth--;
            else if (c == '=' && depth == 0)
            {
                var j = i + 1;
                while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                var start = j;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
                return (i, j > start ? text[start..j] : null);
            }
            else if (c == '@' && depth == 0 && i > from)
            {
                break;      // the next declaration started; this one had no name
            }
        }
        return (from, null);
    }

    /// <summary>The bracketed body that follows a class header, brace-matched so a
    /// nested choices block does not end it early.</summary>
    private static string BodyAfter(string text, int from)
    {
        var open = text.IndexOf('[', from);
        if (open < 0)
            return string.Empty;
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '[') depth++;
            else if (text[i] == ']' && --depth == 0)
                return text[(open + 1)..i];
        }
        return text[(open + 1)..];
    }

    /// <summary>
    /// The default from a key declaration: <c>name(type) [meta] : "Label" : default
    /// : "help"</c>, so the second colon-separated field after the type. Quoted or
    /// bare, and absent on plenty of keys.
    /// </summary>
    private static string? DefaultOf(string body, int from)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 0;
        for (var i = from; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '"')
            {
                var end = body.IndexOf('"', i + 1);
                if (end < 0) break;
                current.Append(body, i + 1, end - i - 1);
                i = end;
                continue;
            }
            if (c is '[' or '(' or '{') { depth++; continue; }
            if (c is ']' or ')' or '}') { depth--; continue; }
            if (depth > 0) continue;
            if (c == ':') { fields.Add(current.ToString().Trim()); current.Clear(); continue; }
            // A declaration ends at its line, or where a choices list begins.
            if (c is '\n' or '=') break;
            current.Append(c);
        }
        fields.Add(current.ToString().Trim());
        // fields[0] is whatever sat between the type and the first colon (the
        // metadata block is skipped above), fields[1] the label, fields[2] the default.
        return fields.Count > 2 && fields[2].Length > 0 ? fields[2] : null;
    }

    private static FieldType MapType(string declared) => declared.ToLowerInvariant() switch
    {
        "integer" => FieldType.Integer,
        "flags" => FieldType.Flags,
        "float" => FieldType.Float,
        "boolean" => FieldType.Boolean,
        "vector" or "angle" or "vector4" or "origin" => FieldType.Vector,
        "color255" or "color255alpha" or "color1" => FieldType.Color,
        // remove_key is handled as a REMOVAL rather than a type; see Key.Removed.
        "remove_key" => FieldType.String,
        "target_source" or "target_destination" or "target_name_or_class" => FieldType.EntityName,
        _ => FieldType.String,
    };

    [GeneratedRegex(@"@include\s+""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex IncludeRegex();

    // Only the "@PointClass" token. The rest of the header is scanned rather than
    // matched, because a metadata block inside it carries '=' of its own.
    [GeneratedRegex(@"@\w*Class\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClassRegex();

    [GeneratedRegex(@"base\s*\(([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex BaseRegex();

    // "priority(integer) : "Spawn Priority" : 0" - and not an input/output line.
    [GeneratedRegex(@"^[ \t]*(?!input\b|output\b)([A-Za-z_][\w]*)\s*\(\s*(\w+)\s*\)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex KeyRegex();
}
