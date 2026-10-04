using System.Numerics;
using System.Runtime.InteropServices;
using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Physics;

/// <summary>
/// The world's physics part as the model compile assembles it from the
/// pieces physicsbuilder hands over (<see cref="WorldCollision.Pieces"/>):
/// the shapes in part order (<see cref="WorldCollision.PartOrder"/>), hulls
/// written before the mesh gatherer runs, each shape's collision attribute
/// and surface property registered on first appearance, and the meshes
/// grouped into soups (<see cref="WorldCollision.Group"/>) that each become
/// one <see cref="RnMesh"/>.
/// </summary>
public sealed class WorldPhysics
{
    /// <summary>A collision attribute as the table keeps it: the strings of the shape that registered it.</summary>
    public sealed record Attribute(string Group, string[] InteractAs, string[] InteractWith, string[] InteractExclude);

    /// <summary>A shape as the part writes it.</summary>
    public sealed record Shape(int Attribute, int Surface, uint ToolMaterialHash, RnHull? Hull, RnMesh? Mesh)
    {
        public string Name { get; init; } = "";

        /// <summary>The name's flags: sealed once "; ..." ends it, long once it passed 50 characters.</summary>
        public bool NameSealed { get; init; }
        public bool NameLong { get; init; }

        /// <summary>A sphere's centre or a capsule's two, and the radius.</summary>
        public (Vector3[] Centers, float Radius)? Round { get; init; }
    }

    public List<Shape> Spheres { get; } = [];
    public List<Shape> Capsules { get; } = [];
    public List<Shape> Hulls { get; } = [];
    public List<Shape> Meshes { get; } = [];
    public List<Attribute> Attributes { get; } = [];

    /// <summary>
    /// The surface table: each entry's hash and the name its shape carried. The
    /// table goes by that name as spelled, not by hash: a prop's surface comes as
    /// vphysics2's name ("Wood") and a material's as the material spells it
    /// ("wood"), so one hash can have two entries (c2m2's prefab).
    /// </summary>
    public List<(uint Hash, string? Name)> Surfaces { get; } = [];

    /// <summary>
    /// The entries RED2 lists, in the order a shape first names them outright (a
    /// prop model's surface, a material's or a painted layer's own property); the
    /// implicit default of a material with none is not listed (triggers; c2m2's
    /// prefab lists "default" only where a painted layer names it).
    /// </summary>
    public List<int> ListedSurfaces { get; } = [];

    /// <summary>How many mesh pieces went into the soups (RED2's physics_shape_mesh_count).</summary>
    public int MeshPieces { get; private set; }

