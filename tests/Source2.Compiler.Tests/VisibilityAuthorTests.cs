using Source2.Compiler.Maps;
using ValvePak;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="VisibilityAuthor"/> from Valve's own visibility read back:
/// RED2 (with Valve's input fingerprint), DATA and VXVS against the file
/// (the no-bake references in %TEMP%/nobake_refs).
/// </summary>
public class VisibilityAuthorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("probe01")]
    [InlineData("cardtest")]
    [InlineData("atixref")]
    [InlineData("ze_hold_em_p")]
    public void RoundTripsValvesVisibility(string map)
    {
        var vpk = Path.Combine(Path.GetTempPath(), "nobake_refs", map + ".vpk");
        if (!File.Exists(vpk))
            return;
        using var package = new Package();
        package.Read(vpk);
        if (!package.Entries!.TryGetValue("vvis_c", out var visEntries))
            return;
        var entry = visEntries.First();
        package.ReadEntry(entry, out var valve);
        var vis = VoxelVisibilityReader.Read(valve);
        var trees = WorldPhysicsAuthorTests.Trees(valve);
        var fingerprint = (uint)(long)trees["RED2"]["m_ArgumentDependencies"]!.Values.First()["m_nFingerprint"]!;
        var ours = VisibilityAuthor.Container(vis, fingerprint);
        var mine = WorldPhysicsAuthorTests.Trees(ours);
        var lines = new List<string>();
        foreach (var block in new[] { "RED2", "DATA" })
            lines.AddRange(KvTreeDiff.Diff(trees[block], mine[block]).Select(l => block + l));
        foreach (var l in lines.Take(20))
            output.WriteLine(l);
        var facts = (WorldPhysicsAuthorTests.Facts(valve), WorldPhysicsAuthorTests.Facts(ours));
        output.WriteLine($"{map}: {lines.Count} tree lines; facts {(facts.Item1 == facts.Item2 ? "same" : $"{facts.Item1} vs {facts.Item2}")}");
        Assert.Empty(lines);
        Assert.Equal(facts.Item1, facts.Item2);
    }
}
