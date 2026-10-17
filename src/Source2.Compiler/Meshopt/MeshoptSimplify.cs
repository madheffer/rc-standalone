// Port of meshoptimizer's simplifier (src/simplifier.cpp, v1.2; v1.1 and v1.1.1 run the same
// code), the path meshopt_simplify takes without attributes, vertex locks, sparsity or pruning.
// Copyright (c) 2016-2026 Arseny Kapoulkine. MIT licence:
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software
// and associated documentation files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions: The above copyright notice
// and this permission notice shall be included in all copies or substantial portions of the
// Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.

namespace Source2.Compiler.Meshopt;

/// <summary>
/// meshopt_simplify as resourcecompiler links it (0923 1812b8710, a wrapper that
/// calls meshopt_simplifyEdge 1812b39f0 with no attributes, weights or lock: the
/// v1.1 to v1.2 signature with vertex_lock,
/// RegularizeLight tested, no PreserveFolds or ErrorClamped). Every float
/// operation keeps the C++ order; collapses are ranked by quadric error and
/// counting-sorted on the top 12 bits of the error.
/// </summary>
public static class MeshoptSimplify
{
    /// <summary>meshopt_SimplifyLockBorder.</summary>
    public const uint LockBorder = 1 << 0;
    /// <summary>meshopt_SimplifyErrorAbsolute.</summary>
    public const uint ErrorAbsolute = 1 << 2;
    /// <summary>meshopt_SimplifyRegularize.</summary>
    public const uint Regularize = 1 << 4;
    /// <summary>meshopt_SimplifyRegularizeLight.</summary>
    public const uint RegularizeLight = 1 << 6;

    private const uint Supported = LockBorder | ErrorAbsolute | Regularize | RegularizeLight;
    private const uint None = ~0u;

    private const byte KindManifold = 0, KindBorder = 1, KindSeam = 2, KindComplex = 3, KindLocked = 4;

    private static readonly byte[,] CanCollapse =
    {
        { 1, 1, 1, 1, 1 },
        { 0, 1, 0, 0, 1 },
        { 0, 0, 1, 0, 1 },
        { 0, 0, 0, 1, 1 },
        { 0, 0, 0, 0, 0 },
    };

    private static readonly byte[,] HasOpposite =
    {
        { 1, 1, 1, 1, 1 },
        { 1, 0, 1, 0, 0 },
        { 1, 1, 1, 0, 1 },
        { 1, 0, 0, 0, 0 },
        { 1, 0, 1, 0, 0 },
    };

    private struct Vector3
    {
        public float X, Y, Z;
    }

    private struct Quadric
    {
        public float A00, A11, A22, A10, A20, A21, B0, B1, B2, C, W;
    }

    private struct Collapse
    {
        public uint V0, V1;
        // the C++ union: bidi, error, errorui
        public uint Bits;
        public float Error { readonly get => BitConverter.UInt32BitsToSingle(Bits); set => Bits = BitConverter.SingleToUInt32Bits(value); }
    }

    private sealed class EdgeAdjacency(int indexCount, int vertexCount)
    {
        public readonly uint[] Offsets = new uint[vertexCount + 1];
        public readonly uint[] Next = new uint[indexCount];
        public readonly uint[] Prev = new uint[indexCount];
    }

