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
        var best = -1f;
        while (true)
        {
            var (owner, other, cost) = state.Cheapest();
            if (owner < 0 || (live <= budget && cost >= costLimit))
                break;
            best = cost;
            state.Absorb(owner, other);
            live--;
            if (state.Live == 1)
                break;
        }

        state.Keep(clusters);
        return MathF.Max(best, costLimit);
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

        public Selection(List<Cluster> clusters)
        {
            _clusters = clusters;
            _alive = [.. Enumerable.Repeat(true, clusters.Count)];
            _candidates = [.. Enumerable.Range(0, clusters.Count).Select(_ => new List<(int, float)>())];
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

        /// <summary>
        /// <c>180027e10</c>: order two boxes by longest side, then each extent,
        /// then the corner, taking the first key that differs.
        /// </summary>
        private static bool Before(Cluster a, Cluster b)
        {
            var one = a.Maxs - a.Mins;
            var two = b.Maxs - b.Mins;
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
            if (a.Mins.X != b.Mins.X)
                return a.Mins.X < b.Mins.X;
            if (a.Mins.Y != b.Mins.Y)
                return a.Mins.Y < b.Mins.Y;
            return a.Mins.Z < b.Mins.Z;
        }

        /// <summary>Merge one pair and rebuild the survivor's candidates.</summary>
        public void Absorb(int owner, int other)
        {
            _clusters[owner].Absorb(_clusters[other]);
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

        private void Rebuild(int owner)
        {
            _candidates[owner].Clear();
            // 1800306e0 starts one unit out and grows by BaseVoxelSize, up to
            // four more times, while the query still finds nothing but itself.
            var slack = Slack;
            for (var attempt = 0; attempt < 4 && Reached(owner, slack) == 0; attempt++)
                slack += VisClusters.BaseVoxelSize;

            for (var other = 0; other < _clusters.Count; other++)
            {
                if (other == owner || !_alive[other] || !Touches(owner, slack, other))
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

        private int Reached(int owner, float slack)
        {
            var found = 0;
            for (var other = 0; other < _clusters.Count; other++)
                if (other != owner && _alive[other] && Touches(owner, slack, other))
                    found++;
            return found;
        }

        private bool Touches(int owner, float slack, int other)
        {
            var a = _clusters[owner];
            var b = _clusters[other];
            return a.Mins.X - slack <= b.Maxs.X && a.Maxs.X + slack >= b.Mins.X
                && a.Mins.Y - slack <= b.Maxs.Y && a.Maxs.Y + slack >= b.Mins.Y
                && a.Mins.Z - slack <= b.Maxs.Z && a.Maxs.Z + slack >= b.Mins.Z;
        }

        private VisMergeCost.Cluster Read(int at)
        {
            var c = _clusters[at];
            return new VisMergeCost.Cluster(c.Visibility, c.VoxelCount, c.VoxelSize, c.Tag, c.Mins, c.Maxs);
        }
    }
}
