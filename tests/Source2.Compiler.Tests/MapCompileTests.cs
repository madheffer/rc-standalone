using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="MapCompile"/> is the path compile-map takes, with the game's own
/// content in place of the test fixtures. Its entity lumps must be the bytes
/// the lump tests check against Valve (EntityLumpAgainstValveTests), settle
/// included, and without the settle the lumps must differ only where props
/// settle.
/// </summary>
public class MapCompileTests(ITestOutputHelper output)
{
    private static string Root => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CS2Fixtures.StockPak()!)!, "..", ".."));

    [Theory]
    [InlineData("ze_doom_p2", "cardtest")]
    [InlineData("s2probe", "atixref")]
    [InlineData("s2c_lighting", "ze_hold_em_p")]
    [InlineData("c2m2", "c2m2_fairgrounds_csgo_gameplay")]
    [InlineData("s2probe", "probe_classes")]
    public void EntityLumpsAreTheTestedOnes(string addon, string map)
    {
        var source = MapFixtures.VmapSource(addon, map);
        if (source is null || CS2Fixtures.StockPak() is not { } pak || MapFixtures.GameSchema() is not { } schema)
        {
            MapFixtures.Skip($"the {addon}/{map} source");
            return;
        }
        var document = DmxBinary.ReadFile(source);
        using var content = new GameContent(pak, Path.Combine(Root, "game", "csgo_addons", addon));
        var ours = MapCompile.EntityLumps(document, map, schema, content, settle: true);
        var tested = EntityLumpSet.Author(MapEntities.From(document), schema, map, MapEntities.FixupEntityNames(document),
                                          document, MapFixtures.SmartPropLocators, SettleLumpTests.Settle(document, source));
        Assert.Equal(tested.Select(l => l.Path), ours.Select(o => o.Path));
        for (var i = 0; i < tested.Count; i++)
            Assert.True(tested[i].Bytes.AsSpan().SequenceEqual(ours[i].Bytes), $"{map}: {tested[i].Path} differs");
        output.WriteLine($"{map}: {ours.Count} lumps identical");
    }
}
