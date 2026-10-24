namespace Source2.Compiler.Maps;

/// <summary>
/// A map's CMapPrefab nodes and the maps they reference. A prefab is not
/// collapsed as an instance is: its map is loaded as a world of its own and
/// walked where the prefab node stands, moved by the prefab's matrix, its
/// nodes named by id path (prefab id, then the node's own id: hammerUniqueId
/// "108:3" on the probe map s2c_prefabprobe; c2m2_fairgrounds_csgo_multi's
/// prefab entities sit between the world's own).
/// </summary>
public static class MapPrefabs
{
    /// <summary>The attribute that carries a prefab's loaded world on the prefab element.</summary>
    public const string WorldKey = "s2c:prefabWorld";

    /// <summary>The attribute that carries the loaded map's hidden node ids (<see cref="MapEntities.HiddenNodes"/>).</summary>
    public const string HiddenKey = "s2c:prefabHidden";

    /// <summary>The attribute that carries the loaded map's instance targets.</summary>
    public const string TargetsKey = "s2c:prefabTargets";

    /// <summary>The attribute that carries the loaded map's document.</summary>
    public const string DocumentKey = "s2c:prefabDocument";

    /// <summary>
    /// Load every prefab's map under <paramref name="doc"/> and hang its world
    /// on the prefab element, then do the same inside it for prefabs set to
    /// load when nested (loadIfNested). A prefab set to load at runtime, or
    /// whose map <paramref name="load"/> cannot find, stays empty.
    /// <paramref name="load"/> takes the content-relative map path
    /// (targetMapPath, e.g. maps/prefabs/x.vmap).
    /// </summary>
    public static void Attach(DmxBinary.Document doc, Func<string, DmxBinary.Document?> load)
        => Attach(doc, load, nested: false, depth: 0);

    private static void Attach(DmxBinary.Document doc, Func<string, DmxBinary.Document?> load, bool nested, int depth)
    {
        if (depth > 32)
            throw new InvalidDataException("prefabs nested more than 32 deep; a prefab referencing itself?");
        ReparentTargets(doc);
        foreach (var prefab in doc.OfType("CMapPrefab"))
        {
            if (prefab.Attributes.ContainsKey(WorldKey))
                continue;
            if (prefab.GetValue<bool>("loadAtRuntime") == true)
                continue;
            if (nested && prefab.GetValue<bool>("loadIfNested") == false)
                continue;
            if (prefab.Get<string>("targetMapPath") is not { Length: > 0 } path || load(path) is not { } loaded)
                continue;
            if (loaded.OfType("CMapWorld").FirstOrDefault() is not { } world)
                continue;
            Attach(loaded, load, nested: true, depth + 1);
            prefab.Attributes[WorldKey] = world;
            prefab.Attributes[DocumentKey] = loaded;
            prefab.Attributes[HiddenKey] = MapEntities.HiddenNodes(loaded);
            prefab.Attributes[TargetsKey] = loaded.OfType("CMapInstance")
                .Select(i => i.Get<DmxBinary.Element>("target")).OfType<DmxBinary.Element>().ToList();
        }
    }

    /// <summary>
    /// What the load's last pass (resourcecompiler 0923 180f69c50) does to
    /// instance targets: every CMapInstance-typed node of the map (prefabs
    /// too), listed in tree order first (180f86e70 with flags 8, nested
    /// worlds included), whose target group's parent is not the instance's
    /// world (180f8c120: the nearest world above it) has the group appended to
    /// that world's children (vf 0x1c8, AddChild 180f75010); vf 0x888 and
    /// vf 0x858, which could exempt one, return 0 for both classes. A target
    /// inside another instance's target is so not copied with it, and the
    /// inner copy is made from the original (captured on s2c_instnest:
    /// capture_collapse.py --parents and --clones).
    /// </summary>
    public static void ReparentTargets(DmxBinary.Document doc)
    {
        var parents = new Dictionary<DmxBinary.Element, DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        foreach (var e in doc.Elements)
            foreach (var child in e.GetElements("children"))
                parents.TryAdd(child, e);
        var instances = new List<DmxBinary.Element>();
        void Walk(DmxBinary.Element node)
        {
            foreach (var child in node.GetElements("children"))
            {
                if (child.Type is "CMapInstance" or "CMapPrefab")
                    instances.Add(child);
                Walk(child);
            }
        }
        foreach (var world in doc.OfType("CMapWorld").Where(w => !parents.ContainsKey(w)))
            Walk(world);
        foreach (var instance in instances)
        {
            if (instance.Get<DmxBinary.Element>("target") is not { } target)
                continue;
            var world = parents.GetValueOrDefault(instance);
            while (world != null && world.Type != "CMapWorld")
                world = parents.GetValueOrDefault(world);
            if (world == null || ReferenceEquals(parents.GetValueOrDefault(target), world))
                continue;
            if (parents.GetValueOrDefault(target) is { } old && old.Get<object?[]>("children") is { } siblings)
                old.Attributes["children"] = siblings.Where(c => !ReferenceEquals(c, target)).ToArray();
            world.Attributes["children"] = (world.Get<object?[]>("children") ?? []).Append(target).ToArray();
            parents[target] = world;
        }
    }

    /// <summary>A loader for maps under an addon's content folder (content/csgo_addons/&lt;addon&gt;).</summary>
    public static Func<string, DmxBinary.Document?> FromContent(string addonContent)
        => path =>
        {
            var file = Path.Combine(addonContent, path.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(file) ? DmxBinary.ReadFile(file) : null;
        };
}
