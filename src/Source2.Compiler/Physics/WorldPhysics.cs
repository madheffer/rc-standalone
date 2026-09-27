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
    }

    public List<Shape> Hulls { get; } = [];
    public List<Shape> Meshes { get; } = [];
    public List<Attribute> Attributes { get; } = [];

    /// <summary>The surface table: each hash and, where known, its name.</summary>
    public List<(uint Hash, string? Name)> Surfaces { get; } = [];

    /// <summary>How many mesh pieces went into the soups (RED2's physics_shape_mesh_count).</summary>
    public int MeshPieces { get; private set; }

    public static WorldPhysics Build(IReadOnlyList<WorldCollision.Piece> pieces)
    {
        var model = new WorldPhysics();
        var ordered = WorldCollision.PartOrder(pieces, p => p.Type);
        var attributeKeys = new List<string>();
        int AttributeOf(WorldCollision.MaterialPhysics physics)
        {
            var at = attributeKeys.IndexOf(physics.AttributeKey);
            if (at >= 0)
                return at;
            attributeKeys.Add(physics.AttributeKey);
            model.Attributes.Add(new Attribute(physics.CollisionGroup.Length == 0 ? "Default" : physics.CollisionGroup,
                Tags(physics.InteractAs), Tags(physics.InteractWith), Tags(physics.InteractExclude)));
            return attributeKeys.Count - 1;
        }
        int SurfaceOf(WorldCollision.MaterialPhysics physics)
        {
            var hash = physics.SurfaceKey;
            var at = model.Surfaces.FindIndex(s => s.Hash == hash);
            if (at >= 0)
                return at;
            model.Surfaces.Add((hash, physics.SurfaceHash == null ? (physics.SurfaceProperty.Length == 0 ? "default" : physics.SurfaceProperty) : null));
            return model.Surfaces.Count - 1;
        }
        // Hulls are written before the mesh gatherer runs, so they register first.
        foreach (var p in ordered.Where(p => p.Hull != null))
            model.Hulls.Add(new Shape(AttributeOf(p.Physics), SurfaceOf(p.Physics), 0, p.Hull, null));
        var meshPieces = ordered.Where(p => p.Hull == null).ToList();
        var meshes = meshPieces.Select(p => (Attribute: AttributeOf(p.Physics), Surface: SurfaceOf(p.Physics), p.Points, p.Indices)).ToList();
        model.MeshPieces = meshes.Count;
        foreach (var soup in WorldCollision.Group(meshes))
        {
            var mesh = RnMeshBuilder.Create([.. soup.Indices], [.. soup.Vertices], soup.Materials?.ToArray());
            if (mesh == null)
                continue;
            var tools = soup.Members.Select(m => meshPieces[m].ToolMaterial).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var tool = tools.Count == 1 && tools[0].Length > 0 ? Maps.SettleWorld.NameHash(tools[0]) : 0;
            model.Meshes.Add(new Shape(soup.Attribute, soup.SurfaceProperty, tool, null, mesh)
            {
                Name = string.Join("; ", soup.Members.Select(m => meshPieces[m].Name).Where(n => n.Length > 0)),
            });
        }
        return model;
    }

    private static string[] Tags(string list)
        => list.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// The trees of a map's world_physics.vmdl_c (<see cref="WorldPhysicsAuthor"/>),
