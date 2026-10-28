using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The world's entity list (CMapWorld +0x1828 count, +0x1830 array) as the compile's entity searches see it
/// (FUN_180fc7220 walks it in stored order) once the instance bake is done.
///
/// <para>From resourcecompiler 0923's code and a capture of every list edit on s2c_navlink3
/// (tools/re/capture_entitylist.py, GROUND_TRUTH 177):</para>
/// <list type="bullet">
/// <item>The load adds each node as it joins the document (OnNodeAdded 180fca010): the world first (180fca3d0), then
/// the tree in preorder. Only CMapEntityIONode nodes are listed: every CMapGameDataNode (the classes with
/// entity_properties, <see cref="MapEntities.CarriesGameKeys"/>), CMapInstance and CMapPrefab (the IsA chains);
/// groups, meshes and the navlink locators are not. A prefab's map fills its own world's list.</item>
/// <item>The bake (MapDoc_BakeInstances 180f60740) collapses in rounds, each walking the tree in preorder
/// (<see cref="MapInstances"/>): an instance's copy goes in as the last child of the instance's parent and its listed
/// nodes are appended in preorder; when no other instance still names the target (180f684c0, 180f8e9e0) the target's
/// subtree leaves, in postorder; then the instance leaves. A prefab appends its map's listed nodes in preorder, then
/// leaves.</item>
/// <item>A node leaves by FastRemove (180fcb150): the last entry moves into its slot.</item>
/// <item>A round's instances (prefabs included) are collected once before any collapses (180f86e70, flags 0xd),
/// so a prefab map's instances and prefabs, moved in this round, collapse in the next, as the root's do
/// (GROUND_TRUTH 179). A prefab map's instance that joins the world ahead of its target names a clone of it
/// (181019590, GROUND_TRUTH 180).</item>
/// <item>A hidden instance (visibility state off) is deleted, not collapsed: it leaves, then its target if nothing else
/// names it (GROUND_TRUTH 181).</item>
/// </list>
/// Exact against Valve's list on s2c_prefabprobe3, 4, 5, 7 and 9, s2c_deformpath3 and 4, ze_ffvii_mako_reactor_v6_p and
/// c2m3_coaster_d_d (MapEntityListTests).
/// Refused until measured: a hidden prefab, a copied prefab whose
/// map has instances (Valve crashes), a prefab inside a target, a target that loses its last referrer by being
/// deleted with another (the cascade's order), and a prefab map's instance ahead of a target with fewer than two
/// other referrers (moved, not cloned).
/// </summary>
public static class MapEntityList
{
    /// <summary>One listed node: its source element, the instances it was copied through (outermost first) and the
    /// prefabs whose maps hold it (outermost first), as <see cref="SettleWorld.Placed"/> takes them.</summary>
    public sealed record Item(DmxBinary.Element Node, IReadOnlyList<DmxBinary.Element> Through, IReadOnlyList<DmxBinary.Element> Prefabs);

    private sealed class Node(DmxBinary.Element element, Node? parent, IReadOnlyList<DmxBinary.Element> through, IReadOnlyList<DmxBinary.Element> prefabs)
    {
        public DmxBinary.Element Element { get; } = element;
        public Node? Parent { get; set; } = parent;
        public IReadOnlyList<DmxBinary.Element> Through { get; } = through;
        public IReadOnlyList<DmxBinary.Element> Prefabs { get; } = prefabs;
        public List<Node> Kids { get; } = [];
        public bool Copy { get; init; }
        public bool Root { get; init; }
        public bool Collapsed { get; set; }
        // The target clone an instance was pointed at as it joined the world (181019590); null for the element's node.
        public Node? TargetNode { get; set; }
    }

