using ValveKeyValue;

namespace Source2.Compiler.Compiling;

/// <summary>
/// resourcecompiler's surface property compile (<c>.vsurf</c> to <c>.vsurf_c</c>),
/// CResourceCompilerSurfaceProperties vf11: rc 0923 18012e470, 1103 18012e640 (the same
/// code, relocations masked; its helpers 18012eb80 / 18012e360 likewise, at +0x1d0).
///
/// <para>Each entry of <c>SurfacePropertiesList</c>, in order:</para>
/// <list type="number">
/// <item>A new table is built. If the entry names a <c>base</c>, the base chain is merged in
/// first, deepest base first (18012eb80): every member of the base entry but <c>hidden</c>
/// (at any depth, 18012e360 with its skip flag), tables merged member by member, anything
/// else set. A base is the first entry of the whole list with that name; entries before this
/// one have already been replaced by their compiled tables, later ones are as written.</item>
/// <item>The entry's own members are merged on top, nothing skipped.</item>
/// <item><c>m_nameHash</c> = the string token of <c>surfacePropertyName</c> (uint32), and the
/// name becomes a <c>surface_prop</c> subasset definition; then <c>m_baseNameHash</c> = the
/// base's token, 0 without one (uint32).</item>
/// <item>The table replaces the entry in the list, and every value of its
/// <c>audiosounds</c> table (inherited ones too) is a <c>soundevent</c> subasset reference.</item>
/// </list>
/// <para>Members are found by their lower-cased name (KV3 keys are hashed that way), so a
/// set keeps the first spelling and its position; a new member goes last.</para>
/// <para>Refused as RC refuses: no list, no entry named <c>default</c> (its members are read
/// into a table nothing uses afterwards, so only its presence matters), a missing or
/// looping base, a duplicate name. A missing name is logged by RC and compiles without a
/// hash or definition.</para>
/// </summary>
public static class SurfacePropertiesCompile
{
    private const string ListKey = "SurfacePropertiesList";
    private const string NameKey = "surfacePropertyName";

    /// <summary>The compiled DATA tree and the RED2 subasset tables (null where RC leaves them null).</summary>
    public sealed record Result(KVObject Data, KVObject SubassetReferences, KVObject SubassetDefinitions);

    /// <summary>Compile a parsed <c>.vsurf</c> root.</summary>
    /// <exception cref="InvalidDataException">The source is one RC refuses to compile.</exception>
    public static Result Compile(KVObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!root.IsCollection || !TryGet(root, ListKey, out var listNode) || !listNode.IsArray)
            throw new InvalidDataException("resourcecompiler: the surface properties have no SurfacePropertiesList.");

