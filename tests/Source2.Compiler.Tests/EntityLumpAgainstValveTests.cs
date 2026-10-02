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
    // prop_physics soccer balls and round-shapes models dropped onto s2c_rounds:
    // the settle of sphere and capsule shapes (tools/physics/settle_round_map.py).
    [InlineData("s2c_settleround")]
    // Prefabs whose maps hold instances: c2m2's environment prefab and atixref
    // (instances collapsed after the prefab's move).
    [InlineData("s2c_prefabprobe3")]
    [InlineData("s2c_prefabprobe4")]
    // One instance in a small map (tools/physics/instance_prefab_map.py), alone and as a prefab, with 0 and 7 extra nodes.
    [InlineData("s2c_pinst")]
    [InlineData("s2c_prefabprobe5")]
    [InlineData("s2c_prefabprobe6")]
    // Hammer's lattice and bend deformers (tools/physics/deformer_probe_map.py, deformer_probe2.py).
    [InlineData("deformerprobe")]
    [InlineData("deformerprobe2")]
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
        Maps.MapDeformers.Apply(document);
        // LUMP_VIS=1 runs vis on the map's trace scene for the lights'
        // precomputed_vis_clusters (minutes), then checks them value and place.
        List<(System.Numerics.Vector3, System.Numerics.Vector3)>[]? boxes = null;
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(source)))!;
        if (Environment.GetEnvironmentVariable("LUMP_VIS") == "1" && VisFixtures.RayTraceScene(addon, map) is var (rte, _))
            boxes = Maps.VisBuild.RunWithBlocks(rte, Maps.VisConfig.Read(
                Path.Combine(Path.GetTempPath(), "csgo_addons", addon, "maps", map + ".viscfg"))).FlatClusterBoxes;
        var ours = EntityLumpSet.Author(MapEntities.From(document), MapFixtures.GameSchema(), map,
                                        MapEntities.FixupEntityNames(document), document, MapFixtures.SmartPropLocators,
                                        SettleLumpTests.Settle(document, source), BakedIn(valve),
                                        lightScene: SettleLumpTests.LightScene(document, source), visClusterBoxes: boxes);
        if (boxes is not null)
        {
            int withKey = 0, same = 0;
            foreach (var lump in ours)
            {
                var mine = EntityLumpComparison.Read(lump.Bytes, lump.Path).ToDictionary(e => e.HammerId);
                foreach (var t in EntityLumpComparison.Read(valve[lump.Path], lump.Path).Where(e => e.Values.ContainsKey("precomputed_vis_clusters")))
                {
                    withKey++;
                    if (mine.TryGetValue(t.HammerId, out var m) && m.Values.TryGetValue("precomputed_vis_clusters", out var v)
                        && v.Value == t.Values["precomputed_vis_clusters"].Value && m.KeyOrder[^1] == "precomputed_vis_clusters")
                        same++;
                }
            }
            output.WriteLine($"{map}: precomputed_vis_clusters {same}/{withKey} exact and last");
            Assert.Equal(withKey, same);
        }

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
            // LUMPLIST=<class>: both sides' entities of a class in lump order, id, origin and source id.
            if (Environment.GetEnvironmentVariable("LUMPLIST") is { Length: > 0 } listed)
                foreach (var (side, bytes) in new[] { ("valve", valve[lump.Path]), ("ours", lump.Bytes) })
                    foreach (var e in EntityLumpComparison.Read(bytes, lump.Path).Where(e => e.Values.TryGetValue("classname", out var c) && c.Value.Contains(listed, StringComparison.Ordinal)))
                        output.WriteLine($"    list {side}: {e.HammerId} {(e.Values.TryGetValue("hammeruniqueid", out var h) ? h.Value : "")} {(e.Values.TryGetValue("origin", out var o) ? o.Value : "")} src {(e.Values.TryGetValue("compile_source_id", out var sid) ? sid.Value : "")}");
            // LUMPMISSING=1: the entities only one side has, by id and class.
            if (Environment.GetEnvironmentVariable("LUMPMISSING") == "1")
            {
                var theirs = EntityLumpComparison.Read(valve[lump.Path], lump.Path).ToList();
                var mine = EntityLumpComparison.Read(lump.Bytes, lump.Path).ToList();
                var mineIds = mine.Select(e => e.HammerId).ToHashSet();
                var theirIds = theirs.Select(e => e.HammerId).ToHashSet();
                foreach (var e in theirs.Where(e => !mineIds.Contains(e.HammerId)))
                    output.WriteLine($"    only valve: {e.HammerId} {(e.Values.TryGetValue("classname", out var cn) ? cn.Value : "")}");
                foreach (var e in mine.Where(e => !theirIds.Contains(e.HammerId)))
                    output.WriteLine($"    only ours: {e.HammerId} {(e.Values.TryGetValue("classname", out var cn) ? cn.Value : "")}");
            }
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
    /// docs/ENTITIES.md.</summary>
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
