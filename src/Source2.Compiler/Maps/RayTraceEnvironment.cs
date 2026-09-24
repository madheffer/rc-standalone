using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Source2.Compiler.Maps;

/// <summary>
/// The <c>.rte</c> ray trace scene visibility is built from, which the compile
/// leaves in <c>%TEMP%\csgo_addons\&lt;addon&gt;\maps\&lt;map&gt;.rte</c>.
///
/// <para>It matters more than its size suggests: it survives the compile and it is
/// the geometry input to visibility, so a visibility builder can be written and
/// scored against a real map before a geometry pipeline exists. The layout is in
/// docs/RTE.md and both known specimens tile it exactly.</para>
/// </summary>
public sealed class RayTraceEnvironment
{
    private readonly byte[] _data;
    private readonly int _triangleAt;
    private readonly int _indexAt;

    /// <summary>Triangles the file holds, including any visibility excludes.</summary>
    public int TriangleCount { get; }

    /// <summary>The world box the file states for itself.</summary>
    public Vector3 Mins { get; }

    /// <summary>The world box the file states for itself.</summary>
    public Vector3 Maxs { get; }

    /// <summary>
    /// The bits <c>CVisibilityMesh::LoadRTEFromFile</c> tests before converting a
    /// triangle at all: <c>if ((flags &amp; 0x801) == 0)</c>. The twelve of
    /// ze_hold_em_p carrying <c>0x800</c> are the difference between the file's
    /// 4,548 triangles and the compile's own "Convert RTE with 4536 triangles".
    /// </summary>
    public const ushort ExcludedFromTrace = 0x0801;

    /// <summary>
    /// Nodraw, as the converter leaves it. It reads <c>0x10</c> and rewrites it to
    /// this, so downstream only ever sees one bit.
    ///
    /// <para>A nodraw triangle IS converted and IS traced. That is the whole point
    /// of the material: it seals the world without drawing. What the flag buys is
    /// the check below it, where a map whose vis geometry is over 80% nodraw by
    /// area has the mark cleared off every triangle and logs "Vis geometry appears
    /// to be mostly nodraw, reconfiguring".</para>
    /// </summary>
    public const ushort NoDraw = 0x0020;

    /// <summary>The bit the converter rewrites into <see cref="NoDraw"/>.</summary>
    public const ushort NoDrawInFile = 0x0010;

    /// <summary>Fraction of nodraw area that makes the converter drop the mark.</summary>
    public const float NoDrawReconfigureAt = 0.8f;

    /// <summary>
    /// How far a cast ray reaches: <c>g_flConfigMaxCoord</c>, which every caster
    /// in the binary builds its end point with.
    ///
    /// <para>It is not a guess and it is not the scene's diagonal. visbuilder
    /// imports the symbol from tier0.dll, whose export table puts it in
    /// <c>.data</c> holding <c>0x46800000</c>. On a map longer than this a ray
    /// down its length is an ESCAPE rather than a hit, which is a real
    /// difference: ze_hold_em_p is 21,379 units end to end.</para>
    /// </summary>
    public const float MaxCoord = 16384f;

    /// <summary>Nine counts and the world box, which is all of it.</summary>
    public const int HeaderSize = 60;

    private RayTraceEnvironment(byte[] data)
    {
        _data = data;
        var nodes = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        TriangleCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        Mins = Vector(36);
        Maxs = Vector(48);
        _triangleAt = HeaderSize + (nodes * 8);
        _indexAt = _triangleAt + (TriangleCount * 48);
    }

