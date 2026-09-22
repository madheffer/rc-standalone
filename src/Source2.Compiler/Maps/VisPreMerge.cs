using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The distance pre-merge, <c>18002f5c0</c>, which runs between cluster
/// generation and the five merge passes and prints
/// <c>Distance merged regions (%d merged to %d)</c>.
///
/// <para>It takes the sets that produced exactly ONE cluster carrying the open
/// space flag, groups the ones that sit face to face into runs, and folds each
/// run into its first member. So a corridor that voxelized into a row of
/// identical open boxes becomes one cluster before the cost driven merge ever
/// sees it.</para>
///
/// <para>The stage is gated on <c>sampler+0xf4</c>, which the setup sets only
/// when <c>PreMergeOpenSpaceDistanceThreshold</c> is positive. The BINARY's
/// default for that key is -1, so reading the binary alone says this never runs;
/// the shipped config sets it to 128 and the compile prints the line on every
/// map. The threshold itself is only the switch -- nothing inside reads it. What
/// does the work is <c>PreMergeOpenSpaceMaxDimension</c> and
/// <c>PreMergeOpenSpaceMaxRatio</c>, whose binary defaults ARE used.</para>
/// </summary>
public static class VisPreMerge
{
    /// <summary>
    /// Longest side a merged box may have, <c>PreMergeOpenSpaceMaxDimension</c>
    /// defaulting to <c>DAT_18017f1c0</c>.
    /// </summary>
    public const float MaxDimension = 1024f;

    /// <summary>
    /// How many times longer than thin a merged box may be,
    /// <c>PreMergeOpenSpaceMaxRatio</c> defaulting to <c>DAT_18017f154</c>.
    /// </summary>
    public const float MaxRatio = 4f;

    /// <summary>How near two faces must line up to count as shared (<c>DAT_18017f0dc</c>).</summary>
    public const float FaceTolerance = 0.0125f;

    /// <summary>How near two faces must be to count as touching (<c>DAT_18017f0f0</c>).</summary>
    public const float TouchTolerance = 0.25f;

    /// <summary>Floor on the short side before it divides, <c>DAT_18017f0cc</c>.</summary>
    public const float RatioFloor = 1e-4f;

    /// <summary>What a query box is grown by, <c>DAT_18017f1e8</c> and <c>f118</c>.</summary>
    public const float Slack = 1f;

    /// <summary>What the stage did.</summary>
    /// <param name="Before">Runs it started with, the first number in the log line.</param>
    /// <param name="After">Runs it ended with, the second.</param>
    /// <param name="Volume">Their boxes' volume summed, which <c>180031f00</c>
    /// uses to pull the cluster target down when the runs came out big.</param>
    public readonly record struct Result(int Before, int After, double Volume);

    private sealed class Item
    {
        public Vector3 Mins;
        public Vector3 Maxs;
        public List<int> Members = [];
        public int Proxy;
        public int Index;
        public bool Used;
    }

    /// <summary>
    /// Run it over a map's sets, rewriting them in place, and report what it did.
    /// </summary>
    /// <param name="sets">The sets cluster generation produced.</param>
    public static Result Run(IReadOnlyList<VisClusterSet.Set> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var tree = new VisBoxTree();
        var live = new List<Item?>();
        for (var i = 0; i < sets.Count; i++)
        {
            if (sets[i].Clusters.Count != 1 || !sets[i].Clusters[0].OpenSpace)
                continue;
            var cluster = sets[i].Clusters[0];
            var item = new Item { Mins = cluster.Mins, Maxs = cluster.Maxs, Members = [i] };
            item.Proxy = tree.Create(item.Mins, item.Maxs, live.Count);
            live.Add(item);
        }

        var started = live.Count;
        while (true)
        {
            // 180028c70 compacts the nulls out and renumbers before every round,
            // and the index it writes is what a candidate is named by.
            live.RemoveAll(x => x is null);
            for (var i = 0; i < live.Count; i++)
            {
                live[i]!.Index = i;
                live[i]!.Used = false;
            }
            if (!Round(tree, live))
                break;
        }

        var volume = 0.0;
        foreach (var item in live)
        {
            var size = item!.Maxs - item.Mins;
            volume += (double)(size.X * size.Y * size.Z);
            var owner = sets[item.Members[0]];
            foreach (var other in item.Members.Skip(1))
            {
                foreach (var cluster in sets[other].Clusters)
                    owner.Clusters[0].Absorb(cluster);
                sets[other].Clusters.Clear();
            }
        }
        return new Result(started, live.Count, volume);
    }

