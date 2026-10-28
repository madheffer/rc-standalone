using System.Numerics;
using Source2.Compiler.Maps;
using Source2.Compiler.Physics;

namespace Source2.Compiler.SteamAudio;

/// <summary>
/// The acoustic geometry resourcecompiler 0923 collects from the .vmap and steamaudio 1023 hands phonon as its one
/// static mesh:
/// <list type="bullet">
/// <item>the collector (180f54950 into the node walk 180f53f40 and the per-node rules 180f53780) takes a map mesh
/// that collides (a CMapMesh or CMapPath whose CollisionType, vf 0x680, is not 0: 180f4bb00) through 180f519d0: the
/// node's physics shapes from PhysMesh_Shapes (181057020, the same shapes the settle builds,
/// <see cref="SettleWorld.MeshShapes"/>), each as triangles again (a hull through RnHull_Triangulate 1819589f0 at
/// scale 1, a mesh's vertices times (1, 1, 1) and its triangles, 1819cea20), grouped by (surface property index,
/// InteractsAs, collision group) in first-seen order (the 0x48-byte groups), under the transform the node's world
/// matrix gives (its translation, the longest column as scale, the unscaled rotation's quaternion);</item>
/// <item>the groups are appended (180f510d0) in order, a group of collision group 1 left out: each vertex through
/// the transform's matrix (1801feb70, then Matrix3x4_TransformPointXmm 1812651a0), indices offset by the vertices
/// before, one material per triangle (the surface index, 1 when the surface was not found);</item>
/// <item>steamaudio's scene build (180084d90) takes the lists as they are: each vertex to metres and Steam Audio's
/// axes (180063600), each triangle's corners reversed.</item>
/// </list>
/// </summary>
public sealed class AcousticScene
{
    /// <summary>The collected mesh in Source inches: vertices, triangles and one material index per triangle.</summary>
    public List<Vector3> Vertices { get; } = [];

    public List<(int A, int B, int C)> Triangles { get; } = [];

    public List<int> Materials { get; } = [];

    /// <summary>One 0x48-byte group of 180f519d0: its key, then its points and indices.</summary>
    private sealed class Group(int surface, ulong interactsAs, byte collisionGroup)
    {
        public int Surface { get; } = surface;
        public ulong InteractsAs { get; } = interactsAs;
        public byte CollisionGroup { get; } = collisionGroup;
        public List<Vector3> Points { get; } = [];
        public List<int> Indices { get; } = [];
    }

    /// <summary>
    /// The InteractsAs mask of the reverb bake's collection (180101690 sets 180f54950's first flag: 0x1400,
    /// blocksound and window). A group stays when its collision group is 0 or its InteractsAs meets the mask.
    /// </summary>
    public const ulong BakeMask = 0x1400;

    /// <summary>The mask of the probe generation's collection (180100e40 leaves the flag clear: 0x3400, passbullets too).</summary>
    public const ulong GenerationMask = 0x3400;

    private ulong staticMask;

    /// <summary>
    /// The scene of a map as a collection with <paramref name="mask"/> gathers it (<see cref="BakeMask"/> or
    /// <see cref="GenerationMask"/>, into 180f54950 on the map's world): the nodes depth first in stored order (180f53f40), each through 180f53780. <paramref name="surfaceNames"/> is the
    /// surface property system's list (index = material index). What the collector does that is not ported is
    /// refused: instances and prefabs (the instance paths of 180f88460), hidden nodes, a mesh's own
    /// steamaudioGeometryMode (CMapMeshBase +0x3b70), entity models and smart props (180f52220), paths and cables,
    /// and faces whose material sets tools.steamaudiogeometry (180f54020).
    /// </summary>
    public static AcousticScene Build(DmxBinary.Document document, SettleWorld.IModels models, FgdSchema schema, IReadOnlyList<string> surfaceNames,
                                      ulong mask = BakeMask)
    {
        var index = new Dictionary<uint, int>();
        for (var i = 0; i < surfaceNames.Count; i++)
            if (!index.TryAdd(SettleWorld.NameHash(surfaceNames[i]), i))
                throw new NotSupportedException($"two surface properties share the name hash of {surfaceNames[i]}");
        foreach (var type in (string[])["CMapInstance", "CMapPrefab", "CMapSmartProp", "CMapPath", "CMapCable"])
            if (document.OfType(type).Any())
                throw new NotSupportedException($"acoustic geometry with a {type} is not ported");
        if (MapEntities.HiddenNodes(document).Count > 0)
            throw new NotSupportedException("acoustic geometry with hidden nodes is not ported");
        var shapes = SettleWorld.MeshShapes(document, models, schema);
        var scene = new AcousticScene { staticMask = mask };
        foreach (var world in document.OfType("CMapWorld"))
            scene.Walk(world, null, shapes, models, schema, index);
        return scene;
    }

