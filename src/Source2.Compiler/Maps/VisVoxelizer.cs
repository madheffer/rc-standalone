using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// Stage 2 of visibility: the octree the rest of the builder hangs off.
///
/// <para>The compile logs it as <c>Voxelize (8 units) took 0.49 seconds (81,625
/// nodes)</c>, and that node count is what this is scored on. Three parameters are
/// read off Valve's own output rather than chosen: the root is a CUBE of 4,096
/// base voxels a side, a leaf is FOUR base voxels a side because a leaf carries a
/// 4x4x4 occupancy mask, and a node subdivides when the scene's geometry reaches
/// into it.</para>
/// </summary>
public static class VisVoxelizer
{
    /// <summary>A leaf is four base voxels a side, which is what its mask covers.</summary>
    public const int VoxelsPerLeaf = 4;

    /// <summary>
    /// The root is a CUBE whose side is a power of two multiple of the base voxel,
    /// and the multiple is per map rather than fixed: ze_hold_em_p is 32,768 units
    /// over a 4,096 voxel side and cardtest is 4,096 over 512. The compiled file
    /// states it, so it is read and not assumed.
    /// </summary>
    public static int VoxelsPerRoot(Vector3 min, Vector3 max, float baseVoxelSize)
    {
        var side = max.X - min.X;
        if (side <= 0f || Math.Abs(max.Y - min.Y - side) > 0.01f || Math.Abs(max.Z - min.Z - side) > 0.01f)
            throw new InvalidOperationException($"the visibility root is not a cube: {min} .. {max}");
        return (int)MathF.Round(side / baseVoxelSize);
    }

    /// <summary>The octree a voxelize pass produces.</summary>
    /// <param name="Origin">The root cube's corner.</param>
    /// <param name="LeafSize">World units a leaf spans.</param>
    /// <param name="LeafMasks">For every leaf the geometry reaches, which of its
    /// 64 sub-cells it reaches, keyed by (level above a leaf, cell). A mask is
    /// always 4x4x4 over the node's OWN box, so a coarse leaf has one too: that
    /// is the case where the only geometry in it is CoarseOccupancyOnly, which
    /// stops the subdivision but still fills a mask.</param>
    /// <param name="BranchesPerLevel">Branch nodes at each level, leaf level first.</param>
    /// <param name="BranchCells">Every branch as (level above a leaf, cell at that
    /// level), so the tree can be compared against a shipped one node for node
    /// rather than through the leaves, which do not reach a branch whose geometry
    /// stops counting further down.</param>
    public sealed record Octree(
        Vector3 Origin, float LeafSize,
        IReadOnlyDictionary<(int Level, (int X, int Y, int Z) Cell), ulong> LeafMasks,
        IReadOnlyList<int> BranchesPerLevel,
        IReadOnlySet<(int Level, (int X, int Y, int Z) Cell)> BranchCells)
    {
        /// <summary>Smallest-leaf cells the geometry reaches.</summary>
        public IEnumerable<(int X, int Y, int Z)> Occupied
            => from key in LeafMasks.Keys where key.Level == 0 select key.Cell;

        /// <summary>Leaves of the whole tree, most of which hold no geometry.</summary>
        public int Leaves => Nodes - Branches;

        /// <summary>Branch nodes, each of which owns eight children.</summary>
        public int Branches => BranchesPerLevel.Sum();

        /// <summary>
        /// Nodes the compile would report. Every branch owns exactly eight
        /// children, so the count is one root plus eight per branch, which is why
        /// Valve's 81,625 is 1 + 8 x 10,203 to the unit.
        /// </summary>
        public int Nodes => 1 + 8 * Branches;

        /// <summary>Levels between a leaf and the root.</summary>
        public int Depth => BranchesPerLevel.Count;
    }

