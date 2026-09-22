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
    /// A triangle the ray tracer excludes. Exactly the twelve of ze_hold_em_p
    /// carrying this flag are the difference between the file's 4,548 triangles and
    /// the compile's own "Convert RTE with 4536 triangles".
    /// </summary>
    public const uint ExcludedFromTrace = 0x0800;

    private RayTraceEnvironment(byte[] data)
    {
        _data = data;
        var nodes = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        TriangleCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        Mins = Vector(0x24);
        Maxs = Vector(0x30);
        _triangleAt = 64 + nodes * 8;
    }

    /// <summary>Read one, or throw when the sections do not tile the file.</summary>
    public static RayTraceEnvironment Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 64)
            throw new InvalidOperationException("too short to hold an .rte header");

        var rte = new RayTraceEnvironment(data);
        var indices = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20)) - 1;
        var total = rte._triangleAt + rte.TriangleCount * 48 + indices * 4
                  + rte.TriangleCount * 8 + rte.TriangleCount * 12;
        return total == data.Length
            ? rte
            : throw new InvalidOperationException(
                $"the .rte sections total {total} bytes and the file is {data.Length}; the layout is wrong for it");
    }

    /// <summary>Read from disk.</summary>
    public static RayTraceEnvironment ReadFile(string path) => Read(File.ReadAllBytes(path));

    /// <summary>Everything above the two axis bytes of the packed word.</summary>
    public uint Flags(int index) => Word(index) >> 16;

    /// <summary>
    /// The triangle's three world vertices, or null when the record is degenerate.
    ///
    /// <para>The record stores no vertices. It stores the plane and two barycentric
    /// edge equations over the plane's 2D projection, so a vertex is where the
    /// barycentric pair reaches (0,0), (1,0) or (0,1). Rebuilding a whole file this
    /// way reproduces the bounding box its own header states, which is the check
    /// that settles the decode.</para>
    /// </summary>
    public Vector3[]? Vertices(int index)
    {
        var word = Word(index);
        int u = (int)(word & 0xFF), v = (int)((word >> 8) & 0xFF);
        if (u > 2 || v > 2 || u == v)
            return null;
        var w = 3 - u - v;

        Span<float> t = stackalloc float[12];
        for (var i = 0; i < 12; i++)
            t[i] = Float(_triangleAt + index * 48 + i * 4);

        // The normal is a FIXED slot mapping and does NOT follow the axis word,
        // which only names the two projection axes. Reading it in axis order scores
        // 100% on an axis-aligned map and quietly corrupts one that is not.
        Span<float> normal = [t[11], t[0], t[1]];

        // The Badouel form scales the normal so its dominant component is 1, and
        // some records leave that component at zero rather than writing it. Those
        // are not degenerate: the plane is the dominant axis at the stored
        // distance. ze_hold_em_p has two, both walls at its own x extremes, and
        // Mako has 10,168 of them, 3.6% of the file.
        if (normal[w] == 0f)
            normal[w] = 1f;

        float a1 = t[4], b1 = t[5], c1 = t[6], a2 = t[7], b2 = t[8], c2 = t[9];
        var det = a1 * b2 - a2 * b1;
        if (det == 0f)
            return null;

        var found = new Vector3[3];
        ReadOnlySpan<(float First, float Second)> corners = [(0f, 0f), (1f, 0f), (0f, 1f)];
        for (var i = 0; i < 3; i++)
        {
            var p1 = corners[i].First - c1;
            var p2 = corners[i].Second - c2;
            var alongU = (p1 * b2 - p2 * b1) / det;
            var alongV = (a1 * p2 - a2 * p1) / det;
            var alongW = (t[2] - normal[u] * alongU - normal[v] * alongV) / normal[w];
            Span<float> point = [0f, 0f, 0f];
            point[u] = alongU;
            point[v] = alongV;
            point[w] = alongW;
            found[i] = new Vector3(point[0], point[1], point[2]);
        }

        // The file states the box its own geometry occupies, so a reconstruction
        // outside it is a decode failure and not a triangle. It matters: one
        // cardtest record whose edge determinant is 2e-6 rebuilds 27 billion units
        // away, and voxelizing it smeared occupancy across the whole root cube.
        foreach (var point in found)
            if (!Inside(point))
                return null;
        return found;
    }

    private bool Inside(Vector3 point)
        => point.X >= Mins.X - 1f && point.X <= Maxs.X + 1f
        && point.Y >= Mins.Y - 1f && point.Y <= Maxs.Y + 1f
        && point.Z >= Mins.Z - 1f && point.Z <= Maxs.Z + 1f;

    private uint Word(int index)
        => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_triangleAt + index * 48 + 40));

    private float Float(int at) => BitConverter.ToSingle(_data, at);

    private Vector3 Vector(int at) => new(Float(at), Float(at + 4), Float(at + 8));
}
