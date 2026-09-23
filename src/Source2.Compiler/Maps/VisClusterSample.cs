using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The per-cluster visibility sample, ported from visbuilder.dll's
/// <c>180031a20</c> and the setup around it in <c>180030df0</c>.
///
/// <para>This is what <see cref="VisMergeCost"/> reads, and without it the merge
/// has nothing to weigh: the cost is the visibility each side INHERITS, so two
/// clusters that already see the same things merge for the price of the gap
/// between them, and two that do not are 32 or 128 times dearer.</para>
///
/// <para>The sample is local. A ray leaves a cluster's box centre toward every
/// other entry's centre, runs until it meets geometry or reaches the padded
/// box's diagonal, and sets one bit for every entry box the segment crosses. So
/// "visibility" here means which of this region's own voxels, and which of the
/// shell boxes around it, a straight line from this voxel actually reaches.</para>
/// </summary>
public static class VisClusterSample
{
    /// <summary>The padded box's margin before the grow, <c>DAT_18017f148</c>.</summary>
    public const float Margin = 2f;

    /// <summary>The cells of the shell, <c>DAT_18017bfa0</c>.</summary>
    /// <remarks>
    /// A 4x4x4 grid of half-leaf cells over the padded box with the central 2x2x2
    /// removed, which is 56 boxes, and the table in the binary is exactly that
    /// set. They carry no cluster, so they never merge; they exist so a ray that
    /// leaves the region still crosses something and the cost can tell one
    /// direction of escape from another.
    /// </remarks>
    public static readonly int[] Shell =
    [
        .. from cell in Enumerable.Range(0, 64)
           let x = cell & 3
           let y = (cell >> 2) & 3
           let z = (cell >> 4) & 3
           where x is 0 or 3 || y is 0 or 3 || z is 0 or 3
           select cell,
    ];

    /// <summary>One box the merge holds, whether or not it carries a cluster.</summary>
    /// <param name="Mins">Its box.</param>
    /// <param name="Maxs">Its box.</param>
    /// <param name="Padding">A shell box, which never merges and only takes bits.</param>
    public sealed record Entry(Vector3 Mins, Vector3 Maxs, bool Padding);

    /// <summary>The padded box the merge works inside, and its diagonal.</summary>
    public static (Vector3 Mins, Vector3 Maxs) Padded(Vector3 leafMins, float side)
    {
        var grow = (side + (Margin * 2f)) * 0.5f;
        var back = Margin + grow;
        return (leafMins - new Vector3(back),
                leafMins + new Vector3(side) + new Vector3(back));
    }

    /// <summary>
    /// The merge's entries for one region: a cluster per open voxel in bit order,
    /// then the 56 shell boxes, which is the order <c>1800337a0</c> fills them in.
    /// </summary>
    public static List<Entry> Entries(Vector3 leafMins, float side, ulong open)
    {
        var voxel = side * 0.25f;
        var found = new List<Entry>();
        for (var bit = 0; bit < 64; bit++)
        {
            if ((open & (1UL << bit)) == 0)
                continue;
            var at = leafMins + new Vector3(bit & 3, (bit >> 2) & 3, (bit >> 4) & 3) * voxel;
            found.Add(new Entry(at, at + new Vector3(voxel), false));
        }

        var (mins, _) = Padded(leafMins, side);
        var half = side * 0.5f;
        foreach (var cell in Shell)
        {
            var at = mins + new Vector3(cell & 3, (cell >> 2) & 3, (cell >> 4) & 3) * half;
            found.Add(new Entry(at, at + new Vector3(half), true));
        }
        return found;
    }

    /// <summary>Directions a set of 512 or more clusters samples with.</summary>
    public const int SphereDirections = 512;

    /// <summary>
    /// A leaf index to its cube, which the marking walk needs because it
    /// confirms against a cluster's occupied cells rather than its box. The
    /// compile reaches the same thing through the sampler at the merge state's
    /// <c>+0x20</c>.
    /// </summary>
    /// <param name="leaf">The leaf a cluster's voxel pair names.</param>
    public delegate (Vector3 Corner, float Side) LeafCube(int leaf);

