using ValveKeyValue;

namespace Source2.Compiler.Meshes;

/// <summary>
/// A compiled mesh file (<c>.vmesh_c</c>, which older models reference through <c>m_refMeshes</c>): its
/// DATA tree and its buffers, from its VBIB block or, without one, from the buffers DATA lists.
/// </summary>
public sealed class CompiledMesh
{
    /// <summary>The underlying resource.</summary>
    public CompiledResource Resource { get; }

    private CompiledMesh(CompiledResource resource) => Resource = resource;

    /// <summary>Open a <c>.vmesh_c</c>.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a well-formed resource.</exception>
    public static CompiledMesh Read(byte[] bytes) => new(CompiledResource.Read(bytes));

    /// <summary>The DATA tree (scene objects and draw calls, skeleton, attachments).</summary>
    public KVObject Data => Resource.Kv3("DATA") ?? throw new InvalidDataException("The mesh has no KV3 DATA block.");

    /// <summary>The vertex and index buffers, decoded, with their layouts.</summary>
    /// <exception cref="InvalidDataException">A buffer is malformed.</exception>
    public Vbib Vbib => Resource.Block("VBIB") is { } vbib ? Vbib.Read(vbib.Data) : Vbib.FromDescriptor(Resource, Data);
}
