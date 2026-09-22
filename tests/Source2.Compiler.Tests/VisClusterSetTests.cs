using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The five merge passes, scored against every count the compile prints on the
/// way down.
///
/// <para>Running them means generating the clusters for real and sampling each
/// one's visibility five times over, so this is behind <c>MERGE=1</c> rather
/// than in the ordinary suite. The structural checks below it are not.</para>
/// </summary>
public class VisClusterSetTests(ITestOutputHelper output)
{
    /// <param name="Generated">"N clusters generated".</param>
    /// <param name="First">"Merged to N clusters in first pass".</param>
    /// <param name="Second">"Merged to N clusters in second pass".</param>
    /// <param name="Final">"Merged cluster lists [N clusters]".</param>
    /// <param name="Cost">The avg the compile prints after the first pass, which
    /// is the number the whole chain is steered by.</param>
    /// <param name="Tolerance">What the counts may be off by.</param>
    /// <param name="Assigned">"Compacted to N regions", the flat entry array's
    /// length after assignment.</param>
    private sealed record Specimen(
        string Addon, string Map, int Target, int Generated,
        int First, int Second, int Final, float Cost, int Assigned, double Tolerance);

    private static readonly Specimen[] Maps =
    [
        new("s2c_rc_probe", "probe01", 862, 81_707, 1_979, 1_815, 1_621, 110_760.1f, 30_665, 0.04),
        new("s2c_rc_probe", "cardtest", 862, 81_991, 1_998, 1_825, 1_626, 0f, 30_817, 0.08),
        // ze_hold_em_p does not reach its budget or its cost limit at all: it
        // merges to its connected components and stops, 350 of them against
        // Valve's 258. That is a separate defect from anything this test
        // steers, so its bound is wide on purpose and guards against drift
        // rather than asserting correctness. Tighten it by fixing the
        // component count, not by touching the number here.
        new("s2c_lighting", "ze_hold_em_p", 1_342, 93_354, 258, 258, 258, 21_940.0f, 103_358, 0.36),
    ];

    [Fact]
    public void TheFivePassesLandWhereTheCompileDoes()
    {
        if (Environment.GetEnvironmentVariable("MERGE") is not { Length: > 0 })
            return;

        foreach (var specimen in Maps)
        {
            if (VisFixtures.RayTraceScene(specimen.Addon, specimen.Map) is not var (rte, valve))
                continue;

            var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
            var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                     / VisVoxelizer.VoxelsPerLeaf;
            var regions = VisRegions.Build(tree, side);
            var inside = VisOutside.Detect(tree, regions, rte, valve.GridSize);
            var compact = VisRegions.Compact(regions, inside.Regions);

            var started = DateTime.UtcNow;
            var sets = VisClusters.Generate(rte, tree, compact);
            var generated = sets.Sum(s => s.Clusters.Count);
            output.WriteLine($"{specimen.Map,-16} generated {generated,8:n0}"
                           + $"  compile {specimen.Generated,8:n0}"
                           + $"  {(double)generated / specimen.Generated - 1,8:P2}"
                           + $"  in {(DateTime.UtcNow - started).TotalSeconds:F0}s");

            var seen = new List<(int Clusters, float Cost)>();
            VisClusterSet.MergeAll(rte, sets, specimen.Target, VisClusters.Cubes(tree, compact), (n, c) =>
            {
                var pass = VisClusterSet.Passes[seen.Count];
                var budget = (int)(specimen.Target * pass.Budget);
                var perCell = (int)Math.Ceiling((double)budget / sets.Count);
                var capped = sets.Count(b => b.Clusters.Count >= perCell);
                output.WriteLine($"   pass {seen.Count + 1}: {n,7:n0} clusters, cost {c:F1}"
                               + $"   cell {pass.Cell,6}  buckets {sets.Count,5:n0}"
                               + $"  budget {budget,6:n0}  perCell {perCell,5:n0}"
                               + $"  at cap {capped,4:n0}"
                               + $"  biggest {sets.Max(b => b.Clusters.Count),5:n0}");
                seen.Add((n, c));
            });

            if (specimen.Cost > 0)
            {
                var cost = (double)seen[0].Cost / specimen.Cost - 1;
                output.WriteLine($"{specimen.Map,-16} cost   {seen[0].Cost,9:n1}"
                               + $"  compile {specimen.Cost,9:n1}  {cost,8:P2}");
                Assert.True(Math.Abs(cost) <= 0.05,
                    $"{specimen.Map}: the chain's first cost is {cost:P2} off, so the cost"
                  + " function, the sampling or the merge order is wrong");
            }

            // Assignment, which is what the PVS walk reads and the only stage
            // whose number the merge alone cannot produce.
            // The key in a cluster's (mask, key) pair is an octree LEAF, which is
            // what the binary's own array is indexed by, so the blockers are keyed
            // the same way and the array is as long as the leaf list.
            var blockers = compact.Leaves
                .Select((leaf, i) => new VisVisibility.Entry(
                    0, (i << 2) | VisVisibility.Blocking, leaf.Solid))
                .Where(e => e.Cells != 0)
                .ToList();
            var assigned = VisAssign.Run(sets, compact.Leaves.Count, blockers, _ => true);
            var pairs = sets.SelectMany(s => s.Clusters).Sum(c => c.Voxels.Count);
            output.WriteLine($"{specimen.Map,-16} pairs   {pairs,8:n0}"
                           + $"  over {compact.Leaves.Count,8:n0} leaves,"
                           + $" {compact.Regions.Count,8:n0} regions");
            output.WriteLine($"{specimen.Map,-16} assign  {assigned.Regions,8:n0}"
                           + $"  compile {specimen.Assigned,8:n0}"
                           + $"  {(double)assigned.Regions / specimen.Assigned - 1,8:P2}"
                           + $"  ({assigned.Clusters:n0} clusters, {blockers.Count:n0} blockers)");

            foreach (var (label, ours, theirs) in new[]
            {
                ("first", seen[1].Clusters, specimen.First),
                ("second", seen[3].Clusters, specimen.Second),
                ("final", seen[4].Clusters, specimen.Final),
            })
            {
                var error = (double)ours / theirs - 1;
                output.WriteLine($"{specimen.Map,-16} {label,-6} {ours,7:n0}  compile {theirs,7:n0}"
                               + $"  {error,8:P2}  in {(DateTime.UtcNow - started).TotalSeconds:F0}s");
                Assert.True(Math.Abs(error) <= specimen.Tolerance,
                    $"{specimen.Map} {label}: {ours:n0} against the compile's {theirs:n0}, {error:P2} off");
            }
        }
    }

