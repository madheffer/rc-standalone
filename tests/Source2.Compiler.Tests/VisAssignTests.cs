using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Cluster assignment, pinned to what the tail of
/// <c>CVoxelSampler3::MergeInsideRegions</c> does with a cluster rather than to
/// any map's count.
/// </summary>
public class VisAssignTests
{
    private static VisClusterSet.Set SetOf(params (ulong Mask, int Leaf)[][] clusters)
    {
        var set = new VisClusterSet.Set();
        foreach (var voxels in clusters)
        {
            var cluster = new VisMerge.Cluster();
            cluster.Voxels.AddRange(voxels);
            set.Clusters.Add(cluster);
        }
        return set;
    }

    /// <summary>
    /// A cluster's pairs scatter into the octree leaf each one names, stamped with a
    /// running cluster index that spans the sets rather than restarting.
    /// </summary>
    [Fact]
    public void EveryClusterPairLandsInTheLeafItNames()
    {
        var a = SetOf([(0b1101UL, 0), (0b10UL, 2)], [(0xFFUL, 0)]);
        var b = SetOf([(0b11UL, 1)]);

        var result = VisAssign.Run([a, b], 3, [], _ => true);

        Assert.Equal(3, result.Clusters);
        Assert.Equal(4, result.Regions);

        // Leaf 0 owns cluster 0 and cluster 1; leaf 2 owns cluster 0 only.
        Assert.Equal(2, result.Counts[0]);
        Assert.Equal(1, result.Counts[1]);
        Assert.Equal(1, result.Counts[2]);

        var first = result.Entries[result.Offsets[0]];
        Assert.Equal(0, first.Cluster);
        Assert.Equal(0, first.Leaf);
        Assert.Equal(VisVisibility.Open, first.Kind);
        Assert.Equal(0b1101UL, first.Cells);

        // The third set's cluster is index 2, not index 0 again.
        Assert.Equal(2, result.Entries[result.Offsets[1]].Cluster);
    }

    /// <summary>
    /// The octree leaf a record came from is packed above the kind bits, so a cluster
    /// record round trips both halves.
    /// </summary>
    [Fact]
    public void TheLeafAndKindSharePackedWord()
    {
        var result = VisAssign.Run([SetOf([(1UL, 300)])], 512, [], _ => true);
        var entry = result.Entries[0];

        Assert.Equal(300 << 2, entry.Packed);
        Assert.Equal(300, entry.Leaf);
        Assert.Equal(VisVisibility.Open, entry.Kind);
    }

    /// <summary>
    /// The second pass keeps the compaction's blocking and skipped records and
    /// drops its open ones, because the first pass has just rebuilt those with
    /// real cluster ids on them.
    /// </summary>
    [Fact]
    public void OnlyBlockingAndSkippedRecordsSurviveTheCompaction()
    {
        VisVisibility.Entry[] compacted =
        [
            new(0, (0 << 2) | VisVisibility.Open, 0xF0UL),
            new(0, (0 << 2) | VisVisibility.Blocking, 0x0FUL),
            new(0, (1 << 2) | VisVisibility.Skipped, 0xAAUL),
        ];

        var result = VisAssign.Run([SetOf([(1UL, 0)])], 2, compacted, _ => true);

        Assert.Equal(3, result.Regions);
        var leaf0 = result.Entries.AsSpan(result.Offsets[0], result.Counts[0]).ToArray();
        Assert.Equal([VisVisibility.Open, VisVisibility.Blocking],
                     leaf0.Select(e => e.Kind));
        Assert.DoesNotContain(result.Entries, e => e.Kind == VisVisibility.Open && e.Cells == 0xF0UL);
        Assert.Equal(VisVisibility.Skipped, result.Entries[result.Offsets[1]].Kind);
    }

    /// <summary>
    /// A leaf that is no longer kept contributes nothing, and the offsets stay
    /// a running total over the ones that are.
    /// </summary>
    [Fact]
    public void ADroppedLeafContributesNothingAndDoesNotShiftTheRest()
    {
        var set = SetOf([(1UL, 0)], [(2UL, 1)], [(4UL, 2)]);
        var result = VisAssign.Run([set], 3, [], leaf => leaf != 1);

        Assert.Equal(2, result.Regions);
        Assert.Equal(0, result.Offsets[0]);
        Assert.Equal(-1, result.Offsets[1]);
        Assert.Equal(1, result.Offsets[2]);
        Assert.Equal(0, result.Counts[1]);
    }

    /// <summary>
    /// The compaction hands assignment THREE records per leaf, not one, and the
    /// two non-open ones are most of what the stage carries: 92,640 of
    /// ze_hold_em_p's 103,358. Each is emitted only when its own mask came out
    /// non-empty, and all three carry the leaf in the same packed word.
    /// </summary>
    [Fact]
    public void TheCompactionEmitsTheEnclosedOutsideAndSolidUnionsPerLeaf()
    {
        // Leaf 0: some enclosed, some outside, and solid voxels. Leaf 1: outside
        // only, no solid. Leaf 2: solid only, and no region at all.
        var leaves = new List<VisRegions.Leaf>
        {
            new(0, (0, 0, 0), 0x00F0UL),
            new(0, (1, 0, 0), 0UL),
            new(0, (2, 0, 0), 0xFF00UL),
        };
        var regions = new VisRegions.Result(leaves,
        [
            new VisRegions.Region(0, 0x0001UL),
            new VisRegions.Region(0, 0x0002UL),
            new VisRegions.Region(1, 0x0004UL),
        ]);

        var collapsed = VisRegions.Collapse(regions,
        [
            VisOutside.Status.Inside,
            VisOutside.Status.Outside,
            VisOutside.Status.Outside,
        ]);

        Assert.Equal(
        [
            (0, VisVisibility.Open, 0x0001UL),
            (0, VisVisibility.Blocking, 0x0002UL),
            (0, VisVisibility.Skipped, 0x00F0UL),
            (1, VisVisibility.Blocking, 0x0004UL),
            (2, VisVisibility.Skipped, 0xFF00UL),
        ], collapsed.Select(e => (e.Leaf, e.Kind, e.Cells)));

        // And the enclosed half of it is exactly what Compact returns.
        var compact = VisRegions.Compact(regions,
        [
            VisOutside.Status.Inside,
            VisOutside.Status.Outside,
            VisOutside.Status.Outside,
        ]);
        Assert.Equal(collapsed.Count(e => e.Kind == VisVisibility.Open), compact.Regions.Count);
    }

    /// <summary>
    /// The whole point of the stage: what it produces is what the PVS walk reads,
    /// so a line through an assigned leaf finds that leaf's cluster.
    /// </summary>
    [Fact]
    public void TheWalkFindsTheClusterAssignmentPutThere()
    {
        var result = VisAssign.Run([SetOf([(ulong.MaxValue, 0)])], 1, [], _ => true);

        // One leaf covering the root, pointing at leaf 0's single entry.
        VisVisibility.Node[] nodes = [new((uint)((result.Offsets[0] << 1) | 1),
                                          (ushort)result.Counts[0])];

        var crossed = VisVisibility.Walk(
            nodes, result.Entries, Vector3.Zero, 16f,
            new Vector3(2f, 2f, -8f), new Vector3(0f, 0f, 32f), throughBlockers: true);

        Assert.Equal([0], crossed);
    }
}
