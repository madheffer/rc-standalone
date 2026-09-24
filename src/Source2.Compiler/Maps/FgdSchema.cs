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
    /// <summary>
    /// What a key's declared type means for its value, grouped the way the lump
    /// writer's switch groups resourcecompiler's type ids (FUN_180fa84e0). The
    /// exact id is on <see cref="Key.TypeId"/>.
    /// </summary>
    public enum FieldType
    {
        /// <summary>Anything written as text: string, choices, sound, and every
        /// type the writer has no case for.</summary>
        String,

        /// <summary><c>integer</c>, <c>int</c> and <c>intchoices</c>: V_atoi.</summary>
        Integer,

        /// <summary><c>node_id</c>: V_StringToInt32.</summary>
        Int32,

        /// <summary><c>flags</c>: V_StringToUint32, written unsigned.</summary>
        Flags,

        /// <summary><c>float</c> and <c>floatchoices</c>: a 32-bit float.</summary>
        Float,

        /// <summary><c>boolean</c> and <c>bool</c>: "true", or a nonzero V_atoi.</summary>
        Boolean,

        /// <summary><c>vector2d</c>: two floats.</summary>
        Vector2,

        /// <summary><c>vector</c>: three floats.</summary>
        Vector,

        /// <summary><c>angle</c>: three floats read as a QAngle.</summary>
        Angle,

        /// <summary><c>vector4d</c>: four floats.</summary>
        Vector4,

        /// <summary><c>color255</c> and <c>color255alpha</c>: three bytes.</summary>
        Color,

        /// <summary>A resource path, with the extension its type names.</summary>
        Resource,

        /// <summary><c>kv3</c>, which the key writer skips entirely.</summary>
        Kv3,

        /// <summary>
        /// A key that names another entity (<c>target_source</c>,
        /// <c>target_destination</c>, <c>target_name_or_class</c>,
        /// <c>filterclass</c>). Text like any other, but it is the set the compile
        /// rewrites when the map asks for entity-name fixup.
        /// </summary>
        EntityName,
    }

    /// <summary>A key as the schema declares it.</summary>
    /// <param name="Name">The key, spelled as its declaration spells it.</param>
    /// <param name="Type">What the value means.</param>
    /// <param name="Default">The default the FGD states, or null when it states none.</param>
    /// <param name="TypeId">resourcecompiler's own id for the declared type.</param>
    /// <param name="Extension">For a resource, the extension its type names
    /// (<c>studio</c> is vmdl, <c>resource:particle</c> is vpcf); empty otherwise.</param>
    /// <param name="WriteToPathKey">For a path node class's key, the path key its
    /// values are collected into (<c>pin_enabled</c> writes
    /// <c>pathNodePinsEnabled</c>); null otherwise.</param>
    public sealed record Key(string Name, FieldType Type, string? Default, int TypeId = -1, string Extension = "",
                             string? WriteToPathKey = null)
    {
        /// <summary>A flags key's bits and their descriptions, in declaration order.</summary>
        public IReadOnlyList<(uint Bit, string Name)> Flags { get; init; } = [];
    }

    private readonly Dictionary<string, Class> _classes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _solid = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Classes in declaration order, which is the order they are finalized in.</summary>
    private readonly List<Class> _order = [];

    /// <summary>
    /// One class. <paramref name="Own"/> is what its body and any
    /// <c>@OverrideClass</c> declared; <see cref="Flat"/> is the finalized list,
    /// its bases' keys and then its own, which is every lookup's answer and the
    /// order its defaults are filled in.
    /// </summary>
    private sealed record Class(
        string Name, List<string> Bases, List<Key> Own, List<KeyValuePair<string, string>> GameKeys,
        HashSet<string> Flags, List<TemplateLump> TemplateLumps, Dictionary<string, string> Metadata)
    {
        public List<Key> Flat { get; set; } = [];

        /// <summary>The metadata flags with the bases' merged in.</summary>
        public HashSet<string> FlatFlags { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The metadata values with the bases' merged in, the class's own
        /// winning.</summary>
        public Dictionary<string, string> FlatMetadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A string value of the class's metadata, following base classes:
    /// path_particle_rope_clientside declares no path_node_class and inherits
    /// PathParticleRopeBase's, which is why Valve writes its supports_loop key
    /// closed_loop.
    /// </summary>
    public string? MetadataOf(string className, string key)
        => _classes.TryGetValue(className, out var cls) && cls.FlatMetadata.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Whether an entity of the class has the spawnflag described as
    /// <paramref name="description"/> (case blind) set (FUN_180f34460): the
    /// first choice of that name decides, and a bit of 0 never matches.
    /// </summary>
    public bool HasSpawnflag(string className, long spawnflags, string description)
    {
        foreach (var (bit, name) in KeyOf(className, "spawnflags")?.Flags ?? [])
            if (name.Equals(description, StringComparison.OrdinalIgnoreCase))
                return bit != 0 && ((uint)spawnflags & bit) == bit;
        return false;
    }

    /// <summary>A flags key's choices: <c>bit : "description" : default</c> entries of the bracket after the key.</summary>
    private static IReadOnlyList<(uint Bit, string Name)> FlagsOf(string declaration, string body, int at)
    {
        if (!declaration.Trim().Equals("flags", StringComparison.OrdinalIgnoreCase))
            return [];
        var open = body.IndexOf('[', at);
        var eq = body.IndexOf('=', at);
        if (open < 0 || eq < 0 || eq > open)
            return [];
        var close = body.IndexOf(']', open);
        if (close < 0)
            return [];
        return [.. Regex.Matches(body[(open + 1)..close], @"(\d+)\s*:\s*""([^""]*)""")
            .Select(m => (uint.Parse(m.Groups[1].Value), m.Groups[2].Value))];
    }

    /// <summary>Whether the class is a <c>@PathNodeClass</c>.</summary>
    public bool IsPathNodeClass(string className) => _pathNodes.Contains(className);

    private readonly HashSet<string> _pathNodes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One entry of a class's <c>create_entity_template_lumps</c> metadata: the
    /// lump the compile builds out of the entities the class names.
    /// </summary>
    /// <param name="Mode"><c>SingleTemplate</c> (members at the origin, named by
    /// <paramref name="SourceKey"/>) or <c>PointTemplate</c> (members in the
    /// class's local space, named by Template01 to Template128). <c>None</c> and
    /// <c>Children</c> build nothing here.</param>
    /// <param name="SourceKey">The key naming a SingleTemplate's member.</param>
    /// <param name="WorldKey">The key the map's name is written to.</param>
    /// <param name="LumpKey">The key the lump's name is written to, and the part
    /// of that name after the '#'.</param>
    public sealed record TemplateLump(string Mode, string SourceKey, string WorldKey, string LumpKey);

    /// <summary>The template lumps a class's metadata asks for, in order.</summary>
    public IReadOnlyList<TemplateLump> TemplateLumpsOf(string className)
        => _classes.TryGetValue(className, out var cls) ? cls.TemplateLumps : [];

    /// <summary>
    /// Whether the class is declared <c>@SolidClass</c>, which is to say it IS its
    /// brushes.
    ///
    /// <para>One of those with no brushes is not compiled at all.
    /// ze_doom_p2_c_gameplay carries 43 of them, ten func_button and ten
    /// trigger_hurt among others, and Valve's compile ships none of the 43 in any
    /// lump. The other three maps measured have none, which is why this only
    /// surfaced once the corpus grew.</para>
    /// </summary>
    public bool IsSolidClass(string className) => _solid.Contains(className);

    /// <summary>
    /// Whether the class is <paramref name="baseName"/> or inherits it through
    /// any chain of bases (FUN_180dd2500).
    /// </summary>
    public bool Inherits(string className, string baseName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>([className]);
        while (stack.Count > 0)
        {
            var at = stack.Pop();
            if (at.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (seen.Add(at) && _classes.TryGetValue(at, out var cls))
                foreach (var b in cls.Bases)
                    stack.Push(b);
        }
        return false;
    }

    /// <summary>
    /// Whether the class's <c>metadata</c> block sets this boolean flag.
    ///
    /// <para>Two of them decide whether an entity reaches the compiled lump at
    /// all. <c>static_prop</c> marks prop_static, which the compile bakes into the
    /// world rather than shipping; <c>editor_only</c> marks the path node classes,
    /// which exist to carry a path's shape in Hammer. Both still consume a
    /// compile_source_id, so they are filtered when the lump is written and not
    /// when the source is walked.</para>
    /// </summary>
    public bool HasFlag(string className, string flag)
        => _classes.TryGetValue(className, out var cls) && cls.FlatFlags.Contains(flag);

    /// <summary>
    /// The key/value pairs a class's <c>class_game_keys</c> metadata says every
    /// entity of that class ships with.
    ///
    /// <para>This is how a point prefab works, and it is declared rather than
    /// discovered: <c>counterterrorist_team_intro</c> is an ordinary
    /// <c>@PointClass</c> whose metadata carries
    /// <c>isPointPrefab true</c> and <c>targetMapName
    /// "prefabs/misc/counterterrorist_team_intro"</c>, which is exactly what Valve's
    /// compile writes into the entity. Nothing has to search a content tree.</para>
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> GameKeysOf(string className)
        => _classes.TryGetValue(className, out var cls) ? cls.GameKeys : [];

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
        schema.FinalizeClasses();
        return schema;
    }

    /// <summary>
    /// The type of <paramref name="key"/> on <paramref name="className"/>, following
    /// base classes. Null when the schema has never heard of it, which is a real
    /// answer: a key with no declared type is one the compiler must leave as a string.
    /// </summary>
    public FieldType? TypeOf(string className, string key) => KeyOf(className, key)?.Type;

    /// <summary>The key as declared, following base classes, or null when unknown.
    /// The first match in the finalized list wins, case-insensitively
    /// (FUN_180dd5550).</summary>
    public Key? KeyOf(string className, string key)
        => _classes.TryGetValue(className, out var cls)
            ? cls.Flat.FirstOrDefault(k => k.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>
    /// Every key a class carries, in the finalized order: each base's keys in the
    /// order the bases are listed, then the class's own, a redeclared key keeping
    /// the place it first had. That is the order the compile fills in defaults.
    /// </summary>
    public IReadOnlyList<Key> KeysOf(string className)
        => _classes.TryGetValue(className, out var cls) ? cls.Flat : [];

    /// <summary>
    /// resourcecompiler's AddVariable (FUN_180dd08c0). A key already present is
    /// replaced where it stands, or taken out when the new declaration is
    /// <c>remove_key</c>; anything else is appended, a <c>remove_key</c> for a key
    /// the list does not hold included, so that it removes the key when the
    /// class's bases are merged in ahead of it.
    /// </summary>
    private static void AddVariable(List<Key> list, Key key)
    {
        var at = list.FindIndex(k => k.Name.Equals(key.Name, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
            list.Add(key);
        else if (key.TypeId == RemoveKey)
            list.RemoveAt(at);
        else
            list[at] = key;
    }

    /// <summary>
    /// Finalize every class in declaration order (FUN_180dcc5e0, FUN_180dd1110):
    /// the bases' finalized keys first, in the order the bases are listed, then the
    /// class's own. A base is always declared before a class that names it, so it
    /// is final by then.
    /// </summary>
    private void FinalizeClasses()
    {
        foreach (var cls in _order)
        {
            var flat = new List<Key>();
            foreach (var baseName in cls.Bases)
                if (_classes.TryGetValue(baseName, out var parent))
                    foreach (var key in parent.Flat)
                        AddVariable(flat, key);
            foreach (var key in cls.Own)
                AddVariable(flat, key);
            cls.Flat = flat;

            foreach (var baseName in cls.Bases)
                if (_classes.TryGetValue(baseName, out var parent))
                {
                    cls.FlatFlags.UnionWith(parent.FlatFlags);
                    foreach (var (k, v) in parent.FlatMetadata)
                        cls.FlatMetadata[k] = v;
                }
            cls.FlatFlags.UnionWith(cls.Flags);
            foreach (var (k, v) in cls.Metadata)
                cls.FlatMetadata[k] = v;
        }
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

        // Declarations and @exclude apply in file order. csgo.fgd excludes forty
        // base classes (beam_spotlight, env_sprite_oriented, env_texturetoggle,
        // path_particle_rope among them), so the compiler knows nothing of them
        // and writes their entities' keys as the plain strings the source holds;
        // it excludes env_sky too and then declares its own.
        var events = ClassRegex().Matches(text).Select(m => (m.Index, Class: m, Exclude: (Match?)null))
            .Concat(ExcludeRegex().Matches(text).Select(m => (m.Index, Class: (Match?)null!, Exclude: (Match?)m)))
            .OrderBy(e => e.Index);
        foreach (var (_, cls, exclude) in events)
        {
            if (exclude is not null)
            {
                if (_classes.Remove(exclude.Groups[1].Value, out var gone))
                    _order.Remove(gone);
                _solid.Remove(exclude.Groups[1].Value);
            }
            else
                ParseClass(cls, text);
        }
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

        // The body's keys go through AddVariable as they are read, so a key the
        // class states twice keeps its first place and its last declaration. A key
        // whose type resourcecompiler does not know fails the class, which is then
        // not loaded at all (FUN_180dd7810, FUN_180dcd6b0).
        var body = BodyAfter(text, headerEnd);
        var own = new List<Key>();
        foreach (Match key in KeyRegex().Matches(body))
        {
            if (TypeOfDeclaration(key.Groups[2].Value) is not { } type)
                return;
            AddVariable(own, new Key(key.Groups[1].Value, type.Type, DefaultOf(body, key.Index + key.Length),
                                     type.Id, type.Extension, WriteToPathKeyOf(body, key.Index + key.Length))
                             { Flags = FlagsOf(key.Groups[2].Value, body, key.Index + key.Length) });
        }

        // @OverrideClass merges into the class it names (FUN_180dd1680): each key
        // replaces the one it matches in place, a remove_key takes it out, and the
        // rest are appended. It adds no bases. A second plain declaration of a
        // class is an error to resourcecompiler and changes nothing; csgo.fgd's
        // restated env_sky is legal only because an @exclude removed the first.
        var gameKeys = GameKeysIn(head);
        var flags = MetadataFlagsIn(head);
        if (kind.Value.Equals("@OverrideClass", StringComparison.OrdinalIgnoreCase))
        {
            if (!_classes.TryGetValue(name, out var existing))
                return;
            foreach (var key in own)
                AddVariable(existing.Own, key);
            existing.GameKeys.AddRange(gameKeys.Where(g => !existing.GameKeys.Any(
                e => e.Key.Equals(g.Key, StringComparison.OrdinalIgnoreCase))));
            existing.Flags.UnionWith(flags);
            foreach (var (k, v) in MetadataValuesIn(head))
                existing.Metadata[k] = v;
            return;
        }
        if (_classes.ContainsKey(name))
            return;
        if (kind.Value.Equals("@SolidClass", StringComparison.OrdinalIgnoreCase))
            _solid.Add(name);
        if (kind.Value.Equals("@PathNodeClass", StringComparison.OrdinalIgnoreCase))
            _pathNodes.Add(name);
        var cls = new Class(name, bases, own, gameKeys, flags, TemplateLumpsIn(head), MetadataValuesIn(head));
        _classes[name] = cls;
        _order.Add(cls);
    }

    /// <summary>
    /// The create_entity_template_lumps entries in a class header's metadata:
    /// <c>[ { lumpMode = "PointTemplate" targetWorldKey = "worldName"
    /// targetLumpKey = "entityLumpName" } ]</c> on point_template.
    /// </summary>
    private static List<TemplateLump> TemplateLumpsIn(string head)
    {
        var block = TemplateLumpsBlockRegex().Match(head);
        if (!block.Success)
            return [];
        var lumps = new List<TemplateLump>();
        foreach (Match entry in BraceRegex().Matches(block.Groups[1].Value))
        {
            var fields = MetadataStringRegex().Matches(entry.Groups[1].Value)
                .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.OrdinalIgnoreCase);
            lumps.Add(new TemplateLump(
                fields.GetValueOrDefault("lumpMode", ""), fields.GetValueOrDefault("sourceKey", ""),
                fields.GetValueOrDefault("targetWorldKey", ""), fields.GetValueOrDefault("targetLumpKey", "")));
        }
        return lumps;
    }

    /// <summary>The quoted string values of a class header's metadata block, such
    /// as <c>path_node_class = "path_node_cable"</c>.</summary>
    private static Dictionary<string, string> MetadataValuesIn(string head)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var block = MetadataBlockRegex().Match(head);
        if (!block.Success)
            return values;
        foreach (Match m in MetadataStringRegex().Matches(head[(block.Index + block.Length)..]))
            values.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        return values;
    }

    /// <summary>A key's <c>write_to_path_key</c>, from the braces that may follow
    /// its <c>name(type)</c>.</summary>
    private static string? WriteToPathKeyOf(string body, int from)
    {
        var at = from;
        while (at < body.Length && body[at] is ' ' or '\t')
            at++;
        if (at >= body.Length || body[at] != '{')
            return null;
        var end = body.IndexOf('}', at);
        if (end < 0)
            return null;
        var m = WriteToPathKeyRegex().Match(body, at, end - at);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>The class_game_keys pairs in a class header's metadata block.</summary>
    private static List<KeyValuePair<string, string>> GameKeysIn(string head)
    {
        var block = GameKeysBlockRegex().Match(head);
        if (!block.Success)
            return [];
        return [.. GameKeyRegex().Matches(block.Groups[1].Value)
                    .Select(m => new KeyValuePair<string, string>(
                        m.Groups[1].Value,
                        m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[3].Value))];
    }

    /// <summary>
    /// The flags a class header's <c>metadata</c> block sets to true.
    ///
    /// <para>The block cannot be matched with a regex because it nests: prop_static
    /// carries <c>model_archetypes = [ "static_prop_model" ]</c> beside its flags,
    /// so the closing brace has to be found by counting.</para>
    /// </summary>
    private static HashSet<string> MetadataFlagsIn(string head)
    {
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var block = MetadataBlockRegex().Match(head);
        if (!block.Success)
            return flags;

        var at = block.Index + block.Length;
        var depth = 1;
        while (at < head.Length && depth > 0)
        {
            if (head[at] == '{') depth++;
            else if (head[at] == '}') depth--;
            at++;
        }
        foreach (Match flag in MetadataFlagRegex().Matches(head[(block.Index + block.Length)..(at - 1)]))
            flags.Add(flag.Groups[1].Value);
        return flags;
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

    /// <summary>resourcecompiler's id for <c>remove_key</c>.</summary>
    private const int RemoveKey = 0x42;

    /// <summary>
    /// A declared type as resourcecompiler resolves it (FUN_180dda5c0): six
    /// aliases first, then the type table, and a <c>resource:</c> qualifier named
    /// through a short list of resource kinds or taken as the extension itself.
    /// Null for a type the compiler does not know, which fails the class.
    /// </summary>
    private static (FieldType Type, int Id, string Extension)? TypeOfDeclaration(string declared)
    {
        var colon = declared.IndexOf(':');
        var head = colon < 0 ? declared : declared[..colon];
        var qualifier = colon < 0 ? "" : declared[(colon + 1)..];

        if (Aliases.TryGetValue(head, out var alias))
            return (Meaning(alias.Id), alias.Id, alias.Extension);
        if (!TypeIds.TryGetValue(head, out var id))
            return null;
        // Only a resource's qualifier is an extension; vdata_choice and array
        // carry theirs for the editor.
        var extension = id is 0x20 or 0x21
            ? ResourceKinds.GetValueOrDefault(qualifier, qualifier)
            : "";
        return (Meaning(id), id, extension);
    }

    /// <summary>
    /// The case of the lump writer's switch (FUN_180fa84e0) each type id falls
    /// into; an id the switch has no case for is written as text.
    /// </summary>
    private static FieldType Meaning(int id) => id switch
    {
        0x1e => FieldType.Boolean,
        4 or 8 => FieldType.Integer,
        0x1f => FieldType.Int32,
        10 => FieldType.Flags,
        0x12 or 9 => FieldType.Float,
        0x4d => FieldType.Vector2,
        0xe => FieldType.Vector,
        0 => FieldType.Angle,
        0x4e => FieldType.Vector4,
        0xb or 0xc => FieldType.Color,
        0x20 or 0x21 => FieldType.Resource,
        0x44 => FieldType.Kv3,
        // The name types, which entity-name fixup rewrites. filterclass names a
        // FILTER ENTITY: Valve writes filtername = "[PR#]humans" on ze_hold_em_p's
        // trigger_multiple, trigger_hurt and trigger_teleport.
        1 or 2 or 3 or 0x11 => FieldType.EntityName,
        _ => FieldType.String,
    };

    /// <summary>The aliases FUN_180dda5c0 tries before the table, with the type
    /// and extension each stands for (FUN_18009fe10).</summary>
    private static readonly Dictionary<string, (int Id, string Extension)> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["decal"] = (0x20, "vmat"),
            ["sprite"] = (0x20, "vmat"),
            ["studio"] = (0x20, "vmdl"),
            ["material"] = (0x20, "vmat"),
            ["particlesystem"] = (0x20, "vpcf"),
            ["ehandle"] = (1, ""),
        };

    /// <summary>The resource kinds a <c>resource:</c> qualifier may name, and
    /// their extensions. Anything else is taken as the extension itself.</summary>
    private static readonly Dictionary<string, string> ResourceKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["texture"] = "vtex", ["material"] = "vmat", ["mesh"] = "vmesh", ["particle"] = "vpcf",
        ["model"] = "vmdl", ["collisionmesh"] = "vphzc", ["sky"] = "vsky", ["map"] = "vmap",
        ["postprocessing"] = "vpost", ["snapshot"] = "vsnap",
    };

    /// <summary>resourcecompiler's type table, name to id, read out of the
    /// 2026-09-23 build.</summary>
    private static readonly Dictionary<string, int> TypeIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["angle"] = 0x0, ["choices"] = 0x7, ["intchoices"] = 0x8, ["floatchoices"] = 0x9,
        ["suggestions"] = 0x48, ["flagchoices"] = 0x49, ["color255"] = 0xb, ["color255alpha"] = 0xc,
        ["flags"] = 0xa, ["integer"] = 0x4, ["int"] = 0x4, ["sound"] = 0xd, ["string"] = 0x5,
        ["string_instanced"] = 0x6, ["text_block"] = 0x30, ["target_destination"] = 0x1,
        ["target_source"] = 0x3, ["target_name_or_class"] = 0x2, ["vector2d"] = 0x4d, ["vector"] = 0xe,
        ["vector4d"] = 0x4e, ["transform"] = 0xf, ["npcclass"] = 0x10, ["filterclass"] = 0x11,
        ["float"] = 0x12, ["side"] = 0x13, ["sidelist"] = 0x14, ["axis"] = 0x16, ["vecline"] = 0x15,
        ["pointentityclass"] = 0x17, ["node_dest"] = 0x18, ["script"] = 0x19, ["scriptlist"] = 0x1a,
        ["instance_file"] = 0x1b, ["instance_variable"] = 0x1c, ["instance_parm"] = 0x1d,
        ["boolean"] = 0x1e, ["bool"] = 0x1e, ["node_id"] = 0x1f, ["resource"] = 0x20,
        ["resource_name"] = 0x21, ["kv3"] = 0x44, ["tag_list"] = 0x22, ["tag_list_dynamic"] = 0x23,
        ["world_point"] = 0x24, ["world_angle"] = 0x25, ["world_transform"] = 0x26, ["local_point"] = 0x27,
        ["localaxis"] = 0x28, ["node_id_List"] = 0x29, ["gameunitclass"] = 0x2a, ["gameitemclass"] = 0x2b,
        ["sequence"] = 0x2c, ["bodygroupchoices"] = 0x2d, ["modelstatechoices"] = 0x2e, ["lod_level"] = 0x2f,
        ["parentAttachment"] = 0x31, ["materialgroup"] = 0x32, ["model_bone"] = 0x37,
        ["model_attachment"] = 0x38, ["propdataname"] = 0x39, ["model_bodygroup"] = 0x3a,
        ["model_breakpiece"] = 0x3b, ["model_morphchannel"] = 0x3c, ["remove_key"] = RemoveKey,
        ["curve"] = 0x43, ["collision_property"] = 0x33, ["surface_properties"] = 0x34, ["array"] = 0x45,
        ["struct"] = 0x46, ["model_cloth_effect"] = 0x3d, ["model_cloth_vertex_map"] = 0x3e,
        ["model_cloth_node"] = 0x3f, ["model_runtime_cloth_node"] = 0x40, ["model_physics_shape"] = 0x41,
        ["vdata_choice"] = 0x35, ["subclass_choice"] = 0x36, ["particle_cfg"] = 0x47, ["stringmap"] = 0x4a,
        ["intmap"] = 0x4b, ["polymorphic_class"] = 0x4c, ["panorama_image"] = 0x4f,
        ["panorama_layout"] = 0x50, ["panorama_style"] = 0x51, ["panorama_video"] = 0x52,
        ["animgraph_enum"] = 0x53, ["animgraph"] = 0x54, ["npc_ability_name"] = 0x55,
        ["animgraph2_identifier"] = 0x56, ["localization_required"] = 0x57, ["animgraph2_clip"] = 0x58,
        ["animgraph2_graph"] = 0x59, ["choice_group"] = 0x5a,
    };

    [GeneratedRegex(@"@include\s+""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex IncludeRegex();

    [GeneratedRegex(@"@exclude\s+([\w.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ExcludeRegex();

    // Only the "@PointClass" token. The rest of the header is scanned rather than
    // matched, because a metadata block inside it carries '=' of its own.
    [GeneratedRegex(@"@\w*Class\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClassRegex();

    [GeneratedRegex(@"base\s*\(([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex BaseRegex();

    // class_game_keys = [ { key = "isPointPrefab" value = true }, ... ]
    [GeneratedRegex(@"class_game_keys\s*=\s*\[(.*?)\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex GameKeysBlockRegex();

    [GeneratedRegex(@"create_entity_template_lumps\s*=\s*\[(.*?)\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TemplateLumpsBlockRegex();

    [GeneratedRegex(@"write_to_path_key\s*=\s*""([^""]*)""")]
    private static partial Regex WriteToPathKeyRegex();

    [GeneratedRegex(@"\{([^}]*)\}")]
    private static partial Regex BraceRegex();

    [GeneratedRegex(@"(\w+)\s*=\s*""([^""]*)""")]
    private static partial Regex MetadataStringRegex();

    [GeneratedRegex(@"\{\s*key\s*=\s*""([^""]+)""\s+value\s*=\s*(?:""([^""]*)""|([^}\s]+))\s*\}",
                    RegexOptions.IgnoreCase)]
    private static partial Regex GameKeyRegex();

    // metadata { static_prop = true ... }, matched only as far as the opening brace.
    [GeneratedRegex(@"\bmetadata\s*\{", RegexOptions.IgnoreCase)]
    private static partial Regex MetadataBlockRegex();

    [GeneratedRegex(@"(\w+)\s*=\s*true\b", RegexOptions.IgnoreCase)]
    private static partial Regex MetadataFlagRegex();

    // "priority(integer) : "Spawn Priority" : 0" - and not an input/output line.
    // Names may carry dots: base.fgd declares local.origin, local.angles and
    // local.scales for every parentable class. Types may carry a qualifier:
    // info_player_start's PawnSubclass is vdata_choice:scripts/player.vdata.
    [GeneratedRegex(@"^[ \t]*(?!input\b|output\b)([A-Za-z_][\w.]*)\s*\(\s*([^)\s]+)\s*\)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex KeyRegex();
}
