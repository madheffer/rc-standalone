using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The greedy merge, <c>1800337a0</c>, over any set of clusters.
///
/// <para>It runs at two places in the pipeline and the two look nothing alike
/// from the outside. Cluster generation calls it per REGION with the shell
/// padding on, which keeps the live count above the target for the whole run, so
/// the cost only orders the merges and a region collapses to its connected
/// components. <see cref="VisClusterSet"/> calls it per grid CELL with padding
/// off, and then the budget and the cost limit are exactly what stop it.</para>
///
/// <para>The cost is <see cref="VisMergeCost"/>, and the visibility it weighs is
/// sampled fresh at the start of every run, because a cluster's bit vector is
/// indexed by position in THIS set.</para>
/// </summary>
public static class VisMerge
{
    /// <summary>One cluster as it exists between merges, the 0x58 byte record.</summary>
    public sealed class Cluster
    {
        /// <summary>The (mask, leaf) pairs it covers, merged by <c>18002f250</c>.</summary>
        public List<(ulong Mask, int Leaf)> Voxels { get; init; } = [];

        /// <summary>Its box, at <c>+0x30</c>.</summary>
        public Vector3 Mins { get; set; }

        /// <summary>Its box, at <c>+0x30</c>.</summary>
        public Vector3 Maxs { get; set; }

        /// <summary>Accumulated voxels, at <c>+0x48</c>, which a merge sums.</summary>
        public int VoxelCount { get; set; }

        /// <summary>The size it was born at, at <c>+0x50</c>; a merge takes the minimum.</summary>
        public int VoxelSize { get; set; }

        /// <summary>The candidate box it came from, at <c>+0x52</c>; a merge zeroes a mismatch.</summary>
        public short Tag { get; set; }

        /// <summary>The open-space flag at <c>+0x54</c>, set by the region fast path.</summary>
        public bool OpenSpace { get; set; }

        /// <summary>What this cluster can see, indexed over the current merge set.</summary>
        public ulong[] Visibility { get; set; } = [];

        /// <summary>Its box centre, which is what every grid and query keys on.</summary>
        public Vector3 Centre => (Mins + Maxs) * 0.5f;

        /// <summary>
        /// Fold another cluster's (mask, leaf) pairs in, which is
        /// <c>18002f250</c>: both lists are sorted by leaf and merged, and two
        /// entries for the SAME leaf become one with their masks ORed. That is
        /// what makes the final region count a real number rather than a running
        /// total of every voxel ever merged.
        /// </summary>
        public void Take(IReadOnlyList<(ulong Mask, int Leaf)> other)
        {
            ArgumentNullException.ThrowIfNull(other);
            var byLeaf = new Dictionary<int, ulong>(Voxels.Count + other.Count);
            foreach (var (mask, leaf) in Voxels)
                byLeaf[leaf] = byLeaf.GetValueOrDefault(leaf) | mask;
            foreach (var (mask, leaf) in other)
                byLeaf[leaf] = byLeaf.GetValueOrDefault(leaf) | mask;
            Voxels.Clear();
            foreach (var leaf in byLeaf.Keys.Order())
                Voxels.Add((byLeaf[leaf], leaf));
        }

        /// <summary>Take another cluster into this one, which is <c>180030a50</c>.</summary>
        public void Absorb(Cluster other)
        {
            ArgumentNullException.ThrowIfNull(other);
            Mins = Vector3.Min(Mins, other.Mins);
            Maxs = Vector3.Max(Maxs, other.Maxs);
            VoxelCount += other.VoxelCount;
            VoxelSize = Math.Min(VoxelSize, other.VoxelSize);
            if (Tag != other.Tag)
                Tag = 0;
            Take(other.Voxels);
            for (var w = 0; w < Visibility.Length && w < other.Visibility.Length; w++)
                Visibility[w] |= other.Visibility[w];
        }
    }

    /// <summary>
    /// Merge a set down, in place, and return the cost the loop stopped at, which
    /// is what the caller uses as the next pass's limit.
    /// </summary>
    /// <param name="scene">The ray trace scene the visibility is sampled against.</param>
    /// <param name="clusters">The set, rewritten to the survivors.</param>
    /// <param name="mins">The box the merge works inside.</param>
    /// <param name="maxs">The box the merge works inside.</param>
    /// <param name="costLimit">What a merge may cost before the loop gives up.</param>
    /// <param name="budget">Clusters the loop aims at, the <c>param_5</c> argument.</param>
    /// <param name="padded">Whether to add the 56 shell boxes, which is on for
    /// cluster generation and off for every later pass.</param>
    /// <param name="cube">A leaf's cube, which the sampling needs.</param>
    public static float Run(
        RayTraceEnvironment scene, List<Cluster> clusters, Vector3 mins, Vector3 maxs,
        float costLimit, int budget, bool padded, VisClusterSample.LeafCube cube)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(clusters);
        if (budget >= clusters.Count)
            return costLimit;