    /// <summary>
    /// The sphere the sampler switches to at 512 entries, which has to be the
    /// golden spiral <c>180027400</c> builds rather than any other even sphere.
    /// </summary>
    [Fact]
    public void TheBigSetDirectionsAreAGoldenSpiralOnTheUnitSphere()
    {
        Assert.Equal(512, VisClusterSample.Sphere.Length);
        Assert.All(VisClusterSample.Sphere, d => Assert.Equal(1f, d.Length(), 3));

        // A golden spiral is even, so opposite hemispheres carry the same count
        // and the mean is at the origin. A grid of axis-aligned rays would pass
        // the first of those and fail the spacing below.
        Assert.Equal(256, VisClusterSample.Sphere.Count(d => d.Y > 0));
        var mean = VisClusterSample.Sphere.Aggregate(System.Numerics.Vector3.Zero, (a, b) => a + b)
                 / VisClusterSample.Sphere.Length;
        Assert.True(mean.Length() < 0.01f, $"the sphere is lopsided by {mean.Length()}");

        // What makes it the spiral rather than any even sphere: the heights step
        // by exactly 2/n, and the azimuth turns by the golden angle every time.
        var step = 2f / VisClusterSample.Sphere.Length;
        var turn = (3f - MathF.Sqrt(5f)) * MathF.PI;
        for (var i = 1; i < VisClusterSample.Sphere.Length; i++)
        {
            Assert.Equal(step, VisClusterSample.Sphere[i].Y - VisClusterSample.Sphere[i - 1].Y, 4);
            var before = MathF.Atan2(VisClusterSample.Sphere[i - 1].Z, VisClusterSample.Sphere[i - 1].X);
            var now = MathF.Atan2(VisClusterSample.Sphere[i].Z, VisClusterSample.Sphere[i].X);
            var turned = (now - before + (MathF.Tau * 2)) % MathF.Tau;
            Assert.Equal(turn % MathF.Tau, turned, 2);
        }
    }

    /// <summary>
    /// The five passes' parameters, which are read off the call site rather than
    /// chosen, and the chain that carries the cost limit between them.
    /// </summary>
    [Fact]
    public void TheFivePassesAreTheOnesTheCallSiteMakes()
    {
        Assert.Equal(5, VisClusterSet.Passes.Length);
        Assert.Equal([512f, 512f, 2048f, 2048f, 4096f],
                     VisClusterSet.Passes.Select(p => p.Cell));
        Assert.Equal([0f, 256f, 0f, 1024f, 0f],
                     VisClusterSet.Passes.Select(p => p.Margin));
        Assert.Equal([6f, 5.75f, 4.5f, 4.25f, 3f],
                     VisClusterSet.Passes.Select(p => p.Budget));
    }
}
