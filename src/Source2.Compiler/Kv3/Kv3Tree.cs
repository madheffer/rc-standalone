using System.Buffers.Binary;
using System.Text;

namespace Source2.Compiler.Kv3;

/// <summary>
/// One KV3 value exactly as it sits on the wire: its node type (which a <c>KVObject</c> folds
/// away), its flag, and its payload. <see cref="Kv3Tree"/> reads and writes these without
/// deciding anything itself, so a file re-encoded from an unchanged tree carries Valve's own
/// layout. A serializer that writes every array as a generic ARRAY (VRF's did) loses that, and CS2
/// silently rejected an NmGraph re-encoded that way (2026-09-26).
/// </summary>
public sealed class Kv3Node
{
    /// <summary>The node type.</summary>
    public Kv3Type Type { get; set; }

    /// <summary>The flag byte (<c>resource:</c>, <c>soundevent:</c>...), 0 for none.</summary>
    public byte Flag { get; set; }

    /// <summary>Scalar payload, little-endian in the low bytes (width set by <see cref="Type"/>).</summary>
    public ulong Bits { get; set; }

    /// <summary>String table index for STRING (-1 is the empty string).</summary>
    public int StringId { get; set; }

    /// <summary>The bytes of a BINARY_BLOB.</summary>
    public byte[]? Blob { get; set; }

    /// <summary>Children of ARRAY, typed arrays and OBJECT.</summary>
    public List<Kv3Node>? Items { get; set; }

    /// <summary>OBJECT keys as string table indices, parallel to <see cref="Items"/>.</summary>
    public List<int>? KeyIds { get; set; }

    /// <summary>Element type of a typed array (written once, not per element).</summary>
    public Kv3Type ElementType { get; set; }

    /// <summary>Element flag of a typed array.</summary>
    public byte ElementFlag { get; set; }

    /// <summary>Whether this is one of the typed array forms.</summary>
    public bool IsTypedArray => Type is Kv3Type.ArrayTyped or Kv3Type.ArrayTypeByteLength or Kv3Type.ArrayTypeAuxiliaryBuffer;
}

/// <summary>A binary KV3 node type, as the type byte stores it.</summary>
public enum Kv3Type : byte
{
    /// <summary>null.</summary>
    Null = 1,
    /// <summary>A boolean in a 1-byte lane.</summary>
    Boolean = 2,
    /// <summary>A signed 64-bit integer.</summary>
    Int64 = 3,
    /// <summary>An unsigned 64-bit integer.</summary>
    UInt64 = 4,
    /// <summary>A 64-bit float.</summary>
    Double = 5,
    /// <summary>A string, as a string table id.</summary>
    String = 6,
    /// <summary>A binary blob.</summary>
    BinaryBlob = 7,
    /// <summary>An array whose elements each carry a type.</summary>
    Array = 8,
    /// <summary>An object: names and values.</summary>
    Object = 9,
    /// <summary>A typed array with a 32-bit length.</summary>
    ArrayTyped = 10,
    /// <summary>A signed 32-bit integer.</summary>
    Int32 = 11,
    /// <summary>An unsigned 32-bit integer.</summary>
    UInt32 = 12,
    /// <summary>true, with no payload.</summary>
    BooleanTrue = 13,
    /// <summary>false, with no payload.</summary>
    BooleanFalse = 14,
    /// <summary>The integer 0, with no payload.</summary>
    Int64Zero = 15,
    /// <summary>The integer 1, with no payload.</summary>
    Int64One = 16,
    /// <summary>The float 0.0, with no payload.</summary>
    DoubleZero = 17,
    /// <summary>The float 1.0, with no payload.</summary>
    DoubleOne = 18,
    /// <summary>A 32-bit float.</summary>
    Float = 19,
    /// <summary>A signed 16-bit integer.</summary>
    Int16 = 20,
    /// <summary>An unsigned 16-bit integer.</summary>
    UInt16 = 21,
    /// <summary>A signed 8-bit integer.</summary>
    Int8 = 22,
    /// <summary>An unsigned 8-bit integer.</summary>
    UInt8 = 23,
    /// <summary>A typed array with an 8-bit length, elements in the main buffer.</summary>
    ArrayTypeByteLength = 24,
    /// <summary>A typed array with an 8-bit length, elements in the auxiliary buffer.</summary>
    ArrayTypeAuxiliaryBuffer = 25,
}

