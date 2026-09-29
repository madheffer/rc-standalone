using ValveKeyValue;
using ValveKeyValue.KeyValues3;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler;

/// <summary>
/// A world node's model (<c>worldnodes/n*_*.vmdl_c</c>): one embedded render
/// mesh, its buffers meshopt-encoded (<see cref="Meshopt.MeshoptEncoder"/>).
///
/// <para>Blocks in the order Valve writes them: the buffers (MVTX, MIDX) in
/// block index order, then MDAT (the CRenderMesh), CTRL (the embedded
/// mesh and its buffer descriptions), RERL (the materials), RED2 and DATA.
/// Resource version 1; KV3 blocks under the generic format with binary_auto's
/// compression (<see cref="AuthoredKv3.ChooseCompression"/>).</para>
/// </summary>
public static class WorldNodeModelAuthor
{
    /// <summary>The container's resource version.</summary>
    public const ushort ResourceVersion = 1;

    /// <summary>
    /// The container around the encoded buffers and the four trees. The
    /// buffers come in block index order (each buffer's <c>m_nBlockIndex</c>
    /// in CTRL): a draw set's vertex buffers, then its index buffer.
    /// </summary>
    public static byte[] Container(IReadOnlyList<(BlockType Type, byte[] Bytes)> buffers, KVObject mdat, KVObject ctrl,
                                   IEnumerable<string> references, KVObject red2, KVObject data)
    {
        var generic = KV3IDLookup.Get("generic");
        using var resource = new Resource();
        Source2ContainerAuthor.SetResourceVersion(resource, ResourceVersion);
        foreach (var (type, bytes) in buffers)
            resource.Blocks.Add(ResourceBuilder.RawBlock(type, bytes, resource));
        resource.Blocks.Add(AuthoredKv3.Block(mdat, generic, BlockType.MDAT, resource));
        resource.Blocks.Add(AuthoredKv3.Block(ctrl, generic, BlockType.CTRL, resource));
        var rerl = new ResourceExtRefList { Resource = resource };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in references)
            if (!string.IsNullOrEmpty(path) && seen.Add(path))
                rerl.ResourceRefInfoList.Add(new ResourceExtRefList.ResourceReferenceInfo { Id = Source2ResourceId.ForPath(path), Name = path });
        if (rerl.ResourceRefInfoList.Count > 0)
            resource.Blocks.Add(rerl);
        resource.Blocks.Add(AuthoredKv3.Block(red2, generic, BlockType.RED2, resource));
        resource.Blocks.Add(AuthoredKv3.Block(data, generic, BlockType.DATA, resource));
        return ValveLayout.Serialize(resource);
    }
}