    /// <summary>
    /// The direction set <c>180027400</c> builds when a merge set is big enough
    /// to stop sampling toward every other cluster: a golden-spiral sphere of
    /// <see cref="SphereDirections"/> unit vectors.
    ///
    /// <para>The switch at 512 is not an optimisation to skip. It changes which
    /// bits are set, which changes every cost, which changes the merge order, so
    /// a set of 511 and a set of 512 are sampled differently on purpose.</para>
    /// </summary>
    public static readonly Vector3[] Sphere = BuildSphere(SphereDirections);

    private static Vector3[] BuildSphere(int count)
    {
        var step = 2f / count;
        var turn = (3f - MathF.Sqrt(5f)) * MathF.PI;
        var found = new Vector3[count];
        for (var i = 0; i < count; i++)
        {
            var y = (i * step) - 1f + (step * 0.5f);
            var radius = MathF.Sqrt(MathF.Max(1f - (y * y), 0f));
            var angle = i * turn;
            found[i] = new Vector3(MathF.Cos(angle) * radius, y, MathF.Sin(angle) * radius);
        }
        return found;
    }

    /// <summary>
    /// Fill every cluster's bit vector over the set it is now part of, which is
    /// <c>180030df0</c> dispatching <c>180031a20</c>.
    ///
    /// <para>The entries are the clusters, then the 56 shell boxes when
    /// <paramref name="padded"/>. The box is the leaf's when padded, and the
    /// grid cell's when not; the rays reach its diagonal.</para>
    /// </summary>
    public static void SampleInto(
        RayTraceEnvironment scene, IReadOnlyList<VisMerge.Cluster> clusters,
        Vector3 mins, Vector3 maxs, bool padded, LeafCube cube)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(clusters);
        ArgumentNullException.ThrowIfNull(cube);

        var (boxMins, boxMaxs) = padded ? Padded(mins, maxs.X - mins.X) : (mins, maxs);
        var extent = boxMaxs - boxMins;
        var reach = MathF.Sqrt((extent.Z * extent.Z) + (extent.Y * extent.Y) + (extent.X * extent.X));

        var boxes = new List<(Vector3 Mins, Vector3 Maxs)>(clusters.Count + Shell.Length);
        foreach (var cluster in clusters)
            boxes.Add((cluster.Mins, cluster.Maxs));
        if (padded)
        {
            var half = (maxs.X - mins.X) * 0.5f;
            foreach (var cell in Shell)
            {
                var at = boxMins + new Vector3(cell & 3, (cell >> 2) & 3, (cell >> 4) & 3) * half;
                boxes.Add((at, at + new Vector3(half)));
            }
        }

        var centres = boxes.Select(b => (b.Mins + b.Maxs) * 0.5f).ToArray();
        var words = (boxes.Count + 63) / 64;
        var sphere = boxes.Count >= SphereDirections;

        // 18003d600 walks the merge's OWN AABB tree, the one 1800337a0 built and
        // 1800306e0 queries: a leaf's payload is the entry, and a payload with no
        // cluster behind it is a shell box, which counts on its box alone. Same
        // structure, same boxes, so it is built here rather than approximated.
        var index = new VisBoxTree();
        for (var i = 0; i < boxes.Count; i++)
            index.Create(boxes[i].Mins, boxes[i].Maxs, i);

