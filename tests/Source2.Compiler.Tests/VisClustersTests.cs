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
            var inside = VisOutside.Detect(tree, regions, rte.Mins, rte.Maxs, valve.GridSize);
            var born = VisClusters.Born(tree, regions, inside.Regions);

            var big = 0;
            var counts = new Dictionary<int, int>();
            foreach (var group in born.GroupBy(c => c.Region))
            {
                counts[group.Key] = group.Count();
                if (group.Count() > VisClusters.MergeTarget)
                    big++;
            }

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
}