/// <summary>
/// A binary KV3 v5 block read and written losslessly (see <see cref="Kv3Node"/>). Reads
/// uncompressed, LZ4 and Zstd blocks (Zstd on a handful of stock clips and models).
/// </summary>
public sealed class Kv3Tree
{
    /// <summary>An auxiliary array this long or longer is counted in the buffer-2 counters as if its
    /// elements were there. Pinned by stock data: 31 never counts, 32 does (a vsndevts_c).</summary>
    private const int AuxCountedFrom = 32;

    /// <summary>The block's format (schema) GUID.</summary>
    public Guid Format { get; set; }

    /// <summary>The compression the block was read with (0 none, 1 LZ4, 2 Zstd).</summary>
    public uint Compression { get; set; }

    /// <summary>The LZ4 blob frame size the block was read with.</summary>
    public ushort FrameSize { get; set; }

    /// <summary>The string table, by id.</summary>
    public List<string> Strings { get; } = [];

    /// <summary>The root value.</summary>
    public Kv3Node Root { get; set; } = new() { Type = Kv3Type.Null };

    /// <summary>The header as read, kept so a round-trip can report which fields a rewrite changed.</summary>
    public int[] ReadHeader { get; private set; } = [];

    /// <summary>The uncompressed sections as read (buffer 2 up to and including its trailer).</summary>
    public (byte[] Buffer1, byte[] Buffer2, byte[] Blobs) ReadSections { get; private set; } = ([], [], []);

    /// <summary>The id of <paramref name="s"/> in the string table, adding it when new; -1 for empty.</summary>
    public int StringId(string s)
    {
        if (s.Length == 0) return -1;
        var i = Strings.IndexOf(s);
        if (i >= 0) return i;
        Strings.Add(s);
        return Strings.Count - 1;
    }

    /// <summary>The string with table id <paramref name="id"/> (empty for -1).</summary>
    public string StringOf(int id) => id < 0 ? string.Empty : Strings[id];

    // ---- navigation and typed edits. A setter picks the node form RC would: 0.0 and 1.0 as
    // DoubleZero / DoubleOne, 0 and 1 as Int64Zero / Int64One (measured in stock clips). A typed
    // array element keeps its array's element type, since elements carry no type byte.

    /// <summary>The member <paramref name="key"/> of object <paramref name="obj"/>, or null.</summary>
    public Kv3Node? Get(Kv3Node obj, string key)
    {
        if (obj.Type != Kv3Type.Object) return null;
        for (var i = 0; i < obj.Items!.Count; i++)
            if (StringOf(obj.KeyIds![i]) == key) return obj.Items[i];
        return null;
    }

    /// <summary>The member <paramref name="key"/> of <paramref name="obj"/>.</summary>
    /// <exception cref="InvalidDataException">There is no such member.</exception>
    public Kv3Node Need(Kv3Node obj, string key) => Get(obj, key) ?? throw new InvalidDataException($"KV3: missing '{key}'");

    /// <summary>Replace the member <paramref name="key"/> of <paramref name="obj"/>.</summary>
    /// <exception cref="InvalidDataException">There is no such member.</exception>
    public void Replace(Kv3Node obj, string key, Kv3Node value)
    {
        for (var i = 0; i < obj.Items!.Count; i++)
            if (StringOf(obj.KeyIds![i]) == key) { obj.Items[i] = value; return; }
        throw new InvalidDataException($"KV3: missing '{key}'");
    }

    /// <summary>A numeric node's value as a double.</summary>
    public static double AsDouble(Kv3Node n) => n.Type switch
    {
        Kv3Type.Double => BitConverter.UInt64BitsToDouble(n.Bits),
        Kv3Type.Float => BitConverter.UInt32BitsToSingle((uint)n.Bits),
        Kv3Type.DoubleZero or Kv3Type.Int64Zero => 0,
        Kv3Type.DoubleOne or Kv3Type.Int64One => 1,
        _ => AsLong(n),
    };