    /// <param name="surfaceName">A surface's name by hash (the game's surfaceproperties), for a prop's.</param>
    /// <param name="entityModel">A brush entity's model rather than the world's (see AttributeOf).</param>
    public static WorldPhysics Build(IReadOnlyList<WorldCollision.Piece> pieces, Func<uint, string?>? surfaceName = null, bool entityModel = false)
    {
        var model = new WorldPhysics();
        var ordered = WorldCollision.PartOrder(pieces, p => p.Type);
        var attributeKeys = new List<string>();
        int AttributeOf(WorldCollision.MaterialPhysics physics, WorldCollision.Piece piece)
        {
            // A shape whose material sets no collision group or tags leaves its
            // node's attribute unset; rc 180c25900 pushes the node's attribute
            // over the part's (180c2d4c0) before taking the index, so it keeps
            // attribute 0, whichever registered it (atixref's glass breakables:
            // toolsnodraw after glass is window; Mako's func_water: water).
            // In the world's model the part's attribute is "default" with no
            // tags (180c2d4c0 seeds it so; 180c2da10 overrides it only for a
            // shape that sets one), so an unset shape takes that attribute,
            // registered in its turn (cs_script_demo: index 1, after the hulls'
            // "Default" excluding player).
            if (entityModel && Unset(physics) && attributeKeys.Count > 0)
                return 0;
            var at = attributeKeys.IndexOf(physics.AttributeKey);
            if (at >= 0)
                return at;
            attributeKeys.Add(physics.AttributeKey);
            model.AttributeSources.Add($"{piece.Type switch { WorldCollision.SphereType => "sphere", WorldCollision.CapsuleType => "capsule", WorldCollision.HullType => "hull", _ => "mesh" }} node {piece.NodeId} {piece.MaterialName} group '{physics.CollisionGroup}'");
            model.Attributes.Add(new Attribute(physics.CollisionGroup.Length == 0 ? "default" : physics.CollisionGroup,
                Tags(physics.InteractAs), Tags(physics.InteractWith), Tags(physics.InteractExclude)));
            return attributeKeys.Count - 1;
        }
        int SurfaceOf(WorldCollision.MaterialPhysics physics, WorldCollision.Piece piece)
        {
            var hash = physics.SurfaceKey;
            var name = physics.SurfaceHash != null ? surfaceName?.Invoke(hash)
                : physics.SurfaceProperty.Length == 0 ? "default" : physics.SurfaceProperty;
            // A prop's surface the game's table does not know comes back as the
            // default (Mako's street bench, 2586520238: not in Valve's table).
            if (physics.SurfaceHash != null && surfaceName != null && name == null)
                (hash, name) = (Maps.SettleWorld.NameHash("default"), surfaceName(Maps.SettleWorld.NameHash("default")));
            var at = model.Surfaces.FindIndex(s => s.Hash == hash && string.Equals(s.Name, name, StringComparison.Ordinal));
            if (at < 0)
            {
                model.SurfaceSources.Add($"type {piece.Type} node {piece.NodeId} {piece.MaterialName} surface '{physics.SurfaceProperty}' hash {hash}{(physics.SurfaceHash != null ? " (model)" : "")}");
                model.Surfaces.Add((hash, name));
                at = model.Surfaces.Count - 1;
            }
            if ((physics.SurfaceHash != null || physics.SurfaceProperty.Length > 0) && !model.ListedSurfaces.Contains(at))
                model.ListedSurfaces.Add(at);
            return at;
        }
        // The part writes spheres, capsules, then hulls (rc 180c28230) before the
        // mesh gatherer runs, so they register in that order.
        // A sphere or capsule registers its attribute and surface before the
        // per-type pass drops one whose radius is not above 0 (rc 180c25810,
        // 180c25230).
        foreach (var p in ordered.Where(p => p.Type == WorldCollision.SphereType))
            if (new Shape(AttributeOf(p.Physics, p), SurfaceOf(p.Physics, p), 0, null, null) { Round = p.Round } is var s && 0f < p.Round!.Value.Radius)
                model.Spheres.Add(s);
        foreach (var p in ordered.Where(p => p.Type == WorldCollision.CapsuleType))
            if (new Shape(AttributeOf(p.Physics, p), SurfaceOf(p.Physics, p), 0, null, null) { Round = p.Round } is var c && 0f < p.Round!.Value.Radius)
                model.Capsules.Add(c);
        foreach (var p in ordered.Where(p => p.Type == WorldCollision.HullType))
            model.Hulls.Add(new Shape(AttributeOf(p.Physics, p), SurfaceOf(p.Physics, p),
                Io.ResourceNames.ToolMaterialHash(p.ToolMaterial), p.Hull, null) { Name = p.Name });
        var meshPieces = ordered.Where(p => p.Type == WorldCollision.MeshType).ToList();
        var meshes = meshPieces.Select(p => (Attribute: AttributeOf(p.Physics, p), Surface: SurfaceOf(p.Physics, p), p.Points, p.Indices)).ToList();
        model.MeshPieces = meshes.Count;
        foreach (var soup in WorldCollision.Group(meshes))
        {
            var mesh = RnMeshBuilder.Create([.. soup.Indices], [.. soup.Vertices], soup.Materials?.ToArray());
            if (mesh == null)
                continue;
            // rc 180c27e30 counts each member's triangles under its tool hash
            // (180c25900); 180c28690 keeps the hash when exactly one has any.
            var withTriangles = soup.Members.Where(m => meshPieces[m].Indices.Length >= 3)
                .Select(m => Io.ResourceNames.ToolMaterialHash(meshPieces[m].ToolMaterial)).Distinct().ToList();
            var tool = withTriangles.Count == 1 ? withTriangles[0] : 0;
            var (name, isSealed, isLong) = SoupName(soup.Members.Where(m => meshPieces[m].Indices.Length >= 3).Select(m => meshPieces[m].Name));
            model.Meshes.Add(new Shape(soup.Attribute, soup.SurfaceProperty, tool, null, mesh)
            {
                Name = name, NameSealed = isSealed, NameLong = isLong,
            });
        }
        return model;
    }

