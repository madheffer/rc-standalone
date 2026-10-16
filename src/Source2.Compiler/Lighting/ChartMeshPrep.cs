using System.Buffers.Binary;
using System.Numerics;
using Source2.Compiler.Maps;

namespace Source2.Compiler.Lighting;

/// <summary>
/// StaticLighting_PrepareChartMesh (0923 180210730) for a unique mesh, as the
/// chart job 180212850 calls it: what <see cref="LightmapCharts"/> is handed.
/// <list type="number">
/// <item>The mesh loses its LightmapUV stream; with the weld switch it is
/// welded at 1/32 (CMesh_Weld, <see cref="Physics.MeshWeld"/>), and each
/// welded vertex remembers the last input vertex that became it.</item>
/// <item>The chart mesh copies the vertices and indices, with the position,
/// texcoord (set 1 when the entry asks for uv2 and the mesh has it), normal
/// and tangent offsets.</item>
/// <item>A RenderBackfaces material (18020d860 bit 2) flips each triangle
/// whose face normal points away from the sum of its vertex normals.</item>
/// <item>Each triangle's resolution scale from the importance volumes
/// (<see cref="ImportanceScales"/>, 18020f910).</item>
/// <item>The corners' neighbours (18020e6a0, 18020efc0) from the welded
/// mesh's own indices: vertices made one by position and, as the switches
/// ask, normal (within 1 degree) and the chart texcoord set (within 2^-11).</item>
/// </list>
/// </summary>
internal static class ChartMeshPrep
{
    /// <summary>The switches GenerateCharts passes down.</summary>
    /// <param name="Weld">GenerateCharts' fourth argument (the weld, on in a CS2 bake).</param>
    /// <param name="SplitByNormal">GenerateCharts' second argument is 0 (no directional_irradiance_sh2_dc output, as in CS2): vertices are also told apart by normal.</param>
    /// <param name="UnwrapUsingUvsFromMesh">gameinfo ResourceCompiler/BakedLighting/UnwrapUsingUvsFromMesh (default true).</param>
    /// <param name="TransitionRegion">gameinfo ImportanceVolumeTransitionRegion (72 by default, 120 in CS2).</param>
    public sealed record Options(bool Weld = true, bool SplitByNormal = true, bool UnwrapUsingUvsFromMesh = true, float TransitionRegion = 120f);

    // cos(1 degree) by tier0's V_cosf (183baf5c4, set at 18004c060).
    private static readonly float NormalDot = BitConverter.Int32BitsToSingle(0x3f7ff605);

    /// <summary>A prepared chart mesh: the charting's input and the normal and tangent byte offsets PackCharts reads.</summary>
    public sealed record Prepared(LightmapCharts.Input Input, int NormalOffset, int TangentOffset);

