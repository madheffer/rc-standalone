using System.Numerics;

namespace Source2.Compiler;

/// <summary>
/// The instances a map places, and what each one puts in the compiled lump.
///
/// <para>A <c>CMapInstance</c> has no children of its own. It carries a
/// <c>target</c> pointing at a <c>CMapGroup</c> elsewhere in the same tree, and
/// the compile writes one transformed COPY of that group's entities per placement
/// while shipping none of the group itself. atixref places 144 instances over 15
/// distinct groups, which is 199 of its 821 lump entities.</para>
/// </summary>
public static class MapInstances
{
    /// <summary>One copy of one template entity.</summary>
    /// <param name="EmitAt">The walk index the copy is written at.</param>
    /// <param name="Template">Walk index of the entity copied, which the copy
    /// carries as its compile_source_id.</param>
    /// <param name="NodeId">The copy's own hammerUniqueId.</param>
    /// <param name="Origin">World position after the placement's transform.</param>
    /// <param name="Angles">Rotation after the placement's transform.</param>
    /// <param name="Layer">The world layer the placing instance sits in, whose lump
    /// the copy ships in; null for the world's own.</param>
    public sealed record Copy(int EmitAt, int Template, int NodeId, Vector3 Origin, Vector3 Angles,
                              string? Layer = null);

