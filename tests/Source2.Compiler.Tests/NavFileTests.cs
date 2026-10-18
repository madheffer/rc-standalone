using Source2.Compiler.Maps;
using ValvePak;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// NavFile against resourcecompiler's own .nav: a map without point_nav_walkable seeds or a
/// CMapNavData node ships the fixed empty nav (ze_hold_em_p, ze_doom_p2_c_gameplay, s2c_rounds in
/// s2c_rc_probe), and a map with them is left to the generator (probe01).
/// </summary>
public class NavFileTests
{
    [Theory]
    [InlineData("ze_hold_em_p")]
    [InlineData("ze_doom_p2_c_gameplay")]
    [InlineData("s2c_rounds")]
    public void EmptyNavEqualsValves(string map)
    {
        var source = MapFixtures.VmapSource("s2c_rc_probe", map);
        var package = Package(map);
        if (source is null || package is null) { MapFixtures.Skip($"the {map} source and its compile"); return; }
        var document = DmxBinary.ReadFile(source);
        MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(source))!));
        Assert.False(NavFile.NeedsGeneration(document));
        Assert.Equal(package, NavFile.EmptyDefault());
    }

    [Fact]
    public void SeededMapNeedsTheGenerator()
    {
        var source = MapFixtures.VmapSource("s2c_rc_probe", "probe01");
        if (source is null) { MapFixtures.Skip("probe01's source"); return; }
        Assert.True(NavFile.NeedsGeneration(DmxBinary.ReadFile(source)));
    }

    private static byte[]? Package(string map)
    {
        if (CS2Fixtures.StockPak() is not { } pak)
            return null;
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, "..", "csgo_addons", "s2c_rc_probe", "maps", map + ".vpk"));
        if (!File.Exists(path))
            return null;
        using var pkg = new Package();
        pkg.Read(path);
        var entry = pkg.FindEntry($"maps/{map}.nav");
        return entry is null ? null : Io.VpkEntries.Read(pkg, entry);
    }
}
