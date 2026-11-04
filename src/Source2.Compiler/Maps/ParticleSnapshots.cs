using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Source2.Compiler.Compression;
using Source2.Compiler.Io;
using ValveKeyValue;

namespace Source2.Compiler.Maps;

/// <summary>
/// The particle snapshots a map compile generates: an entity whose snapshot_mesh names a map node
/// (EntityLumpSet's rule, FUN_180f8ee20) makes that node write
/// <c>maps/&lt;map&gt;/particle_snapshots/node_&lt;id&gt;.vsnap</c> (180f7aa30, an auxiliary data file
/// saved as KV3 text), which CompileSnap (1803401b0) compiles to the .vsnap_c the package ships.
/// The node fills the snapshot through its vtable +0x588; an empty one writes no file.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A CMapMesh (181083850): one particle per face corner (half-edge with a face), first the open
/// edge loops' corners, then every other in handle order; position (its vertex's, in the mesh's own
/// space: neither the node's origin nor its scales, s2c_snapprobe's scaled box) and normal.</item>
/// <item>A CMapPath or CMapCable (1810b5100): the path's samples at its particleSnapshotSpacing
/// (1810b8270 over 1812802d0, the cable collision's sampler), with each sample's colour (the nodes'
/// color_tint, sRGB to linear, 18126a5a0) linear along its node pair, and a radius stream left at 0
/// (the radius scale write is gated on a type class generic_float does not have).</item>
/// <item>The world and a CMapEntity (180f7a100) fill nothing, so they write no file.</item>
/// </list>
/// Not ported (refused): other node types; the third mesh stream 181083850 registers, "color" (mesh
/// +0x10de, a type-3 stream not identified), so a mesh with a vertex or face-vertex stream beyond the
/// ones it is known not to read; a path with children other than its nodes (180f7a100 returns before
/// the fill when a child's vf 0x130 answers); prefab and instance placements (EntityLumpSet refuses
/// those first). The DMX arrays are taken as the runtime mesh's handles and dense order (index =
/// handle): the loader is not read, but Valve's corner sweep comes out in index order.
/// </remarks>
public static class ParticleSnapshots
{
    /// <summary>One generated snapshot: its source text and its compiled file, by package path.</summary>
    public sealed record File(string SourcePath, string Source, string CompiledPath, byte[] Compiled);

    /// <summary>
    /// One snapshot stream: its name and type in the saved .vsnap (the stream type table 1830149f0),
    /// the attribute type CompileSnap gives it (181908020) and its values, <paramref name="Components"/> floats each.
    /// </summary>
    public sealed record Stream(string Name, string SourceType, string AttributeType, int Components, float[] Values);

    /// <summary>A snapshot: its particle count and its streams in registration order.</summary>
    public sealed record Snapshot(int Count, IReadOnlyList<Stream> Streams);

    // Streams 181083850 does not read: it takes position (vertex), normal and an unidentified
    // "color" stream (face-vertex side). Anything outside this set might be that colour.
    private static readonly HashSet<string> VertexStreamsKnown = new(StringComparer.Ordinal) { "position" };
    private static readonly HashSet<string> CornerStreamsKnown = new(StringComparer.Ordinal)
    {
        "texcoord", "normal", "tangent", "PerVertexLighting", "VertexPaintBlendParams",
    };

    /// <summary>
    /// The snapshots of <paramref name="document"/>'s entities, in the order their nodes are first named;
    /// a node named twice is written once.
    /// </summary>
    public static IReadOnlyList<File> Build(DmxBinary.Document document, IReadOnlyList<MapEntities.Entity> entities,
                                            FgdSchema? schema, string addon, string worldName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(entities);
        worldName = Tier0Strings.LowerAscii(worldName);
        var nodes = MapNodes(document);
        var files = new List<File>();
        var done = new HashSet<int>();
        foreach (var entity in entities)
        {
            if (EntityLumpSet.SnapshotNode(entity, nodes.Keys) is not { } id || !done.Add(id))
                continue;
            // 180f7a100 is the world's and an entity's fill: it adds nothing.
            if (id == 1 || !nodes.TryGetValue(id, out var node) || node.Type is "CMapWorld" or "CMapEntity")
                continue;
            var snapshot = node.Type switch
            {
                "CMapMesh" => FromMesh(node.Get<DmxBinary.Element>("meshData")
                                       ?? throw new InvalidDataException($"node {id}: CMapMesh without meshData"), id),
                "CMapPath" or "CMapCable" => FromPath(node, entities.FirstOrDefault(e => e.NodeId == id && e.PathNodes is not null)
                                                            ?? throw new InvalidDataException($"node {id}: a path the entity walk does not list"),
                                                      schema, id),
                _ => throw new NotSupportedException($"{entity.ClassName} {entity.NodeId}: a particle snapshot from a {node.Type} is not ported"),
            };
            if (snapshot.Count == 0)
                continue;
            var source = $"maps/{worldName}/particle_snapshots/node_{id.ToString(CultureInfo.InvariantCulture)}.vsnap";
            var text = SourceText(snapshot);
            var crc = System.IO.Hashing.Crc32.HashToUInt32(Encoding.UTF8.GetBytes(text));
            files.Add(new File(source, text, source + "_c", Compile(snapshot, new SavedSource(source, $"csgo_addons/{addon}", crc))));
        }
        return files;
    }

