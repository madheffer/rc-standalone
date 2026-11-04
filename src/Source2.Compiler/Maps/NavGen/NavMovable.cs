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
/// Ported: func_movelinear (the 180fc6f40 search, flags 3, in the baked entity list's order) with
/// CreateMovableNavMesh whose first child is a mesh ("does not have any associated mesh" / "does not
/// have a mesh" otherwise, and it is skipped). Refused: func_mover, point_nav_movable_mesh and
/// prop_deformable's enable_map_generated_navmesh (the collector's other three parts), and a mover inside
/// an instance or prefab (its id path and matrix there are not read).
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

    /// <summary>180fb5ce0's movable meshes, in its order.</summary>
    public static List<NavMovable> Collect(DmxBinary.Document document, FgdSchema schema)
    {
        foreach (var other in new[] { "func_mover", "point_nav_movable_mesh" })
            if (NavGenMapInput.Found(document, other).Any(f => other != "func_mover" || Bool(f.Entity, "CreateMovableNavMesh")))
                throw new NotSupportedException($"a movable nav mesh from {other} is not ported");
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
            if (children.Count == 0 || children[0].Type != "CMapMesh")
                continue;
            var nodes = new HashSet<int> { entity.NodeId };
            var stack = new Stack<DmxBinary.Element>(children);
            while (stack.TryPop(out var n))
            {
                if (n.GetValue<int>("nodeID") is { } id)
                    nodes.Add(id);
                foreach (var c in n.GetElements("children"))
                    stack.Push(c);
            }
            found.Add(new NavMovable([entity.NodeId], EntityLumpAuthor.WorldToLocal(entity), false, nodes));
        }
        return found;
    }
}
