using System.Numerics;

namespace Source2.Compiler.Meshes;

/// <summary>
/// meshoptimizer's vertex buffer codec (formats v0, header 0xA0, and v1, 0xA1), the compression
/// of a model's vertex buffers (CTRL <c>m_bMeshoptCompressed</c>). Decoding takes either; the
/// encoder writes v1 as resourcecompiler does (docs/MESHES.md).
/// </summary>
public static class MeshoptVertexCodec
{
    private const int BlockSizeBytes = 8192, BlockMaxSize = 256, GroupSize = 16, GroupDecodeLimit = 24;
    private static readonly int[] BitsV0 = [0, 2, 4, 8];
    private static readonly int[] BitsV1 = [0, 1, 2, 4, 8];
    // The bit table of each v1 control value: the widths from that one on.
    private static readonly int[][] BitsV1Tables = [[0, 1, 2, 4, 8], [1, 2, 4, 8]];

    internal static int BlockSize(int vertexSize) => Math.Min((BlockSizeBytes / vertexSize) & ~(GroupSize - 1), BlockMaxSize);

    // ---- decode ----

    /// <summary>Decode <paramref name="count"/> vertices of <paramref name="size"/> bytes.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> data, int count, int size)
    {
        if (size <= 0 || size > 256 || size % 4 != 0)
            throw new InvalidDataException($"Vertex size {size} is not a multiple of 4 up to 256.");
        if (data.Length < 1 || (data[0] & 0xF0) != 0xA0 || (data[0] & 0x0F) > 1)
            throw new InvalidDataException("Not a meshopt vertex buffer (v0 or v1).");
        int version = data[0] & 0x0F;
        int tail = size + (version == 0 ? 0 : size / 4);
        int tailPadded = Math.Max(tail, version == 0 ? 32 : 24);
        var body = data[1..];
        if (body.Length < tailPadded)
            throw new InvalidDataException("Vertex buffer shorter than its tail.");
        // Every block takes at least one header byte per lane group, so the count cannot exceed
        // what the buffer could encode; a count from a hostile descriptor is not an allocation.
        int blockSize = BlockSize(size);
        if (count < 0 || (long)count > (long)(body.Length - tailPadded + 1) * blockSize)
            throw new InvalidDataException($"{count} vertices cannot be in a {data.Length}-byte buffer.");
        var last = body.Slice(body.Length - tail, size).ToArray();
        var channels = version == 0 ? null : body.Slice(body.Length - tail + size, size / 4).ToArray();
        var output = new byte[(long)count * size];
        var buffer = new byte[BlockMaxSize * 4];
        int pos = 0;
        for (int offset = 0; offset < count; offset += blockSize)
        {
            int n = Math.Min(blockSize, count - offset);
            pos = DecodeBlock(body, pos, body.Length - tailPadded, output.AsSpan(offset * size), n, size, last, channels, version, buffer);
        }
        if (pos != body.Length - tailPadded)
            throw new InvalidDataException("Vertex buffer has bytes past its last block.");
        return output;
    }

