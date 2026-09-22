using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// Stage 4: the clusters a region is cut into, ported from visbuilder.dll's
/// <c>180032d80</c>.
///
/// <para>There is no closed form for the count, and the reason is structural
/// rather than a gap in the reading: a cluster is born for EVERY open voxel of
/// every region the map encloses, and the count only comes down because
/// <c>1800337a0</c> then merges the cheapest pairs by
/// <see cref="VisMergeCost"/> until 32 remain or the cheapest merge left costs
/// more than <see cref="MergeThreshold"/>. A region's mask holds at most 64
/// voxels, so the merge only ever runs on a region with more than 32 open, and
/// everything smaller is born and kept.</para>
/// </summary>
public static class VisClusters
{
    /// <summary>A leaf's voxel is a quarter of its side (<c>DAT_18017f0f0</c>).</summary>
    public const float SubCell = 0.25f;

    /// <summary>Slack on a candidate box's minimum (<c>DAT_18017f1e0</c>).</summary>
    public const float CandidateSlackMin = -0.1f;

    /// <summary>Slack on a candidate box's maximum (<c>DAT_18017f0e4</c>).</summary>
    public const float CandidateSlackMax = 0.1f;

    /// <summary>What a merge may cost before the loop gives up (<c>DAT_18017f18c</c>).</summary>
    public const float MergeThreshold = 20f;

    /// <summary>Clusters a region is merged down towards, the <c>0x20</c> argument.</summary>
    public const int MergeTarget = 32;

    /// <summary>One cluster, in the shape the 0x58 byte record holds it.</summary>
    /// <param name="Region">Which region it was born in.</param>
    /// <param name="Mask">Its voxels within that region's leaf.</param>
    /// <param name="Leaf">The leaf index the mask is relative to.</param>
    /// <param name="Mins">Its box.</param>
    /// <param name="Maxs">Its box.</param>
    /// <param name="Voxels">Voxels accumulated, which a merge sums.</param>
    /// <param name="VoxelSize">The leaf's sub-cell in world units, at <c>+0x50</c>.</param>
    /// <param name="Tag">The candidate box it came from, at <c>+0x52</c>.</param>
    public sealed record Cluster(
        int Region, ulong Mask, int Leaf,
        Vector3 Mins, Vector3 Maxs, int Voxels, int VoxelSize, short Tag);

    /// <summary>
    /// Every cluster born, before any merging. This is the number the compile
    /// would print as "N clusters generated" if nothing merged, and it is the
    /// number it does print for every region with 32 or fewer open voxels.
    /// </summary>
    public static List<Cluster> Born(
        VisVoxelizer.Octree tree, VisRegions.Result regions,
        IReadOnlyList<VisOutside.Status> status)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(status);

        var found = new List<Cluster>();
        for (var i = 0; i < regions.Regions.Count; i++)
        {
            if (status[i] != VisOutside.Status.Inside)
                continue;

            var region = regions.Regions[i];
            var leaf = regions.Leaves[region.Leaf];
            var side = tree.LeafSize * (1 << leaf.Level);
            var corner = tree.Origin + new Vector3(leaf.Cell.X, leaf.Cell.Y, leaf.Cell.Z) * side;
            var voxel = side * SubCell;

            for (var bit = 0; bit < 64; bit++)
            {
                if ((region.Open & (1UL << bit)) == 0)
                    continue;
                var at = corner + new Vector3(bit & 3, (bit >> 2) & 3, (bit >> 4) & 3) * voxel;
                found.Add(new Cluster(i, 1UL << bit, region.Leaf,
                                      at, at + new Vector3(voxel, voxel, voxel),
                                      1, (int)voxel, 0));
            }
        }
        return found;
    }

    /// <summary>
    /// How many clusters a region's own set of them would merge down to, given a
    /// cost for each pair. Regions at or below <see cref="MergeTarget"/> never
    /// merge at all, which is the common case and needs no cost.
    /// </summary>
    public static int MergedCount(int born, Func<int, int, float> cost)
    {
        ArgumentNullException.ThrowIfNull(cost);
        if (born <= MergeTarget)
            return born;

        var alive = new bool[born];
        Array.Fill(alive, true);
        var count = born;
        while (count > MergeTarget)
        {
            var bestCost = float.MaxValue;
            var (left, right) = (-1, -1);
            for (var a = 0; a < born; a++)
            {
                if (!alive[a])
                    continue;
                for (var b = a + 1; b < born; b++)
                {
                    if (!alive[b])
                        continue;
                    var at = cost(a, b);
                    if (at >= bestCost)
                        continue;
                    bestCost = at;
                    (left, right) = (a, b);
                }
            }
            if (left < 0 || bestCost > MergeThreshold)
                break;
            alive[right] = false;
            count--;
        }
        return count;
    }
}