/// typed as Valve's decode back: an unsigned field whose value is 0 or 1 comes
/// back Int64 (KV3's zero and one encodings), floats are doubles.
/// </summary>
public static class WorldPhysicsTrees
{
    /// <summary>The model DATA: a named model with no meshes, bones or groups.</summary>
    public static KVObject Data(string mapName)
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
            ("m_name", new KVObject($"maps/{mapName}/world_physics.vmdl")),
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
    public static KVObject Red2(WorldPhysics model, Func<uint, string?> surfaceName)
    {
        KVObject Argument(string name, string type) => Collection(
            ("m_ParameterName", new KVObject(name)), ("m_ParameterType", new KVObject(type)),
            ("m_nFingerprint", new KVObject(0L)), ("m_nFingerprintDefault", new KVObject(0L)));
        var arguments = Array(Argument("___OverrideInputData___", "BinaryBlobArg"), Argument("keep_vertices", "FloatArg"),
            Argument("mapbuilder_entity_classname", "StringArg"));
        var special = Array(Collection(("m_String", new KVObject("ModelDoc Compiler Version")), ("m_CompilerIdentifier", new KVObject("CompileModel")),
            ("m_nFingerprint", U(3)), ("m_nUserData", new KVObject(0L))));
        var shapes = model.Hulls.Count + model.MeshPieces;
        var user = Collection(("compile_warnings", new KVObject(0L)));
        // Only when the table holds "default" (atixref has it; ze_hold_em_p has not, and no key).
        if (model.Surfaces.Any(s => s.Hash == Maps.SettleWorld.NameHash("default")))
            user.Add("has_default_surface_property", new KVObject(1L));
        foreach (var (key, value) in Collection(
            ("IsChildResource", new KVObject(1L)),
            ("model_animgraph2ref_count", new KVObject(0L)), ("model_archetype_id", new KVObject("")), ("model_bodygroup_count", new KVObject(0L)),
            ("model_bone_count", new KVObject(0L)), ("model_has_embedded_animation", new KVObject(0L)), ("model_is_modeldoc", new KVObject(1L)),
            ("model_lod0_triangle_count", new KVObject(0L)), ("model_lod0_vertex_count", new KVObject(0L)), ("model_lod_count", new KVObject(0L)),
            ("model_materialgroup_count", new KVObject(0L)), ("model_nmskeletonref_count", new KVObject(0L)),
            ("model_primary_associated_entity", new KVObject("")), ("model_total_triangle_count", new KVObject(0L)),
            ("model_total_vertex_count", new KVObject(0L)), ("morph", new KVObject(0L)), ("morph_atlas_pixels", new KVObject(0L)),
            ("physics_joint_count", new KVObject(0L)),
            ("physics_shape_count", I(shapes)), ("physics_shape_hull_count", I(model.Hulls.Count)), ("physics_shape_mesh_count", I(model.MeshPieces))).Children)
            user.Add(key, value);
        var names = KVObject.Collection();
        foreach (var (hash, name) in model.Surfaces)
        {
            var canonical = surfaceName(hash) ?? name ?? throw new InvalidOperationException($"no name for surface property {hash}");
            if (!names.ContainsKey(canonical))
                names.Add(canonical, new KVObject(1L));
        }
        return Collection(
            ("m_InputDependencies", Empty()), ("m_AdditionalInputDependencies", Empty()), ("m_ArgumentDependencies", arguments),
            ("m_SpecialDependencies", special), ("m_SpecialInputDependencies", Empty()), ("m_AdditionalRelatedFiles", Empty()),
            ("m_ChildResourceList", Empty()), ("m_WeakReferenceList", Empty()), ("m_SearchableUserData", user),
            ("m_SubassetReferences", Collection(("surface_prop", names))), ("m_SubassetDefinitions", KVObject.Null()));
    }

    /// <summary>The embedded physics aggregate: one part holding every shape, and the tables.</summary>
    public static KVObject Phys(WorldPhysics model)
    {
        var shape = Collection(
            ("m_spheres", Empty()), ("m_capsules", Empty()),
            ("m_hulls", Array([.. model.Hulls.Select(s => ShapeDesc(s, "m_Hull", Hull(s.Hull!)))])),
            ("m_meshes", Array([.. model.Meshes.Select(s => ShapeDesc(s, "m_Mesh", Mesh(s.Mesh!)))])),
            ("m_compounds", Empty()),
            // Empty when every shape takes attribute 0 (ze_hold_em_p), else one per shape.
            ("m_CollisionAttributeIndices", model.Hulls.Concat(model.Meshes).All(s => s.Attribute == 0) ? Empty()
                : Array([.. model.Hulls.Concat(model.Meshes).Select(s => new KVObject((uint)s.Attribute))])));
        var part = Collection(
            ("m_nFlags", U(2)), ("m_flMass", new KVObject(0.0)), ("m_rnShape", shape),
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
        ("m_UserFriendlyName", new KVObject(s.Name)), ("m_bUserFriendlyNameSealed", new KVObject(false)), ("m_bUserFriendlyNameLong", new KVObject(false)),
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
