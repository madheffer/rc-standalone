using System.Diagnostics;
using System.Text.RegularExpressions;
using Source2.Compiler.Maps;
using ValvePak;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The physics settle end to end in the entity lump build: every lump
/// authored with <see cref="SettleWorld.Run"/>'s results against the lumps
/// in the addon's own compiled package (read as they are, never recompiled),
/// and not one settled-prop key (origin, angles, spawnflags,
/// phys_start_asleep) may differ.
/// </summary>
public sealed partial class SettleLumpTests(ITestOutputHelper output)
{
    private static string Root => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CS2Fixtures.StockPak()!)!, "..", ".."));

    [Theory]
    [InlineData("s2c_rc_probe", "atixref")]
    [InlineData("s2c_rc_probe", "c2m2_fairgrounds_csgo_environment_prefab")]
    [InlineData("s2c_big", "ze_ffvii_mako_reactor_v6_p")]
    public void SettledPropsMatchValvesLumps(string addon, string map)
    {
        if (CS2Fixtures.StockPak() is not { } pak || MapFixtures.GameSchema() is not { } schema)
            return;
        var source = Directory.EnumerateFiles(Path.Combine(Root, "content", "csgo_addons", addon, "maps"), map + ".vmap", SearchOption.AllDirectories).FirstOrDefault();
        var compiled = Path.Combine(Root, "game", "csgo_addons", addon, "maps", map + ".vpk");
        if (source is null || !File.Exists(compiled))
        {
            MapFixtures.Skip($"{addon}/{map}");
            return;
        }
        var valve = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using (var pkg = new Package())
        {
            pkg.Read(compiled);
            foreach (var entry in pkg.Entries.GetValueOrDefault("vents_c") ?? [])
                valve[entry.GetFullPath()] = Io.VpkEntries.Read(pkg, entry);
        }

        var document = DmxBinary.ReadFile(source);
        var createdOnLoad = SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators);
        using var models = new SettleBuildTests.PakModels(pak, Path.Combine(Root, "game", "csgo_addons", addon));
        var clock = Stopwatch.StartNew();
        var settled = SettleWorld.Run(document, models, schema, createdOnLoad);
        output.WriteLine($"{map}: settle of {settled.Values.Count(s => s.Moved)} props, {settled.Count} candidates ({settled.Values.Count(s => s.Asleep)} asleep) in {clock.Elapsed.TotalSeconds:F2} s");

        var ours = EntityLumpSet.Author(MapEntities.From(document), schema, map, MapEntities.FixupEntityNames(document),
                                        document, MapFixtures.SmartPropLocators, settled);
        var wrong = new List<string>();
        var total = 0;
        foreach (var lump in ours)
        {
            if (!valve.TryGetValue(lump.Path, out var theirs))
                continue;
            foreach (var line in EntityLumpComparison.Diff(EntityLumpComparison.Read(theirs, lump.Path), EntityLumpComparison.Read(lump.Bytes, lump.Path)))
            {
                total++;
                if (SettledKey().IsMatch(line))
                    wrong.Add($"{Path.GetFileName(lump.Path)}: {line}");
            }
        }
        output.WriteLine($"{map}: {total} lump difference(s), {wrong.Count} on settled props");
        foreach (var line in wrong.Take(20))
            output.WriteLine("  " + line);
        Assert.Empty(wrong);
    }

    /// <summary>
    /// The settle for a map source, with the models of its own addon (the
    /// game folder beside content/csgo_addons/&lt;addon&gt;) over the stock
    /// paks; null without the game or the FGD.
    /// </summary>
    internal static Dictionary<int, SettleWorld.Settlement>? Settle(DmxBinary.Document document, string source)
    {
        if (CS2Fixtures.StockPak() is not { } pak || MapFixtures.GameSchema() is not { } schema)
            return null;
        var parts = Path.GetFullPath(source).Split(Path.DirectorySeparatorChar);
        var at = Array.FindIndex(parts, p => p.Equals("csgo_addons", StringComparison.OrdinalIgnoreCase));
        var addon = at >= 0 && at + 1 < parts.Length ? Path.Combine(Root, "game", "csgo_addons", parts[at + 1]) : "";
        using var models = new SettleBuildTests.PakModels(pak, addon);
        return SettleWorld.Run(document, models, schema, SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators));
    }

    /// <summary>The editor trace scene of a map's meshes, materials from the game and the map's addon.</summary>
    internal static EditorTraceScene? LightScene(DmxBinary.Document document, string source)
    {
        if (CS2Fixtures.StockPak() is not { } pak)
            return null;
        var parts = Path.GetFullPath(source).Split(Path.DirectorySeparatorChar);
        var at = Array.FindIndex(parts, p => p.Equals("csgo_addons", StringComparison.OrdinalIgnoreCase));
        var addon = at >= 0 && at + 1 < parts.Length ? Path.Combine(Root, "game", "csgo_addons", parts[at + 1]) : "";
        using var models = new SettleBuildTests.PakModels(pak, addon);
        return EditorTraceScene.ForMap(document, models);
    }

    [GeneratedRegex(@"^\[prop_physics(_override|_multiplayer)?#\d+\] (origin|angles|spawnflags|phys_start_asleep)[: ]")]
    private static partial Regex SettledKey();
}
