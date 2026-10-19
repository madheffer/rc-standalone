using Source2.Compiler.Maps;
using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Stages 024-029 one at a time from Valve's own input (polygons and connection blocks) against Valve's
/// output, from tools/re/capture_navgen.py's dumps (<c>NAVGEN_INPUT</c>, default %TEMP%/navgen_capture/probe01_in;
/// <c>NAVGEN_MAP</c> names the s2c_rc_probe map the dumps came from, default probe01).
/// </summary>
public sealed class NavGenStageTests(ITestOutputHelper output)
{
    private const string Content = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\s2c_rc_probe\maps\";

    private static string Vmap() => Path.Combine(Content, (Environment.GetEnvironmentVariable("NAVGEN_MAP") ?? "probe01") + ".vmap");

    private static string Dir() => Environment.GetEnvironmentVariable("NAVGEN_INPUT") ?? Path.Combine(Path.GetTempPath(), "navgen_capture", "probe01_in");

    private static NavGenPolys? Load(string stage, string side)
    {
        var path = Path.Combine(Dir(), $"{stage}_{side}.bin");
        var conn = Path.Combine(Dir(), $"{stage}_{side}_conn.bin");
        if (!File.Exists(path) || !File.Exists(conn))
            return null;
        var g = NavGenInputTests.LoadStage(path);
        var blocks = NavGenConnectionTests.LoadBlocks(conn);
        Assert.Equal(g.Polys.Count, blocks.Count);
        for (var i = 0; i < blocks.Count; i++)
            g.Polys[i].Links = NavGenConnectionTests.ToLinks(blocks[i]);
        return g;
    }

    private void Check(string stage, Action<NavGenPolys> run)
    {
        if (Load(stage, "in") is not { } g || Load(stage, "out") is not { } valve)
            return;
        var before = g.Polys.Count;
        run(g);
        Assert.Null(NavGenInputTests.Compare(g, valve));
        for (var i = 0; i < g.Polys.Count; i++)
        {
            var diff = NavGenConnectionTests.Compare(g.Polys[i].Links, NavGenConnectionTests.LoadBlocks(Path.Combine(Dir(), $"{stage}_out_conn.bin"))[i]);
            Assert.True(diff is null, $"{stage} polygon {i}: {diff}");
        }
        output.WriteLine($"{stage}: {before} -> {g.Polys.Count} polygons, {g.Polys.Sum(p => p.Links?.Count ?? 0)} connections, Valve's");
    }

    [Fact]
    public void SplitBottleneckPolysMatchesValve() => Check("024_SplitBottleneckPolys", g => g.SplitBottleneckPolys(NavGenStages.BottleneckWidth));

    [Fact]
    public void SplitPolysWithMultipleConnectionsMatchesValve()
        => Check("025_SplitPolysWithMultipleConnections", g => g.SplitPolysWithMultipleConnections(NavGenStages.MultiConnectionTol));

    [Fact]
    public void RemoveSmallAreasOnEdgeMatchesValve()
    {
        if (CS2Fixtures.StockPak() is not { } pak)
            return;
        var settings = NavGenInputTests.MapSettings(DmxBinary.ReadFile(Vmap()), pak);
        Check("027_RemoveSmallAreasOnEdge", g => g.RemoveSmallAreasOnEdge(settings.SmallAreaOnEdgeRemovalSize, settings.Byte58 != 0));
    }

    [Fact]
    public void IslandRemovalMatchesValve()
    {
        if (CS2Fixtures.StockPak() is not { } pak)
            return;
        var vmap = Vmap();
        var document = DmxBinary.ReadFile(vmap);
        MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(vmap))!));
        var settings = NavGenInputTests.MapSettings(document, pak);
        var seeds = NavGenMapInput.Seeds(document);
        output.WriteLine($"seeds: {string.Join("; ", seeds)}");
        Check("029_IslandRemoval", g => NavGenStages.IslandStage(g, NavIslandInput.FromSeeds(seeds), settings));
    }
}
