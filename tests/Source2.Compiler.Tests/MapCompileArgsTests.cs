using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// compile-map's command line, read as resourcecompiler reads it, on the lines
/// Hammer's presets write for a customer CS2 install; and the command end to
/// end on a scratch copy of Valve's compile of s2c_rounds.
/// </summary>
public class MapCompileArgsTests(ITestOutputHelper output)
{
    private const string Fixed = "-threads 16 -fshallow -maxtextureres 256 -quiet -html -unbufferedio -i x.vmap -noassert ";
    private const string Tail = " -breakpad -nop4 -outroot out";

    private static MapCompileArgs Line(string builders) => MapCompileArgs.Parse((Fixed + builders + Tail).Split(' ', StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void OnlyEntitiesIsTheWorldStepInEntitiesMode()
    {
        var args = Line("-entities -skipauxfiles -nolightmaps");
        Assert.Equal(["world"], args.SelectedBuilders(out var partial));
        Assert.True(partial);
        Assert.True(args.EntitiesOnly);
        Assert.True(args.Settle);
        Assert.True(args.KeepsPackage);
        Assert.Equal("x.vmap", args.Input);
        Assert.Equal("out", args.OutRoot);
    }

    [Fact]
    public void FullAndFastSelectTheWorld()
    {
        var full = Line("-world -rebake_surfacegraph -bakelighting -lightmapMaxResolution 1024 -lightmapVRadQuality 1 -phys -vis -nav"
                        + " -sareverb -sareverb_threads 8 -sapaths -sareverb_threads 8 -sacustomdata -sacustomdata_threads 8");
        // Hammer's -bakelighting is not the table's bakedlighting.
        Assert.Equal(new HashSet<string> { "world", "phys", "vis", "nav", "sareverb", "sapaths", "sacustomdata" },
                     full.SelectedBuilders(out _));
        Assert.False(full.EntitiesOnly);
        Assert.Equal(1024f, full.Arguments["lightmapMaxResolution"]);
        var fast = Line("-world -skipauxfiles -nolightmaps -phys -nav");
        Assert.Equal(new HashSet<string> { "world", "phys", "nav" }, fast.SelectedBuilders(out _));
    }

    [Fact]
    public void NoBuilderMeansEveryOneTheGameAllows()
    {
        var args = MapCompileArgs.Parse(["-i", "x.vmap", "-deformables", "forced", "-nosettle"]);
        Assert.Equal(MapCompileArgs.GameBuilders, args.SelectedBuilders(out var partial));
        Assert.False(partial);
        Assert.False(args.Settle);
        Assert.Equal("forced", args.Arguments["deformables"]);
    }

    [Fact]
    public void EndToEndOnS2cRounds()
    {
        // A current compile with the world's collision, of the copy it was
        // made from (the physics manifest records the addon).
        var source = MapFixtures.VmapSource("s2c_rc_probe", "s2c_rounds");
        var valve = CS2Fixtures.StockPak() is { } pak
            ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, "..", "csgo_addons", "s2c_rc_probe", "maps", "s2c_rounds.vpk"))
            : null;
        if (source is null || valve is null || !File.Exists(valve))
        {
            MapFixtures.Skip("s2c_rounds and Valve's compile of it");
            return;
        }
        var outRoot = Path.Combine(Path.GetTempPath(), "s2c_compile_map_test");
        var package = Path.Combine(outRoot, "csgo_addons", "s2c_rc_probe", "maps", "s2c_rounds.vpk");
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        File.Copy(valve, package, overwrite: true);
        var before = Io.VpkWriter.ReadAll(package);
        var log = new StringWriter();

        // Hammer's Full line is refused, and leaves the package alone.
        Assert.Equal(1, MapCompile.Run(MapCompileArgs.Parse(["-i", source, "-outroot", outRoot, "-world", "-phys", "-vis"]), log));
        Assert.Contains("not ported: world", log.ToString());
        // s2c_rounds has no light, so nothing stops the lump (a light would, without --accept-gaps).
        Assert.DoesNotContain(MapEntities.From(DmxBinary.ReadFile(source)), e => MapCompile.HasLumpGap(e.ClassName));

        // s2c_rounds' props have sphere shapes, which the settle does not port;
        // with nothing to settle it builds no world, and the build goes through.
        Assert.Equal(0, MapCompile.Run(MapCompileArgs.Parse(["-i", source, "-outroot", outRoot, "-entities"]), log, acceptGaps: true));
        File.Copy(valve, package, overwrite: true);

        Assert.Equal(0, MapCompile.Run(MapCompileArgs.Parse(["-i", source, "-outroot", outRoot, "-entities", "-phys", "-nosettle"]), log, acceptGaps: true));
        output.WriteLine(log.ToString());
        var after = Io.VpkWriter.ReadAll(package);
        // What the two steps build, straight from the library; their exactness
        // against Valve is the lump and world physics tests' (the physics
        // compares decoded trees, since a compressed block's bytes need not be
        // Valve's).
        var document = DmxBinary.ReadFile(source);
        using var assets = new GameContent(CS2Fixtures.StockPak()!, Path.GetDirectoryName(Path.GetDirectoryName(valve)!)!);
        var baked = before.Keys.Any(k => k.StartsWith("maps/s2c_rounds/lightmaps/irradiance.vtex_c", StringComparison.OrdinalIgnoreCase)
                                      || k.StartsWith("maps/s2c_rounds/lightmaps/direct_light_shadows.vtex_c", StringComparison.OrdinalIgnoreCase));
        var expected = MapCompile.EntityLumps(document, "s2c_rounds", MapFixtures.GameSchema()!, assets, settle: false,
                                              bakedLighting: baked, entitiesOnly: true)
            .ToDictionary(l => l.Path, l => l.Bytes);
        var world = Physics.WorldPhysicsFiles.Build(document, "s2c_rc_probe", "s2c_rounds", assets, null, []);
        expected[world.ModelPath] = world.Model;
        expected[world.ManifestPath] = world.Manifest;
        foreach (var (path, bytes) in after)
        {
            var want = expected.TryGetValue(path, out var built) ? built : before[path];
            Assert.True(want.AsSpan().SequenceEqual(bytes), $"{path} is not what the build wrote");
        }
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
    }
}
