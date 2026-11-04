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
/// </summary>
/// <remarks>
/// A CMapMesh fills the snapshot with 181083850 (its vtable +0x588), one particle per face corner
/// (half-edge with a face): first the open edge loops' corners, then every other in handle order;
/// each takes its vertex's position and the corner's normal. Not ported (refused): the world's and
/// CMapPath's snapshot (1810b5100, particleSnapshotSpacing) and any other node type; the third
/// stream 181083850 registers, "color" (mesh +0x10de, a type-3 stream not identified), so a mesh
/// with a vertex or face-vertex stream beyond the ones it is known not to read; a node with scales
/// other than 1 (where +0x498's mesh is in the node's space is not read); prefab and instance
/// placements (EntityLumpSet refuses those first).
/// The DMX arrays are taken as the runtime mesh's handles and dense order (index = handle): the
/// loader is not read, but Valve's corner sweep comes out in index order on probe_classes.
/// </remarks>
public static class ParticleSnapshots
{
    /// <summary>One generated snapshot: its source text and its compiled file, by package path.</summary>
    public sealed record File(string SourcePath, string Source, string CompiledPath, byte[] Compiled);

    // Streams 181083850 does not read: it takes position (vertex), normal and an unidentified
    // "color" stream (face-vertex side). Anything outside this set might be that colour.
    private static readonly HashSet<string> VertexStreamsKnown = new(StringComparer.Ordinal) { "position" };
    private static readonly HashSet<string> CornerStreamsKnown = new(StringComparer.Ordinal)
    {
        "texcoord", "normal", "tangent", "PerVertexLighting", "VertexPaintBlendParams",
    };

    /// <summary>
    /// The snapshots of <paramref name="document"/>'s entities, in the order their nodes are first named;
    /// a node named twice is written once. A mesh with no face makes none (180f7aa30's "is empty").
    /// </summary>
    public static IReadOnlyList<File> Build(DmxBinary.Document document, IReadOnlyList<MapEntities.Entity> entities,
                                            string addon, string worldName)
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
            if (id == 1 || !nodes.TryGetValue(id, out var node))
                throw new NotSupportedException($"{entity.ClassName} {entity.NodeId}: the world's particle snapshot is not ported");
            if (node.Type != "CMapMesh")
                throw new NotSupportedException($"{entity.ClassName} {entity.NodeId}: a particle snapshot from a {node.Type} is not ported");
            if (node.GetValue<Vector3>("scales") is { } s && s != Vector3.One)
                throw new NotSupportedException($"node {id}: a particle snapshot from a scaled mesh is not ported");
            var data = node.Get<DmxBinary.Element>("meshData") ?? throw new InvalidDataException($"node {id}: CMapMesh without meshData");
            var snapshot = FromMesh(data, id);
            if (snapshot.Count == 0)
                continue;
            var source = $"maps/{worldName}/particle_snapshots/node_{id.ToString(CultureInfo.InvariantCulture)}.vsnap";
            var text = SourceText(snapshot);
            var crc = System.IO.Hashing.Crc32.HashToUInt32(Encoding.UTF8.GetBytes(text));
            files.Add(new File(source, text, source + "_c", Compile(snapshot, new SavedSource(source, $"csgo_addons/{addon}", crc))));
        }
        return files;
    }

    /// <summary>A snapshot's particles: positions, and normals when the mesh has them.</summary>
    public sealed record Snapshot(Vector3[] Positions, Vector3[]? Normals)
    {
        public int Count => Positions.Length;
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

    /// <summary>181083850 over a CMapMesh's polygon mesh.</summary>
    internal static Snapshot FromMesh(DmxBinary.Element data, int id)
    {
        foreach (var (array, known) in new[] { ("vertexData", VertexStreamsKnown), ("faceVertexData", CornerStreamsKnown) })
            foreach (var held in data.Get<DmxBinary.Element>(array)?.GetElements("streams") ?? [])
                if (!known.Contains(held.Name.Split(':')[0]))
                    throw new NotSupportedException($"node {id}: a particle snapshot from a mesh with a {array} stream {held.Name} is not ported");

        var mesh = new FaceArrays(data);
        var vertexOut = (data.Get<object?[]>("vertexEdgeIndices") ?? []).Select(x => x is int i ? i : -1).ToArray();
        var corners = Corners(mesh, vertexOut, id);

        var positions = new Vector3[corners.Count];
        for (var i = 0; i < corners.Count; i++)
            positions[i] = mesh.Pos(mesh.To[corners[i]]);
        Vector3[]? normals = null;
        if (data.Get<DmxBinary.Element>("faceVertexData")?.GetElements("streams")
                .FirstOrDefault(s => s.Name.StartsWith("normal:", StringComparison.Ordinal))?.Get<object?[]>("data") is { } stream)
        {
            normals = new Vector3[corners.Count];
            for (var i = 0; i < corners.Count; i++)
                normals[i] = (Vector3)stream[mesh.CornerData[corners[i]]]!;
        }
        return new Snapshot(positions, normals);
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
    /// num_values and streams, 1813220e0's name, type (the stream type names at 1830149f0:
    /// position_3d 0xc, normal_3d 0xd) and values, a vector each. No string_list (none written).
    /// </summary>
    public static string SourceText(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var streams = KVObject.Array();
        foreach (var (name, type, values) in Streams(snapshot))
        {
            var list = KVObject.Array();
            foreach (var v in values)
            {
                var vector = KVObject.Array();
                vector.Add(new KVObject((double)v.X));
                vector.Add(new KVObject((double)v.Y));
                vector.Add(new KVObject((double)v.Z));
                list.Add(vector);
            }
            var stream = KVObject.Collection();
            stream.Add("name", new KVObject(name));
            stream.Add("type", new KVObject(type));
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

    private static IEnumerable<(string Name, string Type, Vector3[] Values)> Streams(Snapshot snapshot)
    {
        // 181083850 registers position (0xc), normal (0xd) and color (0xb), each only when the mesh has it.
        yield return ("position", "position_3d", snapshot.Positions);
        if (snapshot.Normals is { } normals)
            yield return ("normal", "normal_3d", normals);
    }

    /// <summary>
    /// CompileSnap (1803401b0, 18033fe60): DATA is num_particles and one attribute per stream (name by
    /// 181907da0 / 181664870: position 0, normal 0x15; type float3 by 181908020), data_offset and
    /// data_size into the attribute buffer, which SNAP holds through FastCompress.
    /// </summary>
    public static byte[] Compile(Snapshot snapshot, SavedSource saved)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var buffer = new List<byte>();
        var attributes = KVObject.Array();
        foreach (var (name, _, values) in Streams(snapshot))
        {
            var bytes = MemoryMarshal.AsBytes(values.AsSpan());
            var attribute = KVObject.Collection();
            attribute.Add("name", new KVObject(name));
            attribute.Add("type", new KVObject("float3"));
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
