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

    /// <summary>One entry of a merged list as CVisibilityMeshMerger::MergeMeshes takes it.</summary>
    public sealed record MergerInput(string Material, int Stride, float[] Vertices, int[] Indices, WrbMeshEntry Facts);

    /// <summary>
    /// The merged lists' entries (the aggregate, overlay and base lists of
    /// <see cref="MeshLists.Merged"/>) from BuildNode's output and its overlay
    /// meshes, as the merger takes them:
    /// <list type="bullet">
    /// <item>World and prop entries go to the list CompileNode assigns
    /// (<see cref="MeshLists.Assign"/>) with their header's facts, the
    /// cubemap and light probe <see cref="EnvMaps"/> picked.</item>
    /// <item>Overlays take GenerateOverlayMeshes' record (zero flags, +0x98
    /// set), their render order as overlay order, object flag 0x2000 in the
    /// overlay list (COverlayMeshList::OnAdd), their own tintColor / 255 as
    /// the CMesh tint when not white, their target entry's texcoord
    /// precision, and the 1/32 weld of BuildNode.</item>
    /// <item>PrepareForMerge's stream filter (<see cref="MaterialStreams"/>)
    /// on each entry without attribute bit 0 or 11; a stream's type is 39
    /// plus its float count unless it carries one.</item>
    /// </list>
    /// <paramref name="keepLighting"/> is the lists' baked-lighting switch
    /// (setting byte +4), off in a -world build.
    /// </summary>
    public static Dictionary<MeshLists.Kind, List<MergerInput>> MergerInputs(IReadOnlyList<Built> built, IReadOnlyList<NodeOverlays.Projection> overlays,
                                                                             IReadOnlyList<NodeOverlays.Descriptor> descriptors, IReadOnlyList<(int Cubemap, int Probe)> envMaps,
                                                                             GameContent content, ShaderLibrary shaders, bool keepLighting = false)
    {
        (int Stride, float[] Vertices, List<WrbMeshEntry.Stream> Streams) Filtered(string material, float[] vertices, int stride,
                                                                                    IReadOnlyList<Physics.MeshWeld.Stream> streams, bool precise, ulong attributes)
        {
            var kept = Enumerable.Range(0, streams.Count).ToList();
            // PrepareForMerge passes over an entry with attribute bit 0 or 11 (& 0x801).
            if ((attributes & 0x801) == 0 && content.Read(material + "_c") is { } bytes)
                kept = MaterialStreams.Kept(streams, MaterialStreams.Inputs(Source2.Compiler.MaterialAuthor.ExtractInputSignature(bytes).Select(x => x.Semantic)),
                                            keepLighting && (attributes & 0x20000000) != 0);
            var newStride = kept.Sum(i => streams[i].Count);
            var count = vertices.Length / stride;
            var v = new float[count * newStride];
            for (var k = 0; k < count; k++)
            {
                var to = k * newStride;
                foreach (var i in kept)
                {
                    Array.Copy(vertices, (k * stride) + streams[i].First, v, to, streams[i].Count);
                    to += streams[i].Count;
                }
            }
            var described = kept.Select(i => new WrbMeshEntry.Stream(streams[i].Name, streams.Take(i).Count(x => x.Name == streams[i].Name),
                streams[i].Count, (byte)(precise && streams[i].Name == "texcoord" ? 1 : 0),
                streams[i].Type != 0 ? streams[i].Type : 39 + streams[i].Count)).ToList();
            return (newStride, v, described);
        }

        var lists = MeshLists.Merged.ToDictionary(k => k, _ => new List<MergerInput>());
        float[] identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];
        for (var i = 0; i < built.Count; i++)
        {
            var b = built[i];
            var (e, h) = (b.Source.Entry, b.Source.Header);
            if (MeshLists.Assign(new MeshLists.Input(h.Attributes, h.ObjectFlags, 0, e.Record.FadeMax, false, false)) is not { } kind || !lists.TryGetValue(kind, out var list))
                continue;
            var (stride, v, streams) = Filtered(e.Material, b.Vertices, e.Stride, e.Streams, e.PreciseTexcoords, h.Attributes);
            list.Add(new MergerInput(e.Material, stride, v, b.Indices, new WrbMeshEntry
            {
                Attributes = h.Attributes, Material = e.Material, ObjectFlags = h.ObjectFlags, Field28 = h.Tint, Stride = stride, Streams = streams,
                Field1a3 = h.Byte1a3, Field_b8 = h.EmissiveBoost, Field1a8 = 1f, Cubemap = envMaps[i].Cubemap, LightProbe = envMaps[i].Probe,
                Mesh184 = 1f, Field9c = h.Field9c, Field1a1 = h.Byte1a1, Field1a0 = h.Byte1a0, Field_b0 = h.FadeMin, FadeMax = h.FadeMax,
                Matrix = identity,
            }));
        }
        foreach (var projection in overlays)
        {
            var d = descriptors[projection.Overlay];
            var attributes = content.Material(d.Material) is { } info ? MaterialAttributes.Of(info, shaders, content.TextureSize) : MaterialAttributes.Empty;
            var flags = MeshEntryFlags.Compute(attributes, new MeshEntryFlags.Record(0, 0, 0, true, null, false)).Flags;
            if (MeshLists.Assign(new MeshLists.Input(flags, 0, d.RenderOrder, 0, false, false)) is not { } kind || !lists.TryGetValue(kind, out var list))
                continue;
            var mesh = projection.Mesh;
            var (wv, wi) = Physics.MeshWeld.Weld(mesh.Vertices, mesh.Stride, [.. Enumerable.Range(0, mesh.Vertices.Length / mesh.Stride)], mesh.Streams, 1f / 32f, true);
            var (stride, v, streams) = Filtered(d.Material, wv, mesh.Stride, mesh.Streams, built[projection.Target].Source.Entry.PreciseTexcoords, flags);
            var tinted = d.TintColor.Any(x => x != 255);
            list.Add(new MergerInput(d.Material, stride, v, wi, new WrbMeshEntry
            {
                Attributes = flags, Material = d.Material, OverlayOrder = d.RenderOrder, ObjectFlags = kind == MeshLists.Kind.Overlay ? 0x2000u : 0u,
                DebugColor = tinted,
                DebugColorValue = tinted ? new Vector4(d.TintColor[0] / 255f, d.TintColor[1] / 255f, d.TintColor[2] / 255f, d.TintColor[3] / 255f) : default,
                Field28 = Vector4.One, Stride = stride, Streams = streams, Field1a3 = 1, Field_b8 = 1f, Field1a8 = 1f, Mesh184 = 1f, Field9c = 1f,
                Field1a1 = 1, Field_b0 = -1f, Matrix = identity,
            }));
        }
        return lists;
    }

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
