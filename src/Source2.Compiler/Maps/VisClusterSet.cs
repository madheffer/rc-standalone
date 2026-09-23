using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The five merge passes that take a map's clusters from one set per region down
/// to the few hundred it ships with, which is <c>180034220</c> and
/// <c>CVoxelSampler3::MergeClusterSet</c> at <c>180033fd0</c>.
///
/// <para>Each pass re-buckets every cluster into a 2D grid over the scene and
/// merges within a cell. The grid is 2D on purpose: only x and y are in the key,
/// and a cell's box spans the whole of z.</para>
/// </summary>
public static class VisClusterSet
{
    /// <summary>One bucket: the clusters in it and the box they merge inside.</summary>
    public sealed class Set
    {
        /// <summary>Its clusters, rewritten in place by every merge.</summary>
        public List<VisMerge.Cluster> Clusters { get; init; } = [];

        /// <summary>The box the merge samples inside.</summary>
        public Vector3 Mins { get; set; }

        /// <summary>The box the merge samples inside.</summary>
        public Vector3 Maxs { get; set; }
    }

    /// <summary>The cell size, margin and budget multiplier of each of the five passes.</summary>
    /// <param name="Cell">Grid cell in world units.</param>
    /// <param name="Margin">Added to the scene box before the grid is laid out.</param>
    /// <param name="Budget">Multiple of the cluster target this pass aims at.</param>
    public readonly record struct Pass(float Cell, float Margin, float Budget);

    /// <summary>
    /// The five calls <c>MergeInsideRegions</c> makes, in order. The cost limit
    /// is not here because it is a chain: the first is
    /// <see cref="VisClusters.MergeThreshold"/> and each later one is whatever
    /// the pass before it returned.
    /// </summary>
    public static readonly Pass[] Passes =
    [
        new(512f, 0f, 6f),
        new(512f, 256f, 5.75f),
        new(2048f, 0f, 4.5f),
        new(2048f, 1024f, 4.25f),
        new(4096f, 0f, 3f),
    ];

    /// <summary>Run all five over a map's sets and report what is left.</summary>
    public static int MergeAll(
        RayTraceEnvironment scene, List<Set> sets, int target,
        VisClusterSample.LeafCube cube, Action<int, float>? after = null,
        Action<VisMerge.Halt>? halted = null, Action<VisMerge.Halt>? first = null,
        Action<int, IReadOnlyList<Set>>? entering = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(sets);

        var cost = VisClusters.MergeThreshold;
        for (var i = 0; i < Passes.Length; i++)
        {
            var pass = Passes[i];
            entering?.Invoke(i, sets);
            cost = Regrid(scene, sets, pass.Cell, pass.Margin, cost,
                          (int)(target * pass.Budget), cube, halted, first);
            after?.Invoke(sets.Sum(s => s.Clusters.Count), cost);
        }
        return sets.Sum(s => s.Clusters.Count);
    }

    /// <summary>
    /// One pass: re-bucket every cluster into a grid of <paramref name="cell"/>
    /// units and merge within each cell. Returns the average cost the merges
    /// stopped at, which is the next pass's limit.
    /// </summary>
    public static float Regrid(
        RayTraceEnvironment scene, List<Set> sets,
        float cell, float margin, float costLimit, int budget, VisClusterSample.LeafCube cube,
        Action<VisMerge.Halt>? halted = null, Action<VisMerge.Halt>? first = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(sets);

        // The grid is laid over the TRACER's bounds, the object at sampler+0xe8,
        // not the file header's.
        var (sceneMins, sceneMaxs) = scene.TracedBounds;
        var bucketed = Bucket(sceneMins, sceneMaxs, sets, cell, margin);
        if (bucketed.Count == 0)
            return costLimit;
        var perCell = (int)Math.Ceiling((double)budget / bucketed.Count);
        var cost = MergeClusterSet(scene, bucketed, perCell, costLimit, cube, halted, first);

        sets.Clear();
        sets.AddRange(bucketed);
        return cost;
    }

    /// <summary>
    /// <c>Regrid</c>'s bucketing alone: every cluster, in set order, keyed by its
    /// box centre's cell, with a bucket's index the order its key first appears.
    /// </summary>
    public static List<Set> Bucket(Vector3 sceneMins, Vector3 sceneMaxs, IEnumerable<Set> sets,
                                   float cell, float margin)
    {
        ArgumentNullException.ThrowIfNull(sets);
        var half = margin * 0.5f;
        var inv = 1f / cell;
        var originX = MathF.Floor((sceneMins.X - half) * inv) * cell;
        var originY = MathF.Floor((sceneMins.Y - half) * inv) * cell;
        var lowZ = MathF.Floor((sceneMins.Z - half) * inv);
        var highZ = MathF.Ceiling((sceneMaxs.Z + half) * inv);
        var acrossX = (int)(MathF.Max((MathF.Ceiling((sceneMaxs.X + half) * inv) * cell) - originX, 0f) * inv);
        var acrossY = (int)(MathF.Max((MathF.Ceiling((sceneMaxs.Y + half) * inv) * cell) - originY, 0f) * inv);
        if (acrossX < 1 || acrossY < 1)
            return [];

        var order = new List<int>();
        var byKey = new Dictionary<int, Set>();
        foreach (var cluster in sets.SelectMany(s => s.Clusters))
        {
            var centre = cluster.Centre;
            var key = ((int)(uint)(long)((centre.Y - originY) * inv) % acrossY * acrossX)
                    + ((int)(uint)(long)((centre.X - originX) * inv) % acrossX);
            if (!byKey.TryGetValue(key, out var set))
            {
                var corner = new Vector3(((float)(key % acrossX) * cell) + originX,
                                         ((float)(key / acrossX % acrossY) * cell) + originY,
                                         // The compiler doubles this one, and it is
                                         // implemented as it is rather than as the
                                         // floor it looks like: the box only has to
                                         // contain the cell, and the reach the merge
                                         // samples with is its diagonal.
                                         (lowZ * cell) + (lowZ * cell));
                set = new Set
                {
                    Mins = corner,
                    Maxs = new Vector3(corner.X + cell, corner.Y + cell, highZ * cell),
                };
                byKey[key] = set;
                order.Add(key);
            }
            set.Clusters.Add(cluster);
        }
        return [.. order.Select(k => byKey[k])];
    }

    /// <summary>
    /// <c>CVoxelSampler3::MergeClusterSet</c>: merge every bucket at the incoming
    /// limit with DOUBLE the budget, take the average cost that produced, then
    /// merge again at that average with the single budget.
    /// </summary>
    public static float MergeClusterSet(
        RayTraceEnvironment scene, List<Set> sets, int budget, float costLimit,
        VisClusterSample.LeafCube cube, Action<VisMerge.Halt>? halted = null,
        Action<VisMerge.Halt>? first = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(sets);
        if (sets.Count == 0)
            return costLimit;

        var costs = new float[sets.Count];
        Parallel.For(0, sets.Count, i =>
            costs[i] = VisMerge.Run(scene, sets[i].Clusters, sets[i].Mins, sets[i].Maxs,
                                    costLimit, budget * 2, padded: false, cube, first));

        // A float running sum in bucket order, then one divide, as the compile
        // does it; LINQ's Sum accumulates in double and lands a bit elsewhere.
        var total = 0f;
        foreach (var c in costs)
            total += c;
        var average = total / sets.Count;
        Parallel.For(0, sets.Count, i =>
            VisMerge.Run(scene, sets[i].Clusters, sets[i].Mins, sets[i].Maxs,
                         average, budget, padded: false, cube, halted));
        return average;
    }
}