    /// <summary>An integer or boolean node's value.</summary>
    /// <exception cref="InvalidDataException">The node is not a number.</exception>
    public static long AsLong(Kv3Node n) => n.Type switch
    {
        Kv3Type.Int64Zero or Kv3Type.BooleanFalse or Kv3Type.DoubleZero => 0,
        Kv3Type.Int64One or Kv3Type.BooleanTrue or Kv3Type.DoubleOne => 1,
        Kv3Type.Int8 => (sbyte)(byte)n.Bits,
        Kv3Type.Int16 => (short)(ushort)n.Bits,
        Kv3Type.Int32 => (int)(uint)n.Bits,
        Kv3Type.Int64 => (long)n.Bits,
        Kv3Type.Boolean or Kv3Type.UInt8 or Kv3Type.UInt16 or Kv3Type.UInt32 or Kv3Type.UInt64 => (long)n.Bits,
        _ => throw new InvalidDataException($"KV3: {n.Type} is not a number"),
    };

    /// <summary>A boolean node's value.</summary>
    /// <exception cref="InvalidDataException">The node is not a boolean.</exception>
    public static bool AsBool(Kv3Node n) => n.Type switch
    {
        Kv3Type.BooleanTrue => true,
        Kv3Type.BooleanFalse => false,
        Kv3Type.Boolean => n.Bits != 0,
        _ => throw new InvalidDataException($"KV3: {n.Type} is not a boolean"),
    };

    /// <summary>A double as RC writes a described (non-element) node.</summary>
    public static Kv3Node Double(double v) => v switch
    {
        0 when !double.IsNegative(v) => new() { Type = Kv3Type.DoubleZero },
        1 => new() { Type = Kv3Type.DoubleOne },
        _ => new() { Type = Kv3Type.Double, Bits = BitConverter.DoubleToUInt64Bits(v) },
    };

    /// <summary>A boolean as RC writes a described node.</summary>
    public static Kv3Node Bool(bool v) => new() { Type = v ? Kv3Type.BooleanTrue : Kv3Type.BooleanFalse };

    /// <summary>An integer in the same width family as <paramref name="like"/>, in RC's form.</summary>
    /// <exception cref="InvalidDataException"><paramref name="like"/> is not an integer.</exception>
    public static Kv3Node IntLike(Kv3Node like, long v) => like.Type switch
    {
        Kv3Type.Int64 or Kv3Type.Int64Zero or Kv3Type.Int64One =>
            v == 0 ? new() { Type = Kv3Type.Int64Zero } : v == 1 ? new() { Type = Kv3Type.Int64One } : new() { Type = Kv3Type.Int64, Bits = (ulong)v },
        Kv3Type.Int32 => new() { Type = like.Type, Bits = (uint)(int)v },
        Kv3Type.Int16 => new() { Type = like.Type, Bits = (ushort)(short)v },
        Kv3Type.Int8 => new() { Type = like.Type, Bits = (byte)(sbyte)v },
        Kv3Type.UInt8 or Kv3Type.UInt16 or Kv3Type.UInt32 or Kv3Type.UInt64 => new() { Type = like.Type, Bits = (ulong)v },
        _ => throw new InvalidDataException($"KV3: {like.Type} is not an integer"),
    };

    // ---- reading

