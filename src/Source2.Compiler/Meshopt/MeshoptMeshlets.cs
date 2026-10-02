namespace Source2.Compiler.Meshopt;

/// <summary>
/// meshopt 1.0's meshlet clusterizer as resourcecompiler (0923) builds it:
/// buildMeshletsFlex (1812bfeb0; buildMeshlets 1812bfdf0 calls it with
/// min triangles = max triangles and no split), with computeTriangleCones
/// (1812becb0), the k-d tree (1812bf290, partition 1812bf850, nearest
/// 1812bf620), getNeighborTriangle (1812bef70), appendSeedTriangles
/// (1812bd780) and selectSeedTriangle (1812bf9b0); and
/// optimizeMeshletLevel (1812bd240).
/// </summary>
public static class MeshoptMeshlets
{
    /// <summary>A meshlet: offsets into the vertex and triangle-byte arrays, and counts.</summary>
    public readonly record struct Meshlet(int VertexOffset, int TriangleOffset, int VertexCount, int TriangleCount);

    public sealed record Result(List<Meshlet> Meshlets, List<int> Vertices, List<byte> Triangles);

    private const int AddSeeds = 4;
    private const int MaxSeeds = 256;

    private struct Cone
    {
        public float Px, Py, Pz, Nx, Ny, Nz;
    }

    // Node: a split value or a point index, and a word (children << 2 | axis); a leaf's word holds its point count.
    private struct Node
    {
        public float Split;
        public uint Index;
        public uint Word;
    }

