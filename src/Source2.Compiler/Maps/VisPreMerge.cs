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
    /// Longest side a merged box may have. Read out of the live compile, where
    /// <c>BestPartner</c> is handed 2,048: that is sampler+0xfc, the shipped
    /// <c>PreMergeOpenSpaceMaxDimension</c> from csgo_core/gameinfo.gi, not the
    /// binary's 1,024.
    /// </summary>
    public const float MaxDimension = 2048f;

    /// <summary>
    /// The binary's own default for <see cref="MaxDimension"/> (<c>DAT_18017f1c0</c>),
    /// which the shipped gameinfo overrides; kept so the constant test can still
    /// check what the image holds.
    /// </summary>
    public const float BinaryMaxDimension = 1024f;

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
        public bool Dead;
    }

    /// <summary>
    /// Run it over a map's sets, rewriting them in place, and report what it did.
    /// </summary>
    /// <param name="sets">The sets cluster generation produced.</param>
    /// <param name="round">Called at the start of every round with the runs in order, for replay.</param>
    public static Result Run(IReadOnlyList<VisClusterSet.Set> sets,
                             Action<IReadOnlyList<(Vector3 Mins, Vector3 Maxs)>>? round = null)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var tree = new VisBoxTree();
        var live = new List<Item?>();
        // The tree's payload is a run's creation number, which compaction does
        // not touch; the compile's tree hands back the run record itself and
        // reads its CURRENT index from it, so a payload is looked up here.
        var made = new List<Item>();
        for (var i = 0; i < sets.Count; i++)
        {
            if (sets[i].Clusters.Count != 1 || !sets[i].Clusters[0].OpenSpace)
                continue;
            var cluster = sets[i].Clusters[0];
            var item = new Item { Mins = cluster.Mins, Maxs = cluster.Maxs, Members = [i] };
            item.Proxy = tree.Create(item.Mins, item.Maxs, made.Count);
            made.Add(item);
            live.Add(item);
        }

        var started = live.Count;
        while (true)
        {
            // GroupByDistance compacts before every round, walking from the end
            // and moving the LAST run into each empty slot, then renumbers. The
            // index it writes is what a candidate is named by, so the order is
            // kept as it does it rather than as a stable removal would.
            for (var i = live.Count - 1; i >= 0; i--)
            {
                if (live[i] is not null)
                    continue;
                if (i != live.Count - 1)
                    live[i] = live[^1];
                live.RemoveAt(live.Count - 1);
            }
            for (var i = 0; i < live.Count; i++)
            {
                live[i]!.Index = i;
                live[i]!.Used = false;
            }
            round?.Invoke([.. live.Select(x => (x!.Mins, x.Maxs))]);
            if (!Round(tree, live, made))
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
    private static bool Round(VisBoxTree tree, List<Item?> live, List<Item> made)
    {
        var best = new int[live.Count];
        var cost = new float[live.Count];
        Parallel.For(0, live.Count, i => (best[i], cost[i]) = Cheapest(tree, live, made, i));

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

        // SortPairs is std::sort with 180027e10 over the union box. Two runs that
        // chose each other make two pairs with the same union, and which comes
        // first decides which run keeps the merge, so the sort is MSVC's own.
        var sorted = pairs.ToArray();
        MsvcSort.Sort(sorted, (a, b) => VisMerge.Before(a.Mins, a.Maxs, b.Mins, b.Maxs));

        foreach (var (_, _, owner, other) in sorted)
        {
            var keep = live[owner];
            var drop = live[other];
            if (keep is null || drop is null || keep.Used || drop.Used)
                continue;

            live[other] = null;
            drop.Dead = true;
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
    private static (int Best, float Cost) Cheapest(VisBoxTree tree, List<Item?> live, List<Item> made, int at)
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
            if ((uint)index >= (uint)made.Count || made[index] is not { Dead: false } theirs || theirs == mine)
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
            // At an equal volume the binary takes a better ratio outright, and
            // otherwise, WORSE ratio included, whichever union box sorts first.
            if (best >= 0 && (volume > bestVolume
                || (volume == bestVolume && !(ratio < bestRatio)
                    && !VisMerge.Before(mins, maxs, bestMins, bestMaxs))))
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