    /// <summary>
    /// meshopt_simplify: <paramref name="indices"/> simplified towards
    /// <paramref name="targetIndexCount"/> within <paramref name="targetError"/>
    /// (relative to the mesh extent). Positions are the first three floats of each
    /// <paramref name="strideFloats"/>-float vertex. Returns the new index count; the
    /// indices are written to <paramref name="destination"/>.
    /// </summary>
    public static int Simplify(uint[] destination, ReadOnlySpan<uint> indices, ReadOnlySpan<float> vertexData, int vertexCount, int strideFloats,
        int targetIndexCount, float targetError, uint options, out float resultErrorOut)
    {
        if ((options & ~Supported) != 0)
            throw new NotSupportedException($"meshopt_simplify options {options & ~Supported:x} are not ported.");
        var indexCount = indices.Length;
        var result = destination;
        indices.CopyTo(result);

        var adjacency = new EdgeAdjacency(indexCount, vertexCount);
        UpdateEdgeAdjacency(adjacency, result, indexCount, vertexCount, null);

        var remap = new uint[vertexCount];
        var wedge = new uint[vertexCount];
        BuildPositionRemap(remap, wedge, vertexData, vertexCount, strideFloats);

        var vertexKind = new byte[vertexCount];
        var loop = new uint[vertexCount];
        var loopback = new uint[vertexCount];
        ClassifyVertices(vertexKind, loop, loopback, vertexCount, adjacency, remap, wedge, options);

        var positions = new Vector3[vertexCount];
        var vertexScale = RescalePositions(positions, vertexData, vertexCount, strideFloats);

        var quadrics = new Quadric[vertexCount];
        FillFaceQuadrics(quadrics, result, indexCount, positions, remap);
        FillVertexQuadrics(quadrics, positions, vertexCount, remap, options);
        FillEdgeQuadrics(quadrics, result, indexCount, positions, remap, vertexKind, loop, loopback);

        var collapseCapacity = BoundEdgeCollapses(adjacency, vertexCount, indexCount, vertexKind);
        var edgeCollapses = new Collapse[collapseCapacity];
        var collapseOrder = new uint[collapseCapacity];
        var collapseRemap = new uint[vertexCount];
        var collapseLocked = new byte[vertexCount];

        var resultCount = indexCount;
        var resultError = 0f;

        var errorScale = (options & ErrorAbsolute) != 0 ? vertexScale : 1f;
        var errorLimit = (targetError * targetError) / (errorScale * errorScale);

        while (resultCount > targetIndexCount)
        {
            UpdateEdgeAdjacency(adjacency, result, resultCount, vertexCount, remap);
            var edgeCollapseCount = PickEdgeCollapses(edgeCollapses, collapseCapacity, result, resultCount, remap, vertexKind, loop, loopback);
            if (edgeCollapseCount == 0)
                break;
            RankEdgeCollapses(edgeCollapses, edgeCollapseCount, positions, quadrics, remap);
            SortEdgeCollapses(collapseOrder, edgeCollapses, edgeCollapseCount);

            var triangleCollapseGoal = (resultCount - targetIndexCount) / 3;
            for (var i = 0; i < vertexCount; i++)
                collapseRemap[i] = (uint)i;
            Array.Clear(collapseLocked);

            var collapses = PerformEdgeCollapses(collapseRemap, collapseLocked, edgeCollapses, edgeCollapseCount, collapseOrder, remap, wedge, vertexKind, loop, loopback,
                positions, adjacency, triangleCollapseGoal, errorLimit, ref resultError);
            if (collapses == 0)
                break;

            UpdateQuadrics(collapseRemap, vertexCount, quadrics, remap);
            RemapEdgeLoops(loop, vertexCount, collapseRemap);
            RemapEdgeLoops(loopback, vertexCount, collapseRemap);
            resultCount = RemapIndexBuffer(result, resultCount, collapseRemap, remap);
        }

        resultErrorOut = MathF.Sqrt(resultError) * errorScale;
        return resultCount;
    }

    private static void UpdateEdgeAdjacency(EdgeAdjacency adjacency, uint[] indices, int indexCount, int vertexCount, uint[]? remap)
    {
        var faceCount = indexCount / 3;
        var offsets = adjacency.Offsets;
        // offsets[1..] is the C++ "offsets + 1"
        Array.Clear(offsets, 1, vertexCount);
        for (var i = 0; i < indexCount; i++)
        {
            var v = remap != null ? remap[indices[i]] : indices[i];
            offsets[v + 1]++;
        }
        uint offset = 0;
        for (var i = 0; i < vertexCount; i++)
        {
            var count = offsets[i + 1];
            offsets[i + 1] = offset;
            offset += count;
        }
        for (var i = 0; i < faceCount; i++)
        {
            uint a = indices[i * 3], b = indices[i * 3 + 1], c = indices[i * 3 + 2];
            if (remap != null)
            {
                a = remap[a];
                b = remap[b];
                c = remap[c];
            }
            var o = offsets[a + 1]++;
            adjacency.Next[o] = b;
            adjacency.Prev[o] = c;
            o = offsets[b + 1]++;
            adjacency.Next[o] = c;
            adjacency.Prev[o] = a;
            o = offsets[c + 1]++;
            adjacency.Next[o] = a;
            adjacency.Prev[o] = b;
        }
        offsets[0] = 0;
    }

