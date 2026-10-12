using System.Text;
using K4os.Compression.LZ4;
using System.Reflection;
using System.Runtime.InteropServices;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;

namespace Source2.Compiler;

/// <summary>
/// Binary KV3 version 5 as tier0's SaveKV3 writes it (tier0 1800c7220 and the
/// node writers 1800c6c20 / 1800c64b0 / 1800c6070 / 1800c5ea0), so a block
/// comes out byte for byte as resourcecompiler's:
/// <list type="bullet">
/// <item>ints that fit 32 bits are INT32 / UINT32, 0 and 1 the zero and one
/// types (an unsigned 0 or 1 too), doubles 0 and 1 likewise;</item>
/// <item>a non-empty array whose elements share one kind is typed: under 256
/// elements a byte length, ints, uints and doubles in the auxiliary buffer
/// (25), others in the main one (24); from 256 on ARRAY_TYPED (10). The
/// element type is the one every element encodes as, else the kind's own;</item>
/// <item>string ids, array lengths and member names always go to the main
/// buffer; strings get ids in first use order, an empty one -1;</item>
/// <item>the header counts nodes, objects, arrays, and the arrays (with their
/// elements) that are neither native nor short auxiliary ones;</item>
/// <item>each buffer is LZ4 HC level 12 (V_LZ4_compress_HC's default).</item>
/// </list>
/// A float array stands for a natively typed one (FLOAT elements only come from
/// those), copied to the auxiliary buffer.
/// </summary>
public static class Kv3V5Writer
{
    private const int Magic = 0x4B563305;
    private const int FrameSize = 16384;

    private sealed class Lanes
    {
        public readonly MemoryStream B1 = new(), B2 = new(), B4 = new(), B8 = new();
        public readonly BinaryWriter W1, W2, W4, W8;

        public Lanes()
        {
            W1 = new BinaryWriter(B1);
            W2 = new BinaryWriter(B2);
            W4 = new BinaryWriter(B4);
            W8 = new BinaryWriter(B8);
        }
    }

    private sealed class Context
    {
        public readonly Lanes Main = new(), Aux = new();
        public readonly MemoryStream Types = new();
        public readonly List<int> ObjectLengths = [];
        public readonly Dictionary<string, int> Strings = new(StringComparer.Ordinal);
        public readonly List<string> StringOrder = [];
        public readonly List<byte[]> Blobs = [];
        public int Nodes, Objects, Arrays, ArraysMain, ElementsMain;

        public int StringId(string s)
        {
            if (string.IsNullOrEmpty(s))
                return -1;
            if (!Strings.TryGetValue(s, out var id))
            {
                id = StringOrder.Count;
                Strings[s] = id;
                StringOrder.Add(s);
            }
            return id;
        }
    }

    // KeyValues3's own kinds (the type field 1801... reads, bits 2-5).
    private const int KNull = 1, KBool = 2, KInt = 3, KUInt = 4, KDouble = 5, KString = 6, KBlob = 7, KArray = 8, KTable = 9;

    private static int Kind(KVObject v) => v.ValueType switch
    {
        KVValueType.Null => KNull,
        KVValueType.Boolean => KBool,
        KVValueType.Int16 or KVValueType.Int32 or KVValueType.Int64 => KInt,
        KVValueType.UInt16 or KVValueType.UInt32 or KVValueType.UInt64 => KUInt,
        KVValueType.FloatingPoint or KVValueType.FloatingPoint64 => KDouble,
        KVValueType.String => KString,
        KVValueType.BinaryBlob => KBlob,
        KVValueType.Array => KArray,
        KVValueType.Collection => KTable,
        _ => throw new NotSupportedException($"KV3 value type {v.ValueType}"),
    };

    private static long Int(KVObject v) => v.ValueType switch
    {
        KVValueType.Int16 => (short)v,
        KVValueType.Int32 => (int)v,
        _ => (long)v,
    };

