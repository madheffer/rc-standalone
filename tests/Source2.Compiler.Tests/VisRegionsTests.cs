using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Stage 3, the part of it that is established.
///
/// <para>There is no number from the compile to score this against yet: its
/// "Generated clusters for 10554 regions" is counted AFTER an outside detection
/// pass whose criterion is not known, and every candidate measured so far is out
/// by between -29% and +323%. See docs/VIS.md. What is checked here is that the
/// regions are a correct partition of the octree's open space, which is what the
/// stages above them will consume either way.</para>
/// </summary>
public class VisRegionsTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("s2c_lighting", "ze_hold_em_p")]
    [InlineData("s2c_rc_probe", "cardtest")]
    [InlineData("s2c_rc_probe", "probe01")]
    public void RegionsPartitionTheOpenSpaceOfEveryLeaf(string addon, string map)
    {
        if (VisFixtures.RayTraceScene(addon, map) is not var (rte, valve))
            return;

        var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
        var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                 / VisVoxelizer.VoxelsPerLeaf;
        var result = VisRegions.Build(tree, side);

        Assert.Equal(tree.Leaves, result.Leaves.Count);
        Assert.Equal(tree.LeafMasks.Count, result.Leaves.Count(l => l.Solid != 0));

        var open = new Dictionary<int, ulong>();
        foreach (var region in result.Regions)
        {
            Assert.NotEqual(0UL, region.Open);
            var already = open.GetValueOrDefault(region.Leaf);
            Assert.True((already & region.Open) == 0,
                $"{map}: two regions of leaf {region.Leaf} claim the same voxel");
            open[region.Leaf] = already | region.Open;
        }
        foreach (var (leaf, covered) in open)
            Assert.True(covered == ~result.Leaves[leaf].Solid,
                $"{map}: leaf {leaf}'s regions do not cover its open voxels exactly");

        // A coarse leaf holds geometry only in one case, and it is a real one:
        // the child was empty under the narrow mask, the compile retried it with
        // the wide one and stopped there, so the only thing in it is
        // CoarseOccupancyOnly. A map without any such triangle must have none.
        var coarseOnly = Enumerable.Range(0, rte.TriangleCount)
            .Count(i => rte.Traced(i)
                     && (rte.Flags(i) & RayTraceEnvironment.CoarseOccupancyOnly) != 0);
        var bigWithGeometry = result.Leaves.Count(l => l.Level > 0 && l.Solid != 0);
        Assert.True(coarseOnly > 0 || bigWithGeometry == 0,
            $"{map}: {bigWithGeometry} coarse leaves hold geometry and the scene has no"
          + " coarse-only triangle for them to hold");

        output.WriteLine($"{map,-16} {result.Leaves.Count,7:n0} leaves, {result.Regions.Count,7:n0} regions, "
            + $"{result.Leaves.Count(l => l.Solid != 0),7:n0} of them holding geometry"
            + $" ({bigWithGeometry:n0} coarse, from {coarseOnly:n0} coarse-only triangle(s))");
    }
}