    private static int HashBuckets2(int count)
    {
        var buckets = 1;
        while (buckets < count + count / 4)
            buckets *= 2;
        return buckets;
    }

    private static void BuildPositionRemap(uint[] remap, uint[] wedge, ReadOnlySpan<float> vertexData, int vertexCount, int strideFloats)
    {
        var tableSize = HashBuckets2(vertexCount);
        var table = new uint[tableSize];
        Array.Fill(table, None);
        var hashmod = (uint)tableSize - 1;
        for (var i = 0; i < vertexCount; i++)
        {
            var index = (uint)i;
            var bucket = PositionHash(vertexData, index, strideFloats) & hashmod;
            var slot = -1;
            for (uint probe = 0; probe <= hashmod; probe++)
            {
                var item = table[bucket];
                if (item == None || PositionEqual(vertexData, item, index, strideFloats))
                {
                    slot = (int)bucket;
                    break;
                }
                bucket = (bucket + probe + 1) & hashmod;
            }
            if (table[slot] == None)
                table[slot] = index;
            remap[index] = table[slot];
        }
        for (var i = 0; i < vertexCount; i++)
            wedge[i] = (uint)i;
        for (var i = 0; i < vertexCount; i++)
            if (remap[i] != i)
            {
                var r = remap[i];
                wedge[i] = wedge[r];
                wedge[r] = (uint)i;
            }
    }

    private static uint PositionHash(ReadOnlySpan<float> data, uint index, int stride)
    {
        var at = (int)index * stride;
        uint x = BitConverter.SingleToUInt32Bits(data[at]), y = BitConverter.SingleToUInt32Bits(data[at + 1]), z = BitConverter.SingleToUInt32Bits(data[at + 2]);
        x = x == 0x80000000 ? 0 : x;
        y = y == 0x80000000 ? 0 : y;
        z = z == 0x80000000 ? 0 : z;
        x ^= x >> 17;
        y ^= y >> 17;
        z ^= z >> 17;
        return (x * 73856093u) ^ (y * 19349663u) ^ (z * 83492791u);
    }

    private static bool PositionEqual(ReadOnlySpan<float> data, uint lhs, uint rhs, int stride)
    {
        int l = (int)lhs * stride, r = (int)rhs * stride;
        return data[l] == data[r] && data[l + 1] == data[r + 1] && data[l + 2] == data[r + 2];
    }

    private static bool HasEdge(EdgeAdjacency adjacency, uint a, uint b)
    {
        var count = adjacency.Offsets[a + 1] - adjacency.Offsets[a];
        var start = adjacency.Offsets[a];
        for (uint i = 0; i < count; i++)
            if (adjacency.Next[start + i] == b)
                return true;
        return false;
    }

