using System.Text.RegularExpressions;
using Source2.Compiler;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Every entity lump of a map, authored from its source and compared against
/// Valve's compile of the same source: value, type, flag, key order, entity order
/// and every field of every connection, across default_ents, the world layers and
/// every template lump.
///
/// <para>Whatever still differs has to be one of the gaps written down here, each
/// with its reason. A test that skipped the unfinished parts would stop reporting
/// the day they start mattering; this one fails on anything outside the list, so a
/// regression anywhere else shows, and closing a gap shows as a list that can
/// shrink.</para>
/// </summary>
public partial class EntityLumpAgainstValveTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("ze_doom_p2", "cardtest")]
    [InlineData("s2probe", "probe01")]
    [InlineData("s2c_lighting", "ze_hold_em_p")]
    [InlineData("s2probe", "atixref")]
    [InlineData("doom_p2", "ze_doom_p2_c_gameplay")]
    [InlineData("c2m2", "c2m2_fairgrounds_csgo_gameplay")]
    public void EveryLump_DiffersOnlyInTheDocumentedGaps(string addon, string map)
    {
        var source = MapFixtures.VmapSource(addon, map);
        if (source is null) { MapFixtures.Skip($"the {addon}/{map} source"); return; }
        var valve = MapFixtures.RcCompiledLumps(source);
        if (valve is null) { MapFixtures.Skip("resourcecompiler.exe"); return; }
        Check(map, source, valve);
    }

    /// <summary>
    /// Mako, the biggest zombie escape map in the corpus: 2,275 entities in
    /// default_ents, four world layers and 70 template lumps. It is too big to
    /// recompile on demand, so the addon's own current compile is the reference.
    /// </summary>
    [Fact]
    public void Mako_DiffersOnlyInTheDocumentedGaps()
    {
        const string map = "ze_ffvii_mako_reactor_v6_p";
        var source = MapFixtures.VmapSource("s2c_big", map);
        if (source is null) { MapFixtures.Skip($"the s2c_big/{map} source"); return; }
        var valve = MapFixtures.AddonLumps("s2c_big", map);
        if (valve is null) { MapFixtures.Skip($"a current compile of {map}"); return; }
        Check(map, source, valve, allowLayerIds: true);
    }

    private void Check(string map, string source, IReadOnlyDictionary<string, byte[]> valve, bool allowLayerIds = false)
    {
        var document = DmxBinary.ReadFile(source);
        var ours = EntityLumpSet.Author(MapEntities.From(document), MapFixtures.GameSchema(), map,
                                        MapEntities.FixupEntityNames(document), document);

        Assert.Equal(valve.Keys.Order(StringComparer.OrdinalIgnoreCase),
                     ours.Select(l => l.Path).Order(StringComparer.OrdinalIgnoreCase),
                     StringComparer.OrdinalIgnoreCase);

        var unexplained = new List<string>();
        var total = 0;
        foreach (var lump in ours)
        {
            var report = EntityLumpComparison.Diff(
                EntityLumpComparison.Read(valve[lump.Path], lump.Path),
                EntityLumpComparison.Read(lump.Bytes, lump.Path));
            total += report.Count;
            var layered = lump.Path.Contains("/world_layer_", StringComparison.OrdinalIgnoreCase);
            unexplained.AddRange(report.Where(line => !IsKnownGap(line) && !(allowLayerIds && layered))
                                       .Select(line => $"{Path.GetFileName(lump.Path)}: {line}"));
        }
        output.WriteLine($"{map}: {ours.Count} lumps, {total} difference(s), {unexplained.Count} unexplained");
        Assert.True(unexplained.Count == 0,
            $"{map}: differences outside the documented gaps. " + EntityLumpComparison.Summarize(unexplained));
    }

    /// <summary>The gaps, each with its reason. Keep in step with
    /// docs/MAP_RESOURCES.md.</summary>
    private static bool IsKnownGap(string line)
        // The light export is not ported (FUN_180240a60's light half and the
        // shape passes FUN_180f1def0, FUN_180f1e0e0, FUN_180f19f20): it adds the
        // precomputed bounds, oriented boxes and sub-frusta, rewrites a light's
        // angles through its matrix and sets directlight, and every key it adds
        // moves the light's key order.
        => LightClass().IsMatch(line)
        // The bake writes its results back into the lump: a light's shadow index
        // and unique ids, a probe volume's atlas textures, handshake and size.
        || BakedKeys().IsMatch(line)
        // Physics props are SETTLED: the compile simulates them to rest, writes the
        // resting origin and angles and sets spawnflag 1. Geometry tier.
        || SettledProp().IsMatch(line)
        // A cable's rendercolor ships as the plain string its node's tintColor
        // spells ("255 255 255") where every other colour255 key is typed. The
        // node binds tintColor to rendercolor through hammer_embedded_properties
        // (registered in FUN_181089940), but the write that bypasses the typing
        // is not read yet. One sample, Mako's cable_dynamic.
        || line.StartsWith("[cable_dynamic#", StringComparison.Ordinal) && line.Contains("] rendercolor: ", StringComparison.Ordinal);

    [GeneratedRegex(@"^\[(light_\w+|env_combined_light_probe_volume|env_light_probe_volume|env_cubemap\w*)#")]
    private static partial Regex LightClass();

    [GeneratedRegex(@"\] (bakedshadowindex|light_map_uniqueid|light_path_uniqueid|brightness_legacy|brightness_lumens"
                  + @"|lightprobetexture\w*|cubemaptexture|handshake|light_probe_size_\w|light_probe_atlas\w*|array_index): ")]
    private static partial Regex BakedKeys();

    [GeneratedRegex(@"^\[prop_physics(_override|_multiplayer)?#\d+\] (origin|angles|spawnflags): ")]
    private static partial Regex SettledProp();
}
