namespace Source2.Compiler.Meshes;

/// <summary>
/// meshoptimizer's index buffer codec (format v1, header 0xE1), the compression of a model's
/// triangle lists (CTRL <c>m_bMeshoptCompressed</c> on an index buffer): a 16-entry edge FIFO and
/// vertex FIFO, one code byte per triangle, varint-delta free indices and a fixed 16-byte
/// code-aux table at the end. The encoder writes what resourcecompiler writes (docs/MESHES.md).
/// </summary>
public static class MeshoptIndexCodec
{
    // The code-aux table meshoptimizer encodes with; the last two entries are unused.
    private static readonly byte[] CodeAuxTable =
        [0x00, 0x76, 0x87, 0x56, 0x67, 0x78, 0xa9, 0x86, 0x65, 0x89, 0x68, 0x98, 0x01, 0x69, 0x00, 0x00];

    private static readonly int[][] Order = [[0, 1, 2], [1, 2, 0], [2, 0, 1]];

    /// <summary>Decode <paramref name="count"/> indices (a multiple of 3) of 2 or 4 bytes.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> buffer, int count, int indexSize)
    {
        if (count % 3 != 0 || indexSize is not (2 or 4))
            throw new InvalidDataException("An index buffer holds whole triangles of 2- or 4-byte indices.");
        int dataOffset = 1 + count / 3;
        if (buffer.Length < dataOffset + 16 || (buffer[0] & 0xF0) != 0xE0 || (buffer[0] & 0x0F) > 1)
            throw new InvalidDataException("Not a meshopt index buffer (v0 or v1).");
        int version = buffer[0] & 0x0F;
        var edges = new (uint A, uint B)[16];
        var vertices = new uint[16];
        int edgeOffset = 0, vertexOffset = 0;
        uint next = 0, last = 0;
        int fecmax = version >= 1 ? 13 : 15;
        var code = buffer[1..dataOffset];
        var data = buffer[dataOffset..];
        int position = 0, safeEnd = data.Length - 16;
        var table = data[safeEnd..];
        var output = new uint[count];
        int o = 0;
        void PushEdge(uint a, uint b) { edges[edgeOffset] = (a, b); edgeOffset = (edgeOffset + 1) & 15; }
        void PushVertex(uint v, bool cond = true) { vertices[vertexOffset] = v; vertexOffset = (vertexOffset + (cond ? 1 : 0)) & 15; }
        uint ReadIndex(ReadOnlySpan<byte> d, ref int p)
        {
            uint lead = d[p++], v = lead & 127;
            if (lead >= 128)
                for (int i = 0, shift = 7; i < 4; i++, shift += 7)
                {
                    uint g = d[p++];
                    v |= (g & 127) << shift;
                    if (g < 128) break;
                }
            return last + ((v >> 1) ^ (uint)-(int)(v & 1));
        }

        foreach (byte codetri in code)
        {
            uint a, b, c;
            if (codetri < 0xF0)
            {
                (a, b) = edges[(edgeOffset - 1 - (codetri >> 4)) & 15];
                int fec = codetri & 15;
                if (fec < fecmax)
                {
                    c = fec == 0 ? next : vertices[(vertexOffset - 1 - fec) & 15];
                    if (fec == 0) next++;
                    PushVertex(c, fec == 0);
                }
                else
                {
                    if (position > safeEnd) throw new InvalidDataException("Index buffer data is truncated.");
                    last = c = fec != 15 ? last + (uint)(fec * 2 - 27) : ReadIndex(data, ref position);
                    PushVertex(c);
                }
                PushEdge(c, b);
                PushEdge(a, c);
            }
            else
            {
                int feb, fec;
                bool table16 = codetri < 0xFE;
                byte codeaux;
                if (table16)
                    codeaux = table[codetri & 15];
                else
                {
                    if (position > safeEnd) throw new InvalidDataException("Index buffer data is truncated.");
                    codeaux = data[position++];
                }
                feb = codeaux >> 4;
                fec = codeaux & 15;
                int fea = table16 || codetri == 0xFE ? 0 : 15;
                if (!table16 && codeaux == 0)
                    next = 0;
                a = fea == 0 ? next++ : 0;
                b = feb == 0 ? next++ : vertices[(vertexOffset - feb) & 15];
                c = fec == 0 ? next++ : vertices[(vertexOffset - fec) & 15];
                if (fea == 15) last = a = ReadIndex(data, ref position);
                if (feb == 15) last = b = ReadIndex(data, ref position);
                if (fec == 15) last = c = ReadIndex(data, ref position);
                PushVertex(a);
                PushVertex(b, feb == 0 || feb == 15);
                PushVertex(c, fec == 0 || fec == 15);
                PushEdge(b, a);
                PushEdge(c, b);
                PushEdge(a, c);
            }
            output[o++] = a;
            output[o++] = b;
            output[o++] = c;
        }
        if (position != safeEnd)
            throw new InvalidDataException("Index buffer data is malformed.");
        var bytes = new byte[count * indexSize];
        for (int i = 0; i < count; i++)
        {
            if (indexSize == 2) BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), (ushort)output[i]);
            else BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), output[i]);
        }
        return bytes;
    }

    /// <summary>Encode a triangle list (format v1).</summary>
    public static byte[] Encode(ReadOnlySpan<uint> indices)
    {
        if (indices.Length % 3 != 0)
            throw new ArgumentException("An index buffer holds whole triangles.");
        const int version = 1, fecmax = 13;
        var edges = new (uint A, uint B)[16];
        var vertices = new uint[16];
        Array.Fill(edges, (uint.MaxValue, uint.MaxValue));
        Array.Fill(vertices, uint.MaxValue);
        int edgeOffset = 0, vertexOffset = 0;
        uint next = 0, last = 0;
        var code = new List<byte>(indices.Length / 3);
        var data = new List<byte>();

        int EdgeFifo(uint a, uint b, uint c)
        {
            for (int i = 0; i < 16; i++)
            {
                var (e0, e1) = edges[(edgeOffset - 1 - i) & 15];
                if (e0 == a && e1 == b) return i << 2;
                if (e0 == b && e1 == c) return (i << 2) | 1;
                if (e0 == c && e1 == a) return (i << 2) | 2;
            }
            return -1;
        }
        int VertexFifo(uint v)
        {
            for (int i = 0; i < 16; i++)
                if (vertices[(vertexOffset - 1 - i) & 15] == v) return i;
            return -1;
        }
        void PushEdge(uint a, uint b) { edges[edgeOffset] = (a, b); edgeOffset = (edgeOffset + 1) & 15; }
        void PushVertex(uint v, bool cond = true) { vertices[vertexOffset] = v; vertexOffset = (vertexOffset + (cond ? 1 : 0)) & 15; }
        void WriteIndex(uint index)
        {
            uint d = index - last;
            uint v = (d << 1) ^ (uint)((int)d >> 31);
            do
            {
                data.Add((byte)((v & 127) | (v > 127 ? 128 : 0)));
                v >>= 7;
            } while (v != 0);
            last = index;
        }

        for (int i = 0; i < indices.Length; i += 3)
        {
            int fer = EdgeFifo(indices[i], indices[i + 1], indices[i + 2]);
            if (fer >= 0 && (fer >> 2) < 15)
            {
                var order = Order[fer & 3];
                uint a = indices[i + order[0]], b = indices[i + order[1]], c = indices[i + order[2]];
                int fe = fer >> 2;
                int fc = VertexFifo(c);
                int fec = fc >= 1 && fc < fecmax ? fc : c == next ? 0 : 15;
                if (fec == 0) next++;
                if (fec == 15 && version >= 1)
                {
                    // last - 1 and last + 1 take codes 13 and 14 (strip-like runs).
                    if (c + 1 == last) { fec = 13; last = c; }
                    if (c == last + 1) { fec = 14; last = c; }
                }
                code.Add((byte)((fe << 4) | fec));
                if (fec == 15) WriteIndex(c);
                if (fec == 0 || fec >= fecmax) PushVertex(c);
                PushEdge(c, b);
                PushEdge(a, c);
            }
            else
            {
                uint a0 = indices[i], b0 = indices[i + 1], c0 = indices[i + 2];
                int rotation = b0 == next ? 1 : c0 == next ? 2 : 0;
                var order = Order[rotation];
                uint a = indices[i + order[0]], b = indices[i + order[1]], c = indices[i + order[2]];
                bool reset = false;
                if (a == 0 && b == 1 && c == 2 && next > 0 && version >= 1)
                {
                    reset = true;
                    next = 0;
                    Array.Fill(vertices, uint.MaxValue);
                }
                int fb = VertexFifo(b), fc = VertexFifo(c);
                int fea = a == next ? 0 : 15;
                if (fea == 0) next++;
                int feb = fb >= 0 && fb < 14 ? fb + 1 : b == next ? 0 : 15;
                if (feb == 0) next++;
                int fec = fc >= 0 && fc < 14 ? fc + 1 : c == next ? 0 : 15;
                if (fec == 0) next++;
                byte codeaux = (byte)((feb << 4) | fec);
                int auxIndex = Array.IndexOf(CodeAuxTable, codeaux);
                if (fea == 0 && auxIndex >= 0 && auxIndex < 14 && !reset)
                    code.Add((byte)(0xF0 | auxIndex));
                else
                {
                    code.Add((byte)(0xF0 | 14 | fea));
                    data.Add(codeaux);
                }
                if (fea == 15) WriteIndex(a);
                if (feb == 15) WriteIndex(b);
                if (fec == 15) WriteIndex(c);
                if (fea == 0 || fea == 15) PushVertex(a);
                if (feb == 0 || feb == 15) PushVertex(b);
                if (fec == 0 || fec == 15) PushVertex(c);
                PushEdge(b, a);
                PushEdge(c, b);
                PushEdge(a, c);
            }
        }

        var output = new byte[1 + code.Count + data.Count + 16];
        output[0] = 0xE0 | version;
        code.CopyTo(output, 1);
        data.CopyTo(output, 1 + code.Count);
        CodeAuxTable.CopyTo(output, 1 + code.Count + data.Count);
        return output;
    }

    /// <summary>Encode 2- or 4-byte indices as stored in a buffer.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> indexBytes, int indexSize)
    {
        if (indexSize is not (2 or 4) || indexBytes.Length % indexSize != 0)
            throw new ArgumentException("Indices are 2 or 4 bytes each.");
        var indices = new uint[indexBytes.Length / indexSize];
        for (int i = 0; i < indices.Length; i++)
            indices[i] = indexSize == 2 ? BitConverter.ToUInt16(indexBytes[(i * 2)..]) : BitConverter.ToUInt32(indexBytes[(i * 4)..]);
        return Encode(indices);
    }
}