    private static void ClassifyVertices(byte[] result, uint[] loop, uint[] loopback, int vertexCount, EdgeAdjacency adjacency, uint[] remap, uint[] wedge, uint options)
    {
        Array.Fill(loop, None);
        Array.Fill(loopback, None);
        var openinc = loopback;
        var openout = loop;
        for (var i = 0; i < vertexCount; i++)
        {
            var vertex = (uint)i;
            var count = adjacency.Offsets[vertex + 1] - adjacency.Offsets[vertex];
            var start = adjacency.Offsets[vertex];
            for (uint j = 0; j < count; j++)
            {
                var target = adjacency.Next[start + j];
                if (target == vertex)
                    openinc[vertex] = openout[vertex] = vertex;
                else if (!HasEdge(adjacency, target, vertex))
                {
                    openinc[target] = openinc[target] == None ? vertex : target;
                    openout[vertex] = openout[vertex] == None ? target : vertex;
                }
            }
        }
        for (var i = 0; i < vertexCount; i++)
        {
            if (remap[i] == i)
            {
                if (wedge[i] == i)
                {
                    uint openi = openinc[i], openo = openout[i];
                    if (openi == None && openo == None)
                        result[i] = KindManifold;
                    else if (openi != None && openo != None && remap[openi] == remap[openo] && openi != i)
                        result[i] = KindSeam;
                    else if (openi != i && openo != i)
                        result[i] = KindBorder;
                    else
                        result[i] = KindLocked;
                }
                else if (wedge[wedge[i]] == i)
                {
                    var w = wedge[i];
                    uint openiv = openinc[i], openov = openout[i];
                    uint openiw = openinc[w], openow = openout[w];
                    if (openiv != None && openiv != i && openov != None && openov != i &&
                        openiw != None && openiw != w && openow != None && openow != w)
                        result[i] = remap[openiv] == remap[openow] && remap[openov] == remap[openiw] && remap[openiv] != remap[openov] ? KindSeam : KindLocked;
                    else
                        result[i] = KindLocked;
                }
                else
                    result[i] = KindLocked;
            }
            else
                result[i] = result[remap[i]];
        }
        if ((options & LockBorder) != 0)
            for (var i = 0; i < vertexCount; i++)
                if (result[i] == KindBorder)
                    result[i] = KindLocked;
    }

    private static float RescalePositions(Vector3[] result, ReadOnlySpan<float> data, int vertexCount, int stride)
    {
        Span<float> minv = [float.MaxValue, float.MaxValue, float.MaxValue];
        Span<float> maxv = [-float.MaxValue, -float.MaxValue, -float.MaxValue];
        for (var i = 0; i < vertexCount; i++)
        {
            var at = i * stride;
            result[i].X = data[at];
            result[i].Y = data[at + 1];
            result[i].Z = data[at + 2];
            for (var j = 0; j < 3; j++)
            {
                var vj = data[at + j];
                minv[j] = minv[j] > vj ? vj : minv[j];
                maxv[j] = maxv[j] < vj ? vj : maxv[j];
            }
        }
        var extent = 0f;
        extent = (maxv[0] - minv[0]) < extent ? extent : (maxv[0] - minv[0]);
        extent = (maxv[1] - minv[1]) < extent ? extent : (maxv[1] - minv[1]);
        extent = (maxv[2] - minv[2]) < extent ? extent : (maxv[2] - minv[2]);
        var scale = extent == 0 ? 0f : 1f / extent;
        for (var i = 0; i < vertexCount; i++)
        {
            result[i].X = (result[i].X - minv[0]) * scale;
            result[i].Y = (result[i].Y - minv[1]) * scale;
            result[i].Z = (result[i].Z - minv[2]) * scale;
        }
        return extent;
    }

    private static float Normalize(ref Vector3 v)
    {
        var length = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        if (length > 0)
        {
            v.X /= length;
            v.Y /= length;
            v.Z /= length;
        }
        return length;
    }

    private static void QuadricAdd(ref Quadric q, in Quadric r)
    {
        q.A00 += r.A00;
        q.A11 += r.A11;
        q.A22 += r.A22;
        q.A10 += r.A10;
        q.A20 += r.A20;
        q.A21 += r.A21;
        q.B0 += r.B0;
        q.B1 += r.B1;
        q.B2 += r.B2;
        q.C += r.C;
        q.W += r.W;
    }

    private static float QuadricEval(in Quadric q, in Vector3 v)
    {
        var rx = q.B0;
        var ry = q.B1;
        var rz = q.B2;
        rx += q.A10 * v.Y;
        ry += q.A21 * v.Z;
        rz += q.A20 * v.X;
        rx *= 2;
        ry *= 2;
        rz *= 2;
        rx += q.A00 * v.X;
        ry += q.A11 * v.Y;
        rz += q.A22 * v.Z;
        var r = q.C;
        r += rx * v.X;
        r += ry * v.Y;
        r += rz * v.Z;
        return r;
    }

