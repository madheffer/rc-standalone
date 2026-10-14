using Source2.Compiler.Kv3;
using ValveKeyValue;

namespace Source2.Compiler;

/// <summary>
/// Binary KV3 version 5 as tier0's SaveKV3 writes it (tier0 1800c7220 and the
/// node writers 1800c6c20 / 1800c64b0 / 1800c6070 / 1800c5ea0), so a block
/// comes out byte for byte as resourcecompiler's:
/// <list type="bullet">
/// <item>ints that fit 32 bits are INT32 / UINT32, 0 and 1 the zero and one
/// types (an unsigned 0 or 1 too), doubles 0 and 1 likewise; an int read as
/// INT8 or UINT8 (<see cref="Kv3WireTypes"/>) stays one while it fits;</item>
/// <item>a non-empty array whose elements share one kind is typed: under 256
/// elements a byte length, ints, uints and doubles in the auxiliary buffer
/// (25), others in the main one (24); from 256 on ARRAY_TYPED (10). The
/// element type is the one every element encodes as, else the kind's own;</item>
/// <item>string ids, array lengths and member names always go to the main
/// buffer; strings get ids in first use order, an empty one -1;</item>
/// <item>the header counts nodes, objects, arrays, and the arrays (with their
/// elements) that are neither native nor short auxiliary ones;</item>
/// <item>the block is compressed as binary_auto chooses (see <see cref="Write"/>).</item>
/// </list>
/// A float array stands for a natively typed one (FLOAT elements only come from
/// those), copied to the auxiliary buffer.
/// </summary>
public static class Kv3V5Writer
{
    // KeyValues3's own kinds (the type field 1801... reads, bits 2-5). Numerically the node types
    // of the same name.
    private enum Kind { Null = 1, Bool, Int, UInt, Double, String, Blob, Array, Table }

    private sealed class Context
    {
        public readonly Kv3V5Block Block = new();
        private readonly Dictionary<string, int> _strings = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];

        public IReadOnlyList<string> Strings => _order;

