using System.Runtime.CompilerServices;
using ValveKeyValue;
using ValveKeyValue.KeyValues3;

namespace Source2.Compiler;

/// <summary>
/// A compiled resource opened for editing, by this library alone (no VRF).
///
/// <para>KV3 blocks are decoded on first use (<see cref="Kv3"/>) into trees that an edit changes
/// in place. <see cref="Write"/> then writes each block as resourcecompiler would: a block whose
/// tree is unchanged goes out as its original bytes, an edited one is re-encoded by
/// <see cref="Kv3V5Writer"/> (tier0's SaveKV3), and the file is laid out by
/// <see cref="ResourceFile.Write"/>. An unedited file therefore comes back byte for byte, whatever
/// KV3 version or encoder wrote it (measured on all of pak01 through the VRF-based path this
/// replaces).</para>
/// </summary>
public sealed class CompiledResource
{
    private sealed class Decoded(KVDocument document, byte[]? original)
    {
        public KVDocument Document = document;

        /// <summary>The bytes the tree was read from, or null for a tree set by the caller.</summary>
        public readonly byte[]? Original = original;
    }

    private readonly ConditionalWeakTable<ResourceBlock, Decoded> _decoded = new();

    /// <summary>The container: header, block list, tail.</summary>
    public ResourceFile File { get; }

    /// <summary>Wrap an already-read container.</summary>
    public CompiledResource(ResourceFile file) => File = file;

    /// <summary>Read <paramref name="bytes"/> (see <see cref="ResourceFile.Read"/>).</summary>
    /// <exception cref="InvalidDataException">The bytes are not a well-formed resource.</exception>
    public static CompiledResource Read(byte[] bytes) => new(ResourceFile.Read(bytes));

    /// <summary>The resource version (u16 at offset 6).</summary>
    public ushort Version
    {
        get => File.Version;
        set => File.Version = value;
    }

    /// <summary>The first block of <paramref name="type"/>, or null.</summary>
    public ResourceBlock? Block(string type) => File[type];

    /// <summary>True when the resource has a block of <paramref name="type"/>.</summary>
    public bool Has(string type) => File[type] is not null;

    /// <summary>
    /// The tree of the first <paramref name="type"/> block when it is KV3 (null when there is no such
    /// block or it is not KV3). The same object is returned on every call, so edits to it are what
    /// <see cref="Write"/> writes.
    /// </summary>
    /// <exception cref="InvalidDataException">The block claims to be KV3 but is malformed.</exception>
    public KVObject? Kv3(string type) => Document(type)?.Root;

    /// <summary>The format (schema GUID) of the first <paramref name="type"/> block, when it is KV3.</summary>
    public KV3ID? Kv3Format(string type) => Document(type)?.Header?.Format;

    private KVDocument? Document(string type)
    {
        var block = File[type];
        if (block is null)
            return null;
        if (_decoded.TryGetValue(block, out var cached))
            return cached.Document;
        if (!Kv3Binary.IsKv3(block.Data))
            return null;
        var decoded = new Decoded(Kv3Binary.Read(block.Data), block.Data);
        _decoded.Add(block, decoded);
        return decoded.Document;
    }

    /// <summary>
    /// Replace the first <paramref name="type"/> block with <paramref name="root"/> under
    /// <paramref name="format"/>, or add one: at <paramref name="insertAt"/> when given, else at the end.
    /// </summary>
    public void SetKv3(string type, KVObject root, KV3ID format, int? insertAt = null)
    {
        var block = new ResourceBlock(type, []);
        _decoded.Add(block, new Decoded(new KVDocument(new KVHeader { Format = format }, null, root), null));
        Put(block, insertAt);
    }

    /// <summary>Replace the first <paramref name="type"/> block with raw bytes, or add one.</summary>
    public void SetRaw(string type, byte[] data, int? insertAt = null) => Put(new ResourceBlock(type, data), insertAt);

    /// <summary>Remove every block of <paramref name="type"/>; true when there was one.</summary>
    public bool Remove(string type) => File.Blocks.RemoveAll(b => b.Type == type) > 0;

    private void Put(ResourceBlock block, int? insertAt)
    {
        var at = File.Blocks.FindIndex(b => b.Type == block.Type);
        if (at >= 0)
            File.Blocks[at] = block;
        else
            File.Blocks.Insert(Math.Clamp(insertAt ?? File.Blocks.Count, 0, File.Blocks.Count), block);
    }

    /// <summary>The paths in RERL, in order (empty when there is no RERL).</summary>
    public IReadOnlyList<string> References =>
        File["RERL"] is { } rerl ? [.. ResourceRefs.Read(rerl.Data).Select(r => r.Name)] : [];

    /// <summary>
    /// Set RERL to <paramref name="paths"/>, each with its engine id (<see cref="Source2ResourceId.ForPath"/>).
    /// A new RERL goes just before RED2, where resourcecompiler puts it; an empty list removes it.
    /// </summary>
    public void SetReferences(IEnumerable<string> paths)
    {
        var refs = paths.Select(p => (Source2ResourceId.ForPath(p), p)).ToList();
        if (refs.Count == 0)
        {
            Remove("RERL");
            return;
        }
        var red2 = File.Blocks.FindIndex(b => b.Type == "RED2");
        SetRaw("RERL", ResourceRefs.Write(refs, 0), red2 >= 0 ? red2 : 0);
    }

    /// <summary>The file: unchanged KV3 blocks as their original bytes, edited ones re-encoded.</summary>
    public byte[] Write()
    {
        for (var i = 0; i < File.Blocks.Count; i++)
        {
            var block = File.Blocks[i];
            if (!_decoded.TryGetValue(block, out var decoded))
                continue;
            if (decoded.Original is { } original)
            {
                var read = Kv3Binary.Read(original).Root;
                if (ResourceBuilder.SameTree(decoded.Document.Root, read))
                    continue;
                // a field an edit replaced keeps the width Valve stored it with
                Kv3WireTypes.Inherit(read, decoded.Document.Root);
            }
            var written = new ResourceBlock(block.Type, Kv3Binary.Write(decoded.Document.Root, decoded.Document.Header!.Format));
            _decoded.Add(written, new Decoded(decoded.Document, written.Data));
            File.Blocks[i] = written;
        }
        return File.Write();
    }
}
