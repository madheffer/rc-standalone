using Source2.Compiler.Physics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The parts of a map compile the port builds, each from the .vmap alone:
/// the entity lumps (settled or not) and the world's collision with the
/// physics-only entity models. Which parts a build asks for is the caller's
/// (the CLI's compile-map, from Hammer's switches).
/// </summary>
public static class MapCompile
{
    /// <summary>One file of the compiled map, by its path in the package.</summary>
    public sealed record Output(string Path, byte[] Bytes);

    /// <summary>
    /// Every entity lump: default_ents, the world layers and the template
    /// lumps. <paramref name="settle"/> runs the physics settle first, which
    /// is what the compile does unless it is given nosettle.
    /// </summary>
    public static List<Output> EntityLumps(DmxBinary.Document document, string worldName, FgdSchema schema,
                                           GameContent content, bool settle, bool bakedLighting = false,
                                           bool entitiesOnly = false)
    {
        var locators = Locators(content);
        var settled = settle
            ? SettleWorld.Run(document, content, schema, SmartProps.NodesCreatedOnLoad(document, locators))
            : null;
        return [.. EntityLumpSet.Author(MapEntities.From(document), schema, worldName,
                                        MapEntities.FixupEntityNames(document), document, locators, settled,
                                        bakedLighting, entitiesOnly)
            .Select(l => new Output(l.Path, l.Bytes))];
    }

    /// <summary>
    /// The world's collision (world_physics.vmdl_c and its manifest) and the
    /// models of brush entities that hold only physics. A world the build does
    /// not port yet is left out with a note; the entity models still come.
    /// </summary>
    public static List<Output> Physics(DmxBinary.Document document, string addon, string mapName, FgdSchema schema,
                                       GameContent content, Func<string, MaterialSampler.Renderer?>? sample,
                                       List<string> notes)
    {
        var outputs = new List<Output>();
        try
        {
            var files = WorldPhysicsFiles.Build(document, addon, mapName, content, sample, notes);
            outputs.Add(new(files.ModelPath, files.Model));
            outputs.Add(new(files.ManifestPath, files.Manifest));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            notes.Add($"world_physics not built: {ex.Message}");
        }
        // The other brush entity models carry render meshes, which are not ported.
        outputs.AddRange(EntityPhysicsModels.Build(document, mapName, content.Material, content.CollisionProperty, schema,
                                                   notes, content.SmartProp)
            .Where(m => m.PhysicsOnly)
            .Select(m => new Output(m.Path + "_c", EntityPhysicsModels.Author(m, content.SurfaceName))));
        return outputs;
    }

