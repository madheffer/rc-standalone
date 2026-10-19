namespace Source2.Compiler.Maps;

/// <summary>
/// The world's flat node array (CMapWorld +0x1828 count, +0x1830 nodes) after the instance collapse:
/// the order every entity search (MapWorld_FindEntitiesByClass, 180fc7790 in the 10-15 build) walks,
/// and so the order of the scene's lights (L17, ledger 101). Exact against the captured array of
/// atixref (5032 nodes, all 854 SetIndexInWorld mutations; tools/entities/capture_entitysearch.py).
/// <list type="number">
/// <item>Load (AddNode, 180fca580) appends the world, then every entity-like node (CMapEntity,
/// CMapInstance, CMapPath, CMapPathNode) in pre-order of the whole tree, target groups included;
/// meshes, groups, overlays and smart props are never appended.</item>
/// <item>One pass over the array by index, always advancing (a slot is not looked at again after a
/// removal fills it): each instance that is not inside a target group collapses. It appends its
/// target's entity-like copies in copy id order, nested instance copies included (those queue);
/// when no other live instance in the array uses the target, the target's own entity-like nodes
/// are removed in pre-order; then the instance is removed. A removal moves the last node into the
/// slot (index -1 for the removed one).</item>
/// <item>The queued nested copies collapse the same way, first in first out.</item>
/// </list>
/// Not captured: prefabs and hidden instances (refused).
/// </summary>
public static class MapWorldOrder
{
    private static bool EntityLike(DmxBinary.Element e) => e.Type is "CMapEntity" or "CMapInstance" or "CMapPath" or "CMapPathNode";

    /// <summary>The node ids in the world's array after the collapse; <paramref name="createdOnLoad"/> as for <see cref="MapInstances.Expand"/>.</summary>
    public static List<int> NodeIds(DmxBinary.Document document, int createdOnLoad)
    {
        var world = document.OfType("CMapWorld").FirstOrDefault() ?? throw new InvalidDataException("no CMapWorld");
        if (document.Elements.Any(e => e.Type == "CMapPrefab"))
            throw new NotSupportedException("the world node order with prefabs is not captured");
        static int Id(DmxBinary.Element e) => e.GetValue<int>("nodeID") ?? throw new InvalidDataException($"{e.Type} without a nodeID");

        var hidden = MapEntities.HiddenNodes(document);
        var preorder = new List<DmxBinary.Element>();
        void Pre(DmxBinary.Element n)
        {
            foreach (var c in n.GetElements("children"))
            {
                preorder.Add(c);
                Pre(c);
            }
        }
        Pre(world);
        var inTarget = new HashSet<DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        foreach (var n in preorder)
            if (n.Type == "CMapInstance" && n.Get<DmxBinary.Element>("target") is { } t && inTarget.Add(t))
                Subtree(t, inTarget);
        foreach (var n in preorder)
            if (n.Type == "CMapInstance" && !inTarget.Contains(n) && hidden.Contains(Id(n)))
                throw new NotSupportedException("the world node order with a hidden instance is not captured");

        // MapInstances.Expand's copy ids, per instance chain (outermost first) and template node. A
        // nested instance writes no copy of its own; it takes its slot in the block (start + slot,
        // the slot counted over every node of the target in pre-order).
        var copies = new Dictionary<string, Dictionary<DmxBinary.Element, int>>();
        static string Chain(IEnumerable<DmxBinary.Element> through) => string.Join("/", through.Select(Id));
        MapInstances.Expand(document, MapEntities.From(document), createdOnLoad, (node, id, through, _) =>
        {
            var key = Chain(through);
            if (!copies.TryGetValue(key, out var m))
                copies[key] = m = new(ReferenceEqualityComparer.Instance);
            m[node] = id;
        });

        // The array, a node's slot, and what each array entry is: (template, chain of instance templates).
        var array = new List<int>();
        var slot = new Dictionary<int, int>();
        var info = new Dictionary<int, (DmxBinary.Element Template, DmxBinary.Element[] Chain)>();
        // Live instances (not an original inside a target group) in the array, per target.
        var users = new Dictionary<DmxBinary.Element, int>(ReferenceEqualityComparer.Instance);
        bool Live(int id) => info[id] is var (t, c) && t.Type == "CMapInstance" && !(c.Length == 0 && inTarget.Contains(t));
        DmxBinary.Element? Target(int id) => info[id].Template.Get<DmxBinary.Element>("target");
        void Append(int id, DmxBinary.Element template, DmxBinary.Element[] chain)
        {
            if (!slot.TryAdd(id, array.Count))
                throw new InvalidDataException($"node id {id} twice in the world");
            array.Add(id);
            info[id] = (template, chain);
            if (Live(id) && Target(id) is { } t)
                users[t] = users.GetValueOrDefault(t) + 1;
        }
        void Remove(int id)
        {
            var at = slot[id];
            var last = array[^1];
            array.RemoveAt(array.Count - 1);
            if (at < array.Count)
            {
                array[at] = last;
                slot[last] = at;
            }
            slot.Remove(id);
            if (Live(id) && Target(id) is { } t)
                users[t]--;
        }

        Append(Id(world), world, []);
        foreach (var n in preorder)
            if (EntityLike(n))
                Append(Id(n), n, []);

        var queue = new Queue<int>();
        void Collapse(int id)
        {
            var (template, chain) = info[id];
            var target = template.Get<DmxBinary.Element>("target");
            var subtree = new List<DmxBinary.Element>();
            if (target is not null)
                Subtree(target, subtree);
            DmxBinary.Element[] through = [.. chain, template];
            var known = copies.GetValueOrDefault(Chain(through));
            int? start = null;
            if (known is not null)
                for (var s = 0; s < subtree.Count && start is null; s++)
                    if (known.TryGetValue(subtree[s], out var cid))
                        start = cid - s;
            var placed = new List<(int Id, DmxBinary.Element Node)>();
            for (var s = 0; s < subtree.Count; s++)
            {
                var node = subtree[s];
                if (!EntityLike(node))
                    continue;
                if (known is not null && known.TryGetValue(node, out var cid))
                    placed.Add((cid, node));
                else if (node.Type == "CMapInstance")
                    placed.Add(((start ?? throw new NotSupportedException("a target holding only instances: its copy ids are not derived")) + s, node));
            }
            foreach (var (cid, node) in placed.OrderBy(p => p.Id))
            {
                Append(cid, node, through);
                if (node.Type == "CMapInstance")
                    queue.Enqueue(cid);
            }
            // The instance is still in the array: the target's last user removes its originals.
            if (target is not null && users.GetValueOrDefault(target) <= 1)
                foreach (var node in subtree)
                    if (EntityLike(node) && slot.ContainsKey(Id(node)) && info[Id(node)].Chain.Length == 0)
                        Remove(Id(node));
            Remove(id);
        }

        for (var i = 0; i < array.Count; i++)
        {
            var id = array[i];
            if (info[id].Chain.Length == 0 && Live(id))
                Collapse(id);
        }
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (slot.ContainsKey(id))
                Collapse(id);
        }
        return array;
    }

    private static void Subtree(DmxBinary.Element node, ICollection<DmxBinary.Element> into)
    {
        foreach (var c in node.GetElements("children"))
        {
            into.Add(c);
            Subtree(c, into);
        }
    }
}
