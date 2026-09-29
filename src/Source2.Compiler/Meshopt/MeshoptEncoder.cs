// Ported from meshoptimizer (src/vertexcodec.cpp, src/indexcodec.cpp),
// Copyright (C) 2016-2026, by Arseny Kapoulkine (arseny.kapoulkine@gmail.com),
// distributed under the MIT License:
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

namespace Source2.Compiler.Meshopt;

/// <summary>
/// meshoptimizer's vertex and index buffer codecs, encode side, as the map
/// compile writes a node model's MVTX and MIDX blocks: vertex codec version 1
/// at level 3 and index codec version 1. Measured by round trip on every
/// meshopt buffer of probe01, cardtest and atixref's world node models.
/// </summary>
public static class MeshoptEncoder
{
    private const byte VertexHeader = 0xa0;
    private const byte IndexHeader = 0xe0;
    private const byte SequenceHeader = 0xd0;
    private const int VertexBlockSizeBytes = 8192;
    private const int VertexBlockMaxSize = 256;
    private const int ByteGroupSize = 16;
    private const int ByteGroupDecodeLimit = 24;
    private const int TailMinSizeV0 = 32;
    private const int TailMinSizeV1 = 24;

    private static readonly int[] BitsV0 = [0, 2, 4, 8];
    private static readonly int[] BitsV1 = [0, 1, 2, 4, 8];

    private static readonly byte[] CodeAuxEncodingTable =
        [0x00, 0x76, 0x87, 0x56, 0x67, 0x78, 0xa9, 0x86, 0x65, 0x89, 0x68, 0x98, 0x01, 0x69, 0, 0];

    // ---- vertex codec ----

    private static int VertexBlockSize(int vertexSize)
    {
        var result = (VertexBlockSizeBytes / vertexSize) & ~(ByteGroupSize - 1);
        return result < VertexBlockMaxSize ? result : VertexBlockMaxSize;
    }

    /// <summary>meshopt_encodeVertexBufferBound.</summary>
    public static int VertexBufferBound(int vertexCount, int vertexSize)
    {
        var blockSize = VertexBlockSize(vertexSize);
        var blockCount = (vertexCount + blockSize - 1) / blockSize;
        var controlSize = vertexSize / 4;
        var byteHeaderSize = (blockSize / ByteGroupSize + 3) / 4;
        var tailSize = vertexSize + vertexSize / 4;
        var tailMin = Math.Max(TailMinSizeV0, TailMinSizeV1);
        var tailPad = tailSize < tailMin ? tailMin : tailSize;
        return 1 + blockCount * (controlSize + vertexSize * (byteHeaderSize + blockSize)) + tailPad;
    }

    private static uint Rotate(uint v, int r) => (v << r) | (v >> ((32 - r) & 31));

    private static bool GroupZero(ReadOnlySpan<byte> buffer)
    {
        for (var i = 0; i < ByteGroupSize; i++)
            if (buffer[i] != 0)
                return false;
        return true;
    }

    private static int GroupMeasure(ReadOnlySpan<byte> buffer, int bits)
    {
        if (bits == 0)
            return GroupZero(buffer) ? 0 : int.MaxValue;
        if (bits == 8)
            return ByteGroupSize;
        var result = ByteGroupSize * bits / 8;
        var sentinel = (1 << bits) - 1;
        for (var i = 0; i < ByteGroupSize; i++)
            result += buffer[i] >= sentinel ? 1 : 0;
        return result;
    }