    private static Dictionary<int, DmxBinary.Element> MapNodes(DmxBinary.Document document)
    {
        var nodes = new Dictionary<int, DmxBinary.Element>();
        var stack = new Stack<DmxBinary.Element>(document.OfType("CMapWorld"));
        while (stack.TryPop(out var node))
        {
            if (node.GetValue<int>("nodeID") is { } id)
                nodes.TryAdd(id, node);
            foreach (var child in node.GetElements("children"))
                stack.Push(child);
        }
        return nodes;
    }

    private static float[] Flat(IEnumerable<Vector3> v) => [.. v.SelectMany(x => new[] { x.X, x.Y, x.Z })];

    /// <summary>181083850 over a CMapMesh's polygon mesh: position (0xc) and, when the mesh has it, normal (0xd).</summary>
    internal static Snapshot FromMesh(DmxBinary.Element data, int id)
    {
        foreach (var (array, known) in new[] { ("vertexData", VertexStreamsKnown), ("faceVertexData", CornerStreamsKnown) })
            foreach (var held in data.Get<DmxBinary.Element>(array)?.GetElements("streams") ?? [])
                if (!known.Contains(held.Name.Split(':')[0]))
                    throw new NotSupportedException($"node {id}: a particle snapshot from a mesh with a {array} stream {held.Name} is not ported");

        var mesh = new FaceArrays(data);
        var vertexOut = (data.Get<object?[]>("vertexEdgeIndices") ?? []).Select(x => x is int i ? i : -1).ToArray();
        var corners = Corners(mesh, vertexOut, id);

        var streams = new List<Stream> { new("position", "position_3d", "float3", 3, Flat(corners.Select(c => mesh.Pos(mesh.To[c])))) };
        if (data.Get<DmxBinary.Element>("faceVertexData")?.GetElements("streams")
                .FirstOrDefault(s => s.Name.StartsWith("normal:", StringComparison.Ordinal))?.Get<object?[]>("data") is { } normals)
            streams.Add(new("normal", "normal_3d", "float3", 3, Flat(corners.Select(c => (Vector3)normals[mesh.CornerData[c]]!))));
        return new Snapshot(corners.Count, streams);
    }

    // 18126a5a0's 1/255, as stored (0x182a9e7e8).
    private static readonly float ByteToUnit = BitConverter.Int32BitsToSingle(0x3b808081);

    /// <summary>
    /// 1810b5100 over a CMapPath or CMapCable: 1810b8270 (mode 0, the path's own space) at the node's
    /// particleSnapshotSpacing, position (0xc), color (0xb) and radius (3) per sample.
    /// </summary>
    internal static Snapshot FromPath(DmxBinary.Element node, MapEntities.Entity path, FgdSchema? schema, int id)
    {
        foreach (var child in node.GetElements("children"))
            if (child.Type != "CMapPathNode")
                throw new NotSupportedException($"node {id}: a path snapshot with a {child.Type} child is not ported");
        var spacing = node.GetValue<float>("particleSnapshotSpacing")
                      ?? throw new InvalidDataException($"node {id}: a path without particleSnapshotSpacing");
        var (controls, _) = EntityLumpAuthor.PathControls(path, schema);
        var (samples, starts) = Physics.CableCollision.Samples(controls, spacing);
        if (samples.Length < 1)
            return new Snapshot(0, []);

        // The same node order as PathControls: a wrapping loop repeats the first.
        var nodes = path.PathNodes!;
        var order = Enumerable.Range(0, nodes.Count).ToList();
        if (EntityLumpAuthor.Wraps(path, schema))
            order.Add(0);
        var tints = order.Select(i => Tint(nodes[i])).ToArray();
        var colours = new Vector3[samples.Length];
        for (var k = 0; k < starts.Length - 1; k++)
        {
            var (a, b) = (tints[k], tints[k + 1]);
            var span = starts[k + 1] - starts[k];
            for (var i = 0; i <= span; i++)
            {
                var f = (float)i / span;
                colours[starts[k] + i] = new Vector3(((b.X - a.X) * f) + a.X, a.Y + ((b.Y - a.Y) * f), a.Z + ((b.Z - a.Z) * f));
            }
        }
        return new Snapshot(samples.Length,
        [
            new("position", "position_3d", "float3", 3, Flat(samples)),
            new("color", "generic_vector_3d", "float3", 3, Flat(colours)),
            // 1810b5100 writes a sample's radius scale only when the stream's type class
            // (18237a0a0[type]) is 4, but generic_float (3) is class 2: the stream keeps its zeros.
            new("radius", "generic_float", "float", 1, new float[samples.Length]),
        ]);
    }

