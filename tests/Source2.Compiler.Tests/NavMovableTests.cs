using Source2.Compiler.Maps;
using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Movable nav meshes (rc 180fb5ce0, navsystem 1800250e0): s2c_navmovable (tools/coverage/movable_nav_specimen.py)
/// holds two func_movelinear platforms with CreateMovableNavMesh (one yawed 30) and a control without it; Valve's
/// compile (compile_fresh.py -world -phys -nav, CS2 open) is kept at %TEMP%/fresh_compile/s2c_navmovable_run1.vpk.
/// Our .nav from the .vmap is compared up to the CS custom data (its ids are heap pointers in Valve's).
/// </summary>
public class NavMovableTests(ITestOutputHelper output)
{
    private const string Content = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\s2c_rc_probe\maps\";
    private const string AddonGame = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe";
    private const string GameInfo = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\gameinfo.gi";

    [Fact]
    public void Tag_IsMurmurOfTheIdPath()
    {
        // Valve's s2c_navmovable .nav: node 127's polygons carry 508913949, node 129's 4198742250.
        Assert.Equal(508913949u, new NavMovable([127], new float[12], false, new HashSet<int>()).Tag);
        Assert.Equal(4198742250u, new NavMovable([129], new float[12], false, new HashSet<int>()).Tag);
    }

    [Theory]
    [InlineData("s2c_navmovable", "s2c_navmovable_run1.vpk")]
    public void Nav_IsValvesUpToTheCustomData(string map, string reference)
    {
        var vmap = Path.Combine(Content, map + ".vmap");
        var package = Path.Combine(Path.GetTempPath(), "fresh_compile", reference);
        if (!File.Exists(vmap) || !File.Exists(package) || CS2Fixtures.StockPak() is not { } pak || MapFixtures.GameSchema() is not { } schema)
        {
            MapFixtures.Skip($"{map} and Valve's compile in %TEMP%/fresh_compile");
            return;
        }
        using var stock = new ValvePak.Package();
        stock.Read(pak);
        byte[]? Read(string name)
        {
            if (stock.FindEntry(name) is not { } e)
                return null;
            stock.ReadEntry(e, out var b);
            return b;
        }
        var document = DmxBinary.ReadFile(vmap);
        using var models = new SettleBuildTests.PakModels(pak, AddonGame);
        var rules = new SettleWorld.CollisionRules();
        var bodies = SettleWorld.Build(document, models, schema, SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators),
            d => SmartProps.NodesCreatedOnLoad(d, MapFixtures.SmartPropLocators), rules);
        var settings = NavGenPipeline.Settings(document, NavGenPipeline.HullVData(Read), NavGenPipeline.NavSystemBlock(GameInfo));
        var result = NavGenPipeline.Generate(document, bodies, rules, settings, schema, models.Material);
        output.WriteLine($"static {result.Polys.Polys.Count} polygons; movable: {string.Join(", ", result.Movable.Select(m => $"{m.Mesh.Name} {m.Polys?.Polys.Count ?? 0}"))}");
        var ours = NavWriter.Write(NavCompile.Mesh(document, schema, bodies, result), result.Settings, ValveKeyValue.KVObject.Null());

        using var valvePackage = new ValvePak.Package();
        valvePackage.Read(package);
        valvePackage.ReadEntry(valvePackage.FindEntry($"maps/{map}.nav")!, out var valve);
        if (Environment.GetEnvironmentVariable("NAVGEN_NAVDUMP") is { } dump)
        {
            File.WriteAllBytes(Path.Combine(dump, map + "_ours.nav"), ours);
            File.WriteAllBytes(Path.Combine(dump, map + "_valve.nav"), valve);
        }
        var end = NavWriterTests.CustomDataAt(valve);
        Assert.Equal(end, NavWriterTests.CustomDataAt(ours));
        var first = Enumerable.Range(0, end).FirstOrDefault(i => ours[i] != valve[i], -1);
        Assert.True(first < 0, $"differs at byte {first} of {end}");
    }
}
