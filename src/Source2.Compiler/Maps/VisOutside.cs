using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The pass that decides which regions the map encloses, ported from
/// visbuilder.dll's <c>1800321f0</c>.
///
/// <para>It is NOT a flood fill, and the difference matters: a ray trace scene is
/// under no obligation to be closed, and ze_hold_em_p's is not, so a fill from the
/// world box marks everything outside. Valve instead classifies each region on its
/// own and lets the answer spread along RAYS: a region is outside outright when
/// its box centre leaves the world, and otherwise it casts rays and votes on what
/// they reach. A region deep in a corridor stays inside because its rays end on
/// nearby geometry, ceiling or no ceiling.</para>
///
/// <para>What is ported exactly: the seed, the march, and the propagation. What is
/// NOT is the set of rays. The compiler gathers those per region from a structure
/// whose record type is still unidentified, so the direction set and reach are
/// parameters here, and docs/VIS.md records how the region count moves with
/// them.</para>
/// </summary>
public static class VisOutside
{
    /// <summary>What the compile stores per region in its status array.</summary>
    public enum Status : byte
    {
        /// <summary>Not yet decided.</summary>
        Unknown = 0,

        /// <summary>The map encloses it. These are the regions the compile counts.</summary>
        Inside = 1,

        /// <summary>The void reaches it. Sets the region's flag bit 0.</summary>
        Outside = 2,
    }

    /// <param name="Regions">Status per region, in the order they were built.</param>
    /// <param name="Passes">How many sweeps it took to settle.</param>
    public sealed record Result(IReadOnlyList<Status> Regions, int Passes)
    {
        /// <summary>The count the compile prints.</summary>
        public int Inside => Regions.Count(s => s == Status.Inside);

        /// <summary>Regions the void reaches.</summary>
        public int Outside => Regions.Count(s => s == Status.Outside);
    }

    /// <summary>The 26 directions off a cube: faces, edges and corners.</summary>
    public static readonly Vector3[] AllDirections =
    [
        .. from x in new[] { -1, 0, 1 }
           from y in new[] { -1, 0, 1 }
           from z in new[] { -1, 0, 1 }
           where x != 0 || y != 0 || z != 0
           select Vector3.Normalize(new Vector3(x, y, z)),
    ];