    public static Result Build(IReadOnlyList<int> indices, IReadOnlyList<float> positions, int vertexCount, int strideFloats,
                               int maxVertices, int minTriangles, int maxTriangles, float coneWeight, float splitFactor = 0f)
    {
        var meshlets = new List<Meshlet>();
        var meshletVertices = new List<int>();
        var meshletTriangles = new List<byte>();
        var indexCount = indices.Count;
        if (indexCount == 0)
            return new Result(meshlets, meshletVertices, meshletTriangles);
        var faceCount = indexCount / 3;

        // Adjacency (dense and sparse give the same per-vertex lists).
        var counts = new uint[vertexCount];
        foreach (var i in indices)
            counts[i]++;
        var offsets = new uint[vertexCount];
        uint off = 0;
        for (var v = 0; v < vertexCount; v++)
        {
            offsets[v] = off;
            off += counts[v];
        }
        var data = new uint[indexCount];
        var fill = (uint[])offsets.Clone();
        for (var f = 0; f < faceCount; f++)
            for (var k = 0; k < 3; k++)
                data[fill[indices[(f * 3) + k]]++] = (uint)f;
        var live = counts;
        var emitted = new bool[faceCount];

        // computeTriangleCones.
        var tris = new Cone[faceCount];
        var meshArea = 0f;
        for (var f = 0; f < faceCount; f++)
        {
            int a = indices[f * 3] * strideFloats, b = indices[(f * 3) + 1] * strideFloats, c = indices[(f * 3) + 2] * strideFloats;
            float ax = positions[a], ay = positions[a + 1], az = positions[a + 2];
            float bx = positions[b], by = positions[b + 1], bz = positions[b + 2];
            float cx = positions[c], cy = positions[c + 1], cz = positions[c + 2];
            float p20y = cy - ay, p20z = cz - az, p20x = cx - ax;
            float p10z = bz - az, p10y = by - ay, p10x = bx - ax;
            var nx = (p20z * p10y) - (p20y * p10z);
            var ny = (p20x * p10z) - (p20z * p10x);
            var nz = (p20y * p10x) - (p20x * p10y);
            var area = MathF.Sqrt((ny * ny) + (nx * nx) + (nz * nz));
            var inv = area != 0f ? 1f / area : 0f;
            tris[f] = new Cone
            {
                Px = (ax + bx + cx) / 3f,
                Py = (by + ay + cy) / 3f,
                Pz = (az + bz + cz) / 3f,
                Nx = inv * nx,
                Ny = inv * ny,
                Nz = inv * nz,
            };
            meshArea += area;
        }
        float expectedRadius;
        if (faceCount == 0)
            expectedRadius = MathF.Sqrt((float)maxTriangles * 0f) * 0.5f;
        else
            expectedRadius = MathF.Sqrt((float)maxTriangles * (meshArea / (float)faceCount) * 0.5f) * 0.5f;

        // k-d tree over the triangle centroids, leaves of 8.
        var kdIndices = new uint[faceCount];
        for (var f = 0; f < faceCount; f++)
            kdIndices[f] = (uint)f;
        var nodes = new Node[faceCount * 2];
        KdBuild(0, nodes, tris, kdIndices, 0, faceCount, 8, 0);

        float cornerX = float.MaxValue, cornerY = float.MaxValue, cornerZ = float.MaxValue;
        foreach (var t in tris)
        {
            cornerX = cornerX <= t.Px ? cornerX : t.Px;
            cornerY = cornerY <= t.Py ? cornerY : t.Py;
            cornerZ = cornerZ <= t.Pz ? cornerZ : t.Pz;
        }
        var used = new short[vertexCount];
        Array.Fill(used, (short)-1);

        uint initialSeed = uint.MaxValue;
        var initialScore = float.MaxValue;
        for (var f = 0; f < faceCount; f++)
        {
            float dy = tris[f].Py - cornerY, dx = tris[f].Px - cornerX, dz = tris[f].Pz - cornerZ;
            var d = MathF.Sqrt((dy * dy) + (dx * dx) + (dz * dz));
            if (initialSeed == uint.MaxValue || d < initialScore)
            {
                initialSeed = (uint)f;
                initialScore = d;
            }
        }

        var seeds = new uint[MaxSeeds];
        var seedCount = 0;
        int mVertexOffset = 0, mTriangleOffset = 0, mVertexCount = 0, mTriangleCount = 0;
        float accPx = 0, accPy = 0, accPz = 0, accNx = 0, accNy = 0, accNz = 0;
        while (true)
        {
            // getMeshletCone.
            var invCount = mTriangleCount == 0 ? 0f : 1f / (float)mTriangleCount;
            var cone = new Cone { Px = invCount * accPx, Py = invCount * accPy, Pz = invCount * accPz };
            var length = (accNy * accNy) + (accNx * accNx) + (accNz * accNz);
            var invLength = 0f;
            if (length != 0f)
                invLength = 1f / MathF.Sqrt(length);
            cone.Nz = accNz * invLength;
            cone.Nx = invLength * accNx;
            cone.Ny = invLength * accNy;

            uint best;
            if (meshlets.Count != 0 || mTriangleCount != 0)
                best = Neighbor(mVertexOffset, mVertexCount, cone, meshletVertices, indices, offsets, data, tris, live, used, expectedRadius, coneWeight);
            else
                best = initialSeed;
            var split = false;
            if (best == uint.MaxValue)
            {
                var index = uint.MaxValue;
                var distance = float.MaxValue;
                KdNearest(nodes, 0, tris, emitted, cone.Px, cone.Py, cone.Pz, ref index, ref distance);
                if (minTriangles <= mTriangleCount && 0f < splitFactor && expectedRadius * splitFactor < distance)
                    split = true;
                if (index == uint.MaxValue)
                {
                    if (mTriangleCount != 0)
                        meshlets.Add(new Meshlet(mVertexOffset, mTriangleOffset, mVertexCount, mTriangleCount));
                    return new Result(meshlets, meshletVertices, meshletTriangles);
                }
                best = index;
            }
            int Extra(uint t) => (used[indices[(int)t * 3]] < 0 ? 1 : 0) + (used[indices[((int)t * 3) + 1]] < 0 ? 1 : 0) + (used[indices[((int)t * 3) + 2]] < 0 ? 1 : 0);
            if (split || maxVertices < Extra(best) + mVertexCount || maxTriangles <= mTriangleCount)
            {
                // pruneSeedTriangles, the cap, appendSeedTriangles, selectSeedTriangle.
                var kept = 0;
                for (var i = 0; i < seedCount; i++)
                {
                    seeds[kept] = seeds[i];
                    kept += emitted[seeds[i]] ? 0 : 1;
                }
                seedCount = kept + AddSeeds <= MaxSeeds ? kept : MaxSeeds - AddSeeds;
                seedCount += AppendSeeds(seeds, seedCount, mVertexOffset, mVertexCount, meshletVertices, indices, offsets, data, tris, live, cornerX, cornerY, cornerZ);
                var chosen = SelectSeed(seeds, seedCount, indices, tris, live, cornerX, cornerY, cornerZ);
                if (chosen != uint.MaxValue)
                    best = chosen;
            }
            int a0 = indices[(int)best * 3], b0 = indices[((int)best * 3) + 1], c0 = indices[((int)best * 3) + 2];
            var flushed = false;
            if (maxVertices < Extra(best) + mVertexCount || maxTriangles <= mTriangleCount || split)
            {
                meshlets.Add(new Meshlet(mVertexOffset, mTriangleOffset, mVertexCount, mTriangleCount));
                for (var i = 0; i < mVertexCount; i++)
                    used[meshletVertices[mVertexOffset + i]] = -1;
                mVertexOffset += mVertexCount;
                mTriangleOffset += mTriangleCount * 3;
                mVertexCount = 0;
                mTriangleCount = 0;
                flushed = true;
            }
            foreach (var v in new[] { a0, b0, c0 })
                if (used[v] < 0)
                {
                    used[v] = (short)mVertexCount;
                    if (meshletVertices.Count <= mVertexOffset + mVertexCount)
                        meshletVertices.Add(v);
                    else
                        meshletVertices[mVertexOffset + mVertexCount] = v;
                    mVertexCount++;
                }
            meshletTriangles.Add((byte)used[a0]);
            meshletTriangles.Add((byte)used[b0]);
            meshletTriangles.Add((byte)used[c0]);
            mTriangleCount++;
            if (flushed)
            {
                accPx = accPy = accPz = accNx = accNy = accNz = 0f;
            }
            foreach (var v in new[] { a0, b0, c0 })
            {
                var at = offsets[v];
                var size = live[v];
                for (uint i = 0; i < size; i++)
                    if (data[at + i] == best)
                    {
                        data[at + i] = data[at + size - 1];
                        live[v]--;
                        break;
                    }
            }
            var tri = tris[best];
            accNz += tri.Nz;
            accPx += tri.Px;
            accPy += tri.Py;
            accPz += tri.Pz;
            accNx += tri.Nx;
            accNy += tri.Ny;
            emitted[best] = true;
        }
    }