    /// <summary>
    /// Voxelize a ray trace scene into the octree, over the root cube the compile
    /// would use.
    ///
    /// <para>This is <c>18002e310</c>'s shape and not a marking pass: the tree is
    /// built downwards, and a child is a node at all only when some triangle
    /// reaches into its box. The difference is not cosmetic, because the set of
    /// triangles that count depends on the box: a parent wider than 256 units
    /// tests its children against every triangle, and at or below that width the
    /// <see cref="RayTraceEnvironment.CoarseOccupancyOnly"/> ones stop counting.
    /// A bottom-up marking pass cannot express that, and on a map that has such
    /// triangles it is out by tens of percent.</para>
    /// </summary>
    /// <param name="rte">The scene, whose excluded triangles are skipped.</param>
    /// <param name="origin">The root cube's corner, which the compiled file states
    /// as <c>m_vMinBounds</c>.</param>
    /// <param name="max">Its far corner, <c>m_vMaxBounds</c>, which gives the depth.</param>
    /// <param name="baseVoxelSize">The compile's <c>BaseVoxelSize</c>, 8 by default.</param>
    public static Octree Build(
        RayTraceEnvironment rte, Vector3 origin, Vector3 max, float baseVoxelSize = 8f)
    {
        ArgumentNullException.ThrowIfNull(rte);

        var leafSize = baseVoxelSize * VoxelsPerLeaf;
        var side = VoxelsPerRoot(origin, max, baseVoxelSize) / VoxelsPerLeaf;

        var triangles = new List<Vector3[]>();
        var coarseOnly = new List<bool>();
        for (var i = 0; i < rte.TriangleCount; i++)
        {
            if (!rte.Traced(i) || rte.Vertices(i) is not { } corners)
                continue;
            triangles.Add(corners);
            coarseOnly.Add((rte.Flags(i) & RayTraceEnvironment.CoarseOccupancyOnly) != 0);
        }

        var masks = new Dictionary<(int Level, (int X, int Y, int Z) Cell), ulong>();
        var branches = new int[BitOperations.Log2((uint)side) + 1];
        var cells = new HashSet<(int Level, (int X, int Y, int Z) Cell)>();
        var all = Enumerable.Range(0, triangles.Count).ToArray();
        Descend(origin, side, all, [.. all.Where(t => coarseOnly[t])],
                triangles, coarseOnly, baseVoxelSize, (0, 0, 0), masks, branches, cells);

        // The leaf level has no branches of its own, and the array is indexed from
        // it, so the root's level is last and the count reads leaf-first.
        return new Octree(origin, leafSize, masks, branches[1..], cells);
    }

    /// <summary>
    /// One node, given the triangles that reach its box. A branch filters that set
    /// down for each child; a leaf turns it into the 4x4x4 mask.
    ///
    /// <para><c>cells</c> is the node's side counted in LEAVES, so 1 is a leaf.
    /// <c>wide</c> carries the CoarseOccupancyOnly triangles separately, because
    /// the narrow mask drops them from <c>reaching</c> the moment a box is 256
    /// units or less and the compile can still find them: it queries the kd tree
    /// rather than a list, at every depth.</para>
    /// </summary>
    private static void Descend(
        Vector3 origin, int cells, int[] reaching, int[] wide,
        List<Vector3[]> triangles, List<bool> coarseOnly, float voxel,
        (int X, int Y, int Z) cell,
        Dictionary<(int Level, (int X, int Y, int Z) Cell), ulong> masks, int[] branches,
        HashSet<(int Level, (int X, int Y, int Z) Cell)> branchCells)
    {
        var leafSize = voxel * VoxelsPerLeaf;
        if (cells == 1)
        {
            Mask(origin, voxel, reaching, triangles, coarseOnly, fine: true, 0, cell, masks);
            return;
        }

        // The mask is chosen from THIS box's width and applied to the children, so
        // a 512 unit parent still counts what its 256 unit children may not.
        // The split is not conditional on a child being occupied. A node is only
        // ever reached because its PARENT found geometry in it, and it then
        // subdivides on size alone, so it is a branch even when the narrower mask
        // leaves every one of its eight children empty. That case is real: it is
        // the two coarse levels a CoarseOccupancyOnly triangle carries on its own.
        branches[BitOperations.Log2((uint)cells)]++;
        branchCells.Add((BitOperations.Log2((uint)cells), cell));

        var fine = cells * leafSize <= RayTraceEnvironment.FineBoxSize;
        var half = cells / 2;
        var childSize = half * leafSize * 0.5f;
        for (var octant = 0; octant < 8; octant++)
        {
            var corner = origin + new Vector3(octant & 1, (octant >> 1) & 1, (octant >> 2) & 1)
                                * (half * leafSize);
            var centre = corner + new Vector3(childSize, childSize, childSize);
            var kept = new List<int>();
            foreach (var t in reaching)
            {
                if (fine && coarseOnly[t])
                    continue;
                if (Overlaps(triangles[t], centre, childSize))
                    kept.Add(t);
            }
            var here = wide.Where(t => Overlaps(triangles[t], centre, childSize)).ToArray();
            var childCell = (cell.X * 2 + (octant & 1), cell.Y * 2 + ((octant >> 1) & 1),
                             cell.Z * 2 + ((octant >> 2) & 1));
            if (kept.Count == 0)
            {
                // Empty under the narrow mask, but the compile retries the same
                // box with the wide one: a child holding nothing but
                // CoarseOccupancyOnly geometry stops here as a LEAF and still
                // gets a mask, built from that geometry.
                if (fine && here.Length > 0)
                    Mask(corner, half * leafSize * 0.25f, here, triangles, coarseOnly,
                         fine: false, BitOperations.Log2((uint)half), childCell, masks);
                continue;
            }
            Descend(corner, half, [.. kept], here, triangles, coarseOnly, voxel, childCell,
                    masks, branches, branchCells);
        }
    }

