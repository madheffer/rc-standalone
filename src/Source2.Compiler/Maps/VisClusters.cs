using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// Stage 4: the clusters a region is cut into, ported from visbuilder.dll's
/// <c>180032d80</c>.
///
/// <para>A cluster is born for EVERY open voxel of every region the map
/// encloses, and <c>1800337a0</c> then merges the cheapest pairs by
/// <see cref="VisMergeCost"/>. A region's mask holds at most 64 voxels, so the
/// merge only ever runs on a region with more than 32 open, and everything
/// smaller is born and kept.</para>
///
/// <para>When it does run it runs to the end. The count the loop tests against
/// its target of 32 includes the 56 shell boxes <see cref="VisClusterSample"/>
/// pads the set with, and a merge takes one off it, so a region's clusters can
/// never bring it down that far: the loop stops when the candidate graph is
/// exhausted instead. A region is one CONNECTED run of open voxels and
/// <c>1800306e0</c> rebuilds the surviving cluster's candidates from its grown
/// box after every merge, so the graph stays connected and the whole region
/// collapses to one cluster. That is why <see cref="Uniform"/> reproduces the
/// count without sampling anything, and it is not because the voxels see
/// alike.</para>
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

    /// <summary>
    /// <c>ResourceCompiler/VisBuilder/BaseVoxelSize</c>, which defaults to 8 and
    /// which the shipped gameinfo does not override. The sampler keeps it at
    /// <c>+0xf0</c>, and the candidate query grows its box by this much, not by
    /// the cluster's own voxel size.
    /// </summary>
    public const float BaseVoxelSize = 8f;

    /// <summary>Clusters a region is merged down towards, the <c>0x20</c> argument.</summary>
    public const int MergeTarget = 32;

    /// <summary>
    /// How far a region's first voxel must be from any surface before the whole
    /// region becomes ONE cluster outright, skipping the per-voxel birth and the
    /// merge. <c>PreMergeOpenSpaceDistanceThreshold</c> in
    /// <c>csgo_core/gameinfo.gi</c>; the binary's own default is -1, which turns
    /// the rule off, and the shipped config turns it on.
    /// </summary>
    public const float OpenSpaceDistance = 128f;

    /// <summary>Rays a face for the gather cluster generation asks for.</summary>
    public const int OpenSpaceQuality = 6;

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
    public static List<Cluster> Born(VisVoxelizer.Octree tree, VisRegions.Result compacted)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(compacted);

        var regions = compacted;
        var found = new List<Cluster>();
        for (var i = 0; i < regions.Regions.Count; i++)
        {
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
    /// How many clusters a region ends up with, which is the number the compile
    /// sums into "N clusters generated".
    ///
    /// <para>With a <paramref name="scene"/> this runs the real thing: a cluster
    /// per open voxel, <see cref="VisClusterSample"/> for what each one can see,
    /// and <see cref="Merge"/> for the greedy loop. Without one it takes the
    /// shortcut in <see cref="Uniform"/>. They agree to the unit on every map
    /// measured, which is the point of keeping both: the shortcut is a claim
    /// about what the merge always does, and the sampler is what tests it.</para>
    /// </summary>
    public static int Count(
        VisVoxelizer.Octree tree, VisRegions.Result compacted, RayTraceEnvironment? scene = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(compacted);

        var regions = compacted;
        var total = 0;
        var counts = new int[regions.Regions.Count];
        Parallel.For(0, regions.Regions.Count, i =>
        {
            var region = regions.Regions[i];
            var open = System.Numerics.BitOperations.PopCount(region.Open);
            if (open <= MergeTarget || scene is null)
            {
                counts[i] = open <= MergeTarget ? open : 1;
                return;
            }

            var leaf = regions.Leaves[region.Leaf];
            var side = tree.LeafSize * (1 << leaf.Level);
            var corner = tree.Origin + new Vector3(leaf.Cell.X, leaf.Cell.Y, leaf.Cell.Z) * side;
            counts[i] = OpenSpace(scene, corner, side, region.Open)
                ? 1
                : Merge(scene, corner, side, region.Open);
        });
        foreach (var count in counts)
            total += count;
        return total;
    }

    /// <summary>
    /// Every enclosed region's clusters, born and merged, as one set per region.
    /// This is the whole of <c>180032d80</c> and the <c>1800337a0</c> under it,
    /// and it is what the five <see cref="VisClusterSet"/> passes then work on.
    /// </summary>
    /// <remarks>
    /// With split hints (<paramref name="splits"/>) a region's voxels are dealt
    /// out over the leaf's candidate boxes (<see cref="CandidateBoxes"/>) as
    /// GenerateRegionClusters (180034420) deals them: box by box, a voxel still
    /// unclaimed goes to the box when its sub-cell lies within the box grown by
    /// 0.1 (the last box takes every voxel left), each born cluster carries the
    /// box's tag, and each box's clusters are merged on their own and then
    /// appended. Without split hints the only box is the leaf itself, tag 0.
    /// The split path is read from the binary; no specimen map has a split
    /// hint, so it is not measured.
    /// </remarks>
    public static List<VisClusterSet.Set> Generate(
        RayTraceEnvironment scene, VisVoxelizer.Octree tree, VisRegions.Result compacted, SplitHints? splits = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(compacted);

        var sets = new VisClusterSet.Set[compacted.Regions.Count];
        Parallel.For(0, compacted.Regions.Count, i =>
        {
            var region = compacted.Regions[i];
            var leaf = compacted.Leaves[region.Leaf];
            var side = tree.LeafSize * (1 << leaf.Level);
            var corner = tree.Origin + new Vector3(leaf.Cell.X, leaf.Cell.Y, leaf.Cell.Z) * side;
            var voxel = side * SubCell;
            var set = new VisClusterSet.Set { Mins = corner, Maxs = corner + new Vector3(side) };

            if (OpenSpace(scene, corner, side, region.Open))
            {
                var (mins, maxs) = Box(voxel, region.Open);
                set.Clusters.Add(new VisMerge.Cluster
                {
                    Voxels = { (region.Open, region.Leaf) },
                    Mins = corner + mins,
                    Maxs = corner + maxs,
                    VoxelCount = System.Numerics.BitOperations.PopCount(region.Open),
                    VoxelSize = (int)voxel,
                    OpenSpace = true,
                });
                sets[i] = set;
                return;
            }

            if (splits is not { Any: true })
            {
                for (var bit = 0; bit < 64; bit++)
                {
                    if ((region.Open & (1UL << bit)) == 0)
                        continue;
                    var at = corner + new Vector3(bit & 3, (bit >> 2) & 3, (bit >> 4) & 3) * voxel;
                    set.Clusters.Add(new VisMerge.Cluster
                    {
                        Voxels = { (1UL << bit, region.Leaf) },
                        Mins = at,
                        Maxs = at + new Vector3(voxel),
                        VoxelCount = 1,
                        VoxelSize = (int)voxel,
                    });
                }
                VisMerge.Run(scene, set.Clusters, set.Mins, set.Maxs,
                             MergeThreshold, MergeTarget, padded: true, Cubes(tree, compacted));
                sets[i] = set;
                return;
            }

            var boxes = CandidateBoxes(set.Mins, set.Maxs, splits);
            var left = region.Open;
            for (var b = 0; b < boxes.Count; b++)
            {
                var (bmin, bmax, tag) = boxes[b];
                var clusters = new List<VisMerge.Cluster>();
                for (var bit = 0; bit < 64; bit++)
                {
                    if ((left & (1UL << bit)) == 0)
                        continue;
                    if (b != boxes.Count - 1)
                    {
                        // SubBox (18010e610), against the box grown by 0.1.
                        float ox = (bit & 3) * voxel, oy = ((bit >> 2) & 3) * voxel, oz = ((bit >> 4) & 3) * voxel;
                        var lo = new Vector3(ox + corner.X, oy + corner.Y, oz + corner.Z);
                        var hi = new Vector3(corner.X + (ox + voxel), corner.Y + (oy + voxel), corner.Z + (oz + voxel));
                        if (!(bmin.X + CandidateSlackMin <= lo.X && bmin.Y + CandidateSlackMin <= lo.Y && bmin.Z + CandidateSlackMin <= lo.Z
                              && hi.X <= bmax.X + CandidateSlackMax && hi.Y <= bmax.Y + CandidateSlackMax && hi.Z <= bmax.Z + CandidateSlackMax))
                            continue;
                    }
                    var at = corner + new Vector3(bit & 3, (bit >> 2) & 3, (bit >> 4) & 3) * voxel;
                    clusters.Add(new VisMerge.Cluster
                    {
                        Voxels = { (1UL << bit, region.Leaf) },
                        Mins = at,
                        Maxs = at + new Vector3(voxel),
                        VoxelCount = 1,
                        VoxelSize = (int)voxel,
                        Tag = tag,
                    });
                    left &= ~(1UL << bit);
                }
                VisMerge.Run(scene, clusters, set.Mins, set.Maxs,
                             MergeThreshold, MergeTarget, padded: true, Cubes(tree, compacted));
                set.Clusters.AddRange(clusters);
            }
            sets[i] = set;
        });
        return [.. sets];
    }

    /// <summary>
    /// The split hints as Hints_Load (visbuilder 18002c970) files them: type 4
    /// on the x list, 5 on the y list, 6 on the z list, each box
    /// <c>origin + box_mins</c> to <c>origin + box_maxs</c> as it stands.
    /// </summary>
    public sealed record SplitHints(IReadOnlyList<(Vector3 Mins, Vector3 Maxs)> X, IReadOnlyList<(Vector3 Mins, Vector3 Maxs)> Y,
                                    IReadOnlyList<(Vector3 Mins, Vector3 Maxs)> Z)
    {
        /// <summary>Whether there is any split hint at all.</summary>
        public bool Any => X.Count + Y.Count + Z.Count > 0;

        /// <summary>A map's split hints, in entity order within each list.</summary>
        public static SplitHints From(IReadOnlyList<VisHint> hints)
        {
            ArgumentNullException.ThrowIfNull(hints);
            List<(Vector3, Vector3)> Of(int type) => [.. hints.Where(h => h.Type == type).Select(h => (h.Mins, h.Maxs))];
            return new SplitHints(Of(4), Of(5), Of(6));
        }
    }

    /// <summary>
    /// A leaf box cut by the split hints (CandidateBoxes, 18002d320, with
    /// CandidateBoxes_SplitOne, 18002d140): the z list, then x, then y, the
    /// tag a counter from 1 over every hint of the three in that order. The
    /// box still being cut is given up (nothing more pushed) once any of its
    /// sides is under 1.1. A hint that touches it cuts it along its axis
    /// unless it reaches less than 1.1 into it: the piece inside the hint is
    /// pushed with the hint's tag; when pieces remain on both sides the lower
    /// one is cut again from the start and the upper one carries on, otherwise
    /// the one remaining piece carries on, and with none the cutting ends.
    /// What is left at the end is pushed with tag 0.
    /// </summary>
    public static List<(Vector3 Mins, Vector3 Maxs, short Tag)> CandidateBoxes(Vector3 mins, Vector3 maxs, SplitHints splits)
    {
        ArgumentNullException.ThrowIfNull(splits);
        var found = new List<(Vector3, Vector3, short)>();
        Cut(mins, maxs, splits, found);
        return found;
    }

    private const float SplitMinimum = 1.1f;

    private static void Cut(Vector3 mins, Vector3 maxs, SplitHints splits, List<(Vector3, Vector3, short)> found)
    {
        var tag = 1;
        foreach (var (list, axis) in new[] { (splits.Z, 2), (splits.X, 0), (splits.Y, 1) })
        {
            foreach (var (hmin, hmax) in list)
            {
                if (!(maxs.X - mins.X >= SplitMinimum) || !(maxs.Y - mins.Y >= SplitMinimum) || !(maxs.Z - mins.Z >= SplitMinimum))
                    return;
                if (!(mins.X > hmax.X) && !(hmin.X > maxs.X) && !(mins.Y > hmax.Y) && !(hmin.Y > maxs.Y)
                    && !(mins.Z > hmax.Z) && !(hmin.Z > maxs.Z))
                    (mins, maxs) = SplitOne(mins, maxs, hmin, hmax, axis, (short)tag, splits, found);
                tag++;
            }
        }
        if (!(maxs.X - mins.X >= SplitMinimum) || !(maxs.Y - mins.Y >= SplitMinimum) || !(maxs.Z - mins.Z >= SplitMinimum))
            return;
        found.Add((mins, maxs, 0));
    }

    private static (Vector3 Mins, Vector3 Maxs) SplitOne(Vector3 mins, Vector3 maxs, Vector3 hmin, Vector3 hmax, int axis, short tag,
                                                         SplitHints splits, List<(Vector3, Vector3, short)> found)
    {
        static float Get(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;
        static Vector3 With(Vector3 v, int a, float x) => a == 0 ? v with { X = x } : a == 1 ? v with { Y = x } : v with { Z = x };
        float cmin = Get(mins, axis), cmax = Get(maxs, axis), lo = Get(hmin, axis), hi = Get(hmax, axis);
        if (SplitMinimum > cmax - lo || SplitMinimum > hi - cmin)
            return (mins, maxs);
        var below = lo - cmin >= SplitMinimum;
        var above = cmax - hi >= SplitMinimum;
        var from = below ? lo : cmin;
        var to = above ? hi : cmax;
        found.Add((With(mins, axis, from), With(maxs, axis, to), tag));
        if (above)
        {
            if (below)
                Cut(mins, With(maxs, axis, lo), splits, found);
            return (With(mins, axis, hi), maxs);
        }
        if (below)
            return (mins, With(maxs, axis, lo));
        return (new Vector3(float.MaxValue), new Vector3(float.MinValue));
    }

    /// <summary>
    /// Whether a region is far enough from anything to skip cluster generation
    /// entirely, which is <c>180032d80</c>'s first branch:
    /// <c>(f4 == 0 || nearest &lt; f8 || nearest == FLT_MAX)</c> takes the normal
    /// path and anything else becomes one cluster flagged at <c>+0x54</c>.
    ///
    /// <para>The gather is over the region's FIRST set voxel, not its whole box,
    /// and it asks for <see cref="OpenSpaceQuality"/> rays a face rather than the
    /// seed's five.</para>
    /// </summary>
    public static bool OpenSpace(RayTraceEnvironment scene, Vector3 leafMins, float side, ulong open)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var first = System.Numerics.BitOperations.TrailingZeroCount(open);
        if (first > 63)
            return false;

        var voxel = side * SubCell;
        var at = leafMins + new Vector3(first & 3, (first >> 2) & 3, (first >> 4) & 3) * voxel;
        var nearest = VisSeed.Gather(scene, at, at + new Vector3(voxel), OpenSpaceQuality).Nearest;
        return nearest >= OpenSpaceDistance && !float.IsInfinity(nearest);
    }

    /// <summary>
    /// The shortcut: a region the merge runs on at all collapses to one cluster,
    /// so the count is the popcounts of the small regions plus one per large.
    /// Instant, and identical to <see cref="Merge"/> on every map measured.
    /// </summary>
    public static int Uniform(VisRegions.Result compacted)
    {
        ArgumentNullException.ThrowIfNull(compacted);

        var total = 0;
        foreach (var region in compacted.Regions)
        {
            var open = System.Numerics.BitOperations.PopCount(region.Open);
            total += open > MergeTarget ? 1 : open;
        }
        return total;
    }

    /// <summary>Cubic units of enclosed space a cluster is worth (<c>_DAT_18017f0f8</c>, 2^-20).</summary>
    public const double VolumePerCluster = 1048576d;

    /// <summary>
    /// The ceiling the config caps the target at, times four.
    ///
    /// <para>This is NOT the binary's default of 2,048. CS2 ships a
    /// <c>ResourceCompiler/VisBuilder</c> block in
    /// <c>game/csgo_core/gameinfo.gi</c> that overrides five of the six keys the
    /// driver reads, and taking the defaults out of the binary is wrong for every
    /// one of them. See docs/VIS.md.</para>
    /// </summary>
    public const int MaxVisClusters = 4096;

    /// <summary>
    /// The cluster count the whole merge aims at, which <c>180031f00</c> works
    /// out from the enclosed VOLUME before <c>MergeInsideRegions</c> runs:
    ///
    /// <code>
    /// t = (int)(volume / 2^20)
    /// target = t &lt; 1 ? 32 : max(32, (t + 33) &amp; ~31)
    /// target = min(target, MaxVisClusters * 4)
    /// </code>
    ///
    /// <para>It is worth more than its own stage. The compile prints it as
    /// "Target N clusters", so it is a number to hit, and it depends on nothing
    /// but the compacted regions and their boxes. Landing on it is a check that
    /// the octree, the masks, the outside pass and the compaction are ALL right,
    /// with no ray sampling involved at all.</para>
    /// </summary>
    public static int TargetClusters(VisVoxelizer.Octree tree, VisRegions.Result compacted)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(compacted);

        // Each region's volume is a FLOAT product, summed in double.
        var volume = 0d;
        foreach (var region in compacted.Regions)
        {
            var leaf = compacted.Leaves[region.Leaf];
            var side = tree.LeafSize * (1 << leaf.Level);
            var (mins, maxs) = Box(side * SubCell, region.Open);
            volume += (double)((maxs.X - mins.X) * (maxs.Y - mins.Y) * (maxs.Z - mins.Z));
        }

        // VoxelStageDriver would also cap the pre-merge's open space at 2^19 a
        // run, from sampler+0x108 and +0x110, but it takes the target BEFORE
        // MergeInsideRegions, which is where the pre-merge runs and fills them.
        // In one compile they are still zero here, so the rule never fires:
        // applying it takes probe01 to 606 against the compile's 862.
        var wanted = (int)(volume * (1.0 / VolumePerCluster));
        if (wanted < 1)
            return Math.Min(32, MaxVisClusters * 4);
        var target = (wanted + 0x21) & ~0x1f;
        return target < 32 ? 32 : Math.Min(target, MaxVisClusters * 4);
    }

    /// <summary>
    /// What the five passes actually aim at: <see cref="TargetClusters"/> less
    /// two, which is what <c>VoxelStageDriver</c> hands <c>MergeInsideRegions</c>
    /// and what the compile prints as "[target N clusters]".
    /// </summary>
    public static int PassTarget(VisVoxelizer.Octree tree, VisRegions.Result compacted)
        => TargetClusters(tree, compacted) - 2;

    /// <summary>
    /// The leaf cube lookup the sampling needs: a leaf index to its corner and
    /// edge, which is what a cluster's (mask, leaf) pairs are relative to.
    /// </summary>
    /// <param name="tree">The voxel tree the leaves index.</param>
    /// <param name="regions">The compacted leaves.</param>
    public static VisClusterSample.LeafCube Cubes(VisVoxelizer.Octree tree, VisRegions.Result regions)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(regions);
        return leaf =>
        {
            var at = regions.Leaves[leaf];
            var side = tree.LeafSize * (1 << at.Level);
            return (tree.Origin + (new Vector3(at.Cell.X, at.Cell.Y, at.Cell.Z) * side), side);
        };
    }

    /// <summary>
    /// A mask's box within its leaf, which is <c>18010be50</c>: the corners of
    /// the set sub-cells, in leaf-local units.
    /// </summary>
    public static (Vector3 Mins, Vector3 Maxs) Box(float sub, ulong mask)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        for (var bit = 0; bit < 64; bit++)
        {
            if ((mask & (1UL << bit)) == 0)
                continue;
            var at = new Vector3(bit & 3, (bit >> 2) & 3, (bit >> 4) & 3) * sub;
            lo = Vector3.Min(lo, at);
            hi = Vector3.Max(hi, at + new Vector3(sub));
        }
        return mask == 0 ? (Vector3.Zero, Vector3.Zero) : (lo, hi);
    }

    /// <summary>
    /// One region's clusters, born and then merged. This is <c>1800337a0</c>:
    /// the cheapest pair first, continuing while there are more than
    /// <see cref="MergeTarget"/> left OR the cheapest is still under
    /// <see cref="MergeThreshold"/>.
    ///
    /// <para>Two things about that loop are easy to get wrong and both change
    /// the answer. The live count it tests INCLUDES the 56 shell boxes, and a
    /// merge only takes one off it, so there are never enough clusters to bring
    /// it down to the target: the run ends when the candidate graph is
    /// exhausted, not when the count reaches 32. And the surviving cluster's
    /// candidates are REBUILT after every merge by <c>1800306e0</c>, against its
    /// new box and its merged bit vector, so the graph stays connected and the
    /// costs stay honest.</para>
    /// </summary>
    public static int Merge(RayTraceEnvironment scene, Vector3 leafMins, float side, ulong open)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var entries = VisClusterSample.Entries(leafMins, side, open);
        var visibility = VisClusterSample.Visibility(scene, leafMins, side, open, entries);
        var count = visibility.Length;
        if (count <= MergeTarget)
            return count;

        var state = new Working(entries, visibility, (int)(side * SubCell), side * SubCell);
        var live = count + VisClusterSample.Shell.Length;
        var clusters = count;
        for (var i = 0; i < count; i++)
            state.Rebuild(i);

        while (true)
        {
            var (owner, other, cost) = state.Cheapest();
            if (owner < 0 || (live <= MergeTarget && cost >= MergeThreshold))
                break;

            state.Absorb(owner, other);
            live--;
            if (--clusters == 1)
                break;
        }
        return clusters;
    }

    /// <summary>
    /// The merge's working set: a cluster per open voxel, and the candidate list
    /// each one owns. A pair is stored once, in the HIGHER indexed cluster's
    /// list, which is also the one that survives the merge.
    /// </summary>
    private sealed class Working(
        IReadOnlyList<VisClusterSample.Entry> entries, ulong[][] visibility, int voxelSize, float grow)
    {
        private readonly int _count = visibility.Length;
        private readonly Vector3[] _mins = [.. entries.Take(visibility.Length).Select(e => e.Mins)];
        private readonly Vector3[] _maxs = [.. entries.Take(visibility.Length).Select(e => e.Maxs)];
        private readonly int[] _voxels = [.. Enumerable.Repeat(1, visibility.Length)];
        private readonly bool[] _alive = [.. Enumerable.Repeat(true, visibility.Length)];
        private readonly List<(int Other, float Cost)>[] _candidates =
            [.. Enumerable.Range(0, visibility.Length).Select(_ => new List<(int, float)>())];

        /// <summary>The cheapest live pair, or (-1, -1, max) when none is left.</summary>
        public (int Owner, int Other, float Cost) Cheapest()
        {
            var best = (Owner: -1, Other: -1, Cost: float.MaxValue);
            for (var owner = 0; owner < _count; owner++)
            {
                if (!_alive[owner])
                    continue;
                foreach (var (other, cost) in _candidates[owner])
                    if (_alive[other] && cost < best.Cost)
                        best = (owner, other, cost);
            }
            return best;
        }

        /// <summary>Merge <paramref name="other"/> into <paramref name="owner"/>.</summary>
        public void Absorb(int owner, int other)
        {
            _mins[owner] = Vector3.Min(_mins[owner], _mins[other]);
            _maxs[owner] = Vector3.Max(_maxs[owner], _maxs[other]);
            _voxels[owner] += _voxels[other];
            for (var w = 0; w < visibility[owner].Length; w++)
                visibility[owner][w] |= visibility[other][w];

            _alive[other] = false;
            _candidates[other].Clear();
            for (var i = 0; i < _count; i++)
                _candidates[i].RemoveAll(c => c.Other == other);
            Rebuild(owner);
        }

        /// <summary>
        /// A cluster's candidates, from its box dilated by a unit, grown by a
        /// voxel at a time until the query finds more than the cluster itself.
        /// </summary>
        public void Rebuild(int owner)
        {
            _candidates[owner].Clear();
            var slack = 1f;
            for (var attempt = 0; attempt < 5 && Reached(owner, slack) == 0; attempt++)
                slack += grow;

            for (var other = 0; other < _count; other++)
            {
                if (other == owner || !_alive[other] || !Touches(owner, slack, other))
                    continue;
                var cost = VisMergeCost.Of(Cluster(owner), Cluster(other));
                var (holder, id) = owner < other ? (other, owner) : (owner, other);
                var list = _candidates[holder];
                var at = list.FindIndex(c => c.Other == id);
                if (at >= 0)
                    list[at] = (id, cost);
                else
                    list.Add((id, cost));
            }
        }

        private int Reached(int owner, float slack)
        {
            var found = 0;
            for (var other = 0; other < _count; other++)
                if (other != owner && _alive[other] && Touches(owner, slack, other))
                    found++;
            return found;
        }

        private bool Touches(int owner, float slack, int other)
            => _mins[owner].X - slack <= _maxs[other].X && _maxs[owner].X + slack >= _mins[other].X
            && _mins[owner].Y - slack <= _maxs[other].Y && _maxs[owner].Y + slack >= _mins[other].Y
            && _mins[owner].Z - slack <= _maxs[other].Z && _maxs[owner].Z + slack >= _mins[other].Z;

        private VisMergeCost.Cluster Cluster(int at)
            => new(visibility[at], _voxels[at], voxelSize, 0, _mins[at], _maxs[at]);
    }

    /// <summary>
    /// How many clusters a region's own set of them would merge down to, given a
    /// cost for each pair. Regions at or below <see cref="MergeTarget"/> never
    /// merge at all, which is the common case and needs no cost.
    ///
    /// <para>The loop is <c>1800337a0</c>'s: cheapest pair first, and continuing
    /// while there are more than <see cref="MergeTarget"/> left OR the cheapest
    /// merge still costs under <see cref="MergeThreshold"/>.</para>
    /// </summary>
    public static int MergedCount(int born, Func<int, int, float> cost)
    {
        ArgumentNullException.ThrowIfNull(cost);
        if (born <= MergeTarget)
            return born;

        var alive = new bool[born];
        Array.Fill(alive, true);
        var count = born;
        while (true)
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
            if (left < 0 || (count <= MergeTarget && bestCost >= MergeThreshold))
                break;
            alive[right] = false;
            count--;
            if (count == 1)
                break;
        }
        return count;
    }
}
