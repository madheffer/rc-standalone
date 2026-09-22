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
    public static List<VisClusterSet.Set> Generate(
        RayTraceEnvironment scene, VisVoxelizer.Octree tree, VisRegions.Result compacted)
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
        });
        return [.. sets];
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

        var volume = 0d;
        foreach (var region in compacted.Regions)
        {
            var leaf = compacted.Leaves[region.Leaf];
            var side = tree.LeafSize * (1 << leaf.Level);
            var (mins, maxs) = Box(side * SubCell, region.Open);
            volume += (double)(maxs.X - mins.X) * (maxs.Y - mins.Y) * (maxs.Z - mins.Z);
        }

        var wanted = (int)(volume / VolumePerCluster);
        var target = wanted < 1 ? 32 : Math.Max(32, (wanted + 0x21) & ~0x1f);
        return Math.Min(target, MaxVisClusters * 4);
    }

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