    // 180f53f40: the node, then its children in order. owner is the nearest entity above (MapNode_OwnerEntity, 180f8b770).
    private void Walk(DmxBinary.Element node, DmxBinary.Element? owner, Func<DmxBinary.Element, DmxBinary.Element?, SettleWorld.BodyBuild?> shapes,
                      SettleWorld.IModels models, FgdSchema schema, Dictionary<uint, int> surfaces)
    {
        foreach (var child in node.GetElements("children"))
        {
            if (child.Type == "CMapEntity" && child.Get<DmxBinary.Element>("entity_properties")?.Get<string>("model") is { Length: > 0 } model)
                throw new NotSupportedException($"acoustic geometry from an entity model ({model}, 180f52220) is not ported");
            if (child.Type == "CMapMesh")
                Node(child, owner, shapes, models, surfaces);
            Walk(child, child.Type == "CMapEntity" ? child : owner, shapes, models, schema, surfaces);
        }
    }

    /// <summary>180f53780 for a CMapMesh, with no filter flag and no dynamic list (both collections).</summary>
    private void Node(DmxBinary.Element mesh, DmxBinary.Element? owner, Func<DmxBinary.Element, DmxBinary.Element?, SettleWorld.BodyBuild?> shapes,
                      SettleWorld.IModels models, Dictionary<uint, int> surfaces)
    {
        string? Key(string name) => owner?.Get<DmxBinary.Element>("entity_properties")?.Get<string>(name);
        // An owner entity whose solid key reads 0 (V_atoi, default -1) contributes nothing.
        if (owner != null && Key("solid") is { } solid && (int)CNumbers.Atoi(solid) == 0)
            return;
        // CMapMeshBase's embedded property steamaudioGeometryMode (+0x3b70, registered under
        // hammer_embedded_properties in 1810e3f50): where the .vmap stores it is not read, so a mesh carrying
        // either name is refused. Without it the mode is 0 and the owner's steamaudio_geometry_mode decides.
        foreach (var attribute in mesh.Attributes.Keys)
            if (attribute.Contains("steamaudio", StringComparison.OrdinalIgnoreCase) || attribute.Contains("embedded", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"a mesh's own {attribute} is not ported");
        var mode = owner != null && Key("steamaudio_geometry_mode") is { } m ? (int)CNumbers.Atoi(m) : 0;
        if (mode == 1)
            return;
        if (mode == 3)
            return; // dynamic geometry: the reverb collection keeps no dynamic list.
        if (mode != 2 && owner != null && (int)CNumbers.Atoi(Key("solidity") ?? "1") != 2)
            return;
        var mask = mode == 2 ? 0ul : staticMask;
        if (FaceGeometry(mesh, models))
            throw new NotSupportedException("faces with tools.steamaudiogeometry (180f54020) are not ported");
        if (shapes(mesh, owner) is { } body)
            AddPiece(body, surfaces, mask);
    }

    /// <summary>Whether any of the mesh's materials sets tools.steamaudiogeometry (IMaterial2 vf 0x48), which sends its faces through 180f54020.</summary>
    private static bool FaceGeometry(DmxBinary.Element mesh, SettleWorld.IModels models)
    {
        var data = mesh.Get<DmxBinary.Element>("meshData");
        foreach (var name in (data?.Get<object?[]>("materials") ?? []).OfType<string>())
            if (models.Material(name)?.Store?.Invoke().Bool(Maps.MeshEntryFlags.Key("tools.steamaudiogeometry")) == true)
                return true;
        return false;
    }

    private void AddPiece(SettleWorld.BodyBuild body, Dictionary<uint, int> surfaces, ulong mask)
    {
        var groups = new List<Group>();
        foreach (var shape in body.Shapes)
        {
            var (points, indices) = shape.Type switch
            {
                Simulation.BroadphaseShape.HullType => SettleWorld.HullMesh(shape.Hull!),
                Simulation.BroadphaseShape.MeshType => MeshTriangles(shape.Mesh!),
                _ => throw new NotSupportedException($"acoustic geometry from a shape of type {shape.Type}"),
            };
            if (points.Length == 0 || indices.Length < 3)
                continue;
            var a = shape.Attributes;
            // 180f519d0: a default-group shape that is no layer yet asks the physics interface (vfn 0xd8, then
            // 0x58) for its layers; not read.
            if (a.Group == 4 && a.InteractsAs == 0 && (a.Flags & 1) != 0)
                throw new NotSupportedException("a default-group shape with no InteractsAs (the interface's layers are not read)");
            var surface = shape.Name.Length > 0 && surfaces.TryGetValue(SettleWorld.NameHash(shape.Name), out var s) ? s : -1;
            var group = groups.FirstOrDefault(g => g.Surface == surface && g.InteractsAs == a.InteractsAs && g.CollisionGroup == a.Group);
            if (group == null)
                groups.Add(group = new Group(surface, a.InteractsAs, a.Group));
            var first = group.Points.Count;
            group.Points.AddRange(points);
            group.Indices.AddRange(indices.Select(i => i + first));
        }
        var matrix = Matrix(body.Position, body.Scale, body.Orientation);
        foreach (var g in groups)
        {
            if (g.Points.Count == 0 || g.Indices.Count <= 2 || g.CollisionGroup == 1)
                continue;
            if (mask != 0 && g.CollisionGroup != 0 && (g.InteractsAs & mask) == 0)
                continue;
            var first = Vertices.Count;
            Vertices.AddRange(g.Points.Select(p => LightMath.Transform34(matrix, p)));
            for (var t = 0; t + 2 < g.Indices.Count; t += 3)
                Triangles.Add((g.Indices[t] + first, g.Indices[t + 1] + first, g.Indices[t + 2] + first));
            var material = g.Surface < 0 ? 1 : g.Surface;
            for (var t = 0; t < g.Indices.Count / 3; t++)
                Materials.Add(material);
        }
    }

    /// <summary>1819cea20 at scale (1, 1, 1): the mesh's vertices, each coordinate times 1, and its triangles.</summary>
    private static (Vector3[] Points, int[] Indices) MeshTriangles(RnMesh mesh)
        => ([.. mesh.Vertices.Select(v => new Vector3(v.X * 1f, v.Y * 1f, v.Z * 1f))], [.. mesh.Triangles.SelectMany(t => new[] { t.A, t.B, t.C })]);

    /// <summary>1801feb70: QuaternionMatrix (181260150) with the position, the rotation columns times the scale when it is not 1.</summary>
    private static float[] Matrix(Vector3 position, float scale, Quaternion orientation)
    {
        var m = new CTransform(position, 1f, orientation).Matrix();
        if (scale != 1f)
            foreach (var i in (int[])[0, 1, 2, 4, 5, 6, 8, 9, 10])
                m[i] = scale * m[i];
        return m;
    }

    /// <summary>
    /// 180100e40's automatic probe box: the vertices' bounds in Source inches (each minimum and maximum replaced on
    /// &lt;= and &gt;=, from +-FLT_MAX), the centre (max + min) 0.5 and the size max - min; all zero with no vertex.
    /// </summary>
    public (Vector3 Centre, Vector3 Size) Box()
    {
        if (Vertices.Count == 0)
            return (Vector3.Zero, Vector3.Zero);
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = -float.MaxValue, maxY = -float.MaxValue, maxZ = -float.MaxValue;
        foreach (var v in Vertices)
        {
            if (v.X <= minX) minX = v.X;
            if (v.Y <= minY) minY = v.Y;
            if (v.Z <= minZ) minZ = v.Z;
            if (maxX <= v.X) maxX = v.X;
            if (maxY <= v.Y) maxY = v.Y;
            if (maxZ <= v.Z) maxZ = v.Z;
        }
        return (new Vector3((minX + maxX) * 0.5f, (maxY + minY) * 0.5f, (maxZ + minZ) * 0.5f), new Vector3(maxX - minX, maxY - minY, maxZ - minZ));
    }

    /// <summary>180084d90: the vertices in metres on Steam Audio's axes (180063600), each triangle reversed.</summary>
    public (Vector3[] Vertices, (int, int, int)[] Triangles, int[] Materials) ToSteamAudio()
        => ([.. Vertices.Select(v => { var (x, y, z) = ProbeGeneration.SourceToMeters(v.X, v.Y, v.Z); return new Vector3(x, y, z); })],
            [.. Triangles.Select(t => (t.C, t.B, t.A))], [.. Materials]);
}