    private static float QuadricError(in Quadric q, in Vector3 v)
    {
        var r = QuadricEval(q, v);
        var s = q.W == 0f ? 0f : 1f / q.W;
        return MathF.Abs(r) * s;
    }

    private static Quadric QuadricFromPlane(float a, float b, float c, float d, float w)
    {
        var aw = a * w;
        var bw = b * w;
        var cw = c * w;
        var dw = d * w;
        return new Quadric
        {
            A00 = a * aw, A11 = b * bw, A22 = c * cw, A10 = a * bw, A20 = a * cw, A21 = b * cw,
            B0 = a * dw, B1 = b * dw, B2 = c * dw, C = d * dw, W = w,
        };
    }

    private static Quadric QuadricFromPoint(float x, float y, float z, float w) => new()
    {
        A00 = w, A11 = w, A22 = w, A10 = 0, A20 = 0, A21 = 0,
        B0 = -x * w, B1 = -y * w, B2 = -z * w, C = (x * x + y * y + z * z) * w, W = w,
    };

    private static Quadric QuadricFromTriangle(in Vector3 p0, in Vector3 p1, in Vector3 p2, float weight)
    {
        var p10 = new Vector3 { X = p1.X - p0.X, Y = p1.Y - p0.Y, Z = p1.Z - p0.Z };
        var p20 = new Vector3 { X = p2.X - p0.X, Y = p2.Y - p0.Y, Z = p2.Z - p0.Z };
        var normal = new Vector3 { X = p10.Y * p20.Z - p10.Z * p20.Y, Y = p10.Z * p20.X - p10.X * p20.Z, Z = p10.X * p20.Y - p10.Y * p20.X };
        var area = Normalize(ref normal);
        var distance = normal.X * p0.X + normal.Y * p0.Y + normal.Z * p0.Z;
        return QuadricFromPlane(normal.X, normal.Y, normal.Z, -distance, MathF.Sqrt(area) * weight);
    }

    private static Quadric QuadricFromTriangleEdge(in Vector3 p0, in Vector3 p1, in Vector3 p2, float weight)
    {
        var p10 = new Vector3 { X = p1.X - p0.X, Y = p1.Y - p0.Y, Z = p1.Z - p0.Z };
        var lengthsq = p10.X * p10.X + p10.Y * p10.Y + p10.Z * p10.Z;
        var length = MathF.Sqrt(lengthsq);
        var p20 = new Vector3 { X = p2.X - p0.X, Y = p2.Y - p0.Y, Z = p2.Z - p0.Z };
        var p20p = p20.X * p10.X + p20.Y * p10.Y + p20.Z * p10.Z;
        var perp = new Vector3 { X = p20.X * lengthsq - p10.X * p20p, Y = p20.Y * lengthsq - p10.Y * p20p, Z = p20.Z * lengthsq - p10.Z * p20p };
        Normalize(ref perp);
        var distance = perp.X * p0.X + perp.Y * p0.Y + perp.Z * p0.Z;
        return QuadricFromPlane(perp.X, perp.Y, perp.Z, -distance, length * weight);
    }

    private static void FillFaceQuadrics(Quadric[] quadrics, uint[] indices, int indexCount, Vector3[] positions, uint[] remap)
    {
        for (var i = 0; i < indexCount; i += 3)
        {
            uint i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
            var q = QuadricFromTriangle(positions[i0], positions[i1], positions[i2], 1f);
            QuadricAdd(ref quadrics[remap[i0]], q);
            QuadricAdd(ref quadrics[remap[i1]], q);
            QuadricAdd(ref quadrics[remap[i2]], q);
        }
    }

