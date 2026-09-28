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
    // Every class csgo.fgd offers, twice: bare and with every key set
    // (tools/coverage/probe_map.py over cardtest).
    [InlineData("s2probe", "probe_classes")]
    // Mako's cable_dynamic on cardtest, its tintColor (1 2 3) apart from its
    // rendercolor key (12 34 56).
    [InlineData("s2probe", "probe_cable")]
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
        Check(map, source, valve);
    }

    /// <summary>
    /// The prefab probe (tools/physics/prefab_probe_map.py): s2c_rounds with a
    /// moved, turned CMapPrefab of s2c_propover. Its entities ship by id path
    /// (108:3) where the prefab stands. The reference is a full compile kept at
    /// %TEMP%/gt/s2c_prefabprobe.vpk (tools' gt_compile.sh).
    /// </summary>
    [Theory]
    [InlineData("s2c_prefabprobe")]
    // A prefab of c2m2's gameplay map: brush entities in a prefab.
    [InlineData("s2c_prefabprobe2")]
    public void PrefabProbe_DiffersOnlyInTheDocumentedGaps(string map)
    {
        var source = MapFixtures.VmapSource("s2c_rc_probe", map);
        var package = Path.Combine(Path.GetTempPath(), "gt", map + ".vpk");
        if (source is null || !File.Exists(package)) { MapFixtures.Skip($"the {map} source and its compile"); return; }
        using var pkg = new ValvePak.Package();
        pkg.Read(package);
        var valve = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in pkg.Entries.GetValueOrDefault("vents_c") ?? [])
            valve[entry.GetFullPath()] = Io.VpkEntries.Read(pkg, entry);
        Check(map, source, valve);
    }

    private void Check(string map, string source, IReadOnlyDictionary<string, byte[]> valve)
    {
        var document = DmxBinary.ReadFile(source);
        // content/csgo_addons/<addon>/maps/<map>.vmap: prefabs load from the addon's content.
        Maps.MapPrefabs.Attach(document, Maps.MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(source))!));
        var ours = EntityLumpSet.Author(MapEntities.From(document), MapFixtures.GameSchema(), map,
                                        MapEntities.FixupEntityNames(document), document, MapFixtures.SmartPropLocators,
                                        SettleLumpTests.Settle(document, source), BakedIn(valve));

        Assert.Equal(valve.Keys.Order(StringComparer.OrdinalIgnoreCase),
                     ours.Select(l => l.Path).Order(StringComparer.OrdinalIgnoreCase),
                     StringComparer.OrdinalIgnoreCase);

        var unexplained = new List<string>();
        var total = 0;
        var classes = new List<string>();
        foreach (var lump in ours)
        {
            var report = EntityLumpComparison.Diff(
                EntityLumpComparison.Read(valve[lump.Path], lump.Path),
                EntityLumpComparison.Read(lump.Bytes, lump.Path));
            total += report.Count;
            if (Environment.GetEnvironmentVariable("LUMPDIFF") is { Length: > 0 } shown)
                foreach (var line in report.Where(l => l.Contains(shown, StringComparison.Ordinal)).Take(Environment.GetEnvironmentVariable("LUMPDIFF_ALL") is null ? 60 : int.MaxValue))
                    output.WriteLine($"    {Path.GetFileName(lump.Path)} {line}");
            classes.AddRange(report.Select(line => ClassOf().Match(line)).Where(m => m.Success).Select(m => m.Groups[1].Value));
            unexplained.AddRange(report.Where(line => !IsKnownGap(line))
                                       .Select(line => $"{Path.GetFileName(lump.Path)}: {line}"));
        }
        output.WriteLine($"{map}: {ours.Count} lumps, {total} difference(s), {unexplained.Count} unexplained");
        // The classes that still differ, which docs/COVERAGE.md marks partial.
        foreach (var group in classes.GroupBy(c => c).OrderBy(g => g.Key, StringComparer.Ordinal))
            output.WriteLine($"  differs: {group.Key} {group.Count()}");
        Assert.True(unexplained.Count == 0,
            $"{map}: differences outside the documented gaps. " + EntityLumpComparison.Summarize(unexplained));
    }

    /// <summary>
    /// Whether Valve's compile had baked lighting: its lumps name the probe
    /// atlas or carry a light's map unique id, keys only the flag adds.
    /// </summary>
    internal static bool BakedIn(IReadOnlyDictionary<string, byte[]> valve)
        => valve.Any(l => EntityLumpComparison.Read(l.Value, l.Key).Any(e => e.Values.ContainsKey("light_map_uniqueid")
               || e.Values.TryGetValue("lightprobetexture", out var v) && v.Value.EndsWith("env_light_probe_volume_atlas.vtex", StringComparison.Ordinal)));

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
        || BakedKeys().IsMatch(line);

    [GeneratedRegex(@"^\[(light_\w+|env_combined_light_probe_volume|env_light_probe_volume|env_cubemap\w*)#")]
    private static partial Regex LightClass();

    [GeneratedRegex(@"\] (bakedshadowindex|light_map_uniqueid|light_path_uniqueid|brightness_legacy|brightness_lumens"
                  + @"|lightprobetexture\w*|cubemaptexture|handshake|light_probe_size_\w|light_probe_atlas\w*|array_index): ")]
    private static partial Regex BakedKeys();

    [GeneratedRegex(@"^\[([^#\]]+)#")]
    private static partial Regex ClassOf();
}
