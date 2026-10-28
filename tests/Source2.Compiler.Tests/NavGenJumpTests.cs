using System.Buffers.Binary;
using System.Numerics;
using Source2.Compiler.Maps;
using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>AddJumps (026) from Valve's own input, swept through our nav world, against Valve's output.</summary>
public sealed class NavGenJumpTests(ITestOutputHelper output)
{
    private const string Content = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\s2c_rc_probe\maps\";

    /// <summary>The map's nav world as AddJumps sweeps it (<see cref="NavJumpSweepWorld"/> over our settle bodies).</summary>
    internal static NavJumpSweepWorld? SweepWorld(DmxBinary.Document document, string pak)
    {
        if (MapFixtures.GameSchema() is not { } schema)
            return null;
        using var models = new SettleBuildTests.PakModels(pak, @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe");
        var bodies = SettleWorld.Build(document, models, schema, SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators),
            d => SmartProps.NodesCreatedOnLoad(d, MapFixtures.SmartPropLocators), new SettleWorld.CollisionRules());
        return new NavJumpSweepWorld(bodies, NavGenInput.NavWorldFilter(document, schema));
    }

    private static NavGenPolys? Load(string dir, string side)
    {
        var path = Path.Combine(dir, $"026_AddJumps_{side}.bin");
        var conn = Path.Combine(dir, $"026_AddJumps_{side}_conn.bin");
        if (!File.Exists(path) || !File.Exists(conn))
            return null;
        var g = NavGenInputTests.LoadStage(path);
        var blocks = NavGenConnectionTests.LoadBlocks(conn);
        for (var i = 0; i < blocks.Count; i++)
            g.Polys[i].Links = NavGenConnectionTests.ToLinks(blocks[i]);
        return g;
    }

    [Theory]
    [InlineData("probe01")]
    [InlineData("cardtest")]
    [InlineData("probe_classes")]
    [InlineData("s2c_prefabprobe5")]
    [InlineData("s2c_prefabprobe6")]
    [InlineData("s2c_prefabprobe3")]
    public void AddJumpsMatchesValve(string map)
    {
        var dir = Path.Combine(Path.GetTempPath(), "navgen_capture", map + "_in");
        if (Load(dir, "in") is not { } g || Load(dir, "out") is not { } valve || CS2Fixtures.StockPak() is not { } pak)
            return;
        var document = DmxBinary.ReadFile(Path.Combine(Content, map + ".vmap"));
        MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(Path.Combine(Content, map + ".vmap")))!));
        var settings = NavGenInputTests.MapSettings(document, pak);
        if (SweepWorld(document, pak) is not { } world)
            return;
        var before = g.Polys.Sum(p => p.Links?.Count ?? 0);
        g.AddJumps(settings.Hulls[0], world, new NavGenPolys.JumpSettings());
        var blocks = NavGenConnectionTests.LoadBlocks(Path.Combine(dir, "026_AddJumps_out_conn.bin"));
        var diffs = new List<string>();
        for (var i = 0; i < g.Polys.Count; i++)
            if (NavGenConnectionTests.Compare(g.Polys[i].Links, blocks[i], flag61: false) is { } diff)
                diffs.Add($"polygon {i}: {diff}");
        output.WriteLine($"{map}: {before} -> {g.Polys.Sum(p => p.Links?.Count ?? 0)} connections (Valve {valve.Polys.Sum(p => p.Links?.Count ?? 0)}), {world.Casts} casts, {world.Blocked} blocked");
        foreach (var d in diffs.Take(10))
            output.WriteLine(d);
        Assert.Empty(diffs);
    }
}
