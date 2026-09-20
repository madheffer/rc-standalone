using ValveKeyValue;
using Source2.Compiler.Maps;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Vis = Source2.Compiler.Maps.VoxelVisibility;

namespace Source2.Compiler.Tests;

/// <summary>
/// Valve's compiled visibility, decoded by our codec, for every installed map.
/// One place so the tests that read it cannot drift apart on how they read it.
/// </summary>
internal static class VisFixtures
{
    public const string Suffix = "world_visibility.vvis_c";

    /// <summary>A map's visibility: our decode, Valve's index, and the raw payload.</summary>
    internal sealed record Specimen(string Map, Vis Vis, Vis.Layout ValveLayout, byte[] Vxvs);

    /// <summary>
    /// Each installed map's visibility, skipping the pre-octree format some very
    /// old maps still ship.
    /// </summary>
    public static IEnumerable<Specimen> All(int limit = int.MaxValue)
    {
        foreach (var (map, bytes) in MapFixtures.AllResources(Suffix, limit))
        {
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));

            var block = resource.GetBlockByType(BlockType.VXVS);
            if (block is null || resource.DataBlock is not BinaryKV3 kv3)
                continue;

            var root = kv3.Data.Root;
            if (!root.ContainsKey("m_nBaseClusterCount"))
                continue;

            var layout = LayoutOf(root);
            var vxvs = bytes.AsSpan((int)block.Offset, (int)block.Size).ToArray();
            yield return new Specimen(map, Vis.ReadVxvs(vxvs, layout, ScalarsOf(root)), layout, vxvs);
        }
    }

    /// <summary>The six (offset, count) pairs exactly as Valve's DATA index states them.</summary>
    public static Vis.Layout LayoutOf(KVObject root)
    {
        VisBlockRef Ref(string name, int stride)
        {
            var sub = root.GetSubCollection(name);
            return new VisBlockRef(sub.GetInt32Property("m_nOffset"),
                                   sub.GetInt32Property("m_nElementCount"), stride);
        }
        return new Vis.Layout(
            Ref("m_NodeBlock", 8), Ref("m_RegionBlock", 8),
            Ref("m_EnclosedClusterListBlock", 8), Ref("m_EnclosedClustersBlock", 2),
            Ref("m_MasksBlock", 8), Ref("m_nVisBlocks", 1));
    }

    /// <summary>The scalars from the DATA index, with no arrays attached yet.</summary>
    public static Vis ScalarsOf(KVObject root) => new()
    {
        BaseClusterCount = root.GetUInt32Property("m_nBaseClusterCount"),
        PVSBytesPerCluster = root.GetUInt32Property("m_nPVSBytesPerCluster"),
        MinBounds = root.GetSubCollection("m_vMinBounds").ToVector3(),
        MaxBounds = root.GetSubCollection("m_vMaxBounds").ToVector3(),
        GridSize = root.GetFloatProperty("m_flGridSize"),
        SkyVisibilityCluster = root.GetUInt32Property("m_nSkyVisibilityCluster"),
        SunVisibilityCluster = root.GetUInt32Property("m_nSunVisibilityCluster"),
    };

    /// <summary>
    /// A gate that fails loudly rather than passing on an empty corpus. A test
    /// that finds nothing and returns green is worse than no test.
    /// </summary>
    public static void RequireCorpus(int maps)
    {
        if (maps > 0)
            return;
        Xunit.Assert.True(MapFixtures.WorkshopDir() is null,
            $"Workshop maps are installed but none yielded a readable {Suffix}.");
    }
}