    /// <summary>
    /// A soup's name as rc 180c27e30 joins its members' (those with triangles;
    /// it skips the rest outright): the first member's name,
    /// then each later non-empty one after "; " until the name passes 50
    /// characters (long); the next non-empty name after that adds "; ..." and
    /// seals it, and nothing is added after (Mako's first soup).
    /// </summary>
    internal static (string Name, bool Sealed, bool Long) SoupName(IEnumerable<string> members)
    {
        var name = "";
        bool first = true, isSealed = false, isLong = false;
        foreach (var member in members)
        {
            if (first)
            {
                name = member;
                first = false;
                continue;
            }
            if (member.Length == 0 || isSealed)
                continue;
            if (isLong)
            {
                isSealed = true;
                name += "; ...";
                continue;
            }
            if (name.Length > 0)
                name += "; ";
            name += member;
            if (name.Length > 50)
                isLong = true;
        }
        return (name, isSealed, isLong);
    }

    // The node loop marks a shape's attribute set when its group is non-empty
    // or any list parses to a tag (rc 1802c3030), so ", " alone is unset.
    private static bool Unset(WorldCollision.MaterialPhysics p)
        => p.CollisionGroup.Length == 0 && Tags(p.InteractAs).Length == 0 && Tags(p.InteractWith).Length == 0
           && Tags(p.InteractExclude).Length == 0;

    // A tag list as the table writes it: each tag once, first spelling kept, in
    // V_stricmp_fast order (c2m2: ladder, npcclip, playerclip; atixref:
    // "window, window" is one).
    private static string[] Tags(string list) => [.. Io.Tier0Strings.ParseTags(list)];

    /// <summary>Every shape in the part's order: spheres, capsules, hulls, meshes.</summary>
    public IEnumerable<Shape> AllShapes => Spheres.Concat(Capsules).Concat(Hulls).Concat(Meshes);

    /// <summary>Which piece registered each surface, for exploration.</summary>
    public List<string> SurfaceSources { get; } = [];

    /// <summary>Which piece registered each attribute, for exploration.</summary>
    public List<string> AttributeSources { get; } = [];
}

/// <summary>
/// The trees of a map's world_physics.vmdl_c (<see cref="WorldPhysicsAuthor"/>),
/// typed as Valve's decode back: an unsigned field whose value is 0 or 1 comes
/// back Int64 (KV3's zero and one encodings), floats are doubles.
/// </summary>
public static class WorldPhysicsTrees
{
    /// <summary>The model DATA: a named model with no meshes, bones or groups.</summary>
    public static KVObject Data(string mapName) => ModelData($"maps/{mapName}/world_physics.vmdl");

