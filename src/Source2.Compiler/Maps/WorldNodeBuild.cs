using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The world renderer builder's node from the .vmap alone, step by step, with
/// the ports each step uses (docs/GEOMETRY.md, "World nodes and render
/// meshes"). Each step is checked against the capture of the same step
/// (tools/vis/capture_nodeentries.py; WorldNodeBuildTests).
/// <list type="bullet">
/// <item><see cref="WorldEntries"/>: the visible world meshes' entries
/// (<see cref="NodeMeshEntries"/>) with their headers
/// (<see cref="NodeEntryHeader.World"/>): the origin is the centre of the
/// node's world bounds over all its pieces, an instance copy's id the
/// copy's.</item>
/// <item><see cref="BuildNode"/>: entries with attribute bit 10
/// (toolslightmapres), bit 34 or bit 36 leave; the T-junction fix runs over
/// the rest (<see cref="TJunctionFix"/>; attribute bit 0 takes no part,
/// +0x1a0 skips its own node), then each entry is welded at 1/32
/// (CMesh_Weld) unless it is +0x1a0, baked (+0x98) and +0xc0 bit 4.</item>
/// </list>
/// </summary>
internal static class WorldNodeBuild
{
    /// <summary>One entry and its header.</summary>
    public sealed record Node(NodeMeshEntries.Entry Entry, NodeEntryHeader Header);

    /// <summary>An entry after BuildNode: its vertices and indices, the header carried.</summary>
    public sealed record Built(Node Source, float[] Vertices, int[] Indices);

    public static List<Node> WorldEntries(DmxBinary.Document doc, GameContent content, ShaderLibrary shaders, Func<string, bool> rendersAsWorld)
    {
        IReadOnlyCollection<string> Signature(string material) => content.Read(material + "_c") is { } b
            ? [.. Source2.Compiler.MaterialAuthor.ExtractInputSignature(b).Select(x => x.Semantic)] : [];
        var entries = NodeMeshEntries.FromWorld(doc, signature: Signature, rendersAsWorld: rendersAsWorld);
        var copyIds = NodePropEntries.CopyIds(doc, content);
        string NodeKey(NodeMeshEntries.Entry o) => NodePropEntries.Key(o.Source!, o.Instances);
        var bounds = new Dictionary<string, (Vector3 Min, Vector3 Max)>();
        foreach (var o in entries)
            for (var k = 0; k < o.Vertices.Length / o.Stride; k++)
            {
                var p = new Vector3(o.Vertices[k * o.Stride], o.Vertices[(k * o.Stride) + 1], o.Vertices[(k * o.Stride) + 2]);
                bounds[NodeKey(o)] = bounds.TryGetValue(NodeKey(o), out var b) ? (Vector3.Min(b.Min, p), Vector3.Max(b.Max, p)) : (p, p);
            }
        var result = new List<Node>(entries.Count);
        foreach (var o in entries)
        {
            var attributes = content.Material(o.Material) is { } info ? MaterialAttributes.Of(info, shaders, content.TextureSize) : MaterialAttributes.Empty;
            var (min, max) = bounds[NodeKey(o)];
            var id = o.Instances.Length == 0 ? o.NodeId : copyIds.GetValueOrDefault(NodeKey(o), -1);
            result.Add(new Node(o, NodeEntryHeader.World(o.Source!, id, (min + max) * 0.5f, o.Record, attributes, copy: o.Instances.Length > 0)));
        }
        return result;
    }

    /// <summary>The baked static props' entries (<see cref="NodePropEntries"/>) with their headers.</summary>
    public static List<Node> PropEntries(DmxBinary.Document doc, GameContent content, ShaderLibrary shaders, List<string>? notes = null)
        => [.. NodePropEntries.FromWorld(doc, content, notes).Select(o => new Node(o, NodeEntryHeader.Prop(o.Source!, o.NodeId, o.Source!.Get<string>("model") ?? "", o.Record,
               content.Material(o.Material) is { } info ? MaterialAttributes.Of(info, shaders, content.TextureSize) : MaterialAttributes.Empty)))];

