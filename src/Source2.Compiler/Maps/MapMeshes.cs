using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The polygon meshes of a <c>.vmap</c> (<c>CMapMesh</c>, each holding a
/// <c>CDmePolygonMesh</c>), read into world space face by face.
///
/// <para>The mesh is half-edge: <c>faceEdgeIndices</c> gives each face one of
/// its half-edges, <c>edgeNextIndices</c> walks the loop and
/// <c>edgeVertexIndices</c> names the vertex a half-edge points to, which is
/// the face's corner order. A face's material is
/// <c>materials[materialindex]</c>.</para>
///
/// <para>A group that a <c>CMapInstance</c> targets is not compiled where it
/// stands; each instance compiles the group's contents again. A node's world
/// matrix (<c>FUN_180ffdf20</c>) is the instance path's matrix times the
/// node's own <c>AngleMatrix(angles, origin)</c> (its vtable slot 0xa0), and
/// each instance on the path adds the instance's matrix times the inverse of
/// its target's (<c>FUN_180ffddd0</c>), outermost first. That is
/// <see cref="Mesh.Path"/>. The faces themselves are placed as the bake's
/// collapse leaves the copy: its node moved through the instances
/// (<see cref="SettleWorld.Baked"/>), then that node's own matrix. Measured
/// against the .rte: atixref's 240 triangles under instances rotated 90, 180
/// and 270 degrees, one ulp off through the path, are all bit exact this
/// way, as is every instanced triangle of Mako.</para>
///
/// <para>A mesh the visibility manager hides, or one under a hidden node or
/// reached through a hidden instance, is marked <see cref="Mesh.Hidden"/>:
/// the compile leaves it out (Mako's 7,626 hidden skybox, pipe and train
/// triangles are in no .rte).</para>
/// </summary>
public static class MapMeshes
{
    /// <summary>One face: its corners in world space, in loop order, and its material.</summary>
    public sealed record Face(Vector3[] Corners, string Material);

    /// <summary>One mesh and where it hangs: under the world, or under an entity (with its classname).</summary>
    public sealed record Mesh(int NodeId, string ParentType, string? ParentClass, Vector3 Origin, Vector3 Angles, Vector3 Scales, Face[] Faces)
    {
        /// <summary>The <c>CMapMesh</c> element, for its other settings.</summary>
        public DmxBinary.Element? Element { get; init; }

        /// <summary>The world matrix the faces were placed with (matrix3x4, row major).</summary>
        internal float[] World { get; init; } = [];

        /// <summary>The instance path's matrix alone, without the node's own transform.</summary>
        internal float[] Path { get; init; } = [];

        /// <summary>The <c>CMapInstance</c> node ids this copy was reached through, outermost first.</summary>
        public int[] Instances { get; init; } = [];

        /// <summary>The <c>CMapInstance</c> elements this copy was reached through, outermost first.</summary>
        public IReadOnlyList<DmxBinary.Element> Through { get; init; } = [];

        /// <summary>The node's place in the depth-first walk, shared with <see cref="EntityNode"/>.</summary>
        public int Sequence { get; init; }

        /// <summary>
        /// Whether the map's visibility manager hides the mesh, or a node it
        /// hangs under or was reached through (<see cref="MapEntities.HiddenNodes"/>).
        /// </summary>
        public bool Hidden { get; init; }

        /// <summary>The <c>CMapPrefab</c> node ids the mesh was reached through, outermost first
        /// (<see cref="MapPrefabs"/>); its id path is these, then <see cref="NodeId"/>.</summary>
        public int[] Prefabs { get; init; } = [];

        /// <summary>The <c>CMapPrefab</c> elements the mesh was reached through, outermost first.</summary>
        public IReadOnlyList<DmxBinary.Element> PrefabChain { get; init; } = [];

        /// <summary>Whether the mesh is rotated or scaled, which the plain local + origin path does not cover.</summary>
        public bool Transformed => Angles != Vector3.Zero || Scales != Vector3.One;
    }

    /// <summary>An entity node met on the same walk, in walk order with the meshes.</summary>
    public sealed record EntityNode(int Sequence, DmxBinary.Element Element, int[] Instances)
    {
        /// <summary>The instance path's matrix.</summary>
        internal float[] Path { get; init; } = [];

        /// <summary>The CMapInstance elements the copy was reached through, outermost first.</summary>
        public IReadOnlyList<DmxBinary.Element> Through { get; init; } = [];

        /// <summary>The <c>CMapPrefab</c> node ids the node was reached through, outermost first.</summary>
        public int[] Prefabs { get; init; } = [];

        /// <summary>The <c>CMapPrefab</c> elements the node was reached through, outermost first.</summary>
        public IReadOnlyList<DmxBinary.Element> PrefabChain { get; init; } = [];

        /// <summary>Whether the node, or one it hangs under or was reached through, is hidden.</summary>
        public bool Hidden { get; init; }

        /// <summary>
        /// The CMapDeformer node whose lattice the entity takes (181022540),
        /// when the climb from its parent reaches one; see <see cref="DeformerBelow"/>.
        /// </summary>
        public DmxBinary.Element? Deformer { get; init; }
    }

    /// <summary>
    /// The deformer in effect under <paramref name="node"/> (181022540, which
    /// climbs from an entity's parent): a CMapDeformer node itself; else the
    /// one above when the node's type (vtable 0x4a0) lets the climb through,
    /// which is 1 or 3: a group, or an entity whose class has the static_prop
    /// or deformable metadata flag (in CS2's FGDs only prop_static has
    /// either); the world, instances and other entities (type 0) stop it.
    /// </summary>
    internal static DmxBinary.Element? DeformerBelow(DmxBinary.Element node, DmxBinary.Element? deformer)
    {
        if (node.Type.StartsWith("CMapDeformer", StringComparison.Ordinal))
            return node;
        if (node.Type == "CMapGroup")
            return deformer;
        if (node.Type == "CMapEntity" && node.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") == "prop_static")
            return deformer;
        return null;
    }

    public static List<Mesh> Read(DmxBinary.Document doc) => ReadWithEntities(doc).Meshes;

    /// <summary>The meshes and the entity nodes, each carrying its place in the one depth-first walk.</summary>
    public static (List<Mesh> Meshes, List<EntityNode> Entities) ReadWithEntities(DmxBinary.Document doc)
    {
        var (meshes, entities, _) = ReadAll(doc, false);
        return (meshes, entities);
    }

    /// <summary>
    /// The map's <c>CMapStaticOverlay</c> nodes in walk order, placed as
    /// meshes are (their polygon mesh in <c>meshData</c>, <see cref="Mesh.World"/>
    /// its matrix); <see cref="Mesh.Faces"/> are left empty.
    /// </summary>
    public static List<Mesh> ReadOverlays(DmxBinary.Document doc) => ReadAll(doc, true).Overlays!;

    private static (List<Mesh> Meshes, List<EntityNode> Entities, List<Mesh>? Overlays) ReadAll(DmxBinary.Document doc, bool withOverlays)
    {
        List<Mesh>? overlays = withOverlays ? [] : null;
        var targets = new HashSet<DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        foreach (var instance in doc.OfType("CMapInstance"))
        {
            if (instance.Get<DmxBinary.Element>("target") is { } target)
                targets.Add(target);
        }
        var meshes = new List<Mesh>();
        var entities = new List<EntityNode>();
        var sequence = 0;
        var hidden = MapEntities.HiddenNodes(doc);
        foreach (var world in doc.OfType("CMapWorld"))
            Walk(world, world, Identity, [], [], [], [], targets, hidden, false, meshes, entities, overlays, ref sequence, null);
        return (meshes, entities, overlays);
    }

    private static void Walk(DmxBinary.Element node, DmxBinary.Element parent, float[] path, int[] instances, DmxBinary.Element[] through,
                             int[] prefabs, DmxBinary.Element[] prefabChain, HashSet<DmxBinary.Element> targets, HashSet<int> hiddenIds, bool hidden, List<Mesh> meshes,
                             List<EntityNode> entities, List<Mesh>? overlays, ref int sequence, DmxBinary.Element? deformer = null)
    {
        // A collapsed instance's copy is appended to its parent's children
        // (CMapInstance_Collapse), so instances come after their siblings.
        var children = node.GetElements("children").ToList();
        foreach (var child in children.Where(c => c.Type != "CMapInstance").Concat(children.Where(c => c.Type == "CMapInstance")))
        {
            var hides = hidden || hiddenIds.Contains(child.GetValue<int>("nodeID") ?? int.MinValue);
            switch (child.Type)
            {
                case "CMapMesh":
                    var className = parent.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname");
                    // A prefab's map is collapsed into the document as an instance
                    // is (captured), its nodes moved as the collapse moves them.
                    var placed = through.Length == 0 && prefabs.Length == 0 ? Concat(path, Local(child))
                               : prefabs.Length == 0 ? SettleWorld.Baked(child, through)
                               : SettleWorld.NestedPlacement(child, through, prefabChain);
                    meshes.Add(new Mesh(child.GetValue<int>("nodeID") ?? -1, parent.Type, className,
                                        child.GetValue<Vector3>("origin") ?? Vector3.Zero, child.GetValue<Vector3>("angles") ?? Vector3.Zero,
                                        child.GetValue<Vector3>("scales") ?? Vector3.One, Faces(child, placed))
                               { Element = child, Instances = instances, Through = through, World = placed, Path = path, Sequence = sequence++, Hidden = hides, Prefabs = prefabs, PrefabChain = prefabChain });
                    break;
                case "CMapStaticOverlay" when overlays != null:
                {
                    var at = through.Length == 0 ? Concat(path, Local(child)) : SettleWorld.Baked(child, through);
                    overlays.Add(new Mesh(child.GetValue<int>("nodeID") ?? -1, parent.Type, null,
                                          child.GetValue<Vector3>("origin") ?? Vector3.Zero, child.GetValue<Vector3>("angles") ?? Vector3.Zero,
                                          child.GetValue<Vector3>("scales") ?? Vector3.One, [])
                                 { Element = child, Instances = instances, World = at, Path = path, Sequence = sequence, Hidden = hides, Prefabs = prefabs, PrefabChain = prefabChain });
                    break;
                }
                case "CMapPrefab":
                    // The prefab's map, walked where the prefab stands and moved by
                    // its matrix; its own hidden nodes and instance targets apply.
                    if (child.Get<DmxBinary.Element>(MapPrefabs.WorldKey) is not { } prefabWorld)
                        break;
                    Walk(prefabWorld, prefabWorld, Concat(path, Local(child)), instances, through,
                         [.. prefabs, child.GetValue<int>("nodeID") ?? -1], [.. prefabChain, child],
                         new HashSet<DmxBinary.Element>(child.Get<List<DmxBinary.Element>>(MapPrefabs.TargetsKey) ?? [], ReferenceEqualityComparer.Instance),
                         child.Get<HashSet<int>>(MapPrefabs.HiddenKey) ?? [], hides, meshes, entities, overlays, ref sequence);
                    break;
                case "CMapInstance":
                    if (child.Get<DmxBinary.Element>("target") is not { } target)
                        break;
                    var step = Concat(Local(child), Invert(Local(target)));
                    Walk(target, target, Concat(path, step), [.. instances, child.GetValue<int>("nodeID") ?? -1], [.. through, child], prefabs, prefabChain, targets, hiddenIds, hides,
                         meshes, entities, overlays, ref sequence, deformer);
                    break;
                default:
                    if (targets.Contains(child))
                        break;
                    // An entity's own shapes come before its children's.
                    if (child.Type is "CMapEntity" or "CMapSmartProp")
                        entities.Add(new EntityNode(sequence++, child, instances) { Path = path, Through = through, Prefabs = prefabs, PrefabChain = prefabChain, Hidden = hides, Deformer = deformer });
                    Walk(child, child, path, instances, through, prefabs, prefabChain, targets, hiddenIds, hides, meshes, entities, overlays, ref sequence, DeformerBelow(child, deformer));
                    break;
            }
        }
    }

    private static Face[] Faces(DmxBinary.Element mesh, float[] world)
    {
        var data = mesh.Get<DmxBinary.Element>("meshData") ?? throw new InvalidDataException("CMapMesh without meshData.");
        var positions = Stream(data, "vertexData", "position");
        var materials = Ints(data, "faceData", "materialindex");
        var names = (data.Get<object?[]>("materials") ?? []).Select(x => x as string ?? "").ToArray();
        var next = IntArray(data, "edgeNextIndices");
        var to = IntArray(data, "edgeVertexIndices");
        var first = IntArray(data, "faceEdgeIndices");

        var scales = mesh.GetValue<Vector3>("scales") ?? Vector3.One;
        var faces = new Face[first.Length];
        for (var f = 0; f < first.Length; f++)
        {
            var corners = new List<Vector3>();
            var e = first[f];
            do
            {
                corners.Add(Transform(world, (Vector3)positions[to[e]]! * scales));
                e = next[e];
            }
            while (e != first[f] && corners.Count <= next.Length);
            var material = f < materials.Length && materials[f] >= 0 && materials[f] < names.Length ? names[materials[f]] : "";
            faces[f] = new Face([.. corners], material);
        }
        return faces;
    }

    // Matrices are Valve's matrix3x4_t: three rows of (axis x, axis y, axis z,
    // translation), row major.
    private static readonly float[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];

    // A point through a matrix: each row dotted with (x, y, z, 1) as a SIMD
    // horizontal add pairs it, (x + z) + (y + t). Measured on atixref: a roll
    // of -89.999985 degrees and instanced copies of a mesh at a yaw of 180.5
    // tell the orders apart, and only this one rebuilds all of both.
    internal static Vector3 Transform(float[] m, Vector3 v)
        => new(((m[0] * v.X) + (m[2] * v.Z)) + ((m[1] * v.Y) + m[3]),
               ((m[4] * v.X) + (m[6] * v.Z)) + ((m[5] * v.Y) + m[7]),
               ((m[8] * v.X) + (m[10] * v.Z)) + ((m[9] * v.Y) + m[11]));

    // A node's own matrix (vtable slot 0xa0, FUN_181255fb0): AngleMatrix of its
    // angles with its origin as the translation.
    internal static float[] Local(DmxBinary.Element node)
    {
        var m = AngleMatrix(node.GetValue<Vector3>("angles") ?? Vector3.Zero);
        var origin = node.GetValue<Vector3>("origin") ?? Vector3.Zero;
        m[3] = origin.X;
        m[7] = origin.Y;
        m[11] = origin.Z;
        return m;
    }

    // FUN_181255d60: pitch, yaw, roll in degrees, each times 0.017453292f
    // through tier0's V_sincosf (the CRT sinf and cosf), grouped as the
    // binary groups them. The columns are the forward, left and up axes.
    internal static float[] AngleMatrix(Vector3 angles)
    {
        const float Radians = 0.017453292f;
        float sp = MathF.Sin(angles.X * Radians), cp = MathF.Cos(angles.X * Radians);
        float sy = MathF.Sin(angles.Y * Radians), cy = MathF.Cos(angles.Y * Radians);
        float sr = MathF.Sin(angles.Z * Radians), cr = MathF.Cos(angles.Z * Radians);
        return [cy * cp, (sr * sp * cy) - (cr * sy), (cr * sp * cy) - (-sy * sr), 0,
                sy * cp, (sr * sp * sy) + (cr * cy), (cr * sp * sy) - (sr * cy), 0,
                -sp, sr * cp, cr * cp, 0];
    }

    // FUN_181258890 (ConcatTransforms, SIMD): row i of a times b, the terms
    // summed z, y, x and then a's translation (or +0).
    internal static float[] Concat(float[] a, float[] b)
    {
        var o = new float[12];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 4; j++)
                o[(i * 4) + j] = (((a[(i * 4) + 2] * b[8 + j]) + (a[(i * 4) + 1] * b[4 + j])) + (a[i * 4] * b[j])) + (j == 3 ? a[(i * 4) + 3] : 0f);
        }
        return o;
    }

    // FUN_18125aba0 (MatrixInvert): the transpose, and the translation as
    // -(t.z * r2 + t.y * r1 + t.x * r0) against the transposed rows. The
    // rescale it does when the first column is not unit length never applies
    // to a rotation.
    internal static float[] Invert(float[] m)
    {
        float[] o = [m[0], m[4], m[8], 0, m[1], m[5], m[9], 0, m[2], m[6], m[10], 0];
        float tx = m[3], ty = m[7], tz = m[11];
        for (var i = 0; i < 3; i++)
            o[(i * 4) + 3] = -(((tz * o[(i * 4) + 2]) + (ty * o[(i * 4) + 1])) + (tx * o[i * 4]));
        return o;
    }

    private static object?[] Stream(DmxBinary.Element data, string array, string name)
    {
        var holder = data.Get<DmxBinary.Element>(array) ?? throw new InvalidDataException($"meshData without {array}.");
        var stream = holder.GetElements("streams").FirstOrDefault(s => s.Name.StartsWith(name + ":", StringComparison.Ordinal))
                     ?? throw new InvalidDataException($"{array} without a {name} stream.");
        return stream.Get<object?[]>("data") ?? [];
    }

    private static int[] Ints(DmxBinary.Element data, string array, string name)
    {
        var holder = data.Get<DmxBinary.Element>(array);
        var stream = holder?.GetElements("streams").FirstOrDefault(s => s.Name.StartsWith(name + ":", StringComparison.Ordinal));
        return (stream?.Get<object?[]>("data") ?? []).Select(x => x is int i ? i : -1).ToArray();
    }

    private static int[] IntArray(DmxBinary.Element e, string name)
        => (e.Get<object?[]>(name) ?? []).Select(x => x is int i ? i : -1).ToArray();
}