    private static uint Neighbor(int vertexOffset, int vertexCount, Cone cone, List<int> meshletVertices, IReadOnlyList<int> indices, uint[] offsets, uint[] data,
                                 Cone[] tris, uint[] live, short[] used, float expectedRadius, float coneWeight)
    {
        var best = uint.MaxValue;
        var bestPriority = 5;
        var bestScore = float.MaxValue;
        for (var i = 0; i < vertexCount; i++)
        {
            var index = meshletVertices[vertexOffset + i];
            var at = offsets[index];
            var size = live[index];
            for (uint j = 0; j < size; j++)
            {
                var triangle = data[at + j];
                int a = indices[(int)triangle * 3], b = indices[((int)triangle * 3) + 1], c = indices[((int)triangle * 3) + 2];
                var extra = (used[a] < 0 ? 1 : 0) + (used[b] < 0 ? 1 : 0) + (used[c] < 0 ? 1 : 0);
                int priority;
                if (extra == 0)
                    priority = 0;
                else if (live[a] == 1 || live[b] == 1 || live[c] == 1)
                    priority = 1;
                else if ((live[a] == 2 ? 1 : 0) + (live[b] == 2 ? 1 : 0) + (live[c] == 2 ? 1 : 0) < 2)
                    priority = extra + 2;
                else
                    priority = extra + 1;
                if (bestPriority < priority)
                    continue;
                var t = tris[triangle];
                float dz = t.Pz - cone.Pz, dy = t.Py - cone.Py, dx = t.Px - cone.Px;
                var distance = MathF.Sqrt((dy * dy) + (dx * dx) + (dz * dz));
                var spread = (t.Ny * cone.Ny) + (t.Nx * cone.Nx) + (t.Nz * cone.Nz);
                var c1 = 1f - (spread * coneWeight);
                var clamped = 0.001f <= c1 ? c1 : 0.001f;
                var score = (((distance / expectedRadius) * (1f - coneWeight)) + 1f) * clamped;
                if (bestPriority <= priority && bestScore <= score)
                    continue;
                best = triangle;
                bestPriority = priority;
                bestScore = score;
            }
        }
        return best;
    }

