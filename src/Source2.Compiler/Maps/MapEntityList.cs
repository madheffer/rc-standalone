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
/// so a prefab map's instances, moved in this round, collapse in the next, as the root's do (GROUND_TRUTH 179).</item>
/// </list>
/// Refused until measured: a hidden instance or prefab (the bake deletes it without a copy), a prefab whose map has
/// prefabs, a copied prefab whose map has instances, a prefab inside a target, and a target that loses its last
/// referrer by being deleted with another (the cascade's order).
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
                if (!n.Copy && Hidden(n.Element, HiddenOf(n)))
                    throw new NotSupportedException($"a hidden {n.Element.Type} (deleted by the bake without a copy) is not measured in the entity list");
                if (n.Element.Type == "CMapPrefab")
                {
                    if (insideTarget.Contains(n))
                        throw new NotSupportedException("a prefab inside an instance target is not measured in the entity list");
                    if (n.Element.Get<DmxBinary.Element>(MapPrefabs.WorldKey) is not { } map)
                        throw new NotSupportedException("a prefab whose map is not loaded is not measured in the entity list");
                    var doc = n.Element.Get<DmxBinary.Document>(MapPrefabs.DocumentKey);
                    if (doc is not null && doc.OfType("CMapPrefab").Any())
                        throw new NotSupportedException("a prefab whose map has prefabs is not measured in the entity list");
                    if (n.Copy && doc is not null && doc.OfType("CMapInstance").Any())
                        throw new NotSupportedException("a copied prefab whose map has instances is not measured in the entity list");
                    // The map's nodes are reparented, not cloned (ledger 178): its instances and targets collapse as
                    // the root's do, in the next round (the round's list is collected before the prefabs move).
                    var content = new Node(map, n.Parent, n.Through, [.. n.Prefabs, n.Element]) { Copy = n.Copy, Root = true };
                    foreach (var child in map.GetElements("children"))
                        content.Kids.Add(Build(child, content, n.Through, [.. n.Prefabs, n.Element], copy: n.Copy));
                    n.Parent.Kids.Add(content);
                    MarkTargets(content);
                    foreach (var c in Preorder(content))
                        Add(c);
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
                var referred = Preorder(root).Any(o => !ReferenceEquals(o, n) && o.Element.Type == "CMapInstance" && !o.Collapsed
                                                   && SameTree(o, n) && ReferenceEquals(o.Element.Get<DmxBinary.Element>("target"), target));
                if (!referred && Preorder(root).FirstOrDefault(o => !o.Copy && SameTree(o, n) && ReferenceEquals(o.Element, target)) is { } template)
                {
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
                Remove(n);
                Detach(n);
            }
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