    private static int DecodeBlock(ReadOnlySpan<byte> data, int pos, int end, Span<byte> output, int count, int size,
        byte[] last, byte[]? channels, int version, byte[] buffer)
    {
        int aligned = (count + GroupSize - 1) & ~(GroupSize - 1);
        int controlSize = version == 0 ? 0 : size / 4;
        if (pos + controlSize > end)
            throw new InvalidDataException("Vertex block control bytes are truncated.");
        int control = pos;
        pos += controlSize;
        for (int k = 0; k < size; k += 4)
        {
            byte ctrlByte = version == 0 ? (byte)0 : data[control + k / 4];
            var scratch = new byte[aligned];
            for (int j = 0; j < 4; j++)
            {
                int ctrl = (ctrlByte >> (j * 2)) & 3;
                var lane = buffer.AsSpan(j * count, count);
                if (ctrl == 3)
                {
                    if (pos + count > end) throw new InvalidDataException("Literal vertex bytes are truncated.");
                    data.Slice(pos, count).CopyTo(lane);
                    pos += count;
                }
                else if (ctrl == 2)
                    lane.Clear();
                else
                {
                    pos = DecodeBytes(data, pos, end, scratch, version == 0 ? BitsV0 : BitsV1Tables[ctrl]);
                    scratch.AsSpan(0, count).CopyTo(lane);
                }
            }
            int channel = version == 0 ? 0 : channels![k / 4];
            int width = (channel & 3) switch { 0 => 1, 1 => 2, 2 => 4, _ => throw new InvalidDataException("Invalid vertex channel mode.") };
            int rot = (32 - (channel >> 4)) & 31;
            for (int b = 0; b < 4; b += width)
            {
                uint p = 0;
                for (int i = 0; i < width; i++) p |= (uint)last[k + b + i] << (8 * i);
                for (int v = 0; v < count; v++)
                {
                    uint d = 0;
                    for (int i = 0; i < width; i++) d |= (uint)buffer[(b + i) * count + v] << (8 * i);
                    uint value = width switch
                    {
                        1 => (byte)(Unzigzag(d) + p),
                        2 => (ushort)(Unzigzag(d, 16) + p),
                        _ => BitOperations.RotateLeft(d, rot) ^ p,
                    };
                    for (int i = 0; i < width; i++) output[v * size + k + b + i] = (byte)(value >> (8 * i));
                    p = value;
                }
            }
        }
        output.Slice((count - 1) * size, size).CopyTo(last);
        return pos;
    }

    private static uint Unzigzag(uint v, int bits = 8)
    {
        uint mask = bits == 8 ? 0xFFu : 0xFFFFu;
        v &= mask;
        return ((0 - (v & 1)) ^ (v >> 1)) & mask;
    }

    private static int DecodeBytes(ReadOnlySpan<byte> data, int pos, int end, Span<byte> buffer, int[] bits)
    {
        int groups = buffer.Length / GroupSize;
        int header = pos;
        pos += (groups + 3) / 4;
        for (int g = 0; g < groups; g++)
        {
            if (data.Length - pos < GroupDecodeLimit)
                throw new InvalidDataException("Vertex byte group is truncated.");
            int bitk = (data[header + g / 4] >> ((g % 4) * 2)) & 3;
            pos = DecodeGroup(data, pos, buffer.Slice(g * GroupSize, GroupSize), bits[bitk]);
        }
        return pos;
    }

    private static int DecodeGroup(ReadOnlySpan<byte> data, int pos, Span<byte> group, int bits)
    {
        if (bits == 0) { group.Clear(); return pos; }
        if (bits == 8) { data.Slice(pos, GroupSize).CopyTo(group); return pos + GroupSize; }
        int perByte = 8 / bits, selectorBytes = GroupSize / perByte;
        int extra = pos + selectorBytes;
        int sentinel = (1 << bits) - 1;
        for (int i = 0; i < GroupSize; i++)
        {
            int byteIndex = i / perByte, slot = i % perByte;
            // 1-bit groups pack their values from the low bit; wider ones from the high bits.
            int shift = bits == 1 ? slot : 8 - bits * (slot + 1);
            int v = (data[pos + byteIndex] >> shift) & sentinel;
            group[i] = v == sentinel ? data[extra++] : (byte)v;
        }
        return extra;
    }

    // ---- encode (v1) ----

    /// <summary>
    /// Encode as format v1 the way resourcecompiler does: each 4-byte channel's mode estimated
    /// (<see cref="EstimateChannel"/>), then <see cref="EncodeV1"/>.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> vertices, int count, int size)
    {
        if (size <= 0 || size > 256 || size % 4 != 0 || vertices.Length != count * size)
            throw new ArgumentException("Vertex data and size do not fit.");
        var data = vertices.ToArray();
        var channels = new byte[size / 4];
        for (int k = 0; k < size; k += 4)
            channels[k / 4] = EstimateChannel(data, count, size, k, EstimateRotate(data, count, size, k));
        return EncodeV1(vertices, count, size, channels);
    }

