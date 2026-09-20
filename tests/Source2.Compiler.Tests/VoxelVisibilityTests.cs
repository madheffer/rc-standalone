using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The VXVS codec against Valve's own compiles, on every installed map.
///
/// <para>Round-tripping bytes would prove nothing if the reader kept the raw
/// words, so it does not: <see cref="VisNode"/> and <see cref="VisRegion"/> split
/// their two 64 bits into named fields and rebuild them from those. A byte-exact
/// re-encode therefore says every bit is claimed by a field we named, which is
/// the property a writer needs and the one a memcpy would fake.</para>
/// </summary>
public sealed class VoxelVisibilityTests(ITestOutputHelper output)
{
    [Fact]
    public void ReEncodesEveryMapByteForByte()
    {
        var maps = 0;
        long bytes = 0;
        foreach (var specimen in VisFixtures.All())
        {
            var written = specimen.Vis.WriteVxvs();
            Assert.Equal(specimen.Vxvs.Length, written.Length);
            if (!written.AsSpan().SequenceEqual(specimen.Vxvs))
            {
                var at = FirstDifference(specimen.Vxvs, written);
                Assert.Fail($"{specimen.Map}: VXVS differs at byte {at} of {written.Length} "
                          + $"(valve {specimen.Vxvs[at]:x2}, ours {written[at]:x2})");
            }
            maps++;
            bytes += written.Length;
        }
        VisFixtures.RequireCorpus(maps);
        output.WriteLine($"{maps} maps, {bytes / 1024 / 1024} MB of VXVS re-encoded byte for byte");
    }

    [Fact]
    public void DerivesValvesOwnIndexFromTheArraysAlone()
    {
        var maps = 0;
        foreach (var specimen in VisFixtures.All())
        {
            var ours = VoxelVisibility.Layout.For(specimen.Vis);
            Assert.Equal(specimen.ValveLayout, ours);
            Assert.Equal(specimen.Vxvs.Length, ours.TotalBytes);
            maps++;
        }
        VisFixtures.RequireCorpus(maps);
        output.WriteLine($"{maps} maps: all six offsets are prefix sums of the counts");
    }

    [Fact]
    public void PVSMatrixIsRowsTimesStride()
    {
        var maps = 0;
        foreach (var specimen in VisFixtures.All())
        {
            var vis = specimen.Vis;
            Assert.Equal(VoxelVisibility.BytesPerCluster(vis.BaseClusterCount), vis.PVSBytesPerCluster);
            Assert.Equal((int)(vis.PVSRowCount * vis.PVSBytesPerCluster), vis.VisBlocks.Length);
            maps++;
        }
        VisFixtures.RequireCorpus(maps);
        output.WriteLine($"{maps} maps: PVS is rows * roundUp4(ceil(clusters/8))");
    }

    /// <summary>
    /// The structural invariants a generated vis file would have to satisfy.
    /// Every index has to land inside the array it addresses, or the game walks
    /// off the end of one of these arrays at run time.
    /// </summary>
    [Fact]
    public void EveryIndexPointsInsideTheArrayItAddresses()
    {
        var maps = 0;
        foreach (var specimen in VisFixtures.All())
        {
            var vis = specimen.Vis;
            var where = specimen.Map;

            for (var i = 0; i < vis.Nodes.Length; i++)
            {
                var node = vis.Nodes[i];
                if (node.IsLeaf)
                {
                    Assert.True(node.Offset + node.RegionCount <= (uint)vis.Regions.Length,
                        $"{where}: leaf {i} runs off the region array");
                }
                else
                {
                    Assert.True(node.Offset + 8 <= (uint)vis.Nodes.Length,
                        $"{where}: branch {i} child group runs off the node array");
                }

                if (node.HasEnclosedList)
                {
                    Assert.True(node.EnclosedListIndex < (uint)vis.EnclosedClusterList.Length,
                        $"{where}: node {i} enclosed list index out of range");
                }
            }

            foreach (var region in vis.Regions)
            {
                Assert.True(region.MaskIndex < (uint)vis.Masks.Length,
                    $"{where}: region mask index out of range");
            }

            foreach (var range in vis.EnclosedClusterList)
            {
                Assert.True(range.Offset >= 0 && range.Offset + range.Count <= vis.EnclosedClusters.Length,
                    $"{where}: enclosed range runs off the cluster array");
            }

            foreach (var cluster in vis.EnclosedClusters)
            {
                Assert.True(cluster < vis.BaseClusterCount,
                    $"{where}: enclosed cluster {cluster} past {vis.BaseClusterCount}");
            }

            maps++;
        }
        VisFixtures.RequireCorpus(maps);
        output.WriteLine($"{maps} maps: every node, region, mask and cluster index is in range");
    }

    /// <summary>
    /// The octree's shape, which a builder has to reproduce: node 0 is the root
    /// over the stated bounds, branches own eight consecutive children, and the
    /// tree covers every node exactly once.
    /// </summary>
    [Fact]
    public void OctreeIsASingleTreeRootedAtNodeZero()
    {
        var maps = 0;
        foreach (var specimen in VisFixtures.All())
        {
            var vis = specimen.Vis;
            if (vis.Nodes.Length == 0)
                continue;

            var reached = new bool[vis.Nodes.Length];
            var stack = new Stack<uint>();
            stack.Push(0);
            reached[0] = true;
            var visited = 1;

            while (stack.Count > 0)
            {
                var node = vis.Nodes[stack.Pop()];
                if (node.IsLeaf)
                    continue;
                for (var octant = 0u; octant < 8; octant++)
                {
                    var child = node.Offset + octant;
                    Assert.False(reached[child], $"{specimen.Map}: node {child} has two parents");
                    reached[child] = true;
                    visited++;
                    stack.Push(child);
                }
            }

            Assert.Equal(vis.Nodes.Length, visited);
            Assert.True(vis.MinBounds.X < vis.MaxBounds.X, $"{specimen.Map}: empty bounds");
            maps++;
        }
        VisFixtures.RequireCorpus(maps);
        output.WriteLine($"{maps} maps: one tree, eight children per branch, no orphans");
    }

    private static int FirstDifference(byte[] left, byte[] right)
    {
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
                return i;
        }
        return -1;
    }

}
