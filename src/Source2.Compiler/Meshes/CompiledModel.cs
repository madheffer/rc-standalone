using ValveKeyValue;

namespace Source2.Compiler.Meshes;

/// <summary>
/// A compiled model (<c>.vmdl_c</c>) opened for editing: its KV3 blocks as trees (through
/// <see cref="CompiledResource"/>, so untouched blocks keep their bytes and edited ones are
/// re-encoded as resourcecompiler encodes them) and its embedded meshes' buffers decoded and
/// re-encoded with the meshopt codecs (docs/MESHES.md).
/// </summary>
public sealed class CompiledModel
{
    /// <summary>The underlying resource, for anything this view does not cover.</summary>
    public CompiledResource Resource { get; }

    private CompiledModel(CompiledResource resource) => Resource = resource;

    /// <summary>Open a <c>.vmdl_c</c>.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a well-formed resource.</exception>
    public static CompiledModel Read(byte[] bytes) => new(CompiledResource.Read(bytes));

    /// <summary>The DATA tree (model info, mesh references, mesh group masks, physics and animation references).</summary>
    public KVObject Data => Resource.Kv3("DATA") ?? throw new InvalidDataException("The model has no DATA block.");

    /// <summary>The CTRL tree (the embedded meshes' descriptors), or null for a model without embedded meshes.</summary>
    public KVObject? Control => Resource.Has("CTRL") ? Resource.Kv3("CTRL") : null;

    /// <summary>The embedded meshes, in CTRL order.</summary>
    public IReadOnlyList<EmbeddedMesh> Meshes =>
        Control is { } ctrl && ctrl.ContainsKey("embedded_meshes")
            ? ctrl["embedded_meshes"]!.Values.Select(m => new EmbeddedMesh(this, m)).ToList()
            : [];

    /// <summary>The file, re-encoding only what changed.</summary>
    public byte[] Write() => Resource.Write();
}

/// <summary>One embedded mesh: its descriptor in CTRL, its MDAT tree and its buffers.</summary>
public sealed class EmbeddedMesh
{
    private readonly CompiledModel _model;

    internal EmbeddedMesh(CompiledModel model, KVObject descriptor)
    {
        _model = model;
        Descriptor = descriptor;
    }

    /// <summary>The CTRL entry.</summary>
    public KVObject Descriptor { get; }

    /// <summary><c>m_Name</c>.</summary>
    public string Name => (string)Descriptor["m_Name"]!;

    /// <summary>The mesh's MDAT tree (scene objects and draw calls, skeleton, attachments, morph set).</summary>
    public KVObject Data => _model.Resource.Kv3At((int)Descriptor["m_nDataBlock"]!)
        ?? throw new InvalidDataException($"Mesh {Name}: its data block is not KV3.");

    /// <summary>The vertex buffers.</summary>
    public IReadOnlyList<MeshBuffer> VertexBuffers => Buffers("m_vertexBuffers", vertex: true);

    /// <summary>The index buffers.</summary>
    public IReadOnlyList<MeshBuffer> IndexBuffers => Buffers("m_indexBuffers", vertex: false);

    private List<MeshBuffer> Buffers(string key, bool vertex) =>
        Descriptor.ContainsKey(key) ? Descriptor[key]!.Values.Select(b => new MeshBuffer(_model, b, vertex)).ToList() : [];
}

/// <summary>A vertex or index buffer: its CTRL descriptor and its block's bytes.</summary>
public sealed class MeshBuffer
{
    private readonly CompiledModel _model;

    internal MeshBuffer(CompiledModel model, KVObject descriptor, bool vertex)
    {
        _model = model;
        Descriptor = descriptor;
        IsVertexBuffer = vertex;
    }

    /// <summary>The CTRL entry (count, element size, flags, input layout).</summary>
    public KVObject Descriptor { get; }

    /// <summary>True for a vertex buffer, false for an index buffer.</summary>
    public bool IsVertexBuffer { get; }

    /// <summary><c>m_nElementCount</c>: vertices or indices.</summary>
    public int ElementCount => (int)Descriptor["m_nElementCount"]!;

    /// <summary><c>m_nElementSizeInBytes</c>: the vertex stride, or 2 or 4 for indices.</summary>
    public int ElementSize => (int)Descriptor["m_nElementSizeInBytes"]!;

    /// <summary>Whether the stored bytes are meshopt-compressed.</summary>
    public bool IsCompressed => (bool)Descriptor["m_bMeshoptCompressed"]!;

    private int BlockIndex => (int)Descriptor["m_nBlockIndex"]!;

    /// <summary>The elements, decoded: <see cref="ElementCount"/> × <see cref="ElementSize"/> bytes.</summary>
    /// <exception cref="InvalidDataException">The buffer is zstd-compressed (not measured) or malformed.</exception>
    public byte[] Read()
    {
        if (Descriptor.ContainsKey("m_bCompressedZSTD") && (bool)Descriptor["m_bCompressedZSTD"]!)
            throw new InvalidDataException("A zstd-compressed mesh buffer is not read here yet.");
        var stored = _model.Resource.File.Blocks[BlockIndex].Data;
        if (!IsCompressed)
            return stored.ToArray();
        return IsVertexBuffer
            ? MeshoptVertexCodec.Decode(stored, ElementCount, ElementSize)
            : MeshoptIndexCodec.Decode(stored, ElementCount, ElementSize);
    }

    /// <summary>
    /// Replace the elements and store them as resourcecompiler would: a buffer it compressed is
    /// meshopt-encoded again (vertex format v1, index v1) when that is smaller than the raw bytes,
    /// else kept raw; a raw one stays raw (a morph mesh's vertex buffers). The descriptor's count
    /// and compression flag follow. Index buffers keep each triangle and its winding, not which of
    /// its vertices comes first.
    /// </summary>
    public void Write(byte[] elements, int elementCount)
    {
        if (elements.Length != elementCount * ElementSize)
            throw new ArgumentException($"{elementCount} elements of {ElementSize} bytes are {elementCount * ElementSize} bytes, not {elements.Length}.");
        if (!IsVertexBuffer && elementCount % 3 != 0)
            throw new ArgumentException("An index buffer holds whole triangles.");
        byte[] stored = elements;
        bool compressed = false;
        if (IsCompressed && elementCount > 0)
        {
            var encoded = IsVertexBuffer
                ? MeshoptVertexCodec.Encode(elements, elementCount, ElementSize)
                : MeshoptIndexCodec.Encode(elements, ElementSize);
            if (encoded.Length < elements.Length)
            {
                stored = encoded;
                compressed = true;
            }
        }
        _model.Resource.SetRawAt(BlockIndex, stored);
        // Only a changed field is replaced, so an unchanged descriptor keeps CTRL's bytes.
        if (ElementCount != elementCount)
            Descriptor["m_nElementCount"] = new KVObject(elementCount);
        if (IsCompressed != compressed)
            Descriptor["m_bMeshoptCompressed"] = new KVObject(compressed);
    }
}