    private static ulong UInt(KVObject v) => v.ValueType switch
    {
        KVValueType.UInt16 => (ushort)v,
        KVValueType.UInt32 => (uint)v,
        _ => (ulong)v,
    };

    private static double Double(KVObject v) => v.ValueType == KVValueType.FloatingPoint ? (float)v : (double)v;

    private static List<KVObject> Items(KVObject array) => [.. array.Select(x => x.Value)];

    /// <summary>
    /// 18021c770 and the count: the typed array type of an array whose elements
    /// share one kind, else plain ARRAY (an empty array too).
    /// </summary>
    private static int ArrayType(List<KVObject> items)
    {
        if (items.Count == 0)
            return 8;
        var kind = Kind(items[0]);
        if (items.Any(x => Kind(x) != kind))
            return 8;
        if (items.Count >= 0x100)
            return 10;
        return kind is KInt or KUInt or KDouble ? 0x19 : 0x18;
    }

    // 1800c6c20's choice for a node written with its own type byte.
    private static int NodeType(KVObject v)
    {
        switch (Kind(v))
        {
            case KBool:
                return (bool)v ? 13 : 14;
            case KInt:
            {
                var x = Int(v);
                return x == 0 ? 15 : x == 1 ? 16 : x == (int)x ? 11 : 3;
            }
            case KUInt:
            {
                var x = UInt(v);
                return x == 0 ? 15 : x == 1 ? 16 : x == (uint)x ? 12 : 4;
            }
            case KDouble:
            {
                var x = Double(v);
                return x == 0.0 ? 17 : x == 1.0 ? 18 : 5;
            }
            case KArray:
                return ArrayType(Items(v));
            default:
                return Kind(v);
        }
    }

    // 1800c6070's per-element type inside a typed array.
    private static int ElementType(KVObject v)
    {
        switch (Kind(v))
        {
            case KBool:
                return (bool)v ? 13 : 14;
            case KInt:
                return Int(v) == (int)Int(v) ? 11 : 3;
            case KUInt:
                return UInt(v) == (uint)UInt(v) ? 12 : 4;
            case KArray:
                return ArrayType(Items(v));
            default:
                return Kind(v);
        }
    }

    private static void TypeByte(Context c, int type, KVFlag flag)
    {
        if (flag != KVFlag.None)
        {
            c.Types.WriteByte((byte)(type | 0x80));
            c.Types.WriteByte((byte)flag);
        }
        else
            c.Types.WriteByte((byte)type);
    }

    private static void Node(Context c, KVObject v)
    {
        var type = NodeType(v);
        TypeByte(c, type, v.Flag);
        c.Nodes++;
        Value(c, c.Main, v, type);
    }

    // 1800c64b0: one value of encoded type <type> into <lanes> (strings, lengths and names to the main lanes).
    private static void Value(Context c, Lanes lanes, KVObject v, int type)
    {
        switch (type)
        {
            case 1 or 13 or 14 or 15 or 16 or 17 or 18:
                return;
            case 2:
                lanes.W1.Write((byte)((bool)v ? 1 : 0));
                return;
            case 3:
                lanes.W8.Write(Int(v));
                return;
            case 4:
                lanes.W8.Write(UInt(v));
                return;
            case 5:
                lanes.W8.Write(Double(v));
                return;
            case 6:
                c.Main.W4.Write(c.StringId((string)v!));
                return;
            case 7:
                c.Blobs.Add(v.AsBlob());
                return;
            case 8:
            {
                var items = Items(v);
                c.Main.W4.Write(items.Count);
                c.Arrays++;
                c.ArraysMain++;
                c.ElementsMain += items.Count == 0 ? 1 : items.Count;
                foreach (var item in items)
                    Node(c, item);
                return;
            }
            case 9:
            {
                c.Objects++;
                c.ObjectLengths.Add(v.Count);
                foreach (var (key, member) in v)
                {
                    c.Main.W4.Write(c.StringId(key));
                    Node(c, member);
                }
                return;
            }
            case 10 or 0x18 or 0x19:
                TypedArray(c, Items(v), type);
                return;
            case 11:
                lanes.W4.Write((int)Int(v));
                return;
            case 12:
                lanes.W4.Write((uint)UInt(v));
                return;
            case 19:
                lanes.W4.Write((float)v);
                return;
            case 20:
                lanes.W2.Write((short)Int(v));
                return;
            case 21:
                lanes.W2.Write((ushort)UInt(v));
                return;
            default:
                throw new NotSupportedException($"KV3 node type {type}");
        }
    }

