using Source2.Compiler;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Compiles the entities of a map source and compares the result against
/// <c>resourcecompiler.exe</c>'s own lump for the same map, key by key and type by
/// type.
///
/// <para>The reference is produced by running Valve's compiler here, not by reading
/// an addon's existing VPK. Those go stale: an entity's origin was a fixed-decimal
/// string in an April compile and an array of doubles in an August one, so a test
/// pinned to a shipped artifact pins us to a compiler that no longer exists. These
/// run the real thing, once per map, and cache it.</para>
///
/// <para>The comparison reports every difference rather than the first, because the
/// rules are per-class and per-key: one run should teach the whole rule, not one
/// key of it.</para>
/// </summary>
public class EntityLumpAuthorTests
{
    [Theory]
    [InlineData("gflscripts", "untitled_1")]
    public void AuthoredLump_SaysWhatResourceCompilerSays(string addon, string map)
    {
        var source = MapFixtures.VmapSource(addon, map);
        if (source is null) { MapFixtures.Skip($"the {addon}/{map} source"); return; }

        var valve = MapFixtures.RcCompiledLump(source);
        if (valve is null) { MapFixtures.Skip("resourcecompiler.exe"); return; }

        var schema = MapFixtures.GameSchema();
        Assert.NotNull(schema);

        var entities = MapEntities.From(DmxBinary.ReadFile(source));
        Assert.NotEmpty(entities);

        var ours = EntityLumpAuthor.Author(entities, schema, worldName: map,
                                           fixupEntityNames: MapEntities.FixupEntityNames(DmxBinary.ReadFile(source)));

        var report = EntityLumpComparison.Diff(
            EntityLumpComparison.Read(valve, $"{map}.valve.vents_c"),
            EntityLumpComparison.Read(ours, $"{map}.ours.vents_c"));

        Assert.True(report.Count == 0, $"{addon}/{map}: " + EntityLumpComparison.Summarize(report));
    }

    [Fact]
    public void Entities_ComeOutInTheWorldsChildOrder()
    {
        var source = MapFixtures.VmapSource("ze_doom_p2", "cardtest");
        if (source is null) { MapFixtures.Skip("the cardtest source"); return; }

        var entities = MapEntities.From(DmxBinary.ReadFile(source));

        // Worldspawn leads, then the world's children depth first. Valve's own
        // lump for this map runs 2138, 4, 5, 6, 7 - which is the TREE order, not
        // the order those elements sit in the file.
        Assert.Equal("worldspawn", entities[0].ClassName);
        Assert.Equal([2138, 4, 5, 6, 7], entities.Skip(1).Take(5).Select(e => e.NodeId));
    }
}