    /// <summary>The DATA of a model holding only physics, by its name.</summary>
    public static KVObject ModelData(string modelName)
    {
        var zero3 = () => Vec(Vector3.Zero);
        var info = Collection(
            ("m_nFlags", new KVObject(8388608u)),
            ("m_vHullMin", zero3()), ("m_vHullMax", zero3()), ("m_vViewMin", zero3()), ("m_vViewMax", zero3()),
            ("m_flMass", new KVObject(0.0)), ("m_vEyePosition", zero3()), ("m_flMaxEyeDeflection", new KVObject(0.0)),
            ("m_sSurfaceProperty", new KVObject("")), ("m_keyValueText", new KVObject("")));
        var skeleton = Collection(("m_boneName", Empty()), ("m_nParent", Empty()), ("m_boneSphere", Empty()), ("m_nFlag", Empty()),
            ("m_bonePosParent", Empty()), ("m_boneRotParent", Empty()), ("m_boneScaleParent", Empty()));
        return Collection(
            ("m_name", new KVObject(modelName)),
            ("m_modelInfo", info),
            ("m_ExtParts", Empty()), ("m_refMeshes", Empty()), ("m_refMeshGroupMasks", Empty()), ("m_refPhysGroupMasks", Empty()),
            ("m_refLODGroupMasks", Empty()), ("m_lodGroupSwitchDistances", Empty()), ("m_refPhysicsData", Empty()),
            ("m_refPhysicsHitboxData", Empty()), ("m_refAnimGroups", Empty()), ("m_refSequenceGroups", Empty()),
            ("m_meshGroups", Empty()), ("m_materialGroups", Empty()),
            ("m_nDefaultMeshGroupMask", new KVObject(ulong.MaxValue)),
            ("m_modelSkeleton", skeleton),
            ("m_remappingTable", Empty()), ("m_remappingTableStarts", Empty()), ("m_boneFlexDrivers", Empty()),
            ("m_pModelConfigList", KVObject.Null()),
            ("m_BodyGroupsHiddenInTools", Empty()), ("m_refAnimIncludeModels", Empty()), ("m_AnimatedMaterialAttributes", Empty()),
            ("m_animGraph2Refs", Empty()), ("m_vecNmSkeletonRefs", Empty()));
    }