    // A width estimate for a byte of changed bits: 0, 2, 4 or 8.
    private static int EstimateBits(byte v) => v <= 15 ? (v <= 3 ? (v == 0 ? 0 : 2) : 4) : 8;

    // The xor rotation (0..7): over groups of 16 vertices, OR the bits that change from vertex to
    // vertex, rotate, and add the estimated width of each byte; the smallest total wins, the
    // lowest rotation on a tie.
    internal static int EstimateRotate(byte[] data, int count, int size, int k)
    {
        Span<long> sizes = stackalloc long[8];
        sizes.Clear();
        uint last = BitConverter.ToUInt32(data, k);
        for (int i = 0; i < count; i += 16)
        {
            uint changed = 0;
            for (int j = 0; j < 16 && i + j < count; j++)
            {
                uint v = BitConverter.ToUInt32(data, (i + j) * size + k);
                changed |= v ^ last;
                last = v;
            }
            for (int r = 0; r < 8; r++)
            {
                uint bits = BitOperations.RotateLeft(changed, r);
                sizes[r] += EstimateBits((byte)bits) + EstimateBits((byte)(bits >> 8)) + EstimateBits((byte)(bits >> 16)) + EstimateBits((byte)(bits >> 24));
            }
        }
        int best = 0;
        for (int r = 1; r < 8; r++)
            if (sizes[r] < sizes[best])
                best = r;
        return best;
    }

    // The channel mode: every third block, each mode's deltas from the vertex before the block,
    // each 16-byte group costed at its best of 1, 2, 4 and 8 bits; the smallest total wins, the
    // lowest mode on a tie. 271 of 271 of RC's channels (docs/MESHES.md).
    internal static byte EstimateChannel(byte[] data, int count, int size, int k, int rot)
    {
        Span<long> sizes = stackalloc long[3];
        sizes.Clear();
        int blockSize = BlockSize(size);
        var block = new byte[BlockMaxSize + GroupSize];
        for (int i = 0; i < count; i += blockSize * 3)
        {
            int n = Math.Min(blockSize, count - i);
            int previous = (i == 0 ? 0 : i - 1) * size;
            for (int channel = 0; channel < 3; channel++)
            {
                int width = channel == 0 ? 1 : channel == 1 ? 2 : 4;
                for (int j = 0; j < 4; j++)
                {
                    Array.Clear(block);
                    int lane = j % width, b0 = j - lane;
                    uint p = 0;
                    for (int t = 0; t < width; t++) p |= (uint)data[previous + k + b0 + t] << (8 * t);
                    for (int v = 0; v < n; v++)
                    {
                        uint value = 0;
                        for (int t = 0; t < width; t++) value |= (uint)data[(i + v) * size + k + b0 + t] << (8 * t);
                        uint d = width switch
                        {
                            1 => Zigzag((byte)(value - p), 8),
                            2 => Zigzag((ushort)(value - p), 16),
                            _ => BitOperations.RotateLeft(value ^ p, rot),
                        };
                        block[v] = (byte)(d >> (8 * lane));
                        p = value;
                    }
                    for (int g = 0; g < n; g += GroupSize)
                    {
                        var group = block.AsSpan(g, GroupSize);
                        sizes[channel] += Math.Min(Math.Min(MeasureGroup(group, 1), MeasureGroup(group, 2)),
                            Math.Min(MeasureGroup(group, 4), MeasureGroup(group, 8)));
                    }
                }
            }
        }
        int best = 0;
        for (int c = 1; c < 3; c++)
            if (sizes[c] < sizes[best])
                best = c;
        return (byte)(best == 2 ? best | (rot << 4) : best);
    }