        var list = listNode.Values.Select(v => v.IsCollection ? Table.From(v) : throw new InvalidDataException(
            "resourcecompiler: every SurfacePropertiesList entry must be a table.")).ToList();
        if (!list.Any(e => string.Equals(e.GetString(NameKey), "default", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("resourcecompiler: Surface Property \"default\" not found.  Cannot compile!");

        var refs = new Subassets();
        var defs = new Subassets();
        for (var i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            var compiled = new Table();
            uint baseHash = 0;
            var baseName = entry.GetString("base");
            if (!string.IsNullOrEmpty(baseName))
            {
                MergeBase(list, baseName, compiled, new HashSet<Table>(ReferenceEqualityComparer.Instance));
                baseHash = StringToken.Get(baseName);
            }
            Merge(compiled, entry, skipHidden: false);

            var name = compiled.GetString(NameKey);
            if (!string.IsNullOrEmpty(name))
            {
                compiled.Set("m_nameHash", new KVObject(StringToken.Get(name)));
                defs.Define("surface_prop", name);
                for (var j = 0; j < i; j++)
                    if (string.Equals(list[j].GetString(NameKey), name, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            $"resourcecompiler: Surface Property \"{name}\" is a duplicate of \"{list[j].GetString(NameKey)}\".  Cannot compile!");
            }
            compiled.Set("m_baseNameHash", new KVObject(baseHash));
            list[i] = compiled;

            if (compiled.TryGet("audiosounds", out var sounds) && sounds.Table is { } soundTable)
                foreach (var (_, sound) in soundTable.Members)
                    refs.Reference("soundevent", sound.Leaf is { ValueType: KVValueType.String } s ? (string)s
                        : throw new InvalidOperationException("A surface property's audiosounds value that is not a string is not ported."));
        }

        var data = KVObject.Collection();
        foreach (var child in root.Children)
            data.Add(child.Key, string.Equals(child.Key, ListKey, StringComparison.OrdinalIgnoreCase)
                ? KVObject.Array(list.Select(e => e.ToKv()))
                : child.Value);
        return new Result(data, refs.ToKv(counted: true), defs.ToKv(counted: false));
    }

    // 18012eb80: find the base by name (first match), refuse a loop, merge its own base first,
    // then its members but "hidden".
    private static void MergeBase(List<Table> list, string baseName, Table into, HashSet<Table> visited)
    {
        var found = list.FirstOrDefault(e => e.GetString(NameKey) is { Length: > 0 } n
                                             && string.Equals(n, baseName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"resourcecompiler: Surface Property has missing base surface reference \"{baseName}\"!");
        if (!visited.Add(found))
            throw new InvalidDataException(
                $"resourcecompiler: Surface Property \"{found.GetString(NameKey)}\" has looping base surface reference \"{baseName}\"!");
        if (found.GetString("base") is { Length: > 0 } next)
            MergeBase(list, next, into, visited);
        Merge(into, found, skipHidden: true);
    }

    // 18012e360: tables merge member by member, anything else is set (a copy).
    private static void Merge(Table into, Table from, bool skipHidden)
    {
        foreach (var (key, value) in from.Members)
        {
            if (value.Table is { } sub)
            {
                var target = into.FindOrAdd(key);
                target.Table ??= new Table();
                target.Leaf = null;
                Merge(target.Table, sub, skipHidden);
            }
            else if (!skipHidden || !string.Equals(key, "hidden", StringComparison.OrdinalIgnoreCase))
                into.Set(key, value.Leaf!);
        }
    }

    private static bool TryGet(KVObject table, string key, out KVObject value)
    {
        foreach (var child in table.Children)
            if (string.Equals(child.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = child.Value;
                return true;
            }
        value = null!;
        return false;
    }

    private sealed class Node
    {
        public KVObject? Leaf;
        public Table? Table;
    }

    // A KV3 table as tier0 keeps it: members in insertion order, found by lower-cased name.
    private sealed class Table
    {
        private readonly List<(string Key, Node Value)> members = [];
        private readonly Dictionary<string, int> index = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<(string Key, Node Value)> Members => members;

        public static Table From(KVObject kv)
        {
            var t = new Table();
            foreach (var child in kv.Children)
            {
                var node = t.FindOrAdd(child.Key);
                if (child.Value.IsCollection)
                    node.Table = From(child.Value);
                else
                    node.Leaf = child.Value;
            }
            return t;
        }

        public Node FindOrAdd(string key)
        {
            if (index.TryGetValue(key, out var at))
                return members[at].Value;
            var node = new Node();
            index[key] = members.Count;
            members.Add((key, node));
            return node;
        }

        public bool TryGet(string key, out Node node)
        {
            if (index.TryGetValue(key, out var at))
            {
                node = members[at].Value;
                return true;
            }
            node = null!;
            return false;
        }

        public void Set(string key, KVObject leaf)
        {
            var node = FindOrAdd(key);
            node.Table = null;
            node.Leaf = leaf;
        }

        // FUN_181eaed10's string read: the member's string, null when absent or not a string.
        public string? GetString(string key)
            => TryGet(key, out var n) && n.Leaf is { ValueType: KVValueType.String } s ? (string)s : null;

        public KVObject ToKv()
        {
            var kv = KVObject.Collection();
            foreach (var (key, node) in members)
                kv.Add(key, node.Table is { } t ? t.ToKv() : node.Leaf!);
            return kv;
        }
    }

    // The compile context's subasset tables (CResourceCompilerContext vf2 180175570 and
    // AddSubassetReference 180175420): types and names keyed ignoring case, first spelling
    // kept; a definition is listed once, a reference counts. Null when nothing was added.
    private sealed class Subassets
    {
        private readonly List<(string Type, List<string> Names, Dictionary<string, int> Counts)> types = [];

        private (string Type, List<string> Names, Dictionary<string, int> Counts) Of(string type)
        {
            foreach (var t in types)
                if (string.Equals(t.Type, type, StringComparison.OrdinalIgnoreCase))
                    return t;
            var added = (type, new List<string>(), new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
            types.Add(added);
            return added;
        }

        public void Define(string type, string name)
        {
            var t = Of(type);
            if (t.Counts.TryAdd(name, 1))
                t.Names.Add(name);
        }

        public void Reference(string type, string name)
        {
            if (name.Length == 0)
                return;
            var t = Of(type);
            if (t.Counts.TryGetValue(name, out var n))
                t.Counts[name] = n + 1;
            else
            {
                t.Counts[name] = 1;
                t.Names.Add(name);
            }
        }

        public KVObject ToKv(bool counted)
        {
            if (types.Count == 0)
                return KVObject.Null();
            var kv = KVObject.Collection();
            foreach (var (type, names, counts) in types)
            {
                if (counted)
                {
                    var table = KVObject.Collection();
                    foreach (var name in names)
                        table.Add(name, new KVObject((long)counts[name]));
                    kv.Add(type, table);
                }
                else
                    kv.Add(type, KVObject.Array(names.Select(n => new KVObject(n))));
            }
            return kv;
        }
    }
}
