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

        // Every occupancy question goes through the tracer's own kd tree, the
        // one the loader rebuilds, because the walk decides which triangles a
        // box ever meets: see TracerKd.
        var kd = new TracerKd(rte);
        var masks = new Dictionary<(int Level, (int X, int Y, int Z) Cell), ulong>();
        var branches = new int[BitOperations.Log2((uint)side) + 1];
        var cells = new HashSet<(int Level, (int X, int Y, int Z) Cell)>();
        Descend(kd, origin, max, side, (0, 0, 0), masks, branches, cells);

        // The leaf level has no branches of its own, and the array is indexed from
        // it, so the root's level is last and the count reads leaf-first.
        return new Octree(origin, leafSize, masks, branches[1..], cells);
    }

    /// <summary>
    /// One node of <c>Voxelize</c>. A node wider than a leaf splits on size
    /// alone and asks the tree about each child under the mask ITS OWN width
    /// picks: <c>0x811</c> above 256 units, <c>0x1811</c> at or below. An
    /// occupied child is descended; an empty one under the narrow mask is asked
    /// again with the wide one and, if that finds something, becomes a leaf
    /// with a wide mask (<c>LeafEntries(child, 0)</c>). That is how a
    /// CoarseOccupancyOnly triangle carries coarse levels on its own. A leaf is
    /// <c>LeafEntries(node, size &lt;= 256)</c>.
    ///
    /// <para><c>cells</c> is the node's side counted in LEAVES, so 1 is a leaf.
    /// Boxes are halved at their midpoints, as the compile halves them.</para>
    /// </summary>
    private static void Descend(
        TracerKd kd, Vector3 mins, Vector3 maxs, int cells, (int X, int Y, int Z) cell,
        Dictionary<(int Level, (int X, int Y, int Z) Cell), ulong> masks, int[] branches,
        HashSet<(int Level, (int X, int Y, int Z) Cell)> branchCells)
    {
        var size = maxs.X - mins.X;
        if (cells == 1)
        {
            Mask(kd, mins, maxs, fine: size <= RayTraceEnvironment.FineBoxSize, 0, cell, masks);
            return;
        }

        branches[BitOperations.Log2((uint)cells)]++;
        branchCells.Add((BitOperations.Log2((uint)cells), cell));

        var narrow = size <= RayTraceEnvironment.FineBoxSize;
        var mask = narrow ? Narrow : Wide;
        var mid = new Vector3((maxs.X + mins.X) * 0.5f, (maxs.Y + mins.Y) * 0.5f, (maxs.Z + mins.Z) * 0.5f);
        var half = cells / 2;
        for (var octant = 0; octant < 8; octant++)
        {
            var lo = new Vector3((octant & 1) == 0 ? mins.X : mid.X, (octant & 2) == 0 ? mins.Y : mid.Y,
                                 (octant & 4) == 0 ? mins.Z : mid.Z);
            var hi = new Vector3((octant & 1) == 0 ? mid.X : maxs.X, (octant & 2) == 0 ? mid.Y : maxs.Y,
                                 (octant & 4) == 0 ? mid.Z : maxs.Z);
            var childCell = (cell.X * 2 + (octant & 1), cell.Y * 2 + ((octant >> 1) & 1),
                             cell.Z * 2 + ((octant >> 2) & 1));
            if (kd.Occupied(lo, hi, mask))
                Descend(kd, lo, hi, half, childCell, masks, branches, branchCells);
            else if (narrow && kd.Occupied(lo, hi, Wide))
                Mask(kd, lo, hi, fine: false, BitOperations.Log2((uint)half), childCell, masks);
        }
    }

    /// <summary>The query masks: reject excluded triangles, and below 256 units the coarse-only ones too.</summary>
    private const ushort Wide = 0x0811, Narrow = 0x1811;

    private static bool Overlaps(Vector3[] triangle, Vector3 centre, float half)
        => Overlaps(triangle[0], triangle[1], triangle[2], centre, new Vector3(half));

    /// <summary>
    /// Whether a triangle reaches into a box, which is <c>FUN_18010b310</c> with
    /// its tolerance at 0, transcribed rather than rederived. It is the familiar
    /// separating axis test with four things of its own, and every one of them
    /// decides boundary cases: the edges are NORMALISED before the nine cross
    /// axes, only the two projections that can differ are taken on each, a box
    /// touching the triangle counts (rejection is strict), and the last test is
    /// the plane of the normalised edge2 x edge0.
    /// </summary>
    internal static bool Overlaps(Vector3 pa, Vector3 pb, Vector3 pc, Vector3 centre, Vector3 h)
    {
        float a0 = pa.X - centre.X, b0 = pb.X - centre.X, c0 = pc.X - centre.X;
        if (!(Min3(a0, b0, c0) <= h.X) || !(-h.X <= Max3(a0, b0, c0)))
            return false;
        float a1 = pa.Y - centre.Y, b1 = pb.Y - centre.Y, c1 = pc.Y - centre.Y;
        if (!(Min3(a1, b1, c1) <= h.Y) || !(-h.Y <= Max3(a1, b1, c1)))
            return false;
        float a2 = pa.Z - centre.Z, b2 = pb.Z - centre.Z, c2 = pc.Z - centre.Z;
        if (!(Min3(a2, b2, c2) <= h.Z) || !(-h.Z <= Max3(a2, b2, c2)))
            return false;

        var (ex, ey, ez) = Unit(b0 - a0, b1 - a1, b2 - a2);
        if (Outside(a1 * ez - a2 * ey, c1 * ez - c2 * ey, (h.Y * MathF.Abs(ez)) + (MathF.Abs(ey) * h.Z))
            || Outside(a2 * ex - a0 * ez, c2 * ex - c0 * ez, (MathF.Abs(ex) * h.Z) + (h.X * MathF.Abs(ez)))
            || Outside(c0 * ey - c1 * ex, b0 * ey - b1 * ex, (h.Y * MathF.Abs(ex)) + (h.X * MathF.Abs(ey))))
            return false;
        var (e0x, e0y, e0z) = (ex, ey, ez);

        (ex, ey, ez) = Unit(c0 - b0, c1 - b1, c2 - b2);
        if (Outside(a1 * ez - a2 * ey, b1 * ez - b2 * ey, (h.Y * MathF.Abs(ez)) + (MathF.Abs(ey) * h.Z))
            || Outside(a2 * ex - a0 * ez, b2 * ex - b0 * ez, (h.X * MathF.Abs(ez)) + (MathF.Abs(ex) * h.Z))
            || Outside(a0 * ey - a1 * ex, c0 * ey - c1 * ex, (h.Y * MathF.Abs(ex)) + (h.X * MathF.Abs(ey))))
            return false;

        (ex, ey, ez) = Unit(a0 - c0, a1 - c1, a2 - c2);
        if (Outside(a1 * ez - a2 * ey, b1 * ez - b2 * ey, (h.Y * MathF.Abs(ez)) + (MathF.Abs(ey) * h.Z))
            || Outside(a2 * ex - a0 * ez, b2 * ex - b0 * ez, (h.X * MathF.Abs(ez)) + (MathF.Abs(ex) * h.Z))
            || Outside(c0 * ey - c1 * ex, b0 * ey - b1 * ex, (h.Y * MathF.Abs(ex)) + (h.X * MathF.Abs(ey))))
            return false;

        var (nx, ny, nz) = Unit((ez * e0y) - (ey * e0z), (ex * e0z) - (ez * e0x), (ey * e0x) - (ex * e0y));
        var d = (nz * a2) + (ny * a1) + (nx * a0);
        var r = MathF.Abs(ny * h.Y) + MathF.Abs(nx * h.X) + MathF.Abs(nz * h.Z);
        return d <= r && -r <= d;
    }

    // The binary's min and max of three, <= swaps in its order.
    private static float Min3(float a, float b, float c)
    {
        var m = a <= b ? a : b;
        return m <= c ? m : c;
    }

    private static float Max3(float a, float b, float c)
    {
        var m = b <= a ? a : b;
        return c <= m ? m : c;
    }

    // One cross axis. `held` is the projection the binary tests as the lesser
    // when it is no greater than `other`; the rejection is strict either way.
    private static bool Outside(float held, float other, float r)
        => other <= held ? r < other || held < -r : r < held || other < -r;

    // The inline normalise: length summed z first, a reciprocal multiply, zero
    // below 1e-17, and the double precision path for anything extreme.
    private static (float, float, float) Unit(float x, float y, float z)
    {
        var length = MathF.Sqrt((z * z) + (y * y) + (x * x));
        if (length < 1e-17f || length > 1e17f)
        {
            if (length == 0f)
                return (0f, 0f, 0f);
            var l = Math.Sqrt(((double)x * x) + ((double)y * y) + ((double)z * z));
            return ((float)(x / l), (float)(y / l), (float)(z / l));
        }
        var inverse = 1f / length;
        return (x * inverse, y * inverse, z * inverse);
    }

    /// <summary>
    /// A node's 4x4x4 occupancy, which is <c>LeafEntries</c>: one query per
    /// sub-cell, each box built by <c>SubBox</c>, under the mask the node's own
    /// size picks.
    /// </summary>
    private static void Mask(
        TracerKd kd, Vector3 mins, Vector3 maxs, bool fine, int level, (int X, int Y, int Z) cell,
        Dictionary<(int Level, (int X, int Y, int Z) Cell), ulong> masks)
    {
        var mask = 0UL;
        var sub = (maxs.X - mins.X) * 0.25f;
        for (var i = 0; i < 64; i++)
        {
            float ox = (i & 3) * sub, oy = ((i >> 2) & 3) * sub, oz = ((i >> 4) & 3) * sub;
            var lo = new Vector3(ox + mins.X, oy + mins.Y, oz + mins.Z);
            var hi = new Vector3(mins.X + (ox + sub), mins.Y + (oy + sub), mins.Z + (oz + sub));
            if (kd.Occupied(lo, hi, fine ? Narrow : Wide))
                mask |= 1UL << i;
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