    /// <summary>The list after the bake, in stored order.</summary>
    public static List<Item> Baked(DmxBinary.Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var world = document.OfType("CMapWorld").FirstOrDefault();
        if (world is null)
            return [];

        var hidden = MapEntities.HiddenNodes(document);
        var root = Build(world, null, [], [], copy: false);
        var insideTarget = new HashSet<Node>();
        var clones = new HashSet<Node>();
        // The collector (180f86e70, flag 4) skips a target's subtree while an instance names it: per tree, the root's
        // and each prefab's moved map.
        void MarkTargets(Node top)
        {
            var targets = new HashSet<DmxBinary.Element>(ReferenceEqualityComparer.Instance);
            foreach (var n in Preorder(top).Where(n => n.Element.Type == "CMapInstance"))
                if (n.Element.Get<DmxBinary.Element>("target") is { } t)
                    targets.Add(t);
            foreach (var n in Preorder(top))
                for (var at = n.Parent; at != null; at = at.Parent)
                    if (targets.Contains(at.Element))
                    {
                        insideTarget.Add(n);
                        break;
                    }
        }
        MarkTargets(root);
        // A prefab map's node ids are its own document's.
        var hiddenByDoc = new Dictionary<DmxBinary.Element, IReadOnlySet<int>>(ReferenceEqualityComparer.Instance);
        IReadOnlySet<int> HiddenOf(Node n)
        {
            if (n.Prefabs.Count == 0)
                return hidden;
            var prefab = n.Prefabs[^1];
            if (!hiddenByDoc.TryGetValue(prefab, out var set))
                hiddenByDoc[prefab] = set = prefab.Get<DmxBinary.Document>(MapPrefabs.DocumentKey) is { } d ? MapEntities.HiddenNodes(d) : new HashSet<int>();
            return set;
        }

        var list = new List<Node>();
        var slot = new Dictionary<Node, int>();
        void Add(Node n)
        {
            if (!Listed(n))
                return;
            slot[n] = list.Count;
            list.Add(n);
        }
        void Remove(Node n)
        {
            if (!slot.Remove(n, out var at))
                return;
            var last = list[^1];
            list.RemoveAt(list.Count - 1);
            if (!ReferenceEquals(last, n))
            {
                list[at] = last;
                slot[last] = at;
            }
        }
        void Detach(Node n) => n.Parent?.Kids.Remove(n);

        foreach (var n in Preorder(root))
            Add(n);

        while (true)
        {
            var round = Preorder(root)
                .Where(n => n.Element.Type is "CMapInstance" or "CMapPrefab" && !n.Collapsed && (n.Copy || !insideTarget.Contains(n)))
                .ToList();
            if (round.Count == 0)
                break;
            foreach (var n in round)
            {
                if (n.Parent is null)
                    continue;
                n.Collapsed = true;
                // A hidden instance is deleted, not collapsed (180f5ff40 -> 180f6d410 with flags 3): it leaves the list,
                // then UnregisterSubtree (180f684c0) deletes its target when nothing else names it (GROUND_TRUTH 181).
                if (!n.Copy && Hidden(n.Element, HiddenOf(n)))
                {
                    if (n.Element.Type == "CMapPrefab")
                        throw new NotSupportedException("a hidden CMapPrefab (deleted by the bake) is not measured in the entity list");
                    if (n.Element.Get<DmxBinary.Element>("target") is not { } hiddenTarget)
                        throw new NotSupportedException("a hidden instance with no target is not measured in the entity list");
                    Remove(n);
                    RemoveTargetIfUnnamed(n, hiddenTarget);
                    Detach(n);
                    continue;
                }
                if (n.Element.Type == "CMapPrefab")
                {
                    if (insideTarget.Contains(n))
                        throw new NotSupportedException("a prefab inside an instance target is not measured in the entity list");
                    if (n.Element.Get<DmxBinary.Element>(MapPrefabs.WorldKey) is not { } map)
                        throw new NotSupportedException("a prefab whose map is not loaded is not measured in the entity list");
                    var doc = n.Element.Get<DmxBinary.Document>(MapPrefabs.DocumentKey);
                    // Valve's compiler crashes on this one (s2c_prefabprobe10, ENTITIES.md).
                    if (n.Copy && doc is not null && doc.OfType("CMapInstance").Any())
                        throw new NotSupportedException("a copied prefab whose map has instances (Valve's compiler crashes on it) is not measured in the entity list");
                    // The map's nodes are reparented, not cloned (ledger 178): its instances, targets and prefabs
                    // collapse as the root's do, in the next round (the round's list is collected before the prefabs
                    // move; s2c_prefabprobe7 and 9 nest prefabs).
                    var content = new Node(map, n.Parent, n.Through, [.. n.Prefabs, n.Element]) { Copy = n.Copy, Root = true };
                    foreach (var child in map.GetElements("children"))
                        content.Kids.Add(Build(child, content, n.Through, [.. n.Prefabs, n.Element], copy: n.Copy));
                    n.Parent.Kids.Add(content);
                    MarkTargets(content);
                    // Each node joins the world in preorder (180fc5010: OnNodeAdded, then vf 0x228, then the children).
                    // An instance resolves its target there (vf69 1810158a0 -> 181019590): a target still in the
                    // prefab's own world with two or more other referrers is cloned (180f86a90) into the instance's
                    // world (180f5f230), its listed nodes joining right after the instance, and the instance names the
                    // clone; a target already moved is named as it is (GROUND_TRUTH 180).
                    var moved = new HashSet<DmxBinary.Element>(ReferenceEqualityComparer.Instance);
                    var cloned = new HashSet<Node>();
                    var contentInstances = Preorder(content).Where(o => o.Element.Type == "CMapInstance").ToList();
                    foreach (var c in Preorder(content))
                    {
                        Add(c);
                        moved.Add(c.Element);
                        if (c.Element.Type != "CMapInstance" || c.Element.Get<DmxBinary.Element>("target") is not { } t || moved.Contains(t))
                            continue;
                        var others = contentInstances.Count(o => !ReferenceEquals(o, c) && !cloned.Contains(o)
                                                                 && ReferenceEquals(o.Element.Get<DmxBinary.Element>("target"), t));
                        if (others < 2)
                            throw new NotSupportedException("a prefab map's instance ahead of its target with fewer than two other referrers (181019590 moves the target) is not measured in the entity list");
                        if (Preorder(Build(t, null, [], [], copy: false)).Any(o => o.Element.Type is "CMapInstance" or "CMapPrefab"))
                            throw new NotSupportedException("a cloned target holding instances or prefabs is not measured in the entity list");
                        var clone = Build(t, root, c.Through, c.Prefabs, copy: false);
                        root.Kids.Add(clone);
                        clones.Add(clone);
                        foreach (var o in Preorder(clone))
                        {
                            insideTarget.Add(o);
                            Add(o);
                        }
                        c.TargetNode = clone;
                        cloned.Add(c);
                    }
                    Remove(n);
                    Detach(n);
                    continue;
                }
                var target = n.Element.Get<DmxBinary.Element>("target")
                    ?? throw new NotSupportedException("an instance with no target is not measured in the entity list");
                var copyRoot = new Node(target, n.Parent, [.. n.Through, n.Element], n.Prefabs) { Copy = true, Root = true };
                foreach (var child in target.GetElements("children"))
                    copyRoot.Kids.Add(Build(child, copyRoot, copyRoot.Through, n.Prefabs, copy: true));
                n.Parent.Kids.Add(copyRoot);
                foreach (var c in Preorder(copyRoot))
                    Add(c);
                RemoveTargetIfUnnamed(n, target);
                Remove(n);
                Detach(n);
            }
        }

        // The target leaves, its subtree in postorder, when no other instance still names it: CMapInstance_Collapse's
        // 180f88920 == 1 after a collapse, UnregisterSubtree's 180f8e9e0 == false after a delete. A clone has its
        // instance as the only referrer.
        void RemoveTargetIfUnnamed(Node n, DmxBinary.Element target)
        {
            var referred = n.TargetNode is null
                           && Preorder(root).Any(o => !ReferenceEquals(o, n) && o.Element.Type == "CMapInstance" && !o.Collapsed && o.TargetNode is null
                                                      && SameTree(o, n) && ReferenceEquals(o.Element.Get<DmxBinary.Element>("target"), target));
            if (referred || (n.TargetNode ?? Preorder(root).FirstOrDefault(o => !o.Copy && !clones.Contains(o) && SameTree(o, n) && ReferenceEquals(o.Element, target))) is not { } template)
                return;
            var leaving = Postorder(template).ToList();
            var left = new HashSet<Node>(leaving);
            foreach (var inner in leaving.Where(o => o.Element.Type == "CMapInstance"))
            {
                var innerTarget = inner.Element.Get<DmxBinary.Element>("target");
                if (!Preorder(root).Any(o => !left.Contains(o) && !ReferenceEquals(o, n) && o.Element.Type == "CMapInstance" && !o.Collapsed
                                             && SameTree(o, inner) && ReferenceEquals(o.Element.Get<DmxBinary.Element>("target"), innerTarget)))
                    throw new NotSupportedException("a target deleted with another (the delete cascade's order) is not measured in the entity list");
            }
            foreach (var o in leaving)
                Remove(o);
            Detach(template);
        }
        return [.. list.Select(n => new Item(n.Element, n.Through, n.Prefabs))];
    }

