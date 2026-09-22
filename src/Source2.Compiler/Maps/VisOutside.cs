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
    /// <param name="Seeded">What the seed alone decided, before the second pass.</param>
    public sealed record Result(IReadOnlyList<Status> Regions, int Passes,
                                IReadOnlyList<Status>? Seeded = null)
    {
        /// <summary>The count the compile prints.</summary>
        public int Inside => Regions.Count(s => s == Status.Inside);

        /// <summary>Regions the void reaches.</summary>
        public int Outside => Regions.Count(s => s == Status.Outside);
    }

    /// <summary>
    /// Classify every region, which is <c>1800321f0</c>.
    ///
    /// <para>It is exactly two passes and it does not iterate to a fixed point.
    /// First the seed runs over every region in a thread pool; then a SEQUENTIAL
    /// pass classifies whatever the seed left undecided, in region order, each
    /// one seeing the verdicts of the ones before it. A region the seed called
    /// outside has its flag bit set between the two, which is what makes a ray
    /// crossing it answer outright.</para>
    /// </summary>
    /// <param name="tree">The voxelized octree.</param>
    /// <param name="regions">Its leaves and their regions.</param>
    /// <param name="scene">The ray trace scene, which <see cref="VisSeed"/> casts into.</param>
    /// <param name="baseVoxelSize">The compile's BaseVoxelSize.</param>
    /// <param name="quality">Rays a box face is divided into, per side.</param>
    public static Result Detect(
        VisVoxelizer.Octree tree, VisRegions.Result regions,
        RayTraceEnvironment scene, float baseVoxelSize,
        int quality = VisSeed.Quality)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(scene);

        var space = new Space(tree, regions, baseVoxelSize);
        var status = new Status[regions.Regions.Count];
        var flagged = new bool[regions.Regions.Count];

        Parallel.For(0, status.Length, i =>
        {
            var (mins, maxs) = space.Box(i);
            status[i] = VisSeed.Decide(VisSeed.Gather(scene, mins, maxs, quality));
        });
        for (var i = 0; i < status.Length; i++)
            flagged[i] = status[i] == Status.Outside;
        var seeded = (Status[])status.Clone();

        // Pass two, in region order and one at a time, because a region's answer
        // is what the next one's rays are allowed to see.
        for (var i = 0; i < status.Length; i++)
        {
            if (status[i] != Status.Unknown)
                continue;
            status[i] = Classify(space, scene, status, flagged, i, quality);
        }
        for (var i = 0; i < status.Length; i++)
            if (status[i] == Status.Outside)
                flagged[i] = true;

        return new Result(status, 2, seeded);
    }

    /// <summary>How far short of the surface a ray stops (<c>DAT_18017f170</c>).</summary>
    public const float MarchBackOff = 8f;

    /// <summary>The shortest a marched segment may be (<c>DAT_18017f0e4</c>).</summary>
    public const float MarchShortest = 0.1f;

    /// <summary>Rays that may answer outside before the vote gives up.</summary>
    public const int OutsideVotesAllowed = 5;

    /// <summary>
    /// One undecided region, which is <c>18002e050</c>. Its box centre leaving
    /// the scene's own bounds settles it outright; otherwise it casts the same
    /// grid the seed does and marches every ray that landed on a surface facing
    /// it, stopping a voxel short of what it hit.
    /// </summary>
    private static Status Classify(
        Space space, RayTraceEnvironment scene, Status[] status, bool[] flagged, int region, int quality)
    {
        var (mins, maxs) = space.Box(region);
        var centre = (mins + maxs) * 0.5f;
        if (centre.X < scene.Mins.X || centre.Y < scene.Mins.Y || centre.Z < scene.Mins.Z
            || centre.X > scene.Maxs.X || centre.Y > scene.Maxs.Y || centre.Z > scene.Maxs.Z)
            return Status.Outside;

        var grid = Math.Clamp(quality, 2, 10);
        var reach = (scene.Maxs - scene.Mins).Length();
        int inside = 0, outside = 0;
        foreach (var direction in VisSeed.Directions(mins, maxs, quality))
        {
            if (scene.Trace(centre, direction, reach, VisSeed.Ignored) is not { } hit
                || Vector3.Dot(hit.Normal, centre) < hit.PlaneDistance)
                continue;

            var stop = MathF.Max(hit.Distance - MarchBackOff, MarchShortest);
            var reached = space.March(centre, centre + (direction * stop), status, flagged);
            if (reached == Status.Inside) inside++;
            else if (reached == Status.Outside) outside++;
        }

        var n = grid * grid;
        return inside > n * 2 || (inside > n && outside < OutsideVotesAllowed)
            ? Status.Inside
            : Status.Outside;
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
            var (lo, hi) = Box(region);
            return (lo + hi) * 0.5f;
        }

        /// <summary>The region's own box, which 18010be50 builds the same way.</summary>
        public (Vector3 Mins, Vector3 Maxs) Box(int region)
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
            return (lo, hi);
        }

        /// <summary>
        /// March a SEGMENT through the octree and report what it reaches, which
        /// is <c>18002deb0</c>. It is a breadth-first walk of the tree rather
        /// than a step over base voxels: a branch queues whichever of its eight
        /// children the segment crosses, and a leaf checks the segment against
        /// its regions' own voxels.
        ///
        /// <para>The polarity is the whole of it. Outside is returned the moment
        /// the segment touches a region whose FLAG is set, and wins outright.
        /// Inside is returned only at the end, and only if some region it crossed
        /// is currently marked inside. Reaching nothing is Unknown and counts for
        /// neither side.</para>
        /// </summary>
        public Status March(Vector3 from, Vector3 to, Status[] status, bool[] flagged)
        {
            var touched = false;
            var queue = new Stack<(int Level, (int X, int Y, int Z) Cell)>();
            queue.Push((_depth, (0, 0, 0)));
            while (queue.Count > 0)
            {
                var (level, cell) = queue.Pop();
                var size = _tree.LeafSize * (1 << level);
                var corner = _tree.Origin + new Vector3(cell.X, cell.Y, cell.Z) * size;
                if (!Crosses(from, to, corner, corner + new Vector3(size)))
                    continue;

                if (level > 0 && _tree.BranchCells.Contains((level, cell)))
                {
                    for (var octant = 0; octant < 8; octant++)
                        queue.Push((level - 1, (cell.X * 2 + (octant & 1),
                                                cell.Y * 2 + ((octant >> 1) & 1),
                                                cell.Z * 2 + ((octant >> 2) & 1))));
                    continue;
                }

                if (!_leaves.TryGetValue((level, cell.X, cell.Y, cell.Z), out var leaf))
                    continue;
                var parts = _byLeaf[leaf];
                if (parts is null)
                    continue;

                var crossed = Voxels(from, to, corner, size * 0.25f);
                foreach (var region in parts)
                {
                    if ((_regions.Regions[region].Open & crossed) == 0)
                        continue;
                    if (flagged[region])
                        return Status.Outside;
                    if (status[region] == Status.Inside)
                        touched = true;
                }
            }
            return touched ? Status.Inside : Status.Unknown;
        }

        /// <summary>Which of a leaf's 64 sub-cells a segment passes through.</summary>
        private static ulong Voxels(Vector3 from, Vector3 to, Vector3 corner, float sub)
        {
            var found = 0UL;
            for (var bit = 0; bit < 64; bit++)
            {
                var at = corner + new Vector3(bit & 3, (bit >> 2) & 3, (bit >> 4) & 3) * sub;
                if (Crosses(from, to, at, at + new Vector3(sub)))
                    found |= 1UL << bit;
            }
            return found;
        }

        /// <summary>Whether a segment reaches into a box, which is the slab test.</summary>
        private static bool Crosses(Vector3 from, Vector3 to, Vector3 mins, Vector3 maxs)
        {
            var along = to - from;
            float enter = 0f, leave = 1f;
            for (var axis = 0; axis < 3; axis++)
            {
                var d = axis == 0 ? along.X : axis == 1 ? along.Y : along.Z;
                var o = axis == 0 ? from.X : axis == 1 ? from.Y : from.Z;
                var lo = axis == 0 ? mins.X : axis == 1 ? mins.Y : mins.Z;
                var hi = axis == 0 ? maxs.X : axis == 1 ? maxs.Y : maxs.Z;
                if (d == 0f)
                {
                    if (o < lo || o > hi)
                        return false;
                    continue;
                }
                var first = (lo - o) / d;
                var second = (hi - o) / d;
                if (first > second)
                    (first, second) = (second, first);
                enter = MathF.Max(enter, first);
                leave = MathF.Min(leave, second);
            }
            return enter <= leave;
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
            return _tree.LeafMasks.TryGetValue((0, cell), out var solid) && (solid & Bit(x, y, z)) != 0;
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
