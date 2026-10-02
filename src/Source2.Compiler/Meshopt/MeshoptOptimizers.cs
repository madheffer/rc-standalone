namespace Source2.Compiler.Meshopt;

/// <summary>
/// The draw-order passes CResourceCompilerMesh::AddDrawDescriptors runs on
/// each draw, as resourcecompiler (0923) builds them: meshopt's
/// optimizeVertexCacheTable (1812b8c10) with kVertexScoreTable
/// (18243aab0), and optimizeOverdraw (1812c4f30: hard boundaries from
/// whole-triangle cache misses, soft boundaries 1812c4c40, sort data
/// 1812c4230 with the mesh centroid over the vertices, the 11-bit radix
/// sort 1812c4810). Cache size 16, valence clamp 8.
/// </summary>
public static class MeshoptOptimizers
{
    private const int CacheSize = 16;
    private const int ValenceMax = 8;

    // kVertexScoreTable: cache[1 + position] (17 entries), live[min(valence, 8)] (9 entries).
    private static readonly float[] Table = [.. new uint[]
    {
        0x0, 0x3f476c8b, 0x3f4a7efa, 0x3f49fbe7, 0x3f7b22d1, 0x3f57ced9, 0x3f39db23, 0x3f58d4fe, 0x3f61cac1,
        0x3f5df3b6, 0x3f4c8b44, 0x3f245a1d, 0x3f1ced91, 0x3f19999a, 0x3f116873, 0x3ebe76c9, 0x3e6f9db2,
        0x0, 0x3f7eb852, 0x3f36872b, 0x3ee66666, 0x3eced917, 0x3d71a9fc, 0x3ba3d70a, 0x3e16872b, 0x3bc49ba6,
    }.Select(BitConverter.UInt32BitsToSingle)];

    private static float Score(int cachePosition, uint live) => Table[17 + (live < ValenceMax ? (int)live : ValenceMax)] + Table[1 + cachePosition];

    /// <summary>
    /// buildTriangleAdjacency (1812b88e0): per vertex, the triangles using
    /// it in triangle order (counts, offsets, data).
    /// </summary>
    private static (uint[] Counts, uint[] Offsets, uint[] Data) Adjacency(IReadOnlyList<int> indices, int vertexCount)
    {
        var faces = indices.Count / 3;
        var counts = new uint[vertexCount];
        foreach (var i in indices)
            counts[i]++;
        var offsets = new uint[vertexCount];
        uint offset = 0;
        for (var v = 0; v < vertexCount; v++)
        {
            offsets[v] = offset;
            offset += counts[v];
        }
        var data = new uint[indices.Count];
        var fill = (uint[])offsets.Clone();
        for (var f = 0; f < faces; f++)
            for (var k = 0; k < 3; k++)
                data[fill[indices[(f * 3) + k]]++] = (uint)f;
        return (counts, offsets, data);
    }

    public static int[] OptimizeVertexCache(IReadOnlyList<int> indices, int vertexCount)
    {
        var faceCount = indices.Count / 3;
        var destination = new int[indices.Count];
        if (indices.Count == 0 || vertexCount == 0)
            return destination;
        var (live, offsets, data) = Adjacency(indices, vertexCount);
        var emitted = new bool[faceCount];
        var vertexScores = new float[vertexCount];
        for (var v = 0; v < vertexCount; v++)
            vertexScores[v] = Score(-1, live[v]);
        var triangleScores = new float[faceCount];
        for (var f = 0; f < faceCount; f++)
            triangleScores[f] = vertexScores[indices[f * 3]] + vertexScores[indices[(f * 3) + 1]] + vertexScores[indices[(f * 3) + 2]];
        var cache = new int[CacheSize + 4];
        var cacheNew = new int[CacheSize + 4];
        var cacheCount = 0;
        var current = 0;
        var inputCursor = 1;
        var output = 0;
        while (current != -1)
        {
            int a = indices[current * 3], b = indices[(current * 3) + 1], c = indices[(current * 3) + 2];
            destination[output * 3] = a;
            destination[(output * 3) + 1] = b;
            destination[(output * 3) + 2] = c;
            output++;
            emitted[current] = true;
            triangleScores[current] = 0;
            var write = 0;
            cacheNew[write++] = a;
            cacheNew[write++] = b;
            cacheNew[write++] = c;
            for (var i = 0; i < cacheCount; i++)
            {
                var index = cache[i];
                cacheNew[write] = index;
                write += (index != a && index != b && index != c) ? 1 : 0;
            }
            (cache, cacheNew) = (cacheNew, cache);
            cacheCount = write > CacheSize ? CacheSize : write;
            for (var k = 0; k < 3; k++)
            {
                var index = indices[(current * 3) + k];
                var at = offsets[index];
                var size = live[index];
                for (uint i = 0; i < size; i++)
                    if (data[at + i] == current)
                    {
                        data[at + i] = data[at + size - 1];
                        live[index]--;
                        break;
                    }
            }
            var best = -1;
            var bestScore = 0f;
            for (var i = 0; i < write; i++)
            {
                var index = cache[i];
                if (live[index] == 0)
                    continue;
                var position = i >= CacheSize ? -1 : i;
                var score = Score(position, live[index]);
                var diff = score - vertexScores[index];
                vertexScores[index] = score;
                var at = offsets[index];
                for (uint t = 0; t < live[index]; t++)
                {
                    var tri = (int)data[at + t];
                    var triScore = diff + triangleScores[tri];
                    if (bestScore < triScore)
                    {
                        best = tri;
                        bestScore = triScore;
                    }
                    triangleScores[tri] = triScore;
                }
            }
            current = best;
            if (current == -1)
            {
                while (inputCursor < faceCount && emitted[inputCursor])
                    inputCursor++;
                current = inputCursor < faceCount ? inputCursor : -1;
            }
        }
        return destination;
    }

