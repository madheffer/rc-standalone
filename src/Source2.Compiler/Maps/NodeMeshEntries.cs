using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The world renderer builder's node mesh entries as BuildNode
/// (18024b400) receives them: each visible world mesh's face sets, one per
/// (lightmap scale bias, material), in walk order, one vertex per triangle
/// corner. A corner carries position, texcoord, normal, tangent and, when the
/// mesh has it, PerVertexLighting (<see cref="MapMeshCorners"/>, cut on world
/// positions). Hammer's mesh export moves positions to the world by the
/// node's matrix and rotates normals and tangents by it
/// (HammerMesh_TransformToWorld, 1810c4e50); the builder's mesh then holds
/// them renormalised: squares summed z, y, x, times the reciprocal of the
/// root (measured exact on probe01; the function doing it is not read).
/// PerVertexLighting is baked lighting Hammer applies from a previous bake
/// (180f0ea90), the .vmap's own stream otherwise; it belongs to the baked
/// halves and is taken as given.
/// </summary>
internal static class NodeMeshEntries
{
    public sealed record Entry(int NodeId, string Material, int Stride, IReadOnlyList<Physics.MeshWeld.Stream> Streams, float[] Vertices, int[] Indices)
    {
        /// <summary>The corners before the world move, for diagnosis.</summary>
        public float[] Stored { get; init; } = [];

        /// <summary>The matrix normals and tangents turned by.</summary>
        public float[] Turn { get; init; } = [];

        /// <summary>
        /// The builder record's fields from the source node (<see cref="MeshEntryFlags.Record"/>):
        /// a mesh's disableShadows and disablemerging, a prop's disableshadows and
        /// donotcollapse or disablemerging, and either's fademaxdist.
        /// </summary>
        public MeshEntryFlags.Record Record { get; init; } = new(0, 0, 0, false, null, false);

        /// <summary>The node it came from: the CMapMesh element, or a prop's entity_properties.</summary>
        public DmxBinary.Element? Source { get; init; }

        /// <summary>The ids of the instances the node was reached through, outermost first (none outside instances).</summary>
        public int[] Instances { get; init; } = [];
    }

    /// <param name="keepsTexcoords">Whether a material stops the texcoord shift
    /// (<see cref="MapMeshCorners.KeepsTexcoords"/>); none does when absent.</param>
    /// <param name="signature">A material's vertex input semantics (its INSG); with it
    /// the corners carry the mesh's streams as <see cref="WithPaintStreams"/> sets them,
    /// without it the short layout (texcoord, normal, tangent, PerVertexLighting).</param>
    /// <param name="rendersAsWorld">Whether an entity class renders as world (its FGD metadata
    /// sets render_as_world_but_physics_as_entity: func_water); its meshes join the world's,
    /// as the trace scene's do (<see cref="MapGeometry"/>). None does when absent.</param>
    public static List<Entry> FromWorld(DmxBinary.Document doc, Func<string, bool>? keepsTexcoords = null,
                                        Func<string, IReadOnlyCollection<string>>? signature = null, Func<string, bool>? rendersAsWorld = null)
    {
        var entries = new List<Entry>();
        foreach (var mesh in MapMeshes.Read(doc))
        {
            // A mesh under the world or a group, or under an entity whose class renders as world.
            if (mesh.Element is null || mesh.Hidden
                || (mesh.ParentType == "CMapEntity" && !(mesh.ParentClass is { } cls && rendersAsWorld?.Invoke(cls) == true)))
                continue;
            var names = mesh.Element.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials")?.OfType<string>().ToArray() ?? [];
            var shift = keepsTexcoords is null || !names.Any(keepsTexcoords);
            var world = mesh.World;
            // Normals and tangents turn by the node's own matrix (vtable 0xa0,
            // AngleMatrix with the origin), whose zeros keep their signs:
            // mesh.World went through Concat, which adds +0 and so loses a -0
            // that decides the sign of a zero component.
            var turn = mesh.Instances.Length == 0 ? MapMeshes.Local(mesh.Element) : world;
            var pieces = MapMeshCorners.Build(mesh.Element, shift, p => MapMeshes.Transform(world, p), withTangent: true, allStreams: signature != null);
            if (signature != null)
            {
                var needs = names.SelectMany(signature).ToHashSet();
                pieces = [.. pieces.Select(p => WithPaintStreams(p, needs))];
            }
            foreach (var piece in pieces)
            {
                var v = (float[])piece.Vertices.Clone();
                var s = piece.Stride;
                var directions = piece.Streams.Where(x => x.Name is "normal" or "tangent").Select(x => x.First).ToArray();
                for (var c = 0; c < v.Length / s; c++)
                {
                    var at = c * s;
                    var p = MapMeshes.Transform(world, new Vector3(v[at], v[at + 1], v[at + 2]));
                    (v[at], v[at + 1], v[at + 2]) = (p.X, p.Y, p.Z);
                    foreach (var first in directions)
                    {
                        var d = Normalise(Rotate(turn, new Vector3(v[at + first], v[at + first + 1], v[at + first + 2])));
                        (v[at + first], v[at + first + 1], v[at + first + 2]) = (d.X, d.Y, d.Z);
                    }
                }
                var name = piece.Material < names.Length ? names[piece.Material] : "";
                entries.Add(new Entry(mesh.NodeId, name, s, piece.Streams, v, piece.Indices) { Stored = piece.Vertices, Turn = turn, Record = RecordOf(mesh.Element), Source = mesh.Element, Instances = mesh.Instances });
            }
        }
        return entries;
    }

