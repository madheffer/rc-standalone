using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Source2.Compiler.Kv3;

/// <summary>
/// The sections of a binary KV3 block, filled by a writer and laid out, compressed and framed here
/// as tier0's SaveKV3 does (1800c7220). Both writers share it: <see cref="Kv3V5Writer"/> (from a
/// <c>KVObject</c> tree) and <see cref="Kv3Tree"/> (from the lossless nodes).
/// <para>Version 5: buffer 1 holds the strings and the auxiliary lanes, buffer 2 the object lengths,
/// the main lanes, the type bytes, the blob lengths and the trailer. Version 4 (what most stock
/// materials and models still carry): one buffer, the lanes (object lengths among the 4-byte
/// values, the auxiliary lanes the same as the main ones) and then the strings, the types, the
/// blob lengths and the trailer.</para>
/// </summary>
internal sealed class Kv3Block
{
    public const uint Magic = 0x4B563305;
    public const uint MagicV4 = 0x4B563304;
    public const int HeaderSize = 120;
    private const int HeaderSizeV4 = 72;
    public const uint Trailer = 0xFFEEDD00;
    private const int Lz4FrameSize = 16384;

    /// <summary>The 1, 2, 4 and 8 byte value lanes of one buffer.</summary>
    public sealed class Lanes
    {
        public readonly MemoryStream B1 = new(), B2 = new(), B4 = new(), B8 = new();

