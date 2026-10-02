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

    /// <summary>A loader for maps under an addon's content folder (content/csgo_addons/&lt;addon&gt;).</summary>
    public static Func<string, DmxBinary.Document?> FromContent(string addonContent)
        => path =>
        {
            var file = Path.Combine(addonContent, path.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(file) ? DmxBinary.ReadFile(file) : null;
        };
}