    /// <summary>
    /// A path node's colour (181112010): its color_tint (vf 0x618 18110f230: 180f31d40, white when the
    /// node has no such key, else V_sscanf "%d %d %d", each the low byte), as 18126a5a0 makes it linear:
    /// each byte times 1/255, clamped to [0, 1], then x / 12.92 up to 0.04045 and V_powf((x + 0.055) /
    /// 1.055, 2.4) above.
    /// </summary>
    internal static Vector3 Tint(MapEntities.PathNode node)
    {
        var text = node.Keys.FirstOrDefault(k => k.Key.EqualsAscii("color_tint")).Value;
        int r = 255, g = 255, b = 255;
        if (text is not null)
        {
            r = g = b = 0;
            var at = 0;
            if (Scan(text, ref at, out var x))
            {
                r = x;
                if (Scan(text, ref at, out x))
                {
                    g = x;
                    if (Scan(text, ref at, out x))
                        b = x;
                }
            }
        }
        return new Vector3(Linear((byte)r), Linear((byte)g), Linear((byte)b));
    }

    private static float Linear(byte value)
    {
        var x = value * ByteToUnit;
        x = x < 0f ? 0f : x > 1f ? 1f : x;
        return x <= 0.04045f ? x / 12.92f : Simulation.CrtMath.Pow((x + 0.055f) / 1.055f, 2.4f);
    }

    // sscanf's %d: white space, an optional sign, decimal digits (none is a failure).
    private static bool Scan(string text, ref int at, out int value)
    {
        value = 0;
        while (at < text.Length && char.IsWhiteSpace(text[at]))
            at++;
        var negative = false;
        if (at < text.Length && text[at] is '+' or '-')
            negative = text[at++] == '-';
        var start = at;
        long v = 0;
        while (at < text.Length && text[at] is >= '0' and <= '9')
            v = (v * 10) + (text[at++] - '0');
        if (at == start)
            return false;
        value = unchecked((int)(negative ? -v : v));
        return true;
    }

    /// <summary>
    /// The corner half-edges 181083850 makes particles of, in order: for each entry of each open edge
    /// loop (<see cref="OpenEdgeLoops"/>) the corner on the edge's face that ends at the edge's tail
    /// there, then every half-edge with a face, by handle; each once (its sorted set).
    /// </summary>
    internal static List<int> Corners(FaceArrays mesh, int[] vertexOut, int id)
    {
        var seen = new HashSet<int>();
        var order = new List<int>();
        foreach (var loop in OpenEdgeLoops(mesh))
            foreach (var edge in loop)
            {
                // 18139a960: the edge's face, or its twin's when it has none.
                var face = mesh.EdgeFace[edge] >= 0 ? mesh.EdgeFace[edge] : mesh.EdgeFace[mesh.Opposite[edge]];
                // 1813a2d80: the edge's half-edge on that face; 1813a3180: its tail (its twin's vertex).
                var side = mesh.EdgeFace[edge] == face ? edge : mesh.Opposite[edge];
                var tail = mesh.To[mesh.Opposite[side]];
                // 18138be80: around the tail from its out half-edge, the incoming one on the face.
                var corner = IncomingOnFace(mesh, vertexOut, tail, face)
                             ?? throw new NotSupportedException($"node {id}: an open edge whose vertex has no corner on its face");
                if (seen.Add(corner))
                    order.Add(corner);
            }
        for (var h = 0; h < mesh.Next.Length; h++)
            if (mesh.EdgeFace[h] >= 0 && seen.Add(h))
                order.Add(h);
        return order;
    }

    // 18138be80: h = the vertex's out half-edge; its twin when the twin is on the face, else on to the
    // twin's next, until back at the start.
    private static int? IncomingOnFace(FaceArrays mesh, int[] vertexOut, int vertex, int face)
    {
        var start = vertexOut[vertex];
        var h = start;
        do
        {
            var twin = mesh.Opposite[h];
            if (mesh.EdgeFace[twin] == face)
                return twin;
            h = mesh.Next[twin];
        }
        while (h != start);
        return null;
    }

