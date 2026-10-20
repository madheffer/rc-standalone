namespace Source2.Compiler.Maps;

/// <summary>
/// The world's flat node array (CMapWorld +0x1828 count, +0x1830 nodes) after the instance collapse:
/// the order every entity search (MapWorld_FindEntitiesByClass, 180fc7790 in the 10-15 build) walks,
/// and so the order of the scene's lights (L17, ledgers 102 and 106). Exact against the captured
/// arrays of atixref (5032 nodes, all 854 SetIndexInWorld mutations), cardtest, probe01, s2c_pinst,
/// s2c_pinst7 and the prefab maps s2c_prefabprobe5 and 6 (tools/entities/capture_entitysearch.py).
/// <list type="number">
/// <item>Load (AddNode, 180fca580) appends the world, then every entity-like node (CMapEntity,
/// CMapInstance, CMapPath, CMapPathNode, CMapPrefab) in pre-order of the whole tree, target groups
/// included; meshes, groups, overlays and smart props are never appended.</item>
/// <item>One pass over the array by index, always advancing (a slot is not looked at again after a
/// removal fills it), over the nodes the load appended. Each instance that is not inside a target
/// group collapses: it appends its target's entity-like copies in copy id order, nested instance
/// copies included (those queue); when no other live instance in the array uses the target, the
/// target's own entity-like nodes are removed in pre-order; then the instance is removed. Each
/// prefab collapses too: its map's entity-like nodes are appended in pre-order under the ids
/// <see cref="MapInstances.Expand"/> gives them (its block's start plus the node's slot), its map's
/// instances among them queue, and the prefab is removed. A removal moves the last node into the
/// slot (index -1 for the removed one).</item>
/// <item>The queue then collapses first in first out, by the same rule.</item>
/// </list>
/// Not captured: hidden instances and a prefab inside a prefab (refused). Open: s2c_prefabprobe3 has
/// Valve's ids with a few in another order, and s2c_prefabprobe4's prefab map (atixref, with a smart
/// prop) numbers its nodes one off from <see cref="MapInstances.PrefabSlots"/> in places.
/// </summary>
public static class MapWorldOrder
{
    private static bool EntityLike(DmxBinary.Element e) => e.Type is "CMapEntity" or "CMapInstance" or "CMapPath" or "CMapPathNode" or "CMapPrefab";

    /// <summary>
    /// The node ids in the world's array after the collapse; <paramref name="createdOnLoad"/> and
    /// <paramref name="createdOnLoadIn"/> (a prefab map's) as for <see cref="MapInstances.Expand"/>.
    /// </summary>
    public static List<int> NodeIds(DmxBinary.Document document, int createdOnLoad, Func<DmxBinary.Document, int>? createdOnLoadIn = null)
        => Order(document, createdOnLoad, createdOnLoadIn).Ids;