    private static int AppendSeeds(uint[] seeds, int at, int vertexOffset, int vertexCount, List<int> meshletVertices, IReadOnlyList<int> indices, uint[] offsets, uint[] data,
                                   Cone[] tris, uint[] live, float cornerX, float cornerY, float cornerZ)
    {
        var bestSeeds = new uint[AddSeeds];
        var bestLive = new uint[AddSeeds];
        var bestScore = new float[AddSeeds];
        Array.Fill(bestSeeds, uint.MaxValue);
        Array.Fill(bestLive, uint.MaxValue);
        Array.Fill(bestScore, float.MaxValue);
        for (var i = 0; i < vertexCount; i++)
        {
            var index = meshletVertices[vertexOffset + i];
            var neighbor = uint.MaxValue;
            var neighborLive = uint.MaxValue;
            var o = offsets[index];
            for (uint j = 0; j < live[index]; j++)
            {
                var triangle = data[o + j];
                var l = live[indices[(int)triangle * 3]] + live[indices[((int)triangle * 3) + 2]] + live[indices[((int)triangle * 3) + 1]];
                if (l < neighborLive)
                {
                    neighbor = triangle;
                    neighborLive = l;
                }
            }
            if (neighbor == uint.MaxValue)
                continue;
            var t = tris[neighbor];
            float dz = t.Pz - cornerZ, dx = t.Px - cornerX, dy = t.Py - cornerY;
            var score = MathF.Sqrt((dy * dy) + (dx * dx) + (dz * dz));
            for (var j = 0; j < AddSeeds; j++)
                if (neighborLive < bestLive[j] || (neighborLive == bestLive[j] && score <= bestScore[j]))
                {
                    bestSeeds[j] = neighbor;
                    bestLive[j] = neighborLive;
                    bestScore[j] = score;
                    break;
                }
        }
        var count = 0;
        foreach (var s in bestSeeds)
            if (s != uint.MaxValue)
                seeds[at + count++] = s;
        return count;
    }

