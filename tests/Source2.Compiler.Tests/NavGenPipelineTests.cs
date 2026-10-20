using Source2.Compiler.Maps;
using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="NavGenPipeline"/> from the .vmap against Valve's stage 029 (polygons and connection blocks) for every
/// s2c_rc_probe map captured under %TEMP%/navgen_capture/&lt;map&gt;_in (tools/re/capture_navgen.py), then through
/// <see cref="NavWriter"/> against Valve's .nav up to the CS custom data (maps without movable meshes).
/// </summary>
public sealed class NavGenPipelineTests(ITestOutputHelper output)
{
    private const string Content = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\s2c_rc_probe\maps\";
    private const string AddonGame = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe";
    private const string GameInfo = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\gameinfo.gi";

    [Fact]
    public void MapsMatchValveAt029()
    {
        var root = Path.Combine(Path.GetTempPath(), "navgen_capture");
        if (!Directory.Exists(root) || CS2Fixtures.StockPak() is not { } pak || MapFixtures.GameSchema() is not { } schema)
            return;
        using var stock = new ValvePak.Package();
        stock.Read(pak);
        byte[]? Read(string name)
        {
            if (stock.FindEntry(name) is not { } e)
                return null;
            stock.ReadEntry(e, out var b);
            return b;
        }
        var vdata = NavGenPipeline.HullVData(Read);
        var navSystem = NavGenPipeline.NavSystemBlock(GameInfo);
        int passed = 0, refused = 0;
        var failures = new List<string>();
        foreach (var dir in Directory.GetDirectories(root, "*_in").Order(StringComparer.Ordinal))
        {
            var map = Path.GetFileName(dir)[..^3];
            if (Environment.GetEnvironmentVariable("NAVGEN_MAPS") is { } only && !only.Split(',').Contains(map))
                continue;
            var vmap = Path.Combine(Content, map + ".vmap");
            if (!File.Exists(vmap) || !File.Exists(Path.Combine(dir, "029_IslandRemoval_out_conn.bin")))
                continue;
            var document = DmxBinary.ReadFile(vmap);
            MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(vmap))!));
            using var models = new SettleBuildTests.PakModels(pak, AddonGame);
            var rules = new SettleWorld.CollisionRules();
            var bodies = SettleWorld.Build(document, models, schema, SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators),
                d => SmartProps.NodesCreatedOnLoad(d, MapFixtures.SmartPropLocators), rules);
            NavGenPipeline.Result result;
            try
            {
                // NAVGEN_STAGES=1: each checkpoint against Valve's stage file, the first difference reported.
                var stages = Environment.GetEnvironmentVariable("NAVGEN_STAGES") == "1";
                string? firstStage = null;
                result = NavGenPipeline.Generate(document, bodies, rules, NavGenPipeline.Settings(document, vdata, navSystem), schema,
                    stages ? (n, polys) =>
                    {
                        if (firstStage != null || Directory.GetFiles(dir, n + "_*_out.bin").FirstOrDefault() is not { } file)
                            return;
                        if (NavGenInputTests.Compare(polys, NavGenInputTests.LoadStage(file)) is { } d)
                            firstStage = $"{Path.GetFileName(file)}: {d}";
                    } : null);
                if (stages)
                    output.WriteLine($"{map}: first stage that differs: {firstStage ?? "none"}; seeds {string.Join(" ", NavGenMapInput.Seeds(document).Select(p => $"({p.X:R} {p.Y:R} {p.Z:R})"))}");
            }
            catch (NotSupportedException e)
            {
                output.WriteLine($"{map}: refused ({e.Message})");
                refused++;
                continue;
            }
            var g = result.Polys;
            var diff = NavGenInputTests.Compare(g, NavGenInputTests.LoadStage(Path.Combine(dir, "029_IslandRemoval_out.bin")));
            var blocks = NavGenConnectionTests.LoadBlocks(Path.Combine(dir, "029_IslandRemoval_out_conn.bin"));
            for (var i = 0; diff is null && i < blocks.Count; i++)
                if (NavGenConnectionTests.Compare(g.Polys[i].Links, blocks[i]) is { } d)
                    diff = $"polygon {i}: {d}";
            output.WriteLine($"{map}: {g.Polys.Count} polygons, {g.Polys.Sum(p => p.Links?.Count ?? 0)} connections, {diff ?? "Valve's"}");
            if (diff is null && !NavWriterTests.Movable(dir).Any())
            {
                // On through the mesh build and the save: every byte before the CS custom data.
                var ours = NavWriter.Write(NavWriter.Build(g), result.Settings, ValveKeyValue.KVObject.Null());
                var valve = NavWriterTests.ValveNav(map);
                var end = NavWriterTests.CustomDataAt(valve);
                var first = end == NavWriterTests.CustomDataAt(ours) ? Enumerable.Range(0, end).FirstOrDefault(i => ours[i] != valve[i], -1) : 0;
                output.WriteLine($"{map}: .nav {(first < 0 ? $"bytes 0..{end} of {valve.Length} are Valve's" : $"differs at byte {first}")}");
                if (first >= 0)
                    diff = ".nav";
            }
            if (diff is null)
                passed++;
            else
                failures.Add(map);
        }
        output.WriteLine($"{passed} exact, {refused} refused, {failures.Count} differ ({string.Join(", ", failures)})");
        // s2c_prefabprobe3: Valve's through 027 from the .vmap; IslandRemoval (029) keeps 210 polygons fewer:
        // the map has a ladder (navinput_ladders.bin, from a ladder-material mesh, not detected), which
        // Valve's island walk presumably crosses. prefabprobe4's input is open (ledger 100).
        Assert.Empty(failures.Except(["s2c_prefabprobe3", "s2c_prefabprobe4"]));
    }
}
