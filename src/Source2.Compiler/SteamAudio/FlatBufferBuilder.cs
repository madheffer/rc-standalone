using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Source2.Compiler.SteamAudio;

/// <summary>
/// The FlatBuffers C++ <c>FlatBufferBuilder</c> phonon serializes with, so a buffer comes out byte for byte as
/// phonon's: built back to front; scalars aligned to their size and zero padded; a vector pre-aligned so its
/// length word lands on 4 and its elements on their own alignment; a table's fields tracked by vtable offset and
/// its vtable deduplicated against every vtable written before (oldest first), else written just before the
/// table; a field equal to its default left out (force_defaults off); <c>Finish</c> aligning the root offset to
/// the largest alignment seen. Offsets are as the C++ builder hands them out: the buffer size at the object's
/// start, counted from the end.
/// </summary>
public sealed class FlatBufferBuilder
{
    private byte[] _buf = new byte[1024];
    private int _head = 1024; // the buffer's data is _buf[_head..]
    private int _minAlign = 1;
    private readonly List<(int Off, ushort Field)> _fields = [];
    private ushort _maxField;
    private readonly List<int> _vtables = [];
    private int _tableStart;

    private int Size => _buf.Length - _head;

    private void Reserve(int n)
    {
        if (_head >= n)
            return;
        var grow = Math.Max(_buf.Length, n);
        var next = new byte[_buf.Length + grow];
        Buffer.BlockCopy(_buf, _head, next, next.Length - Size, Size);
        _head = next.Length - Size;
        _buf = next;
    }

    private void Pad(int n)
    {
        Reserve(n);
        for (var i = 0; i < n; i++)
            _buf[--_head] = 0;
    }

    private static int PaddingBytes(int size, int align) => (~size + 1) & (align - 1);

    private void Align(int size)
    {
        if (size > _minAlign)
            _minAlign = size;
        Pad(PaddingBytes(Size, size));
    }

    private void PreAlign(int len, int align)
    {
        if (len == 0)
            return;
        if (align > _minAlign)
            _minAlign = align;
        Pad(PaddingBytes(Size + len, align));
    }

    private Span<byte> Push(int n)
    {
        Reserve(n);
        _head -= n;
        return _buf.AsSpan(_head, n);
    }

    private int PushInt(int v)
    {
        Align(4);
        BinaryPrimitives.WriteInt32LittleEndian(Push(4), v);
        return Size;
    }

    private int ReferTo(int off)
    {
        Align(4);
        return Size - off + 4;
    }

    public void StartTable()
    {
        _fields.Clear();
        _maxField = 0;
        _tableStart = Size;
    }

    private void Track(ushort field, int off)
    {
        _fields.Add((off, field));
        if (field > _maxField)
            _maxField = field;
    }

    /// <summary>Field <paramref name="index"/> of the schema (0-based), as a vtable offset.</summary>
    private static ushort Field(int index) => (ushort)((index + 2) * 2);

    public void AddInt(int index, int value, int def = 0)
    {
        if (value == def)
            return;
        Track(Field(index), PushInt(value));
    }

    public void AddFloat(int index, float value, float def = 0)
    {
        if (BitConverter.SingleToInt32Bits(value) == BitConverter.SingleToInt32Bits(def))
            return;
        Align(4);
        BinaryPrimitives.WriteSingleLittleEndian(Push(4), value);
        Track(Field(index), Size);
    }

    public void AddShort(int index, short value, short def = 0)
    {
        if (value == def)
            return;
        Align(2);
        BinaryPrimitives.WriteInt16LittleEndian(Push(2), value);
        Track(Field(index), Size);
    }

    public void AddByte(int index, byte value, byte def = 0)
    {
        if (value == def)
            return;
        Align(1);
        Push(1)[0] = value;
        Track(Field(index), Size);
    }

    /// <summary>An offset field; 0 (no object) is left out.</summary>
    public void AddOffset(int index, int off)
    {
        if (off == 0)
            return;
        Track(Field(index), PushInt(ReferTo(off)));
    }

    /// <summary>A struct field, copied in place (<paramref name="align"/>: the struct's alignment).</summary>
    public void AddStruct(int index, ReadOnlySpan<byte> value, int align)
    {
        Align(align);
        value.CopyTo(Push(value.Length));
        Track(Field(index), Size);
    }

    public int EndTable()
    {
        var vtableLoc = PushInt(0);
        var vlen = Math.Max((ushort)(_maxField + 2), (ushort)4);
        Pad(vlen);
        var vt = _buf.AsSpan(_head, vlen);
        BinaryPrimitives.WriteUInt16LittleEndian(vt[2..], (ushort)(vtableLoc - _tableStart));
        BinaryPrimitives.WriteUInt16LittleEndian(vt, vlen);
        foreach (var (off, field) in _fields)
            BinaryPrimitives.WriteUInt16LittleEndian(vt[field..], (ushort)(vtableLoc - off));
        _fields.Clear();
        _maxField = 0;
        var use = Size;
        foreach (var old in _vtables)
        {
            var other = _buf.AsSpan(_buf.Length - old);
            var olen = BinaryPrimitives.ReadUInt16LittleEndian(other);
            if (olen != vlen || !other[..olen].SequenceEqual(vt))
                continue;
            use = old;
            _head += Size - vtableLoc;
            break;
        }
        if (use == Size)
            _vtables.Add(use);
        BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(_buf.Length - vtableLoc), use - vtableLoc);
        return vtableLoc;
    }

    private void StartVector(int len, int elemSize, int align)
    {
        PreAlign(len * elemSize, 4);
        PreAlign(len * elemSize, align);
    }

    private int EndVector(int len) => PushInt(len);

    /// <summary>A vector of scalars or structs, as bytes (<paramref name="elemSize"/> each, aligned to <paramref name="align"/>).</summary>
    public int CreateVector(ReadOnlySpan<byte> bytes, int elemSize, int align)
    {
        var len = bytes.Length / elemSize;
        StartVector(len, elemSize, align);
        bytes.CopyTo(Push(bytes.Length));
        return EndVector(len);
    }

    public int CreateVector<T>(ReadOnlySpan<T> values) where T : unmanaged
    {
        var size = Marshal.SizeOf<T>();
        return CreateVector(MemoryMarshal.AsBytes(values), size, size);
    }

    /// <summary>A vector of offsets (0 for a null element is not allowed by the C++ builder's ReferTo either).</summary>
    public int CreateOffsetVector(IReadOnlyList<int> offsets)
    {
        StartVector(offsets.Count, 4, 4);
        for (var i = offsets.Count; i > 0;)
            PushInt(ReferTo(offsets[--i]));
        return EndVector(offsets.Count);
    }

    public byte[] Finish(int root)
    {
        _vtables.Clear();
        PreAlign(4, _minAlign);
        PushInt(ReferTo(root));
        return _buf.AsSpan(_head).ToArray();
    }
}
