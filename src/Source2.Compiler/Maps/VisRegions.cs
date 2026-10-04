using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// Stage 3 of visibility, as far as it is established: the octree's leaves, and
/// the open space inside each of them cut into regions.
///
/// <para>A region is one greedy BOX of open voxels within one leaf, which is
/// what the compiled file's 4x4x4 masks hold. What is NOT here is the step that
/// throws away the regions the map does not enclose, because the compile's
/// criterion for that is not known and the obvious one is provably wrong on this
/// input. The old docs/VIS.md (git c2dc317) has the measurements.</para>
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

        var (leaves, pushed, _) = Enumerate(tree);
        // Regions in the order LeafEntries (18002ebf0) pushes them during the
        // build's depth-first walk, which is not slot order: a branch is
        // recursed into the moment the walk reaches its octant, before the
        // leaves after it (captured on atixref, capture_outside.py). Outside
        // detection's second pass runs in this order.
        var regions = new List<Region>();
        foreach (var i in pushed)
            foreach (var part in Split(leaves[i].Solid))
                regions.Add(new Region(i, part));
        return new Result(leaves, regions);
    }

    /// <summary>
    /// Each leaf's node slot, which is how the compile's own records number a
    /// leaf (branches take slots too), in the order <see cref="Build"/> lists
    /// the leaves.
    /// </summary>
    public static int[] Slots(VisVoxelizer.Octree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return Enumerate(tree).Slots;
    }

    /// <summary>
    /// Every leaf of the tree. A node is a branch exactly where geometry reaches
    /// it, so a leaf is either a child of a branch that geometry misses, at
    /// whatever size that is, or a smallest cell that geometry does reach.
    /// </summary>
    private static (List<Leaf> Leaves, List<int> Pushed, int[] Slots) Enumerate(VisVoxelizer.Octree tree)
    {
        var depth = tree.BranchesPerLevel.Count;
        var branches = tree.BranchCells;

        // In the compile's own order, which is the node pool's: 18002e310 builds
        // downwards, giving a branch's eight children eight consecutive slots
        // the moment it splits and only then recursing, octant 0 first. Every
        // later stage walks leaves in that order, and the merge is sensitive to
        // it, so it is reproduced rather than any traversal that visits the
        // same leaves. Checked against the slot numbers the compile's own
        // clusters carry: equal wherever our branches are its branches.
        var leaves = new List<(int Slot, Leaf Leaf)>();
        var walk = new List<int>();
        var next = 1;
        void Split(int at, (int X, int Y, int Z) cell)
        {
            var first = next;
            next += 8;
            var branchAt = new bool[8];
            for (var octant = 0; octant < 8; octant++)
            {
                var child = (cell.X * 2 + (octant & 1), cell.Y * 2 + ((octant >> 1) & 1),
                             cell.Z * 2 + ((octant >> 2) & 1));
                branchAt[octant] = at - 1 > 0 && branches.Contains((at - 1, child));
                if (!branchAt[octant])
                    leaves.Add((first + octant, new Leaf(at - 1, child, tree.LeafMasks.GetValueOrDefault((at - 1, child)))));
            }
            // The walk meets the children in octant order: a leaf is pushed
            // where it stands, a branch is split (taking the next eight slots)
            // and walked before the octants after it.
            for (var octant = 0; octant < 8; octant++)
            {
                if (!branchAt[octant])
                {
                    walk.Add(first + octant);
                    continue;
                }
                var child = (cell.X * 2 + (octant & 1), cell.Y * 2 + ((octant >> 1) & 1),
                             cell.Z * 2 + ((octant >> 2) & 1));
                Split(at - 1, child);
            }
        }
        if (depth > 0 && branches.Contains((depth, (0, 0, 0))))
            Split(depth, (0, 0, 0));
        else
        {
            leaves.Add((0, new Leaf(depth, (0, 0, 0), tree.LeafMasks.GetValueOrDefault((depth, (0, 0, 0))))));
            walk.Add(0);
        }
        var bySlot = leaves.OrderBy(l => l.Slot).ToList();
        var index = new Dictionary<int, int>();
        for (var i = 0; i < bySlot.Count; i++)
            index[bySlot[i].Slot] = i;
        return ([.. bySlot.Select(l => l.Leaf)], [.. walk.Select(slot => index[slot])], [.. bySlot.Select(l => l.Slot)]);
    }

    /// <summary>
    /// Every leaf's regions collapsed to at most three, which is
    /// <c>180032670</c>. It runs between outside detection and the cluster
    /// stage, so the count the compile prints as "Generated clusters for N
    /// regions" is taken AFTER it.
    ///
    /// <para>That is worth stating plainly because it changes what the number
    /// means: a leaf contributes ONE enclosed region however many boxes it was
    /// cut into, so N is the number of leaves holding any enclosed space at all.
    /// The three the compile keeps are the union of the enclosed masks, the
    /// union of the outside ones, and the solid voxels, tagged 0, 1 and 2 in the
    /// region's own low bits; only the first is counted or clustered.</para>
    /// </summary>
    public static Result Compact(Result regions, IReadOnlyList<VisOutside.Status> status)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(status);

        var enclosed = new ulong[regions.Leaves.Count];
        for (var i = 0; i < regions.Regions.Count; i++)
            if (status[i] == VisOutside.Status.Inside)
                enclosed[regions.Regions[i].Leaf] |= regions.Regions[i].Open;

        var kept = new List<Region>();
        for (var leaf = 0; leaf < enclosed.Length; leaf++)
            if (enclosed[leaf] != 0)
                kept.Add(new Region(leaf, enclosed[leaf]));
        return new Result(regions.Leaves, kept);
    }

    /// <summary>
    /// The same collapse as <see cref="Compact"/>, but as the entry array
    /// <c>180032670</c> actually leaves behind rather than only the enclosed part
    /// of it.
    ///
    /// <para>Per leaf it emits up to THREE records, each only when its mask came
    /// out non-empty and each carrying the leaf in the same packed word: the
    /// enclosed union at kind <see cref="VisVisibility.Open"/>, the OUTSIDE union
    /// at kind <see cref="VisVisibility.Blocking"/>, and the solid union at kind
    /// <see cref="VisVisibility.Skipped"/>. Only the first is counted or
    /// clustered, which is why <see cref="Compact"/> returns just those, but the
    /// other two survive into assignment and are most of what it carries: on
    /// ze_hold_em_p they are 92,640 of the 103,358 records the compile ends
    /// with.</para>
    /// </summary>
    /// <param name="regions">The leaves and their regions.</param>
    /// <param name="status">Outside detection's verdict per region.</param>
    public static VisVisibility.Entry[] Collapse(
        Result regions, IReadOnlyList<VisOutside.Status> status)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(status);

        var enclosed = new ulong[regions.Leaves.Count];
        var outside = new ulong[regions.Leaves.Count];
        for (var i = 0; i < regions.Regions.Count; i++)
        {
            var region = regions.Regions[i];
            if (status[i] == VisOutside.Status.Inside)
                enclosed[region.Leaf] |= region.Open;
            else
                outside[region.Leaf] |= region.Open;
        }

        var kept = new List<VisVisibility.Entry>();
        for (var leaf = 0; leaf < regions.Leaves.Count; leaf++)
        {
            if (enclosed[leaf] != 0)
                kept.Add(new VisVisibility.Entry(0, (leaf << 2) | VisVisibility.Open, enclosed[leaf]));
            if (outside[leaf] != 0)
                kept.Add(new VisVisibility.Entry(0, (leaf << 2) | VisVisibility.Blocking, outside[leaf]));
            if (regions.Leaves[leaf].Solid != 0)
                kept.Add(new VisVisibility.Entry(
                    0, (leaf << 2) | VisVisibility.Skipped, regions.Leaves[leaf].Solid));
        }
        return [.. kept];
    }

    /// <summary>
    /// A leaf's open space cut into regions, which is <c>18010c3f0</c>.
    ///
    /// <para>It is NOT connected components, and that is the single thing about
    /// this stage most worth getting right. It is a greedy BOX decomposition:
    /// take the lowest open voxel nothing has claimed, grow the box as far as it
    /// will go in x, then in y, then in z, each step requiring the whole new slab
    /// to be open, emit it, and start again. An L-shaped run of open space is one
    /// component and several boxes.</para>
    ///
    /// <para>The growth doubles the accumulated mask rather than tracking a
    /// corner, so <c>m * 2</c> is one step in x, <c>m &lt;&lt; 4</c> one in y and
    /// <c>m &lt;&lt; 16</c> one in z, and each is bounded by the starting cell's
    /// own coordinate so the mask cannot wrap into the next row.</para>
    /// </summary>
    public static List<ulong> Split(ulong solid)
    {
        var found = new List<ulong>();
        var taken = solid;
        while (true)
        {
            var at = 0;
            while (at < 64 && (taken & (1UL << at)) != 0)
                at++;
            if (at == 64)
                return found;

            var box = 1UL << at;
            for (var x = at & 3; x < 3; x++)
            {
                var grown = (box * 2) | box;
                if ((solid & grown) != 0)
                    break;
                box = grown;
            }
            for (var y = (at >> 2) & 3; y < 3; y++)
            {
                var grown = (box << 4) | box;
                if ((solid & grown) != 0)
                    break;
                box = grown;
            }
            for (var z = (at >> 4) & 3; z < 3; z++)
            {
                var grown = (box << 16) | box;
                if ((solid & grown) != 0)
                    break;
                box = grown;
            }

            box &= ~taken;
            if (box == 0)
                return found;
            found.Add(box);
            taken |= box;
        }
    }
}
