using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// Point queries against a compiled vis file: which cluster a position is in, and
/// whether one position can see another.
///
/// <para>This exists to COMPARE compiles. Two vis builds of the same map do not
/// agree on cluster numbering or even cluster count, so their PVS matrices cannot
/// be diffed bit for bit. "Can a player at A see a point at B" is the same question
/// in both, and it is the question the game actually asks, so that is the axis
/// <c>vis-diff</c> measures an error margin on.</para>
///
/// <para>The traversal matches the game's: the octree is descended by octant, and a
/// leaf's regions are filtered by a 4x4x4 occupancy mask so one leaf can hold
/// several clusters. The mask layout is the one ValveResourceFormat's reader
/// documents, re-derived here so the compiler does not depend on the viewer.</para>
/// </summary>
public sealed partial class VoxelVisibilityQuery(VoxelVisibility vis)
{
    // A leaf is split twice more for masking, so the bit for a position is the
    // level-1 octant's base plus the level-2 octant's offset inside it.
    private static ReadOnlySpan<byte> SubGridLevel1 => [0, 2, 8, 10, 32, 34, 40, 42];
    private static ReadOnlySpan<byte> SubGridLevel2 => [0, 1, 4, 5, 16, 17, 20, 21];

    /// <summary>The data being queried.</summary>
    public VoxelVisibility Vis { get; } = vis;

    /// <summary>
    /// The leaf node containing <paramref name="point"/>, narrowing
    /// <paramref name="min"/> and <paramref name="max"/> to that leaf's bounds.
    /// Returns -1 when the point is outside the octree.
    /// </summary>
    public int FindLeaf(Vector3 point, ref Vector3 min, ref Vector3 max)
    {
        if (Vis.Nodes.Length == 0)
            return -1;
        if (point.X < min.X || point.Y < min.Y || point.Z < min.Z
         || point.X > max.X || point.Y > max.Y || point.Z > max.Z)
            return -1;
        if (Vis.Nodes[0].IsLeaf)
            return 0;

        var index = 0;
        while (true)
        {
            var node = Vis.Nodes[index];
            if (node.IsLeaf)
                return index;

            var mid = (min + max) * 0.5f;
            var octant = 0;
            if (mid.X < point.X) octant |= 1;
            if (mid.Y < point.Y) octant |= 2;
            if (mid.Z < point.Z) octant |= 4;

            if ((octant & 1) != 0) min.X = mid.X; else max.X = mid.X;
            if ((octant & 2) != 0) min.Y = mid.Y; else max.Y = mid.Y;
            if ((octant & 4) != 0) min.Z = mid.Z; else max.Z = mid.Z;

            index = (int)node.Offset + octant;
            if ((uint)index >= (uint)Vis.Nodes.Length)
                return -1;
        }
    }

    /// <summary>The single mask bit a position occupies within a leaf's 4x4x4 grid.</summary>
    public static ulong SpatialMask(Vector3 point, Vector3 min, Vector3 max)
    {
        var mid = (min + max) * 0.5f;
        var first = 0;
        if (mid.X < point.X) first |= 1;
        if (mid.Y < point.Y) first |= 2;
        if (mid.Z < point.Z) first |= 4;

        if ((first & 1) != 0) min.X = mid.X; else max.X = mid.X;
        if ((first & 2) != 0) min.Y = mid.Y; else max.Y = mid.Y;
        if ((first & 4) != 0) min.Z = mid.Z; else max.Z = mid.Z;

        var mid2 = (min + max) * 0.5f;
        var second = 0;
        if (mid2.X < point.X) second |= 1;
        if (mid2.Y < point.Y) second |= 2;
        if (mid2.Z < point.Z) second |= 4;

        return 1UL << (SubGridLevel1[first] + SubGridLevel2[second]);
    }

    /// <summary>
    /// The cluster containing <paramref name="point"/>, or -1 when the point is
    /// outside the octree or in a leaf that owns no matching region. A caller
    /// comparing two compiles should treat -1 as "no opinion" rather than as a
    /// cluster, because the two builds disagree about where solid space is.
    /// </summary>
    public int ClusterAt(Vector3 point)
    {
        var min = Vis.MinBounds;
        var max = Vis.MaxBounds;
        var leaf = FindLeaf(point, ref min, ref max);
        if (leaf < 0)
            return -1;

        var node = Vis.Nodes[leaf];
        if (node.RegionCount == 0)
            return -1;

        var mask = SpatialMask(point, min, max);
        for (uint r = 0; r < node.RegionCount; r++)
        {
            var index = node.Offset + r;
            if (index >= (uint)Vis.Regions.Length)
                break;
            var region = Vis.Regions[index];
            if ((mask & Vis.Masks[region.MaskIndex]) != 0)
                return region.ClusterId;
        }
        return -1;
    }

    /// <summary>
    /// Whether a viewer at <paramref name="from"/> can see <paramref name="to"/>,
    /// or null when either point has no cluster in this build.
    /// </summary>
    public bool? CanSee(Vector3 from, Vector3 to)
    {
        var a = ClusterAt(from);
        var b = ClusterAt(to);
        if (a < 0 || b < 0 || a >= Vis.BaseClusterCount || b >= Vis.BaseClusterCount)
            return null;
        return Vis.CanSee((uint)a, (uint)b);
    }

    /// <summary>
    /// Mean fraction of clusters visible from a cluster, over the whole PVS matrix.
    /// This is what vis is FOR: a lower number is less to draw, so it is the
    /// quality axis a faster builder must not regress.
    /// </summary>
    public double MeanVisibleFraction()
    {
        var clusters = Vis.BaseClusterCount;
        if (clusters == 0)
            return 0;

        // A row is padded out to a multiple of four bytes, so the tail bits are
        // not clusters. Counting them made a two-cluster skybox report 16.
        var whole = (int)(clusters / 8);
        var tail = (byte)((1 << (int)(clusters % 8)) - 1);

        long set = 0;
        for (var row = 0u; row < clusters; row++)
        {
            var start = (int)(row * Vis.PVSBytesPerCluster);
            for (var i = 0; i < whole; i++)
                set += BitOperations.PopCount((uint)Vis.VisBlocks[start + i]);
            if (tail != 0)
                set += BitOperations.PopCount((uint)(Vis.VisBlocks[start + whole] & tail));
        }
        return (double)set / clusters / clusters;
    }
}
