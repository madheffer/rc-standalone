using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// <c>FUN_180037200</c>: the sampler's final state as the VXVS the world
/// visibility resource carries. Leaves own the regions of their open records
/// (cluster, node, mask index); masks are the distinct cell masks ordered by
/// how many records use them, then by value; each cluster's PVS row is the
/// matrix row cut to the cluster count, followed by the sky and sun rows when
/// those stages produced them.
/// </summary>
public static class VisOutput
{
    /// <summary>Lists of this many clusters or more are not stored.</summary>
    public const int MaxEnclosed = 0x400;

    public static VoxelVisibility Build(VisPvs.State s, VisPvs.Matrix matrix, uint[]? sky, uint[]? sun,
                                        Vector3 minBounds, Vector3 maxBounds)
    {
        var masks = UniqueMasks(s);
        var maskIndex = new Dictionary<ulong, int>(masks.Length);
        for (var i = 0; i < masks.Length; i++)
            maskIndex[masks[i]] = i;

        var nodes = new VisNode[s.NodeWords.Length];
        var regions = new List<VisRegion>();
        for (var n = 0; n < nodes.Length; n++)
        {
            var word = s.NodeWords[n];
            if ((word & 1) == 0)
            {
                nodes[n] = new VisNode(false, word >> 1, 0, VisNode.NoEnclosedList);
                continue;
            }
            var start = regions.Count;
            var first = (int)(word >> 1);
            for (var k = 0; k < s.NodeCounts[n]; k++)
            {
                var e = s.Entries[first + k];
                if ((e.Packed & 3) != 0)
                    continue;
                regions.Add(new VisRegion((ushort)(e.Cluster & 0x7fff), (e.Packed & 2) != 0, (uint)n & 0xffffff,
                                          (uint)maskIndex[e.Cells]));
            }
            nodes[n] = new VisNode(true, (uint)start, (byte)(regions.Count - start), VisNode.NoEnclosedList);
        }

        var (ranges, enclosed) = EnclosedLists(nodes, regions);

        var clusters = (uint)s.Clusters;
        var stride = ((clusters + 31) >> 5) * 4;
        var rows = clusters + (sky is not null ? 1u : 0u) + (sun is not null ? 1u : 0u);
        var blocks = new byte[rows * stride];
        for (var c = 0; c < clusters; c++)
            Buffer.BlockCopy(matrix.Rows[c], 0, blocks, (int)(c * stride), (int)stride);
        uint skyRow = 0, sunRow = 0;
        if (sky is not null)
        {
            skyRow = clusters;
            sunRow = clusters;
            Buffer.BlockCopy(sky, 0, blocks, (int)(clusters * stride), (int)stride);
        }
        if (sun is not null)
        {
            sunRow = clusters + (sky is not null ? 1u : 0u);
            Buffer.BlockCopy(sun, 0, blocks, (int)(sunRow * stride), (int)stride);
        }

        return new VoxelVisibility
        {
            BaseClusterCount = clusters,
            PVSBytesPerCluster = stride,
            MinBounds = minBounds,
            MaxBounds = maxBounds,
            GridSize = s.BaseVoxelSize,
            SkyVisibilityCluster = skyRow,
            SunVisibilityCluster = sunRow,
            Nodes = nodes,
            Regions = [.. regions],
            EnclosedClusterList = ranges,
            EnclosedClusters = enclosed,
            Masks = masks,
            VisBlocks = blocks,
        };
    }

