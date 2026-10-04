using System.Numerics;
using Source2.Compiler.Maps;

namespace Source2.Compiler.Physics;

/// <summary>
/// A brush entity's collision as its model carries it
/// (<c>maps/&lt;map&gt;/entities/&lt;name&gt;_&lt;node&gt;.vmdl_c</c>): for each mesh
/// under the entity, depth first, each material piece welded in the entity's
/// space (<see cref="BrushHulls.Pieces"/>) and hulled by its physics type
/// (<see cref="BrushHulls.Resolve"/>, <see cref="BrushHulls.Inputs"/>), into one
/// part with flags 0. Each hull keeps its material's collision attribute,
/// surface and tool material hash. Unlike the world, a non-solid material
/// (a trigger's) still makes hulls.
/// <para>A class with <c>auto_apply_material</c> metadata (the triggers'
/// toolstrigger) names that material in every slot, as the settle reads it. A
/// model is physics only when its class says <c>physics_only_model</c> or every
/// face's material is <c>mapbuilder.nodraw</c> (atixref: 84 of 144); the others
/// carry render meshes, which are not ported. A model with a material the game
/// cannot find fails to compile and has no file (atixref 26, ze_hold_em_p 8,
/// every one of them). Mesh-type pieces and the PhysicsTypeOverride base
/// classes (only func_shatterglass is taken as a mesh override) are not ported
/// and are listed.</para>
/// </summary>
public static class EntityPhysicsModels
{
    /// <summary>One entity's model: its path, class, whether it holds only physics, and the part.</summary>
    public sealed record Model(int NodeId, string ClassName, string Path, bool PhysicsOnly, WorldPhysics Physics);

    /// <param name="smartProp">A smart prop definition by path, whose locators take
    /// node ids before the instance copies do (<see cref="SmartProps.NodesCreatedOnLoad"/>).</param>
    public static List<Model> Build(DmxBinary.Document doc, string mapName, Func<string, SettleWorld.MaterialInfo?> material,
        Func<string, SettleWorld.CollisionProperty?> collisionProperty, FgdSchema? schema, List<string>? notes = null,
        Func<string, ValveKeyValue.KVObject?>? smartProp = null)
    {
        mapName = Io.Tier0Strings.LowerAscii(mapName);
        var models = new List<Model>();
        // An instance's group is a template, and a node the visibility manager
        // hides is not compiled (MapEntities.HiddenNodes); neither has a model.
        var targets = new HashSet<DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        foreach (var instance in doc.OfType("CMapInstance"))
            if (instance.Get<DmxBinary.Element>("target") is { } target)
                targets.Add(target);
        var mapHidden = MapEntities.HiddenNodes(doc);
        var entities = new List<(DmxBinary.Element Element, int NodeId, IReadOnlyList<DmxBinary.Element> Through, string IdPath,
                                 DmxBinary.Element[] Prefabs, HashSet<int> Hidden)>();
        // A prefab's map is walked where the prefab stands, with its own hidden
        // nodes and instance targets; its entities are named by id path
        // (s2c_prefabprobe2: unnamed_108_3.vmdl).
        void Walk(DmxBinary.Element node, string prefix, DmxBinary.Element[] prefabs, HashSet<int> hidden, HashSet<DmxBinary.Element> skip)
        {
            foreach (var child in node.GetElements("children"))
            {
                var id = child.GetValue<int>("nodeID") ?? -1;
                if (skip.Contains(child) || hidden.Contains(id))
                    continue;
                if (child.Type == "CMapEntity")
                    entities.Add((child, id, [], prefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture), prefabs, hidden));
                if (child.Type == "CMapPrefab" && child.Get<DmxBinary.Element>(MapPrefabs.WorldKey) is { } prefabWorld)
                {
                    Walk(prefabWorld, prefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_", [.. prefabs, child],
                         child.Get<HashSet<int>>(MapPrefabs.HiddenKey) ?? [],
                         new HashSet<DmxBinary.Element>(child.Get<List<DmxBinary.Element>>(MapPrefabs.TargetsKey) ?? [], ReferenceEqualityComparer.Instance));
                    continue;
                }
                Walk(child, prefix, prefabs, hidden, skip);
            }
        }
        foreach (var world in doc.OfType("CMapWorld"))
            Walk(world, "", [], mapHidden, targets);
        // Each copy an instance places is the template entity under the copy's
        // own id (MapInstances.Expand), named after the template.
        var createdOnLoad = smartProp == null ? 0
            : SmartProps.NodesCreatedOnLoad(doc, path => smartProp(path) is { } definition ? SmartProps.LocatorsOf(definition) : 0);
        MapInstances.Expand(doc, MapEntities.From(doc), createdOnLoad, (node, id, through) =>
        {
            if (node.Type == "CMapEntity")
                entities.Add((node, id, through, id.ToString(System.Globalization.CultureInfo.InvariantCulture), [], mapHidden));
        });
        foreach (var (entity, nodeId, through, idPath, prefabs, hidden) in entities)
        {
            if (One(entity, nodeId, through, idPath, prefabs, hidden) is { } model)
                models.Add(model);
        }
        return models;

