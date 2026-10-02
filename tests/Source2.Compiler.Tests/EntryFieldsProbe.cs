using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>ENTRYFIELDS=1</c> with <c>NODEENTRIES</c>): which 4-byte
/// words of the captured 0x238-byte node mesh entries vary, with their most
/// common values, for world and prop entries.
/// </summary>
public class EntryFieldsProbe(ITestOutputHelper output)
{
    /// <summary>
    /// <c>ENTRYFLAGS=1</c> with <c>NODEENTRIES</c> and <c>NODEENTRIES_VMAP</c>:
    /// <see cref="MeshEntryFlags"/> from each captured entry's material, the
    /// record's fields left zero, against the captured +0x1b0, by difference.
    /// </summary>
    [Fact]
    public void FlagsFromMaterials()
    {
        if (Environment.GetEnvironmentVariable("ENTRYFLAGS") != "1" || Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path
            || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap || CS2Fixtures.StockPak() is not { } pak)
            return;
        var all = NodeEntriesFromVmap.Read(path);
        var world = all.Count(c => c.Stage == "BuildNode:in");
        var entries = all.Where(c => c.Stage == "Step256690:in").ToList();
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "csgo_core", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var tally = new SortedDictionary<string, int>();
        var doc = DmxBinary.ReadFile(vmap);
        IReadOnlyCollection<string> Signature(string material) => content.Read(material + "_c") is { } b
            ? [.. MaterialAuthor.ExtractInputSignature(b).Select(x => x.Semantic)] : [];
        var ours = NodeMeshEntries.FromWorld(doc, signature: Signature, rendersAsWorld: NodeEntriesFromVmap.RendersAsWorld(game));
        ours.AddRange(NodePropEntries.FromWorld(doc, content));
        var useRecords = ours.Count == entries.Count;
        output.WriteLine($"ours {ours.Count}, captured {entries.Count}: records {(useRecords ? "used" : "not used")}");
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var info = content.Material(e.Material);
            if (info == null)
            {
                var nm = $"no material {e.Material} valve {BitConverter.ToUInt64(e.Raw, 0x1b0):x}";
                tally[nm] = tally.GetValueOrDefault(nm) + 1;
                continue;
            }
            var attributes = MaterialAttributes.Of(info, shaders);
            if (Environment.GetEnvironmentVariable("ENTRYFLAGS_DEBUG") is { } debug && Path.GetFileNameWithoutExtension(info.Shader).Equals(debug, StringComparison.OrdinalIgnoreCase) && tally.GetValueOrDefault("debugged") == 0)
            {
                tally["debugged"] = 1;
                output.WriteLine($"material {e.Material} shader {info.Shader} params {string.Join(" ", info.Params!.Select(p => $"{p.Key}={p.Value}"))}");
                output.WriteLine($"  ints {string.Join(" ", info.Ints.Select(p => $"{p.Key}={p.Value}"))} strings {string.Join(" ", info.Strings.Select(p => $"{p.Key}={p.Value}"))}");
                output.WriteLine($"  valve attr {BitConverter.ToUInt64(e.Raw, 0x1b0):x}");
                var set = shaders.Collection(info.Shader);
                output.WriteLine($"  set {(set == null ? "none" : $"features {string.Join(",", set.Features.StaticComboArray.Select((f, j) => $"{j}:{f.Name}"))}; programs {set.Programs.Length}")}");
                foreach (var name in new[] { "AllowBackfaceCulling", "DoubleSided", "renderbackfaces", "mapbuilder_donotcollapse", "translucent" })
                    output.WriteLine($"  {name}: {attributes.Bool(MeshEntryFlags.Key(name))}");
                if (set != null)
                    foreach (var program in set.Programs)
                    {
                        var features = info.Params!.Where(p => p.Key.StartsWith("F_")).ToDictionary(p => p.Key, p => (byte)p.Value);
                        var (_, id) = ValveResourceFormat.IO.ShaderDataProvider.GetStaticConfiguration_ForFeatureState(set.Features, program, features);
                        if (!program.StaticComboEntries.ContainsKey(id))
                        {
                            output.WriteLine($"  {program.VcsProgramType}: combo {id} missing");
                            continue;
                        }
                        var combo = program.GetStaticCombo(id);
                        var culls = combo.DynamicComboRenderStates.OfType<ValveResourceFormat.CompiledShader.VfxRenderStateInfoPixelShader>()
                            .Select(r => r.RasterizerStateDesc?.CullMode.ToString() ?? "-").Distinct();
                        output.WriteLine($"  {program.VcsProgramType}: combo {id}, {combo.Attributes.Length} attributes, cull [{string.Join(",", culls)}]");
                    }
            }
            var computed = MeshEntryFlags.Compute(attributes, useRecords ? ours[i].Record : new MeshEntryFlags.Record(0, 0, 0, false, null, false)).Flags;
            if (Environment.GetEnvironmentVariable("ENTRYFLAGS_CULLDEFAULT") == "1" && attributes.Bool(MeshEntryFlags.Key("AllowBackfaceCulling")) == null
                && attributes.Bool(MeshEntryFlags.Key("DoubleSided")) != true && attributes.Bool(MeshEntryFlags.Key("renderbackfaces")) != true)
                computed |= 0x100000000;
            var want = BitConverter.ToUInt64(e.Raw, 0x1b0);
            var key = $"{(i < world ? "world" : "prop")} xor {computed ^ want:x} (ours-only {computed & ~want:x}, valve-only {want & ~computed:x}) {Path.GetFileNameWithoutExtension(info.Shader)}";
            tally[key] = tally.GetValueOrDefault(key) + 1;
        }
        foreach (var (k, v) in tally.OrderByDescending(x => x.Value))
            output.WriteLine($"{v,4} {k}");
    }

    /// <summary><c>MESHKEYS=&lt;vmap&gt;</c>: each CMapMesh scalar attribute with its value counts.</summary>
    [Fact]
    public void MeshKeys()
    {
        if (Environment.GetEnvironmentVariable("MESHKEYS") is not { } vmap)
            return;
        var doc = DmxBinary.ReadFile(vmap);
        if (Environment.GetEnvironmentVariable("MESHKEYS_NODE") is { } node)
        {
            foreach (var mesh in doc.OfType("CMapMesh").Where(m => m.GetValue<int>("nodeID") == int.Parse(node)))
                foreach (var (name, value) in mesh.Attributes)
                    output.WriteLine($"  {name} = {(value is System.Collections.IEnumerable e && value is not string ? $"[{string.Join(",", e.Cast<object>().Take(8))}]" : value)} ({value?.GetType().Name})");
            return;
        }
        var tally = new SortedDictionary<string, int>();
        foreach (var mesh in doc.OfType("CMapMesh"))
            foreach (var (name, value) in mesh.Attributes)
                if (value is bool or int or float or string or byte)
                {
                    var key = $"{name} = {value}";
                    tally[key] = tally.GetValueOrDefault(key) + 1;
                }
        foreach (var (k, v) in tally)
            output.WriteLine($"{v,5} {k}");
    }

    /// <summary>
    /// <c>ENTRYKEYS=&lt;offsets&gt;</c> (hex, comma separated, each +len e.g. 1a1+1)
    /// with <c>NODEENTRIES</c> and <c>NODEENTRIES_VMAP</c>: for each captured
    /// field, its values against the source node's scalar keys, the keys
    /// listed whose value determines the field over every entry.
    /// </summary>
    [Fact]
    public void FieldsAgainstKeys()
    {
        if (Environment.GetEnvironmentVariable("ENTRYKEYS") is not { } spec || Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path
            || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap || CS2Fixtures.StockPak() is not { } pak)
            return;
        var all = NodeEntriesFromVmap.Read(path);
        var world = all.Count(c => c.Stage == "BuildNode:in");
        var entries = all.Where(c => c.Stage == "Step256690:in").ToList();
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        var doc = DmxBinary.ReadFile(vmap);
        IReadOnlyCollection<string> Signature(string material) => content.Read(material + "_c") is { } b
            ? [.. MaterialAuthor.ExtractInputSignature(b).Select(x => x.Semantic)] : [];
        var ours = NodeMeshEntries.FromWorld(doc, signature: Signature, rendersAsWorld: NodeEntriesFromVmap.RendersAsWorld(game));
        ours.AddRange(NodePropEntries.FromWorld(doc, content));
        Assert.Equal(entries.Count, ours.Count);
        foreach (var part in spec.Split(','))
        {
            var bits = part.Split('+');
            var off = Convert.ToInt32(bits[0], 16);
            var len = bits.Length > 1 ? int.Parse(bits[1]) : 4;
            foreach (var (label, from, to) in new[] { ("world", 0, world), ("props", world, entries.Count) })
            {
                string Field(int i) => Convert.ToHexString(entries[i].Raw, off, len);
                var keys = new Dictionary<string, Dictionary<string, HashSet<string>>>();
                for (var i = from; i < to; i++)
                {
                    if (ours[i].Source is not { } src)
                        continue;
                    foreach (var (k, v) in src.Attributes)
                        if (v is bool or int or float or string or byte)
                        {
                            if (!keys.TryGetValue(k, out var map))
                                keys[k] = map = [];
                            var vs = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!;
                            if (!map.TryGetValue(vs, out var fields))
                                map[vs] = fields = [];
                            fields.Add(Field(i));
                        }
                }
                var values = Enumerable.Range(from, to - from).GroupBy(Field).Select(g => $"{g.Key} x{g.Count()}");
                output.WriteLine($"+0x{off:x}+{len} {label}: {string.Join(", ", values)}");
                foreach (var (k, map) in keys)
                    if (map.Values.All(f => f.Count == 1) && map.Count > 1)
                        output.WriteLine($"   determined by {k}: {string.Join("; ", map.Select(kv => $"{kv.Key} -> {kv.Value.First()}"))}");
            }
        }
    }

    /// <summary><c>ENTRYTRACE=&lt;material substring&gt;</c> with <c>NODEENTRIES</c>: the entry's id, attributes and object flags at every captured stage.</summary>
    [Fact]
    public void Trace()
    {
        if (Environment.GetEnvironmentVariable("ENTRYTRACE") is not { } word || Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path)
            return;
        foreach (var c in NodeEntriesFromVmap.Read(path).Where(c => c.Material.Contains(word, StringComparison.OrdinalIgnoreCase)))
            output.WriteLine($"{c.Stage} {c.Index}: id {BitConverter.ToInt32(c.Raw, 0x40)} attr {BitConverter.ToUInt64(c.Raw, 0x1b0):x} obj {BitConverter.ToUInt32(c.Raw, 0xbc):x} nv {c.Vertices.Length / c.Stride}");
    }

    [Fact]
    public void Variance()
    {
        if (Environment.GetEnvironmentVariable("ENTRYFIELDS") != "1" || Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path)
            return;
        var all = NodeEntriesFromVmap.Read(path);
        var world = all.Where(c => c.Stage == "BuildNode:in").ToList();
        var step = all.Where(c => c.Stage == "Step256690:in").ToList();
        output.WriteLine($"raw {world[0].Raw.Length} bytes; world {world.Count}, step {step.Count}");
        foreach (var (name, set) in new[] { ("world", world), ("props", step.Skip(world.Count).ToList()) })
        {
            output.WriteLine($"== {name}");
            for (var off = 0; off + 4 <= set[0].Raw.Length; off += 4)
            {
                var values = set.GroupBy(c => BitConverter.ToUInt32(c.Raw, off)).OrderByDescending(g => g.Count()).ToList();
                if (values.Count > 1 || values[0].Key != 0)
                    output.WriteLine($"+0x{off:x3}: {values.Count} distinct; {string.Join(", ", values.Take(5).Select(g => $"{g.Key:x8} x{g.Count()}"))}");
            }
        }
    }
}
