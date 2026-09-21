using System.Text.RegularExpressions;
using Source2.Compiler;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Which Hammer entity classes we reproduce, measured per class against
/// resourcecompiler across every local map source.
///
/// <para>A total difference count says whether we are close; it does not say
/// whether TRIGGERS work. A zombie escape map is mostly triggers, teleports,
/// doors and buttons wired to each other, so the question that matters is which
/// CLASSES come out right, and that is what this reports. The count is dominated
/// by whichever class appears most, which is exactly the wrong way to read
/// coverage.</para>
///
/// <para>The assertion is a ratchet: every class with a difference has to be on
/// the list below. A class that starts failing shows up as an unexpected name,
/// and a class that gets fixed is removed from the list, so the list only ever
/// shrinks.</para>
/// </summary>
public partial class EntityClassCoverageTests(ITestOutputHelper output)
{
    /// <summary>Local sources that already have a cached resourcecompiler build.</summary>
    private static readonly (string Addon, string Map)[] Maps =
    [
        ("gflscripts", "untitled_1"),
        ("ze_doom_p2", "cardtest"),
        ("s2probe", "probe01"),
        ("s2c_lighting", "ze_hold_em_p"),
        // Chosen by greedy coverage over all 135 local sources: these three take
        // the corpus from 43 entity classes to 98, and atixref alone carries 52
        // buttons, which nothing else here had.
        ("s2probe", "atixref"),
        ("doom_p2", "ze_doom_p2_c_gameplay"),
        ("c2m2", "c2m2_fairgrounds_csgo_gameplay"),
    ];

    /// <summary>
    /// Classes that still differ, with the reason. Keep in step with
    /// docs/MAP_RESOURCES.md.
    /// </summary>
    private static readonly HashSet<string> Imperfect = new(StringComparer.OrdinalIgnoreCase)
    {
        // vrad3 writes its results back INTO the lump: a light's baked shadow
        // index and unique ids, a probe volume's atlas textures. These arrive with
        // the lighting tier and not before.
        "light_barn", "light_omni", "light_omni2", "light_spot", "light_ortho", "light_environment",
        "env_cubemap", "env_light_probe_volume", "env_combined_light_probe_volume",
        // hoverposeflags, which the compiler writes and we do not.
        "func_physbox",
        // A brush physics entity's origin is not the one the source states: Valve
        // moves it, presumably to the hull's mass center, and sets a spawnflag
        // with it. atixref has 81 and every one differs in both.
        "prop_physics_override",
        // Needs child entity lumps. A point_template's entities are compiled into
        // maps/<map>/entities/<nodeid>#entitylumpname.vents_c rather than into
        // default_ents, and the template names that lump in entityLumpName.
        "point_template",
        // Needs the path node children serialized into pathNodes and
        // pathNodeRadiusScales. The nodes are walked and numbered already.
        "path_particle_rope_clientside",
        // The two entity-count lines, which the child lumps and instance expansion
        // above account for. See docs/MAP_RESOURCES.md.
        "(no class)",
    };

    [Fact]
    public void EveryClassThatDiffersIsOneWeHaveWrittenDown()
    {
        var entities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var differing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var examples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var mapsRead = 0;
        var skipped = new List<string>();

        foreach (var (addon, map) in Maps)
        {
            var source = MapFixtures.VmapSource(addon, map);
            if (source is null)
            {
                skipped.Add($"{addon}/{map} (no source)");
                continue;
            }
            var valve = MapFixtures.RcCompiledLump(source);
            if (valve is null)
            {
                // A map resourcecompiler refuses is not evidence either way, and
                // silently dropping it would inflate the coverage claim.
                skipped.Add($"{addon}/{map} (resourcecompiler produced nothing)");
                continue;
            }

            var document = DmxBinary.ReadFile(source);
            var ours = EntityLumpAuthor.Author(
                MapEntities.From(document), MapFixtures.GameSchema(), worldName: map,
                fixupEntityNames: MapEntities.FixupEntityNames(document));

            var theirs = EntityLumpComparison.Read(valve, $"{map}.valve.vents_c");
            foreach (var entity in theirs)
                entities[entity.ClassName] = entities.GetValueOrDefault(entity.ClassName) + 1;

            foreach (var line in EntityLumpComparison.Diff(theirs, EntityLumpComparison.Read(ours, $"{map}.ours.vents_c")))
            {
                var hit = ClassInLine().Match(line);
                var name = hit.Success ? hit.Groups[1].Value : "(no class)";
                var shownLine = hit.Success ? line : $"{map}: {line}";
                differing[name] = differing.GetValueOrDefault(name) + 1;
                var shown = examples.TryGetValue(name, out var list) ? list : examples[name] = [];
                if (shown.Count < 2 && !shown.Contains(shownLine))
                    shown.Add(shownLine);
            }
            mapsRead++;
        }

        if (mapsRead == 0)
        {
            Assert.True(MapFixtures.WorkshopDir() is null, "no local map source compiled, so nothing was measured");
            return;
        }

        var clean = entities.Keys.Where(c => !differing.ContainsKey(c)).OrderBy(c => c, StringComparer.Ordinal).ToList();
        output.WriteLine($"{mapsRead} maps, {entities.Count} entity classes, {entities.Values.Sum()} entities");
        foreach (var name in skipped)
            output.WriteLine($"   SKIPPED {name}");
        output.WriteLine($"\nREPRODUCED EXACTLY ({clean.Count} classes):");
        foreach (var name in clean)
            output.WriteLine($"   {entities[name],4}x  {name}");

        output.WriteLine($"\nSTILL DIFFERING ({differing.Count}):");
        foreach (var (name, count) in differing.OrderByDescending(d => d.Value))
        {
            output.WriteLine($"   {entities.GetValueOrDefault(name),4}x  {name,-34} {count} difference(s)"
                           + (Imperfect.Contains(name) ? "" : "   <-- NOT ON THE LIST"));
            foreach (var line in examples.GetValueOrDefault(name) ?? [])
                output.WriteLine($"          {line}");
        }

        var surprises = differing.Keys.Where(c => !Imperfect.Contains(c)).ToList();
        Assert.True(surprises.Count == 0,
            "entity classes differ that are not written down: " + string.Join(", ", surprises));
    }

    [GeneratedRegex(@"^\[([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex ClassInLine();
}