    /// <summary>
    /// <c>CBoxMerge::MergeBestCandidates</c> (<c>180027f50</c>): price every
    /// live run's best partner, take the cheapest price anyone offered, and merge
    /// EVERY pair at that price in one go. Each run may take part once per round,
    /// which is what the used flag is for.
    /// </summary>
    private static bool Round(VisBoxTree tree, List<Item?> live)
    {
        var best = new int[live.Count];
        var cost = new float[live.Count];
        Parallel.For(0, live.Count, i => (best[i], cost[i]) = Cheapest(tree, live, i));

        var cheapest = float.MaxValue;
        for (var i = 0; i < live.Count; i++)
            if (best[i] >= 0 && cost[i] < cheapest)
                cheapest = cost[i];
        if (cheapest >= float.MaxValue)
            return false;

        var pairs = new List<(Vector3 Mins, Vector3 Maxs, int Owner, int Other)>();
        for (var i = 0; i < live.Count; i++)
        {
            if (best[i] < 0 || cost[i] > cheapest || live[i] is null || live[best[i]] is null)
                continue;
            pairs.Add((Vector3.Min(live[i]!.Mins, live[best[i]]!.Mins),
                       Vector3.Max(live[i]!.Maxs, live[best[i]]!.Maxs), i, best[i]));
        }

        // 1800294f0 sorts them, and its comparator is 180027e10 over the union box.
        pairs.Sort((a, b) => VisMerge.Before(a.Mins, a.Maxs, b.Mins, b.Maxs) ? -1
                           : VisMerge.Before(b.Mins, b.Maxs, a.Mins, a.Maxs) ? 1 : 0);

        foreach (var (_, _, owner, other) in pairs)
        {
            var keep = live[owner];
            var drop = live[other];
            if (keep is null || drop is null || keep.Used || drop.Used)
                continue;

            live[other] = null;
            keep.Mins = Vector3.Min(keep.Mins, drop.Mins);
            keep.Maxs = Vector3.Max(keep.Maxs, drop.Maxs);
            keep.Members.AddRange(drop.Members);
            keep.Used = true;
            tree.Move(keep.Proxy, keep.Mins, keep.Maxs);
            tree.Destroy(drop.Proxy);
        }
        return true;
    }

    /// <summary>
    /// <c>1800284a0</c>, one run's best partner: query the tree with the run's box
    /// grown by <see cref="Slack"/> and keep the partner whose UNION is smallest
    /// by volume, then by aspect ratio, then by the box order.
    /// </summary>
    private static (int Best, float Cost) Cheapest(VisBoxTree tree, List<Item?> live, int at)
    {
        var mine = live[at];
        if (mine is null)
            return (-1, float.MaxValue);

        var grow = new Vector3(Slack);
        var found = new List<int>();
        tree.Query(mine.Mins - grow, mine.Maxs + grow, found);

        var best = -1;
        var bestVolume = float.MaxValue;
        var bestRatio = float.MaxValue;
        Vector3 bestMins = default, bestMaxs = default;

        foreach (var index in found)
        {
            if (index == at || (uint)index >= (uint)live.Count || live[index] is not { } theirs)
                continue;

            var mins = Vector3.Min(mine.Mins, theirs.Mins);
            var maxs = Vector3.Max(mine.Maxs, theirs.Maxs);
            var size = maxs - mins;
            var longest = MathF.Max(MathF.Max(size.X, size.Y), size.Z);
            var shortest = MathF.Min(MathF.Min(size.X, size.Y), size.Z);
            if (longest > MaxDimension || longest > shortest * MaxRatio || !Adjacent(mine, theirs))
                continue;

            var volume = MathF.Ceiling(size.Y * size.X * size.Z);
            var ratio = longest / MathF.Max(shortest, RatioFloor);
            if (best >= 0 && (volume > bestVolume
                || (volume == bestVolume && (ratio > bestRatio
                    || (ratio == bestRatio && !VisMerge.Before(mins, maxs, bestMins, bestMaxs))))))
                continue;

            best = theirs.Index;
            bestVolume = volume;
            bestRatio = ratio;
            bestMins = mins;
            bestMaxs = maxs;
        }
        return (best, best < 0 ? float.MaxValue : bestVolume);
    }

    /// <summary>
    /// Whether two boxes may merge at all: they share a face on one axis, within
    /// <see cref="FaceTolerance"/> on the other two and
    /// <see cref="TouchTolerance"/> along it, or one contains the other.
    /// </summary>
    private static bool Adjacent(Item a, Item b)
    {
        bool Face(float one, float two) => MathF.Abs(one - two) <= FaceTolerance;
        bool Touch(float one, float two) => MathF.Abs(one - two) <= TouchTolerance;

        var sameX = Face(a.Mins.X, b.Mins.X) && Face(a.Maxs.X, b.Maxs.X);
        var sameY = Face(a.Mins.Y, b.Mins.Y) && Face(a.Maxs.Y, b.Maxs.Y);
        var sameZ = Face(a.Mins.Z, b.Mins.Z) && Face(a.Maxs.Z, b.Maxs.Z);

        if (sameX && sameY && (Touch(a.Mins.Z, b.Maxs.Z) || Touch(a.Maxs.Z, b.Mins.Z)))
            return true;
        if (sameX && sameZ && (Touch(a.Mins.Y, b.Maxs.Y) || Touch(a.Maxs.Y, b.Mins.Y)))
            return true;
        if (sameY && sameZ && (Touch(a.Mins.X, b.Maxs.X) || Touch(a.Maxs.X, b.Mins.X)))
            return true;

        return (a.Mins.X <= b.Mins.X && a.Mins.Y <= b.Mins.Y && a.Mins.Z <= b.Mins.Z
                && b.Maxs.X <= a.Maxs.X && b.Maxs.Y <= a.Maxs.Y && b.Maxs.Z <= a.Maxs.Z)
            || (b.Mins.X <= a.Mins.X && b.Mins.Y <= a.Mins.Y && b.Mins.Z <= a.Mins.Z
                && a.Maxs.X <= b.Maxs.X && a.Maxs.Y <= b.Maxs.Y && a.Maxs.Z <= b.Maxs.Z);
    }
}