    private static int EncodeGroup(Span<byte> data, int at, ReadOnlySpan<byte> buffer, int bits)
    {
        if (bits == 0)
            return at;
        if (bits == 8)
        {
            buffer[..ByteGroupSize].CopyTo(data[at..]);
            return at + ByteGroupSize;
        }
        var byteSize = 8 / bits;
        var sentinel = (byte)((1 << bits) - 1);
        for (var i = 0; i < ByteGroupSize; i += byteSize)
        {
            var b = 0;
            for (var k = 0; k < byteSize; k++)
            {
                var enc = buffer[i + k] >= sentinel ? sentinel : buffer[i + k];
                b = ((b << bits) | enc) & 0xff;
            }
            // 1-bit groups go in reverse bit order.
            if (bits == 1)
                b = (int)((((ulong)b * 0x80200802ul) & 0x0884422110ul) * 0x0101010101ul >> 32) & 0xff;
            data[at++] = (byte)b;
        }
        for (var i = 0; i < ByteGroupSize; i++)
        {
            var v = buffer[i];
            data[at] = v;
            at += v >= sentinel ? 1 : 0;
        }
        return at;
    }

    private static int EncodeBytes(Span<byte> data, int at, int end, ReadOnlySpan<byte> buffer, int bufferSize, ReadOnlySpan<int> bits)
    {
        var header = at;
        var headerSize = (bufferSize / ByteGroupSize + 3) / 4;
        if (end - at < headerSize)
            return -1;
        at += headerSize;
        data.Slice(header, headerSize).Clear();
        var lastBits = -1;
        for (var i = 0; i < bufferSize; i += ByteGroupSize)
        {
            if (end - at < ByteGroupDecodeLimit)
                return -1;
            var bestK = 3;
            var bestSize = GroupMeasure(buffer[i..], bits[bestK]);
            for (var k = 0; k < 3; k++)
            {
                var size = GroupMeasure(buffer[i..], bits[k]);
                // favour consistent bit selection across groups, but never replace literals
                if (size < bestSize || (size == bestSize && bits[k] == lastBits && bits[bestK] != 8))
                {
                    bestK = k;
                    bestSize = size;
                }
            }
            var offset = i / ByteGroupSize;
            data[header + offset / 4] |= (byte)(bestK << ((offset % 4) * 2));
            var bestBits = bits[bestK];
            at = EncodeGroup(data, at, buffer[i..], bestBits);
            lastBits = bestBits;
        }
        return at;
    }

    private static void EncodeDeltas(Span<byte> buffer, ReadOnlySpan<byte> vertexData, int vertexCount, int vertexSize,
                                     ReadOnlySpan<byte> lastVertex, int k, int channel)
    {
        switch (channel & 3)
        {
            case 0:
            {
                int p = lastVertex[k];
                for (var i = 0; i < vertexCount; i++)
                {
                    int v = vertexData[i * vertexSize + k];
                    var d = (byte)(v - p);
                    buffer[i] = (byte)((0 - (d >> 7)) ^ (d << 1));
                    p = v;
                }
                return;
            }
            case 1:
            {
                var k0 = k & ~1;
                var ks = (k & 1) * 8;
                var p = (ushort)(lastVertex[k0] | (lastVertex[k0 + 1] << 8));
                for (var i = 0; i < vertexCount; i++)
                {
                    var at = i * vertexSize + k0;
                    var v = (ushort)(vertexData[at] | (vertexData[at + 1] << 8));
                    var d = (ushort)(v - p);
                    var z = (ushort)((0 - (d >> 15)) ^ (d << 1));
                    buffer[i] = (byte)(z >> ks);
                    p = v;
                }
                return;
            }
            case 2:
            {
                var k0 = k & ~3;
                var ks = (k & 3) * 8;
                var rot = channel >> 4;
                var p = (uint)(lastVertex[k0] | (lastVertex[k0 + 1] << 8) | (lastVertex[k0 + 2] << 16) | (lastVertex[k0 + 3] << 24));
                for (var i = 0; i < vertexCount; i++)
                {
                    var at = i * vertexSize + k0;
                    var v = (uint)(vertexData[at] | (vertexData[at + 1] << 8) | (vertexData[at + 2] << 16) | (vertexData[at + 3] << 24));
                    var d = Rotate(v ^ p, rot);
                    buffer[i] = (byte)(d >> ks);
                    p = v;
                }
                return;
            }
            default:
                throw new InvalidOperationException("unsupported channel encoding");
        }
    }