    /// <summary>
    /// Every copy the map's instances produce, in lump order, and the walk indices
    /// of the entities that are TEMPLATES and so ship as copies instead.
    /// </summary>
    /// <param name="document">The map source.</param>
    /// <param name="walked">The walked entities.</param>
    /// <param name="createdOnLoad">Nodes the loader creates before the bake, which
    /// take ids first: one per locator a smart prop's definition creates (see
    /// <see cref="SmartProps"/>).</param>
    /// <param name="placed">Told of every node a placement writes, entity or
    /// not, with its id and the instances it came through, outermost first.</param>
    /// <param name="prefabs">Walk prefabs' maps too (GROUND_TRUTH 46): each
    /// CMapPrefab collapses like an instance in the first round (a block of its
    /// map's node count plus one, writing nothing: its nodes ship by id path),
    /// then the instances inside its map, in the merged tree's order, write
    /// copies with plain ids. Off, a prefab's map is not looked into.</param>
    public static (IReadOnlyList<Copy> Copies, IReadOnlySet<int> Templates) Expand(
        DmxBinary.Document document, IReadOnlyList<MapEntities.Entity> walked, int createdOnLoad = 0,
        Action<DmxBinary.Element, int, IReadOnlyList<DmxBinary.Element>>? placed = null, bool prefabs = false,
        Func<DmxBinary.Document, int>? createdOnLoadIn = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(walked);

        var world = document.OfType("CMapWorld").FirstOrDefault();
        if (world is null)
            return ([], new HashSet<int>());

        // Walked entities by id path: a prefab's map numbers its nodes on its own.
        var byNode = new Dictionary<string, int>();
        for (var i = 0; i < walked.Count; i++)
            byNode.TryAdd(walked[i].IdPath, i);

        var tree = Survey(world, walked, prefabs ? MapEntities.HiddenNodes(document) : null);
        // An instance the map hides places nothing and takes no block of ids:
        // Mako hides four, and its later blocks start exactly that much lower.
        // Its group is still a template and still does not ship in its own right.
        var hidden = MapEntities.HiddenNodes(document);
        // A prefab the visibility manager hides still compiles its contents (c2m3_coaster_d_d's hidden prefab 2: Valve's lump and models carry 2:123 and the rest).
        bool Hidden(Instance i) => !i.Prefab && (i.Prefabs.Length == 0 ? hidden.Contains(i.Node.GetValue<int>("nodeID") ?? -1) : i.Hidden);
        var hiddenTemplates = new HashSet<int>();
        foreach (var instance in tree.Instances.Where(Hidden))
            MarkTemplates(instance.Target, instance.Prefabs, byNode, hiddenTemplates);
        tree.Instances.RemoveAll(Hidden);
        if (tree.Instances.Count == 0)
            return ([], hiddenTemplates);

        // The bake (FUN_180f60740) collapses each instance in tree order, one
        // round at a time; each collapse takes a block of ids as wide as its group
        // plus one, and the copy's root takes the first. Ids start past the map's
        // highest node id (over EVERY element: atixref's entities stop at 7244 and
        // its nodes at 7247) and past the nodes the loader made: atixref's radiator
        // smart prop makes a locator at 7248, so its first collapse returns 7249.
        // An instance inside a group that is itself a target is not collapsed in
        // the first round; it is reached through its group's copies, in a later
        // one (captured on atixref, c2m2's environment prefab and Mako).
        var next = document.Elements.Max(e => e.GetValue<int>("nodeID") ?? 0) + 1 + createdOnLoad;
        var block = new Dictionary<DmxBinary.Element, int>();
        var first = tree.Instances.Where(i => i.Prefabs.Length == 0 && !tree.InsideTarget.Contains(i.Node)).ToList();
        var inner = tree.Instances.Where(i => i.Prefabs.Length > 0 && !tree.InsideTarget.Contains(i.Node)).ToList();
        // Captured (capture_collapse.py): the prefab first, its root the next id,
        // then its map's instances in tree order from the end of its block
        // (s2c_prefabprobe3: 69); an instance inside a target group of the
        // prefab's map waits for its group's copies, past every block of the
        // first round, as in a map of its own (s2c_prefabprobe4: atixref's four
        // nested copies). Not captured: a prefab beside instances of the map's
        // own, and a prefab inside a prefab.
        if (first.Any(i => i.Prefab) && first.Any(i => !i.Prefab))
            throw new NotSupportedException("a map with both prefabs and instances of its own: the collapse order is not captured");
        if (tree.Instances.Any(i => i.Prefab && i.Prefabs.Length > 0))
            throw new NotSupportedException("a prefab inside a prefab: the collapse order is not captured");
        // A prefab's block also holds the nodes its map's loader made (its smart
        // props' locators): c2m2's environment prefab is two nodes wider than its
        // tree, atixref as a prefab one.
        foreach (var instance in first)
        {
            block[instance.Node] = next + 1;
            next += tree.Nodes.GetValueOrDefault(instance.Target) + 1
                    + (instance.Prefab && createdOnLoadIn is not null && instance.Node.Get<DmxBinary.Document>(Maps.MapPrefabs.DocumentKey) is { } loaded
                        ? createdOnLoadIn(loaded) : 0);
        }
        if (inner.Count > 0 && tree.Instances.Any(i => i.Prefabs.Length == 0 && tree.InsideTarget.Contains(i.Node)))
            throw new NotSupportedException("prefab instances beside nested instances of the map's own: the round order is not captured");
        foreach (var instance in inner)
        {
            block[instance.Node] = next + 1;
            next += tree.Nodes.GetValueOrDefault(instance.Target) + 1;
        }

        var copies = new List<Copy>();
        var templates = new HashSet<int>(hiddenTemplates);
        foreach (var instance in first.Where(i => !i.Prefab).Concat(inner))
            Place(instance, Transform.Identity, [], tree, byNode, block, ref next, copies, templates, placed,
                  tree.SubtreeEnd.GetValueOrDefault(instance.Parent, walked.Count), LayerOf(instance.Node, tree));

        // Lump order is the walk with each instance's copies inserted where its
        // PARENT's subtree finishes. atixref's 17 instances under one group land at
        // walk 672, where that group ends, and its 126 root instances land after
        // every walked entity because the world's subtree ends there.
        return ([.. copies.OrderBy(c => c.EmitAt)], templates);
    }

