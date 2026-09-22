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
        RayTraceEnvironment scene, List<Set> sets, int target, Action<int, float>? after = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(sets);

        var cost = VisClusters.MergeThreshold;
        for (var i = 0; i < Passes.Length; i++)
        {
            var pass = Passes[i];
            cost = Regrid(scene, sets, pass.Cell, pass.Margin, cost, (int)(target * pass.Budget));
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
        float cell, float margin, float costLimit, int budget)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(sets);

        var half = margin * 0.5f;
        var inv = 1f / cell;
        var originX = MathF.Floor((scene.Mins.X - half) * inv) * cell;
        var originY = MathF.Floor((scene.Mins.Y - half) * inv) * cell;
        var lowZ = MathF.Floor((scene.Mins.Z - half) * inv);
        var highZ = MathF.Ceiling((scene.Maxs.Z + half) * inv);
        var acrossX = (int)((MathF.Ceiling((scene.Maxs.X + half) * inv) * cell - originX) * inv);
        var acrossY = (int)((MathF.Ceiling((scene.Maxs.Y + half) * inv) * cell - originY) * inv);
        if (acrossX < 1 || acrossY < 1)
            return costLimit;

        var order = new List<int>();
        var byKey = new Dictionary<int, Set>();
        foreach (var cluster in sets.SelectMany(s => s.Clusters))
        {
            var centre = cluster.Centre;
            var key = ((int)((centre.Y - originY) * inv) % acrossY * acrossX)
                    + ((int)((centre.X - originX) * inv) % acrossX);
            if (!byKey.TryGetValue(key, out var set))
            {
                var corner = new Vector3(originX + (key % acrossX * cell),
                                         originY + (key / acrossX % acrossY * cell),
                                         // The compiler doubles this one, and it is
                                         // implemented as it is rather than as the
                                         // floor it looks like: the box only has to
                                         // contain the cell, and the reach the merge
                                         // samples with is its diagonal.
                                         lowZ * cell * 2f);
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

        var bucketed = order.Select(k => byKey[k]).ToList();
        var perCell = (int)Math.Ceiling((double)budget / bucketed.Count);
        var cost = MergeClusterSet(scene, bucketed, perCell, costLimit);

        sets.Clear();
        sets.AddRange(bucketed);
        return cost;
    }

    /// <summary>
    /// <c>CVoxelSampler3::MergeClusterSet</c>: merge every bucket at the incoming
    /// limit with DOUBLE the budget, take the average cost that produced, then
    /// merge again at that average with the single budget.
    /// </summary>
    public static float MergeClusterSet(
        RayTraceEnvironment scene, List<Set> sets, int budget, float costLimit)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(sets);
        if (sets.Count == 0)
            return costLimit;

        var costs = new float[sets.Count];
        Parallel.For(0, sets.Count, i =>
            costs[i] = VisMerge.Run(scene, sets[i].Clusters, sets[i].Mins, sets[i].Maxs,
                                    costLimit, budget * 2, padded: false));

        var average = costs.Sum() / sets.Count;
        Parallel.For(0, sets.Count, i =>
            VisMerge.Run(scene, sets[i].Clusters, sets[i].Mins, sets[i].Maxs,
                         average, budget, padded: false));
        return average;
    }
}