    /// <summary>
    /// <see cref="NodeIds"/>, and the array id of each prefab map's node by the id path the entity lump
    /// names it with ("prefab:node", hammeruniqueid).
    /// </summary>
    public static (List<int> Ids, Dictionary<string, int> ByIdPath) Order(DmxBinary.Document document, int createdOnLoad,
                                                                         Func<DmxBinary.Document, int>? createdOnLoadIn = null)
    {
        var byIdPath = new Dictionary<string, int>(StringComparer.Ordinal);
        var world = document.OfType("CMapWorld").FirstOrDefault() ?? throw new InvalidDataException("no CMapWorld");
        static int Id(DmxBinary.Element e) => e.GetValue<int>("nodeID") ?? throw new InvalidDataException($"{e.Type} without a nodeID");

        var hidden = MapEntities.HiddenNodes(document);
        var preorder = new List<DmxBinary.Element>();
        Subtree(world, preorder);
        // Instance targets, in the map and in every prefab's map.
        var inTarget = new HashSet<DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        void Targets(IEnumerable<DmxBinary.Element> nodes)
        {
            foreach (var n in nodes)
                if (n.Type == "CMapInstance" && n.Get<DmxBinary.Element>("target") is { } t && inTarget.Add(t))
                    Subtree(t, inTarget);
        }
        Targets(preorder);
        var prefabNodes = new Dictionary<DmxBinary.Element, List<DmxBinary.Element>>(ReferenceEqualityComparer.Instance);
        foreach (var n in preorder.Where(n => n.Type == "CMapPrefab"))
        {
            var nodes = new List<DmxBinary.Element>();
            if (n.Get<DmxBinary.Element>(MapPrefabs.WorldKey) is { } prefabWorld)
                Subtree(prefabWorld, nodes);
            if (nodes.Any(c => c.Type == "CMapPrefab"))
                throw new NotSupportedException("the world node order with a prefab inside a prefab is not captured");
            prefabNodes[n] = nodes;
            Targets(nodes);
        }
        foreach (var n in preorder)
            if (n.Type == "CMapInstance" && !inTarget.Contains(n) && hidden.Contains(Id(n)))
                throw new NotSupportedException("the world node order with a hidden instance is not captured");

        // MapInstances.Expand's copy ids, per instance chain (outermost first) and prefab path, and
        // template node. A nested instance writes no copy of its own; it takes its slot in the block
        // (start + slot, the slot counted over every node of the target in pre-order).
        var copies = new Dictionary<string, Dictionary<DmxBinary.Element, int>>();
        static string Key(IEnumerable<DmxBinary.Element> through, IEnumerable<DmxBinary.Element> prefabs)
            => string.Join("/", through.Select(Id)) + "@" + string.Join("/", prefabs.Select(Id));
        var blocks = new Dictionary<DmxBinary.Element, int>(ReferenceEqualityComparer.Instance);
        MapInstances.Expand(document, MapEntities.From(document), createdOnLoad, (node, id, through, prefabs) =>
        {
            var key = Key(through, prefabs);
            if (!copies.TryGetValue(key, out var m))
                copies[key] = m = new(ReferenceEqualityComparer.Instance);
            m[node] = id;
        }, prefabs: prefabNodes.Count > 0, createdOnLoadIn: createdOnLoadIn, prefabBlock: (p, start) => blocks[p] = start);

        // The array, a node's slot, what each array entry is (template, chain of instance templates,
        // the prefabs whose map holds it, whether the load appended it), and the id each document
        // node has in the array as itself (a prefab map's nodes take their block ids).
        var array = new List<int>();
        var slot = new Dictionary<int, int>();
        var info = new Dictionary<int, (DmxBinary.Element Template, DmxBinary.Element[] Chain, DmxBinary.Element[] Prefabs, bool Loaded)>();
        var own = new Dictionary<DmxBinary.Element, int>(ReferenceEqualityComparer.Instance);
        // Live instances (not an original inside a target group) in the array, per target.
        var users = new Dictionary<DmxBinary.Element, int>(ReferenceEqualityComparer.Instance);
        bool Live(int id) => info[id] is var (t, c, _, _) && t.Type == "CMapInstance" && !(c.Length == 0 && inTarget.Contains(t));
        DmxBinary.Element? Target(int id) => info[id].Template.Get<DmxBinary.Element>("target");
        void Append(int id, DmxBinary.Element template, DmxBinary.Element[] chain, DmxBinary.Element[] prefabs, bool loaded)
        {
            if (!slot.TryAdd(id, array.Count))
                throw new InvalidDataException($"node id {id} twice in the world");
            array.Add(id);
            info[id] = (template, chain, prefabs, loaded);
            if (chain.Length == 0)
                own.TryAdd(template, id);
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

        Append(Id(world), world, [], [], true);
        foreach (var n in preorder)
            if (EntityLike(n))
                Append(Id(n), n, [], [], true);

        var queue = new Queue<int>();
        void CollapsePrefab(int id)
        {
            var prefab = info[id].Template;
            DmxBinary.Element[] path = [prefab];
            if (prefabNodes[prefab].Count > 0)
            {
                var slots = MapInstances.PrefabSlots(prefab.Get<DmxBinary.Element>(MapPrefabs.WorldKey)!, out _);
                var start = blocks.TryGetValue(prefab, out var b) ? b : throw new InvalidDataException($"prefab {Id(prefab)} has no block");
                foreach (var (node, nid) in prefabNodes[prefab].Where(EntityLike).Select(n => (n, start + slots[n])))
                {
                    Append(nid, node, [], path, false);
                    byIdPath.TryAdd($"{Id(prefab)}:{Id(node)}", nid);
                    if (node.Type == "CMapInstance")
                        queue.Enqueue(nid);
                }
            }
            Remove(id);
        }
        void Collapse(int id)
        {
            var (template, chain, prefabs, _) = info[id];
            var target = template.Get<DmxBinary.Element>("target");
            var subtree = new List<DmxBinary.Element>();
            if (target is not null)
                Subtree(target, subtree);
            DmxBinary.Element[] through = [.. chain, template];
            var known = copies.GetValueOrDefault(Key(through, prefabs));
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
                Append(cid, node, through, prefabs, false);
                if (node.Type == "CMapInstance")
                    queue.Enqueue(cid);
            }
            // The instance is still in the array: the target's last user removes its originals.
            if (target is not null && users.GetValueOrDefault(target) <= 1)
                foreach (var node in subtree)
                    if (EntityLike(node) && own.TryGetValue(node, out var original) && slot.ContainsKey(original))
                        Remove(original);
            Remove(id);
        }

        for (var i = 0; i < array.Count; i++)
        {
            var id = array[i];
            var (template, chain, _, loaded) = info[id];
            if (!loaded || chain.Length != 0)
                continue;
            if (template.Type == "CMapPrefab")
                CollapsePrefab(id);
            else if (Live(id))
                Collapse(id);
        }
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (slot.ContainsKey(id))
                Collapse(id);
        }
        return (array, byIdPath);
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
