using ValveKeyValue;
using ValveKeyValue.KeyValues3;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler;

/// <summary>
/// A map's <c>world_physics.vmdl_c</c>: the world's collision as a model with
/// no meshes and one embedded physics aggregate.
///
/// <para>Measured on every Valve-compiled map at hand (32 local compiles,
/// resourcecompiler 0923): resource version 1, blocks in the order
/// <c>PHYS CTRL RED2 DATA</c>, every block KV3 v5 under the generic format.
/// CTRL only points the model at its embedded physics and is always stored
/// raw (183 bytes). RED2 and DATA follow the usual payload cut (LZ4 above 256
/// bytes). PHYS is LZ4 up to at least 456,332 bytes of uncompressed payload
/// (buffer plus blobs) and Zstd from 527,006; 512 KiB is taken as the cut, the
/// one round figure in that window.</para>
///
/// <para>Compressed bytes are encoder defined, so parity is the decoded tree
/// of each block plus the container facts (docs/RC_PARITY.md).</para>
/// </summary>
public static class WorldPhysicsAuthor
{
    /// <summary>The container's resource version.</summary>
    public const ushort ResourceVersion = 1;

    /// <summary>Uncompressed PHYS payload (buffer plus blobs) above which it is Zstd rather than LZ4.</summary>
    public const int ZstdThreshold = 512 * 1024;

    /// <summary>The CTRL tree every world_physics carries: the model's physics is embedded block 0.</summary>
    public static KVObject Ctrl()
    {
        var embedded = KVObject.Collection();
        embedded.Add("phys_data_block", new KVObject(0L));
        var root = KVObject.Collection();
        root.Add("embedded_physics", embedded);
        return root;
    }

    /// <summary>
    /// The container around the three trees, typed as they are to be written.
    /// Without physics it is RED2 and DATA alone (a brush entity model with no
    /// shapes: atixref's func_water).
    /// </summary>
    public static byte[] Container(KVObject? phys, KVObject red2, KVObject data)
    {
        ArgumentNullException.ThrowIfNull(red2);
        ArgumentNullException.ThrowIfNull(data);
        var generic = KV3IDLookup.Get("generic");
        using var resource = new Resource();
        Source2ContainerAuthor.SetResourceVersion(resource, ResourceVersion);
        var physBlock = phys == null ? null : AuthoredKv3.Block(phys, generic, BlockType.PHYS, resource);
        if (physBlock != null)
        {
            resource.Blocks.Add(physBlock);
            resource.Blocks.Add(AuthoredKv3.Block(Ctrl(), generic, BlockType.CTRL, resource));
        }
        resource.Blocks.Add(AuthoredKv3.Block(red2, generic, BlockType.RED2, resource));
        resource.Blocks.Add(AuthoredKv3.Block(data, generic, BlockType.DATA, resource));
        AuthoredKv3.ChooseCompression(resource);
        if (physBlock != null && AuthoredKv3.TotalPayloadBytes(physBlock) > ZstdThreshold)
            physBlock.SerializationCompressionMethod = KV3BinaryCompressionMethod.Zstd;
        using var ms = new MemoryStream();
        resource.Serialize(ms);
        return ms.ToArray();
    }
}