    /// <summary>
    /// Entity classes whose lump keys the port does not write in full yet: the
    /// lights' shape and bake keys, and the probe volumes' and cubemaps' bake
    /// keys (docs/COVERAGE.md). A lump holding one is refused unless the caller
    /// accepts the gap, because shipping it would lose what Valve's lump has.
    /// </summary>
    public static bool HasLumpGap(string className)
        => className.StartsWith("light_", StringComparison.OrdinalIgnoreCase)
           || className.Equals("env_cubemap", StringComparison.OrdinalIgnoreCase)
           || className.Equals("env_cubemap_box", StringComparison.OrdinalIgnoreCase)
           || className.Equals("env_light_probe_volume", StringComparison.OrdinalIgnoreCase)
           || className.Equals("env_combined_light_probe_volume", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A map compile from resourcecompiler's own command line, on the parts
    /// the port builds. The steps are the compile's (CompileMap, FUN_1801f7840):
    /// the world step in entities-only mode writes the entity lumps, and the
    /// physics step writes world_physics. The full world step (render
    /// geometry, vis inside it, the lighting bake), nav and Steam Audio are
    /// not ported and are refused by name. Like a -fshallow build, the package
    /// already at the output path is kept and only these outputs replaced; a
    /// build that would start from an empty package is refused, as the
    /// compile refuses a partial build then.
    /// </summary>
    /// <returns>0 on success; 1 when refused or failed, with the reason logged.</returns>
    public static int Run(MapCompileArgs args, TextWriter log, bool acceptGaps = false,
                          Func<GameContent, Func<string, MaterialSampler.Renderer?>?>? sampler = null)
    {
        if (args.Input is not { } input || !File.Exists(input))
            return Fail(log, "-i must name an existing .vmap");
        var full = Path.GetFullPath(input);
        var parts = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var content = Array.FindIndex(parts, p => p.Equals("content", StringComparison.OrdinalIgnoreCase));
        if (content < 1 || content + 3 >= parts.Length || !parts[content + 1].Equals("csgo_addons", StringComparison.OrdinalIgnoreCase)
            || !parts[content + 3].Equals("maps", StringComparison.OrdinalIgnoreCase))
            return Fail(log, "the .vmap must sit under content/csgo_addons/<addon>/maps/");
        var root = string.Join(Path.DirectorySeparatorChar, parts[..content]);
        var addon = parts[content + 2];
        var map = string.Join('/', parts[(content + 4)..])[..^".vmap".Length];
        var game = Path.Combine(root, "game");

        var steps = args.SelectedBuilders(out _);
        var entitiesOnly = args.EntitiesOnly;
        var refused = new List<string>();
        if (steps.Contains("world") && !entitiesOnly)
            refused.Add("world (render geometry, and vis and the lighting bake inside it)");
        refused.AddRange(steps.Where(s => s is "nav" or "sareverb" or "sapaths" or "sacustomdata"));
        if (refused.Count > 0)
            return Fail(log, "not ported: " + string.Join(", ", refused) + ". Only -entities and -phys builds run.");
        if (!args.KeepsPackage)
            return Fail(log, "-f and -fshallow2 start from an empty package, and this is a partial build (the compile refuses it too)");

        var package = Path.Combine(args.OutRoot ?? game, "csgo_addons", addon, "maps", map.Replace('/', Path.DirectorySeparatorChar) + ".vpk");
        if (!File.Exists(package))
            return Fail(log, $"no compiled package at {package} to update; the parts not ported need one built by Valve's compiler");

        try
        {
            return Build(args, log, acceptGaps, sampler, full, root, addon, map, game, steps, package);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            // A part of the map one of these steps does not port yet.
            return Fail(log, "not ported: " + ex.Message);
        }
    }

    private static int Build(MapCompileArgs args, TextWriter log, bool acceptGaps,
                             Func<GameContent, Func<string, MaterialSampler.Renderer?>?>? sampler,
                             string full, string root, string addon, string map, string game,
                             HashSet<string> steps, string package)
    {
        var document = DmxBinary.ReadFile(full);
        var schema = FgdSchema.Load(Path.Combine(game, "csgo", "csgo.fgd"), [Path.Combine(game, "core"), Path.Combine(game, "csgo")]);
        using var assets = new GameContent(Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "csgo_addons", addon));
        var entries = Io.VpkWriter.ReadAll(package);
        var notes = new List<string>();
        log.WriteLine($"{map}.vmap -> {package}");

        if (steps.Contains("world"))
        {
            // Every entity node in the document, instance groups included.
            var gaps = document.Elements
                .Select(e => e.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname"))
                .OfType<string>().Where(HasLumpGap).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (gaps.Count > 0 && !acceptGaps)
                return Fail(log, $"the lump would lose the bake keys of {string.Join(", ", gaps)} (not ported); pass --accept-gaps to write it anyway");
            // The world step clears the map's entity lumps before writing its own.
            // Whether it also clears the entities' models (.vmdl_c) and the
            // flammables folder in an entities-only build is not settled; both
            // are kept, since an entities-only build does not make them again.
            var prefix = $"maps/{map}/entities/".ToLowerInvariant();
            foreach (var old in entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                                        && k.EndsWith(".vents_c", StringComparison.OrdinalIgnoreCase)).ToList())
                entries.Remove(old);
            var lumps = EntityLumps(document, map, schema, assets, args.Settle, BakedLightingIn(entries, map, game, addon),
                                    entitiesOnly: true);
            foreach (var lump in lumps)
                entries[lump.Path] = lump.Bytes;
            log.WriteLine($"  entities: {lumps.Count} lumps{(args.Settle ? "" : ", not settled")}");
        }
        if (steps.Contains("phys"))
        {
            // The physics step writes the world's collision; the brush entity
            // models are the world step's, which an entities-only build skips.
            var world = WorldPhysicsFiles.Build(document, addon, map, assets, sampler?.Invoke(assets), notes);
            entries[world.ModelPath] = world.Model;
            entries[world.ManifestPath] = world.Manifest;
            log.WriteLine($"  physics: {world.ModelPath}, {world.ManifestPath}");
        }
        foreach (var note in notes.Take(10))
            log.WriteLine($"  note: {note}");

        var written = package + ".s2c-tmp";
        File.WriteAllBytes(written, Io.VpkWriter.Write(entries));
        File.Move(written, package, overwrite: true);
        log.WriteLine($"  wrote {entries.Count:n0} entries");
        return 0;
    }

    /// <summary>
    /// Whether an entities-only build has baked lighting (resourcecompiler
    /// 1800fd550): maps/&lt;map&gt;/lightmaps/&lt;channel&gt;.vtex_c exists for a
    /// channel CS2's gameinfo sets to 1 (irradiance, direct_light_shadows),
    /// in the package being updated or loose beside it.
    /// </summary>
    private static bool BakedLightingIn(Dictionary<string, byte[]> entries, string map, string game, string addon)
        => new[] { "irradiance", "direct_light_shadows" }.Any(channel =>
               entries.Keys.Any(k => k.Equals($"maps/{map}/lightmaps/{channel}.vtex_c", StringComparison.OrdinalIgnoreCase))
               || File.Exists(Path.Combine(game, "csgo_addons", addon, "maps", map, "lightmaps", channel + ".vtex_c")));

    private static int Fail(TextWriter log, string reason)
    {
        log.WriteLine("error: " + reason);
        return 1;
    }

    /// <summary>How many locators a smart prop definition creates, read from
    /// its compiled definition; 0 when the game does not have it.</summary>
    public static Func<string, int> Locators(GameContent content)
        => path => content.SmartProp(path.Replace('\\', '/')) is { } definition ? SmartProps.LocatorsOf(definition) : 0;
}
