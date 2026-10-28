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
                              string? Layer = null)
    {
        /// <summary>The template node, the instances it was copied through (outermost first) and the prefabs it hangs
        /// under: a path's copy is placed node by node (<see cref="Maps.SettleWorld.PlacedPath"/>).</summary>
        public DmxBinary.Element? Node { get; init; }
        public IReadOnlyList<DmxBinary.Element> Through { get; init; } = [];
        public DmxBinary.Element[] Prefabs { get; init; } = [];
    }

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
    /// not, with its id, the instances it came through, outermost first, and
    /// the prefabs whose map holds them, outermost first.</param>
    /// <param name="prefabs">Walk prefabs' maps too (GROUND_TRUTH 46): each
    /// CMapPrefab collapses like an instance in the first round (a block of its
    /// map's node count plus one, writing nothing: its nodes ship by id path),
    /// then the instances inside its map, in the merged tree's order, write
    /// copies with plain ids. Off, a prefab's map is not looked into.</param>
    /// <param name="prefabBlock">Told of each prefab's block: its map's nodes take
    /// the block's start plus their slot (<see cref="PrefabSlots"/>).</param>
    public static (IReadOnlyList<Copy> Copies, IReadOnlySet<int> Templates) Expand(
        DmxBinary.Document document, IReadOnlyList<MapEntities.Entity> walked, int createdOnLoad = 0,
        Action<DmxBinary.Element, int, IReadOnlyList<DmxBinary.Element>, DmxBinary.Element[]>? placed = null, bool prefabs = false,
        Func<DmxBinary.Document, int>? createdOnLoadIn = null, Action<DmxBinary.Element, int>? prefabBlock = null)
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
        // Captured (capture_collapse.py): a prefab's block holds its map's copy
        // count plus one (s2c_prefabprobe3: 69), and also the nodes its map's
        // loader made (its smart props' locators): c2m2's environment prefab is
        // two nodes wider than its tree, atixref as a prefab one.
        //
        // The order is the bake's own (MapDoc_BakeInstances 180f60740): rounds
        // until none collapses, each walking the document tree and collapsing,
        // in that order, every instance-typed node it holds (CMapPrefab is one);
        // CMapInstance_Collapse (181017820) adds the copy as the LAST child of
        // the instance's parent (vf 0x1c8), so a later round finds an instance
        // of a copy, or of a prefab's map, where that copy now stands. An
        // instance inside a target group is never walked as itself (only its
        // group's copies are). This gives coaster's first round (prefabs 2, 3,
        // 5, 6, 7, instance 2118), atixref's nested copies past every top-level
        // block, and s2c_prefabprobe7 and 8's rounds (a prefab's prefab, a
        // prefab's instance beside a nested instance of the map's own).
        var due = tree.Instances.Where(i => !tree.InsideTarget.Contains(i.Node))
                      .ToDictionary(i => i.Node);
        var nested = tree.Instances.ToDictionary(i => i.Node);
        var templates = new HashSet<int>(hiddenTemplates);
        var prefabsDone = new Dictionary<DmxBinary.Element, int>();
        var root = BakeNode.Original(world, null, due);

        // Where a copy lands in the lump: after its parent's own subtree and after
        // the maps of the parent's prefabs collapsed before it, which the walk puts
        // at the parent's end in child order; a copy inside a copy lands with it.
        int EmitAt(BakeNode parent)
        {
            if (parent.EmitAt is { } inherited)
                return inherited;
            var done = prefabsDone.GetValueOrDefault(parent.Element);
            return tree.PrefabStarts.TryGetValue(parent.Element, out var starts) && done < starts.Count
                ? starts[done]
                : tree.SubtreeEnd.GetValueOrDefault(parent.Element, walked.Count);
        }

        void Collapse(BakeNode node)
        {
            var instance = node.Collapses!;
            node.Collapses = null;
            var parent = node.Parent!;
            var start = next + 1;
            // A prefab's lattice locators already have their slots in its copy count.
            next += tree.Nodes.GetValueOrDefault(instance.Target) + 1
                    + (instance.Prefab && createdOnLoadIn is not null && instance.Node.Get<DmxBinary.Document>(Maps.MapPrefabs.DocumentKey) is { } loaded
                        ? createdOnLoadIn(loaded) - loaded.Elements.Sum(ClonedLocators) : 0);
            if (instance.Prefab)
            {
                prefabBlock?.Invoke(instance.Node, start);
                parent.Kids.Add(BakeNode.Original(instance.Target, parent, due));
                prefabsDone[parent.Element] = prefabsDone.GetValueOrDefault(parent.Element) + 1;
                return;
            }
            IReadOnlyList<DmxBinary.Element> through = [.. node.Through, instance.Node];
            var copy = new BakeNode(instance.Target, parent)
            {
                Through = through, EmitAt = EmitAt(parent), Layer = node.Layer ?? LayerOf(instance.Node, tree),
            };
            // A copy's id is its slot within the block, counted over EVERY node of the
            // group and not only the entities, so a filtered prop_static still takes
            // its place.
            var slot = 0;
            Emit(instance.Target, instance.Prefabs, copy, byNode, nested, templates, placed, start, ref slot);
            parent.Kids.Add(copy);
        }

        while (true)
        {
            var round = new List<BakeNode>();
            root.Due(round);
            if (round.Count == 0)
                break;
            foreach (var node in round)
                Collapse(node);
        }

        // Lump order is the walk with each instance's copies inserted at their
        // EmitAt, copies sharing one in the final tree's order (a nested copy
        // inside its outer one, s2c_prefabprobe4). atixref's 17 instances under one group land at walk 672, where
        // that group ends, and its 126 root instances land after every walked
        // entity because the world's subtree ends there; s2c_prefabprobe8's root
        // instance's copies come before its prefab's entities.
        var copies = new List<Copy>();
        root.Written(copies);
        return ([.. copies.OrderBy(c => c.EmitAt)], templates);
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
        Dictionary<DmxBinary.Element, List<int>> PrefabStarts,
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
        var tree = new Tree([], [], [], [], [], []);
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
        // A prefab's map is walked where its parent's children end, as
        // MapEntities walks it (the collapse writes the copy there).
        var prefabs = new List<(DmxBinary.Element Child, DmxBinary.Element World, bool Hidden)>();
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
                // The prefab collapses in tree order (its place among the
                // instances), though its entities are walked later.
                tree.Instances.Add(new Instance(child, node, prefabWorld) { Prefab = true, Prefabs = within.Prefabs, Hidden = hidden });
                prefabs.Add((child, prefabWorld, hidden));
                continue;
            }
            Descend(child, tree, seen, ref produced, within);
            tree.SubtreeEnd[child] = produced;
        }
        foreach (var (child, prefabWorld, _) in prefabs)
        {
            if (!tree.PrefabStarts.TryGetValue(node, out var starts))
                tree.PrefabStarts[node] = starts = [];
            starts.Add(produced);
            if (MapEntities.ReadsAsEntity(prefabWorld))
                produced++;
            tree.Parents[prefabWorld] = child;
            Descend(prefabWorld, tree, seen, ref produced,
                    new Within([.. within!.Prefabs, child], child.Get<HashSet<int>>(Maps.MapPrefabs.HiddenKey) ?? [], within.Enclosed));
            tree.SubtreeEnd[prefabWorld] = produced;
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
        PrefabSlots(world, out var total);
        return total;
    }

    /// <summary>
    /// Each node's slot in its prefab's block, in the copy's preorder (see
    /// <see cref="PrefabCopyCount"/>): a node's id is the block's start plus its
    /// slot. A target group copied early for an instance takes the slots after
    /// the instance, and its nodes keep the later ones the walk gives them.
    /// </summary>
    internal static Dictionary<DmxBinary.Element, int> PrefabSlots(DmxBinary.Element world, out int total)
    {
        var copied = new HashSet<DmxBinary.Element>();
        var slots = new Dictionary<DmxBinary.Element, int>(ReferenceEqualityComparer.Instance);
        var next = 0;
        void Walk(DmxBinary.Element node)
        {
            foreach (var child in node.GetElements("children"))
            {
                slots.TryAdd(child, next);
                next++;
                copied.Add(child);
                if (child.Type is "CMapInstance" && child.Get<DmxBinary.Element>("target") is { } target && copied.Add(target))
                    next += 1 + Count(target);
                Walk(child);
                // A lattice's control point locators are its last children in the
                // loaded map, so they take the slots after its own subtree
                // (s2c_deformplace: the prefab's nodes after its lattice are 24 on).
                next += ClonedLocators(child);
            }
        }
        Walk(world);
        total = next;
        return slots;
    }

    private static int Count(DmxBinary.Element group)
    {
        var total = 0;
        foreach (var child in group.GetElements("children"))
            total += 1 + Count(child) + ClonedLocators(child);
        return total;
    }

    /// <summary>
    /// The CMapLatticeControlPointLocator children a lattice holds in the loaded
    /// document but not in the .vmap (none is ever saved, Valve's zoo maps
    /// included). The loader appends one per control point after the lattice's
    /// own children (1811758d0 through 180f5f230), and an instance's clone
    /// (180f86a90) copies them as it does any child (their vf 0x188 is
    /// 1800de990, false), each from the node factory and so with an id. The
    /// lattice's CopyFrom (18116f970) makes none: 1811758d0 needs the document,
    /// which vf 0x4f8 (180f78950) finds through the parent, and a fresh clone
    /// has none yet.
    /// </summary>
    /// <para>A CMapNavLink likewise holds two CMapNavLinkLocators the loader makes (181064ab0: locator 0 at the
    /// link, locator 1 at the end, each from the node factory and added as its last child), which a clone copies:
    /// s2c_navlink3's load gives each link the two ids after its own and its copies take three slots
    /// (tools/re/capture_entitylist.py).</para>
    internal static int ClonedLocators(DmxBinary.Element node)
        => Maps.LatticeDeformer.ControlPointCount(node) + (node.Type == "CMapNavLink" ? 2 : 0);

    /// <summary>
    /// A node of the tree the bake walks: an original (one of the map's, or of a
    /// collapsed prefab's map) or a copy an instance made. <see cref="Collapses"/>
    /// is the instance or prefab it still has to collapse.
    /// </summary>
    private sealed class BakeNode(DmxBinary.Element element, BakeNode? parent)
    {
        public DmxBinary.Element Element { get; } = element;
        public BakeNode? Parent { get; } = parent;
        public List<BakeNode> Kids { get; } = [];
        public Instance? Collapses { get; set; }
        /// <summary>The instances a copy came through, outermost first; empty for an original.</summary>
        public IReadOnlyList<DmxBinary.Element> Through { get; init; } = [];
        /// <summary>A copy's lump position, which everything inside it shares; null for an original.</summary>
        public int? EmitAt { get; init; }
        public string? Layer { get; init; }
        /// <summary>The copy of an entity this node writes, if it is one.</summary>
        public Copy? Writes { get; set; }

        /// <summary>An original node and its subtree, a prefab's map left out until it collapses.</summary>
        public static BakeNode Original(DmxBinary.Element element, BakeNode? parent, Dictionary<DmxBinary.Element, Instance> due)
        {
            var node = new BakeNode(element, parent) { Collapses = due.GetValueOrDefault(element) };
            if (node.Collapses is not { Prefab: true })
                foreach (var child in element.GetElements("children"))
                    node.Kids.Add(Original(child, node, due));
            return node;
        }

        /// <summary>The copies written, in preorder: the order the final tree is walked in.</summary>
        public void Written(List<Copy> into)
        {
            if (Writes is not null)
                into.Add(Writes);
            foreach (var kid in Kids)
                kid.Written(into);
        }

        /// <summary>The nodes still to collapse, in preorder.</summary>
        public void Due(List<BakeNode> into)
        {
            if (Collapses is not null)
                into.Add(this);
            foreach (var kid in Kids)
                kid.Due(into);
        }
    }

    /// <summary>
    /// Write one copy's entities and build its subtree under <paramref name="copy"/>:
    /// an instance in it becomes a node due in a later round.
    /// </summary>
    private static void Emit(
        DmxBinary.Element group, DmxBinary.Element[] prefabs, BakeNode copy, Dictionary<string, int> byNode,
        Dictionary<DmxBinary.Element, Instance> instances, HashSet<int> templates,
        Action<DmxBinary.Element, int, IReadOnlyList<DmxBinary.Element>, DmxBinary.Element[]>? placed,
        int start, ref int slot)
    {
        foreach (var child in group.GetElements("children"))
        {
            var index = slot++;
            var node = new BakeNode(child, copy) { Through = copy.Through, EmitAt = copy.EmitAt, Layer = copy.Layer };
            copy.Kids.Add(node);
            if (child.Type is "CMapInstance")
            {
                node.Collapses = instances.GetValueOrDefault(child);
            }
            else
            {
                placed?.Invoke(child, start + index, copy.Through, prefabs);
                if (MapEntities.CarriesGameKeys(child)
                    && byNode.TryGetValue(IdPath(child, prefabs), out var template))
                {
                    templates.Add(template);
                    // The copy's placement is the collapse's own (TransformBy through
                    // each instance's baked matrix, SettleWorld.BakedPlacement): on
                    // atixref it rebuilds all 199 instanced lights bit for bit, where
                    // adding Euler angles and rotating by quaternion did not.
                    // Inside a prefab's map the prefabs move first, then the instances collapse.
                    var (origin, angles) = prefabs.Length == 0
                        ? Maps.SettleWorld.BakedPlacement(child, copy.Through)
                        : Maps.SettleWorld.NestedPlacement(child.GetValue<Vector3>("origin") ?? Vector3.Zero,
                                                           child.GetValue<Vector3>("angles") ?? Vector3.Zero, copy.Through, prefabs);
                    node.Writes = new Copy(copy.EmitAt!.Value, template, start + index, origin, angles, copy.Layer)
                    {
                        Node = child, Through = copy.Through, Prefabs = prefabs,
                    };
                }
            }
            Emit(child, prefabs, node, byNode, instances, templates, placed, start, ref slot);
            slot += ClonedLocators(child);
        }
    }
}