        Model? One(DmxBinary.Element entity, int nodeId, IReadOnlyList<DmxBinary.Element> through, string idPath,
                   DmxBinary.Element[] prefabs, HashSet<int> hidden)
        {
            // An instance copy's entity and meshes are where the collapse moved
            // them (Mako's spawn train doors and rotated func_doors); a prefab's
            // where the prefab moves them.
            Func<DmxBinary.Element, CTransform>? transformOf = through.Count > 0 ? node =>
            {
                var (origin, angles) = SettleWorld.BakedPlacement(node, through);
                return new CTransform(origin, 1f, CTransform.AngleQuaternion(angles));
            }
            : prefabs.Length > 0 ? node =>
            {
                var (origin, angles) = SettleWorld.PrefabPlacement(node.GetValue<Vector3>("origin") ?? Vector3.Zero,
                                                                   node.GetValue<Vector3>("angles") ?? Vector3.Zero, prefabs);
                return new CTransform(origin, 1f, CTransform.AngleQuaternion(angles));
            }
            : null;
            // The lump points every brush entity at a model, but a hidden mesh is
            // not compiled, and an entity left with none gets no file.
            var meshes = Meshes(entity).Where(m => !hidden.Contains(m.GetValue<int>("nodeID") ?? -1)).ToList();
            if (!entity.GetElements("children").Any(c => c.Type is "CMapMesh") || meshes.Count == 0)
                return null;
            var props = entity.Get<DmxBinary.Element>("entity_properties");
            var className = props?.Get<string>("classname") ?? "";
            var targetName = props?.Get<string>("targetname") ?? "";
            // Inside a prefab with fixupEntityNames the name the path is built from
            // is the fixed-up one, "[PR#]<prefab id>_<name>" (MapEntities.Walk).
            var fixup = prefabs.FirstOrDefault(p => p.GetValue<bool>("fixupEntityNames") == true) is { } fixing
                ? "[PR#]" + (fixing.GetValue<int>("nodeID") ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" : null;
            var path = EntityLumpAuthor.BrushModelPath(EntityLumpAuthor.FixedName(targetName, fixup, schema), idPath, mapName);
            var pieces = new List<WorldCollision.Piece>();
            var physicsOnly = true;
            var applied = schema?.MetadataOf(className, "auto_apply_material") is { Length: > 0 } a ? a : null;
            var used = meshes.SelectMany(m => m.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials") ?? []).Select(x => x as string ?? "").Distinct().ToList();
            if (used.FirstOrDefault(m => material(m) == null) is { } absent)
            {
                notes?.Add($"entity {nodeId} ({className}): {absent} not found, so its model fails to compile and has no file");
                return null;
            }
            foreach (var mesh in meshes)
            {
                var names = mesh.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials") ?? [];
                var type = BrushHulls.Resolve(PhysicsTypeOf(mesh), true, className == "func_shatterglass", false, false);
                BrushHulls.RefuseSimplification(mesh);
                foreach (var (slot, positions, faces, local) in BrushHulls.Pieces(mesh, entity, transformOf: transformOf))
                {
                    var own = slot < names.Length ? (names[slot] as string ?? "") : "";
                    if (material(own)?.Ints.GetValueOrDefault("mapbuilder.nodraw") != 1)
                        physicsOnly = false;
                    var name = applied ?? own;
                    if (type == BrushHulls.PhysicsType.None)
                        continue;
                    if (type == BrushHulls.PhysicsType.Mesh)
                        continue;
                    var physics = WorldCollision.ReadMaterial(material(name), collisionProperty, shaderTranslucency: false);
                    foreach (var input in BrushHulls.Inputs(positions, faces, type, local))
                    {
                        var qh = RnHullBuilder.BuildHull(input, RnHullBuilder.Options.MapBuilder, out _);
                        var points = qh == null ? null : BrushHulls.ShapePoints([.. qh.HullVertices.Select(v => new System.Numerics.Vector3(v.X, v.Y, v.Z))]);
                        var hull = points == null ? null : RnHullBuilder.Create(points, RnHullBuilder.Options.Compile, out _);
                        // A piece the builder cannot hull adds nothing.
                        if (hull == null)
                            continue;
                        hull.RegionSvm = RegionSvmBuilder.Build(hull);
                        RnHullBuilder.Transform(hull, RnHullBuilder.Identity);
                        pieces.Add(new WorldCollision.Piece(nodeId, slot, name, physics, hull.VertexPositions, []) { Hull = hull, ToolMaterial = name });
                    }
                }
                // A mesh-type mesh gives its pieces as triangle meshes, cut and welded
                // as the world's are, in the entity's space (Mako's ladders and pushes).
                if (type == BrushHulls.PhysicsType.Mesh)
                {
                    foreach (var (slot, positions, faces, local, corners, _, _) in BrushHulls.PiecesWithCorners(mesh, entity, transformOf: transformOf))
                    {
                        var own = slot < names.Length ? (names[slot] as string ?? "") : "";
                        var name = applied ?? own;
                        var physics = WorldCollision.ReadMaterial(material(name), collisionProperty, shaderTranslucency: false);
                        var (points, triangles) = BrushHulls.TriangleMesh(positions, faces, local, corners);
                        var indices = new int[triangles.Count * 3];
                        for (var t = 0; t < triangles.Count; t++)
                            (indices[t * 3], indices[(t * 3) + 1], indices[(t * 3) + 2]) = triangles[t];
                        pieces.Add(new WorldCollision.Piece(nodeId, slot, name, physics, [.. points], indices) { ToolMaterial = name });
                    }
                }
            }
            // Flags in the class's metadata: physics_only_model (the triggers, and
            // post_processing_volume through them), and render_as_world_but_physics_as_entity
            // (func_water, whose render goes to the world).
            physicsOnly |= schema?.HasFlag(className, "physics_only_model") == true
                         || schema?.HasFlag(className, "render_as_world_but_physics_as_entity") == true;
            return new Model(nodeId, className, path, physicsOnly, WorldPhysics.Build(pieces, entityModel: true));
        }
    }

    /// <summary>The model file for one that holds only physics (RED2 and DATA alone when it has no shapes).</summary>
    public static byte[] Author(Model model, Func<uint, string?> surfaceName)
        => WorldPhysicsAuthor.Container(model.Physics.AllShapes.Any() ? WorldPhysicsTrees.Phys(model.Physics, partFlags: model.Physics.Meshes.Count > 0 ? 2u : 0u) : null,
            WorldPhysicsTrees.Red2(model.Physics, surfaceName, model.ClassName), WorldPhysicsTrees.ModelData(model.Path));

    // Meshes under a node, depth first in children order.
    private static IEnumerable<DmxBinary.Element> Meshes(DmxBinary.Element node)
    {
        foreach (var c in node.GetElements("children"))
        {
            if (c.Type == "CMapMesh")
                yield return c;
            else
                foreach (var m in Meshes(c))
                    yield return m;
        }
    }

    private static BrushHulls.PhysicsType PhysicsTypeOf(DmxBinary.Element mesh)
        => !mesh.Attributes.TryGetValue("physicsType", out var v) ? BrushHulls.PhysicsType.Default : v switch
        {
            int i => (BrushHulls.PhysicsType)i,
            string t => t switch
            {
                "none" => BrushHulls.PhysicsType.None,
                "convex_single" => BrushHulls.PhysicsType.ConvexSingle,
                "convex_multi" => BrushHulls.PhysicsType.ConvexMulti,
                "mesh" => BrushHulls.PhysicsType.Mesh,
                _ => BrushHulls.PhysicsType.Default,
            },
            _ => BrushHulls.PhysicsType.Default,
        };
}
