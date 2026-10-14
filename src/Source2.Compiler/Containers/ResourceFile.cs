using System.Buffers.Binary;
using System.Text;

namespace Source2.Compiler;

/// <summary>One block of a <see cref="ResourceFile"/>: its four-character type and its bytes.</summary>
/// <param name="Type">The type as written in the block table, e.g. <c>"DATA"</c>, <c>"RED2"</c>.</param>
/// <param name="Data">The block's bytes, exactly as stored.</param>
public sealed record ResourceBlock(string Type, byte[] Data);

/// <summary>
/// A compiled Source 2 resource read and written by this library alone, with no
/// ValveResourceFormat (docs/INDEPENDENCE.md, stage 1).
///
/// <para>The container: <c>u32 fileSize; u16 headerVersion (12); u16 version; u32 blockInfoOffset
/// (8); u32 blockCount;</c> then one 12-byte entry per block (<c>fourCC, u32 offset relative to the
/// field, u32 size</c>), then the blocks. <c>fileSize</c> ends at the last block; what follows it
/// (a texture's mips, a sound's samples) is the <see cref="Tail"/>.</para>
///
/// <para><see cref="Read"/> checks every count, offset and size against the file before it
/// allocates anything, so a hostile file is an <see cref="InvalidDataException"/>, never a huge
/// allocation (the failure <c>s2c fuzz</c> found in VRF's reader). <see cref="Write"/> lays the file
/// out as resourcecompiler does: blocks padded with zeros to 16 bytes,
/// RERL to 4 with its entry array on an 8-byte boundary of the file, nothing between the last
/// block and the tail.</para>
/// </summary>
public sealed class ResourceFile
{
    /// <summary>The only header version CS2 writes or reads.</summary>
    public const ushort HeaderVersion = 12;

    /// <summary>The resource version (u16 at offset 6), which is per type: 0 for vdata, 1 for most,
    /// 2 for vsvg, 4 or 5 for vsnd...</summary>
    public ushort Version { get; set; }

    /// <summary>The blocks, in table order.</summary>
    public List<ResourceBlock> Blocks { get; } = [];

    /// <summary>Bytes after <c>fileSize</c>: streamed texture mips or sound samples. Empty for most types.</summary>
    public byte[] Tail { get; set; } = [];

    /// <summary>The first block of type <paramref name="type"/>, or null.</summary>
    public ResourceBlock? this[string type] => Blocks.Find(b => b.Type == type);

    /// <summary>Read <paramref name="bytes"/>, checking the header and every block against the file.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a well-formed resource.</exception>
    public static ResourceFile Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16)
            throw new InvalidDataException($"A resource needs a 16-byte header; this is {bytes.Length} bytes.");
        var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var headerVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        var blockInfoOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var blockCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        if (headerVersion != HeaderVersion)
            throw new InvalidDataException($"Header version {headerVersion}, expected {HeaderVersion}: not a compiled resource.");
        if (fileSize > bytes.Length)
            throw new InvalidDataException($"The header declares {fileSize} bytes but the file has {bytes.Length}: truncated.");
        var table = 8L + blockInfoOffset;
        if (blockInfoOffset < 8 || table + 12L * blockCount > fileSize)
            throw new InvalidDataException($"{blockCount} block entries at {table} do not fit in {fileSize} bytes.");

        var file = new ResourceFile { Version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) };
        for (var i = 0; i < blockCount; i++)
        {
            var entry = (int)(table + 12 * i);
            var type = Encoding.ASCII.GetString(bytes.Slice(entry, 4));
            var offset = entry + 4L + BinaryPrimitives.ReadUInt32LittleEndian(bytes[(entry + 4)..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(entry + 8)..]);
            // A block of size 0 may point anywhere (a modern vsnd's empty DATA sits at the next block).
            if (size > 0 && offset + size > fileSize)
                throw new InvalidDataException($"Block {i} ({type}) spans {offset}..{offset + size}, past the declared {fileSize} bytes.");
            file.Blocks.Add(new ResourceBlock(type, size == 0 ? [] : bytes.Slice((int)offset, (int)size).ToArray()));
        }
        file.Tail = bytes[(int)fileSize..].ToArray();
        return file;
    }

    /// <summary>The file, laid out as resourcecompiler lays it out.</summary>
    /// <exception cref="InvalidOperationException">A block's type is not four ASCII characters.</exception>
    public byte[] Write()
    {
        foreach (var block in Blocks)
            if (block.Type is not { Length: 4 } || !block.Type.All(char.IsAscii))
                throw new InvalidOperationException($"A block type is four ASCII characters, not '{block.Type}'.");
        var output = new MemoryStream();
        output.Write(new byte[16 + 12 * Blocks.Count]);
        var placed = new (long Offset, long Size)[Blocks.Count];
        for (var i = 0; i < Blocks.Count; i++)
        {
            var block = Blocks[i];
            var align = block.Type == "RERL" ? 4 : 16;
            while (output.Position % align != 0)
                output.WriteByte(0);
            var start = output.Position;
            output.Write(block.Type == "RERL" ? ResourceRefs.Write(ResourceRefs.Read(block.Data), start) : block.Data);
            placed[i] = (start, output.Position - start);
        }
        var fileSize = output.Length;
        output.Write(Tail);
        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)fileSize);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), HeaderVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)Blocks.Count);
        for (var i = 0; i < Blocks.Count; i++)
        {
            var at = 16 + 12 * i;
            Encoding.ASCII.GetBytes(Blocks[i].Type).CopyTo(bytes, at);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 4), (uint)(placed[i].Offset - (at + 4)));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 8), (uint)placed[i].Size);
        }
        return bytes;
    }
}