        var shell = padded ? VisClusterSample.Shell.Length : 0;
        VisClusterSample.SampleInto(scene, clusters, mins, maxs, padded, cube);

        var live = clusters.Count + shell;
        var state = new Selection(clusters);

        // 1800337a0's loop tests the limit TWICE, and the first test is on the
        // cost of the pair it merged LAST rather than on the one it is about to
        // merge: while ((budget < live || best < limit) && (Cheapest(), budget <
        // live || cost < limit)). Absorbing rebuilds the survivor's candidates,
        // so a fresh cost can come out below a limit the run had already passed,
        // and testing only the new one keeps merging there. The seed is
        // DAT_18017f1e8, which is -1.
        var best = -1f;
        while (live > budget || best < costLimit)
        {
            var (owner, other, cost) = state.Cheapest();
            if (!(live > budget || cost < costLimit))
                break;
            if (owner < 0 || other < 0 || owner == other)
                break;
            best = cost;
            state.Absorb(owner, other);
            live--;
        }

        state.Keep(clusters);
        return MathF.Max(best, costLimit);
    }

    /// <summary>
    /// <c>180027e10</c>: order two boxes by longest side, then each extent, then
    /// the corner, taking the first key that differs. It reads only geometry,
    /// never an index, which is what makes every build that uses it reproducible
    /// however the pool's threads interleave.
    /// </summary>
    /// <param name="aMins">The first box.</param>
    /// <param name="aMaxs">The first box.</param>
    /// <param name="bMins">The second box.</param>
    /// <param name="bMaxs">The second box.</param>
    public static bool Before(Vector3 aMins, Vector3 aMaxs, Vector3 bMins, Vector3 bMaxs)
    {
        var one = aMaxs - aMins;
        var two = bMaxs - bMins;
        var longest = MathF.Max(MathF.Max(MathF.Abs(one.X), MathF.Abs(one.Y)), MathF.Abs(one.Z));
        var rival = MathF.Max(MathF.Max(MathF.Abs(two.X), MathF.Abs(two.Y)), MathF.Abs(two.Z));
        if (longest != rival)
            return longest < rival;
        if (one.X != two.X)
            return one.X < two.X;
        if (one.Y != two.Y)
            return one.Y < two.Y;
        if (one.Z != two.Z)
            return one.Z < two.Z;
        if (aMins.X != bMins.X)
            return aMins.X < bMins.X;
        if (aMins.Y != bMins.Y)
            return aMins.Y < bMins.Y;
        return aMins.Z < bMins.Z;
    }

    /// <summary>
    /// The candidate lists and the cheapest-pair scan, which are
    /// <c>180030df0</c>, <c>180031680</c> and <c>1800306e0</c> between them. A
    /// pair is stored once, in the HIGHER indexed cluster's list, which is also
    /// the one that survives the merge.
    /// </summary>
    private sealed class Selection
    {
        /// <summary>The slack a candidate query starts at (<c>DAT_18017f1e8</c> and <c>f118</c>).</summary>
        public const float Slack = 1f;

        /// <summary>How much cheaper a rival must be to displace the held candidate, <c>DAT_18017f0cc</c>.</summary>
        public const float Better = 1e-4f;

        /// <summary>The band two owners' costs count as equal in, <c>DAT_18017f0d0</c>.</summary>
        public const float Tie = 1e-3f;

        private readonly List<Cluster> _clusters;
        private readonly bool[] _alive;
        private readonly List<(int Other, float Cost)>[] _candidates;
        private readonly VisBoxTree _tree = new();
        private readonly int[] _proxy;
        private readonly List<int> _found = [];

        public Selection(List<Cluster> clusters)
        {
            _clusters = clusters;
            _alive = [.. Enumerable.Repeat(true, clusters.Count)];
            _candidates = [.. Enumerable.Range(0, clusters.Count).Select(_ => new List<(int, float)>())];

            // 1800337a0 puts every cluster in the tree with its OWN box, payload
            // its index, before any list is built.
            _proxy = new int[clusters.Count];
            for (var i = 0; i < clusters.Count; i++)
                _proxy[i] = _tree.Create(clusters[i].Mins, clusters[i].Maxs, i);
            for (var i = 0; i < clusters.Count; i++)
                Rebuild(i);
            Live = clusters.Count;
        }

        /// <summary>Clusters still standing.</summary>
        public int Live { get; private set; }

        /// <summary>
        /// The cheapest live pair, or (-1, -1, max) when none is left, which is
        /// <c>180031680</c>. Both levels of it break a tie towards the smaller
        /// side: a rival candidate no dearer than <see cref="Better"/> wins on
        /// <see cref="Preferred"/>, and two owners within <see cref="Tie"/> are
        /// separated by their combined voxel count.
        /// </summary>
        public (int Owner, int Other, float Cost) Cheapest()
        {
            var best = (Owner: -1, Other: -1, Cost: float.MaxValue);
            for (var owner = 0; owner < _clusters.Count; owner++)
            {
                if (!_alive[owner])
                    continue;

                var mine = (Other: -1, Cost: float.MaxValue);
                foreach (var (other, cost) in _candidates[owner])
                {
                    if (!_alive[other])
                        continue;
                    if (mine.Other < 0)
                    {
                        mine = (other, cost);
                        continue;
                    }
                    var gain = mine.Cost - cost;
                    if (gain >= -Better && (gain >= Better || Preferred(other, mine.Other)))
                        mine = (other, cost);
                }
                if (mine.Other < 0)
                    continue;

                if (best.Owner >= 0 && MathF.Abs(mine.Cost - best.Cost) < Tie
                    && Bulk(owner, mine.Other) < Bulk(best.Owner, best.Other))
                    best = (owner, mine.Other, mine.Cost);

                if (mine.Cost < best.Cost)
                    best = (owner, mine.Other, mine.Cost);
            }
            return best;
        }

        private int Bulk(int a, int b) => _clusters[a].VoxelCount + _clusters[b].VoxelCount;

        /// <summary>
        /// <c>180030d70</c>: the rival wins when the incumbent is gone, when it
        /// has fewer voxels, or when the counts match and its box sorts first.
        /// It reads only geometry, never an index, which is what makes the build
        /// reproducible however the pool's threads interleave.
        /// </summary>
        private bool Preferred(int candidate, int incumbent) =>
            !_alive[incumbent]
            || _clusters[incumbent].VoxelCount > _clusters[candidate].VoxelCount
            || Before(_clusters[candidate], _clusters[incumbent]);

        private static bool Before(Cluster a, Cluster b)
            => VisMerge.Before(a.Mins, a.Maxs, b.Mins, b.Maxs);

        /// <summary>Merge one pair and rebuild the survivor's candidates.</summary>
        public void Absorb(int owner, int other)
        {
            _clusters[owner].Absorb(_clusters[other]);
            // 180030a50 moves the survivor to its new box and destroys the other
            // BEFORE rebuilding, so the rebuild queries a tree that already has
            // the union in it and no longer has the absorbed cluster.
            _tree.Move(_proxy[owner], _clusters[owner].Mins, _clusters[owner].Maxs);
            _tree.Destroy(_proxy[other]);
            _alive[other] = false;
            _candidates[other].Clear();
            for (var i = 0; i < _candidates.Length; i++)
                _candidates[i].RemoveAll(c => c.Other == other);
            Rebuild(owner);
            Live--;
        }

        /// <summary>Rewrite the caller's list to the survivors, in order.</summary>
        public void Keep(List<Cluster> into)
        {
            var kept = new List<Cluster>(Live);
            for (var i = 0; i < _clusters.Count; i++)
                if (_alive[i])
                    kept.Add(_clusters[i]);
            into.Clear();
            into.AddRange(kept);
        }

        /// <summary>
        /// <c>1800306e0</c>: clear the owner's list, query the tree with its box
        /// grown by <see cref="Slack"/>, grow by the compile's voxel size up to
        /// four more times while the query still finds nothing but the owner
        /// itself, and price everything it came back with.
        ///
        /// <para>The order the query returns them in is load bearing. A rival no
        /// dearer than <see cref="Better"/> displaces the held candidate, so
        /// which of two equally priced pairs a cluster ends up holding is decided
        /// by which the traversal reached last. That is why this reads the tree
        /// rather than scanning the set by index.</para>
        /// </summary>
        private void Rebuild(int owner)
        {
            _candidates[owner].Clear();
            if (_clusters.Count <= 1)
                return;

            var slack = Slack;
            Query(owner, slack);
            for (var attempt = 0; attempt < 4 && _found.Count <= 1; attempt++)
            {
                slack += VisClusters.BaseVoxelSize;
                Query(owner, slack);
            }

            foreach (var other in _found)
            {
                if (other == owner || (uint)other >= (uint)_clusters.Count || !_alive[other])
                    continue;
                var cost = VisMergeCost.Of(Read(owner), Read(other));
                var (holder, id) = owner < other ? (other, owner) : (owner, other);
                var list = _candidates[holder];
                var at = list.FindIndex(c => c.Other == id);
                if (at >= 0)
                    list[at] = (id, cost);
                else
                    list.Add((id, cost));
            }
        }

        private void Query(int owner, float slack)
        {
            var grow = new Vector3(slack);
            _found.Clear();
            _tree.Query(_clusters[owner].Mins - grow, _clusters[owner].Maxs + grow, _found);
        }

        private VisMergeCost.Cluster Read(int at)
        {
            var c = _clusters[at];
            return new VisMergeCost.Cluster(c.Visibility, c.VoxelCount, c.VoxelSize, c.Tag, c.Mins, c.Maxs);
        }
    }
}
