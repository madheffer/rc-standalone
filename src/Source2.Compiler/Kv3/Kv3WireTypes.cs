using System.Runtime.CompilerServices;
using ValveKeyValue;

namespace Source2.Compiler;

/// <summary>
/// The binary KV3 wire type of integers that <see cref="KVObject"/> cannot state itself.
///
/// <para>Valve writes a schema field with its own width: a model's MDAT stores <c>m_cRenderColor</c> as
/// four UINT8 in an auxiliary-buffer array. <see cref="KVValueType"/> has no 8-bit types, so the reader
/// records INT8 and UINT8 here and <see cref="Kv3V5Writer"/> writes them back as they were, as long as
/// the value still fits. Before this, an edited MDAT came back with those arrays widened to UINT32
/// (349 of 2,612 stock model blocks, <c>s2c reencode-parity</c>).</para>
/// </summary>
public static class Kv3WireTypes
{
    /// <summary>KV3 node type INT8.</summary>
    public const byte Int8 = (byte)Kv3.Kv3Type.Int8;

    /// <summary>KV3 node type UINT8.</summary>
    public const byte UInt8 = (byte)Kv3.Kv3Type.UInt8;

    private static readonly ConditionalWeakTable<KVObject, StrongBox<byte>> Types = new();

    /// <summary>Record that <paramref name="value"/> was stored as <paramref name="type"/>.</summary>
    public static void Set(KVObject value, byte type) => Types.AddOrUpdate(value, new StrongBox<byte>(type));

    /// <summary>The recorded wire type of <paramref name="value"/>, or 0.</summary>
    public static byte Get(KVObject value) => Types.TryGetValue(value, out var t) ? t.Value : (byte)0;

    /// <summary>
    /// Give every integer of <paramref name="edited"/> with no recorded type the one its counterpart in
    /// <paramref name="original"/> (same keys, same indices) was stored with, when it is an integer of
    /// the same signedness that still fits. A field keeps its schema width through an edit that
    /// replaced its value or re-read the tree with a reader that drops it (VRF's).
    /// </summary>
    public static void Inherit(KVObject original, KVObject edited)
    {
        if (original.IsCollection && edited.IsCollection)
        {
            foreach (var (key, value) in edited)
                if (original.TryGetValue(key, out var was))
                    Inherit(was, value);
            return;
        }
        if (original.IsArray && edited.IsArray)
        {
            var a = original.Select(x => x.Value).ToList();
            var i = 0;
            foreach (var (_, value) in edited)
            {
                if (i < a.Count)
                    Inherit(a[i], value);
                i++;
            }
            return;
        }
        var type = Get(original);
        if (type != 0 && Get(edited) == 0 && Fits(edited, type))
            Set(edited, type);
    }

    /// <summary>Whether <paramref name="value"/> is an integer that <paramref name="type"/> can hold.</summary>
    internal static bool Fits(KVObject value, byte type)
    {
        switch (value.ValueType)
        {
            case KVValueType.Int16 or KVValueType.Int32 or KVValueType.Int64:
                long s = value.ValueType switch { KVValueType.Int16 => (short)value, KVValueType.Int32 => (int)value, _ => (long)value };
                return type == Int8 ? s is >= sbyte.MinValue and <= sbyte.MaxValue : type == UInt8 && s is >= 0 and <= byte.MaxValue;
            case KVValueType.UInt16 or KVValueType.UInt32 or KVValueType.UInt64:
                ulong u = value.ValueType switch { KVValueType.UInt16 => (ushort)value, KVValueType.UInt32 => (uint)value, _ => (ulong)value };
                return type == UInt8 ? u <= byte.MaxValue : type == Int8 && u <= (ulong)sbyte.MaxValue;
            default:
                return false;
        }
    }
}
