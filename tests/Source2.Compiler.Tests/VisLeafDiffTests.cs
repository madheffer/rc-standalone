using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Our leaves against the SHIPPED ones, leaf by leaf.
///
/// <para>A count that is 49% short says nothing about where the 49% went. The
/// compiled file carries its own leaves, their regions and each region's mask, so
/// the two trees can be put side by side and the disagreement located rather than
/// guessed at. The shipped tree is collapsed, so ours is folded up to it: a
/// shipped leaf is compared against every one of our leaves inside it.</para>
/// </summary>
public class VisLeafDiffTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("s2c_lighting", "ze_hold_em_p")]
    [InlineData("s2c_rc_probe", "probe01")]
    public void WhereOurLeavesAndTheShippedOnesDisagree(string addon, string map)
    {
        if (VisFixtures.RayTraceScene(addon, map) is not var (rte, valve))
            return;

        var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
        var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                 / VisVoxelizer.VoxelsPerLeaf;
        var depth = (int)Math.Log2(side);
        var regions = VisRegions.Build(tree, side);
        var inside = VisOutside.Detect(tree, regions, rte, valve.GridSize);

        var shipped = ShippedLeaves(valve, depth);
        var shippedRegions = shipped.Values.Sum(r => r.Count);
        var withCluster = shipped.Values.Sum(r => r.Count(x => !valve.ClusterIsUnused(x.ClusterId)));

        output.WriteLine($"{map}");
        output.WriteLine($"  shipped   {shipped.Count,7:n0} leaves  {shippedRegions,7:n0} regions"
                       + $"  {withCluster,7:n0} of them on a live cluster"
                       + $"  {valve.Nodes.Length,7:n0} nodes  {valve.BaseClusterCount,5:n0} clusters");
        output.WriteLine($"  ours      {regions.Leaves.Count,7:n0} leaves  {regions.Regions.Count,7:n0} regions"
                       + $"  {inside.Inside,7:n0} enclosed  {tree.Nodes,7:n0} nodes");

        // Fold ours up to the shipped leaf that contains it, and see whether the
        // two agree on the only thing that matters here: does this box hold any
        // space the map encloses.
        var oursByShipped = new Dictionary<(int Level, (int X, int Y, int Z) Cell), (int Regions, int Inside)>();
        for (var i = 0; i < regions.Regions.Count; i++)
        {
            var leaf = regions.Leaves[regions.Regions[i].Leaf];
            var key = Fold(leaf.Level, leaf.Cell, shipped);
            if (key is null)
                continue;
            var at = oursByShipped.GetValueOrDefault(key.Value);
            oursByShipped[key.Value] = (at.Regions + 1,
                at.Inside + (inside.Regions[i] == VisOutside.Status.Inside ? 1 : 0));
        }

        int bothLive = 0, onlyShipped = 0, onlyOurs = 0, bothDead = 0;
        var samples = new List<string>();
        foreach (var (key, theirs) in shipped)
        {
            var live = theirs.Any(r => !valve.ClusterIsUnused(r.ClusterId));
            var mine = oursByShipped.GetValueOrDefault(key).Inside > 0;
            if (live && mine) bothLive++;
            else if (live) { onlyShipped++; if (samples.Count < 8) samples.Add(Where(tree, key, theirs.Count)); }
            else if (mine) onlyOurs++;
            else bothDead++;
        }
        output.WriteLine($"  agree live {bothLive,7:n0}   agree dead {bothDead,7:n0}"
                       + $"   only shipped {onlyShipped,7:n0}   only ours {onlyOurs,7:n0}");
        foreach (var sample in samples)
            output.WriteLine($"    shipped-only leaf {sample}");
    }

    /// <summary>The shipped leaf containing one of ours, or null when outside the tree.</summary>
    private static (int Level, (int X, int Y, int Z) Cell)? Fold(
        int level, (int X, int Y, int Z) cell,
        Dictionary<(int Level, (int X, int Y, int Z) Cell), List<VisRegion>> shipped)
    {
        for (var at = level; at < 32; at++)
        {
            if (shipped.ContainsKey((at, cell)))
                return (at, cell);
            cell = (cell.X >> 1, cell.Y >> 1, cell.Z >> 1);
        }
        return null;
    }

    private static string Where(VisVoxelizer.Octree tree, (int Level, (int X, int Y, int Z) Cell) key, int regions)
    {
        var size = tree.LeafSize * (1 << key.Level);
        var at = tree.Origin + new System.Numerics.Vector3(key.Cell.X, key.Cell.Y, key.Cell.Z) * size;
        return $"level {key.Level} cell {key.Cell} at {at} size {size} with {regions} region(s)";
    }

    /// <summary>Every leaf of the compiled tree, with the regions it owns.</summary>
    private static Dictionary<(int Level, (int X, int Y, int Z) Cell), List<VisRegion>> ShippedLeaves(
        VoxelVisibility vis, int depth)
    {
        var found = new Dictionary<(int, (int, int, int)), List<VisRegion>>();
        var stack = new Stack<(int Index, int Level, (int X, int Y, int Z) Cell)>();
        stack.Push((0, depth, (0, 0, 0)));
        while (stack.Count > 0)
        {
            var (index, level, cell) = stack.Pop();
            var node = vis.Nodes[index];
            if (node.IsLeaf || level == 0)
            {
                var owned = new List<VisRegion>();
                for (var r = 0; r < node.RegionCount; r++)
                    owned.Add(vis.Regions[node.Offset + r]);
                found[(level, cell)] = owned;
                continue;
            }
            for (var octant = 0; octant < 8; octant++)
                stack.Push(((int)node.Offset + octant, level - 1,
                    (cell.X * 2 + (octant & 1), cell.Y * 2 + ((octant >> 1) & 1),
                     cell.Z * 2 + ((octant >> 2) & 1))));
        }
        return found;
    }
}
