using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// Stage 3 of visibility, as far as it is established: the octree's leaves, and
/// the open space inside each of them cut into regions.
///
/// <para>A region is one connected run of open voxels within one leaf, which is
/// what the compiled file's 4x4x4 masks hold. What is NOT here is the step that
/// throws away the regions the map does not enclose, because the compile's
/// criterion for that is not known and the obvious one is provably wrong on this
/// input. docs/VIS.md has the measurements.</para>
/// </summary>
public static class VisRegions
{
    /// <summary>One leaf of the octree, at whatever size it stopped at.</summary>
    /// <param name="Level">0 is the smallest leaf; a larger level is a coarser cell.</param>
    /// <param name="Cell">Its cell at that level.</param>
    /// <param name="Solid">Which of its 64 voxels the geometry reaches. Only a
    /// smallest leaf can have any, because a node with geometry subdivides.</param>
    public sealed record Leaf(int Level, (int X, int Y, int Z) Cell, ulong Solid);

    /// <summary>One connected run of open voxels inside one leaf.</summary>
    /// <param name="Leaf">Index into the leaves.</param>
    /// <param name="Open">Its voxels, as the leaf's own 4x4x4 mask.</param>
    public sealed record Region(int Leaf, ulong Open);

    /// <summary>The leaves and their regions.</summary>
    public sealed record Result(IReadOnlyList<Leaf> Leaves, IReadOnlyList<Region> Regions);

    /// <summary>Enumerate the octree's leaves and cut each one's open space up.</summary>
    /// <param name="tree">The voxelized octree.</param>
    /// <param name="sideInLeaves">The root cube's side, counted in smallest leaves.</param>
    public static Result Build(VisVoxelizer.Octree tree, int sideInLeaves)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var leaves = Enumerate(tree);
        var regions = new List<Region>();
        for (var i = 0; i < leaves.Count; i++)
            foreach (var part in Components(~leaves[i].Solid))
                regions.Add(new Region(i, part));
        return new Result(leaves, regions);
    }

    /// <summary>
    /// Every leaf of the tree. A node is a branch exactly where geometry reaches
    /// it, so a leaf is either a child of a branch that geometry misses, at
    /// whatever size that is, or a smallest cell that geometry does reach.
    /// </summary>
    private static List<Leaf> Enumerate(VisVoxelizer.Octree tree)
    {
        var depth = tree.BranchesPerLevel.Count;
        var branches = new List<HashSet<(int X, int Y, int Z)>>();
        var level = new HashSet<(int X, int Y, int Z)>(tree.LeafMasks.Keys);
        for (var i = 0; i < depth; i++)
        {
            level = [.. level.Select(c => (c.X >> 1, c.Y >> 1, c.Z >> 1))];
            branches.Add(level);
        }

        var leaves = new List<Leaf>();
        var stack = new Stack<(int Level, (int X, int Y, int Z) Cell)>();
        stack.Push((depth, (0, 0, 0)));
        while (stack.Count > 0)
        {
            var (at, cell) = stack.Pop();
            if (at > 0 && branches[at - 1].Contains(cell))
            {
                for (var octant = 0; octant < 8; octant++)
                    stack.Push((at - 1, (cell.X * 2 + (octant & 1), cell.Y * 2 + ((octant >> 1) & 1),
                                         cell.Z * 2 + ((octant >> 2) & 1))));
                continue;
            }
            leaves.Add(new Leaf(at, cell, at == 0 ? tree.LeafMasks.GetValueOrDefault(cell) : 0UL));
        }
        return leaves;
    }

    /// <summary>
    /// Connected runs of the set voxels of a 4x4x4 mask, by face adjacency.
    ///
    /// <para>Face rather than corner adjacency, though on these maps it makes no
    /// difference: both give the same count on every leaf of all three measured.</para>
    /// </summary>
    public static List<ulong> Components(ulong open)
    {
        var found = new List<ulong>();
        var seen = 0UL;
        for (var start = 0; start < 64; start++)
        {
            var bit = 1UL << start;
            if ((open & bit) == 0 || (seen & bit) != 0)
                continue;

            var part = bit;
            seen |= bit;
            var stack = new Stack<int>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var at = stack.Pop();
                int x = at & 3, y = (at >> 2) & 3, z = (at >> 4) & 3;
                for (var axis = 0; axis < 3; axis++)
                    for (var step = -1; step <= 1; step += 2)
                    {
                        int nx = x + (axis == 0 ? step : 0);
                        int ny = y + (axis == 1 ? step : 0);
                        int nz = z + (axis == 2 ? step : 0);
                        if (nx is < 0 or > 3 || ny is < 0 or > 3 || nz is < 0 or > 3)
                            continue;
                        var next = nx + 4 * ny + 16 * nz;
                        var mask = 1UL << next;
                        if ((open & mask) == 0 || (seen & mask) != 0)
                            continue;
                        seen |= mask;
                        part |= mask;
                        stack.Push(next);
                    }
            }
            found.Add(part);
        }
        return found;
    }
}
