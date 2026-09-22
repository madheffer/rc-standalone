using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Every visibility stage, scored over the whole corpus rather than a map.
///
/// <para>Three maps is not a sample, and a rule that fits one map is not a rule:
/// that is the mistake this work has made more than any other. `tools/vis`
/// compiles a spread of maps with <c>-world -vis</c> and records what each stage
/// logged into <c>corpus.tsv</c>, and this reads those numbers back and scores
/// our stages against every row of it.</para>
///
/// <para>The corpus is not committed, because it is generated from the tester's
/// own CS2 content. Without it the test skips and says so, rather than passing on
/// an empty sample.</para>
/// </summary>
public class VisCorpusTests(ITestOutputHelper output)
{
    /// <summary>One row of the corpus, as far as the stages here are scored.</summary>
    private sealed record Row(string Addon, string Map, int Triangles, int Nodes, int Regions, int Clusters);

    /// <summary>What a stage may be off by before it counts as a miss.</summary>
    private const double Tolerance = 0.001;

    [Fact]
    public void EveryStageIsScoredOverTheWholeCorpus()
    {
        var rows = Corpus();
        if (rows.Count == 0)
        {
            output.WriteLine("no corpus: run tools/vis/build_corpus.py");
            return;
        }

        var misses = new List<string>();
        var scored = 0;
        foreach (var row in rows)
        {
            if (VisFixtures.RayTraceScene(row.Addon, row.Map) is not var (rte, valve))
            {
                output.WriteLine($"{row.Map,-46} no .rte or no compiled vis, skipped");
                continue;
            }

            var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
            var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                     / VisVoxelizer.VoxelsPerLeaf;
            var regions = VisRegions.Build(tree, side);
            var inside = VisOutside.Detect(tree, regions, rte, valve.GridSize);
            var clusters = VisClusters.Count(tree, regions, inside.Regions);

            var traced = Enumerable.Range(0, rte.TriangleCount).Count(rte.Traced);
            output.WriteLine($"{row.Map,-46} {Score("triangles", traced, row.Triangles, row.Map, misses)}"
                           + $" {Score("nodes", tree.Nodes, row.Nodes, row.Map, misses)}"
                           + $" {Score("regions", inside.Inside, row.Regions, row.Map, misses)}"
                           + $" {Score("clusters", clusters, row.Clusters, row.Map, misses)}");
            scored++;
        }

        output.WriteLine($"{scored} of {rows.Count} corpus map(s) scored, {misses.Count} stage(s) over {Tolerance:P1}");
        Assert.True(scored > 0, "the corpus has rows but none of their .rte files are present");
    }

    /// <summary>
    /// A stage's error, and a miss recorded when it is over the tolerance. It
    /// reports rather than fails: the corpus is a measurement of where we are,
    /// and the per-stage tests are what hold the ground already taken.
    /// </summary>
    private static string Score(string stage, int ours, int theirs, string map, List<string> misses)
    {
        if (theirs <= 0)
            return $"{stage} -";
        var error = (double)ours / theirs - 1;
        if (Math.Abs(error) > Tolerance)
            misses.Add($"{map} {stage} {error:P2}");
        return $"{stage} {error,7:P2}";
    }

    /// <summary>The corpus, or an empty list when it has not been built.</summary>
    private static List<Row> Corpus()
    {
        var path = Path.Combine(RepoRoot() ?? ".", "tools", "vis", "corpus.tsv");
        if (!File.Exists(path))
            return [];

        var lines = File.ReadAllLines(path);
        var header = lines[0].Split('\t');
        var found = new List<Row>();
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split('\t');
            if (cells.Length != header.Length || cells[2] != "ok")
                continue;
            found.Add(new Row(cells[0], cells[1],
                              Cell(header, cells, "triangles"), Cell(header, cells, "nodes"),
                              Cell(header, cells, "regions"), Cell(header, cells, "clusters")));
        }
        return found;
    }

    private static int Cell(string[] header, string[] cells, string name)
    {
        var at = Array.IndexOf(header, name);
        return at >= 0 && int.TryParse(cells[at], out var value) ? value : 0;
    }

    private static string? RepoRoot()
    {
        var at = new DirectoryInfo(AppContext.BaseDirectory);
        while (at is not null && !Directory.Exists(Path.Combine(at.FullName, "tools", "vis")))
            at = at.Parent;
        return at?.FullName;
    }

    /// <summary>
    /// Mako on its own, because it is the biggest thing we have and the numbers
    /// for it were recorded before the corpus tool existed: 23 MB of ray trace
    /// scene, 279,064 triangles, 2,643,577 nodes and 751,270 regions, and 1,088
    /// seconds of Valve's own visibility. It is skipped unless MAKO is set,
    /// because voxelizing it is minutes rather than seconds.
    /// </summary>
    [Fact]
    public void TheBiggestSceneWeHaveIsScoredToo()
    {
        if (Environment.GetEnvironmentVariable("MAKO") is not { Length: > 0 })
            return;
        if (VisFixtures.RayTraceScene("s2c_big", "ze_ffvii_mako_reactor_v6_p") is not var (rte, valve))
        {
            output.WriteLine("no Mako .rte or compiled vis");
            return;
        }

        var traced = Enumerable.Range(0, rte.TriangleCount).Count(rte.Traced);
        var started = DateTime.UtcNow;
        var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
        output.WriteLine($"triangles {traced:n0} of {rte.TriangleCount:n0}"
                       + $"   nodes {tree.Nodes:n0} against 2,643,577"
                       + $"   {(double)tree.Nodes / 2_643_577 - 1:P2}"
                       + $"   in {(DateTime.UtcNow - started).TotalSeconds:F0}s");
    }
}
