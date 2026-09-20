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

    /// <summary>A leaf that holds space, with the bounds the octree gives it.</summary>
    /// <param name="Node">Index into <see cref="VoxelVisibility.Nodes"/>.</param>
    /// <param name="Min">Leaf minimum corner.</param>
    /// <param name="Max">Leaf maximum corner.</param>
    /// <param name="Occupancy">Union of its regions' 4x4x4 masks.</param>
    public readonly record struct OccupiedLeaf(int Node, Vector3 Min, Vector3 Max, ulong Occupancy);

    private OccupiedLeaf[]? occupied;

    /// <summary>
    /// Every leaf that owns at least one region, with its bounds and the union of
    /// its regions' occupancy masks. Computed once and cached.
    /// </summary>
    public OccupiedLeaf[] OccupiedLeaves()
    {
        if (occupied is not null)
            return occupied;
        if (Vis.Nodes.Length == 0)
            return occupied = [];

        var found = new List<OccupiedLeaf>();
        var stack = new Stack<(uint Index, Vector3 Min, Vector3 Max)>();
        stack.Push((0, Vis.MinBounds, Vis.MaxBounds));

        while (stack.Count > 0)
        {
            var (index, min, max) = stack.Pop();
            if (index >= (uint)Vis.Nodes.Length)
                continue;
            var node = Vis.Nodes[index];

            if (node.IsLeaf)
            {
                if (node.RegionCount == 0)
                    continue;
                var union = 0UL;
                for (uint r = 0; r < node.RegionCount && node.Offset + r < (uint)Vis.Regions.Length; r++)
                    union |= Vis.Masks[Vis.Regions[node.Offset + r].MaskIndex];
                if (union != 0)
                    found.Add(new OccupiedLeaf((int)index, min, max, union));
                continue;
            }

            var mid = (min + max) * 0.5f;
            for (var octant = 0u; octant < 8; octant++)
            {
                var childMin = min;
                var childMax = max;
                if ((octant & 1) != 0) childMin.X = mid.X; else childMax.X = mid.X;
                if ((octant & 2) != 0) childMin.Y = mid.Y; else childMax.Y = mid.Y;
                if ((octant & 4) != 0) childMin.Z = mid.Z; else childMax.Z = mid.Z;
                stack.Push((node.Offset + octant, childMin, childMax));
            }
        }
        return occupied = [.. found];
    }

    /// <summary>
    /// Points drawn uniformly from the space this build says EXISTS, rather than
    /// from its bounding box.
    ///
    /// <para>Uniform box sampling is unusable here: on a real map 99.4% of the
    /// bounding box is solid or outside, so it wastes almost every draw and biases
    /// what survives toward large open volumes. Picking an occupied 4x4x4 cell
    /// with probability proportional to its volume gives the same uniform-over-
    /// space distribution with every draw landing in a cluster.</para>
    /// </summary>
    public Vector3[] SampleOccupiedPoints(int count, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var leaves = OccupiedLeaves();
        if (leaves.Length == 0 || count <= 0)
            return [];

        // Weight is occupied cells times cell volume, so a coarse leaf counts for
        // as much space as the many fine leaves covering the same volume.
        var cumulative = new double[leaves.Length];
        var total = 0.0;
        for (var i = 0; i < leaves.Length; i++)
        {
            var span = leaves[i].Max - leaves[i].Min;
            var cellVolume = (double)span.X * span.Y * span.Z / 64.0;
            total += BitOperations.PopCount(leaves[i].Occupancy) * cellVolume;
            cumulative[i] = total;
        }
        if (total <= 0)
            return [];

        var points = new Vector3[count];
        for (var n = 0; n < count; n++)
        {
            var pick = Array.BinarySearch(cumulative, random.NextDouble() * total);
            if (pick < 0)
                pick = ~pick;
            var leaf = leaves[Math.Min(pick, leaves.Length - 1)];

            // Choose one of the leaf's occupied cells, then a point inside it.
            var cells = BitOperations.PopCount(leaf.Occupancy);
            var wanted = random.Next(cells);
            var bits = leaf.Occupancy;
            for (var k = 0; k < wanted; k++)
                bits &= bits - 1;
            var bit = BitOperations.TrailingZeroCount(bits);

            var cell = (leaf.Max - leaf.Min) * 0.25f;
            points[n] = leaf.Min + new Vector3(
                (bit & 3) + (float)random.NextDouble(),
                (bit >> 2 & 3) + (float)random.NextDouble(),
                (bit >> 4 & 3) + (float)random.NextDouble()) * cell;
        }
        return points;
    }
}
