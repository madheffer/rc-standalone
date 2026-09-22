using System.Buffers.Binary;
using System.Numerics;

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
                    if (Meets(triangle, origin, direction, 0f, best?.Distance ?? reach) is { } hit
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