    private sealed class Lanes
    {
        public byte[] Data = [];
        public int P1, E1, P2, E2, P4, E4, P8, E8;
        public byte ReadB1() { if (P1 >= E1) throw Bad("bytes1"); return Data[P1++]; }
        public ushort ReadB2() { if (P2 + 2 > E2) throw Bad("bytes2"); var v = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(P2)); P2 += 2; return v; }
        public uint ReadB4() { if (P4 + 4 > E4) throw Bad("bytes4"); var v = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(P4)); P4 += 4; return v; }
        public ulong ReadB8() { if (P8 + 8 > E8) throw Bad("bytes8"); var v = BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(P8)); P8 += 8; return v; }

        // The lanes from offset 0: bytes1, then bytes2, 4 and 8 each aligned when not empty.
        public int Lay(int offset, int count1, int count2, int count4, int count8)
        {
            P1 = offset; E1 = offset += count1;
            offset = count2 > 0 ? Align(offset, 2) : offset; P2 = offset; E2 = offset += count2 * 2;
            offset = count4 > 0 ? Align(offset, 4) : offset; P4 = offset; E4 = offset += count4 * 4;
            offset = count8 > 0 ? Align(offset, 8) : offset; P8 = offset; E8 = offset += count8 * 8;
            if (offset > Data.Length) throw Bad("lanes past the buffer");
            return offset;
        }
    }

    private sealed class ReadState(long nodes)
    {
        public Lanes Main = new(), Aux = new();
        public byte[] Types = [];
        public int PT, ET;
        public byte[] ObjLens = [];
        public int PO, EO;
        public int[] BlobLens = [];
        public int PBL;
        public byte[] Blobs = [];
        public int PB;
        public readonly Kv3Limits.Budget Budget = new(nodes);
    }

    private static InvalidDataException Bad(string what) => new($"KV3 v5: truncated or malformed ({what})");

    private static int Align(int o, int a) => (o + a - 1) & ~(a - 1);

    /// <summary>Read a v5 block.</summary>
    /// <exception cref="InvalidDataException">The block is not a well-formed KV3 v5 block.</exception>
    /// <exception cref="NotSupportedException">The block uses a compression other than none, LZ4 or Zstd.</exception>
    public static Kv3Tree Read(ReadOnlySpan<byte> span)
    {
        try
        {
            return ReadBlock(span.ToArray());
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException("Malformed KV3 v5 block: " + ex.Message, ex);
        }
    }

    private static Kv3Tree ReadBlock(byte[] block)
    {
        if (block.Length < Kv3Block.HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(block) != Kv3Block.Magic)
            throw new InvalidDataException("not a KV3 v5 block");
        var h = new int[30];
        for (var i = 0; i < 30; i++) h[i] = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(i * 4));
        var t = new Kv3Tree
        {
            Format = new Guid(block.AsSpan(4, 16)),
            Compression = (uint)h[5],
            FrameSize = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(26)),
            ReadHeader = h,
        };
        if (t.Compression > 2) throw new NotSupportedException($"KV3 compression {t.Compression}");
        int countBytes1 = h[7], countBytes4 = h[8], countBytes8 = h[9], countTypes = h[10];
        int countBlocks = h[14], sizeBlobs = h[15], countBytes2 = h[16];
        int unc1 = h[18], cmp1 = h[19], unc2 = h[20], cmp2 = h[21];
        int b2Bytes1 = h[22], b2Bytes2 = h[23], b2Bytes4 = h[24], b2Bytes8 = h[25], b2Objects = h[27];

        // Every size and count is checked before anything is allocated from it.
        var compressed = t.Compression != 0;
        long declared = (long)unc1 + unc2 + sizeBlobs;
        long stored = compressed ? (long)cmp1 + cmp2 : declared;
        Kv3Limits.CheckSizes(declared, stored, block.Length - Kv3Block.HeaderSize,
            unc1, cmp1, unc2, cmp2, sizeBlobs, countBlocks, countTypes, countBytes1, countBytes2, countBytes4, countBytes8,
            b2Bytes1, b2Bytes2, b2Bytes4, b2Bytes8, b2Objects, h[17]);

        using var zstd = t.Compression == 2 ? new ZstdSharp.Decompressor() : null;
        var pos = Kv3Block.HeaderSize;
        byte[] Take(int unc, int cmp)
        {
            var o = new byte[unc];
            if (!compressed) { block.AsSpan(pos, unc).CopyTo(o); pos += unc; return o; }
            var n = zstd is null ? Source2.Compiler.Compression.Lz4.Decode(block.AsSpan(pos, cmp), o) : zstd.Unwrap(block.AsSpan(pos, cmp), o);
            if (n != unc) throw Bad("compressed buffer");
            pos += cmp;
            return o;
        }
        var buf1 = Take(unc1, cmp1);
        var buf2 = Take(unc2, cmp2);

        var s = new ReadState(Kv3Limits.NodeBudget(declared));
        // Buffer 1 (auxiliary): bytes1 (strings first) | bytes2 | bytes4 (string count first) | bytes8
        var a = s.Aux; a.Data = buf1;
        a.Lay(0, countBytes1, countBytes2, countBytes4, countBytes8);
        var nStrings = (int)a.ReadB4();
        if (nStrings < 0 || nStrings > countBytes1) throw Bad("string count");
        for (var i = 0; i < nStrings; i++)
        {
            var end = Array.IndexOf(buf1, (byte)0, a.P1, a.E1 - a.P1);
            if (end < 0) throw Bad("string table");
            t.Strings.Add(Encoding.UTF8.GetString(buf1, a.P1, end - a.P1));
            a.P1 = end + 1;
        }

        // Buffer 2 (main): object lengths | bytes1 | bytes2 | bytes4 | bytes8 | types | blob tail
        var m = s.Main; m.Data = buf2;
        var off = b2Objects * 4;
        s.ObjLens = buf2; s.PO = 0; s.EO = off;
        off = m.Lay(off, b2Bytes1, b2Bytes2, b2Bytes4, b2Bytes8);
        s.Types = buf2; s.PT = off; s.ET = off += countTypes;
        if (s.ET > buf2.Length) throw Bad("types past the buffer");
        if (countBlocks > 0)
        {
            if (off + countBlocks * 4L + 4 > buf2.Length) throw Bad("blob lengths");
            s.BlobLens = new int[countBlocks];
            for (var i = 0; i < countBlocks; i++, off += 4)
            {
                s.BlobLens[i] = BinaryPrimitives.ReadInt32LittleEndian(buf2.AsSpan(off));
                if (s.BlobLens[i] < 0) throw Bad("blob length");
            }
            if (s.BlobLens.Sum(x => (long)x) != sizeBlobs) throw Bad("blob lengths do not add up");
            off += 4;   // trailer
            s.Blobs = new byte[sizeBlobs];
            if (!compressed) { block.AsSpan(pos, sizeBlobs).CopyTo(s.Blobs); pos += sizeBlobs; }
            else if (zstd is not null)
            {
                var cmpBlobs = h[13] - cmp1 - cmp2;   // one frame for all blobs, no per-frame size list
                if (zstd.Unwrap(block.AsSpan(pos, cmpBlobs), s.Blobs) != sizeBlobs) throw Bad("zstd blobs");
                pos += cmpBlobs;
            }
            else
            {
                // Decoded blob by blob, as tier0's reader does: it copies each whole frame into the
                // current blob's allocation unchecked, so a frame that crosses into the next blob
                // overruns the heap in game (the 2026-09-27 anim-builder crash).
                var dec = new Source2.Compiler.Compression.Lz4ChainDecoder(t.FrameSize);
                var tmp = new byte[t.FrameSize];
                var sizesEnd = off + h[17];
                var done = 0;
                foreach (var len in s.BlobLens)
                {
                    for (var got = 0; got < len;)
                    {
                        if (off + 2 > sizesEnd) throw Bad("lz4 blob frame sizes");
                        var clen = BinaryPrimitives.ReadUInt16LittleEndian(buf2.AsSpan(off)); off += 2;
                        if (pos + clen > block.Length) throw Bad("lz4 blob frame");
                        var n = dec.Decode(block.AsSpan(pos, clen), tmp);
                        if (n < 1) throw Bad("lz4 blob frame");
                        if (got + n > len) throw Bad("lz4 frame crosses a blob boundary");
                        tmp.AsSpan(0, n).CopyTo(s.Blobs.AsSpan(done + got));
                        pos += clen; got += n;
                    }
                    done += len;
                }
                if (off != sizesEnd) throw Bad("unused lz4 blob frames");
            }
        }

        t.Root = ReadNode(s, ReadType(s, out var rootFlag), rootFlag);
        var b2End = countBlocks > 0 ? s.ET + countBlocks * 4 + 4 : s.ET + 4;
        t.ReadSections = (buf1, buf2[..b2End], s.Blobs);
        return t;
    }

    private static Kv3Type ReadType(ReadState s, out byte flag)
    {
        if (s.PT >= s.ET) throw Bad("types");
        var b = s.Types[s.PT++];
        flag = 0;
        if ((b & 0x80) != 0)
        {
            if (s.PT >= s.ET) throw Bad("type flag");
            b &= 0x3F;
            flag = s.Types[s.PT++];
        }
        return (Kv3Type)b;
    }

    private static Kv3Node ReadNode(ReadState s, Kv3Type type, byte flag)
    {
        s.Budget.Enter();
        try
        {
            var n = new Kv3Node { Type = type, Flag = flag };
            var l = s.Main;
            switch (type)
            {
                case Kv3Type.Null or Kv3Type.BooleanTrue or Kv3Type.BooleanFalse or Kv3Type.Int64Zero or Kv3Type.Int64One
                    or Kv3Type.DoubleZero or Kv3Type.DoubleOne:
                    break;
                case Kv3Type.Boolean or Kv3Type.Int8 or Kv3Type.UInt8: n.Bits = l.ReadB1(); break;
                case Kv3Type.Int16 or Kv3Type.UInt16: n.Bits = l.ReadB2(); break;
                case Kv3Type.Int32 or Kv3Type.UInt32 or Kv3Type.Float: n.Bits = l.ReadB4(); break;
                case Kv3Type.Int64 or Kv3Type.UInt64 or Kv3Type.Double: n.Bits = l.ReadB8(); break;
                case Kv3Type.String: n.StringId = (int)l.ReadB4(); break;
                case Kv3Type.BinaryBlob:
                {
                    if (s.PBL >= s.BlobLens.Length) throw Bad("blob lengths");
                    var len = s.BlobLens[s.PBL++];
                    n.Blob = s.Blobs.AsSpan(s.PB, len).ToArray();
                    s.PB += len;
                    break;
                }
                case Kv3Type.Array:
                {
                    var count = l.ReadB4();
                    n.Items = new(Kv3Limits.Capacity(count));
                    for (var i = 0u; i < count; i++) n.Items.Add(ReadNode(s, ReadType(s, out var f), f));
                    break;
                }
                case Kv3Type.ArrayTyped or Kv3Type.ArrayTypeByteLength or Kv3Type.ArrayTypeAuxiliaryBuffer:
                {
                    var count = type == Kv3Type.ArrayTyped ? l.ReadB4() : l.ReadB1();
                    n.ElementType = ReadType(s, out var ef);
                    n.ElementFlag = ef;
                    n.Items = new(Kv3Limits.Capacity(count));
                    // An auxiliary array's elements live in the other buffer; swap for their duration.
                    if (type == Kv3Type.ArrayTypeAuxiliaryBuffer) (s.Main, s.Aux) = (s.Aux, s.Main);
                    for (var i = 0u; i < count; i++) n.Items.Add(ReadNode(s, n.ElementType, ef));
                    if (type == Kv3Type.ArrayTypeAuxiliaryBuffer) (s.Main, s.Aux) = (s.Aux, s.Main);
                    break;
                }
                case Kv3Type.Object:
                {
                    if (s.PO + 4 > s.EO) throw Bad("object lengths");
                    var count = BinaryPrimitives.ReadUInt32LittleEndian(s.ObjLens.AsSpan(s.PO)); s.PO += 4;
                    n.Items = new(Kv3Limits.Capacity(count));
                    n.KeyIds = new(Kv3Limits.Capacity(count));
                    for (var i = 0u; i < count; i++)
                    {
                        var vt = ReadType(s, out var f);
                        n.KeyIds.Add((int)s.Main.ReadB4());
                        n.Items.Add(ReadNode(s, vt, f));
                    }
                    break;
                }
                default:
                    throw new InvalidDataException($"KV3 node type {(int)type} is not one this reader knows");
            }
            return n;
        }
        finally { s.Budget.Leave(); }
    }

    // ---- writing

    private sealed class WriteState
    {
        public readonly Kv3Block Block = new();
        public bool Swapped;
    }

    private static void WriteType(WriteState s, Kv3Type type, byte flag, bool element = false)
    {
        if (!element) s.Block.Nodes++;
        s.Block.Type((int)type, flag);
    }

    private static void WriteNode(WriteState s, Kv3Node n)
    {
        var b = s.Block;
        var o = b.Main;
        switch (n.Type)
        {
            case Kv3Type.Null or Kv3Type.BooleanTrue or Kv3Type.BooleanFalse or Kv3Type.Int64Zero or Kv3Type.Int64One
                or Kv3Type.DoubleZero or Kv3Type.DoubleOne:
                break;
            case Kv3Type.Boolean or Kv3Type.Int8 or Kv3Type.UInt8: o.Put(n.Bits, 1); break;
            case Kv3Type.Int16 or Kv3Type.UInt16: o.Put(n.Bits, 2); break;
            case Kv3Type.Int32 or Kv3Type.UInt32 or Kv3Type.Float: o.Put(n.Bits, 4); break;
            case Kv3Type.Int64 or Kv3Type.UInt64 or Kv3Type.Double: o.Put(n.Bits, 8); break;
            case Kv3Type.String: o.Put((uint)n.StringId, 4); break;
            case Kv3Type.BinaryBlob: b.Blobs.Add(n.Blob ?? []); break;
            case Kv3Type.Array:
                b.Arrays++;
                // The two undocumented buffer-2 counters, fitted exactly over ~20,000 stock v5 blocks:
                // arrays whose elements land in buffer 2 (plus long auxiliary ones), and their element
                // counts, a generic array counting max(1, length).
                if (!s.Swapped)
                {
                    b.ArraysMain++;
                    b.ElementsMain += Math.Max(1, n.Items!.Count);
                }
                o.Put((uint)n.Items!.Count, 4);
                foreach (var c in n.Items) { WriteType(s, c.Type, c.Flag); WriteNode(s, c); }
                break;
            case Kv3Type.ArrayTyped or Kv3Type.ArrayTypeByteLength or Kv3Type.ArrayTypeAuxiliaryBuffer:
                b.Arrays++;
                if (n.Type == Kv3Type.ArrayTyped) o.Put((uint)n.Items!.Count, 4);
                else
                {
                    if (n.Items!.Count > 255) throw new InvalidDataException($"{n.Type} holds at most 255 elements, not {n.Items.Count}");
                    o.Put((uint)n.Items.Count, 1);
                }
                WriteType(s, n.ElementType, n.ElementFlag, element: true);
                if (n.Type == Kv3Type.ArrayTypeAuxiliaryBuffer) { (b.Main, b.Aux) = (b.Aux, b.Main); s.Swapped = !s.Swapped; }
                if (!s.Swapped || (n.Type == Kv3Type.ArrayTypeAuxiliaryBuffer && n.Items.Count >= AuxCountedFrom))
                {
                    b.ArraysMain++;
                    b.ElementsMain += n.Items.Count;
                }
                foreach (var c in n.Items!)
                {
                    if (c.Type != n.ElementType) throw new InvalidDataException($"typed array of {n.ElementType} holds a {c.Type}");
                    WriteNode(s, c);
                }
                if (n.Type == Kv3Type.ArrayTypeAuxiliaryBuffer) { (b.Main, b.Aux) = (b.Aux, b.Main); s.Swapped = !s.Swapped; }
                break;
            case Kv3Type.Object:
                b.Objects++;
                b.ObjectLength(n.Items!.Count);
                for (var i = 0; i < n.Items.Count; i++)
                {
                    WriteType(s, n.Items[i].Type, n.Items[i].Flag);
                    b.Main.Put((uint)n.KeyIds![i], 4);
                    WriteNode(s, n.Items[i]);
                }
                break;
            default:
                throw new NotSupportedException($"KV3 node type {(int)n.Type}");
        }
    }

    private Kv3Block Fill()
    {
        var s = new WriteState();
        WriteType(s, Root.Type, Root.Flag);
        WriteNode(s, Root);
        s.Block.Strings = Strings;
        return s.Block;
    }

    /// <summary>The uncompressed sections and header fields as they would be written, for parity
    /// checks (compressed bytes depend on the encoder, so they are never compared).</summary>
    public (byte[] Buffer1, byte[] Buffer2, byte[] Blobs, int[] Header) Encode()
    {
        var block = Fill();
        var (b1, b2, h) = block.Layout();
        return (b1, b2, block.Blobs.SelectMany(x => x).ToArray(), h);
    }

    /// <summary>
    /// The complete block, compressed as tier0's SaveKV3 compresses one: with
    /// <paramref name="compression"/> null, binary_auto picks raw, LZ4 HC 12 or Zstd 7 by size,
    /// which is what resourcecompiler writes, so an unchanged tree comes back as Valve's bytes.
    /// 0, 1 and 2 force uncompressed, LZ4 and Zstd.
    /// </summary>
    public byte[] Write(uint? compression = null)
    {
        if (compression > 2) throw new NotSupportedException($"KV3 compression {compression}");
        return Fill().Write(Format, (Kv3Compression?)compression);
    }
}
