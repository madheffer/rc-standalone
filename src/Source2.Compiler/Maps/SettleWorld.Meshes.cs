using System.Numerics;
using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.Maps;

public static partial class SettleWorld
{
    /// <summary>
    /// One triangle soup of a map mesh (FUN_181059770's 0x90-byte entries):
    /// the material record its faces share, and its points in the order the
    /// triangles first reach them.
    /// </summary>
    private sealed class Soup
    {
        public required Record Record { get; init; }
        public List<Vector3> Points { get; } = [];
        public List<int> Indices { get; } = [];
    }

    /// <summary>
    /// A mesh material's physics (FUN_1810564a0's 0x60-byte record): the
    /// surface property's name, the collision attributes, the material the
    /// surface gives, and whether the faces are dropped (group 1, "never",
    /// which the record starts as and a non-solid material keeps).
    /// </summary>
    private sealed record Record(string Name, CollisionAttributes Attributes, ContactSolver.Material Material)
    {
        public bool Dropped => Attributes.Group == 1;
    }

    /// <summary>
    /// A map mesh's body and shapes (FUN_18105d760): a static body at the
    /// node's world matrix (position, longest column as scale, the unscaled
    /// rotation's quaternion with no bind pose), then one shape per soup
    /// (FUN_181057020) by physics type: 2 hulls the soup, 3 hulls each group
    /// of triangles joined by a vertex, 4 makes a triangle mesh. Type 0
    /// ("none") makes no body at all.
    /// </summary>
    private static void FromMesh(DmxBinary.Element mesh, float[] world, int nodeId, DmxBinary.Element? owner, Context context, object?[]? positions = null)
    {
        var type = MeshPhysicsType(mesh, owner, context.Schema);
        if (type == 0)
            return;
        var longest = Enumerable.Range(0, 3)
            .Select(c => MathF.Sqrt(((world[c] * world[c]) + (world[4 + c] * world[4 + c])) + (world[8 + c] * world[8 + c])))
            .Aggregate(0f, (a, b) => a <= b ? b : a);
        var body = new BodyBuild(nodeId, new Vector3(world[3], world[7], world[11]), longest, Orientation(Unscaled(world)), []) { Node = mesh };
        var simplify = Physics.WorldCollision.SimplificationError(mesh, context.Versions.GetValueOrDefault(mesh, context.Version));
        foreach (var soup in Soups(mesh, owner, context, positions))
        {
            if (soup.Record.Dropped || soup.Points.Count == 0 || soup.Indices.Count <= 2)
                continue;
            // With a simplification error (the mesh's vf216, read at vf 0x6c0)
            // each soup goes through 18105bb20: welded at 1/32 and simplified
            // (CMesh_Simplify, rc 1813cb3d0), as physicsbuilder does a world piece.
            if (simplify > 0f)
            {
                var (points, indices) = Physics.WorldCollision.Simplify(simplify, [.. soup.Points], [.. soup.Indices]);
                soup.Points.Clear();
                soup.Points.AddRange(points);
                soup.Indices.Clear();
                soup.Indices.AddRange(indices);
            }
            var name = soup.Record.Name;
            switch (type)
            {
                case 2:
                    if (RnHullBuilder.Create([.. soup.Points], MeshHullOptions, out _) is { } hull)
                        body.Shapes.Add(new ShapeBuild(BroadphaseShape.HullType, hull, 1f, null, Vector3.One, soup.Record.Attributes, soup.Record.Material, name));
                    break;
                case 3:
                    foreach (var part in Components(soup.Points, soup.Indices))
                        if (RnHullBuilder.Create(part, MeshHullOptions, out _) is { } piece)
                            body.Shapes.Add(new ShapeBuild(BroadphaseShape.HullType, piece, 1f, null, Vector3.One, soup.Record.Attributes, soup.Record.Material, name));
                    break;
                case 4:
                    if (RnMeshBuilder.Create([.. soup.Indices], [.. soup.Points], null) is { } triangles)
                        body.Shapes.Add(new ShapeBuild(BroadphaseShape.MeshType, null, 0, triangles, Vector3.One, soup.Record.Attributes, soup.Record.Material, name));
                    break;
            }
        }
        context.Bodies.Add(body);
    }

