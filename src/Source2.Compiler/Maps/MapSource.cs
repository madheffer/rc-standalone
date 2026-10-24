namespace Source2.Compiler.Maps;

/// <summary>
/// A .vmap as the compile has it once loaded: the document, every prefab's map
/// hung on its prefab (<see cref="MapPrefabs"/>, from the addon's content
/// folder the map sits in), and Hammer's deformers applied to the meshes they
/// reach (<see cref="MapDeformers"/>).
/// </summary>
public static class MapSource
{
    /// <summary>Loads <paramref name="vmapPath"/>; prefabs load from the content
    /// folder two levels up (content/csgo_addons/&lt;addon&gt;), when it has one.
    /// A path deformer reads its path class's supports_loop from
    /// <paramref name="schema"/>, or from the game's FGD beside that content
    /// folder (game/csgo/csgo.fgd) when none is given.</summary>
    public static DmxBinary.Document Read(string vmapPath, FgdSchema? schema = null)
    {
        var document = DmxBinary.ReadFile(vmapPath);
        var addonContent = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(vmapPath)));
        if (addonContent != null)
            MapPrefabs.Attach(document, MapPrefabs.FromContent(addonContent));
        if (schema == null && addonContent != null && SettleWorld.AllDocuments(document).Any(d => d.Elements.Any(e => e.Type == "CMapDeformerPath"))
            && Path.GetFullPath(Path.Combine(addonContent, "..", "..", "..", "game")) is var game && File.Exists(Path.Combine(game, "csgo", "csgo.fgd")))
            schema = FgdSchema.Load(Path.Combine(game, "csgo", "csgo.fgd"), [Path.Combine(game, "core"), Path.Combine(game, "csgo")]);
        MapDeformers.Apply(document, schema);
        return document;
    }
}
