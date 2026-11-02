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
    [InlineData("s2c_rc_probe", "atixref")]
    [InlineData("s2c_rc_probe", "ze_hold_em_p")]
    [InlineData("c2m2", "c2m2_fairgrounds_csgo_gameplay")]
    [InlineData("s2c_rc_probe", "probe_classes")]
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
                                          document, MapFixtures.SmartPropLocators, SettleLumpTests.Settle(document, source),
                                          lightScene: SettleLumpTests.LightScene(document, source));
        Assert.Equal(tested.Select(l => l.Path), ours.Select(o => o.Path));
        for (var i = 0; i < tested.Count; i++)
            Assert.True(tested[i].Bytes.AsSpan().SequenceEqual(ours[i].Bytes), $"{map}: {tested[i].Path} differs");
        output.WriteLine($"{map}: {ours.Count} lumps identical");
    }

    /// <summary>
    /// Steam Audio's rebake options as compile-map reads them. ze_hold_em_p's .vmap sets reverb and pathing to Manual
    /// (1) and leaves custom data at the FGD's Cleanup (0); CleanupBakedDataIfNeeded takes Manual as Cleanup, so
    /// nothing is baked, and Valve's full build of it ships no .sareverb or .sapaths (s2c_cubeprobe's package).
    /// </summary>
    [Fact]
    public void SteamAudioCleanupOnZeHoldEm()
    {
        if (MapFixtures.VmapSource("s2c_rc_probe", "ze_hold_em_p") is not { } source || MapFixtures.GameSchema() is not { } schema)
        {
            MapFixtures.Skip("the ze_hold_em_p source");
            return;
        }
        var document = DmxBinary.ReadFile(source);
        Assert.Equal(1, MapCompile.SteamAudioRebakeOption(document, schema, "sareverb"));
        Assert.Equal(1, MapCompile.SteamAudioRebakeOption(document, schema, "sapaths"));
        Assert.Equal(0, MapCompile.SteamAudioRebakeOption(document, schema, "sacustomdata"));
        // A cleanup drops the kind's baked file and nothing else.
        string[] entries = ["maps/ze_hold_em_p.sareverb", "maps/ze_hold_em_p.sapaths", "maps/ze_hold_em_p.nav", "maps/ze_hold_em_p.vmap_c"];
        Assert.Equal(["maps/ze_hold_em_p.sareverb"], MapCompile.SteamAudioCleanup(entries, "ze_hold_em_p", "sareverb"));
        Assert.Equal(["maps/ze_hold_em_p.sapaths"], MapCompile.SteamAudioCleanup(entries, "ze_hold_em_p", "sapaths"));
        Assert.Empty(MapCompile.SteamAudioCleanup(entries, "ze_hold_em_p", "sacustomdata"));
    }
}
