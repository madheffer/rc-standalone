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

    private ReadOnlySpan<byte> Record(int index)
        => _data.AsSpan(_triangleAt + index * 48, 48);

    private float Float(int at) => BitConverter.ToSingle(_data, at);

    private Vector3 Vector(int at) => new(Float(at), Float(at + 4), Float(at + 8));
}
