using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Stage 4, scored against the compile's "N clusters generated".
///
/// <para>A cluster is born per open voxel of every enclosed region, and only a
/// region with more than 32 of them merges at all, so the count is mostly just a
/// sum of popcounts. What the merge takes off the top is measured here rather
/// than assumed.</para>
/// </summary>
public class VisClustersTests(ITestOutputHelper output)
{
    private sealed record Specimen(string Addon, string Map, int Regions, int Clusters);

    private static readonly Specimen[] Maps =
    [
        new("s2c_lighting", "ze_hold_em_p", 10_554, 93_354),
        new("s2c_rc_probe", "cardtest", 7_416, 81_835),
        new("s2c_rc_probe", "probe01", 7_316, 81_707),
    ];

    /// <param name="Tolerance">ze_hold_em_p is exact and pinned at zero. The two
    /// probe maps sit about 2.5% under, because a region there really does merge
    /// to more than one cluster and the model here gives it one.</param>
    private static readonly (int Map, double Tolerance)[] Counted =
    [
        (0, 0), (1, 0.006), (2, 0.003),
    ];

    /// <summary>The count the compile sums into "N clusters generated".</summary>
    [Fact]
    public void TheClusterCountLandsOnTheCompilesOwn()
    {
        foreach (var (which, tolerance) in Counted)
        {
            var specimen = Maps[which];
            if (VisFixtures.RayTraceScene(specimen.Addon, specimen.Map) is not var (rte, valve))
                continue;

            var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
            var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                     / VisVoxelizer.VoxelsPerLeaf;
            var regions = VisRegions.Build(tree, side);
            var inside = VisOutside.Detect(tree, regions, rte, valve.GridSize);
            var started = DateTime.UtcNow;
            var clusters = VisClusters.Count(tree, regions, inside.Regions, rte);
            var uniform = VisClusters.Uniform(regions, inside.Regions);

            var error = (double)clusters / specimen.Clusters - 1;
            output.WriteLine($"{specimen.Map,-16} clusters {clusters,8:n0}"
                           + $"  compile {specimen.Clusters,8:n0}  {error,8:P2}"
                           + $"   (uniform model {uniform,8:n0},"
                           + $" {(double)uniform / specimen.Clusters - 1,7:P2};"
                           + $" sampled in {(DateTime.UtcNow - started).TotalSeconds:F0}s)");
            // The sampler and the shortcut are two routes to the same number and
            // they have to stay that way: the shortcut is a claim about what the
            // merge always does, and this is the only thing that tests it.
            Assert.Equal(uniform, clusters);
            Assert.True(Math.Abs(error) <= tolerance,
                $"{specimen.Map}: {clusters:n0} clusters against the compile's "
              + $"{specimen.Clusters:n0}, {error:P2} off");
        }
    }

    [Fact]
    public void AMergeRunsPastTheTargetWhileItIsStillCheap()
    {
        // Eight clusters that all see alike and sit in a row: the guard keeps them
        // whole, and a set over the target collapses because nothing ever costs 20.
        Assert.Equal(8, VisClusters.MergedCount(8, (_, _) => 1f));
        Assert.Equal(1, VisClusters.MergedCount(40, (_, _) => 1f));

        // And a set that is over the target but expensive stops exactly at it.
        Assert.Equal(VisClusters.MergeTarget,
                     VisClusters.MergedCount(40, (_, _) => VisClusters.MergeThreshold));
    }

