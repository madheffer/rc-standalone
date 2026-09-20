using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Maps;

/// <summary>
/// Load a compiled <c>world_visibility.vvis_c</c> into <see cref="VoxelVisibility"/>.
///
/// <para>The DATA block is an ordinary KV3 tree holding the scalars and the six
/// (offset, count) pairs; VXVS is the payload those pairs address. Reading the
/// index from the file rather than deriving it is deliberate: a file whose stated
/// offsets disagree with the prefix sums is malformed, and
/// <c>VoxelVisibilityTests</c> is what asserts they never do.</para>
/// </summary>
public static class VoxelVisibilityReader
{
    /// <summary>Read from the bytes of a <c>.vvis_c</c>.</summary>
    /// <exception cref="InvalidDataException">The resource carries no readable visibility.</exception>
    public static VoxelVisibility Read(byte[] bytes)
    {
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        return Read(bytes, resource);
    }

    /// <summary>Read from a <c>.vvis_c</c> on disk.</summary>
    public static VoxelVisibility ReadFile(string path) => Read(File.ReadAllBytes(path));

    /// <summary>
    /// Read using an already-parsed resource, when the caller has one. The bytes
    /// must be the same ones it was parsed from.
    /// </summary>
    public static VoxelVisibility Read(byte[] bytes, Resource resource)
    {
        var block = resource.GetBlockByType(BlockType.VXVS)
            ?? throw new InvalidDataException("No VXVS block: this is not a world visibility resource.");
        if (resource.DataBlock is not BinaryKV3 kv3)
            throw new InvalidDataException("The DATA block is not KV3, so VXVS cannot be indexed.");

        var root = kv3.Data.Root;
        if (!root.ContainsKey("m_nBaseClusterCount"))
            throw new InvalidDataException("Pre-octree visibility format, which carries m_clusters instead.");

        return VoxelVisibility.ReadVxvs(
            bytes.AsSpan((int)block.Offset, (int)block.Size), LayoutOf(root), ScalarsOf(root));
    }

    /// <summary>The six (offset, count) pairs exactly as the DATA index states them.</summary>
    public static VoxelVisibility.Layout LayoutOf(KVObject root)
    {
        VisBlockRef Ref(string name, int stride)
        {
            var sub = root.GetSubCollection(name);
            return new VisBlockRef(sub.GetInt32Property("m_nOffset"),
                                   sub.GetInt32Property("m_nElementCount"), stride);
        }
        return new VoxelVisibility.Layout(
            Ref("m_NodeBlock", 8), Ref("m_RegionBlock", 8),
            Ref("m_EnclosedClusterListBlock", 8), Ref("m_EnclosedClustersBlock", 2),
            Ref("m_MasksBlock", 8), Ref("m_nVisBlocks", 1));
    }

    /// <summary>The scalars from the DATA index, with no arrays attached yet.</summary>
    public static VoxelVisibility ScalarsOf(KVObject root) => new()
    {
        BaseClusterCount = root.GetUInt32Property("m_nBaseClusterCount"),
        PVSBytesPerCluster = root.GetUInt32Property("m_nPVSBytesPerCluster"),
        MinBounds = root.GetSubCollection("m_vMinBounds").ToVector3(),
        MaxBounds = root.GetSubCollection("m_vMaxBounds").ToVector3(),
        GridSize = root.GetFloatProperty("m_flGridSize"),
        SkyVisibilityCluster = root.GetUInt32Property("m_nSkyVisibilityCluster"),
        SunVisibilityCluster = root.GetUInt32Property("m_nSunVisibilityCluster"),
    };
}