    private static uint SelectSeed(uint[] seeds, int count, IReadOnlyList<int> indices, Cone[] tris, uint[] live, float cornerX, float cornerY, float cornerZ)
    {
        var best = uint.MaxValue;
        var bestLive = uint.MaxValue;
        var bestScore = float.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var index = seeds[i];
            var t = tris[index];
            float dz = t.Pz - cornerZ, dx = t.Px - cornerX, dy = t.Py - cornerY;
            var l = live[indices[((int)index * 3) + 2]] + live[indices[((int)index * 3) + 1]] + live[indices[(int)index * 3]];
            var score = MathF.Sqrt((dy * dy) + (dx * dx) + (dz * dz));
            if (l < bestLive || (l == bestLive && score < bestScore))
            {
                best = index;
                bestScore = score;
                bestLive = l;
            }
        }
        return best;
    }

    private static float Coordinate(Cone c, int axis) => axis == 0 ? c.Px : axis == 1 ? c.Py : c.Pz;

    private static int KdLeaf(int offset, Node[] nodes, uint[] idx, int start, int count)
    {
        nodes[offset] = new Node { Index = idx[start], Word = ((uint)count << 2) | 3 };
        for (var i = 1; i < count; i++)
            nodes[offset + i] = new Node { Index = idx[start + i], Word = 0xffffffff };
        return offset + count;
    }

    private static int KdBuild(int offset, Node[] nodes, Cone[] points, uint[] idx, int start, int count, int leafSize, int depth)
    {
        while (count > leafSize)
        {
            float m0 = 0, m1 = 0, m2 = 0, v0 = 0, v1 = 0, v2 = 0;
            float runc = 1f, runs = 1f;
            for (var i = 0; i < count; i++)
            {
                runc += 1f;
                var p = points[idx[start + i]];
                var d0 = p.Px - m0;
                m0 += d0 * runs;
                var d1 = p.Py - m1;
                v0 = ((p.Px - m0) * d0) + v0;
                m1 += d1 * runs;
                var d2 = p.Pz - m2;
                v1 = ((p.Py - m1) * d1) + v1;
                var step = d2 * runs;
                runs = 1f / runc;
                m2 += step;
                v2 = ((p.Pz - m2) * d2) + v2;
            }
            var axis = (v0 < v1 || v0 < v2) ? (v1 < v2 ? 2 : 1) : 0;
            var split = axis == 0 ? m0 : axis == 1 ? m1 : m2;
            // kdtreePartition (1812bf850): unconditional swap, advance when below the pivot.
            var middle = 0;
            for (var i = 0; i < count; i++)
            {
                var v = Coordinate(points[idx[start + i]], axis);
                (idx[start + middle], idx[start + i]) = (idx[start + i], idx[start + middle]);
                middle += v < split ? 1 : 0;
            }
            if (middle <= leafSize >> 1 || count - (leafSize >> 1) <= middle || depth > 0x31)
                return KdLeaf(offset, nodes, idx, start, count);
            nodes[offset] = new Node { Split = split, Word = (uint)axis };
            depth++;
            var next = KdBuild(offset + 1, nodes, points, idx, start, middle, leafSize, depth);
            nodes[offset].Word = (nodes[offset].Word & 3) | ((uint)(next - offset - 1) << 2);
            offset = next;
            start += middle;
            count -= middle;
        }
        return KdLeaf(offset, nodes, idx, start, count);
    }

    private static void KdNearest(Node[] nodes, int root, Cone[] points, bool[] emitted, float px, float py, float pz, ref uint result, ref float limit)
    {
        if (nodes[root].Word < 4)
            return;
        while (true)
        {
            var word = nodes[root].Word;
            if ((word & 3) == 3)
            {
                var inactive = true;
                for (var i = 0; i < (int)(word >> 2); i++)
                {
                    var index = nodes[root + i].Index;
                    if (emitted[index])
                        continue;
                    inactive = false;
                    var p = points[index];
                    float dz = p.Pz - pz, dy = p.Py - py, dx = p.Px - px;
                    var d = MathF.Sqrt((dy * dy) + (dx * dx) + (dz * dz));
                    if (d < limit)
                    {
                        result = index;
                        limit = d;
                    }
                }
                if (inactive)
                    nodes[root].Word &= 3;
                return;
            }
            var axisValue = (word & 3) == 0 ? px : (word & 3) == 1 ? py : pz;
            var delta = axisValue - nodes[root].Split;
            var children = word >> 2;
            var first = 0f < delta ? children : 0u;
            var firstNode = root + 1 + (int)first;
            var secondNode = root + 1 + (int)(first ^ children);
            if ((nodes[firstNode].Word | nodes[secondNode].Word) < 4)
                nodes[root].Word &= 3;
            KdNearest(nodes, firstNode, points, emitted, px, py, pz, ref result, ref limit);
            if (limit < MathF.Abs(delta))
                return;
            root = secondNode;
            if (nodes[root].Word < 4)
                return;
        }
    }

    /// <summary>
    /// optimizeMeshletLevel (1812bd240) on one meshlet in place: the local
    /// triangle reorder by cache hits (and at a level above 0 the scored
    /// pick that stops after that many good triangles, then the corner
    /// rotation), then the vertices renumbered by first use.
    /// </summary>
    public static void OptimizeLevel(List<int> meshletVertices, int vertexOffset, int vertexCount, List<byte> triangles, int triangleOffset, int triangleCount, int level)
    {
        var tri = triangles.GetRange(triangleOffset, triangleCount * 3).ToArray();
        var cache = new byte[Math.Max(vertexCount, 256)];
        var valence = new byte[Math.Max(vertexCount, 256)];
        for (var i = 0; i < tri.Length; i++)
            valence[tri[i]]++;
        byte counter = 0x80;
        for (var cur = 0; cur < triangleCount; cur++)
        {
            var best = -1;
            var bestScore = -1;
            var good = 0;
            for (var t = cur; t < triangleCount; t++)
            {
                byte a = tri[t * 3], b = tri[(t * 3) + 1], c = tri[(t * 3) + 2];
                var da = (byte)(counter - cache[a]);
                var db = (byte)(counter - cache[b]);
                var dc = (byte)(counter - cache[c]);
                var hits = (db < 3 ? 1 : 0) + (dc < 3 ? 1 : 0) + (da < 3 ? 1 : 0);
                if (level == 0)
                {
                    if (bestScore < hits)
                    {
                        best = t;
                        bestScore = hits;
                    }
                    if (hits > 1)
                        break;
                }
                else
                {
                    var minValence = Math.Min(Math.Min(valence[a], valence[b]), valence[c]);
                    var score = (int)(((((uint)hits * 0x400) - dc - db - da) * 0x100) - minValence + 0x3ffff);
                    if (bestScore < score)
                    {
                        best = t;
                        bestScore = score;
                    }
                    if (hits > 1 && ++good >= level)
                        break;
                }
            }
            byte ba = tri[best * 3], bb = tri[(best * 3) + 1], bc = tri[(best * 3) + 2];
            Array.Copy(tri, cur * 3, tri, (cur + 1) * 3, (best - cur) * 3);
            valence[ba]--;
            valence[bb]--;
            valence[bc]--;
            counter++;
            cache[ba] = counter;
            cache[bb] = counter;
            cache[bc] = counter;
            tri[cur * 3] = ba;
            tri[(cur * 3) + 1] = bb;
            tri[(cur * 3) + 2] = bc;
        }
        if (level > 0)
        {
            var seen = new bool[Math.Max(vertexCount, 256)];
            for (var i = 0; i < triangleCount; i++)
            {
                byte a = tri[i * 3], b = tri[(i * 3) + 1], c = tri[(i * 3) + 2];
                byte oa = a, ob = b, oc = c;
                if (!seen[a])
                {
                    if (!seen[b])
                    {
                        if (!seen[c])
                        {
                            bool ab = false, bc = false, ca = false;
                            for (var j = i + 1; j < triangleCount && j <= i + 3; j++)
                            {
                                byte x = tri[j * 3], y = tri[(j * 3) + 1], z = tri[(j * 3) + 2];
                                ab |= (x == b && y == a) || (y == b && z == a) || (z == b && x == a);
                                bc |= (x == c && y == b) || (y == c && z == b) || (z == c && x == b);
                                ca |= (x == a && y == c) || (y == a && z == c) || (z == a && x == c);
                            }
                            if (ab && !bc)
                                (oa, ob, oc) = (b, c, a);
                            else if (bc && !ca)
                                (oa, ob, oc) = (c, a, b);
                        }
                    }
                    else if (!seen[c])
                        (oa, ob, oc) = (b, c, a);
                }
                tri[i * 3] = oa;
                tri[(i * 3) + 1] = ob;
                tri[(i * 3) + 2] = oc;
                seen[oa] = true;
                seen[ob] = true;
                seen[oc] = true;
            }
        }
        // Vertices renumbered by first use.
        var remap = new short[Math.Max(vertexCount, 256)];
        Array.Fill(remap, (short)-1);
        var order = new List<int>();
        for (var i = 0; i < tri.Length; i++)
        {
            if (remap[tri[i]] < 0)
            {
                remap[tri[i]] = (short)order.Count;
                order.Add(meshletVertices[vertexOffset + tri[i]]);
            }
            tri[i] = (byte)remap[tri[i]];
        }
        for (var i = 0; i < order.Count; i++)
            meshletVertices[vertexOffset + i] = order[i];
        for (var i = 0; i < tri.Length; i++)
            triangles[triangleOffset + i] = tri[i];
    }
}
