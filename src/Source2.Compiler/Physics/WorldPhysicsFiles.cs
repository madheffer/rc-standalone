using Source2.Compiler.Maps;

namespace Source2.Compiler.Physics;

/// <summary>
/// A map's world collision as the package carries it:
/// <c>maps/&lt;map&gt;/world_physics.vmdl_c</c> (<see cref="WorldPhysicsAuthor"/>)
/// and the manifest beside it, <c>world_physics.vrman_c</c>, that lists it and
/// that <c>world.vrman</c> names.
/// </summary>
public static class WorldPhysicsFiles
{
    /// <summary>The two files by package path.</summary>
    public sealed record Files(string ModelPath, byte[] Model, string ManifestPath, byte[] Manifest);

    /// <summary>
    /// Build both from the map. <paramref name="sample"/> gives new-blending
    /// materials their layers (the GPU sampler), null for none.
    /// </summary>
    public static Files Build(DmxBinary.Document document, string addon, string mapName, GameContent content,
        Func<string, MaterialSampler.Renderer?>? sample, List<string> notes)
    {
        mapName = Io.Tier0Strings.LowerAscii(mapName);
        var pieces = WorldCollision.Pieces(document,
            name => WorldCollision.ReadMaterial(content.Material(name), content.CollisionProperty),
            notes, sample, content.Physics, content.SmartProp, content.CollisionProperty);
        var model = WorldPhysics.Build(pieces, content.SurfaceName);
        var bytes = WorldPhysicsAuthor.Container(WorldPhysicsTrees.Phys(model), WorldPhysicsTrees.Red2(model, content.SurfaceName),
            WorldPhysicsTrees.Data(mapName));
        var modelPath = $"maps/{mapName}/world_physics.vmdl";
        var manifest = ResourceManifestAuthor.AuthorSaved([modelPath], $"maps/{mapName}/world_physics.vrman", addon);
        return new Files(modelPath + "_c", bytes, $"maps/{mapName}/world_physics.vrman_c", manifest);
    }
}
