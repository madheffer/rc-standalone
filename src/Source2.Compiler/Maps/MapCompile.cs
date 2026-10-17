using Source2.Compiler.Io;
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
                                           bool entitiesOnly = false, Action<EntityLumpAuthor.Lighting>? lighting = null)
    {
        var locators = Locators(content);
        var settled = settle
            ? SettleWorld.Run(document, content, schema, SmartProps.NodesCreatedOnLoad(document, locators),
                d => SmartProps.NodesCreatedOnLoad(d, locators))
            : null;
        // The lights' precomputed keys trace the editor's scene of the map's meshes.
        var scene = EditorTraceScene.ForMap(document, content);
        return [.. EntityLumpSet.Author(MapEntities.From(document), schema, worldName,
                                        MapEntities.FixupEntityNames(document), document, locators, settled,
                                        bakedLighting, entitiesOnly, scene, lighting: lighting)
            .Select(l => new Output(l.Path, l.Bytes))];
    }

    /// <summary>
    /// The world's collision (world_physics.vmdl_c and its manifest) and the
    /// models of brush entities (render meshes and physics). A world the build does
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
        // The others carry render meshes beside their physics (EntityRenderModels).
        outputs.AddRange(EntityPhysicsModels.Build(document, mapName, content.Material, content.CollisionProperty, schema,
                                                   notes, content.SmartProp)
            .Select(m => new Output(m.Path + "_c", m.PhysicsOnly ? EntityPhysicsModels.Author(m, content.SurfaceName) : EntityRenderModels.Author(m, content))));
        return outputs;
    }

    /// <summary>
    /// Entity classes whose lump keys the port does not write in full yet: the
    /// lights' precomputed shape keys, shadow slots and vis clusters
    /// (docs/COVERAGE.md). Probe volumes and cubemaps are exact. A lump holding one is refused unless the caller
    /// accepts the gap, because shipping it would lose what Valve's lump has.
    /// </summary>
    public static bool HasLumpGap(string className)
        => className.StartsWithAscii("light_");

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
    /// <param name="skipUnported">Run the ported steps and leave the rest (nav, Steam
    /// Audio, the lighting bake) as the package has them, for a quick test build,
    /// instead of refusing.</param>
    public static int Run(MapCompileArgs args, TextWriter log, bool acceptGaps = false,
                          Func<GameContent, Func<string, MaterialSampler.Renderer?>?>? sampler = null,
                          bool skipUnported = false)
    {
        if (args.Input is not { } input || !File.Exists(input))
            return Fail(log, "-i must name an existing .vmap");
        var full = Path.GetFullPath(input);
        var parts = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var content = Array.FindIndex(parts, p => p.EqualsAscii("content"));
        if (content < 1 || content + 3 >= parts.Length || !parts[content + 1].EqualsAscii("csgo_addons")
            || !parts[content + 3].EqualsAscii("maps"))
            return Fail(log, "the .vmap must sit under content/csgo_addons/<addon>/maps/");
        var root = string.Join(Path.DirectorySeparatorChar, parts[..content]);
        var addon = parts[content + 2];
        var map = string.Join('/', parts[(content + 4)..])[..^".vmap".Length];
        var game = Path.Combine(root, "game");

        var steps = args.SelectedBuilders(out _);
        var entitiesOnly = args.EntitiesOnly;
        var refused = new List<string>();
        var vrad3 = args.Vrad3Output;
        if (vrad3 != null && !File.Exists(Path.Combine(vrad3, "lightmap_packing_geometry.dat")))
            return Fail(log, $"-vrad3output: no vrad3 output in {vrad3}");
        refused.AddRange(steps.Where(s => s is "nav" or "sareverb" or "sapaths" or "sacustomdata" || (s is "bakedlighting" && vrad3 == null)));
        if (args.BakesLighting && vrad3 == null)
            refused.Add("the lighting bake (a build naming no builder, or -bakelighting; -vrad3output takes vrad3's results)");
        if (refused.Count > 0 && !skipUnported)
            return Fail(log, "not ported: " + string.Join(", ", refused)
                + ". -world (without -bakelighting), -vis, -entities and -phys builds run; --skip-unported leaves the rest as the package has it.");
        foreach (var skipped in refused)
            log.WriteLine($"  skipped (not ported): {skipped}");
        // The bake's own outputs are skipped, but the lumps a baking build writes
        // carry its keys (shadow slots, probe atlas), which come from the map alone.
        var bakes = steps.Contains("bakedlighting") || args.BakesLighting;
        steps.ExceptWith(refused);
        if (!args.KeepsPackage)
            return Fail(log, "-f and -fshallow2 start from an empty package, and this is a partial build (the compile refuses it too)");

        var package = Path.Combine(args.OutRoot ?? game, "csgo_addons", addon, "maps", map.Replace('/', Path.DirectorySeparatorChar) + ".vpk");
        if (!File.Exists(package))
            return Fail(log, $"no compiled package at {package} to update; the parts not ported need one built by Valve's compiler");

        try
        {
            return Build(args, log, acceptGaps, sampler, full, root, addon, map, game, steps, package, bakes);
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
                             HashSet<string> steps, string package, bool bakes = false)
    {
        var document = DmxBinary.ReadFile(full);
        // A prefab's map loads from the addon's content, as Hammer's does.
        MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.Combine(root, "content", "csgo_addons", addon)));
        MapDeformers.Apply(document);
        var schema = FgdSchema.Load(Path.Combine(game, "csgo", "csgo.fgd"), [Path.Combine(game, "core"), Path.Combine(game, "csgo")]);
        using var assets = new GameContent(Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "csgo_addons", addon));
        var entries = Io.VpkWriter.ReadAll(package);
        var notes = new List<string>();
        log.WriteLine($"{map}.vmap -> {package}");

        if (steps.Contains("vis") && !steps.Contains("world"))
            log.WriteLine("  note: vis is built inside the world step, so -vis without -world builds nothing");
        if (steps.Contains("world") && !args.EntitiesOnly)
        {
            // The world step without the bake: the lumps, the brush entity models,
            // visibility when the vis builder is on, and the world node
            // (WorldNodeFiles). What a previous build left under the map's
            // worldnodes and entities is replaced. A world_visibility left by an
            // earlier vis build is kept, as -fshallow keeps what a step does not
            // write; whether RC drops it is not captured yet (GROUND_TRUTH 24).
            foreach (var old in entries.Keys.Where(k => k.StartsWithAscii($"maps/{map}/worldnodes/".ToLowerInvariant())
                                                        || k.StartsWithAscii($"maps/{map}/entities/".ToLowerInvariant())).ToList())
                entries.Remove(old);
            EntityLumpAuthor.Lighting? lighting = null;
            var lumps = EntityLumps(document, map, schema, assets, args.Settle, bakes, entitiesOnly: false, lighting: l => lighting = l);
            foreach (var lump in lumps)
                entries[lump.Path] = lump.Bytes;
            var models = EntityPhysicsModels.Build(document, map, assets.Material, assets.CollisionProperty, schema, notes, assets.SmartProp);
            foreach (var m in models)
                entries[m.Path + "_c"] = m.PhysicsOnly ? EntityPhysicsModels.Author(m, assets.SurfaceName) : EntityRenderModels.Author(m, assets);
            using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
            // S2C_WORLD_STAMP=<timestamp>,<fingerprint>: the .vwrld's compile time and
            // fingerprint given rather than now and random, for a reproducible build.
            var (stamp, fingerprint) = Environment.GetEnvironmentVariable("S2C_WORLD_STAMP")?.Split(",") is [var stampText, var printText]
                ? (uint.Parse(stampText, System.Globalization.CultureInfo.InvariantCulture), (ulong?)ulong.Parse(printText, System.Globalization.CultureInfo.InvariantCulture))
                : ((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null);
            var node = WorldNodeFiles.Build(document, addon, map, assets, shaders, schema, game, lighting?.Volumes ?? EnvVolumes.Set.Empty,
                                            stamp, notes, vis: steps.Contains("vis"), fingerprint: fingerprint);
            foreach (var f in node)
                entries[f.Path] = f.Bytes;
            log.WriteLine($"  world: {lumps.Count} lumps, {models.Count} entity models, {node.Count} node files"
                          + (steps.Contains("vis") ? " with visibility" : ", no vis (not asked for)"));
        }
        else if (steps.Contains("world"))
        {
            // Every entity node in the document, instance groups included.
            var gaps = document.Elements
                .Select(e => e.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname"))
                .OfType<string>().Where(HasLumpGap).Distinct(Tier0Strings.IgnoreCase).ToList();
            if (gaps.Count > 0 && !acceptGaps)
                return Fail(log, $"the lump would lose the bake keys of {string.Join(", ", gaps)} (not ported); pass --accept-gaps to write it anyway");
            // The world step clears the map's entity lumps before writing its own.
            // Whether it also clears the entities' models (.vmdl_c) and the
            // flammables folder in an entities-only build is not settled; both
            // are kept, since an entities-only build does not make them again.
            var prefix = $"maps/{map}/entities/".ToLowerInvariant();
            foreach (var old in entries.Keys.Where(k => k.StartsWithAscii(prefix)
                                                        && k.EndsWithAscii(".vents_c")).ToList())
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
        if (args.Vrad3Output is { } folder && bakes && !args.EntitiesOnly)
        {
            // The bake's outputs from vrad3's folder: the lightmap channels, the
            // query data and the probe volume atlases replace what was there.
            using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
            bool RendersAsWorld(string cls) => schema.IsSolidClass(cls) && schema.HasFlag(cls, "render_as_world_but_physics_as_entity");
            var volumes = WorldNodeBuild.ImportanceVolumes(WorldNodeBuild.WorldEntries(document, assets, shaders, RendersAsWorld, buildBakes: true),
                                                           WorldNodeBuild.PropEntries(document, assets, shaders, buildBakes: true));
            var prefix = $"maps/{map}/lightmaps/".ToLowerInvariant();
            foreach (var old in entries.Keys.Where(k => k.StartsWithAscii(prefix)).ToList())
                entries.Remove(old);
            var lightmaps = Lighting.LightmapOutputs.FromVrad3(folder, map, addon, Path.Combine(game, "bin", "win64"), volumes,
                                                               Lighting.LightmapOutputs.Options.From(args.Arguments), note: notes.Add);
            foreach (var f in lightmaps)
                entries[f.Path] = f.Bytes;
            log.WriteLine($"  lighting: {lightmaps.Count} files from {folder}");
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
               entries.Keys.Any(k => k.EqualsAscii($"maps/{map}/lightmaps/{channel}.vtex_c"))
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
