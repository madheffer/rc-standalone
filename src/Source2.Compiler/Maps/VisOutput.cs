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
}