    /// <summary>
    /// Encode as format v1 with the given per-channel modes (one byte per 4-byte channel: low two
    /// bits 0 byte deltas, 1 16-bit deltas, 2 32-bit xor; high nibble the xor rotation).
    /// </summary>
    public static byte[] EncodeV1(ReadOnlySpan<byte> vertices, int count, int size, byte[] channels)
    {
        if (count == 0)
            throw new ArgumentException("A vertex buffer needs at least one vertex.");
        if (size <= 0 || size > 256 || size % 4 != 0 || vertices.Length != count * size || channels.Length != size / 4)
            throw new ArgumentException("Vertex data, size or channel modes do not fit.");
        var output = new Output(vertices.Length + 64);
        output.Add(0xA1);
        var last = vertices[..size].ToArray();
        int blockSize = BlockSize(size);
        var lanes = new byte[4][];
        for (int j = 0; j < 4; j++) lanes[j] = new byte[(blockSize + GroupSize - 1) & ~(GroupSize - 1)];
        for (int offset = 0; offset < count; offset += blockSize)
        {
            int n = Math.Min(blockSize, count - offset);
            EncodeBlock(output, vertices.Slice(offset * size, n * size), n, size, last, channels, lanes);
            vertices.Slice((offset + n - 1) * size, size).CopyTo(last);
        }
        // Tail: the first vertex, the channel modes, padded to the minimum tail size.
        int tail = size + size / 4;
        for (int i = tail; i < 24; i++) output.Add(0);
        output.Write(vertices[..size]);
        output.Write(channels);
        return output.ToArray();
    }

    /// <summary>A growable byte buffer an encoder appends to and patches header bytes in.</summary>
    private sealed class Output(int capacity)
    {
        private byte[] _bytes = new byte[Math.Max(capacity, 16)];

        public int Length { get; private set; }

        public void Add(byte value)
        {
            Reserve(1);
            _bytes[Length++] = value;
        }

        public void Write(ReadOnlySpan<byte> bytes)
        {
            Reserve(bytes.Length);
            bytes.CopyTo(_bytes.AsSpan(Length));
            Length += bytes.Length;
        }

        public void Or(int at, byte bits) => _bytes[at] |= bits;

        public byte[] ToArray() => _bytes.AsSpan(0, Length).ToArray();

        private void Reserve(int more)
        {
            if (Length + more > _bytes.Length)
                Array.Resize(ref _bytes, Math.Max(_bytes.Length * 2, Length + more));
        }
    }

    private static void EncodeBlock(Output output, ReadOnlySpan<byte> vertices, int count, int size, byte[] last, byte[] channels, byte[][] lanes)
    {
        int aligned = (count + GroupSize - 1) & ~(GroupSize - 1);
        int controlAt = output.Length;
        for (int i = 0; i < size / 4; i++) output.Add(0);
        for (int k = 0; k < size; k += 4)
        {
            int channel = channels[k / 4];
            int width = (channel & 3) switch { 0 => 1, 1 => 2, 2 => 4, _ => throw new ArgumentException("Invalid channel mode.") };
            int rot = channel >> 4;
            for (int j = 0; j < 4; j++) Array.Clear(lanes[j], 0, aligned);
            for (int b = 0; b < 4; b += width)
            {
                uint p = 0;
                for (int i = 0; i < width; i++) p |= (uint)last[k + b + i] << (8 * i);
                for (int v = 0; v < count; v++)
                {
                    uint value = 0;
                    for (int i = 0; i < width; i++) value |= (uint)vertices[v * size + k + b + i] << (8 * i);
                    uint d = width switch
                    {
                        1 => Zigzag((byte)(value - p), 8),
                        2 => Zigzag((ushort)(value - p), 16),
                        _ => BitOperations.RotateLeft(value ^ p, rot),
                    };
                    for (int i = 0; i < width; i++) lanes[b + i][v] = (byte)(d >> (8 * i));
                    p = value;
                }
            }
            byte ctrlByte = 0;
            for (int j = 0; j < 4; j++)
            {
                var lane = lanes[j].AsSpan(0, aligned);
                int ctrl = ChooseControl(lane, count);
                ctrlByte |= (byte)(ctrl << (j * 2));
                if (ctrl == 3)
                    output.Write(lane[..count]);
                else if (ctrl != 2)
                    EncodeBytes(output, lane, BitsV1Tables[ctrl]);
            }
            output.Or(controlAt + k / 4, ctrlByte);
        }
    }

