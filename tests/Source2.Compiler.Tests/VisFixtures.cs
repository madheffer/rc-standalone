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

    /// <summary>
    /// The ray trace scene the compile left in %TEMP% for a map, with the
    /// visibility it compiled beside it. Null when either is absent, which is not
    /// evidence either way and means the caller should skip.
    /// </summary>
    public static (RayTraceEnvironment Rte, VoxelVisibility Valve)? RayTraceScene(string addon, string map)
    {
        var rte = Path.Combine(Path.GetTempPath(), "csgo_addons", addon, "maps", map + ".rte");
        var cs2 = CS2Fixtures.StockPak();
        if (!File.Exists(rte) || cs2 is null)
            return null;

        var vpk = Path.Combine(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cs2)!, "..")),
                               "csgo_addons", addon, "maps", map + ".vpk");
        // A partial build in place (compile-map, -fshallow) may lack the vvis; the
        // full compile kept at %TEMP%/gt/<map>.vpk has it.
        foreach (var candidate in new[] { vpk, Path.Combine(Path.GetTempPath(), "gt", map + ".vpk") })
        {
            if (!File.Exists(candidate))
                continue;
            using var package = new ValvePak.Package();
            package.Read(candidate);
            var entry = package.Entries.GetValueOrDefault("vvis_c")?.FirstOrDefault();
            if (entry is null)
                continue;
            package.ReadEntry(entry, out var bytes);
            return (RayTraceEnvironment.ReadFile(rte), VoxelVisibilityReader.Read(bytes));
        }
        return null;
    }
}