    /// <summary>
    /// A cable's body (PhysMesh_Shapes, rc 181057020, its CMapPath branch):
    /// with collisionEnabled (vf 0x8a0; CollisionType, vf 0x680, is then 4)
    /// one soup, the collision tube vf277 gives (<see cref="Physics.CableCollision.Build"/>)
    /// in the cable's space and in vf277's order, under the record
    /// PhysMesh_MaterialRecord makes of the cable's material with its
    /// surface_properties key, the cable its own owner. No further
    /// simplification (the cable's vf 0x6c0 is 1801ae2c0, 0). The body stands
    /// at the cable's node matrix, as a mesh's does, and takes the soup as a
    /// triangle mesh.
    /// </summary>
    /// <param name="through">The instances a placed copy was reached through:
    /// it stands at its first node moved as the collapse moves it, and has the
    /// settings CMapCable::CopyFrom gives it (<see cref="Physics.CableCollision.Copied"/>).</param>
    /// <param name="prefabs">The prefabs a placed copy hangs under.</param>
    private static void FromCable(DmxBinary.Element node, Context context, int? id = null, IReadOnlyList<DmxBinary.Element>? through = null,
                                  IReadOnlyList<DmxBinary.Element>? prefabs = null, bool underDeformer = false)
    {
        if (!Physics.CableCollision.Collides(node) || MapEntities.ReadNode(node) is not { } cable)
            return;
        through ??= [];
        cable = PlacedPath(cable, through, prefabs ?? [], underDeformer, node);
        var world = NodeWorld(Physics.CableCollision.NodeMatrix(cable));
        var longest = Enumerable.Range(0, 3)
            .Select(c => MathF.Sqrt(((world[c] * world[c]) + (world[4 + c] * world[4 + c])) + (world[8 + c] * world[8 + c])))
            .Aggregate(0f, (a, b) => a <= b ? b : a);
        var body = new BodyBuild(id ?? cable.NodeId, new Vector3(world[3], world[7], world[11]), longest, Orientation(Unscaled(world)), []) { Node = node };
        var (controls, scales) = EntityLumpAuthor.PathControls(cable, context.Schema);
        var tube = Physics.CableCollision.Build(controls, scales, through.Count > 0 ? Physics.CableCollision.Copied(Physics.CableCollision.SettingsOf(node)) : Physics.CableCollision.SettingsOf(node));
        // PhysMesh_Build hands PhysMesh_Shapes the deformer 181022480 finds,
        // which needs the node itself of kind 3: an entity class with the
        // static_prop or deformable flag (180dd54d0). No cable class has one,
        // so Valve's settle world keeps every cable straight (s2c_deformplace's
        // eight cables under deformers, captured).
        var (points, indices) = Deformable(cable.ClassName, context.Schema)
            ? Physics.CableCollision.Deformed(tube, node, Physics.CableCollision.NodeMatrix(cable), through, prefabs ?? [])
            : tube;
        var surface = cable.Keys.FirstOrDefault(k => Io.Tier0Strings.EqualsIgnoreCase(k.Key, "surface_properties")).Value;
        var record = MaterialRecord(node.Get<string>("materialName") ?? "", node, node, context, surface);
        if (!record.Dropped && points.Length > 0 && indices.Length > 2
            && RnMeshBuilder.Create(indices, points, null) is { } triangles)
            body.Shapes.Add(new ShapeBuild(BroadphaseShape.MeshType, null, 0, triangles, Vector3.One, record.Attributes, record.Material, record.Name));
        context.Bodies.Add(body);
    }

    /// <summary>
    /// RnHullCreate's options for a map mesh hull: none at all for type 2,
    /// which RnHullCreate fills with a 5 degree merge angle, and the same
    /// block spelled out for type 3 (FUN_1819c9db0).
    /// </summary>
    private static readonly RnHullBuilder.Options MeshHullOptions = new() { Angle = 5f };