    private static uint Zigzag(uint v, int bits)
    {
        uint mask = bits == 8 ? 0xFFu : 0xFFFFu;
        uint sign = (v >> (bits - 1)) & 1;
        return ((v << 1) ^ (0 - sign)) & mask;
    }

    // 2 when every byte is zero; else the smaller of the two bit tables, literal when that is no
    // smaller than the raw bytes. Inferred from RC's buffers (docs/MESHES.md).
    internal static int ChooseControl(ReadOnlySpan<byte> lane, int count)
    {
        if (lane[..count].IndexOfAnyExcept((byte)0) < 0)
            return 2;
        int s0 = MeasureBytes(lane, BitsV1Tables[0]), s1 = MeasureBytes(lane, BitsV1Tables[1]);
        int best = s1 <= s0 ? 1 : 0, size = Math.Min(s0, s1);
        return size < count ? best : 3;
    }

    internal static int MeasureBytes(ReadOnlySpan<byte> lane, int[] bits)
    {
        int groups = lane.Length / GroupSize, total = (groups + 3) / 4, last = -1;
        for (int g = 0; g < groups; g++)
        {
            var (k, size) = BestGroup(lane.Slice(g * GroupSize, GroupSize), bits, last);
            total += size;
            last = bits[k];
        }
        return total;
    }

    // The cheapest width, starting from the widest; a tie goes to the previous group's width
    // unless the widest (literal bytes) holds it: "favor consistent bit selection across groups,
    // but never replace literals" (meshoptimizer's v1 encoder).
    private static (int Bitk, int Size) BestGroup(ReadOnlySpan<byte> group, int[] bits, int lastBits)
    {
        int bestK = 3, best = MeasureGroup(group, bits[3]);
        for (int k = 0; k < 3; k++)
        {
            int s = MeasureGroup(group, bits[k]);
            if (s < best || (s == best && bits[k] == lastBits && bits[bestK] != 8))
            {
                best = s;
                bestK = k;
            }
        }
        return (bestK, best);
    }

    private static int MeasureGroup(ReadOnlySpan<byte> group, int bits)
    {
        if (bits == 0) return group.IndexOfAnyExcept((byte)0) < 0 ? 0 : GroupSize * 2 + 1000;
        if (bits == 8) return GroupSize;
        int sentinel = (1 << bits) - 1, size = GroupSize * bits / 8;
        foreach (var b in group) if (b >= sentinel) size++;
        return size;
    }

    private static void EncodeBytes(Output output, ReadOnlySpan<byte> lane, int[] bits)
    {
        int groups = lane.Length / GroupSize;
        int headerAt = output.Length;
        for (int i = 0; i < (groups + 3) / 4; i++) output.Add(0);
        int last = -1;
        for (int g = 0; g < groups; g++)
        {
            var group = lane.Slice(g * GroupSize, GroupSize);
            var (bitk, _) = BestGroup(group, bits, last);
            output.Or(headerAt + g / 4, (byte)(bitk << ((g % 4) * 2)));
            EncodeGroup(output, group, bits[bitk]);
            last = bits[bitk];
        }
    }

    private static void EncodeGroup(Output output, ReadOnlySpan<byte> group, int bits)
    {
        if (bits == 0) return;
        if (bits == 8) { output.Write(group); return; }
        int perByte = 8 / bits, sentinel = (1 << bits) - 1;
        Span<byte> selectors = stackalloc byte[GroupSize / perByte];
        selectors.Clear();
        Span<byte> extra = stackalloc byte[GroupSize];
        int extras = 0;
        for (int i = 0; i < GroupSize; i++)
        {
            int v = group[i] >= sentinel ? sentinel : group[i];
            if (group[i] >= sentinel) extra[extras++] = group[i];
            int byteIndex = i / perByte, slot = i % perByte;
            int shift = bits == 1 ? slot : 8 - bits * (slot + 1);
            selectors[byteIndex] |= (byte)(v << shift);
        }
        output.Write(selectors);
        output.Write(extra[..extras]);
    }
}
