using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The PVS itself: <c>18002c2a0</c> turning one batch of clear lines into
/// visibility, <c>18002d9e0</c> walking a line through the octree, and
/// <c>18001b690</c> setting the bits.
///
/// <para>The rule is the whole algorithm. Every cluster a clear line passes
/// through can see every other cluster that same line passes through, so a
/// segment collects the clusters it crosses, and each one's row is ORed with the
/// set. The matrix is named in the binary: the write stage asks for
/// <c>MutualVisibilityMatrix</c> by name.</para>
/// </summary>
public static class VisVisibility
{
    /// <summary>Open space, which is the only kind that carries a cluster.</summary>
    public const int Open = 0;

    /// <summary>Kind 1: this one stops a line rather than being seen through.</summary>
    public const int Blocking = 1;

    /// <summary>Kind 2: ignored by the walk outright.</summary>
    public const int Skipped = 2;

    /// <summary>
    /// One 16 byte record of the flat array at <c>this+0x48</c>. A region names a
    /// run of these through the offset packed into its own flag word.
    ///
    /// <para><paramref name="Packed"/> is not a flag word. The compaction writes
    /// it as <c>leaf * 4 | kind</c> and everything downstream reads the two
    /// halves separately, so the octree leaf a record came from survives in the
    /// record itself.</para>
    /// </summary>
    /// <param name="Cluster">The cluster it belongs to, at <c>+0x00</c>, 0 until assignment.</param>
    /// <param name="Packed">At <c>+0x04</c>: <c>(leaf &lt;&lt; 2) | kind</c>.</param>
    /// <param name="Cells">At <c>+0x08</c>: which of the leaf's 64 sub cells it covers.</param>
    public readonly record struct Entry(int Cluster, int Packed, ulong Cells)
    {
        /// <summary>The octree leaf it was written for.</summary>
        public int Leaf => Packed >> 2;

        /// <summary>One of <see cref="Open"/>, <see cref="Blocking"/>, <see cref="Skipped"/>.</summary>
        public int Kind => Packed & 3;
    }

    /// <summary>
    /// One octree node, the 8 bytes at <c>this+0x30</c>: the word carries
    /// <c>IsLeaf</c> in bit 0 and the payload above it, and a leaf's entry count
    /// sits beside it at <c>+0x04</c>.
    /// </summary>
    /// <param name="Word">The packed word at <c>+0x00</c>.</param>
    /// <param name="Count">A leaf's entry count at <c>+0x04</c>.</param>
    public readonly record struct Node(uint Word, ushort Count)
    {
        /// <summary>Bit 0, which is <c>IsLeaf</c> and has never been an outside flag.</summary>
        public bool Leaf => (Word & 1) != 0;

        /// <summary>The first child, or the first entry of a leaf.</summary>
        public int Payload => (int)(Word >> 1);
    }

    /// <summary>
    /// The cluster by cluster bit matrix, which is <c>18001b690</c>. Rows are
    /// 32 bit words and a cluster always sees itself, because the set a segment
    /// collects includes it.
    /// </summary>
    public sealed class Matrix
    {
        private readonly uint[][] _rows;

        /// <summary>Allocate a square matrix. Valve's has two extra clusters, sky and sun.</summary>
        public Matrix(int clusters)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(clusters);
            Clusters = clusters;
            Words = (clusters + 31) / 32;
            _rows = [.. Enumerable.Range(0, clusters).Select(_ => new uint[Words])];
        }

        /// <summary>How many clusters it covers.</summary>
        public int Clusters { get; }

        /// <summary>Words per row.</summary>
        public int Words { get; }

        /// <summary>Whether <paramref name="from"/> has been shown to see <paramref name="to"/>.</summary>
        public bool Sees(int from, int to) => (_rows[from][to >> 5] & (1u << (to & 31))) != 0;