    /// <summary>
    /// MapBuilder_ResolvePhysicsType (FUN_181083540): "none" is 0; "default"
    /// is a mesh (4) outside an entity, and inside one a multi convex (3)
    /// unless the class inherits PhysicsTypeOverride_Mesh (4) or
    /// PhysicsTypeOverride_SingleConvex (2).
    /// </summary>
    private static int MeshPhysicsType(DmxBinary.Element mesh, DmxBinary.Element? owner, FgdSchema? schema)
    {
        var stored = mesh.Get<string>("physicsType") ?? "default";
        switch (stored)
        {
            case "none":
                return 0;
            case "default":
                break;
            case "mesh":
                return 4;
            case "convex_single":
                return 2;
            case "convex_multi":
                return 3;
            default:
                throw new NotSupportedException($"physicsType {stored}");
        }
        if (owner == null)
            return 4;
        var className = owner.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") ?? "";
        if (schema == null)
            throw new InvalidOperationException("an entity's mesh needs the FGD to resolve its physics type");
        if (schema.Inherits(className, "PhysicsTypeOverride_Mesh"))
            return 4;
        if (schema.Inherits(className, "PhysicsTypeOverride_MultiConvex"))
            return 3;
        return schema.Inherits(className, "PhysicsTypeOverride_SingleConvex") ? 2 : 3;
    }

    /// <summary>
    /// FUN_181059770: the mesh's faces triangulated in order (each face cut
    /// by <see cref="PolygonTriangulator"/>), its vertex positions times the
    /// node's scales, one record per material (FUN_1810562e0) merged where
    /// equal (FUN_1810578b0), and each triangle's corners appended to its
    /// material's soup, a vertex taking a new point the first time that soup
    /// meets it.
    /// </summary>
    private static List<Soup> Soups(DmxBinary.Element mesh, DmxBinary.Element? owner, Context context, object?[]? placedPositions = null)
    {
        var data = mesh.Get<DmxBinary.Element>("meshData") ?? throw new InvalidDataException("CMapMesh without meshData.");
        var next = MeshInts(data, "edgeNextIndices");
        var to = MeshInts(data, "edgeVertexIndices");
        var first = MeshInts(data, "faceEdgeIndices");
        var vertexData = MeshInts(data, "vertexDataIndices");
        var faceData = MeshInts(data, "faceDataIndices");
        var positions = placedPositions ?? MeshStream(data, "vertexData", "position");
        var faceMaterials = MeshStream(data, "faceData", "materialindex").Select(x => x is int i ? i : 0).ToArray();
        var names = (data.Get<object?[]>("materials") ?? []).Select(x => x as string ?? "").ToArray();
        // An entity class with auto_apply_material metadata (the triggers'
        // toolstrigger) puts that material in every slot of its meshes when
        // the class is set (FUN_180f3af80 through SetWholeMeshOverrideMaterial;
        // FUN_1810dcca0 then names each slot after the override). atixref's
        // two trigger meshes and Mako's 82 all ask FUN_1802e3120 about a
        // nodraw material, whatever their own faces carry.
        var ownerClass = owner?.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") ?? "";
        if (owner != null && context.Schema?.MetadataOf(ownerClass, "auto_apply_material") is { Length: > 0 } applied)
            names = [.. names.Select(_ => applied)];
        var scales = mesh.GetValue<Vector3>("scales") ?? Vector3.One;

        var records = names.Select(n => MaterialRecord(n, mesh, owner, context)).ToList();
        var soups = new List<Soup>();
        var soupOf = new int[records.Count];
        for (var m = 0; m < records.Count; m++)
        {
            var at = soups.FindIndex(s => s.Record == records[m]);
            if (at < 0)
            {
                at = soups.Count;
                soups.Add(new Soup { Record = records[m] });
            }
            soupOf[m] = at;
        }

        // The faces as the mesh library cuts them (subdivided faces into
        // patches), with equal corner positions welded into one vertex.
        var cut = MeshTessellation.Triangulate(data, placedPositions);
        var pointOf = soups.Select(_ => Enumerable.Repeat(-1, cut.Positions.Count).ToArray()).ToArray();
        for (var t = 0; t < cut.Faces.Count; t++)
        {
            var f = cut.Faces[t];
            var material = faceMaterials.Length == 0 ? 0 : faceMaterials[faceData[f]];
            if (material < 0 || material >= soupOf.Length)
                continue;
            var s = soupOf[material];
            var soup = soups[s];
            for (var k = 0; k < 3; k++)
            {
                var v = cut.Indices[(t * 3) + k];
                if (pointOf[s][v] < 0)
                {
                    pointOf[s][v] = soup.Points.Count;
                    var p = cut.Positions[v];
                    soup.Points.Add(scales == Vector3.One ? p : p * scales);
                }
                soup.Indices.Add(pointOf[s][v]);
            }
        }
        return soups;
    }