        /// <summary>Append the low <paramref name="width"/> bytes of <paramref name="value"/> to that lane.</summary>
        public void Put(ulong value, int width)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            (width switch { 1 => B1, 2 => B2, 4 => B4, _ => B8 }).Write(bytes[..width]);
        }
    }

    /// <summary>A block of binary version <paramref name="version"/> (4 or 5).</summary>
    public Kv3Block(int version = 5)
    {
        if (version is not (4 or 5))
            throw new NotSupportedException($"Binary KV3 version {version} is not written here (4 and 5 are).");
        Version = version;
        Main = new();
        Aux = version == 4 ? Main : new();
    }

    /// <summary>The binary version written.</summary>
    public int Version { get; }

    /// <summary>The lanes values go to; a writer swaps the two around an auxiliary-buffer array
    /// (one and the same in version 4).</summary>
    public Lanes Main, Aux;

    public readonly MemoryStream Types = new();
    public readonly List<int> ObjectLengths = [];
    public readonly List<byte[]> Blobs = [];

    /// <summary>The string table, by id.</summary>
    public IReadOnlyList<string> Strings { get; set; } = [];

    /// <summary>Nodes with a type byte of their own; objects; arrays; the arrays (and their
    /// elements) counted in buffer 2's two extra header counters.</summary>
    public int Nodes, Objects, Arrays, ArraysMain, ElementsMain;

    /// <summary>An object's member count: its own list in version 5, a 4-byte value in version 4.</summary>
    public void ObjectLength(int count)
    {
        if (Version >= 5)
            ObjectLengths.Add(count);
        else
            Main.Put((uint)count, 4);
    }

    /// <summary>A node's type byte, and its flag byte when it has one.</summary>
    public void Type(int type, byte flag)
    {
        if (flag == 0)
        {
            Types.WriteByte((byte)type);
            return;
        }
        Types.WriteByte((byte)(type | 0x80));
        Types.WriteByte(flag);
    }

    /// <summary>
    /// The two buffers before compression and the header fields that do not depend on it: string
    /// and lane counts, the object and array counters (each saturating at 65535, as Valve's maps
    /// store them), blob counts, buffer sizes, node and object counts.
    /// </summary>
    public (byte[] Buffer1, byte[] Buffer2, int[] Header) Layout()
    {
        var b1 = new MemoryStream();
        var stringBytes = 0;
        foreach (var s in Strings)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            b1.Write(bytes);
            b1.WriteByte(0);
            stringBytes += bytes.Length + 1;
        }
        Aux.B1.WriteTo(b1);
        Append(b1, Aux.B2, 2);
        Pad(b1, 4);
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(count, Strings.Count);
        b1.Write(count);
        Aux.B4.WriteTo(b1);
        Append(b1, Aux.B8, 8);

        var b2 = new MemoryStream();
        foreach (var n in ObjectLengths)
            Write32(b2, (uint)n);
        Main.B1.WriteTo(b2);
        Append(b2, Main.B2, 2);
        Append(b2, Main.B4, 4);
        Append(b2, Main.B8, 8);
        Types.WriteTo(b2);
        foreach (var blob in Blobs)
            Write32(b2, (uint)blob.Length);
        Write32(b2, Trailer);

        var h = new int[30];
        h[7] = stringBytes + (int)Aux.B1.Length;
        h[8] = 1 + (int)(Aux.B4.Length / 4);
        h[9] = (int)(Aux.B8.Length / 8);
        h[10] = (int)Types.Length;
        h[11] = Math.Min(Objects, 0xFFFF) | (Math.Min(Arrays, 0xFFFF) << 16);
        h[14] = Blobs.Count;
        h[15] = Blobs.Sum(b => b.Length);
        h[16] = (int)(Aux.B2.Length / 2);
        h[18] = (int)b1.Length;
        h[20] = (int)b2.Length;
        h[22] = (int)Main.B1.Length;
        h[23] = (int)(Main.B2.Length / 2);
        h[24] = (int)(Main.B4.Length / 4);
        h[25] = (int)(Main.B8.Length / 8);
        h[26] = Nodes;
        h[27] = Objects;
        h[28] = ArraysMain;
        h[29] = ElementsMain;
        return (b1.ToArray(), b2.ToArray(), h);
    }

    /// <summary>
    /// The complete block under <paramref name="format"/>: compressed with <paramref name="method"/>,
    /// or with null as binary_auto chooses, which is what resourcecompiler writes.
    /// </summary>
    public byte[] Write(Guid format, Kv3Compression? method)
    {
        if (Version == 4)
            return WriteV4(format, method);
        var (raw1, raw2, h) = Layout();
        var p = Pack(raw1, raw2, Blobs, method);
        var compressed = p.Method != Kv3Compression.Uncompressed;
        h[0] = unchecked((int)Magic);
        h[5] = (int)p.Method;
        h[6] = p.Method == Kv3Compression.Lz4 ? Lz4FrameSize << 16 : 0;
        h[12] = raw1.Length + p.Raw2.Length;
        h[13] = compressed ? p.StoredSize : raw1.Length + p.Raw2.Length;
        h[17] = p.FrameSizeBytes;
        h[19] = compressed ? p.Out1.Length : 0;
        h[20] = p.Raw2.Length;
        h[21] = compressed ? p.Out2.Length : 0;

        var o = new MemoryStream(HeaderSize + p.Out1.Length + p.Out2.Length + p.BlobsOut.Length + 4);
        var head = new byte[HeaderSize];
        for (var i = 0; i < h.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(i * 4), h[i]);
        format.TryWriteBytes(head.AsSpan(4, 16));
        o.Write(head);
        o.Write(p.Out1);
        o.Write(p.Out2);
        if (Blobs.Count > 0)
        {
            o.Write(p.BlobsOut);
            Write32(o, Trailer);
        }
        return o.ToArray();
    }

    // Version 4: one buffer, compressed whole (its LZ4 blob frame sizes after the trailer), behind a
    // 72-byte header.
    private byte[] WriteV4(Guid format, Kv3Compression? method)
    {
        var buffer = new MemoryStream();
        Main.B1.WriteTo(buffer);
        Append(buffer, Main.B2, 2);
        Pad(buffer, 4);
        Write32(buffer, (uint)Strings.Count);
        Main.B4.WriteTo(buffer);
        Pad(buffer, 8);   // aligned to 8 even with no 8-byte values
        Main.B8.WriteTo(buffer);
        var typesAt = buffer.Length;
        foreach (var s in Strings)
        {
            buffer.Write(Encoding.UTF8.GetBytes(s));
            buffer.WriteByte(0);
        }
        Types.WriteTo(buffer);
        var countTypes = (int)(buffer.Length - typesAt);
        foreach (var blob in Blobs)
            Write32(buffer, (uint)blob.Length);
        Write32(buffer, Trailer);

        var p = Pack(null, buffer.ToArray(), Blobs, method);
        var compressed = p.Method != Kv3Compression.Uncompressed;
        var o = new MemoryStream(HeaderSizeV4 + p.Out2.Length + p.BlobsOut.Length + 4);
        Write32(o, MagicV4);
        Span<byte> guid = stackalloc byte[16];
        format.TryWriteBytes(guid);
        o.Write(guid);
        Write32(o, (uint)p.Method);
        Write32(o, p.Method == Kv3Compression.Lz4 ? (uint)Lz4FrameSize << 16 : 0);
        Write32(o, (uint)Main.B1.Length);
        Write32(o, (uint)(1 + Main.B4.Length / 4));
        Write32(o, (uint)(Main.B8.Length / 8));
        Write32(o, (uint)countTypes);
        Write32(o, (uint)(Math.Min(Objects, 0xFFFF) | (Math.Min(Arrays, 0xFFFF) << 16)));
        Write32(o, (uint)p.Raw2.Length);
        Write32(o, (uint)(compressed ? p.StoredSize : p.Raw2.Length));
        Write32(o, (uint)Blobs.Count);
        Write32(o, (uint)Blobs.Sum(b => b.Length));
        Write32(o, (uint)(Main.B2.Length / 2));
        Write32(o, 0);   // the blob frame sizes' length: version 4 leaves it 0 though they follow the trailer
        o.Write(p.Out2);
        if (Blobs.Count > 0)
        {
            o.Write(p.BlobsOut);
            Write32(o, Trailer);
        }
        return o.ToArray();
    }

    private static void Append(MemoryStream to, MemoryStream lane, int alignment)
    {
        if (lane.Length == 0)
            return;
        Pad(to, alignment);
        lane.WriteTo(to);
    }

    private static void Pad(MemoryStream s, int alignment)
    {
        while (s.Length % alignment != 0)
            s.WriteByte(0);
    }

    private static void Write32(MemoryStream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        s.Write(b);
    }

    // ---- compression ----

    /// <summary>A block's sections after binary_auto (or a forced method) has had its say.</summary>
    /// <param name="Method">The method the block is stored with.</param>
    /// <param name="Raw2">Buffer 2 as stored before compression: with LZ4 and blobs, the blob
    /// frames' compressed sizes (u16 each) follow its trailer.</param>
    /// <param name="Out1">Buffer 1 as stored.</param>
    /// <param name="Out2">Buffer 2 as stored.</param>
    /// <param name="BlobsOut">The blobs as stored.</param>
    /// <param name="FrameSizeBytes">Bytes of LZ4 blob frame sizes in <paramref name="Raw2"/>.</param>
    private readonly record struct Packed(Kv3Compression Method, byte[] Raw2, byte[] Out1, byte[] Out2, byte[] BlobsOut, int FrameSizeBytes)
    {
        /// <summary>The header's stored-size field (offset 52): Zstd counts the blobs, LZ4 leaves them out.</summary>
        public int StoredSize => Out1.Length + Out2.Length + (Method == Kv3Compression.Zstd ? BlobsOut.Length : 0);
    }

    // 1800c8790 / 1800c8d80 keep the compressed block when raw / 20 <= raw - compressed.
    private static bool Saves(int raw, int compressed) => raw / 20 <= raw - compressed;

    /// <summary>
    /// tier0's compression stage (1800c7220): <paramref name="method"/>, or with null raw under 256
    /// bytes of buffers and blobs, LZ4 under 0x80001 and zstd from there, kept only if it saves a
    /// twentieth.
    /// </summary>
    private static Packed Pack(byte[]? raw1, byte[] raw2, IReadOnlyList<byte[]> blobs, Kv3Compression? method)
    {
        var blobsRaw = blobs.SelectMany(x => x).ToArray();
        var raw1Length = raw1?.Length ?? 0;
        var total = raw1Length + raw2.Length + blobsRaw.Length;
        var chosen = method ?? (total < 0x100 ? Kv3Compression.Uncompressed
            : total < 0x80001 ? Kv3Compression.Lz4 : Kv3Compression.Zstd);
        if (chosen == Kv3Compression.Lz4)
        {
            // Blobs (1800c7080): one LZ4 HC level 12 stream over every blob in frames of at most
            // 16384 bytes, a blob's last frame short; each frame's compressed size (u16) follows
            // buffer 2's trailer.
            var withSizes = new MemoryStream();
            withSizes.Write(raw2);
            var packed = new MemoryStream();
            var frameSizes = 0;
            if (blobs.Count > 0)
            {
                using var ring = new Lz4BlobRing();
                Span<byte> size = stackalloc byte[2];
                foreach (var blob in blobs)
                    for (var at = 0; at < blob.Length; at += Lz4FrameSize)
                    {
                        var frame = ring.Compress(blob.AsSpan(at, Math.Min(Lz4FrameSize, blob.Length - at)));
                        BinaryPrimitives.WriteUInt16LittleEndian(size, (ushort)frame.Length);
                        withSizes.Write(size);
                        frameSizes += 2;
                        packed.Write(frame);
                    }
            }
            var lzRaw2 = withSizes.ToArray();
            var lz1 = raw1 is null ? [] : Compression.Lz4.Compress(raw1);
            var lz2 = Compression.Lz4.Compress(lzRaw2);
            var lzBlobs = packed.ToArray();
            if (method != null || Saves(raw1Length + lzRaw2.Length + blobsRaw.Length, lz1.Length + lz2.Length + lzBlobs.Length))
                return new Packed(Kv3Compression.Lz4, lzRaw2, lz1, lz2, lzBlobs, frameSizes);
        }
        else if (chosen == Kv3Compression.Zstd)
        {
            var (z1, z2, zBlobs) = Zstd(raw1, raw2, blobs);
            if (method != null || Saves(total, z1.Length + z2.Length + zBlobs.Length))
                return new Packed(Kv3Compression.Zstd, raw2, z1, z2, zBlobs, 0);
        }
        return new Packed(Kv3Compression.Uncompressed, raw2, raw1 ?? [], raw2, blobsRaw, 0);
    }

    // 1800c8d80: one context at level 7 with checksums, streamed (ZSTD_compressStream2) through a
    // 64 KB output buffer: buffer 1 and buffer 2 each ended as a frame of their own, the blobs
    // continued one by one and ended as a third. The streaming path matters: a one-shot compress of
    // the same input gives other bytes.
    private static unsafe (byte[], byte[], byte[]) Zstd(byte[]? raw1, byte[] raw2, IReadOnlyList<byte[]> blobs)
    {
        var cctx = ZstdSharp.Unsafe.Methods.ZSTD_createCCtx();
        try
        {
            ZstdSharp.Unsafe.Methods.ZSTD_CCtx_setParameter(cctx, ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_compressionLevel, 7);
            ZstdSharp.Unsafe.Methods.ZSTD_CCtx_setParameter(cctx, ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
            // Today's tier0 is zstd 1.5.7 with its defaults, as ZstdSharp 0.8.8 (a 730 KB .vdata compiled by
            // resourcecompiler on 2026-10-13, byte for byte). Older stock files were written by 1.5.5/1.5.6
            // (no pre-block splitter) or 1.5.4 and before; a few large blocks in pak01 differ for that reason.
            // Over 1 MB of buffers and blobs tier0 asks for worker threads; zstd's multithreaded frames do
            // not depend on how many.
            if ((raw1?.Length ?? 0) + raw2.Length + blobs.Sum(x => x.Length) > 0x100000)
                ZstdSharp.Unsafe.Methods.ZSTD_CCtx_setParameter(cctx, ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_nbWorkers, 1);
            var chunk = new byte[0x10000];
            void Stream(MemoryStream to, byte[] src, ZstdSharp.Unsafe.ZSTD_EndDirective end)
            {
                fixed (byte* ps = src)
                fixed (byte* po = chunk)
                {
                    var input = new ZstdSharp.Unsafe.ZSTD_inBuffer_s { src = ps, size = (nuint)src.Length, pos = 0 };
                    while (true)
                    {
                        var output = new ZstdSharp.Unsafe.ZSTD_outBuffer_s { dst = po, size = (nuint)chunk.Length, pos = 0 };
                        var left = ZstdSharp.Unsafe.Methods.ZSTD_compressStream2(cctx, &output, &input, end);
                        if (ZstdSharp.Unsafe.Methods.ZSTD_isError(left))
                            throw new InvalidOperationException("zstd compression failed");
                        to.Write(chunk, 0, (int)output.pos);
                        if (end == ZstdSharp.Unsafe.ZSTD_EndDirective.ZSTD_e_end ? left == 0 : input.pos == input.size)
                            break;
                    }
                }
            }
            var a = new MemoryStream();
            if (raw1 is not null)
                Stream(a, raw1, ZstdSharp.Unsafe.ZSTD_EndDirective.ZSTD_e_end);
            var b = new MemoryStream();
            Stream(b, raw2, ZstdSharp.Unsafe.ZSTD_EndDirective.ZSTD_e_end);
            var c = new MemoryStream();
            if (blobs.Count > 0)
            {
                // An empty blob is not handed to the stream at all, so a stream of empty blobs ends
                // as a frame of known size 0.
                foreach (var blob in blobs.Where(x => x.Length > 0))
                    Stream(c, blob, ZstdSharp.Unsafe.ZSTD_EndDirective.ZSTD_e_continue);
                Stream(c, [], ZstdSharp.Unsafe.ZSTD_EndDirective.ZSTD_e_end);
            }
            return (a.ToArray(), b.ToArray(), c.ToArray());
        }
        finally
        {
            ZstdSharp.Unsafe.Methods.ZSTD_freeCCtx(cctx);
        }
    }

    /// <summary>
    /// 1800c7080's blob stream: an LZ4 HC level 12 stream whose input is copied into a 0x14000-byte
    /// ring at the running offset, which goes back to 0 once past 0xffff, each frame compressed with
    /// LZ4_compress_HC_continue (so a wrapped frame shortens the dictionary it overwrites). tier0's
    /// lz4 is 1.9.3 or later (<see cref="Compression.Lz4HcStream"/>): 1.9.2 gets the frames after a
    /// wrap wrong.
    /// </summary>
    private sealed unsafe class Lz4BlobRing : IDisposable
    {
        private const int RingSize = 0x14000;
        private readonly byte* _ring = (byte*)NativeMemory.AllocZeroed(RingSize);
        private readonly Compression.Lz4HcStream _stream = new(12);
        private int _offset;

        public byte[] Compress(ReadOnlySpan<byte> frame)
        {
            frame.CopyTo(new Span<byte>(_ring + _offset, frame.Length));
            var output = new byte[0x4050];
            int n;
            fixed (byte* o = output)
                n = _stream.CompressContinue(_ring + _offset, o, frame.Length, output.Length);
            if (n <= 0)
                throw new InvalidOperationException("LZ4 blob frame failed");
            _offset += frame.Length;
            if (_offset > 0xffff)
                _offset = 0;
            return output.AsSpan(0, n).ToArray();
        }

        public void Dispose()
        {
            NativeMemory.Free(_ring);
            _stream.Dispose();
        }
    }
}
