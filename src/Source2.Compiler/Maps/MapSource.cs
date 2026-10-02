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
    /// folder two levels up (content/csgo_addons/&lt;addon&gt;), when it has one.</summary>
    public static DmxBinary.Document Read(string vmapPath)
    {
        var document = DmxBinary.ReadFile(vmapPath);
        if (Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(vmapPath))) is { } addonContent)
            MapPrefabs.Attach(document, MapPrefabs.FromContent(addonContent));
        MapDeformers.Apply(document);
        return document;
    }
}
