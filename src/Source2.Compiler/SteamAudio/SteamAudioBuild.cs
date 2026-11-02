using Source2.Compiler.Io;
using Source2.Compiler.Maps;
using Source2.Compiler.Maps.NavGen;

namespace Source2.Compiler.SteamAudio;

/// <summary>
/// The -sareverb and -sapaths builders at the Auto rebake option, from the .vmap, as SteamAudioSceneTests checks
/// them piece by piece against Valve's on s2c_saprobe and s2c_sainside (ledgers 170-173): the generation and bake
/// scenes (<see cref="AcousticScene"/>), automatic probes over the generation scene's box, the inside-shapes filter
/// on the compile's physics world, the bake, and the KV3 file. What those maps do not exercise is refused by name:
/// another generation type, the navmesh filter, the map's own probe and volume entities, the nav coverage appender
/// (it runs on any map with a nav, ledger 186), ladders, clustering and compression, sampled pathing visibility.
/// The reverb is seeded 0 (Valve's seeds come from rdtsc, so its own output varies run to run; the pinned capture
/// is the reference), and a probe whose rays meet an exact Embree tie may differ from Valve's (its BVH order is not
/// ported); <see cref="Result.Ties"/> counts them.
/// </summary>
public static class SteamAudioBuild
{
    /// <summary>A built file and how many probes met a ray tie (0 for pathing).</summary>
    public sealed record Result(byte[] Bytes, int Probes, int Ties);

    /// <summary>The bake's inputs from a map compile.</summary>
    /// <param name="HasNavAreas">The build's nav has areas (the nav coverage appender would run).</param>
    public sealed record Inputs(DmxBinary.Document Document, GameContent Content, FgdSchema Schema, string GameInfo, bool HasNavAreas);

    /// <summary>
    /// worldspawn as the compile fills it: the .vmap's value, else the FGD's default, as the KV3 type the FGD key
    /// converts to (int, float or bool); choices and text keys are null (their KV3 conversion is not ported), and so
    /// the reader keeps its default for them.
    /// </summary>
    public static Func<string, object?> World(DmxBinary.Document document, FgdSchema schema)
    {
        var world = MapEntities.From(document).FirstOrDefault(e => e.IsWorld);
        return key =>
        {
            var k = schema.KeyOf("worldspawn", key);
            var value = world?.Keys.FirstOrDefault(kv => kv.Key.EqualsAscii(key)).Value ?? k?.Default;
            if (k is null || value is null)
                return null;
            return k.Type switch
            {
                FgdSchema.FieldType.Integer => (int)CNumbers.Atoi(value),
                FgdSchema.FieldType.Float => CNumbers.Atof(value),
                FgdSchema.FieldType.Boolean => value == "true" || CNumbers.Atoi(value) != 0,
                _ => null,
            };
        };
    }

    /// <summary>The .sareverb at the Auto rebake option.</summary>
    public static Result Reverb(Inputs inputs)
    {
        var settings = Settings(inputs);
        Refuse(inputs, "reverb", settings.FilterNavMesh);
        if (settings.ClusteringEnable || settings.CompressionEnable)
            throw new NotSupportedException("Steam Audio reverb: clustering or compression (not ported)");
        var (gen, box, bake) = Scenes(inputs);
        var probes = Probes(inputs, gen, box, settings.GridSpacing, settings.HeightAboveFloor, "reverb");
        var sim = new ReflectionSimulator(settings.Rays, 2048, settings.AmbisonicsOrder) { Seed = 0 };
        var bins = (int)(settings.IRDuration / 0.01f);
        var fields = new EnergyField?[probes.Count];
        var reverbs = new float[3 * probes.Count];
        var ties = 0;
        Parallel.For(0, probes.Count, p =>
        {
            var scene = bake();
            var c = probes[p];
            var space = new ReflectionSimulator.Space(1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, -1f, c.X, c.Y, c.Z);
            var field = sim.Bake(scene, space, space, settings.Rays, settings.Bounces, settings.IRDuration, settings.AmbisonicsOrder, bins);
            if (scene.Ties > 0)
                Interlocked.Increment(ref ties);
            fields[p] = new EnergyField { Channels = field.Length, Bins = bins, Data = [.. field.SelectMany(ch => ch.SelectMany(b => b))] };
            ReverbEstimator.Estimate(field, 0.01f).CopyTo(reverbs, 3 * p);
        });
        var layer = new BakedDataLayer
        {
            Type = 0,
            Variation = 0,
            Reflections = new BakedReflections { EnergyFields = [.. fields], Reverbs = reverbs, NeedsUpdate = new byte[probes.Count] },
        };
        return new Result(SteamAudioFiles.Reverb(new ProbeBatch { Probes = probes, Layers = [layer] }.Write(), settings), probes.Count, ties);
    }

    /// <summary>The reverb bake's settings: the code's, gameinfo's ReverbDefaults, then worldspawn (18006bc60).</summary>
    public static ReverbSettings Settings(Inputs inputs)
    {
        var order = PathingSettings.GameInfoBlock(inputs.GameInfo, "ResourceCompiler/SteamAudio/Reverb")?.GetValueOrDefault("AmbisonicsOrder");
        return ReverbSettings.From(PathingSettings.GameInfoBlock(inputs.GameInfo, "ResourceCompiler/SteamAudio/ReverbDefaults"), order,
                                   World(inputs.Document, inputs.Schema));
    }