    private static void FillVertexQuadrics(Quadric[] quadrics, Vector3[] positions, int vertexCount, uint[] remap, uint options)
    {
        var factor = (options & RegularizeLight) != 0 ? 1e-2f : ((options & Regularize) != 0 ? 1e-1f : 1e-7f);
        for (var i = 0; i < vertexCount; i++)
        {
            if (remap[i] != i)
                continue;
            var p = positions[i];
            var w = quadrics[i].W * factor;
            var q = QuadricFromPoint(p.X, p.Y, p.Z, w);
            QuadricAdd(ref quadrics[i], q);
        }
    }

    private static void FillEdgeQuadrics(Quadric[] quadrics, uint[] indices, int indexCount, Vector3[] positions, uint[] remap, byte[] kind, uint[] loop, uint[] loopback)
    {
        ReadOnlySpan<int> next = [1, 2, 0, 1];
        for (var i = 0; i < indexCount; i += 3)
            for (var e = 0; e < 3; e++)
            {
                var i0 = indices[i + e];
                var i1 = indices[i + next[e]];
                var k0 = kind[i0];
                var k1 = kind[i1];
                if (k0 != KindBorder && k0 != KindSeam && k1 != KindBorder && k1 != KindSeam)
                    continue;
                if ((k0 == KindBorder || k0 == KindSeam) && loop[i0] != i1)
                    continue;
                if ((k1 == KindBorder || k1 == KindSeam) && loopback[i1] != i0)
                    continue;
                var i2 = indices[i + next[e + 1]];
                var edgeWeight = k0 == KindBorder || k1 == KindBorder ? 10f : 0.5f;
                var q = QuadricFromTriangleEdge(positions[i0], positions[i1], positions[i2], edgeWeight);
                var qt = QuadricFromTriangle(positions[i0], positions[i1], positions[i2], edgeWeight);
                qt.W = 0;
                QuadricAdd(ref q, qt);
                QuadricAdd(ref quadrics[remap[i0]], q);
                QuadricAdd(ref quadrics[remap[i1]], q);
            }
    }

    private static bool HasTriangleFlip(in Vector3 a, in Vector3 b, in Vector3 c, in Vector3 d)
    {
        var eb = new Vector3 { X = b.X - a.X, Y = b.Y - a.Y, Z = b.Z - a.Z };
        var ec = new Vector3 { X = c.X - a.X, Y = c.Y - a.Y, Z = c.Z - a.Z };
        var ed = new Vector3 { X = d.X - a.X, Y = d.Y - a.Y, Z = d.Z - a.Z };
        var nbc = new Vector3 { X = eb.Y * ec.Z - eb.Z * ec.Y, Y = eb.Z * ec.X - eb.X * ec.Z, Z = eb.X * ec.Y - eb.Y * ec.X };
        var nbd = new Vector3 { X = eb.Y * ed.Z - eb.Z * ed.Y, Y = eb.Z * ed.X - eb.X * ed.Z, Z = eb.X * ed.Y - eb.Y * ed.X };
        var ndp = nbc.X * nbd.X + nbc.Y * nbd.Y + nbc.Z * nbd.Z;
        var abc = nbc.X * nbc.X + nbc.Y * nbc.Y + nbc.Z * nbc.Z;
        var abd = nbd.X * nbd.X + nbd.Y * nbd.Y + nbd.Z * nbd.Z;
        return ndp <= 0.25f * MathF.Sqrt(abc * abd);
    }

    private static bool HasTriangleFlips(EdgeAdjacency adjacency, Vector3[] positions, uint[] collapseRemap, uint i0, uint i1)
    {
        var v0 = positions[i0];
        var v1 = positions[i1];
        var start = adjacency.Offsets[i0];
        var count = adjacency.Offsets[i0 + 1] - start;
        for (uint i = 0; i < count; i++)
        {
            var a = collapseRemap[adjacency.Next[start + i]];
            var b = collapseRemap[adjacency.Prev[start + i]];
            if (a == i1 || b == i1 || a == b)
                continue;
            if (HasTriangleFlip(positions[a], positions[b], v0, v1))
                return true;
        }
        return false;
    }