    /// <summary>
    /// The model's RED2: the ModelDoc compiler identity, the shape counts and
    /// the surface properties in table order, each under its canonical name
    /// (the game's surfaceproperties.vsurf_c by hash, <paramref name="surfaceName"/>).
    /// </summary>
    /// <param name="entityClass">For a brush entity's model, its class: keep_vertices
    /// is then an IntArg and the class argument's fingerprint its string token.</param>
    public static KVObject Red2(WorldPhysics model, Func<uint, string?> surfaceName, string? entityClass = null)
    {
        KVObject Argument(string name, string type, uint fingerprint = 0) => Collection(
            ("m_ParameterName", new KVObject(name)), ("m_ParameterType", new KVObject(type)),
            ("m_nFingerprint", fingerprint == 0 ? new KVObject(0L) : new KVObject(fingerprint)), ("m_nFingerprintDefault", new KVObject(0L)));
        var arguments = Array(Argument("___OverrideInputData___", "BinaryBlobArg"),
            Argument("keep_vertices", entityClass == null ? "FloatArg" : "IntArg"),
            Argument("mapbuilder_entity_classname", "StringArg", entityClass == null ? 0 : Maps.SettleWorld.NameHash(entityClass)));
        var special = Array(Collection(("m_String", new KVObject("ModelDoc Compiler Version")), ("m_CompilerIdentifier", new KVObject("CompileModel")),
            ("m_nFingerprint", U(3)), ("m_nUserData", new KVObject(0L))));
        var shapes = model.Spheres.Count + model.Capsules.Count + model.Hulls.Count + model.MeshPieces;
        var user = Collection(("compile_warnings", new KVObject(0L)));
        // Only when a listed entry is "default" (atixref, c2m2's prefab; ze_hold_em_p has none, and no key).
        if (model.ListedSurfaces.Any(i => model.Surfaces[i].Hash == Maps.SettleWorld.NameHash("default")))
            user.Add("has_default_surface_property", new KVObject(1L));
        foreach (var (key, value) in Collection(
            ("IsChildResource", new KVObject(1L)),
            ("model_animgraph2ref_count", new KVObject(0L)), ("model_archetype_id", new KVObject("")), ("model_bodygroup_count", new KVObject(0L)),
            ("model_bone_count", new KVObject(0L)), ("model_has_embedded_animation", new KVObject(0L)), ("model_is_modeldoc", new KVObject(1L)),
            ("model_lod0_triangle_count", new KVObject(0L)), ("model_lod0_vertex_count", new KVObject(0L)), ("model_lod_count", new KVObject(0L)),
            ("model_materialgroup_count", new KVObject(0L)), ("model_nmskeletonref_count", new KVObject(0L)),
            ("model_primary_associated_entity", new KVObject("")), ("model_total_triangle_count", new KVObject(0L)),
            ("model_total_vertex_count", new KVObject(0L)), ("morph", new KVObject(0L)), ("morph_atlas_pixels", new KVObject(0L)),
            ("physics_joint_count", new KVObject(0L))).Children)
            user.Add(key, value);
        // Counts only when there are shapes of the kind (a trigger's model has no
        // mesh count, atixref's empty func_water model no count at all), the keys
        // in alphabetical order (s2c_rounds: capsule, count, mesh, sphere).
        foreach (var (key, count) in new[] { ("physics_shape_capsule_count", model.Capsules.Count), ("physics_shape_count", shapes),
                     ("physics_shape_hull_count", model.Hulls.Count), ("physics_shape_mesh_count", model.MeshPieces),
                     ("physics_shape_sphere_count", model.Spheres.Count) })
            if (count > 0)
                user.Add(key, I(count));
        // Each listed spelling merged by its A-Z folded hash (rc 180175420 keys the
        // symbol on it), under the first spelling met, the value how many
        // spellings share it (c2m2's prefab: Wood = 2, from "Wood" and "wood").
        var counts = new List<(uint Hash, string Name, int Count)>();
        foreach (var i in model.ListedSurfaces)
        {
            var (hash, name) = model.Surfaces[i];
            var spelled = name ?? surfaceName(hash) ?? throw new InvalidOperationException($"no name for surface property {hash}");
            var at = counts.FindIndex(c => c.Hash == hash);
            if (at < 0)
                counts.Add((hash, spelled, 1));
            else
                counts[at] = counts[at] with { Count = counts[at].Count + 1 };
        }
        var names = KVObject.Collection();
        foreach (var (_, name, count) in counts)
            names.Add(name, I(count));
        return Collection(
            ("m_InputDependencies", Empty()), ("m_AdditionalInputDependencies", Empty()), ("m_ArgumentDependencies", arguments),
            ("m_SpecialDependencies", special), ("m_SpecialInputDependencies", Empty()), ("m_AdditionalRelatedFiles", Empty()),
            ("m_ChildResourceList", Empty()), ("m_WeakReferenceList", Empty()), ("m_SearchableUserData", user),
            ("m_SubassetReferences", names.Children.Any() ? Collection(("surface_prop", names)) : KVObject.Null()),
            ("m_SubassetDefinitions", KVObject.Null()));
    }