    /// <summary>
    /// <c>Logs_UniqueMasksOfRegions</c>: every record's cell mask, counted, and
    /// sorted by count and then by mask.
    /// </summary>
    public static ulong[] UniqueMasks(VisPvs.State s)
    {
        var counts = new Dictionary<ulong, uint>();
        foreach (var e in s.Entries)
            counts[e.Cells] = counts.GetValueOrDefault(e.Cells) + 1;
        return [.. counts.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key)];
    }

    // Logs_ComputeEnclosedClusterLists: bottom up, a leaf's sorted distinct
    // cluster ids and a branch's union of its children's; then in node order
    // each list under MaxEnclosed ids is stored once, and a node whose list is
    // already stored gets none.
    private static (VisClusterRange[] Ranges, ushort[] Clusters) EnclosedLists(VisNode[] nodes, List<VisRegion> regions)
    {
        var n = nodes.Length;
        var parent = new int[n];
        Array.Fill(parent, -1);
        for (var i = 0; i < n; i++)
        {
            if (nodes[i].IsLeaf)
                continue;
            for (var k = 0; k < 8; k++)
                parent[nodes[i].Offset + k] = i;
        }
        var lists = new List<int>[n];
        for (var i = 0; i < n; i++)
            lists[i] = [];
        for (var i = n - 1; i >= 0; i--)
        {
            if (nodes[i].IsLeaf)
            {
                for (var k = 0; k < nodes[i].RegionCount; k++)
                    lists[i].Add(regions[(int)nodes[i].Offset + k].ClusterId);
            }
            SortUnique(lists[i]);
            if (parent[i] >= 0)
                lists[parent[i]].AddRange(lists[i]);
        }

        var stored = new Dictionary<string, int>();
        var ranges = new List<VisClusterRange>();
        var clusters = new List<ushort>();
        for (var i = 0; i < n; i++)
        {
            SortUnique(lists[i]);
            if (lists[i].Count >= MaxEnclosed)
                continue;
            var key = string.Join(",", lists[i]);
            if (stored.ContainsKey(key))
                continue;
            stored[key] = ranges.Count;
            nodes[i] = nodes[i] with { EnclosedListIndex = (uint)ranges.Count };
            ranges.Add(new VisClusterRange(clusters.Count, lists[i].Count));
            foreach (var c in lists[i])
                clusters.Add((ushort)c);
        }
        return ([.. ranges], [.. clusters]);

        static void SortUnique(List<int> list)
        {
            list.Sort();
            var w = 0;
            for (var r = 0; r < list.Count; r++)
            {
                if (w == 0 || list[w - 1] != list[r])
                    list[w++] = list[r];
            }
            list.RemoveRange(w, list.Count - w);
        }
    }

    /// <summary>
    /// <c>SplitOpenSpace</c>: a 4x4x4 mask as boxes, greedily. From the lowest
    /// cell not yet covered a box grows along x, then y, then z while it meets
    /// no empty cell (it may grow over covered ones); what it adds is kept and
    /// counts as covered. The pieces are not always boxes themselves.
    /// </summary>
    public static List<ulong> SplitOpenSpace(ulong mask)
    {
        var pieces = new List<ulong>();
        var empty = ~mask;
        var covered = empty;
        while (true)
        {
            var at = 0;
            while ((covered >> at & 1) != 0)
            {
                if (++at > 63)
                    return pieces;
            }
            var box = 1UL << at;
            if ((empty & box) != 0)
                return pieces;
            for (var x = at & 3; x < 3 && (empty & ((box << 1) | box)) == 0; x++)
                box |= box << 1;
            for (var y = (at >> 2) & 3; y < 3 && (empty & ((box << 4) | box)) == 0; y++)
                box |= box << 4;
            for (var z = (at >> 4) & 3; z < 3 && (empty & ((box << 16) | box)) == 0; z++)
                box |= box << 16;
            var piece = ~covered & box;
            if (piece == 0)
                return pieces;
            pieces.Add(piece);
            covered |= piece;
        }
    }

    /// <summary>
    /// <c>FlatVisClusterVector</c>, the per-cluster box lists the world renderer's
    /// visibility-guided mesh clustering reads (<c>CVisibilityMeshMerger</c>).
    /// Filled by the border stage before its rewrite: every open record of every
    /// leaf in node order adds the boxes of its <see cref="SplitOpenSpace"/>
    /// pieces to its cluster (<c>FUN_18003a440</c>, no range check); then every
    /// border claim whose cluster is in range adds its box grown by 0.1f on every
    /// side (<c>FUN_18003a1a0</c>).
    /// </summary>
    public static List<(Vector3 Mins, Vector3 Maxs)>[] FlatClusterBoxes(VisPvs.State merged, IReadOnlyList<List<VisBorders.Claim>> claims)
    {
        var flat = new List<(Vector3, Vector3)>[merged.Clusters];
        for (var c = 0; c < flat.Length; c++)
            flat[c] = [];
        for (var node = 0; node < merged.NodeWords.Length; node++)
        {
            var word = merged.NodeWords[node];
            if ((word & 1) == 0)
                continue;
            var first = (int)(word >> 1);
            for (var k = 0; k < merged.NodeCounts[node]; k++)
            {
                var e = merged.Entries[first + k];
                if ((e.Packed & 3) != 0)
                    continue;
                foreach (var piece in SplitOpenSpace(e.Cells))
                    flat[e.Cluster].Add(VisPvs.RegionBox(merged, e with { Cells = piece }));
            }
        }
        foreach (var list in claims)
        {
            foreach (var claim in list)
            {
                if ((uint)claim.Cluster >= (uint)flat.Length)
                    continue;
                flat[claim.Cluster].Add((new Vector3(claim.Mins.X + -0.1f, claim.Mins.Y + -0.1f, claim.Mins.Z + -0.1f),
                                         new Vector3(claim.Maxs.X + 0.1f, claim.Maxs.Y + 0.1f, claim.Maxs.Z + 0.1f)));
            }
        }
        return flat;
    }

    /// <summary>
    /// <c>MutualVisibilityMatrix</c> as <c>FUN_180049010</c> exports it for the
    /// world renderer, from the finished PVS rows of the real clusters (sky and
    /// sun excluded). With <c>count[k]</c> the clusters that see k and
    /// <c>pair[j][k]</c> those that see both, counted as u16 and read back as
    /// signed shorts, <c>M[j][k] = pair[min][max] / count[max]</c>: written for
    /// k &gt;= j, then mirrored below the diagonal.
    /// </summary>
    public static float[][] MutualVisibility(VoxelVisibility vis)
    {
        var n = (int)vis.BaseClusterCount;
        var stride = (int)vis.PVSBytesPerCluster;
        var count = new ushort[n];
        var pair = new ushort[n][];
        for (var j = 0; j < n; j++)
            pair[j] = new ushort[n];
        for (var i = 0; i < n; i++)
        {
            var at = i * stride;
            for (var j = 0; j < n; j++)
            {
                if ((vis.VisBlocks[at + (j >> 3)] >> (j & 7) & 1) == 0)
                    continue;
                count[j]++;
                for (var k = j; k < n; k++)
                {
                    if ((vis.VisBlocks[at + (k >> 3)] >> (k & 7) & 1) != 0)
                        pair[j][k]++;
                }
            }
        }
        var m = new float[n][];
        for (var j = 0; j < n; j++)
        {
            m[j] = new float[n];
            for (var k = j; k < n; k++)
                m[j][k] = (float)(short)pair[j][k] / (float)(short)count[k];
        }
        for (var j = 0; j < n; j++)
        {
            for (var k = 0; k < j; k++)
                m[j][k] = m[k][j];
        }
        return m;
    }
}