    private static uint Read32(ReadOnlySpan<byte> data, int at)
        => (uint)(data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) | (data[at + 3] << 24));

    private static int EstimateBits(byte v) => v <= 15 ? (v <= 3 ? (v == 0 ? 0 : 2) : 4) : 8;

    private static int EstimateRotate(ReadOnlySpan<byte> vertexData, int vertexCount, int vertexSize, int k, int groupSize)
    {
        Span<long> sizes = stackalloc long[8];
        var last = Read32(vertexData, k);
        var index = 0;
        for (var i = 0; i < vertexCount; i += groupSize)
        {
            uint bitg = 0;
            for (var j = 0; j < groupSize && i + j < vertexCount; j++)
            {
                var v = Read32(vertexData, index++ * vertexSize + k);
                bitg |= v ^ last;
                last = v;
            }
            for (var j = 0; j < 8; j++)
            {
                var bitr = Rotate(bitg, j);
                sizes[j] += EstimateBits((byte)bitr) + EstimateBits((byte)(bitr >> 8))
                            + EstimateBits((byte)(bitr >> 16)) + EstimateBits((byte)(bitr >> 24));
            }
        }
        var best = 0;
        for (var r = 1; r < 8; r++)
            best = sizes[r] < sizes[best] ? r : best;
        return best;
    }

    private static int EstimateChannel(ReadOnlySpan<byte> vertexData, int vertexCount, int vertexSize, int k,
                                       int blockSize, int blockSkip, int maxChannel, int xorRot)
    {
        Span<byte> block = stackalloc byte[VertexBlockMaxSize];
        Span<byte> lastVertex = stackalloc byte[256];
        Span<long> sizes = stackalloc long[3];
        for (var i = 0; i < vertexCount; i += blockSize * blockSkip)
        {
            var size = i + blockSize < vertexCount ? blockSize : vertexCount - i;
            var aligned = (size + ByteGroupSize - 1) & ~(ByteGroupSize - 1);
            lastVertex.Clear();
            vertexData.Slice((i == 0 ? 0 : i - 1) * vertexSize, vertexSize).CopyTo(lastVertex);
            if (size < aligned)
                block[size..aligned].Clear();
            for (var channel = 0; channel < maxChannel; channel++)
                for (var j = 0; j < 4; j++)
                {
                    EncodeDeltas(block, vertexData[(i * vertexSize)..], size, vertexSize, lastVertex, k + j, channel | (xorRot << 4));
                    for (var ig = 0; ig < size; ig += ByteGroupSize)
                    {
                        var g = block[ig..];
                        var s1 = GroupMeasure(g, 1);
                        var s2 = GroupMeasure(g, 2);
                        var s4 = GroupMeasure(g, 4);
                        var s8 = GroupMeasure(g, 8);
                        var best = Math.Min(Math.Min(s1, s2), Math.Min(s4, s8));
                        sizes[channel] += best;
                    }
                }
        }
        var bestChannel = 0;
        for (var c = 1; c < maxChannel; c++)
            bestChannel = sizes[c] < sizes[bestChannel] ? c : bestChannel;
        return bestChannel == 2 ? bestChannel | (xorRot << 4) : bestChannel;
    }

    private static int EstimateControl(ReadOnlySpan<byte> buffer, int vertexCount, int aligned, int level)
    {
        var zero = true;
        for (var i = 0; i < aligned && zero; i += ByteGroupSize)
            zero = GroupZero(buffer[i..]);
        if (zero)
            return 2;
        if (level == 0)
            return 1;
        var headerSize = (aligned / ByteGroupSize + 3) / 4;
        long est0 = headerSize, est1 = headerSize;
        for (var i = 0; i < aligned; i += ByteGroupSize)
        {
            var g = buffer[i..];
            var s0 = GroupMeasure(g, 0);
            var s1 = GroupMeasure(g, 1);
            var s2 = GroupMeasure(g, 2);
            var s4 = GroupMeasure(g, 4);
            var s8 = GroupMeasure(g, 8);
            var s124 = Math.Min(Math.Min(s1, s2), s4);
            est0 += Math.Min(s124, s0);
            est1 += Math.Min(s124, s8);
        }
        if (est0 < vertexCount || est1 < vertexCount)
            return est0 < est1 ? 0 : 1;
        return 3;
    }

    private static int EncodeVertexBlock(Span<byte> data, int at, int end, ReadOnlySpan<byte> vertexData, int vertexCount, int vertexSize,
                                         Span<byte> lastVertex, ReadOnlySpan<byte> channels, int version, int level)
    {
        Span<byte> buffer = stackalloc byte[VertexBlockMaxSize];
        buffer.Clear();
        var aligned = (vertexCount + ByteGroupSize - 1) & ~(ByteGroupSize - 1);
        var controlSize = version == 0 ? 0 : vertexSize / 4;
        if (end - at < controlSize)
            return -1;
        var control = at;
        at += controlSize;
        data.Slice(control, controlSize).Clear();
        for (var k = 0; k < vertexSize; k++)
        {
            EncodeDeltas(buffer, vertexData, vertexCount, vertexSize, lastVertex, k, version == 0 ? 0 : channels[k / 4]);
            var ctrl = 0;
            if (version != 0)
            {
                ctrl = EstimateControl(buffer, vertexCount, aligned, level);
                data[control + k / 4] |= (byte)(ctrl << ((k % 4) * 2));
            }
            if (ctrl == 3)
            {
                if (end - at < vertexCount)
                    return -1;
                buffer[..vertexCount].CopyTo(data[at..]);
                at += vertexCount;
            }
            else if (ctrl != 2)
            {
                at = EncodeBytes(data, at, end, buffer, aligned, version == 0 ? BitsV0 : BitsV1.AsSpan(ctrl));
                if (at < 0)
                    return -1;
            }
        }
        vertexData.Slice(vertexSize * (vertexCount - 1), vertexSize).CopyTo(lastVertex);
        return at;
    }

    /// <summary>meshopt_encodeVertexBufferLevel; null when the bound is too small (never, with the bound).</summary>
    public static byte[] EncodeVertexBuffer(ReadOnlySpan<byte> vertices, int vertexCount, int vertexSize, int level = 3, int version = 1)
    {
        if (vertexSize <= 0 || vertexSize > 256 || vertexSize % 4 != 0)
            throw new ArgumentException("vertex size must be a multiple of 4 up to 256", nameof(vertexSize));
        var bound = VertexBufferBound(vertexCount, vertexSize);
        var data = new byte[bound];
        var at = 0;
        data[at++] = (byte)(VertexHeader | version);
        Span<byte> first = stackalloc byte[256];
        first.Clear();
        if (vertexCount > 0)
            vertices[..vertexSize].CopyTo(first);
        Span<byte> last = stackalloc byte[256];
        first.CopyTo(last);
        var blockSize = VertexBlockSize(vertexSize);
        Span<byte> channels = stackalloc byte[64];
        channels.Clear();
        if (version != 0 && level > 1 && vertexCount > 1)
            for (var k = 0; k < vertexSize; k += 4)
            {
                var rot = level >= 3 ? EstimateRotate(vertices, vertexCount, vertexSize, k, 16) : 0;
                channels[k / 4] = (byte)EstimateChannel(vertices, vertexCount, vertexSize, k, blockSize, 3, level >= 3 ? 3 : 2, rot);
            }
        var offset = 0;
        while (offset < vertexCount)
        {
            var size = offset + blockSize < vertexCount ? blockSize : vertexCount - offset;
            at = EncodeVertexBlock(data, at, bound, vertices[(offset * vertexSize)..], size, vertexSize, last, channels, version, level);
            if (at < 0)
                throw new InvalidOperationException("vertex encode overflowed its bound");
            offset += size;
        }
        var tailSize = vertexSize + (version == 0 ? 0 : vertexSize / 4);
        var tailMin = version == 0 ? TailMinSizeV0 : TailMinSizeV1;
        var tailPad = tailSize < tailMin ? tailMin : tailSize;
        if (tailSize < tailPad)
        {
            data.AsSpan(at, tailPad - tailSize).Clear();
            at += tailPad - tailSize;
        }
        first[..vertexSize].CopyTo(data.AsSpan(at));
        at += vertexSize;
        if (version != 0)
        {
            channels[..(vertexSize / 4)].CopyTo(data.AsSpan(at));
            at += vertexSize / 4;
        }
        return data[..at];
    }

    // ---- index codec ----

    private static int GetEdgeFifo(uint[,] fifo, uint a, uint b, uint c, int offset)
    {
        for (var i = 0; i < 16; i++)
        {
            var index = (offset - 1 - i) & 15;
            uint e0 = fifo[index, 0], e1 = fifo[index, 1];
            if (e0 == a && e1 == b)
                return (i << 2) | 0;
            if (e0 == b && e1 == c)
                return (i << 2) | 1;
            if (e0 == c && e1 == a)
                return (i << 2) | 2;
        }
        return -1;
    }

    private static void PushEdgeFifo(uint[,] fifo, uint a, uint b, ref int offset)
    {
        fifo[offset, 0] = a;
        fifo[offset, 1] = b;
        offset = (offset + 1) & 15;
    }

    private static int GetVertexFifo(uint[] fifo, uint v, int offset)
    {
        for (var i = 0; i < 16; i++)
            if (fifo[(offset - 1 - i) & 15] == v)
                return i;
        return -1;
    }

    private static void PushVertexFifo(uint[] fifo, uint v, ref int offset)
    {
        fifo[offset] = v;
        offset = (offset + 1) & 15;
    }

    private static void EncodeVByte(List<byte> data, uint v)
    {
        do
        {
            data.Add((byte)((v & 127) | (v > 127 ? 128u : 0u)));
            v >>= 7;
        } while (v != 0);
    }

    private static void EncodeIndex(List<byte> data, uint index, uint last)
    {
        var d = index - last;
        var v = (d << 1) ^ (uint)((int)d >> 31);
        EncodeVByte(data, v);
    }

    /// <summary>meshopt_encodeIndexBuffer, index codec version 1.</summary>
    public static byte[] EncodeIndexBuffer(ReadOnlySpan<uint> indices, int version = 1)
    {
        if (indices.Length % 3 != 0)
            throw new ArgumentException("index count must be a multiple of 3", nameof(indices));
        var code = new byte[indices.Length / 3];
        var codeAt = 0;
        var data = new List<byte>();
        var edgeFifo = new uint[16, 2];
        for (var i = 0; i < 16; i++)
            edgeFifo[i, 0] = edgeFifo[i, 1] = uint.MaxValue;
        var vertexFifo = new uint[16];
        Array.Fill(vertexFifo, uint.MaxValue);
        int edgeOffset = 0, vertexOffset = 0;
        uint next = 0, last = 0;
        var fecmax = version >= 1 ? 13 : 15;
        int[] rotations = [0, 1, 2, 0, 1];
        for (var i = 0; i < indices.Length; i += 3)
        {
            var fer = GetEdgeFifo(edgeFifo, indices[i], indices[i + 1], indices[i + 2], edgeOffset);
            if (fer >= 0 && (fer >> 2) < 15)
            {
                var o = fer & 3;
                uint a = indices[i + rotations[o]], b = indices[i + rotations[o + 1]], c = indices[i + rotations[o + 2]];
                var fe = fer >> 2;
                var fc = GetVertexFifo(vertexFifo, c, vertexOffset);
                int fec;
                if (fc >= 1 && fc < fecmax)
                    fec = fc;
                else if (c == next)
                {
                    next++;
                    fec = 0;
                }
                else
                    fec = 15;
                if (fec == 15 && version >= 1)
                {
                    if (c + 1 == last)
                    {
                        fec = 13;
                        last = c;
                    }
                    if (c == last + 1)
                    {
                        fec = 14;
                        last = c;
                    }
                }
                code[codeAt++] = (byte)((fe << 4) | fec);
                if (fec == 15)
                {
                    EncodeIndex(data, c, last);
                    last = c;
                }
                if (fec == 0 || fec >= fecmax)
                    PushVertexFifo(vertexFifo, c, ref vertexOffset);
                PushEdgeFifo(edgeFifo, c, b, ref edgeOffset);
                PushEdgeFifo(edgeFifo, a, c, ref edgeOffset);
            }
            else
            {
                uint i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                var rotation = i1 == next ? 1 : (i2 == next ? 2 : 0);
                uint a = indices[i + rotations[rotation]], b = indices[i + rotations[rotation + 1]], c = indices[i + rotations[rotation + 2]];
                _ = i0;
                var reset = false;
                if (a == 0 && b == 1 && c == 2 && next > 0 && version >= 1)
                {
                    reset = true;
                    next = 0;
                    Array.Fill(vertexFifo, uint.MaxValue);
                }
                var fb = GetVertexFifo(vertexFifo, b, vertexOffset);
                var fc = GetVertexFifo(vertexFifo, c, vertexOffset);
                int fea;
                if (a == next)
                {
                    next++;
                    fea = 0;
                }
                else
                    fea = 15;
                int feb;
                if (fb >= 0 && fb < 14)
                    feb = fb + 1;
                else if (b == next)
                {
                    next++;
                    feb = 0;
                }
                else
                    feb = 15;
                int fecc;
                if (fc >= 0 && fc < 14)
                    fecc = fc + 1;
                else if (c == next)
                {
                    next++;
                    fecc = 0;
                }
                else
                    fecc = 15;
                var codeaux = (byte)((feb << 4) | fecc);
                var codeauxIndex = Array.IndexOf(CodeAuxEncodingTable, codeaux);
                if (fea == 0 && codeauxIndex >= 0 && codeauxIndex < 14 && !reset)
                    code[codeAt++] = (byte)((15 << 4) | codeauxIndex);
                else
                {
                    code[codeAt++] = (byte)((15 << 4) | 14 | fea);
                    data.Add(codeaux);
                }
                if (fea == 15)
                {
                    EncodeIndex(data, a, last);
                    last = a;
                }
                if (feb == 15)
                {
                    EncodeIndex(data, b, last);
                    last = b;
                }
                if (fecc == 15)
                {
                    EncodeIndex(data, c, last);
                    last = c;
                }
                if (fea == 0 || fea == 15)
                    PushVertexFifo(vertexFifo, a, ref vertexOffset);
                if (feb == 0 || feb == 15)
                    PushVertexFifo(vertexFifo, b, ref vertexOffset);
                if (fecc == 0 || fecc == 15)
                    PushVertexFifo(vertexFifo, c, ref vertexOffset);
                PushEdgeFifo(edgeFifo, b, a, ref edgeOffset);
                PushEdgeFifo(edgeFifo, c, b, ref edgeOffset);
                PushEdgeFifo(edgeFifo, a, c, ref edgeOffset);
            }
        }
        var result = new byte[1 + code.Length + data.Count + 16];
        result[0] = (byte)(IndexHeader | version);
        code.CopyTo(result, 1);
        data.CopyTo(result, 1 + code.Length);
        CodeAuxEncodingTable.CopyTo(result, 1 + code.Length + data.Count);
        return result;
    }

    /// <summary>meshopt_encodeIndexSequence, index codec version 1.</summary>
    public static byte[] EncodeIndexSequence(ReadOnlySpan<uint> indices, int version = 1)
    {
        var data = new List<byte> { (byte)(SequenceHeader | version) };
        Span<uint> last = stackalloc uint[2];
        last.Clear();
        var current = 0;
        foreach (var index in indices)
        {
            var cd = (int)(index - last[current]);
            current ^= Math.Abs(cd) >= 30 ? 1 : 0;
            var d = index - last[current];
            var v = (d << 1) ^ (uint)((int)d >> 31);
            EncodeVByte(data, (v << 1) | (uint)current);
            last[current] = index;
        }
        data.AddRange([0, 0, 0, 0]);
        return [.. data];
    }
}