    /// <summary>The embedded physics aggregate: one part holding every shape, and the tables.</summary>
    /// <param name="partFlags">The part's flags: 2 for the world, 0 for a brush entity's model.</param>
    public static KVObject Phys(WorldPhysics model, uint partFlags = 2)
    {
        var shape = Collection(
            ("m_spheres", Array([.. model.Spheres.Select(s => ShapeDesc(s, "m_Sphere", Collection(
                ("m_vCenter", Vec(s.Round!.Value.Centers[0])), ("m_flRadius", F(s.Round.Value.Radius)))))])),
            ("m_capsules", Array([.. model.Capsules.Select(s => ShapeDesc(s, "m_Capsule", Collection(
                ("m_vCenter", Array(Vec(s.Round!.Value.Centers[0]), Vec(s.Round.Value.Centers[1]))), ("m_flRadius", F(s.Round.Value.Radius)))))])),
            ("m_hulls", Array([.. model.Hulls.Select(s => ShapeDesc(s, "m_Hull", Hull(s.Hull!)))])),
            ("m_meshes", Array([.. model.Meshes.Select(s => ShapeDesc(s, "m_Mesh", Mesh(s.Mesh!)))])),
            ("m_compounds", Empty()),
            // Empty when every shape takes attribute 0 (ze_hold_em_p), else one per shape.
            ("m_CollisionAttributeIndices", model.AllShapes.All(s => s.Attribute == 0) ? Empty()
                : Array([.. model.AllShapes.Select(s => new KVObject((uint)s.Attribute))])));
        var part = Collection(
            ("m_nFlags", U(partFlags)), ("m_flMass", new KVObject(0.0)), ("m_rnShape", shape),
            ("m_nCollisionAttributeIndex", new KVObject(0L)), ("m_nReserved", new KVObject(0L)),
            ("m_flInertiaScale", new KVObject(1.0)), ("m_flLinearDamping", new KVObject(0.0)), ("m_flAngularDamping", new KVObject(0.0)),
            ("m_flLinearDrag", new KVObject(1.0)), ("m_flAngularDrag", new KVObject(1.0)),
            ("m_bOverrideMassCenter", new KVObject(false)), ("m_vMassCenterOverride", Vec(Vector3.Zero)));
        return Collection(
            ("m_nFlags", new KVObject(0L)), ("m_nRefCounter", new KVObject(0L)), ("m_bCompoundsPacked", new KVObject(false)),
            ("m_bonesHash", Empty()), ("m_boneNames", Empty()), ("m_indexNames", Empty()), ("m_indexHash", Empty()), ("m_bindPose", Empty()),
            ("m_parts", Array(part)),
            ("m_shapeMarkups", Empty()), ("m_constraints2", Empty()), ("m_joints", Empty()), ("m_pFeModel", KVObject.Null()),
            ("m_boneParents", Empty()),
            ("m_surfacePropertyHashes", Array([.. model.Surfaces.Select(s => U(s.Hash))])),
            ("m_collisionAttributes", Array([.. model.Attributes.Select(Attribute)])),
            ("m_debugPartNames", Empty()), ("m_embeddedKeyvalues", new KVObject("")));
    }

    private static KVObject Attribute(WorldPhysics.Attribute a)
    {
        KVObject Hashes(string[] tags) => Array([.. tags.Select(t => U(Maps.SettleWorld.NameHash(t)))]);
        KVObject Strings(string[] tags) => Array([.. tags.Select(t => new KVObject(t))]);
        return Collection(
            ("m_nIncludeDetailLayerCount", new KVObject(0L)),
            ("m_CollisionGroup", U(Maps.SettleWorld.NameHash(a.Group))),
            ("m_InteractAs", Hashes(a.InteractAs)), ("m_InteractWith", Hashes(a.InteractWith)), ("m_InteractExclude", Hashes(a.InteractExclude)),
            ("m_DetailLayers", Empty()),
            ("m_CollisionGroupString", new KVObject(a.Group)),
            ("m_InteractAsStrings", Strings(a.InteractAs)), ("m_InteractWithStrings", Strings(a.InteractWith)),
            ("m_InteractExcludeStrings", Strings(a.InteractExclude)), ("m_DetailLayerStrings", Empty()));
    }

    private static KVObject ShapeDesc(WorldPhysics.Shape s, string key, KVObject body) => Collection(
        ("m_nCollisionAttributeIndex", U((uint)s.Attribute)), ("m_nSurfacePropertyIndex", U((uint)s.Surface)),
        ("m_UserFriendlyName", new KVObject(s.Name)), ("m_bUserFriendlyNameSealed", new KVObject(s.NameSealed)), ("m_bUserFriendlyNameLong", new KVObject(s.NameLong)),
        ("m_nToolMaterialHash", U(s.ToolMaterialHash)), (key, body));

