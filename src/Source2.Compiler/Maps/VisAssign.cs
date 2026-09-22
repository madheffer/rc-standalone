namespace Source2.Compiler.Maps;

/// <summary>
/// Cluster assignment, which is the tail of
/// <c>CVoxelSampler3::MergeInsideRegions</c> (<c>180034ea0</c>) after the five
/// merge passes have run.
///
/// <para>The merge leaves clusters bucketed by grid cell, each holding the
/// (mask, leaf) pairs it covers. Assignment turns that inside out into a per
/// leaf list, so the PVS walk can ask a leaf which clusters are in it, and then
/// flattens every list into the one array the octree indexes by offset and
/// count.</para>
///
/// <para>Most of what it carries is not clusters. <c>180034ea0</c> copies every
/// record of the compaction whose <c>packed &amp; 3</c> is non-zero straight
/// through, and <see cref="VisRegions.Collapse"/> emits two of those per leaf:
/// the OUTSIDE union and the solid union. On ze_hold_em_p they are 92,640 of the
/// 103,358 records the stage ends with, so feeding it only the solid ones scored
/// it 61% short.</para>
///
/// <para>The key in a pair is an octree LEAF, not a region. The binary's own
/// array is as long as the node array at <c>this+0x30</c> and is indexed
/// straight by that key, which is also what the compaction packs into a record's
/// <c>+0x04</c>.</para>
/// </summary>
public static class VisAssign
{
    /// <summary>
    /// What assignment produced: the flat array, and where each leaf's run of it
    /// starts.
    /// </summary>
    /// <param name="Entries">The array the octree's leaves index into.</param>
    /// <param name="Offsets">Per octree leaf, the first entry, or -1 when dropped.</param>
    /// <param name="Counts">Per octree leaf, how many entries it owns.</param>
    /// <param name="Clusters">How many clusters were assigned, which
    /// <c>18002ed60</c> takes as the largest id in the array plus one rather than
    /// as a running count. That is the number the compile prints as
    /// <c>Assigned N clusters</c>, and the PVS matrix is sized at it plus two for
    /// sky and sun.</param>
    public readonly record struct Result(
        VisVisibility.Entry[] Entries, int[] Offsets, short[] Counts, int Clusters)
    {
        /// <summary>
        /// The number the compile prints as <c>Compacted to N regions</c>, which
        /// is the flat array's length and not a count of anything else.
        /// </summary>
        public int Regions => Entries.Length;
    }

    /// <summary>
    /// Run the three passes.
    ///
    /// <para>The first scatters every final cluster's (mask, leaf) pairs into the
    /// leaf they name, stamping the running cluster index and <c>leaf * 4</c>,
    /// which leaves the kind bits clear because a cluster is always open space.
    /// The second re-adds the blocking and skipped records the compaction had
    /// already written, which carry no cluster and would otherwise be lost. The
    /// third concatenates.</para>
    /// </summary>
    /// <param name="sets">The merged cluster sets, in the order they are held.</param>
    /// <param name="leaves">How many leaves the octree has.</param>
    /// <param name="compacted">The compaction's entry array, whose blocking and
    /// skipped records survive into the new one.</param>
    /// <param name="kept">Whether a leaf is still live, its flag bit 0. A leaf
    /// that is not keeps nothing.</param>
    public static Result Run(
        IReadOnlyList<VisClusterSet.Set> sets, int leaves,
        IReadOnlyList<VisVisibility.Entry> compacted, Func<int, bool> kept)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(compacted);
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentOutOfRangeException.ThrowIfNegative(leaves);

        var byLeaf = new List<VisVisibility.Entry>[leaves];
        for (var i = 0; i < leaves; i++)
            byLeaf[i] = [];

        var cluster = 0;
        foreach (var set in sets)
        {
            foreach (var one in set.Clusters)
            {
                foreach (var (mask, leaf) in one.Voxels)
                {
                    if ((uint)leaf < (uint)leaves)
                        byLeaf[leaf].Add(new VisVisibility.Entry(cluster, leaf << 2, mask));
                }
                cluster++;
            }
        }

        foreach (var entry in compacted)
        {
            if (entry.Kind == VisVisibility.Open)
                continue;
            if ((uint)entry.Leaf < (uint)leaves)
                byLeaf[entry.Leaf].Add(entry);
        }

        var flat = new List<VisVisibility.Entry>();
        var offsets = new int[leaves];
        var counts = new short[leaves];
        for (var leaf = 0; leaf < leaves; leaf++)
        {
            if (!kept(leaf))
            {
                offsets[leaf] = -1;
                continue;
            }
            offsets[leaf] = flat.Count;
            counts[leaf] = checked((short)byLeaf[leaf].Count);
            flat.AddRange(byLeaf[leaf]);
        }

        // 18002ed60 sweeps the finished array for the highest id and grows the
        // cluster table to cover it, so a cluster that ended up with no records
        // at all is not counted unless something above it was.
        var highest = -1;
        foreach (var entry in flat)
            if (entry.Kind == VisVisibility.Open && entry.Cluster > highest)
                highest = entry.Cluster;

        return new Result([.. flat], offsets, counts, highest + 1);
    }
}