    [Fact]
    public void ClustersAreBornOnePerOpenVoxelOfAnEnclosedRegion()
    {
        foreach (var specimen in Maps)
        {
            if (VisFixtures.RayTraceScene(specimen.Addon, specimen.Map) is not var (rte, valve))
                continue;

            var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
            var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                     / VisVoxelizer.VoxelsPerLeaf;
            var regions = VisRegions.Build(tree, side);
            var inside = VisOutside.Detect(tree, regions, rte, valve.GridSize);
            var born = VisClusters.Born(tree, regions, inside.Regions);

            var big = 0;
            var counts = new Dictionary<int, int>();
            foreach (var group in born.GroupBy(c => c.Region))
            {
                counts[group.Key] = group.Count();
                if (group.Count() > VisClusters.MergeTarget)
                    big++;
            }

            var small = counts.Values.Where(n => n <= VisClusters.MergeTarget).Sum();
            var large = counts.Values.Where(n => n > VisClusters.MergeTarget).ToArray();
            output.WriteLine($"{specimen.Map,-16} regions at or under {VisClusters.MergeTarget}"
                + $" contribute {small,7:n0} unmerged; the other {large.Length:n0} start at"
                + $" {large.Sum(),7:n0} and must end at {specimen.Clusters - small,7:n0}"
                + $" ({(double)(specimen.Clusters - small) / Math.Max(large.Length, 1):F2} each)");

            var error = (double)born.Count / specimen.Clusters - 1;
            output.WriteLine($"{specimen.Map,-16} born {born.Count,8:n0}  compile {specimen.Clusters,8:n0}"
                + $"  {error,8:P2}   over {inside.Inside:n0} enclosed regions"
                + $" ({(double)born.Count / Math.Max(inside.Inside, 1):F2} each,"
                + $" {big:n0} of them over {VisClusters.MergeTarget})");
            // One voxel each, and nothing from a region the map does not enclose.
            // Those two are the whole of the birth rule, and they hold whatever the
            // merge below them turns out to do.
            Assert.All(born, c => Assert.Equal(1, System.Numerics.BitOperations.PopCount(c.Mask)));
            Assert.Equal(born.Count, counts.Values.Sum());
            Assert.Equal(inside.Inside, counts.Count);
            Assert.True(born.Count >= specimen.Clusters,
                $"{specimen.Map}: {born.Count:n0} born is under the compile's {specimen.Clusters:n0},"
              + " and a merge can only ever take the count down");
        }
    }

    /// <summary>
    /// How the enclosed regions are spread over their open-voxel counts.
    ///
    /// <para>The cluster count is <c>n</c> for a region with n open voxels at or
    /// under the target and ONE above it, so the function has a cliff of 32 at
    /// the boundary. That makes the total exquisitely sensitive to a region whose
    /// mask is a voxel out, and this is what says whether a map has any.</para>
    /// </summary>
    [Theory]
    [InlineData("s2c_lighting", "ze_hold_em_p", 93_354)]
    [InlineData("s2c_rc_probe", "cardtest", 81_835)]
    public void WhereTheRegionsSitAroundTheMergeTarget(string addon, string map, int clusters)
    {
        if (VisFixtures.RayTraceScene(addon, map) is not var (rte, valve))
            return;

        var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
        var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                 / VisVoxelizer.VoxelsPerLeaf;
        var regions = VisRegions.Build(tree, side);
        var inside = VisOutside.Detect(tree, regions, rte, valve.GridSize);

        var open = new List<int>();
        int coarseRegions = 0, coarseClusters = 0;
        for (var i = 0; i < regions.Regions.Count; i++)
        {
            if (inside.Regions[i] != VisOutside.Status.Inside)
                continue;
            var n = System.Numerics.BitOperations.PopCount(regions.Regions[i].Open);
            open.Add(n);
            if (regions.Leaves[regions.Regions[i].Leaf].Level <= 0)
                continue;
            coarseRegions++;
            coarseClusters += n > VisClusters.MergeTarget ? 1 : n;
        }
        output.WriteLine($"   from COARSE leaves: {coarseRegions:n0} regions"
                       + $" contributing {coarseClusters:n0} clusters");

        var ours = open.Sum(n => n > VisClusters.MergeTarget ? 1 : n);
        output.WriteLine($"{map}  {open.Count:n0} enclosed regions, {ours:n0} clusters"
                       + $" against {clusters:n0}, short by {clusters - ours:n0}");
        // The shipped file carries the deduplicated mask table the compile built,
        // so a mask of ours that is not in it is a mask Valve never produced.
        var theirs = valve.Masks.ToHashSet();
        var mine = new HashSet<ulong>();
        for (var i = 0; i < regions.Regions.Count; i++)
            if (inside.Regions[i] == VisOutside.Status.Inside)
                mine.Add(regions.Regions[i].Open);
        output.WriteLine($"   masks: {mine.Count} distinct of ours, {theirs.Count} in the file,"
                       + $" {mine.Count(m => !theirs.Contains(m))} of ours it never produced");

        foreach (var band in new[] { (1, 8), (9, 16), (17, 24), (25, 31), (32, 32), (33, 40), (41, 56), (57, 64) })
            output.WriteLine($"   {band.Item1,3} to {band.Item2,3} open: {open.Count(n => n >= band.Item1 && n <= band.Item2),7:n0}"
                           + $"  contributing {open.Where(n => n >= band.Item1 && n <= band.Item2).Sum(n => n > VisClusters.MergeTarget ? 1 : n),8:n0}");
    }
}