    /// <summary>
    /// FUN_1810564a0 for one of the mesh's materials. The record starts
    /// dropped (group 1). A material the compile cannot find loads without
    /// attributes. The surface is the material's PhysicsSurfaceProperties
    /// ("default" when unknown), and a solid material (see <see cref="ReadMaterialPhysics"/>)
    /// gets its layer lists, its group (Default, with CONTENTS_SOLID when it
    /// names no layer, if the group is unknown), the mesh node's overrides,
    /// then the owner's rules (FUN_18105ce70) or, in the world, StaticLevel.
    /// <paramref name="surfaceKey"/> is the record's surface when the game knows it
    /// (a path's surface_properties), the material's otherwise.
    /// </summary>
    private static Record MaterialRecord(string path, DmxBinary.Element mesh, DmxBinary.Element? owner, Context context, string? surfaceKey = null)
    {
        var dropped = new CollisionAttributes { EntityId = -1, OwnerId = -1, Group = 1, Flags = 7 };
        // A material the compile cannot find still loads (as the error
        // material), with no attributes: atixref's milwall001 faces join the
        // soup of the other plain material beside them.
        var info = context.Models.Material(path) ?? new MaterialInfo(new Dictionary<string, long>(), new Dictionary<string, string>());
        // The record is named after the surface the name finds (an unknown one
        // finds "default"), so surfaces differing only in case, or two
        // unknown ones, merge (FUN_1810578b0 compares the names).
        var stated = info.Strings.GetValueOrDefault("PhysicsSurfaceProperties", "");
        if (surfaceKey is { Length: > 0 } && context.Models.Surface(NameHash(surfaceKey)) != null)
            stated = surfaceKey;
        var hash = stated.Length > 0 && context.Models.Surface(NameHash(stated)) != null ? NameHash(stated) : DefaultSurface;
        var surface = context.Models.SurfaceName(hash) ?? "default";
        var material = SurfaceMaterial(stated, context.Models);
        var physics = ReadMaterialPhysics(info, context.Models);
        if (!physics.Solid)
            return new Record(surface, dropped, material);

        var rules = context.Rules;
        var a = new CollisionAttributes
        {
            InteractsAs = rules.Mask(physics.InteractAs),
            InteractsWith = rules.Mask(physics.InteractWith),
            InteractsExclude = rules.Mask(physics.InteractExclude),
            EntityId = -1,
            OwnerId = -1,
            Flags = 7,
        };
        var group = physics.Group.Length == 0 ? -1 : rules.Group(physics.Group);
        if (group < 0)
        {
            group = 4;
            if (physics.InteractAs.Length == 0)
                a.InteractsAs |= CollisionRules.ContentsSolid;
        }
        a.Group = (byte)group;
        // The mesh node's own settings (vfuncs 0x690 to 0x6a8).
        if (mesh.Get<string>("physicsGroup") is { Length: > 0 } g && rules.Group(g) is >= 0 and var gi)
            a.Group = (byte)gi;
        a.InteractsAs |= rules.Mask(mesh.Get<string>("physicsInteractsAs") ?? "");
        a.InteractsWith |= rules.Mask(mesh.Get<string>("physicsInteractsWith") ?? "");
        a.InteractsExclude |= rules.Mask(mesh.Get<string>("physicsInteractsExclude") ?? "");
        // The owner's FGD class (entity +0x550) is null outside an entity and
        // for a class the FGD does not declare (Mako's func_monitor); either
        // way the mesh gets the world's rules.
        var className = owner?.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") ?? "";
        if (owner == null || context.Schema?.HasClass(className) == false)
            a.InteractsAs |= 0x40000000;
        else
            OwnerRules(owner, context.Schema, ref a);
        return new Record(surface, a, material);
    }

