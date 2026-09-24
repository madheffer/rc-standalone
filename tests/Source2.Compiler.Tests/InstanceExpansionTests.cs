using Source2.Compiler;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The identity of every entity a map compiles to, in order, against
/// resourcecompiler.
///
/// <para>This is a stronger claim than the per-class audit and a much more
/// fragile one, which is why it is its own test. An instanced copy exists in no
/// walk: the compile invents it, places it, numbers it and gives it a node id of
/// its own. Getting all 821 of atixref's entities in Valve's order with Valve's
/// ids means the placement, the lump order and the id allocator are all right,
/// and any one of them being wrong shows up here immediately.</para>
/// </summary>
public class InstanceExpansionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("s2probe", "atixref", 821)]
    [InlineData("doom_p2", "ze_doom_p2_c_gameplay", 640)]
    [InlineData("c2m2", "c2m2_fairgrounds_csgo_gameplay", 103)]
    public void EveryEntityHasValvesOwnIdentityInValvesOwnOrder(string addon, string map, int expected)
    {
        var source = MapFixtures.VmapSource(addon, map);
        if (source is null || MapFixtures.RcCompiledLumps(source) is not { } valve)
        {
            Assert.True(MapFixtures.WorkshopDir() is null, $"{addon}/{map} has no compile from the installed resourcecompiler (none, or one older than it), so nothing was measured");
            return;
        }

        var document = DmxBinary.ReadFile(source);
        var ours = EntityLumpSet.Author(
            MapEntities.From(document), MapFixtures.GameSchema(), map,
            MapEntities.FixupEntityNames(document), document);

        var theirs = new List<EntityLumpComparison.Entity>();
        var mine = new List<EntityLumpComparison.Entity>();
        foreach (var lump in ours)
        {
            Assert.True(valve.ContainsKey(lump.Path), $"we author {lump.Path}, the compile does not");
            theirs.AddRange(EntityLumpComparison.Read(valve[lump.Path], lump.Path));
            mine.AddRange(EntityLumpComparison.Read(lump.Bytes, lump.Path));
        }

        output.WriteLine($"{map}: {ours.Count} lump(s), valve {theirs.Count} entities, ours {mine.Count}");
        Assert.Equal(valve.Count, ours.Count);
        Assert.Equal(expected, theirs.Count);
        Assert.Equal(theirs.Count, mine.Count);

        var wrong = Enumerable.Range(0, theirs.Count)
            .Where(i => theirs[i].HammerId != mine[i].HammerId
                     || !theirs[i].ClassName.Equals(mine[i].ClassName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var i in wrong.Take(8))
            output.WriteLine($"   {i,4} valve #{theirs[i].HammerId,-6} {theirs[i].ClassName,-20}"
                           + $" ours #{mine[i].HammerId,-6} {mine[i].ClassName}");
        Assert.True(wrong.Count == 0, $"{wrong.Count} of {theirs.Count} entities differ in identity or order");
    }
}
