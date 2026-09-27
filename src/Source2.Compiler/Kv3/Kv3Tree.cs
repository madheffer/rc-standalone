using System.Buffers.Binary;
using System.Text;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Encoders;

namespace Source2.Compiler.Kv3;

/// <summary>
/// One KV3 value exactly as it sits on the wire: its node type (which VRF's KVObject folds
/// away), its flag, and its payload. <see cref="Kv3Tree"/> reads and writes these without
/// deciding anything itself, so a file re-encoded from an unchanged tree carries Valve's
/// own layout. VRF's serializer does not: it writes every array as a generic ARRAY and
/// never uses typed or auxiliary-buffer arrays, and CS2 silently rejected an NmGraph it had
/// re-encoded (vpkstuff, 2026-09-26).
/// </summary>
public sealed class Kv3Node
{
    public Kv3Type Type { get; set; }
    public byte Flag { get; set; }

    /// <summary>Scalar payload, little-endian in the low bytes (width set by <see cref="Type"/>).</summary>
    public ulong Bits { get; set; }

    /// <summary>String table index for STRING (-1 is the empty string).</summary>
    public int StringId { get; set; }

    public byte[]? Blob { get; set; }

    /// <summary>Children of ARRAY, typed arrays and OBJECT.</summary>
    public List<Kv3Node>? Items { get; set; }

    /// <summary>OBJECT keys as string table indices, parallel to <see cref="Items"/>.</summary>
    public List<int>? KeyIds { get; set; }

    /// <summary>Element type and flag of a typed array (written once, not per element).</summary>
    public Kv3Type ElementType { get; set; }
    public byte ElementFlag { get; set; }

    public bool IsTypedArray => Type is Kv3Type.ArrayTyped or Kv3Type.ArrayTypeByteLength or Kv3Type.ArrayTypeAuxiliaryBuffer;
}

public enum Kv3Type : byte
{
    Null = 1, Boolean = 2, Int64 = 3, UInt64 = 4, Double = 5, String = 6, BinaryBlob = 7, Array = 8, Object = 9,
    ArrayTyped = 10, Int32 = 11, UInt32 = 12, BooleanTrue = 13, BooleanFalse = 14, Int64Zero = 15, Int64One = 16,
    DoubleZero = 17, DoubleOne = 18, Float = 19, Int16 = 20, UInt16 = 21, Int32AsByte = 23,
    ArrayTypeByteLength = 24, ArrayTypeAuxiliaryBuffer = 25,
}

/// <summary>
/// A binary KV3 v5 block read and written losslessly (see <see cref="Kv3Node"/>). Reads
/// uncompressed, LZ4 and Zstd blocks (Zstd on a handful of stock clips and models).
/// </summary>
public sealed class Kv3Tree
{
    private const uint Magic5 = 0x4B563305;   // "\x05" "3VK"
    private const uint Trailer = 0xFFEEDD00;
    private const int HeaderSize = 120;

    /// <summary>An auxiliary array this long or longer is counted in the buffer-2 counters as if its
    /// elements were there. Pinned by stock data: 31 never counts, 32 does (a vsndevts_c).</summary>
    private const int AuxCountedFrom = 32;

    public Guid Format { get; set; }
    public uint Compression { get; set; }
    public ushort FrameSize { get; set; }
    public List<string> Strings { get; } = [];
    public Kv3Node Root { get; set; } = new() { Type = Kv3Type.Null };

    /// <summary>The header as read, kept so a round-trip can report which fields a rewrite changed.</summary>
    public int[] ReadHeader { get; private set; } = [];

    /// <summary>The uncompressed sections as read (buffer 2 up to and including its trailer).</summary>
    public (byte[] Buffer1, byte[] Buffer2, byte[] Blobs) ReadSections { get; private set; } = ([], [], []);

    public int StringId(string s)
    {
        if (s.Length == 0) return -1;
        var i = Strings.IndexOf(s);
        if (i >= 0) return i;
        Strings.Add(s);
        return Strings.Count - 1;
    }

    public string StringOf(int id) => id < 0 ? string.Empty : Strings[id];

    // ---- navigation and typed edits. A setter picks the node form RC would: 0.0 and 1.0 as
    // DoubleZero / DoubleOne, 0 and 1 as Int64Zero / Int64One (measured in stock clips). A typed
    // array element keeps its array's element type, since elements carry no type byte.