    // updateCache: whole-triangle misses against a FIFO of timestamps.
    private static int UpdateCache(int a, int b, int c, uint[] timestamps, ref uint timestamp)
    {
        var misses = 0;
        if (timestamp - timestamps[a] > CacheSize)
        {
            timestamps[a] = timestamp++;
            misses++;
        }
        if (timestamp - timestamps[b] > CacheSize)
        {
            timestamps[b] = timestamp++;
            misses++;
        }
        if (timestamp - timestamps[c] > CacheSize)
        {
            timestamps[c] = timestamp++;
            misses++;
        }
        return misses;
    }

    /// <summary>meshopt_optimizeOverdraw over float3 positions (any stride, in floats).</summary>
    public static int[] OptimizeOverdraw(IReadOnlyList<int> indices, IReadOnlyList<float> positions, int vertexCount, int strideFloats, float threshold)
    {
        var destination = new int[indices.Count];
        if (indices.Count == 0 || vertexCount == 0)
            return destination;
        var faces = indices.Count / 3;
        var timestamps = new uint[vertexCount];
        // Hard boundaries.
        var hard = new List<int>();
        uint stamp = CacheSize + 1;
        for (var f = 0; f < faces; f++)
        {
            var m = UpdateCache(indices[f * 3], indices[(f * 3) + 1], indices[(f * 3) + 2], timestamps, ref stamp);
            if (f == 0 || m == 3)
                hard.Add(f);
        }
        // Soft boundaries (1812c4c40).
        Array.Clear(timestamps);
        stamp = 0;
        var soft = new List<int>();
        for (var it = 0; it < hard.Count; it++)
        {
            var start = hard[it];
            var end = it + 1 < hard.Count ? hard[it + 1] : faces;
            stamp += CacheSize + 1;
            uint clusterMisses = 0;
            for (var f = start; f < end; f++)
                clusterMisses += (uint)UpdateCache(indices[f * 3], indices[(f * 3) + 1], indices[(f * 3) + 2], timestamps, ref stamp);
            var clusterThreshold = (float)clusterMisses / (float)(end - start) * threshold;
            soft.Add(start);
            stamp += CacheSize + 1;
            uint runningMisses = 0, runningFaces = 0;
            for (var f = start; f < end; f++)
            {
                runningMisses += (uint)UpdateCache(indices[f * 3], indices[(f * 3) + 1], indices[(f * 3) + 2], timestamps, ref stamp);
                runningFaces++;
                if ((float)runningMisses / (float)runningFaces <= clusterThreshold)
                {
                    soft.Add(f + 1);
                    stamp += CacheSize + 1;
                    runningMisses = 0;
                    runningFaces = 0;
                }
            }
            if (soft[^1] != start)
                soft.RemoveAt(soft.Count - 1);
        }
        var sortData = SortData(indices, positions, vertexCount, strideFloats, soft);
        var order = SortOrderRadix(sortData);
        var offset = 0;
        foreach (var cluster in order)
        {
            var begin = soft[cluster] * 3;
            var finish = cluster + 1 < soft.Count ? soft[cluster + 1] * 3 : indices.Count;
            for (var i = begin; i < finish; i++)
                destination[offset++] = indices[i];
        }
        return destination;
    }