    private static KVObject Hull(RnHull h)
    {
        var svm = h.RegionSvm ?? throw new InvalidOperationException("a hull without its region SVM");
        return Collection(
            ("m_vCentroid", Vec(h.Centroid)), ("m_flMaxAngularRadius", F(h.MaxAngularRadius)), ("m_flMinCentroidRadius", F(h.MinCentroidRadius)),
            ("m_Bounds", Collection(("m_vMinBounds", Vec(h.BoundsMin)), ("m_vMaxBounds", Vec(h.BoundsMax)))),
            ("m_vOrthographicAreas", Vec(h.OrthographicAreas)),
            ("m_MassProperties", Array([.. h.MassProperties.Select(F)])),
            ("m_flVolume", F(h.Volume)), ("m_flSurfaceArea", F(h.SurfaceArea)), ("m_nFlags", U(h.Flags)),
            ("m_pRegionSVM", Collection(("m_Planes", Blob(Planes(svm.Planes))), ("m_Nodes", Blob(MemoryMarshal.AsBytes(svm.Nodes.AsSpan()).ToArray())))),
            ("m_Vertices", Blob(h.Vertices)),
            ("m_VertexPositions", Blob(MemoryMarshal.AsBytes(h.VertexPositions.AsSpan()).ToArray())),
            ("m_Edges", Blob([.. h.Edges.SelectMany(e => new[] { e.Next, e.Twin, e.Origin, e.Face })])),
            ("m_Faces", Blob(h.Faces)),
            ("m_Planes", Blob(Planes(h.Planes))));
    }

    private static KVObject Mesh(RnMesh m)
    {
        var nodes = new byte[m.Nodes.Length * 32];
        for (var i = 0; i < m.Nodes.Length; i++)
        {
            var n = m.Nodes[i];
            var span = nodes.AsSpan(i * 32, 32);
            MemoryMarshal.Write(span, n.Min);
            MemoryMarshal.Write(span[12..], n.Children);
            MemoryMarshal.Write(span[16..], n.Max);
            MemoryMarshal.Write(span[28..], n.TriangleOffset);
        }
        var triangles = m.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
        return Collection(
            ("m_vMin", Vec(m.Min)), ("m_vMax", Vec(m.Max)),
            ("m_Materials", Array([.. m.Materials.Select(b => new KVObject((uint)b))])),
            ("m_vOrthographicAreas", Vec(m.OrthographicAreas)),
            ("m_nFlags", U(m.Flags)), ("m_nDebugFlags", U(m.DebugFlags)), ("m_flSurfaceArea", F(m.SurfaceArea)),
            ("m_Nodes", Blob(nodes)),
            ("m_Triangles", Blob(MemoryMarshal.AsBytes(triangles.AsSpan()).ToArray())),
            ("m_Vertices", Blob(MemoryMarshal.AsBytes(m.Vertices.AsSpan()).ToArray())));
    }

    private static byte[] Planes((Vector3 Normal, float Offset)[] planes)
    {
        var bytes = new byte[planes.Length * 16];
        for (var i = 0; i < planes.Length; i++)
        {
            MemoryMarshal.Write(bytes.AsSpan(i * 16), planes[i].Normal);
            MemoryMarshal.Write(bytes.AsSpan((i * 16) + 12), planes[i].Offset);
        }
        return bytes;
    }

    // An unsigned field as it decodes: 0 and 1 go through KV3's Int64 zero and one.
    private static KVObject U(uint v) => v <= 1 ? new KVObject((long)v) : new KVObject(v);

    // A signed count as RED2 carries it.
    private static KVObject I(int v) => v <= 1 ? new KVObject((long)v) : new KVObject(v);

    private static KVObject F(float v) => new((double)v);

    private static KVObject Vec(Vector3 v) => Array(F(v.X), F(v.Y), F(v.Z));

    private static KVObject Blob(byte[] bytes) => KVObject.Blob(bytes);

    private static KVObject Empty() => KVObject.Array();

    private static KVObject Array(params KVObject[] items)
    {
        var a = KVObject.Array();
        foreach (var i in items)
            a.Add(i);
        return a;
    }

    private static KVObject Collection(params (string Key, KVObject Value)[] items)
    {
        var c = KVObject.Collection();
        foreach (var (k, v) in items)
            c.Add(k, v);
        return c;
    }
}