    /// <summary>
    /// WRB_CollectMeshEntries' record from CMapMesh_ConvertMeshForBuilder's
    /// buffer: the lighting mode is disableShadows (+0x3b6c, the buffer's
    /// +0x35), attribute bit 2 is disablemerging (vf 0x7e8, +0x3b6b, the
    /// buffer's +0x37); bits 37 and 38 (the buffer's +0x44 of 1 or 2) are
    /// not mapped to a key yet.
    /// </summary>
    internal static MeshEntryFlags.Record RecordOf(DmxBinary.Element mesh)
    {
        var mode = mesh.Attributes.GetValueOrDefault("disableShadows") is { } d ? Convert.ToByte(d, System.Globalization.CultureInfo.InvariantCulture) : (byte)0;
        var merge = mesh.Attributes.GetValueOrDefault("disablemerging") is true;
        var fade = mesh.Attributes.GetValueOrDefault("fademaxdist") is { } f ? Convert.ToSingle(f, System.Globalization.CultureInfo.InvariantCulture) : 0f;
        return new MeshEntryFlags.Record(mode, merge ? 2UL : 0UL, fade, false, null, false);
    }

    /// <summary>
    /// The streams a mesh's corners carry: its own faceVertexData streams in
    /// the .vmap's order and, when any of its materials consumes
    /// LowPrecisionUv1, a second texcoord after the first and
    /// VertexPaintBlendParams at the end where missing, zero filled. Measured
    /// on all 538 atixref entries matched to a mesh, subdivided ones included
    /// (a mesh storing VertexPaintBlendParams with no such material keeps its
    /// own streams); the code doing it is not read.
    /// </summary>
    internal static MapMeshCorners.Piece WithPaintStreams(MapMeshCorners.Piece piece, IReadOnlySet<string> needs)
    {
        var names = piece.Streams.Select(x => x.Name).ToList();
        var blend = needs.Contains("LowPrecisionUv1");
        if (!blend)
            return piece;
        var layout = piece.Streams.Select(x => (x.Name, x.Count, Source: (int?)x.First)).ToList();
        if (names.Count(x => x == "texcoord") < 2)
            layout.Insert(names.IndexOf("texcoord") + 1, ("texcoord", 2, null));
        if (!names.Contains("VertexPaintBlendParams"))
            layout.Add(("VertexPaintBlendParams", 4, null));
        var stride = layout.Sum(x => x.Count);
        var corners = piece.Vertices.Length / piece.Stride;
        var v = new float[corners * stride];
        for (var c = 0; c < corners; c++)
        {
            var at = c * stride;
            foreach (var (_, count, source) in layout)
            {
                if (source is { } from)
                    Array.Copy(piece.Vertices, c * piece.Stride + from, v, at, count);
                at += count;
            }
        }
        var streams = new List<Physics.MeshWeld.Stream>();
        var first = 0;
        foreach (var (name, count, _) in layout)
        {
            var old = piece.Streams.FirstOrDefault(x => x.Name == name);
            streams.Add(new Physics.MeshWeld.Stream(name, first, count, false, old.Name == name ? old.Type : 0));
            first += count;
        }
        return piece with { Stride = stride, Vertices = v, Streams = streams };
    }

    /// <summary>
    /// Matrix3x4_Rotate (18125d1b0): (x r0 + y r1) + z r2 per row, plus zero
    /// times the row's translation. That last term only decides the sign of a
    /// zero result; it makes probe01's and cardtest's entries exact, but 16
    /// atixref corners still differ in a zero's sign, so where Valve's signs
    /// come from (the DMX round trip or the renormalisation) is open:
    /// GROUND_TRUTH, "signed zeros in node entry normals".
    /// </summary>
    internal static Vector3 Rotate(float[] m, Vector3 d)
        => new(m[0] * d.X + m[1] * d.Y + m[2] * d.Z + 0f * m[3], m[4] * d.X + m[5] * d.Y + m[6] * d.Z + 0f * m[7], m[8] * d.X + m[9] * d.Y + m[10] * d.Z + 0f * m[11]);

    /// <summary>Squares summed z, y, x; each component times the reciprocal of the root; zero stays zero.</summary>
    internal static Vector3 Normalise(Vector3 d)
    {
        var len = MathF.Sqrt(d.Z * d.Z + d.Y * d.Y + d.X * d.X);
        if (len == 0f)
            return d;
        var inv = 1f / len;
        return new Vector3(d.X * inv, d.Y * inv, d.Z * inv);
    }
}