    /// <summary>The six axial directions, which is what CAxialRayGenerator is named for.</summary>
    public static readonly Vector3[] AxialDirections =
    [
        new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1),
    ];

    /// <summary>
    /// Classify every region.
    /// </summary>
    /// <param name="tree">The voxelized octree.</param>
    /// <param name="regions">Its leaves and their regions.</param>
    /// <param name="worldMin">The geometry's own box, which seeds the pass.</param>
    /// <param name="worldMax">The geometry's own box.</param>
    /// <param name="baseVoxelSize">The compile's BaseVoxelSize.</param>
    /// <param name="directions">Rays cast from each region's centre.</param>
    /// <param name="reach">How far a ray travels before giving up, in world units.</param>
    public static Result Detect(
        VisVoxelizer.Octree tree, VisRegions.Result regions,
        Vector3 worldMin, Vector3 worldMax, float baseVoxelSize,
        IReadOnlyList<Vector3>? directions = null, float reach = 512f)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(regions);
        directions ??= AxialDirections;

        var space = new Space(tree, regions, baseVoxelSize);
        var status = new Status[regions.Regions.Count];

        // The seed. The compile runs a thread pool job named InitialRegionStatus
        // over every region, and that job samples rays over the region's box and
        // decides on how many of them find geometry: enclosed enough is inside,
        // open enough is outside, and anything between is left for the rays below.
        // The compile's own thresholds are a tree over four counters whose meaning
        // is not yet decoded, so the ratios here are ours.
        for (var i = 0; i < status.Length; i++)
        {
            var centre = space.Centre(i);
            if (centre.X < worldMin.X || centre.X > worldMax.X
                || centre.Y < worldMin.Y || centre.Y > worldMax.Y
                || centre.Z < worldMin.Z || centre.Z > worldMax.Z)
            {
                status[i] = Status.Outside;
                continue;
            }

            var enclosed = 0;
            foreach (var direction in AllDirections)
                if (space.Blocked(centre, direction))
                    enclosed++;
            if (enclosed >= AllDirections.Length * 5 / 6)
                status[i] = Status.Inside;
            else if (enclosed <= AllDirections.Length / 3)
                status[i] = Status.Outside;
        }

        // Then spread it, and note the polarity: a region is INSIDE only when its
        // rays reach regions already known inside, and outside when they reach one
        // already outside. Valve's ray march returns outside only on meeting a
        // region whose flag bit is set, never on leaving the world, and its vote
        // makes a region inside only on enough inside answers. Assuming the
        // opposite marks every region on an unsealed map outside.
        var passes = 0;
        for (var settled = false; !settled && passes < 16; passes++)
        {
            settled = true;
            for (var i = 0; i < status.Length; i++)
            {
                if (status[i] != Status.Unknown)
                    continue;
                var from = space.Centre(i);
                int inside = 0, outside = 0;
                foreach (var direction in directions)
                {
                    var reached = space.March(from, direction, reach, status);
                    if (reached == Status.Inside) inside++;
                    else if (reached == Status.Outside) outside++;
                }
                var verdict = inside > outside ? Status.Inside
                            : outside > 0 ? Status.Outside
                            : Status.Unknown;
                if (verdict != Status.Unknown)
                {
                    status[i] = verdict;
                    settled = false;
                }
            }
        }

        // The compile's vote defaults to outside, so anything still unresolved is
        // outside and not inside.
        for (var i = 0; i < status.Length; i++)
            if (status[i] == Status.Unknown)
                status[i] = Status.Outside;
        return new Result(status, passes);
    }

    /// <summary>
    /// The voxel grid a ray marches, and the region each voxel belongs to.
    /// </summary>
    private sealed class Space
    {
        private readonly VisVoxelizer.Octree _tree;
        private readonly VisRegions.Result _regions;
        private readonly Dictionary<(int Level, int X, int Y, int Z), int> _leaves = [];
        private readonly List<int>[] _byLeaf;
        private readonly float _voxel;
        private readonly int _depth;
        private readonly int _side;

        public Space(VisVoxelizer.Octree tree, VisRegions.Result regions, float baseVoxelSize)
        {
            _tree = tree;
            _regions = regions;
            _voxel = baseVoxelSize;
            _depth = tree.BranchesPerLevel.Count;
            _side = (1 << _depth) * VisVoxelizer.VoxelsPerLeaf;

            for (var i = 0; i < regions.Leaves.Count; i++)
            {
                var leaf = regions.Leaves[i];
                _leaves[(leaf.Level, leaf.Cell.X, leaf.Cell.Y, leaf.Cell.Z)] = i;
            }
            _byLeaf = new List<int>[regions.Leaves.Count];
            for (var i = 0; i < regions.Regions.Count; i++)
                (_byLeaf[regions.Regions[i].Leaf] ??= []).Add(i);
        }

        /// <summary>
        /// A region's centre, from its leaf's box and its mask. The compile's own
        /// 18010be50 does this: the sub cell is a quarter of the leaf and bit i is
        /// the cell at x = i &amp; 3, y = (i >> 2) &amp; 3, z = (i >> 4) &amp; 3.
        /// </summary>
        public Vector3 Centre(int region)
        {
            var leaf = _regions.Leaves[_regions.Regions[region].Leaf];
            var size = _tree.LeafSize * (1 << leaf.Level);
            var origin = _tree.Origin + new Vector3(leaf.Cell.X, leaf.Cell.Y, leaf.Cell.Z) * size;
            var sub = size * 0.25f;

            var open = _regions.Regions[region].Open;
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            for (var bit = 0; bit < 64; bit++)
            {
                if ((open & (1UL << bit)) == 0)
                    continue;
                var at = origin + new Vector3(bit & 3, (bit >> 2) & 3, (bit >> 4) & 3) * sub;
                lo = Vector3.Min(lo, at);
                hi = Vector3.Max(hi, at + new Vector3(sub));
            }
            return (lo + hi) * 0.5f;
        }

        /// <summary>
        /// March one ray and report the first thing it resolves: the status of the
        /// first already-classified region it enters, or Unknown when it is stopped
        /// by geometry or runs out of reach.
        ///
        /// <para>Stepped over BASE voxels rather than by descending the octree. The
        /// octree is an index over exactly these voxels, so the answer is the same
        /// and the traversal is one loop.</para>
        /// </summary>
        public Status March(Vector3 from, Vector3 direction, float reach, Status[] status)
        {
            var at = (from - _tree.Origin) / _voxel;
            int x = (int)MathF.Floor(at.X), y = (int)MathF.Floor(at.Y), z = (int)MathF.Floor(at.Z);
            var startRegion = RegionAt(x, y, z);

            int sx = direction.X > 0 ? 1 : -1, sy = direction.Y > 0 ? 1 : -1, sz = direction.Z > 0 ? 1 : -1;
            var step = Delta(direction.X);
            var stepY = Delta(direction.Y);
            var stepZ = Delta(direction.Z);
            var nextX = Boundary(at.X, x, direction.X);
            var nextY = Boundary(at.Y, y, direction.Y);
            var nextZ = Boundary(at.Z, z, direction.Z);

            for (var travelled = 0f; travelled * _voxel < reach;)
            {
                if (nextX < nextY && nextX < nextZ) { travelled = nextX; nextX += step; x += sx; }
                else if (nextY < nextZ) { travelled = nextY; nextY += stepY; y += sy; }
                else { travelled = nextZ; nextZ += stepZ; z += sz; }

                if (x < 0 || y < 0 || z < 0 || x >= _side || y >= _side || z >= _side)
                    return Status.Unknown;          // left the world, which says nothing

                if (Solid(x, y, z))
                    return Status.Unknown;          // stopped by geometry, says nothing

                var region = RegionAt(x, y, z);
                if (region < 0 || region == startRegion)
                    continue;
                if (status[region] != Status.Unknown)
                    return status[region];
            }
            return Status.Unknown;
        }

        /// <summary>Whether a ray from here meets geometry before leaving the world.</summary>
        public bool Blocked(Vector3 from, Vector3 direction)
        {
            var at = (from - _tree.Origin) / _voxel;
            int x = (int)MathF.Floor(at.X), y = (int)MathF.Floor(at.Y), z = (int)MathF.Floor(at.Z);
            int sx = direction.X > 0 ? 1 : -1, sy = direction.Y > 0 ? 1 : -1, sz = direction.Z > 0 ? 1 : -1;
            float stepX = Delta(direction.X), stepY = Delta(direction.Y), stepZ = Delta(direction.Z);
            float nextX = Boundary(at.X, x, direction.X);
            float nextY = Boundary(at.Y, y, direction.Y);
            float nextZ = Boundary(at.Z, z, direction.Z);

            while (true)
            {
                if (nextX < nextY && nextX < nextZ) { nextX += stepX; x += sx; }
                else if (nextY < nextZ) { nextY += stepY; y += sy; }
                else { nextZ += stepZ; z += sz; }

                if (x < 0 || y < 0 || z < 0 || x >= _side || y >= _side || z >= _side)
                    return false;
                if (Solid(x, y, z))
                    return true;
            }
        }

        private static float Delta(float component)
            => component == 0f ? float.MaxValue : MathF.Abs(1f / component);

        private static float Boundary(float at, int cell, float component)
        {
            if (component == 0f)
                return float.MaxValue;
            var edge = component > 0 ? cell + 1 - at : at - cell;
            return edge / MathF.Abs(component);
        }

        private bool Solid(int x, int y, int z)
        {
            var cell = (x / VisVoxelizer.VoxelsPerLeaf, y / VisVoxelizer.VoxelsPerLeaf, z / VisVoxelizer.VoxelsPerLeaf);
            return _tree.LeafMasks.TryGetValue(cell, out var solid) && (solid & Bit(x, y, z)) != 0;
        }

        private int RegionAt(int x, int y, int z)
        {
            var leaf = Leaf(x, y, z);
            if (leaf < 0)
                return -1;
            var parts = _byLeaf[leaf];
            if (parts is null)
                return -1;
            if (_regions.Leaves[leaf].Level > 0)
                return parts[0];                    // a larger leaf is wholly open
            var bit = Bit(x, y, z);
            foreach (var region in parts)
                if ((_regions.Regions[region].Open & bit) != 0)
                    return region;
            return -1;
        }

        private static ulong Bit(int x, int y, int z)
            => 1UL << ((x % VisVoxelizer.VoxelsPerLeaf)
                     + 4 * (y % VisVoxelizer.VoxelsPerLeaf)
                     + 16 * (z % VisVoxelizer.VoxelsPerLeaf));

        /// <summary>The leaf covering a base voxel, found by widening until one exists.</summary>
        private int Leaf(int x, int y, int z)
        {
            for (var level = 0; level <= _depth; level++)
            {
                var shift = VisVoxelizer.VoxelsPerLeaf switch { 4 => 2, _ => 2 };
                var key = (level, x >> (shift + level), y >> (shift + level), z >> (shift + level));
                if (_leaves.TryGetValue(key, out var found))
                    return found;
            }
            return -1;
        }
    }
}