    /// <summary>The chart mesh for <paramref name="e"/>, ready for <see cref="LightmapCharts.Run"/>.</summary>
    /// <param name="useUv2">The entry's +0x9a.</param>
    /// <param name="renderBackfaces">The material's RenderBackfaces is non-zero.</param>
    /// <param name="volumes">The importance volumes (<see cref="WorldNodeBuild.ImportanceVolumes"/>).</param>
    /// <param name="transform">The entry's matrix (+0x1c8, 3x4 row major).</param>
    public static Prepared Prepare(ChartMeshes.Entry e, Options o, bool useUv2, bool renderBackfaces, IReadOnlyList<Vector3[]> volumes,
                                               float[] transform, float aspect, float scale)
    {
        e = ChartMeshes.Without(e, "LightmapUV");
        var vertices = e.Vertices;
        var indices = e.Indices;
        var count = e.VertexCount;
        var source = new int[count];
        for (var i = 0; i < count; i++)
            source[i] = i;
        if (o.Weld)
        {
            var map = new int[count];
            (vertices, indices) = Physics.MeshWeld.Weld(e.Vertices, e.Stride, e.Indices, e.Streams, 1f / 32f, true, null, null, map);
            source = new int[vertices.Length / e.Stride];
            Array.Fill(source, -1);
            for (var i = 0; i < count; i++)
                if (map[i] != -1)
                    source[map[i]] = i;
        }
        var set = useUv2 && Offset(e.Streams, "texcoord", 1) != -1 ? 1 : 0;
        int position = Offset(e.Streams, "position", 0), texcoord = Offset(e.Streams, "texcoord", set);
        int normal = Offset(e.Streams, "normal", 0), tangent = Offset(e.Streams, "tangent", 0);
        var stride = e.Stride;
        var chartIndices = (int[])indices.Clone();
        if (renderBackfaces)
            FlipAwayFacing(vertices, stride, position, normal, chartIndices);
        var scales = ImportanceScales(vertices, stride, position, chartIndices, volumes, transform, o.TransitionRegion);
        // 18020e6a0's switches: 1 position, 2 normal, 4 texcoord 0, 8 texcoord 1.
        var flags = o.UnwrapUsingUvsFromMesh ? ((set + 1) * 4) | 1 : 1;
        if (o.SplitByNormal)
            flags |= 2;
        var ids = VertexIds(vertices, stride, e.Streams, indices, flags);
        var bytes = new byte[vertices.Length * 4];
        for (var i = 0; i < vertices.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), vertices[i]);
        return new Prepared(new LightmapCharts.Input
        {
            Vertices = bytes, Stride = stride * 4, PositionOffset = position * 4, TexcoordOffset = texcoord * 4,
            Indices = chartIndices, CornerNeighbours = CornerNeighbours(ids), SourceVertex = source,
            TriangleScales = scales, Aspect = aspect, Scale = scale, Ratio = 1.5f, MeshUvs = o.UnwrapUsingUvsFromMesh,
        }, normal * 4, tangent * 4);
    }

    // 1812dedf0: the first float of the stream with that name and semantic
    // index (the n-th of the name), or -1.
    private static int Offset(IReadOnlyList<Physics.MeshWeld.Stream> streams, string name, int index)
    {
        var n = 0;
        foreach (var s in streams)
            if (s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && n++ == index)
                return s.First;
        return -1;
    }

    private static Vector3 At(float[] v, int stride, int vertex, int offset)
        => new(v[vertex * stride + offset], v[vertex * stride + offset + 1], v[vertex * stride + offset + 2]);

    // 180210730's winding test: (p1 - p0) x (p2 - p1) against n1 + n0 + n2.
    private static void FlipAwayFacing(float[] v, int stride, int position, int normal, int[] indices)
    {
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            var (p0, p1, p2) = (At(v, stride, indices[t], position), At(v, stride, indices[t + 1], position), At(v, stride, indices[t + 2], position));
            var (n0, n1, n2) = (At(v, stride, indices[t], normal), At(v, stride, indices[t + 1], normal), At(v, stride, indices[t + 2], normal));
            float e1x = p1.X - p0.X, e1y = p1.Y - p0.Y, e1z = p1.Z - p0.Z;
            float e2x = p2.X - p1.X, e2y = p2.Y - p1.Y, e2z = p2.Z - p1.Z;
            var cz = e2y * e1x - e1y * e2x;
            var cy = e1z * e2x - e1x * e2z;
            var cx = e1y * e2z - e1z * e2y;
            var d = cz * (n1.Z + n0.Z + n2.Z) + cy * (n1.Y + n0.Y + n2.Y) + cx * (n1.X + n0.X + n2.X);
            if (d < 0f)
                (indices[t + 1], indices[t + 2]) = (indices[t + 2], indices[t + 1]);
        }
    }

    /// <summary>
    /// 18020e6a0: each corner's vertex id, vertices made one in a CUtlHashtable
    /// keyed by the position's MurmurHash2: equal position bytes and, by the
    /// switches, normals within 1 degree and texcoords within 2^-11; a vertex
    /// takes the first stored one that matches in the table's probe order, ids
    /// in the order vertices first appear.
    /// </summary>
    internal static int[] VertexIds(float[] v, int stride, IReadOnlyList<Physics.MeshWeld.Stream> streams, int[] indices, int flags)
    {
        int position = Offset(streams, "position", 0), normal = Offset(streams, "normal", 0);
        int uv0 = Offset(streams, "texcoord", 0), uv1 = Offset(streams, "texcoord", 1);
        if (uv0 == -1)
            flags &= ~4;
        if (uv1 == -1)
            flags &= ~8;
        var count = v.Length / stride;
        var table = new UtlHashtable<VertexKey, int>(Math.Max(count, 8));
        var ids = new int[count];
        var next = 0;
        for (var i = 0; i < count; i++)
        {
            var b = i * stride;
            var key = new VertexKey(BitConverter.SingleToInt32Bits(v[b + position]), BitConverter.SingleToInt32Bits(v[b + position + 1]),
                                    BitConverter.SingleToInt32Bits(v[b + position + 2]), flags,
                                    (flags & 2) != 0 ? new Vector3(v[b + normal], v[b + normal + 1], v[b + normal + 2]) : default,
                                    (flags & 4) != 0 ? new Vector2(v[b + uv0], v[b + uv0 + 1]) : default,
                                    (flags & 8) != 0 ? new Vector2(v[b + uv1], v[b + uv1 + 1]) : default);
            var slot = table.FindOrInsert(key, key.Hash(), out var inserted);
            if (inserted)
                table.Value(slot) = next++;
            ids[i] = table.Value(slot);
        }
        return [.. indices.Select(x => ids[x])];
    }

    private readonly record struct VertexKey(int X, int Y, int Z, int Flags, Vector3 Normal, Vector2 Uv0, Vector2 Uv1) : IEquatable<VertexKey>
    {
        private const float UvTolerance = 2.3841858e-07f;

        public uint Hash()
        {
            const uint m = 0x5bd1e995;
            var h = 0x3501a678u;
            foreach (var x in new[] { X, Y, Z })
            {
                var k = (uint)x * m;
                h = ((k ^ (k >> 24)) * m) ^ (h * m);
            }
            h = (h ^ (h >> 13)) * m;
            return h ^ (h >> 15);
        }

        // 18021a9c0: this is the stored key, other the one looked up.
        public bool Equals(VertexKey other)
        {
            if (X != other.X || Y != other.Y || Z != other.Z)
                return false;
            if ((Flags & 2) != 0 && !(NormalDot <= other.Normal.Z * Normal.Z + other.Normal.Y * Normal.Y + other.Normal.X * Normal.X))
                return false;
            if ((Flags & 4) != 0)
            {
                float du = Uv0.X - other.Uv0.X, dv = Uv0.Y - other.Uv0.Y;
                if (!(dv * dv + du * du <= UvTolerance))
                    return false;
            }
            if ((Flags & 8) != 0)
            {
                float du = Uv1.X - other.Uv1.X, dv = Uv1.Y - other.Uv1.Y;
                if (!(dv * dv + du * du <= UvTolerance))
                    return false;
            }
            return true;
        }

        public override int GetHashCode() => (int)Hash();
    }

    /// <summary>
    /// 18020efc0: per corner, the corner across the edge it starts in the
    /// triangle that runs that edge the other way, first come first paired; -1
    /// for an open edge, a third claimant, a repeated directed edge or a
    /// triangle with a repeated vertex id.
    /// </summary>
    internal static int[] CornerNeighbours(int[] ids)
    {
        var n = new int[ids.Length];
        var edges = new Dictionary<(int, int), int>();
        for (var t = 0; t + 2 < ids.Length; t += 3)
        {
            if (ids[t] == ids[t + 1] || ids[t + 1] == ids[t + 2] || ids[t + 2] == ids[t])
            {
                n[t] = n[t + 1] = n[t + 2] = -1;
                continue;
            }
            for (var k = 0; k < 3; k++)
            {
                var corner = t + k;
                int a = ids[corner], b = ids[t + (k < 2 ? k + 1 : 0)];
                if (edges.TryGetValue((b, a), out var other))
                {
                    if (n[other] == -1)
                    {
                        n[other] = corner;
                        n[corner] = other;
                    }
                    else
                        n[corner] = -1;
                    continue;
                }
                edges.TryAdd((a, b), corner);
                n[corner] = -1;
            }
        }
        for (var t = 0; t + 2 < n.Length; t += 3)
        {
            if (n[t] == n[t + 1])
                n[t] = n[t + 1] = -1;
            if (n[t] == n[t + 2])
                n[t] = n[t + 2] = -1;
            if (n[t + 2] == n[t + 1])
                n[t + 2] = n[t + 1] = -1;
        }
        return n;
    }

    /// <summary>
    /// 18020f910: each triangle's resolution scale. No volumes: 1. A volume
    /// within the region of every corner of the mesh's bounds: 1 throughout.
    /// Otherwise the volumes within 100 regions of the bounds are kept (none:
    /// 0.01 throughout), and a triangle takes, over the kept volumes with a
    /// hull point in front of it, the least distance from the volume to its
    /// corners and centroid, stopping once that is within the region: 1
    /// there, else region / distance, at least 0.01.
    /// <para>Valve measures with GJK (181266fc0, 181267a70); these distances
    /// are exact geometry, so a scale strictly between 0.01 and 1 may differ
    /// in its last bits (none on probe01).</para>
    /// </summary>
    internal static float[] ImportanceScales(float[] v, int stride, int position, int[] indices, IReadOnlyList<Vector3[]> volumes, float[] m, float region)
    {
        var triangles = indices.Length / 3;
        var scales = new float[triangles];
        if (volumes.Count == 0)
        {
            Array.Fill(scales, 1f);
            return scales;
        }
        var count = v.Length / stride;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i < count; i++)
        {
            var p = At(v, stride, i, position);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        var box = new Vector3[8];
        for (var c = 0; c < 8; c++)
            box[c] = Transform(m, new Vector3((c & 1) != 0 ? max.X : min.X, (c & 2) != 0 ? max.Y : min.Y, (c & 4) != 0 ? max.Z : min.Z));
        var near = new List<Vector3[]>();
        foreach (var volume in volumes)
        {
            if (box.All(c => ConvexDistance.PointToHull(c, volume) <= region))
            {
                Array.Fill(scales, 1f);
                return scales;
            }
            var d = ConvexDistance.HullToHull(ConvexDistance.Box(box), volume);
            if (d < region || MathF.Max(region / d, 0.01f) > 0.01f)
                near.Add(volume);
        }
        if (near.Count == 0)
        {
            Array.Fill(scales, 0.01f);
            return scales;
        }
        for (var t = 0; t < triangles; t++)
        {
            var p0 = Transform(m, At(v, stride, indices[3 * t], position));
            var p1 = Transform(m, At(v, stride, indices[3 * t + 1], position));
            var p2 = Transform(m, At(v, stride, indices[3 * t + 2], position));
            var c = new Vector3((p1.X + p0.X + p2.X) * 0.33333334f, (p1.Y + p0.Y + p2.Y) * 0.33333334f, (p1.Z + p0.Z + p2.Z) * 0.33333334f);
            var nx = (p2.Z - p0.Z) * (p1.Y - p0.Y) - (p1.Z - p0.Z) * (p2.Y - p0.Y);
            var nz = (p2.Y - p0.Y) * (p1.X - p0.X) - (p1.Y - p0.Y) * (p2.X - p0.X);
            var ny = (p1.Z - p0.Z) * (p2.X - p0.X) - (p2.Z - p0.Z) * (p1.X - p0.X);
            var length = MathF.Sqrt(nz * nz + ny * ny + nx * nx);
            Vector3 n;
            if (length < 1e-17f || length > 1e17f)
                n = length != 0f ? VectorNormalizeSlow.Normalise(new Vector3(nx, ny, nz)) : Vector3.Zero;
            else
            {
                var inv = 1f / length;
                n = new Vector3(inv * nx, inv * ny, inv * nz);
            }
            var best = float.MaxValue;
            foreach (var volume in near)
            {
                var front = false;
                foreach (var q in volume)
                    if (0f < (q.Z - c.Z) * n.Z + (q.Y - c.Y) * n.Y + (q.X - c.X) * n.X)
                    {
                        front = true;
                        break;
                    }
                if (!front)
                    continue;
                foreach (var p in new[] { p0, p1, p2, c })
                    best = MathF.Min(ConvexDistance.PointToHull(p, volume), best);
                if (best <= region)
                    break;
            }
            scales[t] = region <= best ? MathF.Max(region / best, 0.01f) : 1f;
        }
        return scales;
    }

    private static Vector3 Transform(float[] m, Vector3 p)
        => new(m[0] * p.X + m[1] * p.Y + m[2] * p.Z + m[3], m[4] * p.X + m[5] * p.Y + m[6] * p.Z + m[7], m[8] * p.X + m[9] * p.Y + m[10] * p.Z + m[11]);
}