    // calculateSortData (1812c4230), the binary's float order.
    private static float[] SortData(IReadOnlyList<int> indices, IReadOnlyList<float> p, int vertexCount, int stride, List<int> clusters)
    {
        float mx = 0, my = 0, mz = 0;
        for (var v = 0; v < vertexCount; v++)
        {
            mx += p[v * stride];
            my += p[(v * stride) + 1];
            mz += p[(v * stride) + 2];
        }
        var count = (float)vertexCount;
        var result = new float[clusters.Count];
        for (var cl = 0; cl < clusters.Count; cl++)
        {
            var begin = clusters[cl] * 3;
            var end = cl + 1 < clusters.Count ? clusters[cl + 1] * 3 : indices.Count;
            float area = 0, cx = 0, cy = 0, cz = 0, nx = 0, ny = 0, nz = 0;
            for (var i = begin; i < end; i += 3)
            {
                int i0 = indices[i] * stride, i1 = indices[i + 1] * stride, i2 = indices[i + 2] * stride;
                float p0x = p[i0], p0y = p[i0 + 1], p0z = p[i0 + 2];
                float p20z = p[i2 + 2] - p0z, p20x = p[i2] - p0x, p20y = p[i2 + 1] - p0y;
                float p10z = p[i1 + 2] - p0z, p10y = p[i1 + 1] - p0y, p10x = p[i1] - p0x;
                var normalX = (p20z * p10y) - (p20y * p10z);
                var normalY = (p20x * p10z) - (p20z * p10x);
                var normalZ = (p20y * p10x) - (p20x * p10y);
                var a = MathF.Sqrt((normalY * normalY) + (normalX * normalX) + (normalZ * normalZ));
                nz += normalZ;
                ny += normalY;
                var third = a / 3f;
                nx += normalX;
                area += a;
                cx += (p0x + p[i1] + p[i2]) * third;
                cy += (p0y + p[i1 + 1] + p[i2 + 1]) * third;
                cz += (p0z + p[i1 + 2] + p[i2 + 2]) * third;
            }
            var invArea = area == 0f ? 0f : 1f / area;
            var length = MathF.Sqrt((ny * ny) + (nx * nx) + (nz * nz));
            var invLength = length != 0f ? 1f / length : 0f;
            result[cl] = ((cx * invArea) - (mx / count)) * nx * invLength
                + ((cy * invArea) - (my / count)) * ny * invLength
                + ((cz * invArea) - (mz / count)) * nz * invLength;
        }
        return result;
    }

    // calculateSortOrderRadix (1812c4810): keys 0.5 - 0.5 d / max over 11 bits, counting sort.
    private static int[] SortOrderRadix(float[] sortData)
    {
        var max = 0.001f;
        foreach (var d in sortData)
            max = MathF.Abs(d) <= max ? max : MathF.Abs(d);
        var keys = new ushort[sortData.Length];
        for (var i = 0; i < sortData.Length; i++)
        {
            var v = 0.5f - (sortData[i] / max * 0.5f);
            if (v <= 0f)
                v = 0f;
            if (1f <= v)
                v = 1f;
            keys[i] = (ushort)((int)((v * 2047f) + 0.5f) & 0x7ff);
        }
        var histogram = new uint[2048];
        foreach (var k in keys)
            histogram[k]++;
        uint sum = 0;
        for (var i = 0; i < 2048; i++)
        {
            var c = histogram[i];
            histogram[i] = sum;
            sum += c;
        }
        var order = new int[sortData.Length];
        for (var i = 0; i < keys.Length; i++)
            order[histogram[keys[i]]++] = i;
        return order;
    }

    /// <summary>The first-use renumbering AddDrawDescriptors applies before the passes: (new indices, old vertex per new).</summary>
    public static (int[] Indices, int[] Remap) RenumberByFirstUse(IReadOnlyList<int> indices)
    {
        var map = new Dictionary<int, int>();
        var remap = new List<int>();
        var result = new int[indices.Count];
        for (var i = 0; i < indices.Count; i++)
        {
            if (!map.TryGetValue(indices[i], out var n))
            {
                map[indices[i]] = n = remap.Count;
                remap.Add(indices[i]);
            }
            result[i] = n;
        }
        return (result, [.. remap]);
    }
}
