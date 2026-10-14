using ValveKeyValue;
using ValveKeyValue.KeyValues3;
using Source2.Compiler.Kv3.Reading;

namespace Source2.Compiler;

/// <summary>How a binary KV3 block's buffers are stored.</summary>
public enum Kv3Compression
{
    /// <summary>Stored as is.</summary>
    Uncompressed = 0,

    /// <summary>LZ4 (resourcecompiler: HC level 12).</summary>
    Lz4 = 1,

    /// <summary>Zstandard (resourcecompiler: level 7, with checksums).</summary>
    Zstd = 2,
}

/// <summary>
/// Binary KV3, read and written by this library alone: every version CS2 content uses (v1 to v5,
/// and the legacy VKV3 container) is read into a <see cref="KVObject"/> tree with the same value
/// types VRF's reader gives (<see cref="Kv3BinaryReader"/>, ported); trees are written as tier0's
/// SaveKV3 writes them (<see cref="Kv3V5Writer"/>).
/// </summary>
public static class Kv3Binary
{
    /// <summary>True when <paramref name="block"/> starts with a binary KV3 signature.</summary>
    public static bool IsKv3(ReadOnlySpan<byte> block) =>
        block.Length >= 4 && Kv3BinaryReader.IsBinaryKV3(BitConverter.ToUInt32(block));

    /// <summary>The block's tree and its header (format GUID).</summary>
    /// <exception cref="InvalidDataException">The bytes are not a well-formed KV3 block.</exception>
    public static KVDocument Read(byte[] block)
    {
        var reader = new Kv3BinaryReader { Offset = 0, Size = block.Length };
        using var r = new BinaryReader(new MemoryStream(block, writable: false));
        try
        {
            reader.Read(r);
        }
        catch (Exception ex) when (ex is EndOfStreamException or ArgumentException or IndexOutOfRangeException
                                       or InvalidCastException or OverflowException or KeyNotFoundException)
        {
            throw new InvalidDataException("Malformed KV3 block: " + ex.Message, ex);
        }
        return reader.Data;
    }

    /// <summary><paramref name="root"/> under <paramref name="format"/>, as tier0's binary_auto save writes it,
    /// in binary <paramref name="version"/> 5 (today's) or 4.</summary>
    public static byte[] Write(KVObject root, KV3ID format, Kv3Compression? compression = null, int version = 5) =>
        Kv3V5Writer.Write(root, format.Id, compression, version);

    /// <summary>The binary version of a KV3 block (1 to 5; 0 for the legacy VKV3 container).</summary>
    public static int Version(ReadOnlySpan<byte> block) =>
        block.Length >= 4 && BitConverter.ToUInt32(block) is var magic && (magic & 0xFFFFFF00) == 0x4B563300 ? (int)(magic & 0xFF) : 0;
}