        /// <summary>
        /// Mark every cluster in <paramref name="crossed"/> as seeing every other
        /// one, and return how many words actually changed. That count is the
        /// only thing that makes a ray "useful".
        /// </summary>
        /// <param name="crossed">Cluster ids, sorted and deduplicated.</param>
        public int Mark(ReadOnlySpan<int> crossed)
        {
            if (crossed.Length == 0)
                return 0;

            // Sorted ids collapse into one (word, bits) pair per 32 clusters,
            // which is why the caller has to sort and unique them first.
            Span<int> words = stackalloc int[crossed.Length];
            Span<uint> bits = stackalloc uint[crossed.Length];
            var runs = 0;
            foreach (var id in crossed)
            {
                var word = id >> 5;
                var bit = 1u << (id & 31);
                if (runs > 0 && words[runs - 1] == word)
                {
                    bits[runs - 1] |= bit;
                    continue;
                }
                words[runs] = word;
                bits[runs] = bit;
                runs++;
            }

            var added = 0;
            foreach (var id in crossed)
            {
                var row = _rows[id];
                for (var i = 0; i < runs; i++)
                {
                    if ((row[words[i]] & bits[i]) == bits[i])
                        continue;
                    row[words[i]] |= bits[i];
                    added++;
                }
            }
            return added;
        }
    }

    /// <summary>
    /// <c>18002c2a0</c>: fold one batch's segments into the matrix and report how
    /// many of them changed anything.
    /// </summary>
    /// <param name="matrix">The matrix to OR into.</param>
    /// <param name="segments">The batch's clear lines.</param>
    /// <param name="walk">Collects the clusters one line crosses, or null when blocked.</param>
    /// <param name="anySight">Whether the batch held any sight ray, which is what
    /// decides both the nudge and whether a blocker abandons the line.</param>
    public static int Accumulate(
        Matrix matrix, IReadOnlyList<VisSampler.Segment> segments,
        Func<Vector3, Vector3, bool, List<int>?> walk, bool anySight)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(walk);

        var useful = 0;
        foreach (var (from, to) in segments)
        {
            // Without a sight ray the batch's lines start on geometry, so the
            // origin steps one unit along before the walk and a blocker throws
            // the line away. With one they start in open space and blockers are
            // walked through.
            var origin = anySight ? from : from + Normalise(to - from);
            var crossed = walk(origin, to - origin, anySight);
            if (crossed is null || crossed.Count == 0)
                continue;

            crossed.Sort();
            var unique = Unique(crossed);
            if (matrix.Mark(unique) > 0)
                useful++;
        }
        return useful;
    }

    /// <summary>
    /// <c>18002d9e0</c>: walk a segment through the octree and collect the
    /// clusters of every entry it crosses. Null means a blocking entry stopped
    /// it and <paramref name="throughBlockers"/> was not set.
    /// </summary>
    /// <param name="nodes">The octree at <c>this+0x30</c>.</param>
    /// <param name="entries">The flat entry array at <c>this+0x48</c>.</param>
    /// <param name="rootMins">The root cube's corner.</param>
    /// <param name="rootSize">The root cube's edge.</param>
    /// <param name="origin">Where the segment starts.</param>
    /// <param name="delta">Its full extent, so the segment is t in [0, 1].</param>
    /// <param name="throughBlockers">Whether a blocking entry is walked through.</param>
    public static List<int>? Walk(
        IReadOnlyList<Node> nodes, IReadOnlyList<Entry> entries,
        Vector3 rootMins, float rootSize,
        Vector3 origin, Vector3 delta, bool throughBlockers)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(entries);

        var inverse = Reciprocal(delta);
        var found = new List<int>();
        var queue = new Queue<(int Node, Vector3 Mins, float Size)>();
        queue.Enqueue((0, rootMins, rootSize));
        var last = -1;

        while (queue.Count > 0)
        {
            var (at, mins, size) = queue.Dequeue();
            var node = nodes[at];
            if (!node.Leaf)
            {
                var half = size * 0.5f;
                var octants = Crossed(origin, inverse, mins, size, 2);
                for (var i = 0; i < 8; i++)
                {
                    if ((octants & (1UL << i)) == 0)
                        continue;
                    var corner = mins + (new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1) * half);
                    queue.Enqueue((node.Payload + i, corner, half));
                }
                continue;
            }

            var cells = Crossed(origin, inverse, mins, size, 4);
            for (var i = 0; i < node.Count; i++)
            {
                var entry = entries[node.Payload + i];
                if ((entry.Packed & Skipped) != 0)
                    continue;
                var blocking = (entry.Packed & Blocking) != 0;
                if (!blocking && entry.Cluster == last)
                    continue;
                if ((entry.Cells & cells) == 0)
                    continue;

                if (blocking)
                {
                    if (!throughBlockers)
                        return null;
                }
                else if (entry.Cluster != last)
                {
                    found.Add(entry.Cluster);
                    last = entry.Cluster;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Which of a box's <paramref name="side"/> cubed cells the segment passes
    /// through, which is <c>18010cb10</c> at a side of 2 and <c>18010c6e0</c> at
    /// 4. Both are the per axis slab min and max, so the answer is the product of
    /// three ranges rather than a walk.
    /// </summary>
    /// <param name="origin">Segment start.</param>
    /// <param name="inverse">Reciprocal of its extent.</param>
    /// <param name="mins">The box corner.</param>
    /// <param name="size">The box edge.</param>
    /// <param name="side">Cells per axis, 2 or 4.</param>
    public static ulong Crossed(Vector3 origin, Vector3 inverse, Vector3 mins, float size, int side)
    {
        var maxs = mins + new Vector3(size);
        var low = (mins - origin) * inverse;
        var high = (maxs - origin) * inverse;
        var enter = MathF.Max(MathF.Max(MathF.Min(low.X, high.X), MathF.Min(low.Y, high.Y)),
                              MathF.Max(MathF.Min(low.Z, high.Z), 0f));
        var leave = MathF.Min(MathF.Min(MathF.Max(low.X, high.X), MathF.Max(low.Y, high.Y)),
                              MathF.Min(MathF.Max(low.Z, high.Z), 1f));
        if (enter > leave)
            return 0;

        var delta = Reciprocal(inverse);
        var a = origin + (delta * enter);
        var b = origin + (delta * leave);
        var cell = size / side;

        Span<int> from = stackalloc int[3];
        Span<int> to = stackalloc int[3];
        for (var axis = 0; axis < 3; axis++)
        {
            var one = Component(a, axis);
            var two = Component(b, axis);
            from[axis] = Slot(MathF.Min(one, two) - Component(mins, axis), cell, side);
            to[axis] = Slot(MathF.Max(one, two) - Component(mins, axis), cell, side);
        }

        var mask = 0UL;
        for (var z = from[2]; z <= to[2]; z++)
            for (var y = from[1]; y <= to[1]; y++)
                for (var x = from[0]; x <= to[0]; x++)
                    mask |= 1UL << (x + (y * side) + (z * side * side));
        return mask;
    }

    private static int Slot(float along, float cell, int side) =>
        Math.Clamp((int)MathF.Floor(along / cell), 0, side - 1);

    private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>
    /// The guarded reciprocal the walk uses, which is the <c>divps</c> against a
    /// vector ORed with a small floor so a zero component becomes an enormous
    /// slope rather than an infinity.
    /// </summary>
    private static Vector3 Reciprocal(Vector3 v)
    {
        const float Floor = 1e-20f;
        return new Vector3(
            1f / (MathF.Abs(v.X) < Floor ? MathF.CopySign(Floor, v.X == 0f ? 1f : v.X) : v.X),
            1f / (MathF.Abs(v.Y) < Floor ? MathF.CopySign(Floor, v.Y == 0f ? 1f : v.Y) : v.Y),
            1f / (MathF.Abs(v.Z) < Floor ? MathF.CopySign(Floor, v.Z == 0f ? 1f : v.Z) : v.Z));
    }

    private static Vector3 Normalise(Vector3 v)
    {
        var length = v.Length();
        return length > 0f ? v / length : Vector3.Zero;
    }

    private static int[] Unique(List<int> sorted)
    {
        var kept = new List<int>(sorted.Count);
        foreach (var id in sorted)
            if (kept.Count == 0 || kept[^1] != id)
                kept.Add(id);
        return [.. kept];
    }
}