    public Kv3Node? Get(Kv3Node obj, string key)
    {
        if (obj.Type != Kv3Type.Object) return null;
        for (var i = 0; i < obj.Items!.Count; i++)
            if (StringOf(obj.KeyIds![i]) == key) return obj.Items[i];
        return null;
    }

    public Kv3Node Need(Kv3Node obj, string key) => Get(obj, key) ?? throw new InvalidDataException($"KV3: missing '{key}'");

    public void Replace(Kv3Node obj, string key, Kv3Node value)
    {
        for (var i = 0; i < obj.Items!.Count; i++)
            if (StringOf(obj.KeyIds![i]) == key) { obj.Items[i] = value; return; }
        throw new InvalidDataException($"KV3: missing '{key}'");
    }

    public static double AsDouble(Kv3Node n) => n.Type switch
    {
        Kv3Type.Double => BitConverter.UInt64BitsToDouble(n.Bits),
        Kv3Type.Float => BitConverter.UInt32BitsToSingle((uint)n.Bits),
        Kv3Type.DoubleZero or Kv3Type.Int64Zero => 0,
        Kv3Type.DoubleOne or Kv3Type.Int64One => 1,
        _ => AsLong(n),
    };

    public static long AsLong(Kv3Node n) => n.Type switch
    {
        Kv3Type.Int64Zero or Kv3Type.BooleanFalse => 0,
        Kv3Type.Int64One or Kv3Type.BooleanTrue => 1,
        Kv3Type.Int32 => (int)(uint)n.Bits,
        Kv3Type.Int16 => (short)(ushort)n.Bits,
        Kv3Type.Int64 => (long)n.Bits,
        Kv3Type.Boolean or Kv3Type.Int32AsByte or Kv3Type.UInt16 or Kv3Type.UInt32 or Kv3Type.UInt64 => (long)n.Bits,
        Kv3Type.DoubleZero => 0,
        Kv3Type.DoubleOne => 1,
        _ => throw new InvalidDataException($"KV3: {n.Type} is not a number"),
    };

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

    public static Kv3Node Bool(bool v) => new() { Type = v ? Kv3Type.BooleanTrue : Kv3Type.BooleanFalse };

    /// <summary>An integer in the same width family as <paramref name="like"/>, in RC's form.</summary>
    public static Kv3Node IntLike(Kv3Node like, long v) => like.Type switch
    {
        Kv3Type.Int64 or Kv3Type.Int64Zero or Kv3Type.Int64One =>
            v == 0 ? new() { Type = Kv3Type.Int64Zero } : v == 1 ? new() { Type = Kv3Type.Int64One } : new() { Type = Kv3Type.Int64, Bits = (ulong)v },
        Kv3Type.Int32 or Kv3Type.UInt32 or Kv3Type.Int16 or Kv3Type.UInt16 or Kv3Type.Int32AsByte or Kv3Type.UInt64 =>
            new() { Type = like.Type, Bits = like.Type == Kv3Type.Int32 ? (uint)(int)v : (ulong)v },
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
    }