    /// <summary>
    /// Whether a triangle reaches into a cube, by the separating axis test: the
    /// cube's three faces, the triangle's plane, and the nine edge cross products.
    /// </summary>
    private static bool Overlaps(Vector3[] triangle, Vector3 centre, float half)
    {
        Span<Vector3> v = [triangle[0] - centre, triangle[1] - centre, triangle[2] - centre];
        for (var axis = 0; axis < 3; axis++)
        {
            var lo = MathF.Min(Component(v[0], axis), MathF.Min(Component(v[1], axis), Component(v[2], axis)));
            var hi = MathF.Max(Component(v[0], axis), MathF.Max(Component(v[1], axis), Component(v[2], axis)));
            if (lo > half || hi < -half)
                return false;
        }

        Span<Vector3> edge = [v[1] - v[0], v[2] - v[1], v[0] - v[2]];
        var normal = Vector3.Cross(edge[0], edge[1]);
        var reach = half * (MathF.Abs(normal.X) + MathF.Abs(normal.Y) + MathF.Abs(normal.Z));
        if (MathF.Abs(Vector3.Dot(normal, v[0])) > reach)
            return false;

        for (var i = 0; i < 3; i++)
            for (var axis = 0; axis < 3; axis++)
            {
                var a = Axis(edge[i], axis);
                var p0 = Vector3.Dot(a, v[0]);
                var p1 = Vector3.Dot(a, v[1]);
                var p2 = Vector3.Dot(a, v[2]);
                reach = half * (MathF.Abs(a.X) + MathF.Abs(a.Y) + MathF.Abs(a.Z));
                if (MathF.Min(p0, MathF.Min(p1, p2)) > reach || MathF.Max(p0, MathF.Max(p1, p2)) < -reach)
                    return false;
            }
        return true;
    }

    /// <summary>
    /// A node's 4x4x4 occupancy, which is <c>18002d670</c>: one kd query per
    /// sub-cell, under the mask the node's own size picks.
    /// </summary>
    private static void Mask(
        Vector3 origin, float sub, IReadOnlyList<int> reaching,
        List<Vector3[]> triangles, List<bool> coarseOnly, bool fine,
        int level, (int X, int Y, int Z) cell,
        Dictionary<(int Level, (int X, int Y, int Z) Cell), ulong> masks)
    {
        var mask = 0UL;
        for (var i = 0; i < 64; i++)
        {
            var centre = origin + new Vector3(
                (i & 3) + 0.5f, ((i >> 2) & 3) + 0.5f, ((i >> 4) & 3) + 0.5f) * sub;
            foreach (var t in reaching)
                if ((!fine || !coarseOnly[t]) && Overlaps(triangles[t], centre, sub * 0.5f))
                {
                    mask |= 1UL << i;
                    break;
                }
        }
        if (mask != 0)
            masks[(level, cell)] = mask;
    }

    private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>An edge crossed with a cube axis, without building the cross product.</summary>
    private static Vector3 Axis(Vector3 edge, int axis) => axis switch
    {
        0 => new Vector3(0f, -edge.Z, edge.Y),
        1 => new Vector3(edge.Z, 0f, -edge.X),
        _ => new Vector3(-edge.Y, edge.X, 0f),
    };
}
