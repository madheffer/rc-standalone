using Source2.Compiler.Maps;
using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The RC ladder collector (<see cref="NavGenMapInput.Ladders"/>) against the captured nav input
/// (navinput_ladders.bin: count, then 0x28 bytes a ladder), and LadderConnect (028) with IslandRemoval
/// (029) from Valve's stage 028 input against Valve's ladder connectivity (navinput_ladderconn.bin) and 029
/// output, for every s2c_rc_probe map captured
/// under %TEMP%/navgen_capture/&lt;map&gt;_in (tools/re/capture_navgen.py) that has ladders.
/// </summary>
public sealed class NavGenLadderTests(ITestOutputHelper output)
{
    private const string Content = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\s2c_rc_probe\maps\";
    private const string AddonGame = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe";

    private static List<NavLadder>? Captured(string dir)
    {
        var path = Path.Combine(dir, "navinput_ladders.bin");
        if (!File.Exists(path))
            return null;
        var b = File.ReadAllBytes(path);
        var n = BitConverter.ToInt32(b, 0);
        // Older captures wrote 0x1c bytes a ladder (no normal y, z or width).
        if (b.Length != 4 + n * 0x28)
            return null;
        var list = new List<NavLadder>();
        for (var i = 0; i < n; i++)
        {
            float F(int k) => BitConverter.ToSingle(b, 4 + i * 0x28 + k * 4);
            list.Add(new NavLadder(new(F(0), F(1), F(2)), new(F(3), F(4), F(5)), new(F(6), F(7), F(8)), F(9)));
        }
        return list;
    }

    private static NavGenPolys Load(string dir, string stage)
    {
        var g = NavGenInputTests.LoadStage(Path.Combine(dir, stage + ".bin"));
        var blocks = NavGenConnectionTests.LoadBlocks(Path.Combine(dir, stage + "_conn.bin"));
        Assert.Equal(g.Polys.Count, blocks.Count);
        for (var i = 0; i < blocks.Count; i++)
            g.Polys[i].Links = NavGenConnectionTests.ToLinks(blocks[i]);
        return g;
    }

    [Fact]
    public void LaddersMatchValve()
    {
        var root = Path.Combine(Path.GetTempPath(), "navgen_capture");
        if (!Directory.Exists(root) || CS2Fixtures.StockPak() is not { } pak || MapFixtures.GameSchema() is not { } schema)
            return;
        var checkedMaps = 0;
        foreach (var dir in Directory.GetDirectories(root, "*_in").Order(StringComparer.Ordinal))
        {
            var map = Path.GetFileName(dir)[..^3];
            var vmap = Path.Combine(Content, map + ".vmap");
            if (!File.Exists(vmap) || Captured(dir) is not { } valve)
                continue;
            var document = DmxBinary.ReadFile(vmap);
            MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(vmap))!));
            using var models = new SettleBuildTests.PakModels(pak, AddonGame);
            var ours = NavGenMapInput.Ladders(document, models.Material);
            foreach (var l in ours)
                output.WriteLine($"{map}: ours  top {l.Top:R} bottom {l.Bottom:R} n ({l.Normal.X:R} {l.Normal.Y:R} {l.Normal.Z:R}) width {l.Width:R}");
            foreach (var l in valve)
                output.WriteLine($"{map}: Valve top {l.Top:R} bottom {l.Bottom:R} n ({l.Normal.X:R} {l.Normal.Y:R} {l.Normal.Z:R}) width {l.Width:R}");
            Assert.Equal(valve, ours);
            checkedMaps++;
            if (valve.Count == 0 || !File.Exists(Path.Combine(dir, "028_LadderConnect_in_conn.bin")))
                continue;

            // 028 and 029 from Valve's 028 input, tracing through our nav world.
            var rules = new SettleWorld.CollisionRules();
            var bodies = SettleWorld.Build(document, models, schema, SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators),
                d => SmartProps.NodesCreatedOnLoad(d, MapFixtures.SmartPropLocators), rules);
            var world = new NavBoxWorld(bodies, NavGenInput.NavWorldFilter(document, schema));
            var g = Load(dir, "028_LadderConnect_in");
            var connectivity = g.LadderConnect(valve, world);
            foreach (var c in connectivity)
                output.WriteLine($"{map}: connectivity {string.Join(" ", c)}");
            // Valve's (ctx+0x88 count, +0x90 seven int32 a ladder), when captured.
            var conn = Path.Combine(dir, "navinput_ladderconn.bin");
            if (File.Exists(conn))
            {
                var b = File.ReadAllBytes(conn);
                var expected = Enumerable.Range(0, BitConverter.ToInt32(b, 0))
                    .Select(i => Enumerable.Range(0, 7).Select(k => BitConverter.ToInt32(b, 4 + i * 0x1c + k * 4)).ToArray()).ToList();
                Assert.Equal(expected, connectivity);
                output.WriteLine($"{map}: connectivity is Valve's");
            }
            var settings = NavGenInputTests.MapSettings(document, pak);
            // Without the ladders, for comparison: what the ladders' floods keep.
            var bare = Load(dir, "028_LadderConnect_in");
            NavGenStages.IslandStage(bare, NavIslandInput.FromSeeds(NavGenMapInput.Seeds(document)), settings);
            output.WriteLine($"{map}: 029 without the ladders: {bare.Polys.Count} polygons");
            NavGenStages.IslandStage(g,new NavIslandInput(NavGenMapInput.Seeds(document), [], connectivity), settings);
            Assert.Null(NavGenInputTests.Compare(g, NavGenInputTests.LoadStage(Path.Combine(dir, "029_IslandRemoval_out.bin"))));
            var blocks = NavGenConnectionTests.LoadBlocks(Path.Combine(dir, "029_IslandRemoval_out_conn.bin"));
            for (var i = 0; i < g.Polys.Count; i++)
                Assert.True(NavGenConnectionTests.Compare(g.Polys[i].Links, blocks[i]) is null, $"{map} 029 polygon {i}");
            output.WriteLine($"{map}: 029 from Valve's 028 input: {g.Polys.Count} polygons, Valve's");
        }
        output.WriteLine($"{checkedMaps} maps checked");
    }
}