    /// <summary>
    /// Whether the visibility state (CMapNode +0x370, 180f6ffc0) is off for a node of the map's own tree: it, or a
    /// node above it, is force_hidden or hidden by the visibility manager, or it is editorOnly (as
    /// <see cref="MapEntities"/> reads them for the lump).
    /// </summary>
    internal static bool Hidden(DmxBinary.Element node, IReadOnlySet<int> hidden)
        => node.GetValue<bool>("editorOnly") == true || (node.GetValue<int>("nodeID") is { } id && hidden.Contains(id))
           || node.GetValue<bool>("force_hidden") == true;

    // Whether two nodes come from the same tree: the root's, or one prefab's moved map (a target is named within it).
    private static bool SameTree(Node a, Node b)
        => a.Prefabs.Count == b.Prefabs.Count && a.Prefabs.Zip(b.Prefabs).All(p => ReferenceEquals(p.First, p.Second));

    // Listed: CMapEntityIONode by IsA (the world, CMapGameDataNode, CMapInstance, CMapPrefab); a copy's or a prefab
    // content's root is a group.
    private static bool Listed(Node n)
        => !n.Root && (n.Element.Type is "CMapWorld" or "CMapInstance" or "CMapPrefab" || MapEntities.CarriesGameKeys(n.Element));

    private static Node Build(DmxBinary.Element e, Node? parent, IReadOnlyList<DmxBinary.Element> through, IReadOnlyList<DmxBinary.Element> prefabs, bool copy)
    {
        var n = new Node(e, parent, through, prefabs) { Copy = copy };
        foreach (var child in e.GetElements("children"))
            n.Kids.Add(Build(child, n, through, prefabs, copy));
        return n;
    }

    private static IEnumerable<Node> Preorder(Node n)
    {
        yield return n;
        foreach (var k in n.Kids.ToList())
            foreach (var d in Preorder(k))
                yield return d;
    }

    private static IEnumerable<Node> Postorder(Node n)
    {
        foreach (var k in n.Kids.ToList())
            foreach (var d in Postorder(k))
                yield return d;
        yield return n;
    }
}