    /// <summary>Read one, or throw when the sections do not tile the file.</summary>
    public static RayTraceEnvironment Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < HeaderSize)
            throw new InvalidOperationException("too short to hold an .rte header");

        var rte = new RayTraceEnvironment(data);
        var indices = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20));
        var total = rte._triangleAt + (rte.TriangleCount * 48) + (indices * 4)
                  + (rte.TriangleCount * 8) + (rte.TriangleCount * 12);
        return total == data.Length
            ? rte
            : throw new InvalidOperationException(
                $"the .rte sections total {total} bytes and the file is {data.Length}; the layout is wrong for it");
    }

    /// <summary>Read from disk.</summary>
    public static RayTraceEnvironment ReadFile(string path) => Read(File.ReadAllBytes(path));

    /// <summary>
    /// The triangle's flag word, at <c>+0x2e</c>, with <see cref="NoDrawInFile"/>
    /// folded into <see cref="NoDraw"/> the way the converter folds it.
    /// </summary>
    public ushort Flags(int index)
    {
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(Record(index)[0x2e..]);
        return (flags & NoDrawInFile) != 0
            ? (ushort)((flags & ~NoDrawInFile) | NoDraw)
            : flags;
    }

    /// <summary>Whether the converter puts this triangle into the trace scene.</summary>
    public bool Traced(int index) => (Flags(index) & ExcludedFromTrace) == 0;

    /// <summary>
    /// The flag word exactly as the file holds it, with no fold. The batch
    /// tracer reads bit 4 directly (<c>(*(ushort *)(tri + 0x2e) &gt;&gt; 4) &amp; 1</c>)
    /// and <see cref="Flags"/> rewrites that bit, so the sampler has to come
    /// here instead.
    /// </summary>
    public ushort RawFlags(int index) =>
        BinaryPrimitives.ReadUInt16LittleEndian(Record(index)[0x2e..]);

    /// <summary>
    /// The bit the voxelizer stops counting as occupancy once a box is small,
    /// which is what <c>18002e310</c>'s mask switch is: <c>0x811</c> for a box
    /// wider than <see cref="FineBoxSize"/> and <c>0x1811</c> at or below it.
    ///
    /// <para>The other three bits in that mask are inert by the time the trace
    /// scene holds a triangle at all, because the converter has already dropped
    /// <c>0x801</c> and rewritten <c>0x10</c> into <see cref="NoDraw"/>. This one
    /// is the whole of the rule, and it is not academic: ze_hold_em_p has none of
    /// these triangles and the two probe maps have eight each, which is the whole
    /// of the gap between -0.55% and +43% on the node count.</para>
    /// </summary>
    public const ushort CoarseOccupancyOnly = 0x1000;

    /// <summary>Box side at or below which <see cref="CoarseOccupancyOnly"/> applies.</summary>
    public const float FineBoxSize = 256f;

    /// <summary>The surface index the record carries in slot 4.</summary>
    public float SurfaceIndex(int index) => Float(_triangleAt + index * 48 + 16);

    /// <summary>The triangle's plane, as <c>dot(normal, point) == distance</c>.</summary>
    public (Vector3 Normal, float Distance) Plane(int index)
    {
        var at = _triangleAt + index * 48;
        return (new Vector3(Float(at), Float(at + 4), Float(at + 8)), Float(at + 12));
    }

    /// <summary>
    /// The triangle's three world vertices, or null when a component comes out
    /// infinite. This is <c>180022030</c>, Valve's own reconstruction, and the
    /// order is theirs: the corners of the two edge equations in the order
    /// (0,1), (0,0), (1,0).
    ///
    /// <para>The record stores no vertices. It stores the plane, two edge
    /// equations over a 2D projection, and the two axes that projection uses, so
    /// a vertex is a 2x2 solve followed by recovering the third coordinate from
    /// the plane. Rebuilding a whole file this way reproduces the bounding box the
    /// header states, which is the check that settles the decode.</para>
    /// </summary>
    public Vector3[]? Vertices(int index)
    {
        var record = Record(index);
        int u = record[0x2c], v = record[0x2d];
        if (u > 2 || v > 2 || u == v)
            return null;
        var w = (v + 1) % 3;

        Span<float> t = stackalloc float[11];
        for (var i = 0; i < 11; i++)
            t[i] = Float(_triangleAt + index * 48 + i * 4);

        // The normal is plainly slots 0 to 2 in xyz order and the axis bytes name
        // only the projection. Reading it in any other order scores well on a map
        // that is mostly axis aligned and is wrong everywhere else.
        Span<float> normal = [t[0], t[1], t[2]];
        float a0 = t[5], b0 = t[6], c0 = t[7], a1 = t[8], b1 = t[9], c1 = t[10];
        var scale = 1f / ((b1 * a0) - (b0 * a1));

        Span<float> corner = stackalloc float[3];
        var found = new Vector3[3];
        ReadOnlySpan<(float AlongU, float AlongV)> solved =
        [
            (((c1 - 1f) * b0) - (b1 * c0), (a1 * c0) - ((c1 - 1f) * a0)),
            ((b0 * c1) - (b1 * c0), (a1 * c0) - (a0 * c1)),
            ((b0 * c1) - ((c0 - 1f) * b1), ((c0 - 1f) * a1) - (a0 * c1)),
        ];
        for (var i = 0; i < 3; i++)
        {
            corner.Clear();
            corner[u] = solved[i].AlongU * scale;
            corner[v] = solved[i].AlongV * scale;
            var onPlane = (normal[0] * corner[0]) + (normal[1] * corner[1]) + (normal[2] * corner[2]);
            corner[w] -= (onPlane - t[3]) / normal[w];
            if (!float.IsFinite(corner[0]) || !float.IsFinite(corner[1]) || !float.IsFinite(corner[2]))
                return null;
            found[i] = new Vector3(corner[0], corner[1], corner[2]);
        }
        return found;
    }

    /// <summary>What a ray found, or nothing when it left the scene.</summary>
    /// <param name="Distance">How far along the ray, in world units.</param>
    /// <param name="Triangle">Which triangle it landed on.</param>
    /// <param name="Normal">That triangle's plane normal.</param>
    /// <param name="PlaneDistance">Its plane distance, as <c>dot(n, p) == d</c>.</param>
    public readonly record struct Hit(float Distance, int Triangle, Vector3 Normal, float PlaneDistance);

    /// <summary>
    /// The nearest triangle a ray meets, ignoring any carrying a bit of
    /// <paramref name="ignore"/>. The traversal is the file's own kd tree, which
    /// is what makes this affordable: ze_hold_em_p's 4,548 triangles sit in 933
    /// leaves of at most ten each.
    ///
    /// <para>A leaf's triangles are tested over the WHOLE ray, not over the
    /// leaf's own slice of it, and the walk does not stop when a node starts
    /// beyond the best hit so far. Both are the usual kd shortcuts and both are
    /// wrong on this file: its 4,548 triangles take only 5,983 index slots, so a
    /// triangle is filed in about 1.3 leaves rather than in every leaf it
    /// overlaps, and a large one is routinely met at a distance outside the slice
    /// of the leaf that holds it. Clipping to the slice threw those hits away and
    /// the ray ran on to something far behind: on ze_hold_em_p it disagreed with
    /// a scan of every triangle on 1,698 of 6,300 rays, every one of them landing
    /// FARTHER than the truth, by up to 1,750 units. It now agrees on all
    /// 6,300.</para>
    /// </summary>
    public Hit? Trace(Vector3 origin, Vector3 direction, float reach, ushort ignore = ExcludedFromTrace)
        => Walk(origin, direction, reach, ignore,
                (triangle, best) => Meets(triangle, origin, direction, 0f, best ?? reach));

    /// <summary>
    /// A segment traced the way the compile's batch tracer does it, which is what
    /// cluster sampling casts through (<c>BatchRay</c>, <c>FlushBatch</c> and the
    /// packet trace they drive, with the batch's mode word left at 0).
    ///
    /// <para>Four things separate it from <see cref="Trace"/>, and each decides
    /// rays that <see cref="Trace"/> answers differently. The direction is the
    /// segment renormalised with <c>rcpps</c> and one Newton step, not the unit
    /// vector the caller built the end from, so it runs on the CPU's own
    /// reciprocal estimate. A hit needs <c>0 &lt; t</c>, strictly, so a ray whose
    /// origin lies ON a triangle's plane does not stop there: that is every ray
    /// of a cluster centred on a wall. The plane must be more than 1e-10 off
    /// parallel. And every dot product is summed in the binary's order.</para>
    /// </summary>
    /// <returns>The hit, its distance measured along the renormalised direction,
    /// or null when nothing is met before <paramref name="end"/>.</returns>
    public Hit? Segment(Vector3 origin, Vector3 end, ushort ignore)
    {
        var delta = new Vector3(end.X - origin.X, end.Y - origin.Y, end.Z - origin.Z);
        var length = MathF.Sqrt((delta.Z * delta.Z) + (delta.Y * delta.Y) + (delta.X * delta.X));
        var guarded = MathF.Abs(length) < 1.17549435e-38f
            ? BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(length)
                                             | BitConverter.SingleToInt32Bits(1.1920929e-07f))
            : length;
        var scale = Refined(guarded);
        var direction = new Vector3(delta.X * scale, delta.Y * scale, delta.Z * scale);

        var hit = Nearest(origin, direction, ignore);
        return hit is { } h && length < h.Distance ? null : hit;
    }

    private Bvh? _bvh;

    /// <summary>
    /// The nearest traced triangle along a ray, over every triangle, which is
    /// what the compile's rebuilt kd tree answers and the file's own tree does
    /// not: the file files a triangle in about 1.3 leaves, so a walk through it
    /// misses hits a complete structure finds. Two triangles met at the very
    /// same distance go to the lower index, the one place this may differ from
    /// the compile's walk order.
    /// </summary>
    private Hit? Nearest(Vector3 origin, Vector3 direction, ushort ignore)
    {
        var bvh = _bvh ??= new Bvh(this);
        Hit? best = null;
        var inverse = new Vector3(1f / direction.X, 1f / direction.Y, 1f / direction.Z);
        Span<int> stack = stackalloc int[128];
        var top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            var node = stack[--top];
            if (!bvh.Enters(node, origin, inverse, best?.Distance ?? float.MaxValue))
                continue;
            var (first, count, left) = bvh.Node(node);
            if (count == 0)
            {
                stack[top++] = left;
                stack[top++] = left + 1;
                continue;
            }
            for (var k = first; k < first + count; k++)
            {
                var triangle = bvh.Triangles[k];
                if ((Flags(triangle) & ignore) != 0)
                    continue;
                var limit = best?.Distance ?? float.MaxValue;
                if (Accepts(triangle, origin, direction, float.MaxValue) is not { } hit || hit.Distance > limit)
                    continue;
                if (best is null || hit.Distance < limit || triangle < best.Value.Triangle)
                    best = hit;
            }
        }
        return best;
    }

    /// <summary>
    /// Every triangle a <see cref="Segment"/> meets at its nearest distance, for
    /// finding the ties <see cref="Nearest"/> settles by index. Diagnostic only.
    /// </summary>
    internal List<Hit> SegmentTies(Vector3 origin, Vector3 end, ushort ignore)
    {
        var tied = new List<Hit>();
        if (Segment(origin, end, ignore) is not { } nearest)
            return tied;
        var delta = new Vector3(end.X - origin.X, end.Y - origin.Y, end.Z - origin.Z);
        var length = MathF.Sqrt((delta.Z * delta.Z) + (delta.Y * delta.Y) + (delta.X * delta.X));
        var scale = Refined(MathF.Abs(length) < 1.17549435e-38f
            ? BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(length) | BitConverter.SingleToInt32Bits(1.1920929e-07f))
            : length);
        var direction = new Vector3(delta.X * scale, delta.Y * scale, delta.Z * scale);
        var traced = Traced();
        for (var i = 0; i < TriangleCount; i++)
        {
            if (float.IsNaN(traced[i * 13]) || (Flags(i) & ignore) != 0)
                continue;
            if (Accepts(i, origin, direction, float.MaxValue) is { } hit && hit.Distance == nearest.Distance)
                tied.Add(hit);
        }
        return tied;
    }

    /// <summary>
    /// The packet test's intermediate values for one segment against one
    /// triangle: t, the projected point and the two edge values. Diagnostic only.
    /// </summary>
    internal (float T, float U, float V, float First, float Second, float Denom)? SegmentDetail(Vector3 origin, Vector3 end, int index)
    {
        var delta = new Vector3(end.X - origin.X, end.Y - origin.Y, end.Z - origin.Z);
        var length = MathF.Sqrt((delta.Z * delta.Z) + (delta.Y * delta.Y) + (delta.X * delta.X));
        var scale = Refined(length);
        var d = new Vector3(delta.X * scale, delta.Y * scale, delta.Z * scale);
        var r = Traced().AsSpan(index * 13, 13);
        if (float.IsNaN(r[0]))
            return null;
        var denom = (d.Z * r[2]) + (d.Y * r[1]) + (d.X * r[0]);
        var t = (r[3] - ((origin.Z * r[2]) + (origin.Y * r[1]) + (r[0] * origin.X))) / denom;
        int u = (int)r[11], v = (int)r[12];
        var pu = (t * Axis(d, u)) + Axis(origin, u);
        var pv = (t * Axis(d, v)) + Axis(origin, v);
        return (t, pu, pv, (r[5] * pu) + (r[6] * pv) + r[7], (r[8] * pu) + (r[9] * pv) + r[10], denom);
    }

    /// <summary>A plain bounding volume hierarchy over the traced triangles' corners.</summary>
    private sealed class Bvh
    {
        private readonly float[] _box;
        private readonly int[] _node;

        public int[] Triangles { get; }

        public Bvh(RayTraceEnvironment rte)
        {
            var traced = rte.Traced();
            var ids = new List<int>();
            var boxes = new List<(Vector3 Lo, Vector3 Hi)>();
            Span<float> p = stackalloc float[9];
            for (var i = 0; i < rte.TriangleCount; i++)
            {
                var r = traced.AsSpan(i * 13, 13);
                if (float.IsNaN(r[0]) || !Corners(r, (int)r[11], (int)r[12], p))
                    continue;
                var lo = Vector3.Min(Vector3.Min(new(p[0], p[1], p[2]), new(p[3], p[4], p[5])), new(p[6], p[7], p[8]));
                var hi = Vector3.Max(Vector3.Max(new(p[0], p[1], p[2]), new(p[3], p[4], p[5])), new(p[6], p[7], p[8]));
                // Conservative: the box only has to contain every point the test
                // can accept, and the test runs on rounded arithmetic.
                var pad = new Vector3(0.01f) + (Vector3.Max(Vector3.Abs(lo), Vector3.Abs(hi)) * 1e-5f);
                ids.Add(i);
                boxes.Add((lo - pad, hi + pad));
            }
            var order = Enumerable.Range(0, ids.Count).ToArray();
            var nodes = new List<(Vector3 Lo, Vector3 Hi, int First, int Count, int Left)>();
            Build(order, 0, order.Length, boxes, nodes);
            Triangles = [.. order.Select(k => ids[k])];
            _box = new float[nodes.Count * 6];
            _node = new int[nodes.Count * 3];
            for (var n = 0; n < nodes.Count; n++)
            {
                var (lo, hi, first, count, left) = nodes[n];
                (_box[n * 6], _box[(n * 6) + 1], _box[(n * 6) + 2]) = (lo.X, lo.Y, lo.Z);
                (_box[(n * 6) + 3], _box[(n * 6) + 4], _box[(n * 6) + 5]) = (hi.X, hi.Y, hi.Z);
                (_node[n * 3], _node[(n * 3) + 1], _node[(n * 3) + 2]) = (first, count, left);
            }
        }

        private static int Build(int[] order, int from, int to, List<(Vector3 Lo, Vector3 Hi)> boxes,
                                 List<(Vector3, Vector3, int, int, int)> nodes)
        {
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            for (var k = from; k < to; k++)
            {
                lo = Vector3.Min(lo, boxes[order[k]].Lo);
                hi = Vector3.Max(hi, boxes[order[k]].Hi);
            }
            var at = nodes.Count;
            nodes.Add((lo, hi, from, to - from, -1));
            if (to - from <= 4)
                return at;
            var extent = hi - lo;
            var axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
            float Centre(int k) => axis == 0 ? boxes[k].Lo.X + boxes[k].Hi.X
                                 : axis == 1 ? boxes[k].Lo.Y + boxes[k].Hi.Y : boxes[k].Lo.Z + boxes[k].Hi.Z;
            Array.Sort(order, from, to - from, Comparer<int>.Create((a, b) => Centre(a).CompareTo(Centre(b))));
            var mid = (from + to) / 2;
            var left = nodes.Count;
            nodes.Add(default);
            nodes.Add(default);
            nodes[left] = nodes[Build(order, from, mid, boxes, nodes)];
            // Children must sit side by side, so each subtree is built and then
            // its root copied into the reserved pair.
            nodes[left + 1] = nodes[Build(order, mid, to, boxes, nodes)];
            nodes[at] = (lo, hi, from, 0, left);
            return at;
        }

        public (int First, int Count, int Left) Node(int n) => (_node[n * 3], _node[(n * 3) + 1], _node[(n * 3) + 2]);

        public bool Enters(int n, Vector3 o, Vector3 inverse, float limit)
        {
            float enter = 0f, leave = limit;
            for (var axis = 0; axis < 3; axis++)
            {
                var oa = axis == 0 ? o.X : axis == 1 ? o.Y : o.Z;
                var ia = axis == 0 ? inverse.X : axis == 1 ? inverse.Y : inverse.Z;
                var a = (_box[(n * 6) + axis] - oa) * ia;
                var b = (_box[(n * 6) + 3 + axis] - oa) * ia;
                if (float.IsNaN(a) || float.IsNaN(b))
                {
                    if (oa < _box[(n * 6) + axis] || oa > _box[(n * 6) + 3 + axis])
                        return false;
                    continue;
                }
                if (a > b)
                    (a, b) = (b, a);
                enter = MathF.Max(enter, a);
                leave = MathF.Min(leave, b);
            }
            return enter <= leave * 1.0001f + 1e-3f;
        }
    }

    /// <summary>Every triangle tested, no tree: what <see cref="Segment"/> would find if the walk were perfect.</summary>
    internal Hit? SegmentBruteForce(Vector3 origin, Vector3 end, ushort ignore)
    {
        var delta = new Vector3(end.X - origin.X, end.Y - origin.Y, end.Z - origin.Z);
        var length = MathF.Sqrt((delta.Z * delta.Z) + (delta.Y * delta.Y) + (delta.X * delta.X));
        var scale = Refined(length);
        var direction = new Vector3(delta.X * scale, delta.Y * scale, delta.Z * scale);
        Hit? best = null;
        for (var i = 0; i < TriangleCount; i++)
            if ((Flags(i) & ignore) == 0 && Accepts(i, origin, direction, best?.Distance ?? float.MaxValue) is { } h)
                best = h;
        return best is { } b && length < b.Distance ? null : best;
    }

    /// <summary>
    /// <c>rcpps</c> and one Newton step, <c>(r + r) - (r * r) * x</c>, which is
    /// how the packet trace divides. The estimate is the hardware's, so this is
    /// exact only on the machine the compile ran on, which is the one scored.
    /// </summary>
    public static float Refined(float x)
    {
        var r = Sse.IsSupported
            ? Sse.ReciprocalScalar(
                Vector128.CreateScalarUnsafe(x)).ToScalar()
            : 1f / x;
        return (r + r) - (r * r * x);
    }

    private float[]? _traced;
    private float[]? _loaderCorners;
    private int[]? _tracerOrder;
    private Vector3 _tracedMins, _tracedMaxs;

    /// <summary>
    /// The tracer's triangles in its own slot order, as file indices: every
    /// traced triangle whose loader corners are finite, in file order, except
    /// that the setup drops a triangle whose conversion is not finite by moving
    /// the LAST one into its slot and looking at that slot again.
    /// </summary>
    internal int[] TracerOrder
    {
        get
        {
            Traced();
            return _tracerOrder!;
        }
    }

    /// <summary>
    /// A file triangle's record as the tracer holds it: normal, plane, the two
    /// edge equations in slots 5 to 10, the projection axes in 11 and 12. NaN in
    /// slot 0 for one it does not hold.
    /// </summary>
    internal ReadOnlySpan<float> TracedRecord(int index) => Traced().AsSpan(index * 13, 13);

    private TracerKd? _kd;

    /// <summary>A triangle's 48 byte record as the file holds it.</summary>
    internal ReadOnlySpan<byte> FileRecord(int index) => Record(index);

    /// <summary>
    /// The record the compile's tracer derives from three corners
    /// (<c>FUN_180118e90</c>): normal, plane, the two edge equations in slots
    /// 5 to 10, the projection axes in 11 and 12. False when degenerate.
    /// </summary>
    internal static bool RecordFromCorners(ReadOnlySpan<float> corners, Span<float> record) => Convert(corners, record);

    /// <summary>
    /// Segments traced the way the compile's batch tracer traces them, which is
    /// what decides ties and hair-thin cracks where <see cref="Segment"/> alone
    /// cannot. <c>BatchRay</c> files each segment by the signs of its delta
    /// (x, y, z negative adding 1, 2, 4) and traces a bucket as a packet once it
    /// holds four; <c>FlushBatch</c> traces what is left, a short packet padded
    /// with copies of its first ray. See <see cref="TracerKd.Packet"/>.
    /// </summary>
    public Hit?[] Segments(IReadOnlyList<(Vector3 From, Vector3 To)> segments, ushort ignore)
    {
        var kd = _kd ??= new TracerKd(this);
        var hits = new Hit?[segments.Count];
        var buckets = new List<int>[8];
        for (var b = 0; b < 8; b++)
            buckets[b] = new List<int>(4);
        for (var i = 0; i < segments.Count; i++)
        {
            var (from, to) = segments[i];
            var octant = ((to.X - from.X) < 0f ? 1 : 0) | ((to.Y - from.Y) < 0f ? 2 : 0) | ((to.Z - from.Z) < 0f ? 4 : 0);
            buckets[octant].Add(i);
            if (buckets[octant].Count == 4)
            {
                kd.Packet(segments, buckets[octant], octant, ignore, hits);
                buckets[octant].Clear();
            }
        }
        for (var b = 0; b < 8; b++)
        {
            if (buckets[b].Count > 0)
                kd.Packet(segments, buckets[b], b, ignore, hits);
        }
        return hits;
    }

    /// <summary>The loader's rebuilt corners of a file triangle, nine floats, which the kd build reads.</summary>
    internal ReadOnlySpan<float> LoaderCorners(int index)
    {
        Traced();
        return _loaderCorners.AsSpan(index * 9, 9);
    }

    /// <summary>
    /// The tracer's own bounds, which the merge passes lay their grid over: the
    /// loader's rebuilt corners of every triangle it kept, rounding and all, so
    /// they differ from <see cref="Mins"/> in the last bits. On probe01 the
    /// floor comes out at -2.6e-5 rather than 0, and at a 512 cell that is a
    /// whole cell lower.
    /// </summary>
    public (Vector3 Mins, Vector3 Maxs) TracedBounds
    {
        get
        {
            Traced();
            return (_tracedMins, _tracedMaxs);
        }
    }

    /// <summary>
    /// The triangles as the compile's tracer holds them, which is NOT the file's
    /// records. <c>CVisibilityMesh::LoadRTEFromFile</c> rebuilds each traced
    /// triangle's three corners from the file (<c>FUN_1800233f0</c>), hands them
    /// to a fresh environment, and that environment's setup derives normal,
    /// plane, projection axes and edge equations from the corners again
    /// (<c>FUN_180118e90</c>). The round trip changes low bits: a wall the file
    /// stores with normal x 0.99999994 comes back as exactly 1. A triangle
    /// whose corners are not finite either way is dropped. Thirteen floats a
    /// triangle, the last two the axes; NaN in slot 0 marks one not traced.
    /// </summary>
    private float[] Traced()
    {
        if (_traced is { } done)
            return done;
        var found = new float[TriangleCount * 13];
        var loader = new float[TriangleCount * 9];
        var added = new List<int>();
        Span<float> v = stackalloc float[9];
        Span<float> c = stackalloc float[13];
        Span<float> lo = [float.MaxValue, float.MaxValue, float.MaxValue];
        Span<float> hi = [-float.MaxValue, -float.MaxValue, -float.MaxValue];
        Span<float> corners = stackalloc float[9];
        for (var i = 0; i < TriangleCount; i++)
        {
            found[i * 13] = float.NaN;
            if ((Flags(i) & 0x0801) != 0)
                continue;
            for (var k = 0; k < 11; k++)
                c[k] = Float(_triangleAt + (i * 48) + (k * 4));
            var record = Record(i);
            if (!Corners(c, record[0x2c], record[0x2d], v))
                continue;
            v.CopyTo(corners);
            v.CopyTo(loader.AsSpan(i * 9, 9));
            added.Add(i);
            if (!Convert(v, c) || !Corners(c, (int)c[11], (int)c[12], v))
                continue;
            c.CopyTo(found.AsSpan(i * 13, 13));
            // The setup's running bounds, v0 then v1 then v2, each a <= swap.
            for (var k = 0; k < 9; k++)
            {
                if (corners[k] <= lo[k % 3])
                    lo[k % 3] = corners[k];
                if (hi[k % 3] <= corners[k])
                    hi[k % 3] = corners[k];
            }
        }
        // The setup's pass over the added triangles: one whose conversion fails
        // takes the last one's slot and the slot is looked at again.
        for (var slot = 0; slot < added.Count; slot++)
        {
            if (!float.IsNaN(found[added[slot] * 13]))
                continue;
            added[slot] = added[^1];
            added.RemoveAt(added.Count - 1);
            slot--;
        }
        _tracerOrder = [.. added];
        _loaderCorners = loader;
        _tracedMins = new Vector3(lo[0], lo[1], lo[2]);
        _tracedMaxs = new Vector3(hi[0], hi[1], hi[2]);
        return _traced = found;
    }

    /// <summary>
    /// A traced triangle's corners as <c>BoxOverlap</c> sees them: rebuilt from
    /// the tracer's converted record, which is a second round trip on top of
    /// the loader's and not the same corners as <see cref="Vertices"/>. Null
    /// for a triangle the tracer does not hold.
    /// </summary>
    public Vector3[]? TracedCorners(int index)
    {
        var r = Traced().AsSpan(index * 13, 13);
        if (float.IsNaN(r[0]))
            return null;
        Span<float> p = stackalloc float[9];
        return Corners(r, (int)r[11], (int)r[12], p)
            ? [new(p[0], p[1], p[2]), new(p[3], p[4], p[5]), new(p[6], p[7], p[8])]
            : null;
    }

    // FUN_1800233f0: the three corners from a record's normal, plane and edges.
    private static bool Corners(ReadOnlySpan<float> r, int u, int v, Span<float> p)
    {
        if (u > 2 || v > 2)
            return false;
        var w = (v + 1) % 3;
        var scale = 1f / ((r[9] * r[5]) - (r[6] * r[8]));
        p.Clear();
        p[u] = (((r[10] - 1f) * r[6]) - (r[9] * r[7])) * scale;
        p[v] = ((r[8] * r[7]) - ((r[10] - 1f) * r[5])) * scale;
        p[3 + u] = ((r[6] * r[10]) - (r[9] * r[7])) * scale;
        p[3 + v] = ((r[8] * r[7]) - (r[5] * r[10])) * scale;
        p[6 + u] = ((r[6] * r[10]) - ((r[7] - 1f) * r[9])) * scale;
        p[6 + v] = (((r[7] - 1f) * r[8]) - (r[5] * r[10])) * scale;
        for (var k = 0; k < 9; k += 3)
        {
            var dot = (r[2] * p[k + 2]) + (r[1] * p[k + 1]) + (r[0] * p[k]);
            p[k + w] -= (dot - r[3]) / r[w];
        }
        foreach (var x in p)
            if (!float.IsFinite(x))
                return false;
        return true;
    }

    // FUN_180118e90: normal, plane, axes and the two edge equations from corners.
    private static bool Convert(ReadOnlySpan<float> p, Span<float> r)
    {
        float x0 = p[0], y0 = p[1], z0 = p[2], x1 = p[3], y1 = p[4], z1 = p[5];
        float x2 = p[6], y2 = p[7], z2 = p[8];
        var dy2 = y2 - y0;
        var nx = ((y1 - y0) * (z2 - z0)) - ((z1 - z0) * dy2);
        var nz = ((x1 - x0) * dy2) - ((y1 - y0) * (x2 - x0));
        var ny = ((z1 - z0) * (x2 - x0)) - ((x1 - x0) * (z2 - z0));
        var length = MathF.Sqrt((nz * nz) + (ny * ny) + (nx * nx));
        if (length < 1e-17f || length > 1e17f)
        {
            if (length == 0f)
                return false;
            var l = Math.Sqrt(((double)nx * nx) + ((double)ny * ny) + ((double)nz * nz));
            (nx, ny, nz) = ((float)(nx / l), (float)(ny / l), (float)(nz / l));
        }
        else
        {
            var inverse = 1f / length;
            (nx, ny, nz) = (inverse * nx, inverse * ny, inverse * nz);
        }
        r[0] = nx;
        r[1] = ny;
        r[2] = nz;
        r[3] = (z0 * nz) + (y0 * ny) + (x0 * nx);

        ReadOnlySpan<float> n = [MathF.Abs(nx), MathF.Abs(ny), MathF.Abs(nz)];
        var major = n[1] > n[0] ? 1 : 0;
        if (n[2] > n[major])
            major = 2;
        int u = (major + 1) % 3, v = (major + 2) % 3;
        r[11] = u;
        r[12] = v;

        var a = p[v] - p[3 + v];
        var b = p[3 + u] - p[u];
        var c0 = (p[u] * a) + (p[v] * b);
        var c = -c0;
        var s = (b * p[6 + v]) + (a * p[6 + u]) + c;
        if (s < 0f)
            (a, b, s, c) = (-a, -b, -s, c0);
        r[5] = a / s;
        r[6] = b / s;
        r[7] = c / s;

        a = p[3 + v] - p[6 + v];
        b = p[6 + u] - p[3 + u];
        var c1 = (p[3 + v] * b) + (p[3 + u] * a);
        c = -c1;
        s = (b * p[v]) + (a * p[u]) + c;
        if (s < 0f)
            (a, b, s, c) = (-a, -b, -s, c1);
        r[8] = a / s;
        r[9] = b / s;
        r[10] = c / s;
        return true;
    }

    /// <summary>The packet trace's triangle test, in its own order of operations.</summary>
    private Hit? Accepts(int index, Vector3 o, Vector3 d, float best)
    {
        var r = Traced().AsSpan(index * 13, 13);
        if (float.IsNaN(r[0]))
            return null;
        float nx = r[0], ny = r[1], nz = r[2], plane = r[3];
        var denom = (d.Z * nz) + (d.Y * ny) + (d.X * nx);
        var t = (plane - ((o.Z * nz) + (o.Y * ny) + (nx * o.X))) / denom;
        if (!((denom > 1e-10f || denom < -1e-10f) && 0f < t && t < best))
            return null;

        int u = (int)r[11], v = (int)r[12];
        var pu = (t * Axis(d, u)) + Axis(o, u);
        var pv = (t * Axis(d, v)) + Axis(o, v);
        var first = (r[5] * pu) + (r[6] * pv) + r[7];
        var second = (r[8] * pu) + (r[9] * pv) + r[10];
        return 0f <= first && 0f <= second && second + first <= 1f
            ? new Hit(t, index, new Vector3(nx, ny, nz), plane)
            : null;
    }

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private Hit? Walk(Vector3 origin, Vector3 direction, float reach, ushort ignore,
                      Func<int, float?, Hit?> test)
    {
        var (enter, leave) = Slab(origin, direction, reach);
        if (enter > leave)
            return null;

        Hit? best = null;
        var stack = new Stack<(int Node, float Enter, float Leave)>();
        stack.Push((0, enter, leave));
        while (stack.Count > 0)
        {
            var (node, from, to) = stack.Pop();
            var word = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(HeaderSize + (node * 8)));
            var axis = (int)(word & 3);
            var payload = (int)(word >> 2);
            if (axis == 3)
            {
                var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                    _data.AsSpan(HeaderSize + (node * 8) + 4));
                for (var i = 0; i < count; i++)
                {
                    var triangle = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                        _data.AsSpan(_indexAt + ((payload + i) * 4)));
                    if ((Flags(triangle) & ignore) != 0)
                        continue;
                    if (test(triangle, best?.Distance) is { } hit
                        && (best is null || hit.Distance < best.Value.Distance))
                        best = hit;
                }
                continue;
            }

            var split = Float(HeaderSize + (node * 8) + 4);
            var along = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            var start = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;

            // A child is a pair, low side first, which is the order 18004c400
            // walks when it decides a box sits wholly above the split.
            var (near, far) = along >= 0f ? (payload, payload + 1) : (payload + 1, payload);
            if (along == 0f)
            {
                stack.Push((start <= split ? payload : payload + 1, from, to));
                continue;
            }

            var at = (split - start) / along;
            if (at >= to)
                stack.Push((near, from, to));
            else if (at <= from)
                stack.Push((far, from, to));
            else
            {
                stack.Push((far, at, to));
                stack.Push((near, from, at));
            }
        }
        return best;
    }

    /// <summary>
    /// Every traced triangle whose own box reaches into the given one, found
    /// through the kd tree the way <c>18004bf20</c> does.
    ///
    /// <para>It exists so a caller with a small box and a great many rays can pay
    /// for the tree once and then test a handful of triangles per ray. Cluster
    /// sampling casts thousands of rays inside one 72 unit box, and walking the
    /// tree for each of them costs more than the triangles do.</para>
    /// </summary>
    public int[] Overlapping(Vector3 mins, Vector3 maxs, ushort ignore = ExcludedFromTrace)
    {
        var found = new List<int>();
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            var word = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(HeaderSize + (node * 8)));
            var axis = (int)(word & 3);
            var payload = (int)(word >> 2);
            if (axis == 3)
            {
                var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                    _data.AsSpan(HeaderSize + (node * 8) + 4));
                for (var i = 0; i < count; i++)
                {
                    var triangle = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                        _data.AsSpan(_indexAt + ((payload + i) * 4)));
                    if ((Flags(triangle) & ignore) != 0 || found.Contains(triangle))
                        continue;
                    if (Vertices(triangle) is not { } corners)
                        continue;
                    var lo = Vector3.Min(Vector3.Min(corners[0], corners[1]), corners[2]);
                    var hi = Vector3.Max(Vector3.Max(corners[0], corners[1]), corners[2]);
                    if (lo.X <= maxs.X && hi.X >= mins.X && lo.Y <= maxs.Y && hi.Y >= mins.Y
                        && lo.Z <= maxs.Z && hi.Z >= mins.Z)
                        found.Add(triangle);
                }
                continue;
            }

            var split = Float(HeaderSize + (node * 8) + 4);
            var low = axis == 0 ? mins.X : axis == 1 ? mins.Y : mins.Z;
            var high = axis == 0 ? maxs.X : axis == 1 ? maxs.Y : maxs.Z;
            if (low <= split)
                stack.Push(payload);
            if (high >= split)
                stack.Push(payload + 1);
        }
        return [.. found];
    }

    /// <summary>Where a ray enters and leaves the scene's own box.</summary>
    private (float Enter, float Leave) Slab(Vector3 origin, Vector3 direction, float reach)
    {
        float enter = 0f, leave = reach;
        for (var axis = 0; axis < 3; axis++)
        {
            var d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            var o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            var lo = axis == 0 ? Mins.X : axis == 1 ? Mins.Y : Mins.Z;
            var hi = axis == 0 ? Maxs.X : axis == 1 ? Maxs.Y : Maxs.Z;
            if (d == 0f)
            {
                if (o < lo || o > hi)
                    return (1f, 0f);
                continue;
            }
            var first = (lo - o) / d;
            var second = (hi - o) / d;
            if (first > second)
                (first, second) = (second, first);
            enter = MathF.Max(enter, first);
            leave = MathF.Min(leave, second);
        }
        return (enter, leave);
    }

    /// <summary>
    /// Whether a ray meets one triangle, using the record's own encoding rather
    /// than rebuilding it: the plane gives the distance, and the two edge
    /// equations ARE the barycentric pair, so the point is inside when both are
    /// non-negative and they sum to at most one.
    /// </summary>
    public Hit? Meets(int index, Vector3 origin, Vector3 direction, float from, float to)
    {
        var at = _triangleAt + (index * 48);
        var normal = new Vector3(Float(at), Float(at + 4), Float(at + 8));
        var along = Vector3.Dot(normal, direction);
        if (along == 0f)
            return null;

        var distance = Float(at + 12);
        var t = (distance - Vector3.Dot(normal, origin)) / along;
        if (t < from || t > to)
            return null;

        var record = Record(index);
        int u = record[0x2c], v = record[0x2d];
        if (u > 2 || v > 2 || u == v)
            return null;

        var point = origin + (direction * t);
        var alongU = u == 0 ? point.X : u == 1 ? point.Y : point.Z;
        var alongV = v == 0 ? point.X : v == 1 ? point.Y : point.Z;
        var first = (Float(at + 20) * alongU) + (Float(at + 24) * alongV) + Float(at + 28);
        var second = (Float(at + 32) * alongU) + (Float(at + 36) * alongV) + Float(at + 40);
        return first >= 0f && second >= 0f && first + second <= 1f
            ? new Hit(t, index, normal, distance)
            : null;
    }

    private ReadOnlySpan<byte> Record(int index)
        => _data.AsSpan(_triangleAt + index * 48, 48);

    private float Float(int at) => BitConverter.ToSingle(_data, at);

    private Vector3 Vector(int at) => new(Float(at), Float(at + 4), Float(at + 8));
}
