using System.Buffers.Binary;
using System.Text;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;

namespace Source2.Compiler;

/// <summary>
/// A resource laid out the way resourcecompiler lays out map resources
/// (measured on world node models, node files and world files):
/// <list type="bullet">
/// <item>the header, then the block table;</item>
/// <item>each block zero-padded to 16 bytes, except RERL, which is padded to 4;</item>
/// <item>RERL's entry array starts on an 8-byte boundary of the file, so its
/// offset field is 8 or 12;</item>
/// <item>nothing after the last block.</item>
/// </list>
/// VRF's own writer aligns every block to 16 and pads with "S2V" markers.
/// </summary>
public static class ValveLayout
{
    /// <summary>The resource's bytes, KV3 compression chosen as binary_auto chooses it.</summary>
    public static byte[] Serialize(Resource resource)
    {
        AuthoredKv3.ChooseCompression(resource);
        var count = resource.Blocks.Count;
        var output = new MemoryStream();
        var header = new byte[16 + 12 * count];
        output.Write(header);
        var table = new List<(BlockType Type, long Offset, long Size)>();
        foreach (var block in resource.Blocks)
        {
            var align = block.Type == BlockType.RERL ? 4 : 16;
            while (output.Position % align != 0)
                output.WriteByte(0);
            var start = output.Position;
            if (block is ResourceExtRefList rerl)
                output.Write(Rerl(rerl, start));
            else
                block.Serialize(output);
            table.Add((block.Type, start, output.Position - start));
        }
        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), resource.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)count);
        for (var i = 0; i < count; i++)
        {
            var at = 16 + 12 * i;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), (uint)table[i].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 4), (uint)(table[i].Offset - (at + 4)));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 8), (uint)table[i].Size);
        }
        return bytes;
    }

    /// <summary>
    /// RERL at file offset <paramref name="start"/>: i32 offset to the entries
    /// and i32 count, the entries (u64 id, i32 offset to the name, u32 zero)
    /// from the next 8-byte boundary, then the names, each NUL terminated.
    /// </summary>
    private static byte[] Rerl(ResourceExtRefList rerl, long start)
    {
        var refs = rerl.ResourceRefInfoList;
        var entries = 8;
        while ((start + entries) % 8 != 0)
            entries++;
        var names = entries + 16 * refs.Count;
        var body = new List<byte>(new byte[names]);
        BinaryPrimitives.WriteInt32LittleEndian(CollectionsMarshalSpan(body, 0), entries);
        BinaryPrimitives.WriteInt32LittleEndian(CollectionsMarshalSpan(body, 4), refs.Count);
        for (var i = 0; i < refs.Count; i++)
        {
            var at = entries + 16 * i;
            BinaryPrimitives.WriteUInt64LittleEndian(CollectionsMarshalSpan(body, at), refs[i].Id);
            BinaryPrimitives.WriteInt32LittleEndian(CollectionsMarshalSpan(body, at + 8), body.Count - (at + 8));
            body.AddRange(Encoding.UTF8.GetBytes(refs[i].Name));
            body.Add(0);
        }
        return [.. body];
    }

    private static Span<byte> CollectionsMarshalSpan(List<byte> list, int at)
        => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)[at..];
}
