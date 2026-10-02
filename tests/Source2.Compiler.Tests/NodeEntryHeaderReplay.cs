using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="NodeEntryHeader"/> for the world entries built from the .vmap
/// against the captured entries (<c>NODEENTRIES</c>, <c>NODEENTRIES_VMAP</c>),
/// field by field.
/// </summary>
public class NodeEntryHeaderReplay(ITestOutputHelper output)
{
    [Fact]
    public void WorldHeaders()
    {
        if (Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap
            || CS2Fixtures.StockPak() is not { } pak)
            return;
        var captured = NodeEntriesFromVmap.Read(path).Where(c => c.Stage == "BuildNode:in").ToList();
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        IReadOnlyCollection<string> Signature(string material) => content.Read(material + "_c") is { } b
            ? [.. MaterialAuthor.ExtractInputSignature(b).Select(x => x.Semantic)] : [];
        var doc = DmxBinary.ReadFile(vmap);
        var ours = NodeMeshEntries.FromWorld(doc, signature: Signature, rendersAsWorld: NodeEntriesFromVmap.RendersAsWorld(game));
        var copyIds = NodePropEntries.CopyIds(doc, content);
        Assert.Equal(captured.Count, ours.Count);
        // The node's world bounds over all its pieces.
        string NodeKey(NodeMeshEntries.Entry o) => NodePropEntries.Key(o.Source!, o.Instances);
        var bounds = new Dictionary<string, (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)>();
        foreach (var o in ours)
            for (var k = 0; k < o.Vertices.Length / o.Stride; k++)
            {
                var p = new System.Numerics.Vector3(o.Vertices[k * o.Stride], o.Vertices[(k * o.Stride) + 1], o.Vertices[(k * o.Stride) + 2]);
                bounds[NodeKey(o)] = bounds.TryGetValue(NodeKey(o), out var b) ? (System.Numerics.Vector3.Min(b.Min, p), System.Numerics.Vector3.Max(b.Max, p)) : (p, p);
            }
        var bad = new SortedDictionary<int, int>();
        var shown = 0;
        for (var i = 0; i < ours.Count; i++)
        {
            var o = ours[i];
            var attributes = content.Material(o.Material) is { } info ? MaterialAttributes.Of(info, shaders, content.TextureSize) : MaterialAttributes.Empty;
            var (bmin, bmax) = bounds[NodeKey(o)];
            var origin = Environment.GetEnvironmentVariable("HEADER_ORIGIN") == "1" ? new System.Numerics.Vector3(o.Turn[3], o.Turn[7], o.Turn[11]) : (bmin + bmax) * 0.5f;
            var id = o.Instances.Length == 0 ? o.NodeId : copyIds.GetValueOrDefault(NodeKey(o), -1);
            var header = NodeEntryHeader.World(o.Source!, id, origin, o.Record, attributes, copy: o.Instances.Length > 0);
            foreach (var (offset, bytes) in header.Fields())
                if (!captured[i].Raw.AsSpan(offset, bytes.Length).SequenceEqual(bytes))
                {
                    bad[offset] = bad.GetValueOrDefault(offset) + 1;
                    if (shown++ < 12)
                        output.WriteLine($"entry {i} node {o.NodeId} +0x{offset:x}: ours {Convert.ToHexString(bytes)} valve {Convert.ToHexString(captured[i].Raw, offset, bytes.Length)}");
                    if (offset == 0x1b8 && content.Material(o.Material) is { } mi && bad[offset] < 8)
                        output.WriteLine($"   representative {attributes.RepresentativeTexture} textures {string.Join(" ", mi.Textures.Select(t => $"{t.Key}={t.Value}:{content.TextureSize(t.Value)}"))}");
                }
        }
        output.WriteLine($"{ours.Count} entries; fields differing: {string.Join(", ", bad.Select(kv => $"+0x{kv.Key:x} x{kv.Value}"))}");
        Assert.Empty(bad);
    }

    [Fact]
    public void PropHeaders()
    {
        if (Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap
            || CS2Fixtures.StockPak() is not { } pak)
            return;
        var all = NodeEntriesFromVmap.Read(path);
        var world = all.Count(c => c.Stage == "BuildNode:in");
        var captured = all.Where(c => c.Stage == "Step256690:in").Skip(world).ToList();
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var ours = NodePropEntries.FromWorld(DmxBinary.ReadFile(vmap), content);
        Assert.Equal(captured.Count, ours.Count);
        var bad = new SortedDictionary<int, int>();
        var shown = 0;
        for (var i = 0; i < ours.Count; i++)
        {
            var o = ours[i];
            var attributes = content.Material(o.Material) is { } info ? MaterialAttributes.Of(info, shaders, content.TextureSize) : MaterialAttributes.Empty;
            var header = NodeEntryHeader.Prop(o.Source!, o.NodeId, o.Source!.Get<string>("model") ?? "", o.Record, attributes);
            foreach (var (offset, bytes) in header.Fields())
                if (!captured[i].Raw.AsSpan(offset, bytes.Length).SequenceEqual(bytes))
                {
                    bad[offset] = bad.GetValueOrDefault(offset) + 1;
                    if (shown++ < 12)
                        output.WriteLine($"entry {i} node {o.NodeId} +0x{offset:x}: ours {Convert.ToHexString(bytes)} valve {Convert.ToHexString(captured[i].Raw, offset, bytes.Length)}");
                }
        }
        output.WriteLine($"{ours.Count} prop entries; fields differing: {string.Join(", ", bad.Select(kv => $"+0x{kv.Key:x} x{kv.Value}"))}");
        Assert.Empty(bad);
    }
}
