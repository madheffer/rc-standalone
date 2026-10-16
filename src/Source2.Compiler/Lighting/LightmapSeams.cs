using System.Buffers.Binary;
using System.Numerics;
using Source2.Compiler.Maps;

namespace Source2.Compiler.Lighting;

/// <summary>
/// lightmap_seams.dat, WriteLightmapEdgeSeamsForVrad3 (0923 180213290), from
/// the charted meshes and the packed geometry:
/// <list type="number">
/// <item>The vertices of every mesh are made one across meshes (1802129d0):
/// a record of position, normal and the mesh's tier byte per vertex, visited
/// in order through tier0's CVertexKDTree; a vertex joins the first earlier
/// one the box query returns whose cluster's first vertex has the same tier,
/// lies within 1/32 and has a normal dot of at least 0.99.</item>
/// <item>The corners' neighbours over all triangles by those ids
/// (18020efc0, <see cref="ChartMeshPrep.CornerNeighbours"/>).</item>
/// <item>Each pair of neighbouring triangles, the lower first, both with two
/// edges whose directions are not within 0.999 of parallel, gives a 32-byte
/// record of its shared edge's atlas uvs, this triangle's two corners then
/// the other's matching two, unless it is the same edge of the same mesh and
/// chart or the four uvs agree pairwise exactly.</item>
/// </list>
/// The uvs are the packed ones as BakeLighting (180217740) leaves them after
/// PackCharts, snapped to a 16-bit grid (<see cref="Snap"/>). The meshes'
/// matrices are taken as identity (every unique mesh's).
/// </summary>
internal static class LightmapSeams
{
    /// <summary>
    /// BakeLighting's snap of each chart entry's uvs, both axes alike (the
    /// atlas is square: one requested side): a step of 1 / (2 side
    /// floor(32767 / side)), the side after the mip and MinSpecLightmapSize
    /// halvings, and u = floor(u / step + 0.5) step.
    /// </summary>
    public static float[] Snap(float[] uvs, int side)
    {
        var step = 1f / (float)((int)MathF.Floor(32767f / (float)side) * side * 2);
        var snapped = new float[uvs.Length];
        for (var i = 0; i < uvs.Length; i++)
            snapped[i] = MathF.Floor(uvs[i] / step + 0.5f) * step;
        return snapped;
    }

    /// <param name="uvs">The snapped atlas uvs, per vertex of the meshes in order (<see cref="Snap"/>).</param>
    public static byte[] Write(IReadOnlyList<PackingAssembly.Mesh> meshes, float[] uvs)
    {
        // 1802129d0's records: x, y, z, nx, ny, nz, tier.
        var bases = new int[meshes.Count];
        var total = 0;
        for (var m = 0; m < meshes.Count; m++)
        {
            bases[m] = total;
            total += meshes[m].Result.VertexCount;
        }
        var records = new float[total * 7];
        for (var m = 0; m < meshes.Count; m++)
        {
            var mesh = meshes[m];
            for (var k = 0; k < mesh.Result.VertexCount; k++)
            {
                var p = Read3(mesh.Result.Vertices, k * mesh.Stride + mesh.PositionOffset);
                var n = Read3(mesh.Result.Vertices, k * mesh.Stride + mesh.NormalOffset);
                var r = (bases[m] + k) * 7;
                (records[r], records[r + 1], records[r + 2]) = (p.X, p.Y, p.Z);
                (records[r + 3], records[r + 4], records[r + 5]) = (n.X, n.Y, n.Z);
                records[r + 6] = 0f;
            }
        }
        var ids = WeldAcross(records, total, 1f / 32f, BitConverter.Int32BitsToSingle(0x3f7d70a4));
        var cornerIds = new List<int>();
        var corners = new List<int>();
        var triMesh = new List<int>();
        var triChart = new List<int>();
        for (var m = 0; m < meshes.Count; m++)
        {
            var r = meshes[m].Result;
            for (var c = 0; c < r.Indices.Length; c++)
            {
                cornerIds.Add(ids[bases[m] + r.Indices[c]]);
                corners.Add(r.Indices[c]);
            }
            for (var t = 0; t < r.Indices.Length / 3; t++)
            {
                triMesh.Add(m);
                triChart.Add(r.TriangleCharts[t]);
            }
        }
        var adjacent = ChartMeshPrep.CornerNeighbours([.. cornerIds]);
        var output = new List<byte>();
        Span<float> record = stackalloc float[8];
        for (var c = 0; c + 2 < corners.Count; c += 3)
        {
            var t = c / 3;
            if (!Spread(meshes[triMesh[t]], corners, c))
                continue;
            for (var k = c; k < c + 3; k++)
            {
                var n = adjacent[k];
                if (n == -1)
                    continue;
                var u = n / 3;
                if (t >= u || !Spread(meshes[triMesh[u]], corners, u * 3))
                    continue;
                var kNext = k - c != 2 ? k + 1 : k - 2;
                var nNext = n % 3 != 2 ? n + 1 : n - 2;
                if (triMesh[t] == triMesh[u] && triChart[t] == triChart[u] && corners[k] == corners[nNext] && corners[kNext] == corners[n])
                    continue;
                Vector2 Uv(int mesh, int vertex) => new(uvs[(bases[mesh] + vertex) * 2], uvs[(bases[mesh] + vertex) * 2 + 1]);
                var (a, b) = (Uv(triMesh[t], corners[k]), Uv(triMesh[t], corners[kNext]));
                var (a2, b2) = (Uv(triMesh[u], corners[nNext]), Uv(triMesh[u], corners[n]));
                if (a2.X == a.X && a2.Y == a.Y && b2.X == b.X && b2.Y == b.Y)
                    continue;
                (record[0], record[1], record[2], record[3]) = (a.X, a.Y, b.X, b.Y);
                (record[4], record[5], record[6], record[7]) = (a2.X, a2.Y, b2.X, b2.Y);
                foreach (var f in record)
                {
                    var bytes = new byte[4];
                    BinaryPrimitives.WriteSingleLittleEndian(bytes, f);
                    output.AddRange(bytes);
                }
            }
        }
        return [.. output];
    }

