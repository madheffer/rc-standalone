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
/// parameters here, and the old docs/VIS.md (git c2dc317) records how the region count moves with
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
    /// <param name="watch">Told what the second pass made of each region it voted on.</param>
    /// <param name="trace">Told, per region, what each of its marches walked.</param>
    public static Result Detect(
        VisVoxelizer.Octree tree, VisRegions.Result regions,
        RayTraceEnvironment scene, float baseVoxelSize,
        int quality = VisSeed.Quality, Action<Judged>? watch = null,
        Action<int, string>? trace = null)
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
            status[i] = Classify(space, scene, status, flagged, i, quality, watch, trace);
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
    /// <summary>What the second pass made of one region, for diagnosis.</summary>
    /// <param name="Region">Which region.</param>
    /// <param name="Answer">The <see cref="Status"/> it settled on.</param>
    /// <param name="Marched">Rays that landed facing and so were marched.</param>
    /// <param name="Inside">Marches that reached a region already known enclosed.</param>
    /// <param name="Outside">Marches that met one already known outside.</param>
    /// <param name="Stop">Mean length a march ran.</param>
    /// <param name="Leaves">Mean leaves a march walked.</param>
    /// <param name="Regions">Mean regions a march tested.</param>
    public readonly record struct Judged(
        int Region, int Answer, int Marched, int Inside, int Outside,
        float Stop, float Leaves, float Regions);

    /// <summary>Each region's box as the seed casts from it (18010be50), for comparisons.</summary>
    internal static Func<int, (Vector3 Mins, Vector3 Maxs)> RegionBoxes(VisVoxelizer.Octree tree, VisRegions.Result regions, float baseVoxelSize)
        => new Space(tree, regions, baseVoxelSize).Box;

    private static Status Classify(
        Space space, RayTraceEnvironment scene, Status[] status, bool[] flagged, int region,
        int quality, Action<Judged>? watch = null, Action<int, string>? trace = null)
    {
        // ClassifyRegion (18002f5d0): a centre outside the TRACER's bounds
        // (sampler +0xe8, not the file header's) is outside outright.
        var (mins, maxs) = space.Box(region);
        var centre = (mins + maxs) * 0.5f;
        var (low, high) = scene.TracedBounds;
        if (!(low.X <= centre.X && low.Y <= centre.Y && low.Z <= centre.Z
              && centre.X <= high.X && centre.Y <= high.Y && centre.Z <= high.Z))
            return Status.Outside;

        var grid = Math.Clamp(quality, 2, 10);
        int inside = 0, outside = 0, marched = 0, leaves = 0, regions = 0;
        var stops = 0f;
        // It gathers again and marches the gather's own records: every ray
        // whose hit faced the centre, to max(t - 8, 0.1) along it.
        var casts = new List<VisSeed.Cast>();
        VisSeed.Gather(scene, mins, maxs, quality, casts);
        foreach (var cast in casts)
        {
            if (!cast.Facing)
                continue;
            var direction = cast.Direction;
            var stop = MathF.Max(cast.Distance - MarchBackOff, MarchShortest);
            var end = new Vector3((stop * direction.X) + cast.Origin.X, (stop * direction.Y) + cast.Origin.Y, (stop * direction.Z) + cast.Origin.Z);
            var reached = space.MarchRay(cast.Origin, end, status, flagged, out var walked, out var tested);
            trace?.Invoke(region, $"  ray {marched}: dir {direction} hit {cast.Distance:F1}"
                                + $" stop {stop:F1} -> {reached} ({walked} leaves, {tested} regions)");
            marched++;
            stops += stop;
            leaves += walked;
            regions += tested;
            if (reached == Status.Inside) inside++;
            else if (reached == Status.Outside) outside++;
        }

        var n = grid * grid;
        var answer = inside > n * 2 || (inside > n && outside < OutsideVotesAllowed)
            ? Status.Inside
            : Status.Outside;
        watch?.Invoke(new Judged(region, (int)answer, marched, inside, outside,
                                 marched == 0 ? 0f : stops / marched,
                                 marched == 0 ? 0f : (float)leaves / marched,
                                 marched == 0 ? 0f : (float)regions / marched));
        return answer;
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
        /// <summary>
        /// <c>MarchRay</c> (18002f430) as ClassifyRegion calls it: a breadth
        /// first walk from the root, a branch's children taken by
        /// <c>OctantMask</c> (18010e500) on the node's MINIMUM corner passed as
        /// both of its box arguments, so every slab is one value and an octant
        /// is entered only when the segment passes exactly through that corner
        /// (all three t equal, within [0, 1]). A leaf would be tested with
        /// <c>CellMask</c> (18010e0d0, not ported) against all its region
        /// records, solid ones included, whose status bytes are never written;
        /// reaching one stops the build. Inside when an inside region was met,
        /// outside on a flagged one, else unknown.
        /// </summary>
        public Status MarchRay(Vector3 from, Vector3 to, Status[] status, bool[] flagged, out int leaves, out int seen)
        {
            leaves = 0;
            seen = 0;
            // ClassifyRegion's divps: a component under FLT_MIN in magnitude has
            // 1.1920929e-07's bits ORed in (a zero becomes 2^-23).
            static float Guarded(float d)
                => 1f / (MathF.Abs(d) < 1.17549435e-38f ? BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(d) | 0x34000000) : d);
            var inverse = new Vector3(Guarded(to.X - from.X), Guarded(to.Y - from.Y), Guarded(to.Z - from.Z));
            var queue = new Queue<(int Level, (int X, int Y, int Z) Cell)>();
            queue.Enqueue((_depth, (0, 0, 0)));
            while (queue.Count > 0)
            {
                var (level, cell) = queue.Dequeue();
                var size = _tree.LeafSize * (1 << level);
                var corner = _tree.Origin + new Vector3(cell.X, cell.Y, cell.Z) * size;
                if (level > 0 && _tree.BranchCells.Contains((level, cell)))
                {
                    var octants = OctantMask(from, inverse, corner, corner);
                    for (var octant = 0; octant < 8; octant++)
                        if ((octants & (1 << octant)) != 0)
                            queue.Enqueue((level - 1, (cell.X * 2 + (octant & 1), cell.Y * 2 + ((octant >> 1) & 1), cell.Z * 2 + ((octant >> 2) & 1))));
                    continue;
                }
                leaves++;
                throw new NotSupportedException($"MarchRay reached a leaf (level {level}, cell {cell}): CellMask (18010e0d0) and the solid records' unwritten status bytes are not ported");
            }
            return Status.Unknown;
        }

        // OctantMask (18010e500) lane by lane: a = (A - o) inv, b = (B - o) inv,
        // m = (b + a) 0.5; per axis the halves [a, m] and [m, b] as min/max
        // pairs (minps/maxps keep the second operand on a tie or NaN); an octant
        // is set when max(lowers, 0) <= min(uppers, 1); bit x + 2y + 4z.
        private static int OctantMask(Vector3 o, Vector3 inv, Vector3 a3, Vector3 b3)
        {
            static float MinPs(float x, float y) => x < y ? x : y;
            static float MaxPs(float x, float y) => x > y ? x : y;
            float[] a = [(a3.X - o.X) * inv.X, (a3.Y - o.Y) * inv.Y, (a3.Z - o.Z) * inv.Z];
            float[] b = [(b3.X - o.X) * inv.X, (b3.Y - o.Y) * inv.Y, (b3.Z - o.Z) * inv.Z];
            float[] m = [(b[0] + a[0]) * 0.5f, (b[1] + a[1]) * 0.5f, (b[2] + a[2]) * 0.5f];
            var lo = new float[3, 2];
            var hi = new float[3, 2];
            for (var axis = 0; axis < 3; axis++)
            {
                lo[axis, 0] = MinPs(a[axis], m[axis]);
                hi[axis, 0] = MaxPs(a[axis], m[axis]);
                lo[axis, 1] = MinPs(m[axis], b[axis]);
                hi[axis, 1] = MaxPs(m[axis], b[axis]);
            }
            var mask = 0;
            for (var z = 0; z < 2; z++)
                for (var y = 0; y < 2; y++)
                    for (var x = 0; x < 2; x++)
                    {
                        // As the asm folds them: x then y, then z, then the [0, 1] clamp.
                        var enter = MaxPs(MaxPs(lo[2, z], MaxPs(lo[0, x], lo[1, y])), 0f);
                        var leave = MinPs(MinPs(hi[2, z], MinPs(hi[0, x], hi[1, y])), 1f);
                        if (enter <= leave)
                            mask |= 1 << (x + (2 * y) + (4 * z));
                    }
            return mask;
        }

        public Status March(Vector3 from, Vector3 to, Status[] status, bool[] flagged)
            => March(from, to, status, flagged, out _, out _);

        public Status March(Vector3 from, Vector3 to, Status[] status, bool[] flagged,
                            out int leaves, out int seen, Action<string>? trace = null)
        {
            leaves = 0;
            seen = 0;
            // 18002deb0 descends by the OCTANT MASK and tests a leaf by the 64
            // cell mask, and both of those are the per axis slab product rather
            // than an exact segment test. That is deliberately conservative: the
            // march reaches cells the segment only grazes, which is what lets it
            // meet an outside region and stop.
            var inverse = VisVisibility.Reciprocal(to - from);
            var touched = false;
            var queue = new Queue<(int Level, (int X, int Y, int Z) Cell)>();
            queue.Enqueue((_depth, (0, 0, 0)));
            while (queue.Count > 0)
            {
                var (level, cell) = queue.Dequeue();
                var size = _tree.LeafSize * (1 << level);
                var corner = _tree.Origin + new Vector3(cell.X, cell.Y, cell.Z) * size;

                if (level > 0 && _tree.BranchCells.Contains((level, cell)))
                {
                    var octants = VisVisibility.Crossed(from, inverse, corner, size, 2);
                    for (var octant = 0; octant < 8; octant++)
                        if ((octants & (1UL << octant)) != 0)
                            queue.Enqueue((level - 1, (cell.X * 2 + (octant & 1),
                                                       cell.Y * 2 + ((octant >> 1) & 1),
                                                       cell.Z * 2 + ((octant >> 2) & 1))));
                    continue;
                }

                if (!_leaves.TryGetValue((level, cell.X, cell.Y, cell.Z), out var leaf))
                    continue;
                var parts = _byLeaf[leaf];
                if (parts is null)
                    continue;

                leaves++;
                var crossed = VisVisibility.Crossed(from, inverse, corner, size, 4);
                trace?.Invoke($"    leaf {leaf} level {level} at {corner} size {size}"
                            + $" crossed {crossed:x16} holds {parts.Count}");
                foreach (var region in parts)
                {
                    seen++;
                    trace?.Invoke($"      region {region} open {_regions.Regions[region].Open:x16}"
                                + $" meets {(_regions.Regions[region].Open & crossed) != 0}"
                                + $" status {status[region]} flagged {flagged[region]}");
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