    /// <summary>The .sapaths at the Auto rebake option.</summary>
    public static Result Paths(Inputs inputs)
    {
        var settings = PathingSettings.From(PathingSettings.GameInfoBlock(inputs.GameInfo, "ResourceCompiler/SteamAudio/PathingDefaults"),
                                            World(inputs.Document, inputs.Schema));
        Refuse(inputs, "pathing", settings.FilterNavMesh);
        if (settings.VisibilitySamples > 1)
            throw new NotSupportedException("Steam Audio pathing: sampled visibility (more than one sample, not ported)");
        var (gen, box, bake) = Scenes(inputs);
        var probes = Probes(inputs, gen, box, settings.GridSpacing, settings.HeightAboveFloor, "pathing");
        var baked = PathBaker.Bake(bake(), probes, settings.VisibilitySamples, settings.VisibilityRadius, settings.VisibilityThreshold,
                                   float.PositiveInfinity, settings.PathRange);
        var batch = new ProbeBatch { Probes = probes, Layers = [new BakedDataLayer { Type = 1, Variation = 3, Pathing = baked }] };
        return new Result(SteamAudioFiles.Paths(batch.Write()), probes.Count, 0);
    }

    private static readonly string[] RefusedClasses = ["func_ladder", "snd_opvar_set_point"];

    private static void Refuse(Inputs inputs, string kind, bool filterNavMesh)
    {
        var raw = MapEntities.From(inputs.Document).FirstOrDefault(e => e.IsWorld)?.Keys
            .FirstOrDefault(k => k.Key.EqualsAscii($"steamaudio_{kind}_generation_type")).Value;
        if (raw is { Length: > 0 } && CNumbers.Atoi(raw) != 0)
            throw new NotSupportedException($"Steam Audio {kind}: generation type {raw} (only Automatic, Everywhere is ported)");
        if (filterNavMesh)
            throw new NotSupportedException($"Steam Audio {kind}: the navmesh filter (not ported)");
        if (inputs.HasNavAreas)
            throw new NotSupportedException($"Steam Audio {kind}: a nav mesh, whose coverage appender samples tier0's time-seeded random stream (ledger 186)");
        foreach (var e in MapEntities.From(inputs.Document))
            if (e.ClassName.Contains("steamaudio", StringComparison.OrdinalIgnoreCase) || RefusedClasses.Any(c => e.ClassName.EqualsAscii(c)))
                throw new NotSupportedException($"Steam Audio {kind}: a {e.ClassName} (its probe filter or appender is not ported)");
    }

    private static (TriangleScene Generation, (System.Numerics.Vector3 Centre, System.Numerics.Vector3 Size) Box, Func<TriangleScene> Bake) Scenes(Inputs inputs)
    {
        var txt = inputs.Content.Read("scripts/surfaceproperties_steamaudio.txt") is { } bytes
            ? System.Text.Encoding.UTF8.GetString(bytes)
            : throw new NotSupportedException("Steam Audio: no scripts/surfaceproperties_steamaudio.txt");
        var (materials, names) = AcousticMaterials.Build(inputs.Content.SurfaceProperties(), txt);
        var gen = AcousticScene.Build(inputs.Document, inputs.Content, inputs.Schema, names, AcousticScene.GenerationMask);
        var bake = AcousticScene.Build(inputs.Document, inputs.Content, inputs.Schema, names, AcousticScene.BakeMask);
        TriangleScene Of(AcousticScene s)
        {
            var (v, t, m) = s.ToSteamAudio();
            return new TriangleScene([.. v.SelectMany(x => new[] { x.X, x.Y, x.Z })], [.. t.SelectMany(x => new[] { x.Item1, x.Item2, x.Item3 })], m, materials);
        }
        return (Of(gen), gen.Box(), () => Of(bake));
    }

    // Automatic, everywhere: the generation scene's box, uniform floor probes, then the inside-shapes filter (180fbe450)
    // on the compile's physics world. For pathing a probe the filter drops is refused: s2c_sainside measured it for the
    // reverb only.
    private static List<Sphere> Probes(Inputs inputs, TriangleScene generation, (System.Numerics.Vector3 Centre, System.Numerics.Vector3 Size) box,
                                       float spacing, float height, string kind)
    {
        var (centre, size) = box;
        var matrix = ProbeGeneration.BoxMatrix(0f, 0f, 0f, 0f, centre.X, centre.Y, centre.Z, size.X, size.Y, size.Z);
        var generated = ProbeGeneration.UniformFloor(generation, matrix, spacing, height);
        var locators = MapCompile.Locators(inputs.Content);
        var bodies = SettleWorld.Build(inputs.Document, inputs.Content, inputs.Schema, SmartProps.NodesCreatedOnLoad(inputs.Document, locators),
                                       d => SmartProps.NodesCreatedOnLoad(d, locators));
        var inNav = NavGenInput.NavWorldFilter(inputs.Document, inputs.Schema);
        var world = SettleWorld.CreateWorld([.. bodies.Where(inNav)], []);
        var source = generated.Select(ProbeGeneration.ToSource).ToList();
        var kept = ProbeFilters.InsideShapes(world, source);
        if (kind == "pathing" && kept.Count != source.Count)
            throw new NotSupportedException("Steam Audio pathing: the inside-shapes filter drops a probe (measured for the reverb only)");
        return [.. kept.Select(ProbeGeneration.FromSource)];
    }
}