    private sealed class ReadState
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
        public int Depth;
    }

    private static InvalidDataException Bad(string what) => new($"KV3 v5: truncated or malformed ({what})");

    private static int Align(int o, int a) => (o + a - 1) & ~(a - 1);

    public static Kv3Tree Read(ReadOnlySpan<byte> span)
    {
        var block = span.ToArray();
        if (block.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic5)
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
        using var zstd = t.Compression == 2 ? new ZstdSharp.Decompressor() : null;
        int countBytes1 = h[7], countBytes4 = h[8], countBytes8 = h[9], countTypes = h[10];
        int countBlocks = h[14], sizeBlobs = h[15], countBytes2 = h[16];
        int unc1 = h[18], cmp1 = h[19], unc2 = h[20], cmp2 = h[21];
        int b2Bytes1 = h[22], b2Bytes2 = h[23], b2Bytes4 = h[24], b2Bytes8 = h[25], b2Objects = h[27];

        var pos = HeaderSize;
        byte[] Take(int unc, int cmp)
        {
            var o = new byte[unc];
            if (t.Compression == 0) { block.AsSpan(pos, unc).CopyTo(o); pos += unc; return o; }
            var n = zstd is null ? LZ4Codec.Decode(block.AsSpan(pos, cmp), o) : zstd.Unwrap(block.AsSpan(pos, cmp), o);
            if (n != unc) throw Bad("compressed buffer");
            pos += cmp;
            return o;
        }
        var buf1 = Take(unc1, cmp1);
        var buf2 = Take(unc2, cmp2);

        var s = new ReadState();
        // Buffer 1 (auxiliary): bytes1 (strings first) | bytes2 | bytes4 (string count first) | bytes8
        var a = s.Aux; a.Data = buf1;
        var off = 0;
        a.P1 = off; a.E1 = off += countBytes1;
        off = countBytes2 > 0 ? Align(off, 2) : off; a.P2 = off; a.E2 = off += countBytes2 * 2;
        off = countBytes4 > 0 ? Align(off, 4) : off; a.P4 = off; a.E4 = off += countBytes4 * 4;
        off = countBytes8 > 0 ? Align(off, 8) : off; a.P8 = off; a.E8 = off += countBytes8 * 8;
        var nStrings = (int)a.ReadB4();
        for (var i = 0; i < nStrings; i++)
        {
            var end = Array.IndexOf(buf1, (byte)0, a.P1, a.E1 - a.P1);
            if (end < 0) throw Bad("string table");
            t.Strings.Add(Encoding.UTF8.GetString(buf1, a.P1, end - a.P1));
            a.P1 = end + 1;
        }

        // Buffer 2 (main): object lengths | bytes1 | bytes2 | bytes4 | bytes8 | types | blob tail
        var m = s.Main; m.Data = buf2;
        off = 0;
        s.ObjLens = buf2; s.PO = 0; s.EO = off += b2Objects * 4;
        m.P1 = off; m.E1 = off += b2Bytes1;
        off = b2Bytes2 > 0 ? Align(off, 2) : off; m.P2 = off; m.E2 = off += b2Bytes2 * 2;
        off = b2Bytes4 > 0 ? Align(off, 4) : off; m.P4 = off; m.E4 = off += b2Bytes4 * 4;
        off = b2Bytes8 > 0 ? Align(off, 8) : off; m.P8 = off; m.E8 = off += b2Bytes8 * 8;
        s.Types = buf2; s.PT = off; s.ET = off += countTypes;
        if (countBlocks > 0)
        {
            s.BlobLens = new int[countBlocks];
            for (var i = 0; i < countBlocks; i++, off += 4) s.BlobLens[i] = BinaryPrimitives.ReadInt32LittleEndian(buf2.AsSpan(off));
            off += 4;   // trailer
            s.Blobs = new byte[sizeBlobs];
            if (t.Compression == 0) { block.AsSpan(pos, sizeBlobs).CopyTo(s.Blobs); pos += sizeBlobs; }
            else if (zstd is not null)
            {
                var cmpBlobs = h[13] - cmp1 - cmp2;   // one frame for all blobs, no per-frame size list
                if (zstd.Unwrap(block.AsSpan(pos, cmpBlobs), s.Blobs) != sizeBlobs) throw Bad("zstd blobs");
                pos += cmpBlobs;
            }
            else
            {
                var dec = new LZ4ChainDecoder(t.FrameSize, 0);
                var done = 0;
                while (done < sizeBlobs)
                {
                    var clen = BinaryPrimitives.ReadUInt16LittleEndian(buf2.AsSpan(off)); off += 2;
                    var frame = Math.Min(t.FrameSize, sizeBlobs - done);
                    if (!dec.DecodeAndDrain(block.AsSpan(pos, clen), s.Blobs.AsSpan(done, frame), out var n) || n < 1) throw Bad("lz4 blob frame");
                    pos += clen; done += n;
                }
            }
        }

        t.Root = t.ReadNode(s, ReadType(s, out var rootFlag), rootFlag);
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
            b &= 0x3F;
            flag = s.Types[s.PT++];
        }
        return (Kv3Type)b;
    }

    private Kv3Node ReadNode(ReadState s, Kv3Type type, byte flag)
    {
        if (++s.Depth > 512) throw new InvalidDataException("KV3 nesting exceeds 512 levels");
        try
        {
            var n = new Kv3Node { Type = type, Flag = flag };
            var l = s.Main;
            switch (type)
            {
                case Kv3Type.Null or Kv3Type.BooleanTrue or Kv3Type.BooleanFalse or Kv3Type.Int64Zero or Kv3Type.Int64One
                    or Kv3Type.DoubleZero or Kv3Type.DoubleOne:
                    break;
                case Kv3Type.Boolean or Kv3Type.Int32AsByte: n.Bits = l.ReadB1(); break;
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
                    var count = (int)l.ReadB4();
                    n.Items = new(count);
                    for (var i = 0; i < count; i++) n.Items.Add(ReadNode(s, ReadType(s, out var f), f));
                    break;
                }
                case Kv3Type.ArrayTyped or Kv3Type.ArrayTypeByteLength or Kv3Type.ArrayTypeAuxiliaryBuffer:
                {
                    var count = type == Kv3Type.ArrayTyped ? (int)l.ReadB4() : l.ReadB1();
                    n.ElementType = ReadType(s, out var ef);
                    n.ElementFlag = ef;
                    n.Items = new(count);
                    // An auxiliary array's elements live in the other buffer; swap for their duration.
                    if (type == Kv3Type.ArrayTypeAuxiliaryBuffer) (s.Main, s.Aux) = (s.Aux, s.Main);
                    for (var i = 0; i < count; i++) n.Items.Add(ReadNode(s, n.ElementType, ef));
                    if (type == Kv3Type.ArrayTypeAuxiliaryBuffer) (s.Main, s.Aux) = (s.Aux, s.Main);
                    break;
                }
                case Kv3Type.Object:
                {
                    if (s.PO + 4 > s.EO) throw Bad("object lengths");
                    var count = BinaryPrimitives.ReadInt32LittleEndian(s.ObjLens.AsSpan(s.PO)); s.PO += 4;
                    n.Items = new(count);
                    n.KeyIds = new(count);
                    for (var i = 0; i < count; i++)
                    {
                        var vt = ReadType(s, out var f);
                        n.KeyIds.Add((int)s.Main.ReadB4());
                        n.Items.Add(ReadNode(s, vt, f));
                    }
                    break;
                }
                default:
                    throw new NotSupportedException($"KV3 node type {(int)type}");
            }
            return n;
        }
        finally { s.Depth--; }
    }

    // ---- writing

    private sealed class Out
    {
        public MemoryStream B1 = new(), B2 = new(), B4 = new(), B8 = new();
        public int Arrays, Objects;
    }

    private sealed class WriteState
    {
        public Out Main = new(), Aux = new();
        public MemoryStream Types = new(), ObjLens = new();
        public List<byte[]> Blobs = [];
        public int TotalObjects, TotalArrays;
        // The two undocumented buffer-2 counters, fitted exactly over ~20,000 stock v5 blocks: arrays
        // whose elements land in buffer 2 (plus long auxiliary ones), and their element counts, a
        // generic array counting max(1, length). Plus the count of nodes with their own type byte.
        public bool Swapped;
        public int MainArrays, MainElements, Described;
    }

    private static void Put(MemoryStream ms, ulong v, int width)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        ms.Write(b[..width]);
    }

    private static void WriteType(WriteState s, Kv3Type type, byte flag, bool element = false)
    {
        if (!element) s.Described++;
        if (flag == 0) { s.Types.WriteByte((byte)type); return; }
        s.Types.WriteByte((byte)((byte)type | 0x80));
        s.Types.WriteByte(flag);
    }

    private static void WriteNode(WriteState s, Kv3Node n)
    {
        var o = s.Main;
        switch (n.Type)
        {
            case Kv3Type.Null or Kv3Type.BooleanTrue or Kv3Type.BooleanFalse or Kv3Type.Int64Zero or Kv3Type.Int64One
                or Kv3Type.DoubleZero or Kv3Type.DoubleOne:
                break;
            case Kv3Type.Boolean or Kv3Type.Int32AsByte: Put(o.B1, n.Bits, 1); break;
            case Kv3Type.Int16 or Kv3Type.UInt16: Put(o.B2, n.Bits, 2); break;
            case Kv3Type.Int32 or Kv3Type.UInt32 or Kv3Type.Float: Put(o.B4, n.Bits, 4); break;
            case Kv3Type.Int64 or Kv3Type.UInt64 or Kv3Type.Double: Put(o.B8, n.Bits, 8); break;
            case Kv3Type.String: Put(o.B4, (uint)n.StringId, 4); break;
            case Kv3Type.BinaryBlob: s.Blobs.Add(n.Blob ?? []); break;
            case Kv3Type.Array:
                o.Arrays++; s.TotalArrays++;
                if (!s.Swapped)
                {
                    s.MainArrays++;
                    s.MainElements += Math.Max(1, n.Items!.Count);
                }
                Put(o.B4, (uint)n.Items!.Count, 4);
                foreach (var c in n.Items) { WriteType(s, c.Type, c.Flag); WriteNode(s, c); }
                break;
            case Kv3Type.ArrayTyped or Kv3Type.ArrayTypeByteLength or Kv3Type.ArrayTypeAuxiliaryBuffer:
                o.Arrays++; s.TotalArrays++;
                if (n.Type == Kv3Type.ArrayTyped) Put(o.B4, (uint)n.Items!.Count, 4);
                else
                {
                    if (n.Items!.Count > 255) throw new InvalidDataException($"{n.Type} holds at most 255 elements, not {n.Items.Count}");
                    Put(o.B1, (uint)n.Items.Count, 1);
                }
                WriteType(s, n.ElementType, n.ElementFlag, element: true);
                if (n.Type == Kv3Type.ArrayTypeAuxiliaryBuffer) { (s.Main, s.Aux) = (s.Aux, s.Main); s.Swapped = !s.Swapped; }
                if (!s.Swapped || (n.Type == Kv3Type.ArrayTypeAuxiliaryBuffer && n.Items.Count >= AuxCountedFrom)) { s.MainArrays++; s.MainElements += n.Items.Count; }
                foreach (var c in n.Items!)
                {
                    if (c.Type != n.ElementType) throw new InvalidDataException($"typed array of {n.ElementType} holds a {c.Type}");
                    WriteNode(s, c);
                }
                if (n.Type == Kv3Type.ArrayTypeAuxiliaryBuffer) { (s.Main, s.Aux) = (s.Aux, s.Main); s.Swapped = !s.Swapped; }
                break;
            case Kv3Type.Object:
                o.Objects++; s.TotalObjects++;
                Put(s.ObjLens, (uint)n.Items!.Count, 4);
                for (var i = 0; i < n.Items.Count; i++)
                {
                    WriteType(s, n.Items[i].Type, n.Items[i].Flag);
                    Put(s.Main.B4, (uint)n.KeyIds![i], 4);
                    WriteNode(s, n.Items[i]);
                }
                break;
            default:
                throw new NotSupportedException($"KV3 node type {(int)n.Type}");
        }
    }

    private static void Pad(MemoryStream ms, int align)
    {
        while (ms.Length % align != 0) ms.WriteByte(0);
    }

    /// <summary>The uncompressed sections as they would be written, for parity checks
    /// (LZ4 output is encoder-defined, so compressed bytes are never compared).</summary>
    public (byte[] Buffer1, byte[] Buffer2, byte[] Blobs, int[] Header) Encode()
    {
        var s = new WriteState();
        WriteType(s, Root.Type, Root.Flag);
        WriteNode(s, Root);

        // Buffer 1: strings lead bytes1 and the string count leads bytes4.
        var b1 = new MemoryStream();
        foreach (var str in Strings) { b1.Write(Encoding.UTF8.GetBytes(str)); b1.WriteByte(0); }
        var aux = s.Aux;
        aux.B1.WriteTo(b1);
        var countBytes1 = (int)b1.Length;
        if (aux.B2.Length > 0) { Pad(b1, 2); aux.B2.WriteTo(b1); }
        Pad(b1, 4);
        Put(b1, (uint)Strings.Count, 4);
        aux.B4.WriteTo(b1);
        if (aux.B8.Length > 0) { Pad(b1, 8); aux.B8.WriteTo(b1); }

        var main = s.Main;
        var b2 = new MemoryStream();
        s.ObjLens.WriteTo(b2);
        main.B1.WriteTo(b2);
        if (main.B2.Length > 0) { Pad(b2, 2); main.B2.WriteTo(b2); }
        if (main.B4.Length > 0) { Pad(b2, 4); main.B4.WriteTo(b2); }
        if (main.B8.Length > 0) { Pad(b2, 8); main.B8.WriteTo(b2); }
        s.Types.WriteTo(b2);
        var blobData = new MemoryStream();
        if (s.Blobs.Count > 0)
        {
            foreach (var bl in s.Blobs) { Put(b2, (uint)bl.Length, 4); blobData.Write(bl); }
            Put(b2, Trailer, 4);
        }
        else Put(b2, Trailer, 4);

        var h = new int[30];
        h[7] = countBytes1;
        h[8] = 1 + (int)(aux.B4.Length / 4);
        h[9] = (int)(aux.B8.Length / 8);
        h[10] = (int)s.Types.Length;
        h[11] = (s.TotalObjects & 0xFFFF) | (s.TotalArrays << 16);   // the two u16 counters at offsets 44 and 46
        h[14] = s.Blobs.Count;
        h[15] = (int)blobData.Length;
        h[16] = (int)(aux.B2.Length / 2);
        h[18] = (int)b1.Length;
        h[20] = (int)b2.Length;
        h[22] = (int)main.B1.Length;
        h[23] = (int)(main.B2.Length / 2);
        h[24] = (int)(main.B4.Length / 4);
        h[25] = (int)(main.B8.Length / 8);
        h[26] = s.Described;
        h[27] = s.TotalObjects;
        h[28] = s.MainArrays;
        h[29] = s.MainElements;
        return (b1.ToArray(), b2.ToArray(), blobData.ToArray(), h);
    }

    /// <summary>
    /// The complete block. <paramref name="compression"/> defaults to what was read (LZ4 for a
    /// new tree); Zstd reads are written as LZ4, which the engine loads just the same. LZ4 is
    /// encoder-defined, so compressed bytes differ from Valve's while every decoded section and
    /// counter matches (<see cref="Encode"/>).
    /// </summary>
    public byte[] Write(uint? compression = null)
    {
        var method = compression ?? (Compression == 0 ? 0u : 1u);
        if (method > 1) throw new NotSupportedException("writes are uncompressed or LZ4");
        var (b1, b2Body, blobs, h) = Encode();
        var frame = method == 1 ? (FrameSize == 0 ? (ushort)16384 : FrameSize) : (ushort)0;

        // LZ4 blobs are compressed in chained frames; each frame's size trails buffer 2.
        var b2 = new MemoryStream();
        b2.Write(b2Body);
        var blobOut = new MemoryStream();
        var frameSizes = 0;
        if (blobs.Length > 0 && method == 1)
        {
            using var enc = new LZ4FastChainEncoder(frame, 0);
            var tmp = new byte[LZ4Codec.MaximumOutputSize(frame)];
            for (var done = 0; done < blobs.Length;)
            {
                var n = Math.Min(frame, blobs.Length - done);
                var action = enc.TopupAndEncode(blobs.AsSpan(done, n), tmp, forceEncode: true, allowCopy: false, out var loaded, out var encoded);
                if (loaded != n || encoded <= 0 || encoded > ushort.MaxValue) throw new InvalidDataException($"LZ4 blob frame failed ({action})");
                Put(b2, (uint)encoded, 2);
                blobOut.Write(tmp, 0, encoded);
                frameSizes++;
                done += n;
            }
        }
        else if (blobs.Length > 0) blobOut.Write(blobs);
        var b2Bytes = b2.ToArray();

        byte[] Pack(byte[] raw) => method == 0 ? raw : Lz4(raw);
        var c1 = Pack(b1);
        var c2 = Pack(b2Bytes);
        h[0] = unchecked((int)Magic5);
        h[5] = (int)method;
        h[6] = frame << 16;
        h[12] = b1.Length + b2Bytes.Length;
        // Bytes stored; compressed blobs count, raw ones do not (vpost_c LUTs, and Zstd reads rely on it).
        h[13] = c1.Length + c2.Length + (method == 0 ? 0 : (int)blobOut.Length);
        h[17] = frameSizes * 2;
        h[18] = b1.Length;
        h[19] = method == 0 ? 0 : c1.Length;
        h[20] = b2Bytes.Length;
        h[21] = method == 0 ? 0 : c2.Length;

        var o = new MemoryStream();
        var head = new byte[HeaderSize];
        for (var i = 0; i < 30; i++) BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(i * 4), h[i]);
        Format.TryWriteBytes(head.AsSpan(4, 16));
        o.Write(head);
        o.Write(c1);
        o.Write(c2);
        blobOut.WriteTo(o);
        if (blobs.Length > 0 || h[14] > 0) Put(o, Trailer, 4);
        return o.ToArray();
    }

    private static byte[] Lz4(byte[] raw)
    {
        var dst = new byte[LZ4Codec.MaximumOutputSize(raw.Length)];
        var n = LZ4Codec.Encode(raw, dst, LZ4Level.L00_FAST);
        if (n <= 0) throw new InvalidDataException("LZ4 encode failed");
        return dst[..n];
    }
}