    /// <summary>
    /// 181394380 over 180ff0100's edge list: each edge once, as its half-edge with the lower handle,
    /// in dense order. The open half-edges are those edges' face-less sides where the other has a face.
    /// A loop starts at the first open half-edge left and follows next; when that walk leaves the set,
    /// the loop starts instead at the first half-edge back in it after one outside. Each half-edge
    /// joins its loop as its edge (the lower handle of it and its twin) and leaves the set, the last
    /// open half-edge taking its place.
    /// </summary>
    internal static List<List<int>> OpenEdgeLoops(FaceArrays mesh)
    {
        var open = new List<int>();
        for (var h = 0; h < mesh.Next.Length; h++)
        {
            var twin = mesh.Opposite[h];
            if (h >= twin)
                continue;
            bool faced = mesh.EdgeFace[h] >= 0, twinFaced = mesh.EdgeFace[twin] >= 0;
            if (!faced && twinFaced)
                open.Add(h);
            else if (!twinFaced && faced)
                open.Add(twin);
        }

        var loops = new List<List<int>>();
        while (open.Count > 0)
        {
            var first = open[0];
            var at = first;
            var previous = 0;
            int found;
            // The walk for a start: back at the first, or into the set after a step outside it.
            while (true)
            {
                at = mesh.Next[at];
                found = open.IndexOf(at);
                if (previous == -1 && found != -1)
                    break;
                previous = found;
                if (at == first)
                    break;
            }
            if (found == -1)
                break;
            var loop = new List<int>();
            while (found != -1)
            {
                loop.Add(Math.Min(at, mesh.Opposite[at]));
                var last = open.Count - 1;
                open[found] = open[last];
                open.RemoveAt(last);
                at = mesh.Next[at];
                found = open.IndexOf(at);
            }
            loops.Add(loop);
        }
        return loops;
    }

    /// <summary>
    /// The saved .vsnap (180f7aa30, SaveKV3 text generic): 1812f5060's stream_data, 181322440's
    /// num_values and streams, 1813220e0's name, type and values: a vector each for a float3 stream
    /// (case 5), a number for a float one (case 2). No string_list (none written).
    /// </summary>
    public static string SourceText(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var streams = KVObject.Array();
        foreach (var s in snapshot.Streams)
        {
            var list = KVObject.Array();
            for (var i = 0; i < snapshot.Count; i++)
            {
                if (s.Components == 1)
                {
                    list.Add(new KVObject((double)s.Values[i]));
                    continue;
                }
                var vector = KVObject.Array();
                for (var c = 0; c < s.Components; c++)
                    vector.Add(new KVObject((double)s.Values[(i * s.Components) + c]));
                list.Add(vector);
            }
            var stream = KVObject.Collection();
            stream.Add("name", new KVObject(s.Name));
            stream.Add("type", new KVObject(s.SourceType));
            stream.Add("values", list);
            streams.Add(stream);
        }
        var data = KVObject.Collection();
        data.Add("num_values", new KVObject(snapshot.Count));
        data.Add("streams", streams);
        var root = KVObject.Collection();
        root.Add("stream_data", data);
        return Kv3TextWriter.Write(root, Guid.Parse("7412167c-06e9-4698-aff2-e63eb59037e7"));
    }

    /// <summary>
    /// CompileSnap (1803401b0, 18033fe60): DATA is num_particles and one attribute per stream (name by
    /// 181907da0 / 181664870: position 0, radius 3, color 6, normal 0x15; type by 181908020), data_offset
    /// and data_size into the attribute buffer, which SNAP holds through FastCompress.
    /// </summary>
    public static byte[] Compile(Snapshot snapshot, SavedSource saved)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var buffer = new List<byte>();
        var attributes = KVObject.Array();
        foreach (var s in snapshot.Streams)
        {
            var bytes = MemoryMarshal.AsBytes(s.Values.AsSpan());
            var attribute = KVObject.Collection();
            attribute.Add("name", new KVObject(s.Name));
            attribute.Add("type", new KVObject(s.AttributeType));
            attribute.Add("data_offset", new KVObject(buffer.Count));
            attribute.Add("data_size", new KVObject(bytes.Length));
            attributes.Add(attribute);
            buffer.AddRange(bytes);
        }
        var root = KVObject.Collection();
        root.Add("num_particles", new KVObject(snapshot.Count));
        root.Add("attributes", attributes);
        return Source2ContainerAuthor.AuthorParticleSnapshot(root, BlockCompress.FastCompress(buffer.ToArray()), saved);
    }
}
