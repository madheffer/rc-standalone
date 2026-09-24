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
        // Chosen by greedy coverage over all 135 local sources. These three take
        // the corpus from 43 entity classes to 93 over 1,820 entities, and atixref
        // alone carries 52 buttons, which nothing else here had. The last two live
        // under maps/prefabs/<parent>/, which is why VmapSource searches.
        ("s2probe", "atixref"),
        ("doom_p2", "ze_doom_p2_c_gameplay"),
        ("c2m2", "c2m2_fairgrounds_csgo_gameplay"),
        // The only local source with multi-node paths: ropes of four, five, six and
        // seven nodes, the path_particle_rope class, and path_node_generic children.
        ("c2m2", "c2m2_fairgrounds_csgo_environment_prefab"),
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
        // The compile SETTLES a physics prop before shipping it: it writes the
        // origin and angles the prop comes to rest at and sets spawnflag 1, which
        // the fgd calls "Start Asleep". 81 of atixref's 82 move, 71 of them also
        // turn, and one of c2m2's falls 11,195 units. That needs world collision,
        // the model's physics hulls and a solver, so it arrives with the geometry
        // tier and not before.
        "prop_physics_override", "prop_physics",
        // Valve ships every key on this one as a plain String, defaults included,
        // while typing its clientside twin on the same map from the same fgd base
        // class. Its path nodes are path_node_generic rather than the
        // path_node_class the fgd names, which is the only difference found. NOT
        // established.
        "path_particle_rope",
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
        // Valve ships every key on these two as a plain String while we type them
        // from the fgd that declares them. Both appear only on the two prefab
        // sources, and the cause is NOT established. See docs/MAP_RESOURCES.md.
        "beam_spotlight", "env_sprite_oriented",
        // A reference that names nothing in the map is not prefixed:
        // env_texturetoggle targets "CacoDemonModel", which is no entity's
        // targetname, and Valve ships it bare. Applying that as a general rule
        // regresses light_environment and point_template, so the narrower rule it
        // belongs to is not known yet.
        "env_texturetoggle",
        // Six entities, all of them members of a CHILD lump, whose angles Valve
        // writes as [-0, -90, 0] against the source's [0, 270, 0]. That is a matrix
        // decompose, and Valve's own precomputedobbangles carry the same
        // "-0.000000" signature, but no decompose we can derive produces a NEGATIVE
        // zero pitch, so the exact path is not known. Everything else about a
        // child lump now matches.
        "prop_dynamic", "point_teleport",
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
            var valve = MapFixtures.RcCompiledLumps(source);
            if (valve is null)
            {
                // A map resourcecompiler refuses is not evidence either way, and
                // silently dropping it would inflate the coverage claim.
                skipped.Add($"{addon}/{map} (resourcecompiler produced nothing)");
                continue;
            }

            var document = DmxBinary.ReadFile(source);
            var ours = EntityLumpSet.Author(
                MapEntities.From(document), MapFixtures.GameSchema(), map,
                MapEntities.FixupEntityNames(document), document);

            // EVERY lump, not just default_ents: a point_template's members compile
            // into a lump of their own, and comparing one file would report all 57
            // of atixref's as entities we invented.
            var theirs = new List<EntityLumpComparison.Entity>();
            var mine = new List<EntityLumpComparison.Entity>();
            foreach (var lump in ours)
            {
                if (!valve.TryGetValue(lump.Path, out var bytes))
                {
                    skipped.Add($"{addon}/{map}: {lump.Path} is not in the compile");
                    continue;
                }
                theirs.AddRange(EntityLumpComparison.Read(bytes, lump.Path));
                mine.AddRange(EntityLumpComparison.Read(lump.Bytes, lump.Path));
            }
            foreach (var path in valve.Keys.Where(k => !ours.Any(l => l.Path.Equals(k, StringComparison.OrdinalIgnoreCase))))
                skipped.Add($"{addon}/{map}: we author no {path}");

            foreach (var entity in theirs)
                entities[entity.ClassName] = entities.GetValueOrDefault(entity.ClassName) + 1;

            var dump = Environment.GetEnvironmentVariable("ENTCOV_DUMP");
            foreach (var line in EntityLumpComparison.Diff(theirs, mine))
            {
                if (dump is not null)
                {
                    var km = System.Text.RegularExpressions.Regex.Match(line, @"^\[([^#\]]+)#[^\]]*\] ([^:]+):");
                    var fgd = km.Success ? MapFixtures.GameSchema()?.KeyOf(km.Groups[1].Value, km.Groups[2].Value) : null;
                    File.AppendAllText(dump, $"{map}\t{fgd?.Type.ToString() ?? "-"}\t{fgd?.Default ?? "-"}\t{line}\n");
                }
                var hit = ClassInLine().Match(line);
                var name = hit.Success ? hit.Groups[1].Value : "(no class)";
                var shownLine = $"{map}: {line}";
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
