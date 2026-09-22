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
    /// 64 base voxels it reaches, as the leaf's own 4x4x4 mask.</param>
    /// <param name="BranchesPerLevel">Branch nodes at each level, leaf level first.</param>
    public sealed record Octree(
        Vector3 Origin, float LeafSize,
        IReadOnlyDictionary<(int X, int Y, int Z), ulong> LeafMasks,
        IReadOnlyList<int> BranchesPerLevel)
    {
        /// <summary>Leaf cells the geometry reaches.</summary>
        public IEnumerable<(int X, int Y, int Z)> Occupied => LeafMasks.Keys;

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
        var masks = new Dictionary<(int X, int Y, int Z), ulong>();

        // Marked at BASE voxel resolution, not leaf resolution. The leaf follows
        // from it, and the mask is what the region stage needs: a leaf holds one
        // region per connected run of open voxels.
        for (var i = 0; i < rte.TriangleCount; i++)
        {
            if (rte.Flags(i) == RayTraceEnvironment.ExcludedFromTrace)
                continue;
            if (rte.Vertices(i) is not { } triangle)
                continue;
            Mark(triangle, origin, baseVoxelSize, side * VoxelsPerLeaf, masks);
        }

        var levels = new List<int>();
        var level = new HashSet<(int X, int Y, int Z)>(masks.Keys);
        for (var depth = side; depth > 1; depth /= 2)
        {
            var parents = new HashSet<(int X, int Y, int Z)>();
            foreach (var (x, y, z) in level)
                parents.Add((x >> 1, y >> 1, z >> 1));
            levels.Add(parents.Count);
            level = parents;
        }
        return new Octree(origin, leafSize, masks, levels);
    }

    private static void Mark(
        Vector3[] triangle, Vector3 origin, float voxel, int side,
        Dictionary<(int X, int Y, int Z), ulong> masks)
    {
        var lo = Vector3.Min(Vector3.Min(triangle[0], triangle[1]), triangle[2]);
        var hi = Vector3.Max(Vector3.Max(triangle[0], triangle[1]), triangle[2]);
        var from = Cell(lo, origin, voxel, side);
        var to = Cell(hi, origin, voxel, side);

        for (var x = from.X; x <= to.X; x++)
            for (var y = from.Y; y <= to.Y; y++)
                for (var z = from.Z; z <= to.Z; z++)
                {
                    var centre = origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * voxel;
                    if (!Overlaps(triangle, centre, voxel * 0.5f))
                        continue;
                    var leaf = (x / VoxelsPerLeaf, y / VoxelsPerLeaf, z / VoxelsPerLeaf);
                    var bit = 1UL << ((x % VoxelsPerLeaf) + 4 * (y % VoxelsPerLeaf) + 16 * (z % VoxelsPerLeaf));
                    masks[leaf] = masks.GetValueOrDefault(leaf) | bit;
                }
    }

    private static (int X, int Y, int Z) Cell(Vector3 point, Vector3 origin, float leafSize, int side)
    {
        var local = (point - origin) / leafSize;
        return (Math.Clamp((int)MathF.Floor(local.X), 0, side - 1),
                Math.Clamp((int)MathF.Floor(local.Y), 0, side - 1),
                Math.Clamp((int)MathF.Floor(local.Z), 0, side - 1));
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

    private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>An edge crossed with a cube axis, without building the cross product.</summary>
    private static Vector3 Axis(Vector3 edge, int axis) => axis switch
    {
        0 => new Vector3(0f, -edge.Z, edge.Y),
        1 => new Vector3(edge.Z, 0f, -edge.X),
        _ => new Vector3(-edge.Y, edge.X, 0f),
    };
}
