using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using ValvePak;

namespace Source2.Compiler.Io;

/// <summary>
/// Writes a single-file VPK v2, the shape of a map package: every entry's data
/// in this file, none preloaded. Measured on Valve's map packages, which it
/// reproduces byte for byte:
/// <list type="bullet">
/// <item>the tree by extension, then folder, then name, each in reverse order
/// of first appearance in the data; a missing extension or folder is a single
/// space;</item>
/// <item>the data in ordinal full-path order, back to back;</item>
/// <item>a chunk section: per 1 MiB of data, archive 0x7FFF, hash type 1 and
/// the chunk's BLAKE3 cut to 16 bytes;</item>
/// <item>MD5s of the tree, of the chunk section and of everything before this
/// last one; then the signature section, empty (magic, version 1).</item>
/// </list>
/// </summary>
public static class VpkWriter
{
    private const uint Signature = 0x55AA1234;
    private const ushort InThisFile = 0x7FFF;
    private const int ChunkSize = 1 << 20;

    /// <summary>The package bytes for these entries, keyed by full path with '/' separators.</summary>
    public static byte[] Write(IReadOnlyDictionary<string, byte[]> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var names = entries.Keys.OrderBy(p => p, StringComparer.Ordinal).ToList();
        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        long at = 0;
        foreach (var name in names)
        {
            offsets[name] = checked((uint)at);
            at += entries[name].Length;
        }

        var tree = Tree(entries, offsets);
        var chunks = new MemoryStream();
        using var body = new MemoryStream();
        Span<byte> header = stackalloc byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)tree.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], checked((uint)at));
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)((at + ChunkSize - 1) / ChunkSize * 28));
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 48);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], 20);
        body.Write(header);
        body.Write(tree);
        foreach (var name in names)
            body.Write(entries[name]);

        var all = body.GetBuffer().AsSpan(28 + tree.Length, checked((int)at));
        Span<byte> record = stackalloc byte[12];
        for (var start = 0; start < all.Length; start += ChunkSize)
        {
            var length = Math.Min(ChunkSize, all.Length - start);
            BinaryPrimitives.WriteUInt16LittleEndian(record, InThisFile);
            BinaryPrimitives.WriteUInt16LittleEndian(record[2..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], (uint)start);
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], (uint)length);
            chunks.Write(record);
            chunks.Write(Blake3.Hasher.Hash(all.Slice(start, length)).AsSpan()[..16]);
        }
        var chunkSection = chunks.ToArray();
        body.Write(chunkSection);
        body.Write(MD5.HashData(tree));
        body.Write(MD5.HashData(chunkSection));
        body.Write(MD5.HashData(body.GetBuffer().AsSpan(0, (int)body.Length)));
        Span<byte> signature = stackalloc byte[20];
        signature.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(signature, Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(signature[4..], 1);
        body.Write(signature);
        return body.ToArray();
    }

    /// <summary>Every entry of a package, by full path with '/' separators.</summary>
    public static Dictionary<string, byte[]> ReadAll(string vpk)
    {
        using var package = new Package();
        package.Read(vpk);
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in VpkEntries.ByExtension(package).Values.SelectMany(list => list))
            entries[entry.GetFullPath().Replace((char)92, '/')] = VpkEntries.Read(package, entry);
        return entries;
    }

    private static byte[] Tree(IReadOnlyDictionary<string, byte[]> entries, Dictionary<string, uint> offsets)
    {
        // Walking the paths in data order, each new extension, folder and name
        // goes to the front of its list: every level comes out in reverse order
        // of first appearance (measured on atixref's 626 entries).
        var layout = new List<(string Ext, List<(string Folder, List<(string Name, string Path)> Files)> Folders)>();
        foreach (var path in entries.Keys.OrderBy(p => p, StringComparer.Ordinal))
        {
            var (ext, folder, name) = Split(path);
            var e = layout.FindIndex(x => x.Ext == ext);
            if (e < 0)
                layout.Insert(e = 0, (ext, []));
            var folders = layout[e].Folders;
            var f = folders.FindIndex(x => x.Folder == folder);
            if (f < 0)
                folders.Insert(f = 0, (folder, []));
            folders[f].Files.Insert(0, (name, path));
        }

        using var tree = new MemoryStream();
        Span<byte> record = stackalloc byte[18];
        foreach (var (ext, folders) in layout)
        {
            String(tree, ext);
            foreach (var (folder, files) in folders)
            {
                String(tree, folder);
                foreach (var (name, path) in files)
                {
                    String(tree, name);
                    var data = entries[path];
                    BinaryPrimitives.WriteUInt32LittleEndian(record, Crc32.HashToUInt32(data));
                    BinaryPrimitives.WriteUInt16LittleEndian(record[4..], 0);
                    BinaryPrimitives.WriteUInt16LittleEndian(record[6..], InThisFile);
                    BinaryPrimitives.WriteUInt32LittleEndian(record[8..], offsets[path]);
                    BinaryPrimitives.WriteUInt32LittleEndian(record[12..], (uint)data.Length);
                    BinaryPrimitives.WriteUInt16LittleEndian(record[16..], 0xFFFF);
                    tree.Write(record);
                }
                tree.WriteByte(0);
            }
            tree.WriteByte(0);
        }
        tree.WriteByte(0);
        return tree.ToArray();
    }

    // Extension, folder, base name as the tree stores them.
    private static (string Ext, string Folder, string Name) Split(string path)
    {
        var slash = path.LastIndexOf('/');
        var folder = slash < 0 ? "" : path[..slash];
        var file = path[(slash + 1)..];
        var dot = file.LastIndexOf('.');
        var (name, ext) = dot <= 0 ? (file, " ") : (file[..dot], file[(dot + 1)..]);
        return (ext.Length == 0 ? " " : ext, folder.Length == 0 ? " " : folder, name);
    }

    private static void String(Stream s, string value)
    {
        s.Write(Encoding.UTF8.GetBytes(value));
        s.WriteByte(0);
    }
}