        public int StringId(string s)
        {
            if (string.IsNullOrEmpty(s))
                return -1;
            if (!_strings.TryGetValue(s, out var id))
            {
                id = _order.Count;
                _strings[s] = id;
                _order.Add(s);
            }
            return id;
        }
    }

    private static Kind KindOf(KVObject v) => v.ValueType switch
    {
        KVValueType.Null => Kind.Null,
        KVValueType.Boolean => Kind.Bool,
        KVValueType.Int16 or KVValueType.Int32 or KVValueType.Int64 => Kind.Int,
        KVValueType.UInt16 or KVValueType.UInt32 or KVValueType.UInt64 => Kind.UInt,
        KVValueType.FloatingPoint or KVValueType.FloatingPoint64 => Kind.Double,
        KVValueType.String => Kind.String,
        KVValueType.BinaryBlob => Kind.Blob,
        KVValueType.Array => Kind.Array,
        KVValueType.Collection => Kind.Table,
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
    private static Kv3Type ArrayType(List<KVObject> items)
    {
        if (items.Count == 0)
            return Kv3Type.Array;
        var kind = KindOf(items[0]);
        if (items.Any(x => KindOf(x) != kind))
            return Kv3Type.Array;
        if (items.Count >= 0x100)
            return Kv3Type.ArrayTyped;
        return kind is Kind.Int or Kind.UInt or Kind.Double ? Kv3Type.ArrayTypeAuxiliaryBuffer : Kv3Type.ArrayTypeByteLength;
    }

    // 1800c6c20's choice for a node written with its own type byte.
    private static Kv3Type NodeType(KVObject v)
    {
        switch (KindOf(v))
        {
            case Kind.Bool:
                return (bool)v ? Kv3Type.BooleanTrue : Kv3Type.BooleanFalse;
            case Kind.Int:
            {
                var x = Int(v);
                return x == 0 ? Kv3Type.Int64Zero : x == 1 ? Kv3Type.Int64One
                    : Narrow(v) is { } w ? w : x == (int)x ? Kv3Type.Int32 : Kv3Type.Int64;
            }
            case Kind.UInt:
            {
                var x = UInt(v);
                return x == 0 ? Kv3Type.Int64Zero : x == 1 ? Kv3Type.Int64One
                    : Narrow(v) is { } w ? w : x == (uint)x ? Kv3Type.UInt32 : Kv3Type.UInt64;
            }
            case Kind.Double:
            {
                var x = Double(v);
                return x == 0.0 ? Kv3Type.DoubleZero : x == 1.0 ? Kv3Type.DoubleOne : Kv3Type.Double;
            }
            case Kind.Array:
                return ArrayType(Items(v));
            default:
                return (Kv3Type)KindOf(v);
        }
    }

    // 1800c6070's per-element type inside a typed array.
    private static Kv3Type ElementType(KVObject v)
    {
        switch (KindOf(v))
        {
            case Kind.Bool:
                return (bool)v ? Kv3Type.BooleanTrue : Kv3Type.BooleanFalse;
            case Kind.Int:
                return Narrow(v) ?? (Int(v) == (int)Int(v) ? Kv3Type.Int32 : Kv3Type.Int64);
            case Kind.UInt:
                return Narrow(v) ?? (UInt(v) == (uint)UInt(v) ? Kv3Type.UInt32 : Kv3Type.UInt64);
            case Kind.Array:
                return ArrayType(Items(v));
            default:
                return (Kv3Type)KindOf(v);
        }
    }

    // INT8 / UINT8 for an integer read as one (Kv3WireTypes) that still fits.
    private static Kv3Type? Narrow(KVObject v) =>
        Kv3WireTypes.Get(v) is var t and > 0 && Kv3WireTypes.Fits(v, t) ? (Kv3Type)t : null;

    private static void Node(Context c, KVObject v)
    {
        var type = NodeType(v);
        c.Block.Type((int)type, (byte)v.Flag);
        c.Block.Nodes++;
        Value(c, c.Block.Main, v, type);
    }

    // 1800c64b0: one value of encoded type <type> into <lanes> (strings, lengths and names to the main lanes).
    private static void Value(Context c, Kv3V5Block.Lanes lanes, KVObject v, Kv3Type type)
    {
        var b = c.Block;
        switch (type)
        {
            case Kv3Type.Null or Kv3Type.BooleanTrue or Kv3Type.BooleanFalse or Kv3Type.Int64Zero or Kv3Type.Int64One
                or Kv3Type.DoubleZero or Kv3Type.DoubleOne:
                return;
            case Kv3Type.Boolean:
                lanes.Put((bool)v ? 1u : 0u, 1);
                return;
            case Kv3Type.Int64:
                lanes.Put((ulong)Int(v), 8);
                return;
            case Kv3Type.UInt64:
                lanes.Put(UInt(v), 8);
                return;
            case Kv3Type.Double:
                lanes.Put(BitConverter.DoubleToUInt64Bits(Double(v)), 8);
                return;
            case Kv3Type.String:
                b.Main.Put((uint)c.StringId((string)v!), 4);
                return;
            case Kv3Type.BinaryBlob:
                b.Blobs.Add(v.AsBlob());
                return;
            case Kv3Type.Array:
            {
                var items = Items(v);
                b.Main.Put((uint)items.Count, 4);
                b.Arrays++;
                b.ArraysMain++;
                b.ElementsMain += items.Count == 0 ? 1 : items.Count;
                foreach (var item in items)
                    Node(c, item);
                return;
            }
            case Kv3Type.Object:
            {
                b.Objects++;
                b.ObjectLengths.Add(v.Count);
                foreach (var (key, member) in v)
                {
                    b.Main.Put((uint)c.StringId(key), 4);
                    Node(c, member);
                }
                return;
            }
            case Kv3Type.ArrayTyped or Kv3Type.ArrayTypeByteLength or Kv3Type.ArrayTypeAuxiliaryBuffer:
                TypedArray(c, Items(v), type);
                return;
            case Kv3Type.Int32:
                lanes.Put((uint)(int)Int(v), 4);
                return;
            case Kv3Type.UInt32:
                lanes.Put((uint)UInt(v), 4);
                return;
            case Kv3Type.Float:
                lanes.Put(BitConverter.SingleToUInt32Bits((float)v), 4);
                return;
            case Kv3Type.Int16:
                lanes.Put((ushort)(short)Int(v), 2);
                return;
            case Kv3Type.UInt16:
                lanes.Put((ushort)UInt(v), 2);
                return;
            case Kv3Type.Int8:
                lanes.Put((byte)(KindOf(v) == Kind.Int ? (sbyte)Int(v) : (sbyte)UInt(v)), 1);
                return;
            case Kv3Type.UInt8:
                lanes.Put(KindOf(v) == Kind.Int ? (byte)Int(v) : (byte)UInt(v), 1);
                return;
            default:
                throw new NotSupportedException($"KV3 node type {type}");
        }
    }

    private static void TypedArray(Context c, List<KVObject> items, Kv3Type type)
    {
        var b = c.Block;
        if (type == Kv3Type.ArrayTyped)
            b.Main.Put((uint)items.Count, 4);
        else
            b.Main.Put((byte)items.Count, 1);
        var native = items.All(x => x.ValueType == KVValueType.FloatingPoint);
        b.Arrays++;
        if (!native && (type != Kv3Type.ArrayTypeAuxiliaryBuffer || items.Count > 0x1f))
        {
            b.ArraysMain++;
            b.ElementsMain += items.Count;
        }
        if (native)
        {
            // 1800c5ea0: the element type, then the raw floats into the auxiliary lanes.
            b.Type((int)Kv3Type.Float, (byte)items[0].Flag);
            foreach (var x in items)
                b.Aux.Put(BitConverter.SingleToUInt32Bits((float)x), 4);
            return;
        }
        var lanes = type == Kv3Type.ArrayTypeAuxiliaryBuffer ? b.Aux : b.Main;
        var first = ElementType(items[0]);
        var element = items.All(x => ElementType(x) == first) ? first
            // a narrowed element next to one that no longer fits: all at the width the widest needs
            : IsNarrowable(first) && items.All(x => IsNarrowable(ElementType(x)))
                ? (KindOf(items[0]) == Kind.Int ? Kv3Type.Int32 : Kv3Type.UInt32)
                : (Kv3Type)KindOf(items[0]);
        b.Type((int)element, (byte)items[0].Flag);
        foreach (var x in items)
            Value(c, lanes, x, element);
    }

    private static bool IsNarrowable(Kv3Type t) => t is Kv3Type.Int8 or Kv3Type.UInt8 or Kv3Type.Int32 or Kv3Type.UInt32;

    /// <summary>
    /// The block's bytes: <paramref name="root"/> under <paramref name="format"/>,
    /// compressed with <paramref name="method"/>, or with null as binary_auto
    /// chooses (1800c7220): raw under 256 bytes of buffers and blobs, else LZ4 HC
    /// level 12 under 0x80001 and zstd level 7 from there, kept only if it saves a twentieth.
    /// </summary>
    public static byte[] Write(KVObject root, Guid format, Kv3Compression? method = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var c = new Context();
        Node(c, root);
        c.Block.Strings = c.Strings;
        return c.Block.Write(format, method);
    }
}