    // 1802129d0's clustering against each cluster's first vertex.
    private static int[] WeldAcross(float[] r, int count, float tolerance, float normalDot)
    {
        var ids = new int[count];
        Array.Fill(ids, -1);
        var first = new List<int>();
        var tree = new Physics.MeshWeld.KdTree(r, 7, 0, [.. Enumerable.Range(0, count)]);
        var found = new List<int>();
        for (var i = 0; i < count; i++)
        {
            if (ids[i] != -1)
                continue;
            var p = i * 7;
            found.Clear();
            tree.FindInBox(found, r[p] - tolerance, r[p + 1] - tolerance, r[p + 2] - tolerance, r[p] + tolerance, r[p + 1] + tolerance, r[p + 2] + tolerance);
            foreach (var j in found)
            {
                if (j >= i)
                    continue;
                var cluster = ids[j];
                var q = first[cluster] * 7;
                if (r[p + 6] != r[q + 6])
                    continue;
                float dx = r[p] - r[q], dy = r[p + 1] - r[q + 1], dz = r[p + 2] - r[q + 2];
                if (!(dx * dx + dy * dy + dz * dz <= tolerance * tolerance))
                    continue;
                if (!(normalDot <= r[p + 5] * r[q + 5] + r[q + 4] * r[p + 4] + r[p + 3] * r[q + 3]))
                    continue;
                ids[i] = cluster;
                break;
            }
            if (ids[i] == -1)
            {
                ids[i] = first.Count;
                first.Add(i);
            }
        }
        return ids;
    }

    // 180213290's triangle test: both edges from the first corner have a
    // length, and their directions are not within 0.999 of parallel.
    private static bool Spread(PackingAssembly.Mesh m, List<int> corners, int c)
    {
        var p0 = Read3(m.Result.Vertices, corners[c] * m.Stride + m.PositionOffset);
        var p1 = Read3(m.Result.Vertices, corners[c + 1] * m.Stride + m.PositionOffset);
        var p2 = Read3(m.Result.Vertices, corners[c + 2] * m.Stride + m.PositionOffset);
        if (Normalize(new Vector3(p1.X - p0.X, p1.Y - p0.Y, p1.Z - p0.Z)) is not { } e1
            || Normalize(new Vector3(p2.X - p0.X, p2.Y - p0.Y, p2.Z - p0.Z)) is not { } e2)
            return false;
        return MathF.Abs(e2.Z * e1.Z + e2.Y * e1.Y + e2.X * e1.X) <= 0.999f;
    }

    // VectorNormalize as 180213290 inlines it: (y y + z z) + x x; null for a zero vector.
    private static Vector3? Normalize(Vector3 v)
    {
        var length = MathF.Sqrt(v.Y * v.Y + v.Z * v.Z + v.X * v.X);
        if (length < 1e-17f || length > 1e17f)
            return length != 0f ? VectorNormalizeSlow.Normalise(v) : null;
        var inv = 1f / length;
        return new Vector3(v.X * inv, v.Y * inv, v.Z * inv);
    }

    private static Vector3 Read3(byte[] b, int at)
        => new(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 4)),
               BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 8)));
}
