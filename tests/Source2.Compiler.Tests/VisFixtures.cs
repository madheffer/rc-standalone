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

            var layout = VoxelVisibilityReader.LayoutOf(root);
            var vxvs = bytes.AsSpan((int)block.Offset, (int)block.Size).ToArray();
            yield return new Specimen(map, VoxelVisibilityReader.Read(bytes, resource), layout, vxvs);
        }
    }

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
