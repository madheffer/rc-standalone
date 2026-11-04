using Source2.Compiler.Io;

namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// A movable nav mesh as rc 180fb5ce0 collects it (from 180fb5020, while the settle world is built): the
/// entity's id path, its world-to-local matrix (vf 0xa8, 180fdee80) and the gravity bit (entry +0x88,
/// point_nav_movable_mesh's GravityFollowsRotation), and the nodes whose bodies go to it rather than the
/// static world (entry +0x70). navsystem generates each on its own (1800250e0) and tags its polygons with
/// <see cref="Tag"/> (180020cc0); the .nav lists every collected one, polygons or not (1818e9510).
/// </summary>
/// <remarks>
/// Ported, each from the 180fc6f40 search (flags 3, the baked entity list's order): func_movelinear with
/// CreateMovableNavMesh whose first child is a mesh (else skipped with a warning); func_mover with
/// CreateMovableNavMesh and children: itself and the entities parented to it (180fb6e00, prop_deformable
/// left out); point_nav_movable_mesh: the one entity ref_ent names (mode 1), itself when navgen_on_ref_ent,
/// and the entities parented to it, with its matrix and the point's id path and GravityFollowsRotation.
/// Refused: prop_deformable's enable_map_generated_navmesh, anything inside an instance or prefab (its id
/// path and matrix there are not read), a repeated name in a parent chain.
/// </remarks>
public sealed record NavMovable(int[] IdPath, float[] Matrix, bool GravityFollowsRotation, IReadOnlySet<int> Nodes)
{
    /// <summary>The id path as the .nav names it (180106ee0: "%d", then ":%d" for each further id).</summary>
    public string Name => string.Join(':', IdPath.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Whether a settle world body is one of this mesh's (a body of one of its nodes).</summary>
    public bool Owns(SettleWorld.BodyBuild body) => body.IdPath is null && Nodes.Contains(body.NodeId);

    /// <summary>
    /// The polygon tag (180020cc0, rc's 180105f50): MurmurHash2 over the id path's inline ints, seed
    /// 0x3501a674 (an out-of-line tail, a path past five ids, would be hashed first and seed it).
    /// </summary>
    public uint Tag
    {
        get
        {
            const uint m = 0x5bd1e995;
            var h = 0x3501a674u ^ (uint)(IdPath.Length * 4);
            foreach (var id in IdPath)
            {
                var k = (uint)id * m;
                k ^= k >> 24;
                h = (h * m) ^ (k * m);
            }
            h = (h ^ (h >> 13)) * m;
            return h ^ (h >> 15);
        }
    }

    private static bool Bool(MapEntities.Entity e, string key)
    {
        var v = e.Keys.FirstOrDefault(k => k.Key.EqualsAscii(key)).Value;
        return v is not null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                                 || (int.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var i) && i != 0));
    }

    /// <summary>180fb5ce0's movable meshes, in its order: func_movelinear, func_mover, point_nav_movable_mesh.</summary>
    public static List<NavMovable> Collect(DmxBinary.Document document, FgdSchema schema)
    {
        if (NavGenMapInput.Found(document, "prop_deformable").Any(f => Bool(f.Entity, "enable_map_generated_navmesh")))
            throw new NotSupportedException("a movable nav mesh from prop_deformable is not ported");
        var found = new List<NavMovable>();
        foreach (var (item, entity, copied) in NavGenMapInput.Found(document, "func_movelinear"))
        {
            if (!Bool(entity, "CreateMovableNavMesh"))
                continue;
            if (copied)
                throw new NotSupportedException("a movable nav mesh inside an instance or prefab is not ported");
            var children = item.Node.GetElements("children").ToList();
            // "does not have any associated mesh" / "does not have a mesh": skipped.
            if (children.Count == 0 || children[0].Type != "CMapMesh")
                continue;
            var nodes = new HashSet<int>();
            AddTree(item.Node, nodes);
            found.Add(new NavMovable([entity.NodeId], EntityLumpAuthor.WorldToLocal(entity), false, nodes));
        }
        foreach (var (item, entity, copied) in NavGenMapInput.Found(document, "func_mover"))
        {
            if (!Bool(entity, "CreateMovableNavMesh"))
                continue;
            if (copied)
                throw new NotSupportedException("a movable nav mesh inside an instance or prefab is not ported");
            // "func_mover entity does not have any children": skipped.
            if (!item.Node.GetElements("children").Any())
                continue;
            var nodes = new HashSet<int>();
            AddTree(item.Node, nodes);
            foreach (var child in ParentedTo(document, entity))
                if (!Tier0Strings.EqualsIgnoreCase(child.Entity.ClassName, "prop_deformable"))
                    AddTree(child.Item.Node, nodes);
            found.Add(new NavMovable([entity.NodeId], EntityLumpAuthor.WorldToLocal(entity), false, nodes));
        }
        foreach (var (item, entity, copied) in NavGenMapInput.Found(document, "point_nav_movable_mesh"))
        {
            var refName = entity.Keys.FirstOrDefault(k => k.Key.EqualsAscii("ref_ent")).Value;
            if (string.IsNullOrEmpty(refName))
                continue;
            // s2c_navmovable2: Valve's search found no entity for ref_ent "pnm_ref", a visible func_brush with that
            // targetname ("0 reference entities found"); why is not read, so the class stays refused.
            throw new NotSupportedException("a movable nav mesh from point_nav_movable_mesh is not ported (its ref_ent search is not understood)");
#pragma warning disable CS0162
            if (copied)
                throw new NotSupportedException("a movable nav mesh inside an instance or prefab is not ported");
            var refs = NavGenMapInput.FoundByName(document, refName).ToList();
            // "must point to exactly one reference entity": skipped.
            if (refs.Count != 1)
                continue;
            var target = refs[0];
            if (target.Copied)
                throw new NotSupportedException("a point_nav_movable_mesh reference inside an instance or prefab is not ported");
            var list = new List<(MapEntityList.Item Item, MapEntities.Entity Entity)>();
            if (Bool(entity, "navgen_on_ref_ent"))
                list.Add((target.Item, target.Entity));
            list.AddRange(ParentedTo(document, target.Entity));
            // "reference entity does not have any children and 'generate on reference entity' is disabled": skipped.
            if (list.Count == 0)
                continue;
            var nodes = new HashSet<int>();
            foreach (var (node, e) in list)
                if (!Tier0Strings.EqualsIgnoreCase(e.ClassName, "prop_deformable"))
                    AddTree(node.Node, nodes);
            found.Add(new NavMovable([entity.NodeId], EntityLumpAuthor.WorldToLocal(target.Entity), Bool(entity, "GravityFollowsRotation"), nodes));
#pragma warning restore CS0162
        }
        return found;
    }

    private static void AddTree(DmxBinary.Element root, HashSet<int> nodes)
    {
        var stack = new Stack<DmxBinary.Element>([root]);
        while (stack.TryPop(out var n))
        {
            if (n.GetValue<int>("nodeID") is { } id)
                nodes.Add(id);
            foreach (var c in n.GetElements("children"))
                stack.Push(c);
        }
    }

    /// <summary>
    /// 180fb6e00: the visible entities whose parentname (compared case-sensitively) names <paramref name="root"/>'s
    /// targetname, and theirs in turn (180fb77a0's tree; a cycle is fatal in Valve's). Sub-maps' entities are
    /// searched too (180fc98a0): an instance or prefab entity parented into the chain is refused.
    /// </summary>
    private static List<(MapEntityList.Item Item, MapEntities.Entity Entity)> ParentedTo(DmxBinary.Document document, MapEntities.Entity root)
    {
        static string? Key(MapEntities.Entity e, string key) => e.Keys.FirstOrDefault(k => k.Key.EqualsAscii(key)).Value;
        var all = NavGenMapInput.Found(document, _ => true, "entity").ToList();
        var result = new List<(MapEntityList.Item, MapEntities.Entity)>();
        var names = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (Key(root, "targetname") is { Length: > 0 } rootName)
        {
            names.Enqueue(rootName);
            seen.Add(rootName);
        }
        while (names.TryDequeue(out var name))
            foreach (var (item, e, copied) in all)
            {
                if (Key(e, "parentname") is not { Length: > 0 } parent || !string.Equals(parent, name, StringComparison.Ordinal))
                    continue;
                if (copied)
                    throw new NotSupportedException("an entity parented to a movable nav mesh inside an instance or prefab is not ported");
                result.Add((item, e));
                if (Key(e, "targetname") is { Length: > 0 } child)
                {
                    if (!seen.Add(child))
                        throw new NotSupportedException($"a repeated targetname '{child}' in a movable nav mesh's parent chain is not measured (a cycle is fatal in Valve's compile)");
                    names.Enqueue(child);
                }
            }
        return result;
    }
}