    /// <summary>A placement's position and rotation, composed as instances nest.</summary>
    private readonly record struct Transform(Vector3 Origin, Vector3 Angles)
    {
        public static Transform Identity => new(Vector3.Zero, Vector3.Zero);

        public Transform Then(Vector3 origin, Vector3 angles)
            => new(Origin + Vector3.Transform(origin, Rotation(Angles)), Angles + angles);
    }

    /// <summary>A collapse: an instance, or with <see cref="Prefab"/> a CMapPrefab whose target is its map's world.
    /// <see cref="Prefabs"/> are the prefab nodes it lies under, outermost first; <see cref="Hidden"/> is
    /// whether its own map hides it (or a hidden prefab encloses it).</summary>
    private sealed record Instance(DmxBinary.Element Node, DmxBinary.Element Parent, DmxBinary.Element Target)
    {
        public bool Prefab { get; init; }
        public DmxBinary.Element[] Prefabs { get; init; } = [];
        public bool Hidden { get; init; }
    }

    private sealed record Tree(
        List<Instance> Instances,
        Dictionary<DmxBinary.Element, int> SubtreeEnd,
        Dictionary<DmxBinary.Element, int> Nodes,
        Dictionary<DmxBinary.Element, DmxBinary.Element> Parents,
        HashSet<DmxBinary.Element> InsideTarget);

    /// <summary>
    /// One walk for everything the expansion needs: the instances in tree order,
    /// how many entities each subtree has produced by the time it ends, each target
    /// group's node count, and which instances sit inside a target group and so are
    /// only ever reached through it.
    /// </summary>
    private static Tree Survey(DmxBinary.Element world, IReadOnlyList<MapEntities.Entity> walked, HashSet<int>? prefabsHidden)
    {
        var tree = new Tree([], [], [], [], []);
        var seen = new HashSet<DmxBinary.Element>();
        var produced = walked.Count > 0 && walked[0].IsWorld ? 1 : 0;
        Descend(world, tree, seen, ref produced, prefabsHidden is null ? null : new Within([], prefabsHidden, false));
        tree.SubtreeEnd[world] = produced;

        var targets = tree.Instances.Where(i => !i.Prefab).Select(i => i.Target).ToHashSet();
        foreach (var target in targets)
            tree.Nodes[target] = Count(target);
        foreach (var prefab in tree.Instances.Where(i => i.Prefab))
            tree.Nodes[prefab.Target] = PrefabCopyCount(prefab.Target);
        foreach (var instance in tree.Instances.Where(i => !i.Prefab))
            for (var at = instance.Parent; at is not null && at.Type is not "CMapPrefab"; at = tree.Parents.GetValueOrDefault(at))
                if (targets.Contains(at))
                {
                    tree.InsideTarget.Add(instance.Node);
                    break;
                }
        return tree;
    }

    /// <summary>The prefabs a walk is inside, the hidden node ids of the map it walks, and whether a hidden node encloses it.</summary>
    private sealed record Within(DmxBinary.Element[] Prefabs, HashSet<int> Hidden, bool Enclosed);

    // The walk MapEntities makes, counting the entities it produces; with
    // `within`, a prefab's map is walked where the prefab stands, after the
    // entity its world reads as.
    private static void Descend(
        DmxBinary.Element node, Tree tree, HashSet<DmxBinary.Element> seen, ref int produced, Within? within)
    {
        foreach (var child in node.GetElements("children"))
        {
            if (!seen.Add(child))
                continue;
            tree.Parents[child] = node;
            var id = child.GetValue<int>("nodeID") ?? int.MinValue;
            var hidden = within is not null && (within.Enclosed || within.Hidden.Contains(id));
            if (child.Type is "CMapInstance" && child.Get<DmxBinary.Element>("target") is { } target)
                tree.Instances.Add(new Instance(child, node, target) { Prefabs = within?.Prefabs ?? [], Hidden = hidden });
            else if (MapEntities.CarriesGameKeys(child))
                produced++;
            if (within is not null && child.Type is "CMapPrefab" && child.Get<DmxBinary.Element>(Maps.MapPrefabs.WorldKey) is { } prefabWorld)
            {
                tree.Instances.Add(new Instance(child, node, prefabWorld) { Prefab = true, Prefabs = within.Prefabs, Hidden = hidden });
                if (MapEntities.ReadsAsEntity(prefabWorld))
                    produced++;
                tree.Parents[prefabWorld] = child;
                Descend(prefabWorld, tree, seen, ref produced,
                        new Within([.. within.Prefabs, child], child.Get<HashSet<int>>(Maps.MapPrefabs.HiddenKey) ?? [], within.Enclosed));
                tree.SubtreeEnd[prefabWorld] = produced;
                tree.SubtreeEnd[child] = produced;
                continue;
            }
            Descend(child, tree, seen, ref produced, within);
            tree.SubtreeEnd[child] = produced;
        }
    }