        Parallel.For(0, clusters.Count, i =>
        {
            var bits = new ulong[words];
            var reached = new List<int>();
            var from = centres[i];
            var aimed = sphere ? Sphere.Length : centres.Length;
            for (var j = 0; j < aimed; j++)
            {
                // Below the sphere switch 180032fa0 aims at EVERY entry, itself
                // included: the self ray has no direction, meets nothing, and
                // walks a segment of zero length from the centre.
                var direction = sphere ? Sphere[j] : Direction(centres[j] - from);
                var segment = Ray(scene, from, direction, reach);
                var to = new Vector3(segment.X + from.X, segment.Y + from.Y, segment.Z + from.Z);
                reached.Clear();
                index.Crossed(from, to, reached);

                foreach (var k in reached)
                    if (Occupied(clusters, k, cube, from, segment))
                        bits[k >> 6] |= 1UL << (k & 63);
            }
            clusters[i].Visibility = bits;
        });
    }

    /// <summary>
    /// A direction as 180032fa0 normalises one: the length summed z first, a
    /// reciprocal multiply rather than a divide, and a zero vector for anything
    /// shorter than 1e-17. The slow path above 1e17 is unreachable inside a map.
    /// </summary>
    public static Vector3 Direction(Vector3 v)
    {
        var length = MathF.Sqrt((v.Z * v.Z) + (v.Y * v.Y) + (v.X * v.X));
        if (length < 1e-17f || length > 1e17f)
        {
            if (length == 0f)
                return Vector3.Zero;
            var l = Math.Sqrt(((double)v.X * v.X) + ((double)v.Y * v.Y) + ((double)v.Z * v.Z));
            return new Vector3((float)(v.X / l), (float)(v.Y / l), (float)(v.Z / l));
        }
        var inverse = 1f / length;
        return new Vector3(v.X * inverse, v.Y * inverse, v.Z * inverse);
    }

    /// <summary>
    /// One sampling ray, as the segment the walk is handed: <c>CastRayGrid</c>
    /// traces the centre to <c>centre + MaxCoord * dir</c> through the batch
    /// tracer, <c>NoDrawSecondLook</c> looks past a nodraw-only surface,
    /// <c>TallyRays</c> drops a hit the centre sees from behind, and
    /// <c>180032fa0</c> turns what is left into <c>(t * dir + o) - centre</c>,
    /// with the box diagonal standing in for t when nothing was hit. Every sum
    /// is in the binary's order because a centre on a wall is decided by them.
    /// </summary>
    public static Vector3 Ray(RayTraceEnvironment scene, Vector3 o, Vector3 d, float reach)
    {
        var far = RayTraceEnvironment.MaxCoord;
        var end = new Vector3((far * d.X) + o.X, (far * d.Y) + o.Y, (far * d.Z) + o.Z);
        var hit = scene.Segment(o, end, VisSeed.Ignored);
        if (hit is { } first && (scene.Flags(first.Triangle) & VisSeed.Insubstantial) == VisSeed.NoDrawOnly
            && scene.Segment(o, end, VisSeed.SeeingThroughNoDraw) is { } again
            && Facing(again, o, d) >= 0f
            && (scene.Flags(again.Triangle) & VisSeed.Insubstantial) == 0)
            hit = again;
        if (hit is { } h && Facing(h, o, d) < 0f)
            hit = null;
        var t = hit?.Distance ?? reach;
        return new Vector3(((t * d.X) + o.X) - o.X, ((t * d.Y) + o.Y) - o.Y, ((t * d.Z) + o.Z) - o.Z);
    }

    // dot(n, centre) - dot(n, landed), each summed z, y, x as TallyRays does.
    private static float Facing(RayTraceEnvironment.Hit h, Vector3 o, Vector3 d)
    {
        var n = h.Normal;
        var t = h.Distance;
        var centre = (n.Z * o.Z) + (o.Y * n.Y) + (n.X * o.X);
        var landed = (((t * d.Z) + o.Z) * n.Z) + (((t * d.Y) + o.Y) * n.Y) + (((t * d.X) + o.X) * n.X);
        return centre - landed;
    }

    /// <summary>
    /// Whether the segment reaches an entry for real. Crossing the box is only
    /// the broad phase: <c>18003d600</c> then walks the cluster's own (mask,
    /// leaf) pairs and keeps it only when the segment passes through a cell the
    /// cluster actually occupies. A shell entry carries no cluster and the box
    /// is all there is, so it counts on the box alone.
    /// </summary>
    private static bool Occupied(
        IReadOnlyList<VisMerge.Cluster> clusters, int entry, LeafCube cube, Vector3 from, Vector3 segment)
    {
        if (entry >= clusters.Count)
            return true;

        var inverse = new Vector3(1f / Guard(segment.X), 1f / Guard(segment.Y), 1f / Guard(segment.Z));
        var last = -1;
        var cells = 0UL;
        foreach (var (mask, leaf) in clusters[entry].Voxels)
        {
            if (leaf != last)
            {
                var (corner, side) = cube(leaf);
                cells = VisVisibility.Crossed(from, inverse, corner, side, 4);
                last = leaf;
            }
            if ((mask & cells) != 0)
                return true;
        }
        return false;
    }

    // 18003ed60's divisor: under FLT_MIN the bits of FLT_EPSILON are ORed in,
    // which keeps the sign, so a zero component divides as +-1.19e-7.
    private static float Guard(float x)
        => MathF.Abs(x) < 1.17549435e-38f
            ? BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(x) | 0x34000000)
            : x;

    /// <summary>
    /// A bit vector per CLUSTER entry, over every entry including the shell. The
    /// shell entries take no vector of their own because they never merge.
    /// </summary>
    public static ulong[][] Visibility(
        RayTraceEnvironment scene, Vector3 leafMins, float side, ulong open, IReadOnlyList<Entry> entries)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(entries);

        var (mins, maxs) = Padded(leafMins, side);
        var reach = (maxs - mins).Length();
        var far = RayTraceEnvironment.MaxCoord;

        var clusters = entries.Count(e => !e.Padding);
        var words = (entries.Count + 63) / 64;
        var centres = new Vector3[entries.Count];
        for (var i = 0; i < entries.Count; i++)
            centres[i] = (entries[i].Mins + entries[i].Maxs) * 0.5f;

        var found = new ulong[clusters][];
        for (var i = 0; i < clusters; i++)
        {
            var bits = new ulong[words];
            var from = centres[i];
            for (var j = 0; j < entries.Count; j++)
            {
                if (j == i)
                    continue;
                var direction = Vector3.Normalize(centres[j] - from);
                var to = from + (direction * Nearest(scene, from, direction, reach, far));
                for (var k = 0; k < entries.Count; k++)
                    if (Crosses(from, to, entries[k].Mins, entries[k].Maxs))
                        bits[k >> 6] |= 1UL << (k & 63);
            }
            found[i] = bits;
        }
        return found;
    }

    /// <summary>
    /// How far a ray gets. Only geometry inside the padded box can matter: a hit
    /// beyond it cuts the segment somewhere there is nothing left to cross, and
    /// the box's own diagonal always carries a ray out of it from any point
    /// inside, so a miss and a distant hit are the same answer.
    /// </summary>
    /// <summary>
    /// How far a sampling ray gets: <c>18004a690</c> casting against the whole
    /// scene, <c>18004b970</c> looking again through nodraw, and
    /// <c>18004a420</c> deciding what the record ends up holding.
    ///
    /// <para>Three things here are the compile's and none of them were obvious.
    /// The cast runs to <c>g_flConfigMaxCoord</c>, not to
    /// <paramref name="reach"/>, so a hit BEYOND the sampled box is still a hit
    /// and the segment is longer than the box's diagonal. A BACK facing hit is
    /// not a hit at all: <c>18004a420</c> clears the record's hit bit and puts
    /// FLT_MAX back in the distance, exactly as it does for a ray that met
    /// nothing. And <paramref name="reach"/> is only the fallback those two cases
    /// fall back TO, which is where <c>180031a20</c> reads it.</para>
    ///
    /// <para>It used to test only the triangles overlapping the box, paid for
    /// once per set. That is sound for a leaf and wrong for a merge pass, whose
    /// box is a 512 unit grid cell spanning the full height of the map: a ray
    /// leaves the cell long before it runs out, and every triangle it would then
    /// have hit was missing from the list.</para>
    /// </summary>
    /// <param name="scene">The ray trace scene.</param>
    /// <param name="from">The cluster's box centre.</param>
    /// <param name="direction">Unit direction.</param>
    /// <param name="reach">The sampled box's diagonal, used when nothing is hit.</param>
    /// <param name="far">How far the cast runs, <see cref="RayTraceEnvironment.MaxCoord"/>.</param>
    private static float Nearest(
        RayTraceEnvironment scene, Vector3 from, Vector3 direction, float reach, float far)
    {
        if (scene.Trace(from, direction, far, VisSeed.Ignored) is not { } first)
            return reach;
        var hit = VisSeed.Behind(scene, from, direction, far, first);
        var landed = from + (direction * hit.Distance);
        return Vector3.Dot(hit.Normal, from) >= Vector3.Dot(hit.Normal, landed) ? hit.Distance : reach;
    }

    /// <summary>
    /// A bounding volume hierarchy over the merge set's boxes, so a segment finds
    /// what it crosses in log time rather than by testing all of them.
    ///
    /// <para>The compile walks a CBoxMerge hierarchy for this. Testing every box
    /// instead is fine for a region's 120 and hopeless for a grid cell's few
    /// thousand, where it is over a billion tests a bucket.</para>
    /// </summary>
    private sealed class BoxTree
    {
        /// <summary>Boxes below which a node stops splitting.</summary>
        public const int LeafSize = 8;

        private readonly Vector3[] _mins;
        private readonly Vector3[] _maxs;
        private readonly int[] _order;
        private readonly List<Node> _nodes = [];

        private readonly record struct Node(
            Vector3 Mins, Vector3 Maxs, int Start, int Count, int Left, int Right);

        public BoxTree(IReadOnlyList<(Vector3 Mins, Vector3 Maxs)> boxes)
        {
            ArgumentNullException.ThrowIfNull(boxes);
            _mins = [.. boxes.Select(b => b.Mins)];
            _maxs = [.. boxes.Select(b => b.Maxs)];
            _order = [.. Enumerable.Range(0, boxes.Count)];
            if (boxes.Count > 0)
                Build(0, boxes.Count);
        }

        private int Build(int from, int count)
        {
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            for (var i = from; i < from + count; i++)
            {
                lo = Vector3.Min(lo, _mins[_order[i]]);
                hi = Vector3.Max(hi, _maxs[_order[i]]);
            }

            var node = _nodes.Count;
            _nodes.Add(new Node(lo, hi, from, count, -1, -1));
            if (count <= LeafSize)
                return node;

            var span = hi - lo;
            var axis = span.X >= span.Y && span.X >= span.Z ? 0 : span.Y >= span.Z ? 1 : 2;
            var ids = _order[from..(from + count)];
            var keys = ids.Select(t => axis == 0 ? _mins[t].X : axis == 1 ? _mins[t].Y : _mins[t].Z).ToArray();
            Array.Sort(keys, ids);
            ids.CopyTo(_order, from);

            var half = count / 2;
            var left = Build(from, half);
            var right = Build(from + half, count - half);
            _nodes[node] = _nodes[node] with { Left = left, Right = right };
            return node;
        }

        /// <summary>Collect every box the segment reaches into.</summary>
        public void Crossed(Vector3 from, Vector3 to, List<int> into)
        {
            if (_nodes.Count == 0)
                return;
            Span<int> stack = stackalloc int[64];
            var depth = 0;
            stack[depth++] = 0;
            while (depth > 0)
            {
                var node = _nodes[stack[--depth]];
                if (!Crosses(from, to, node.Mins, node.Maxs))
                    continue;
                if (node.Left < 0)
                {
                    for (var i = node.Start; i < node.Start + node.Count; i++)
                    {
                        var box = _order[i];
                        if (Crosses(from, to, _mins[box], _maxs[box]))
                            into.Add(box);
                    }
                    continue;
                }
                stack[depth++] = node.Left;
                stack[depth++] = node.Right;
            }
        }
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
}
