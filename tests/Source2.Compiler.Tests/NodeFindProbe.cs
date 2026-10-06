using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: every element with a node id, in a map and the maps its prefabs
/// load, with its type, class, model and enclosing prefab.
/// <c>NODEFIND=&lt;addon&gt;|&lt;map&gt;|&lt;node id&gt;</c>.
/// </summary>
public class NodeFindProbe(ITestOutputHelper output)
{
    [Fact]
    public void FindNode()
    {
        if (Environment.GetEnvironmentVariable("NODEFIND") is not { Length: > 0 } spec || spec.Split('|') is not [var addon, var map, var id])
            return;
        var source = MapFixtures.VmapSource(addon, map)!;
        var doc = DmxBinary.ReadFile(source);
        Maps.MapPrefabs.Attach(doc, Maps.MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(source))!));
        void Search(DmxBinary.Document d, string where)
        {
            foreach (var e in d.Elements)
            {
                if (e.GetValue<int>("nodeID")?.ToString() == id)
                {
                    var props = e.Get<DmxBinary.Element>("entity_properties");
                    output.WriteLine($"{where}: {e.Type} class {props?.Get<string>("classname")} model {props?.Get<string>("model")} origin {e.GetValue<System.Numerics.Vector3>("origin")}"
                        + $" angles {e.GetValue<System.Numerics.Vector3>("angles")} scales {e.GetValue<System.Numerics.Vector3>("scales")}");
                    // NODEFIND_ALL=1: every attribute of the node and its entity keys.
                    if (Environment.GetEnvironmentVariable("NODEFIND_ALL") == "1")
                    {
                        foreach (var (k, v) in e.Attributes)
                            output.WriteLine($"  attr {k} = {(v is System.Collections.IEnumerable list and not string ? $"[{string.Join(", ", list.Cast<object?>().Take(8))}]" : v)}");
                        foreach (var (k, v) in props?.Attributes ?? [])
                            output.WriteLine($"  key {k} = {v}");
                        if (e.Get<DmxBinary.Element>("meshData") is { } md)
                            foreach (var holder in new[] { "vertexData", "faceVertexData", "edgeData", "faceData" })
                                foreach (var st in md.Get<DmxBinary.Element>(holder)?.GetElements("streams") ?? [])
                                    output.WriteLine($"  {holder} stream {st.Name} ({(st.Get<object?[]>("data") ?? []).Length} values)");
                    }
                    foreach (var c in e.GetElements("children"))
                        output.WriteLine($"  child {c.Type} {c.GetValue<int>("nodeID")} origin {c.GetValue<System.Numerics.Vector3>("origin")} angles {c.GetValue<System.Numerics.Vector3>("angles")}"
                            + $" scales {c.GetValue<System.Numerics.Vector3>("scales")} physicsType {c.Attributes.GetValueOrDefault("physicsType")}");
                }
                if (e.Attributes.GetValueOrDefault(Maps.MapPrefabs.DocumentKey) is DmxBinary.Document inner)
                    Search(inner, where + $" > prefab {e.GetValue<int>("nodeID")} ({e.Get<string>("targetMapPath")})");
            }
        }
        Search(doc, map);
    }

    /// <summary><c>LUMPCLASS=&lt;class&gt;|&lt;folder&gt;</c>: every entity of a class in the lumps of each .vpk in the folder.</summary>
    [Fact]
    public void ClassInLumps()
    {
        if (Environment.GetEnvironmentVariable("LUMPCLASS") is not { Length: > 0 } spec || spec.Split('|') is not [var cls, var folder])
            return;
        foreach (var file in Directory.GetFiles(folder, "*.vpk").Order(StringComparer.Ordinal))
        {
            using var pkg = new ValvePak.Package();
            pkg.Read(file);
            var count = 0;
            foreach (var entry in pkg.Entries.GetValueOrDefault("vents_c") ?? [])
                foreach (var e in EntityLumpComparison.Read(Io.VpkEntries.Read(pkg, entry), entry.GetFullPath())
                             .Where(e => e.ClassName.Equals(cls, StringComparison.OrdinalIgnoreCase)))
                {
                    count++;
                    output.WriteLine($"{Path.GetFileName(file)} {entry.GetFullPath()}: {cls} {e.HammerId} origin {(e.Values.TryGetValue("origin", out var o) ? o.Value : "")} angles {(e.Values.TryGetValue("angles", out var an) ? an.Value : "")}");
                    if (Environment.GetEnvironmentVariable("LUMPCLASS_KEYS") == "1")
                        output.WriteLine($"  keys {string.Join(",", e.KeyOrder)}");
                }
            output.WriteLine($"{Path.GetFileName(file)}: {count}");
        }
    }

    /// <summary><c>VPKDIFF=&lt;old folder&gt;|&lt;new folder&gt;</c>: for each .vpk in the new folder, the entries that differ from the same-named .vpk in the old one.</summary>
    [Fact]
    public void PackagesDiffer()
    {
        if (Environment.GetEnvironmentVariable("VPKDIFF") is not { Length: > 0 } spec || spec.Split('|') is not [var oldDir, var newDir])
            return;
        static Dictionary<string, byte[]> Read(string file)
        {
            using var pkg = new ValvePak.Package();
            pkg.Read(file);
            var found = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, entries) in pkg.Entries)
                foreach (var e in entries)
                    found[e.GetFullPath()] = Io.VpkEntries.Read(pkg, e);
            return found;
        }
        foreach (var file in Directory.GetFiles(newDir, "*.vpk").Order(StringComparer.Ordinal))
        {
            var old = Path.Combine(oldDir, Path.GetFileName(file));
            if (!File.Exists(old))
                continue;
            var (a, b) = (Read(old), Read(file));
            var differ = b.Keys.Where(k => a.TryGetValue(k, out var x) && !x.AsSpan().SequenceEqual(b[k])).Order(StringComparer.Ordinal).ToList();
            var added = b.Keys.Except(a.Keys).ToList();
            var removed = a.Keys.Except(b.Keys).ToList();
            output.WriteLine($"{Path.GetFileName(file)}: {b.Count} entries, {differ.Count} differ, {added.Count} new, {removed.Count} gone");
            foreach (var k in differ.Concat(added.Select(x => "+" + x)).Concat(removed.Select(x => "-" + x)).Take(25))
                output.WriteLine($"  {k}");
            // VPKDIFF_TREES=1: each differing entry's blocks decoded and diffed.
            if (Environment.GetEnvironmentVariable("VPKDIFF_TREES") == "1")
                foreach (var k in differ.Take(6))
                {
                    try
                    {
                        var (ta, tb) = (WorldPhysicsAuthorTests.Trees(a[k]), WorldPhysicsAuthorTests.Trees(b[k]));
                        foreach (var block in tb.Keys.Union(ta.Keys))
                            if (ta.TryGetValue(block, out var x) && tb.TryGetValue(block, out var y))
                                foreach (var line in KvTreeDiff.Diff(x, y, 8))
                                    output.WriteLine($"    {k} {block}{line}");
                            else
                                output.WriteLine($"    {k} {block}: only on one side");
                    }
                    catch (Exception e)
                    {
                        output.WriteLine($"    {k}: not decoded ({e.Message}); sizes {a[k].Length} -> {b[k].Length}, first differing byte {a[k].AsSpan().CommonPrefixLength(b[k])}");
                    }
                }
        }
    }

    /// <summary><c>KEYFIND=&lt;addon&gt;|&lt;map&gt;|&lt;key&gt;</c>: the nodes whose entity properties hold the key, with its value and place among their keys.</summary>
    [Fact]
    public void FindKey()
    {
        if (Environment.GetEnvironmentVariable("KEYFIND") is not { Length: > 0 } spec || spec.Split('|') is not [var addon, var map, var key])
            return;
        var doc = DmxBinary.ReadFile(MapFixtures.VmapSource(addon, map)!);
        foreach (var e in doc.Elements)
            if (e.Get<DmxBinary.Element>("entity_properties") is { } props)
            {
                var keys = props.Attributes.Keys.ToList();
                var at = keys.FindIndex(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (at >= 0)
                    output.WriteLine($"node {e.GetValue<int>("nodeID")} {props.Get<string>("classname")}: {keys[at]} = {props.Attributes[keys[at]]} (key {at} of {keys.Count}: {string.Join(",", keys)})");
            }
    }

    /// <summary><c>EDITORONLY=&lt;folder of addon__map.vpk&gt;</c>: each map's (and its prefabs') nodes marked editorOnly.</summary>
    [Fact]
    public void EditorOnlyNodes()
    {
        if (Environment.GetEnvironmentVariable("EDITORONLY") is not { Length: > 0 } folder)
            return;
        foreach (var file in Directory.GetFiles(folder, "*.vpk").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Split("__") is not [var addon, var map] || MapFixtures.VmapSource(addon, map) is not { } source)
                continue;
            var doc = DmxBinary.ReadFile(source);
            Maps.MapPrefabs.Attach(doc, Maps.MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(source))!));
            var found = 0;
            void Scan(DmxBinary.Document d, string where)
            {
                foreach (var e in d.Elements)
                {
                    if (e.GetValue<bool>("editorOnly") == true)
                    {
                        found++;
                        var props = e.Get<DmxBinary.Element>("entity_properties");
                        output.WriteLine($"{name}{where}: {e.Type} {e.GetValue<int>("nodeID")} class {props?.Get<string>("classname")} children {e.GetElements("children").Count()}");
                    }
                    if (e.Attributes.GetValueOrDefault(Maps.MapPrefabs.DocumentKey) is DmxBinary.Document inner)
                        Scan(inner, $"{where} > prefab {e.GetValue<int>("nodeID")}");
                }
            }
            Scan(doc, "");
            output.WriteLine($"{name}: {found} editorOnly");
        }
    }

    /// <summary><c>INSTLIST=&lt;addon&gt;|&lt;map&gt;</c>: the map's instances and prefabs in walk order, with depth and parent.</summary>
    [Fact]
    public void ListInstances()
    {
        if (Environment.GetEnvironmentVariable("INSTLIST") is not { Length: > 0 } spec || spec.Split('|') is not [var addon, var map])
            return;
        var source = MapFixtures.VmapSource(addon, map)!;
        var doc = DmxBinary.ReadFile(source);
        Maps.MapPrefabs.Attach(doc, Maps.MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(source))!));
        var hidden = MapEntities.HiddenNodes(doc);
        var order = 0;
        void Walk(DmxBinary.Element node, int depth)
        {
            foreach (var c in node.GetElements("children"))
            {
                order++;
                if (c.Type is "CMapInstance" or "CMapPrefab")
                    output.WriteLine($"#{order} depth {depth} {c.Type} {c.GetValue<int>("nodeID")} parent {node.Type} {node.GetValue<int>("nodeID")}"
                        + $" target {c.Get<DmxBinary.Element>("target")?.GetValue<int>("nodeID")} map {c.Get<string>("targetMapPath")}"
                        + $" hidden {hidden.Contains(c.GetValue<int>("nodeID") ?? -1)} loaded {c.Attributes.ContainsKey(Maps.MapPrefabs.WorldKey)}");
                Walk(c, depth + 1);
            }
        }
        foreach (var world in doc.OfType("CMapWorld"))
            Walk(world, 0);
        output.WriteLine($"max nodeID {doc.Elements.Max(e => e.GetValue<int>("nodeID") ?? 0)}, nodes walked {order}");
    }
}