    private static uint GetComplexTarget(uint v, uint target, uint[] remap, uint[] loop, uint[] loopback)
    {
        var r = remap[target];
        if (loop[v] != None && remap[loop[v]] == r)
            return loop[v];
        if (loopback[v] != None && remap[loopback[v]] == r)
            return loopback[v];
        return target;
    }

    private static int BoundEdgeCollapses(EdgeAdjacency adjacency, int vertexCount, int indexCount, byte[] kind)
    {
        long dualCount = 0;
        for (var i = 0; i < vertexCount; i++)
        {
            var k = kind[i];
            var e = adjacency.Offsets[i + 1] - adjacency.Offsets[i];
            dualCount += k == KindManifold || k == KindSeam ? e : 0;
        }
        return (int)(indexCount - dualCount / 2) + 3;
    }

    private static int PickEdgeCollapses(Collapse[] collapses, int capacity, uint[] indices, int indexCount, uint[] remap, byte[] kind, uint[] loop, uint[] loopback)
    {
        var count = 0;
        ReadOnlySpan<int> next = [1, 2, 0];
        for (var i = 0; i < indexCount; i += 3)
        {
            if (count + 3 > capacity)
                break;
            for (var e = 0; e < 3; e++)
            {
                var i0 = indices[i + e];
                var i1 = indices[i + next[e]];
                if (remap[i0] == remap[i1])
                    continue;
                var k0 = kind[i0];
                var k1 = kind[i1];
                if ((CanCollapse[k0, k1] | CanCollapse[k1, k0]) == 0)
                    continue;
                if (HasOpposite[k0, k1] != 0 && remap[i1] > remap[i0])
                    continue;
                if ((k0 == KindBorder || k0 == KindSeam) && k1 != KindManifold && loop[i0] != i1)
                    continue;
                if ((k1 == KindBorder || k1 == KindSeam) && k0 != KindManifold && loopback[i1] != i0)
                    continue;
                if ((CanCollapse[k0, k1] & CanCollapse[k1, k0]) != 0)
                    collapses[count++] = new Collapse { V0 = i0, V1 = i1, Bits = 1 };
                else
                {
                    var e0 = CanCollapse[k0, k1] != 0 ? i0 : i1;
                    var e1 = CanCollapse[k0, k1] != 0 ? i1 : i0;
                    collapses[count++] = new Collapse { V0 = e0, V1 = e1, Bits = 0 };
                }
            }
        }
        return count;
    }

    private static void RankEdgeCollapses(Collapse[] collapses, int count, Vector3[] positions, Quadric[] quadrics, uint[] remap)
    {
        for (var i = 0; i < count; i++)
        {
            ref var c = ref collapses[i];
            var i0 = c.V0;
            var i1 = c.V1;
            var bidi = c.Bits != 0;
            var ei = QuadricError(quadrics[remap[i0]], positions[i1]);
            var ej = bidi ? QuadricError(quadrics[remap[i1]], positions[i0]) : float.MaxValue;
            var rev = bidi & (ej < ei);
            c.V0 = rev ? i1 : i0;
            c.V1 = rev ? i0 : i1;
            c.Error = ej < ei ? ej : ei;
        }
    }

    private static void SortEdgeCollapses(uint[] order, Collapse[] collapses, int count)
    {
        const int sortBits = 12;
        const uint sortBins = 2048 + 512;
        Span<uint> histogram = stackalloc uint[(int)sortBins];
        histogram.Clear();
        for (var i = 0; i < count; i++)
        {
            var key = (collapses[i].Bits << 1) >> (32 - sortBits);
            key = key < sortBins ? key : sortBins - 1;
            histogram[(int)key]++;
        }
        uint sum = 0;
        for (var i = 0; i < sortBins; i++)
        {
            var c = histogram[i];
            histogram[i] = sum;
            sum += c;
        }
        for (var i = 0; i < count; i++)
        {
            var key = (collapses[i].Bits << 1) >> (32 - sortBits);
            key = key < sortBins ? key : sortBins - 1;
            order[histogram[(int)key]++] = (uint)i;
        }
    }

