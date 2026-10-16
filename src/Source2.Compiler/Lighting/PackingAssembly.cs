using System.Buffers.Binary;
using System.Numerics;
using Source2.Compiler.Maps;

namespace Source2.Compiler.Lighting;

/// <summary>
/// CStaticLightingProcessor::PackCharts (0923 180214960): the charted meshes,
/// in GenerateCharts' order, concatenated into PackingGeometry_t, then packed
/// (<see cref="LightmapPacker.Pack"/>). Per mesh:
/// <list type="bullet">
/// <item>each vertex: its position through the entry's matrix
/// (Matrix3x4_TransformPoint, 18125d1f0), its normal and its tangent's
/// direction rotated (Matrix3x4_Rotate, 18125d1b0) and normalised, the
/// tangent's sign kept: 0x28 bytes;</item>
/// <item>its chart uvs, each vertex scaled once, by the first triangle that
/// uses it, by that chart's scale (the entry's per-chart list, or 1) times
/// the entry's;</item>
/// <item>its indices after the vertices so far, its triangles' charts after
/// the charts so far (-1 kept), and a zeroed 0x1c record per chart holding its
/// index and flags: bit 30 from the chart mesh's holder, the low 28 bits the
/// entry's chart flags.</item>
/// </list>
/// </summary>
internal static class PackingAssembly
{
    /// <summary>One charted mesh as PackCharts reads it.</summary>
    /// <param name="Stride">The chart mesh's vertex bytes.</param>
    /// <param name="Transform">The entry's matrix (3x4, row major).</param>
    /// <param name="Flags">The chart entry's +0x14.</param>
    /// <param name="Bit30">The holder's +0x18 (chart records' bit 30).</param>
    /// <param name="Scale">The chart entry's +0x18 (1 for a unique mesh).</param>
    /// <param name="ChartScales">The chart entry's per-chart scales (+0x20 count, +0x28), empty for 1 each.</param>
    public sealed record Mesh(LightmapCharts.Result Result, int Stride, int PositionOffset, int NormalOffset, int TangentOffset, float[] Transform,
                              uint Flags, bool Bit30, float Scale, float[] ChartScales);

    public static (PackingGeometry Geometry, float[] LuxelUvs) Assemble(IReadOnlyList<Mesh> meshes)
    {
        const int stride = 0x28;
        var vertices = new List<byte>();
        var uvs = new List<float>();
        var indices = new List<uint>();
        var triangleCharts = new List<uint>();
        var charts = new List<PackingGeometry.Chart>();
        foreach (var m in meshes)
        {
            var r = m.Result;
            var baseVertex = (uint)(vertices.Count / stride);
            var baseChart = charts.Count;
            var record = new byte[stride];
            for (var k = 0; k < r.VertexCount; k++)
            {
                var at = k * m.Stride;
                var p = TransformPoint(m.Transform, Read3(r.Vertices, at + m.PositionOffset));
                var n = Normalize(Rotate(m.Transform, Read3(r.Vertices, at + m.NormalOffset)));
                var t = Normalize(Rotate(m.Transform, Read3(r.Vertices, at + m.TangentOffset)));
                Write3(record, 0, p);
                Write3(record, 0xc, n);
                Write3(record, 0x18, t);
                r.Vertices.AsSpan(at + m.TangentOffset + 12, 4).CopyTo(record.AsSpan(0x24));
                vertices.AddRange(record);
            }
            var luxel = (float[])r.ChartUvs.Clone();
            var seen = new bool[r.VertexCount];
            for (var c = 0; c + 2 < r.Indices.Length; c += 3)
            {
                var chart = r.TriangleCharts[c / 3];
                var f = m.ChartScales.Length == 0 ? 1f : m.ChartScales[chart];
                for (var k = 0; k < 3; k++)
                {
                    var v = r.Indices[c + k];
                    if (seen[v])
                        continue;
                    seen[v] = true;
                    luxel[v * 2] = f * m.Scale * luxel[v * 2];
                    luxel[v * 2 + 1] = f * m.Scale * luxel[v * 2 + 1];
                }
            }
            uvs.AddRange(luxel);
            foreach (var i in r.Indices)
                indices.Add((uint)i + baseVertex);
            foreach (var c in r.TriangleCharts)
                triangleCharts.Add(c == -1 ? uint.MaxValue : (uint)(c + baseChart));
            for (var c = 0; c < r.ChartCount; c++)
                charts.Add(new PackingGeometry.Chart(0, (uint)(baseChart + c), 0, 0, 0, 0, (m.Bit30 ? 0x40000000u : 0u) | (m.Flags & 0xfffffffu)));
        }
        var geometry = new PackingGeometry
        {
            VertexStride = stride, Vertices = [.. vertices], AtlasUv = [.. uvs], Indices = [.. indices], Charts = [.. charts],
            TriangleCharts = [.. triangleCharts], Offsets = [0, 0, 0xc, 0x18, 0, 0],
        };
        return (geometry, [.. uvs]);
    }

    private static Vector3 Read3(byte[] b, int at)
        => new(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 4)),
               BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 8)));

    private static void Write3(byte[] b, int at, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at), v.X);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at + 4), v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at + 8), v.Z);
    }

    // 18125d1f0 (SSE): (m03 + m01 y) + (m02 z + m00 x) per row, w taken as 1.
    internal static Vector3 TransformPoint(float[] m, Vector3 v)
        => new((m[3] + m[1] * v.Y) + (m[2] * v.Z + m[0] * v.X),
               (m[7] + m[5] * v.Y) + (m[6] * v.Z + m[4] * v.X),
               (m[11] + m[9] * v.Y) + (m[10] * v.Z + m[8] * v.X));

    // 18125d1b0 (SSE): m02 z + (m01 y + m00 x) per row.
    internal static Vector3 Rotate(float[] m, Vector3 v)
        => new(m[2] * v.Z + (m[1] * v.Y + m[0] * v.X),
               m[6] * v.Z + (m[5] * v.Y + m[4] * v.X),
               m[10] * v.Z + (m[9] * v.Y + m[8] * v.X));

    // 180214960's VectorNormalize: (y y + z z) + x x, the slow path outside [1e-17, 1e17].
    private static Vector3 Normalize(Vector3 v)
    {
        var length = MathF.Sqrt(v.Y * v.Y + v.Z * v.Z + v.X * v.X);
        if (length < 1e-17f || length > 1e17f)
            return length != 0f ? VectorNormalizeSlow.Normalise(v) : Vector3.Zero;
        var inv = 1f / length;
        return new Vector3(inv * v.X, v.Y * inv, v.Z * inv);
    }
}