    private static void TypedArray(Context c, List<KVObject> items, int type)
    {
        if (type == 10)
            c.Main.W4.Write(items.Count);
        else
            c.Main.W1.Write((byte)items.Count);
        var native = items.All(x => x.ValueType == KVValueType.FloatingPoint);
        c.Arrays++;
        if (!native && (type != 0x19 || items.Count > 0x1f))
        {
            c.ArraysMain++;
            c.ElementsMain += items.Count;
        }
        if (native)
        {
            // 1800c5ea0: the element type, then the raw floats into the auxiliary lanes.
            TypeByte(c, 19, items[0].Flag);
            foreach (var x in items)
                c.Aux.W4.Write((float)x);
            return;
        }
        var lanes = type == 0x19 ? c.Aux : c.Main;
        var first = ElementType(items[0]);
        var element = items.All(x => ElementType(x) == first) ? first : Kind(items[0]);
        TypeByte(c, element, items[0].Flag);
        foreach (var x in items)
            Value(c, lanes, x, element);
    }

    private static void Pad(MemoryStream s, int alignment)
    {
        while (s.Length % alignment != 0)
            s.WriteByte(0);
    }

    // 1800c8d80: one context at level 7 with checksums, streamed (ZSTD_compressStream2)
    // through a 64 KB output buffer: buffer 1 and buffer 2 each ended as a frame of
    // their own, the blobs continued one by one and ended as a third. The streaming
    // path matters: a one-shot compress of the same input gives other bytes.
    private static unsafe (byte[], byte[], byte[]) Zstd(byte[] raw1, byte[] raw2, IReadOnlyList<byte[]> blobs)
    {
        var cctx = ZstdSharp.Unsafe.Methods.ZSTD_createCCtx();
        try
        {
            ZstdSharp.Unsafe.Methods.ZSTD_CCtx_setParameter(cctx, ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_compressionLevel, 7);
            ZstdSharp.Unsafe.Methods.ZSTD_CCtx_setParameter(cctx, ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
            // Over 1 MB of buffers and blobs tier0 asks for worker threads; zstd's
            // multithreaded frames do not depend on how many.
            if (raw1.Length + raw2.Length + blobs.Sum(x => x.Length) > 0x100000)
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
            Stream(a, raw1, ZstdSharp.Unsafe.ZSTD_EndDirective.ZSTD_e_end);
            var b = new MemoryStream();
            Stream(b, raw2, ZstdSharp.Unsafe.ZSTD_EndDirective.ZSTD_e_end);
            var c = new MemoryStream();
            if (blobs.Count > 0)
            {
                // An empty blob is not handed to the stream at all, so a stream of empty blobs
                // ends as a frame of known size 0.
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
    /// 1800c7080's blob stream: an LZ4 HC level 12 stream whose input is copied
    /// into a 0x14000-byte ring at the running offset, which goes back to 0 once
    /// past 0xffff, each frame compressed with LZ4_compress_HC_continue (so a
    /// wrapped frame shortens the dictionary it overwrites). K4os carries lz4hc's
    /// streaming functions only internally; they are bound by reflection.
    /// </summary>
    private sealed unsafe class Lz4BlobRing : IDisposable
    {
        private const int RingSize = 0x14000, StreamSize = 1 << 19;
        private static readonly delegate*<void*, int, void*> Init;
        private static readonly delegate*<void*, int, void> SetLevel;
        private static readonly delegate*<void*, byte*, byte*, int, int, int> Continue;
        private readonly byte* _ring = (byte*)NativeMemory.AllocZeroed(RingSize);
        private readonly void* _stream = NativeMemory.AllocZeroed(StreamSize);
        private int _offset;

        static Lz4BlobRing()
        {
            var asm = typeof(LZ4Codec).Assembly;
            var ll = asm.GetType("K4os.Compression.LZ4.Engine.LL", true)!;
            var llxx = asm.GetType("K4os.Compression.LZ4.Engine.LLxx", true)!;
            var flags = BindingFlags.Public | BindingFlags.Static;
            Init = (delegate*<void*, int, void*>)ll.GetMethods(flags).First(m => m.Name == "LZ4_initStreamHC" && m.GetParameters().Length == 2).MethodHandle.GetFunctionPointer();
            SetLevel = (delegate*<void*, int, void>)ll.GetMethod("LZ4_setCompressionLevel", flags)!.MethodHandle.GetFunctionPointer();
            Continue = (delegate*<void*, byte*, byte*, int, int, int>)llxx.GetMethod("LZ4_compress_HC_continue", flags)!.MethodHandle.GetFunctionPointer();
        }

        public Lz4BlobRing()
        {
            if (Init(_stream, StreamSize) == null)
                throw new InvalidOperationException("LZ4 HC stream init failed");
            SetLevel(_stream, 12);
        }

        public byte[] Compress(ReadOnlySpan<byte> frame)
        {
            frame.CopyTo(new Span<byte>(_ring + _offset, frame.Length));
            var output = new byte[0x4050];
            int n;
            fixed (byte* o = output)
                n = Continue(_stream, _ring + _offset, o, frame.Length, output.Length);
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
            NativeMemory.Free(_stream);
        }
    }

    // 1800c8790 / 1800c8d80 keep the compressed block when raw / 20 <= raw - compressed.
    private static bool Saves(int raw, int compressed) => raw / 20 <= raw - compressed;

    private static byte[] Lz4(byte[] input)
    {
        var output = new byte[LZ4Codec.MaximumOutputSize(input.Length)];
        var n = LZ4Codec.Encode(input, output, LZ4Level.L12_MAX);
        return output.AsSpan(0, n).ToArray();
    }

    /// <summary>A block's three sections after binary_auto (or a forced method) has had its say.</summary>
    /// <param name="Method">The method the block is stored with.</param>
    /// <param name="Raw2">Buffer 2 as stored before compression: with LZ4 and blobs, the blob
    /// frames' compressed sizes (u16 each) follow its trailer.</param>
    /// <param name="Out1">Buffer 1 as stored.</param>
    /// <param name="Out2">Buffer 2 as stored.</param>
    /// <param name="BlobsRaw">The blobs, concatenated, uncompressed.</param>
    /// <param name="BlobsOut">The blobs as stored.</param>
    /// <param name="FrameSizeBytes">Bytes of LZ4 blob frame sizes in <paramref name="Raw2"/>.</param>
    internal readonly record struct Packed(KV3BinaryCompressionMethod Method, byte[] Raw2, byte[] Out1, byte[] Out2,
        byte[] BlobsRaw, byte[] BlobsOut, int FrameSizeBytes)
    {
        /// <summary>The header's stored-size field (offset 52): Zstd counts the blobs, LZ4 leaves them out.</summary>
        public int StoredSize => Method switch
        {
            KV3BinaryCompressionMethod.Zstd => Out1.Length + Out2.Length + BlobsOut.Length,
            KV3BinaryCompressionMethod.Lz4 => Out1.Length + Out2.Length,
            _ => Out1.Length + Out2.Length,
        };
    }

    /// <summary>
    /// tier0's compression stage for a block's uncompressed sections: <paramref name="method"/>, or
    /// with null as binary_auto chooses (1800c7220): raw under 256 bytes of buffers and blobs, else
    /// LZ4 under 0x80001 and zstd from there, kept only if it saves a twentieth. Shared by this
    /// writer and <see cref="Kv3.Kv3Tree"/>, so an edited compiled block is stored as RC stores one.
    /// </summary>
    internal static Packed Pack(byte[] raw1, byte[] raw2, IReadOnlyList<byte[]> blobs, KV3BinaryCompressionMethod? method)
    {
        var blobsRaw = blobs.SelectMany(x => x).ToArray();
        var total = raw1.Length + raw2.Length + blobsRaw.Length;
        var chosen = method ?? (total < 0x100 ? KV3BinaryCompressionMethod.Uncompressed
            : total < 0x80001 ? KV3BinaryCompressionMethod.Lz4 : KV3BinaryCompressionMethod.Zstd);
        byte[] out1 = raw1, out2 = raw2, blobsOut = blobsRaw;
        var frameSizes = 0;
        if (chosen == KV3BinaryCompressionMethod.Lz4)
        {
            // Blobs (1800c7080): one LZ4 HC level 12 stream over every blob in frames of
            // at most 16384 bytes, a blob's last frame short; each frame's compressed size
            // (u16) follows buffer 2's trailer.
            var withSizes = new MemoryStream();
            withSizes.Write(raw2);
            var packed = new MemoryStream();
            if (blobs.Count > 0)
            {
                using var ring = new Lz4BlobRing();
                foreach (var blob in blobs)
                    for (var at = 0; at < blob.Length; at += FrameSize)
                    {
                        var frame = ring.Compress(blob.AsSpan(at, Math.Min(FrameSize, blob.Length - at)));
                        withSizes.Write(BitConverter.GetBytes((ushort)frame.Length));
                        frameSizes += 2;
                        packed.Write(frame);
                    }
            }
            var lzRaw2 = withSizes.ToArray();
            var lz1 = Lz4(raw1);
            var lz2 = Lz4(lzRaw2);
            var lzBlobs = packed.ToArray();
            if (method != null || Saves(raw1.Length + lzRaw2.Length + blobsRaw.Length, lz1.Length + lz2.Length + lzBlobs.Length))
                (raw2, out1, out2, blobsOut) = (lzRaw2, lz1, lz2, lzBlobs);
            else
            {
                chosen = KV3BinaryCompressionMethod.Uncompressed;
                frameSizes = 0;
            }
        }
        else if (chosen == KV3BinaryCompressionMethod.Zstd)
        {
            var (z1, z2, zBlobs) = Zstd(raw1, raw2, blobs);
            if (method != null || Saves(total, z1.Length + z2.Length + zBlobs.Length))
                (out1, out2, blobsOut) = (z1, z2, zBlobs);
            else
                chosen = KV3BinaryCompressionMethod.Uncompressed;
        }
        return new Packed(chosen, raw2, out1, out2, blobsRaw, blobsOut, frameSizes);
    }

    /// <summary>
    /// The block's bytes: <paramref name="root"/> under <paramref name="format"/>,
    /// compressed with <paramref name="method"/>, or with null as binary_auto
    /// chooses (1800c7220): raw under 256 bytes of buffers and blobs, else LZ4
    /// under 0x80001 and zstd from there, kept only if it saves a twentieth.
    /// </summary>
    public static byte[] Write(KVObject root, Guid format, KV3BinaryCompressionMethod? method = null)
    {
        var c = new Context();
        Node(c, root);

        // Buffer 1: strings, then the auxiliary lanes (the string count heads its 4-byte lane).
        var b1 = new MemoryStream();
        var stringBytes = 0;
        foreach (var s in c.StringOrder)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            b1.Write(bytes);
            b1.WriteByte(0);
            stringBytes += bytes.Length + 1;
        }
        c.Aux.B1.WriteTo(b1);
        if (c.Aux.B2.Length > 0)
        {
            Pad(b1, 2);
            c.Aux.B2.WriteTo(b1);
        }
        Pad(b1, 4);
        b1.Write(BitConverter.GetBytes(c.StringOrder.Count));
        c.Aux.B4.WriteTo(b1);
        if (c.Aux.B8.Length > 0)
        {
            Pad(b1, 8);
            c.Aux.B8.WriteTo(b1);
        }

        // Buffer 2: object lengths, the main lanes, the types, blob lengths and the trailer.
        var b2 = new MemoryStream();
        foreach (var n in c.ObjectLengths)
            b2.Write(BitConverter.GetBytes(n));
        c.Main.B1.WriteTo(b2);
        if (c.Main.B2.Length > 0)
        {
            Pad(b2, 2);
            c.Main.B2.WriteTo(b2);
        }
        if (c.Main.B4.Length > 0)
        {
            Pad(b2, 4);
            c.Main.B4.WriteTo(b2);
        }
        if (c.Main.B8.Length > 0)
        {
            Pad(b2, 8);
            c.Main.B8.WriteTo(b2);
        }
        c.Types.WriteTo(b2);
        foreach (var blob in c.Blobs)
            b2.Write(BitConverter.GetBytes(blob.Length));
        b2.Write(BitConverter.GetBytes(0xFFEEDD00));

        var raw1 = b1.ToArray();
        var p = Pack(raw1, b2.ToArray(), c.Blobs, method);
        var (chosen, raw2, out1, out2, blobsOut, frameSizes) = (p.Method, p.Raw2, p.Out1, p.Out2, p.BlobsOut, p.FrameSizeBytes);
        var blobsRaw = p.BlobsRaw;
        var lz4 = chosen == KV3BinaryCompressionMethod.Lz4;
        var zstd = chosen == KV3BinaryCompressionMethod.Zstd;
        var compressed = lz4 || zstd;

        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(Magic);
        w.Write(format.ToByteArray());
        w.Write((uint)chosen);
        w.Write((ushort)0);
        w.Write(lz4 ? (ushort)FrameSize : (ushort)0);
        w.Write(stringBytes + (int)c.Aux.B1.Length);
        w.Write(1 + (int)(c.Aux.B4.Length / 4));
        w.Write((int)(c.Aux.B8.Length / 8));
        w.Write((int)c.Types.Length);
        w.Write((ushort)Math.Min(c.Objects, ushort.MaxValue));
        w.Write((ushort)Math.Min(c.Arrays, ushort.MaxValue));
        w.Write(raw1.Length + raw2.Length);
        w.Write(compressed ? p.StoredSize : raw1.Length + raw2.Length); // LZ4 leaves the blobs out
        w.Write(c.Blobs.Count);
        w.Write(blobsRaw.Length);
        w.Write((int)(c.Aux.B2.Length / 2));
        w.Write(frameSizes);
        w.Write(raw1.Length);
        w.Write(compressed ? out1.Length : 0);
        w.Write(raw2.Length);
        w.Write(compressed ? out2.Length : 0);
        w.Write((int)c.Main.B1.Length);
        w.Write((int)(c.Main.B2.Length / 2));
        w.Write((int)(c.Main.B4.Length / 4));
        w.Write((int)(c.Main.B8.Length / 8));
        w.Write(c.Nodes);
        w.Write(c.Objects);
        w.Write(c.ArraysMain);
        w.Write(c.ElementsMain);
        w.Write(out1);
        w.Write(out2);
        if (c.Blobs.Count > 0)
        {
            w.Write(blobsOut);
            w.Write(0xFFEEDD00);
        }
        return o.ToArray();
    }
}