    private static int PerformEdgeCollapses(uint[] collapseRemap, byte[] collapseLocked, Collapse[] collapses, int collapseCount, uint[] order, uint[] remap, uint[] wedge,
        byte[] kind, uint[] loop, uint[] loopback, Vector3[] positions, EdgeAdjacency adjacency, int triangleCollapseGoal, float errorLimit, ref float resultError)
    {
        var edgeCollapses = 0;
        var triangleCollapses = 0;
        var edgeCollapseGoal = triangleCollapseGoal / 2;
        for (var i = 0; i < collapseCount; i++)
        {
            var c = collapses[order[i]];
            if (c.Error > errorLimit)
                break;
            if (triangleCollapses >= triangleCollapseGoal)
                break;
            var errorGoal = edgeCollapseGoal < collapseCount ? 1.5f * collapses[order[edgeCollapseGoal]].Error : float.MaxValue;
            if (c.Error > errorGoal && c.Error > resultError && triangleCollapses > triangleCollapseGoal / 6)
                break;
            var i0 = c.V0;
            var i1 = c.V1;
            var r0 = remap[i0];
            var r1 = remap[i1];
            var k = kind[i0];
            if ((collapseLocked[r0] | collapseLocked[r1]) != 0)
                continue;
            if (HasTriangleFlips(adjacency, positions, collapseRemap, r0, r1))
            {
                edgeCollapseGoal++;
                continue;
            }
            if (k == KindComplex)
            {
                var v = i0;
                do
                {
                    collapseRemap[v] = GetComplexTarget(v, i1, remap, loop, loopback);
                    v = wedge[v];
                }
                while (v != i0);
            }
            else if (k == KindSeam)
            {
                var s0 = wedge[i0];
                var s1 = loop[i0] == i1 ? loopback[s0] : loop[s0];
                s1 = s1 != None ? s1 : wedge[i1];
                collapseRemap[i0] = i1;
                collapseRemap[s0] = s1;
            }
            else
                collapseRemap[i0] = i1;
            collapseLocked[r0] = 1;
            collapseLocked[r1] = 1;
            triangleCollapses += k == KindBorder ? 1 : 2;
            edgeCollapses++;
            resultError = resultError < c.Error ? c.Error : resultError;
        }
        return edgeCollapses;
    }

    private static void UpdateQuadrics(uint[] collapseRemap, int vertexCount, Quadric[] quadrics, uint[] remap)
    {
        for (var i = 0; i < vertexCount; i++)
        {
            if (collapseRemap[i] == i)
                continue;
            var i0 = (uint)i;
            var i1 = collapseRemap[i];
            var r0 = remap[i0];
            var r1 = remap[i1];
            if (i0 == r0)
                QuadricAdd(ref quadrics[r1], quadrics[r0]);
        }
    }

    private static int RemapIndexBuffer(uint[] indices, int indexCount, uint[] collapseRemap, uint[] remap)
    {
        var write = 0;
        for (var i = 0; i < indexCount; i += 3)
        {
            var v0 = collapseRemap[indices[i]];
            var v1 = collapseRemap[indices[i + 1]];
            var v2 = collapseRemap[indices[i + 2]];
            uint r0 = remap[v0], r1 = remap[v1], r2 = remap[v2];
            if (r0 != r1 && r0 != r2 && r1 != r2)
            {
                indices[write] = v0;
                indices[write + 1] = v1;
                indices[write + 2] = v2;
                write += 3;
            }
        }
        return write;
    }

    private static void RemapEdgeLoops(uint[] loop, int vertexCount, uint[] collapseRemap)
    {
        for (var i = 0; i < vertexCount; i++)
            if (loop[i] != None)
            {
                var l = loop[i];
                var r = collapseRemap[l];
                if (i == r)
                    loop[i] = loop[l] != None ? collapseRemap[loop[l]] : None;
                else
                    loop[i] = r;
            }
    }
}