    // A node's walked id path: the prefab ids it lies under, then its own.
    private static string IdPath(DmxBinary.Element node, DmxBinary.Element[] prefabs)
    {
        var id = (node.GetValue<int>("nodeID") ?? -1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return prefabs.Length == 0 ? id
            : string.Join(':', prefabs.Select(p => (p.GetValue<int>("nodeID") ?? -1).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(id));
    }

    /// <summary>The world layer a node sits in, from its nearest CMapWorldLayer
    /// ancestor; null for the world's own.</summary>
    private static string? LayerOf(DmxBinary.Element node, Tree tree)
    {
        for (var at = tree.Parents.GetValueOrDefault(node); at is not null; at = tree.Parents.GetValueOrDefault(at))
            if (at.Type is "CMapWorldLayer")
                return at.Get<string>("worldLayerName");
        return null;
    }

    /// <summary>Every entity a group holds, at any depth, as a template.</summary>
    private static void MarkTemplates(DmxBinary.Element group, DmxBinary.Element[] prefabs, Dictionary<string, int> byNode, HashSet<int> templates)
    {
        foreach (var child in group.GetElements("children"))
        {
            if (MapEntities.CarriesGameKeys(child)
                && byNode.TryGetValue(IdPath(child, prefabs), out var index))
                templates.Add(index);
            MarkTemplates(child, prefabs, byNode, templates);
        }
    }

    /// <summary>
    /// The nodes the copy of a prefab's map makes, in preorder: every node, and
    /// for an instance whose target group comes later in the walk (not copied
    /// yet) that group's subtree once more, copied for the instance. Captured on
    /// s2c_prefabprobe3: c2m2's environment prefab's instance 4928 precedes its
    /// target 4888 (a group of one entity), and every slot from there on is two
    /// past the tree's (capture_collapse.py).
    /// </summary>
    private static int PrefabCopyCount(DmxBinary.Element world)
    {
        var copied = new HashSet<DmxBinary.Element>();
        var total = 0;
        void Walk(DmxBinary.Element node)
        {
            foreach (var child in node.GetElements("children"))
            {
                total++;
                copied.Add(child);
                if (child.Type is "CMapInstance" && child.Get<DmxBinary.Element>("target") is { } target && copied.Add(target))
                    total += 1 + Count(target);
                Walk(child);
            }
        }
        Walk(world);
        return total;
    }

    private static int Count(DmxBinary.Element group)
    {
        var total = 0;
        foreach (var child in group.GetElements("children"))
            total += 1 + Count(child);
        return total;
    }

    /// <summary>
    /// Write one placement's copies, then recurse into any instance the group
    /// holds. A nested placement's ids continue past every top-level block, which
    /// is where atixref's four copies of its one nested group get 7882 to 7888.
    /// </summary>
    private static void Place(
        Instance instance, Transform outer, IReadOnlyList<DmxBinary.Element> path, Tree tree, Dictionary<string, int> byNode,
        Dictionary<DmxBinary.Element, int> block, ref int next,
        List<Copy> copies, HashSet<int> templates, Action<DmxBinary.Element, int, IReadOnlyList<DmxBinary.Element>>? placed,
        int emitAt, string? layer)
    {
        var at = outer.Then(
            instance.Node.GetValue<Vector3>("origin") ?? Vector3.Zero,
            instance.Node.GetValue<Vector3>("angles") ?? Vector3.Zero);

        // A copy's id is its slot within the block, counted over EVERY node of the
        // group and not only the entities, so a filtered prop_static still takes
        // its place.
        var slot = 0;
        var nested = new List<DmxBinary.Element>();
        IReadOnlyList<DmxBinary.Element> through = [.. path, instance.Node];
        Emit(instance.Target, instance.Prefabs, through, byNode, copies, templates, placed, emitAt,
             block.GetValueOrDefault(instance.Node), ref slot, nested, layer);

        foreach (var inner in nested)
            if (tree.Instances.FirstOrDefault(i => i.Node == inner) is { } found)
            {
                block[found.Node] = next + 1;
                next += tree.Nodes.GetValueOrDefault(found.Target) + 1;
                Place(found, at, through, tree, byNode, block, ref next, copies, templates, placed, emitAt, layer);
            }
    }

    private static void Emit(
        DmxBinary.Element group, DmxBinary.Element[] prefabs, IReadOnlyList<DmxBinary.Element> through, Dictionary<string, int> byNode,
        List<Copy> copies, HashSet<int> templates, Action<DmxBinary.Element, int, IReadOnlyList<DmxBinary.Element>>? placed,
        int emitAt, int start, ref int slot,
        List<DmxBinary.Element> nested, string? layer)
    {
        foreach (var child in group.GetElements("children"))
        {
            var index = slot++;
            if (child.Type is not "CMapInstance")
                placed?.Invoke(child, start + index, through);
            if (child.Type is "CMapInstance")
            {
                nested.Add(child);
            }
            else if (MapEntities.CarriesGameKeys(child)
                     && byNode.TryGetValue(IdPath(child, prefabs), out var template))
            {
                templates.Add(template);
                // The copy's placement is the collapse's own (TransformBy through
                // each instance's baked matrix, SettleWorld.BakedPlacement): on
                // atixref it rebuilds all 199 instanced lights bit for bit, where
                // adding Euler angles and rotating by quaternion did not.
                // Inside a prefab's map the prefabs move first, then the instances collapse.
                var (origin, angles) = prefabs.Length == 0
                    ? Maps.SettleWorld.BakedPlacement(child, through)
                    : Maps.SettleWorld.NestedPlacement(child.GetValue<Vector3>("origin") ?? Vector3.Zero,
                                                       child.GetValue<Vector3>("angles") ?? Vector3.Zero, through, prefabs);
                copies.Add(new Copy(emitAt, template, start + index, origin, angles, layer));
            }
            Emit(child, prefabs, through, byNode, copies, templates, placed, emitAt, start, ref slot, nested, layer);
        }
    }

    /// <summary>Source angles are pitch, yaw and roll about Y, Z and X, yaw
    /// first.</summary>
    private static Quaternion Rotation(Vector3 angles)
        => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Radians(angles.Y))
         * Quaternion.CreateFromAxisAngle(Vector3.UnitY, Radians(angles.X))
         * Quaternion.CreateFromAxisAngle(Vector3.UnitX, Radians(angles.Z));

    private static float Radians(float degrees) => degrees * MathF.PI / 180f;

    /// <summary>
    /// Angles compose by addition wrapped into [0, 360). One atixref template at
    /// yaw 270.00006 ships at 180.00006, 0, 270.00006 and 90.00005 under
    /// placements at yaw 270, 90, 0 and 180.
    /// </summary>
    private static Vector3 Wrap(Vector3 angles)
        => new(Wrap(angles.X), Wrap(angles.Y), Wrap(angles.Z));

    private static float Wrap(float degrees)
    {
        var wrapped = degrees % 360f;
        return wrapped < 0 ? wrapped + 360f : wrapped;
    }
}