    /// <summary>
    /// BuildNode over the world's entries then the props' (Step256690's input
    /// holds both), the overlay meshes projected onto the result
    /// (<see cref="NodeOverlays"/>, Step25ece0) when descriptors are given.
    /// </summary>
    public static (List<Built> Entries, List<NodeOverlays.Projection> Overlays) BuildNode(IReadOnlyList<Node> world, IReadOnlyList<Node> props,
                                                                                         IReadOnlyList<NodeOverlays.Descriptor> overlays)
    {
        // The overlays project onto the entries as the T-junction fix leaves
        // them (Step25ece0), before the closing weld.
        var fixedUp = FixTJunctions([.. world, .. props]);
        var targets = fixedUp.Select(b => new NodeOverlays.Target(b.Vertices, b.Source.Entry.Stride, b.Source.Entry.Streams, b.Indices,
            [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0], b.Source.Header.Byte1a0 != 0 ? 2 : 1, b.Source.Header.NodeId, b.Source.Header.Attributes)).ToList();
        return ([.. fixedUp.Select(Weld)], NodeOverlays.Project(overlays, targets));
    }

    public static List<Built> BuildNode(IReadOnlyList<Node> world) => [.. FixTJunctions(world).Select(Weld)];

    /// <summary>
    /// Each built entry's cubemap (+0xa0) and light probe (+0xa4) handshakes
    /// (<see cref="EnvVolumes.ForEntry"/>, FUN_180255040 in BuildNode): the
    /// point is the header's origin, or for a prop (FLT_MAX) its mesh's bounds
    /// centre; the name the record's lighting origin.
    /// </summary>
    public static List<(int Cubemap, int Probe)> EnvMaps(IReadOnlyList<Built> built, EnvVolumes.Set volumes)
        => [.. built.Select(b => EnvVolumes.ForEntry(b.Source.Header.Origin, b.Source.Entry.Record.LightingOrigin ?? "", b.Source.Header.Attributes,
                                                     () => Bounds(b.Vertices, b.Source.Entry.Stride, b.Source.Entry.Streams), volumes))];

    /// <summary>
    /// An overlay entry's pair: 0 and 0 unless its attributes ask (bits 20,
    /// 21), which throws: the overlay entry's +0x80 is not read yet.
    /// </summary>
    public static (int Cubemap, int Probe) OverlayEnvMaps(ulong attributes)
        => (attributes & 0x300000) == 0 ? (0, 0)
            : throw new NotSupportedException("an overlay entry that needs a cubemap or light probe: its +0x80 is not read");

    // CMesh_Bounds: the positions' per-axis minimum and maximum.
    private static (Vector3 Min, Vector3 Max) Bounds(float[] vertices, int stride, IReadOnlyList<Physics.MeshWeld.Stream> streams)
    {
        var at = streams.First(s => s.Name == "position").First;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(-float.MaxValue);
        for (var k = 0; k + stride <= vertices.Length; k += stride)
        {
            var p = new Vector3(vertices[k + at], vertices[k + at + 1], vertices[k + at + 2]);
            (min, max) = (Vector3.Min(min, p), Vector3.Max(max, p));
        }
        return (min, max);
    }

    // The closing CMesh_Weld at 1/32, unless the entry is +0x1a0, baked (+0x98) and +0xc0 bit 4.
    private static Built Weld(Built b)
    {
        var h = b.Source.Header;
        if (h.Byte1a0 != 0 && h.Baked && (h.Fieldc0 & 0x10) != 0)
            return b;
        var (v, idx) = Physics.MeshWeld.Weld(b.Vertices, b.Source.Entry.Stride, b.Indices, b.Source.Entry.Streams, 1f / 32f, true);
        return b with { Vertices = v, Indices = idx };
    }

    private static List<Built> FixTJunctions(IReadOnlyList<Node> world)
    {
        var kept = new List<(Node Node, TJunctionFix.Mesh Mesh)>();
        foreach (var n in world)
        {
            var attributes = n.Header.Attributes;
            if ((attributes & 0x400000000) != 0 || (attributes & 0x400) != 0 || (attributes & 0x1000000000) != 0)
                continue;
            kept.Add((n, new TJunctionFix.Mesh
            {
                Vertices = [.. n.Entry.Vertices], Stride = n.Entry.Stride, Indices = [.. n.Entry.Indices],
                Group = n.Header.NodeId,
                Excluded = (attributes & 1) != 0,
                SkipOwnNode = n.Header.Byte1a0 != 0,
            }));
        }
        TJunctionFix.Run([.. kept.Select(k => k.Mesh)]);
        return [.. kept.Select(k => new Built(k.Node, [.. k.Mesh.Vertices], [.. k.Mesh.Indices]))];
    }
}