    /// <summary>
    /// FUN_18105ce70 for an entity's mesh: a class that is not a solid class
    /// (or not a static prop), or one that inherits BasePhysicsSimulated,
    /// also interacts with solid, window, passbullets, player and npc
    /// (0xc3001); the class's collision_group metadata sets the group; and
    /// any class but a static prop does not block sound.
    /// </summary>
    private static void OwnerRules(DmxBinary.Element owner, FgdSchema? schema, ref CollisionAttributes a)
    {
        if (schema == null)
            throw new InvalidOperationException("an entity's mesh needs the FGD for its collision rules");
        var className = owner.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") ?? "";
        var staticProp = schema.HasFlag(className, "static_prop");
        if ((!schema.IsSolidClass(className) && !staticProp) || schema.Inherits(className, "BasePhysicsSimulated"))
            a.InteractsWith |= 0xc3001;
        if (schema.MetadataOf(className, "collision_group") is { Length: > 0 } g && new CollisionRules().Group(g) is >= 0 and var gi)
            a.Group = (byte)gi;
        if (!staticProp)
            a.InteractsAs &= ~0x400ul;
    }

    private static ContactSolver.Material SurfaceMaterial(string name, IModels models)
        => (name.Length > 0 ? models.Surface(NameHash(name)) : null) ?? models.Surface(DefaultSurface) ?? DefaultMaterial;

    /// <summary>What FUN_1802e3120 reads off a material for physics.</summary>
    private sealed record MaterialPhysics(bool Solid, string Group, string InteractAs, string InteractWith, string InteractExclude);

    /// <summary>
    /// FUN_1802e3120, resourcecompiler's copy of physicsbuilder's material
    /// reader (<see cref="Physics.MaterialCollision.Read"/>).
    /// </summary>
    private static MaterialPhysics ReadMaterialPhysics(MaterialInfo info, IModels models)
    {
        var r = Physics.MaterialCollision.Read(info, models.CollisionProperty);
        return new MaterialPhysics(r.Solid, r.Group, r.InteractAs, r.InteractWith, r.InteractExclude);
    }

    /// <summary>
    /// FUN_1812d87e0 and FUN_1819c9db0: the soup's vertices joined by a
    /// union-find over each triangle's edges (union by rank, the second
    /// root joining the first on a tie), numbered by first vertex, and each
    /// group's points in vertex order.
    /// </summary>
    private static List<Vector3[]> Components(List<Vector3> points, List<int> indices)
    {
        var n = points.Count;
        var rank = new int[n];
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int v)
        {
            var root = v;
            while (parent[root] != root)
                root = parent[root];
            while (parent[v] != root)
                (v, parent[v]) = (parent[v], root);
            return root;
        }
        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (rank[rb] < rank[ra])
                parent[rb] = ra;
            else if (rank[ra] < rank[rb])
                parent[ra] = rb;
            else if (ra != rb)
            {
                parent[rb] = ra;
                rank[ra]++;
            }
        }
        for (var t = 0; t + 2 < indices.Count; t += 3)
        {
            Union(indices[t], indices[t + 1]);
            Union(indices[t + 1], indices[t + 2]);
            Union(indices[t + 2], indices[t]);
        }
        var label = Enumerable.Repeat(-1, n).ToArray();
        var count = 0;
        var of = new int[n];
        for (var v = 0; v < n; v++)
        {
            var root = Find(v);
            if (label[root] < 0)
                label[root] = count++;
            of[v] = label[root];
        }
        var groups = Enumerable.Range(0, count).Select(_ => new List<Vector3>()).ToList();
        for (var v = 0; v < n; v++)
            groups[of[v]].Add(points[v]);
        return [.. groups.Select(g => g.ToArray())];
    }

    /// <summary>
    /// The string token hash: MurmurHash2 with seed 0x31415926 after ASCII
    /// lowering, as resourcecompiler's 1800c7c10 inlines it (see
    /// <see cref="Io.ResourceNames.Hash"/>).
    /// </summary>
    internal static uint NameHash(string name)
    {
        var bytes = Io.ResourceNames.Terminated(name);
        return Io.ResourceNames.Hash(bytes, bytes.Length - 1, 0x31415926);
    }

    private static int[] MeshInts(DmxBinary.Element data, string name)
        => (data.Get<object?[]>(name) ?? []).Select(x => x is int i ? i : 0).ToArray();

    private static object?[] MeshStream(DmxBinary.Element data, string array, string name)
    {
        var streams = data.Get<DmxBinary.Element>(array)?.GetElements("streams") ?? [];
        var stream = streams.FirstOrDefault(s => s.Name.Split(':')[0] == name || s.Name.StartsWith(name + "$", StringComparison.Ordinal));
        return stream?.Get<object?[]>("data") ?? [];
    }
}
