namespace Source2.Compiler.Maps;

/// <summary>
/// Cluster assignment, which is the tail of
/// <c>CVoxelSampler3::MergeInsideRegions</c> (<c>180034ea0</c>) after the five
/// merge passes have run.
///
/// <para>The merge leaves clusters bucketed by grid cell, each holding the
/// (mask, region) pairs it covers. Assignment turns that inside out into a
/// per-region list, so the PVS walk can ask a leaf which clusters are in it, and
/// then flattens every list into one array that the octree indexes by offset and
/// count.</para>
///
/// <para>It is the line the compile prints as
/// <c>Compacted to N regions (M clusters)</c>, and N is the length of that flat
/// array rather than any count of regions or clusters.</para>
/// </summary>
public static class VisAssign
{
    /// <summary>
    /// What assignment produced: the flat array, and where each region's run of
    /// it starts.
    /// </summary>
    /// <param name="Entries">The array the octree's leaves index into.</param>
    /// <param name="Offsets">Per region, the first entry, or -1 when dropped.</param>
    /// <param name="Counts">Per region, how many entries it owns.</param>
    /// <param name="Clusters">How many clusters were assigned.</param>
    public readonly record struct Result(
        VisVisibility.Entry[] Entries, int[] Offsets, short[] Counts, int Clusters)
    {
        /// <summary>The number the compile prints, the flat array's length.</summary>
        public int Regions => Entries.Length;
    }

    /// <summary>
    /// Run the three passes.
    ///
    /// <para>The first scatters every final cluster's (mask, region) pairs into
    /// the region they name, stamping the running cluster index and
    /// <c>region * 4</c>, which leaves the kind bits clear because a cluster is
    /// always open space. The second re-adds the blocking and skipped records the
    /// region compaction had already written, which carry no cluster and would
    /// otherwise be lost. The third concatenates.</para>
    /// </summary>
    /// <param name="sets">The merged cluster sets, in the order they are held.</param>
    /// <param name="regions">How many regions the octree has.</param>
    /// <param name="compacted">The compaction's entry array, whose blocking and
    /// skipped records survive into the new one.</param>
    /// <param name="kept">Whether a region is still live, its flag bit 0. A
    /// region that is not keeps nothing.</param>
    public static Result Run(
        IReadOnlyList<VisClusterSet.Set> sets, int regions,
        IReadOnlyList<VisVisibility.Entry> compacted, Func<int, bool> kept)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(compacted);
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentOutOfRangeException.ThrowIfNegative(regions);

        var byRegion = new List<VisVisibility.Entry>[regions];
        for (var i = 0; i < regions; i++)
            byRegion[i] = [];

        var cluster = 0;
        foreach (var set in sets)
        {
            foreach (var one in set.Clusters)
            {
                foreach (var (mask, region) in one.Voxels)
                {
                    if ((uint)region < (uint)regions)
                        byRegion[region].Add(new VisVisibility.Entry(
                            cluster, region << 2, mask));
                }
                cluster++;
            }
        }

        foreach (var entry in compacted)
        {
            if (entry.Kind == VisVisibility.Open)
                continue;
            var region = entry.Region;
            if ((uint)region < (uint)regions)
                byRegion[region].Add(entry);
        }

        var flat = new List<VisVisibility.Entry>();
        var offsets = new int[regions];
        var counts = new short[regions];
        for (var region = 0; region < regions; region++)
        {
            if (!kept(region))
            {
                offsets[region] = -1;
                continue;
            }
            offsets[region] = flat.Count;
            counts[region] = checked((short)byRegion[region].Count);
            flat.AddRange(byRegion[region]);
        }

        return new Result([.. flat], offsets, counts, cluster);
    }
}