/// <summary>
/// The RERL block: the resources a file references, each as its 64-bit id
/// (<see cref="Source2ResourceId.ForPath"/>) and its path.
/// <code>
/// i32 offset to the entries (from the block start); i32 count;
/// entries from the next 8-byte boundary of the FILE: { u64 id; i32 offset to the name (from that field); u32 0 }
/// names, each NUL-terminated
/// </code>
/// </summary>
public static class ResourceRefs
{
    /// <summary>The entries of a RERL block, checked against its bytes before anything is allocated.</summary>
    public static List<(ulong Id, string Name)> Read(ReadOnlySpan<byte> block)
    {
        if (block.Length < 8)
            throw new InvalidDataException("RERL block shorter than its header");
        var entries = BinaryPrimitives.ReadInt32LittleEndian(block);
        var count = BinaryPrimitives.ReadInt32LittleEndian(block[4..]);
        if (count < 0 || entries < 8 || (long)entries + 16L * count > block.Length)
            throw new InvalidDataException($"RERL claims {count} entries at {entries}, past its {block.Length} bytes");
        var list = new List<(ulong, string)>(count);
        for (var i = 0; i < count; i++)
        {
            var at = entries + 16 * i;
            var nameAt = at + 8L + BinaryPrimitives.ReadInt32LittleEndian(block[(at + 8)..]);
            if (nameAt < 0 || nameAt >= block.Length)
                throw new InvalidDataException($"RERL entry {i} names a string outside the block");
            var name = block[(int)nameAt..];
            var end = name.IndexOf((byte)0);
            list.Add((BinaryPrimitives.ReadUInt64LittleEndian(block[at..]), Encoding.UTF8.GetString(end < 0 ? name : name[..end])));
        }
        return list;
    }

    /// <summary>A RERL block to be placed at file offset <paramref name="start"/>.</summary>
    public static byte[] Write(IReadOnlyList<(ulong Id, string Name)> refs, long start)
    {
        var entries = 8;
        while ((start + entries) % 8 != 0)
            entries++;
        var body = new MemoryStream();
        body.Write(new byte[entries + 16 * refs.Count]);
        var bytes = body.GetBuffer();
        BinaryPrimitives.WriteInt32LittleEndian(bytes, entries);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), refs.Count);
        var names = new List<byte>();
        var nameBase = entries + 16 * refs.Count;
        for (var i = 0; i < refs.Count; i++)
        {
            var at = entries + 16 * i;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), refs[i].Id);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 8), nameBase + names.Count - (at + 8));
            names.AddRange(Encoding.UTF8.GetBytes(refs[i].Name));
            names.Add(0);
        }
        return [.. bytes.AsSpan(0, nameBase), .. names];
    }
}
