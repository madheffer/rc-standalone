using System.Buffers.Binary;
using ValveKeyValue;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="WorldPhysicsAuthor"/> against Valve's own world_physics.vmdl_c.
/// <c>WPAUTHOR=&lt;compiled .vpk&gt;|&lt;map&gt;</c> re-authors the file from
/// its own decoded trees: the container facts and every block's decoded tree
/// must come back the same, which pins the container before its trees are
/// built from the map.
/// </summary>
public class WorldPhysicsAuthorTests(ITestOutputHelper output)
{
    [Fact]
    public void ReauthorsValvesTrees()
    {
        if (Environment.GetEnvironmentVariable("WPAUTHOR") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var valve = Read(p[0], $"maps/{p[1]}/world_physics.vmdl_c");
        var trees = Trees(valve);
        var mine = WorldPhysicsAuthor.Container(trees["PHYS"], trees["RED2"], trees["DATA"]);
        var report = Compare(valve, mine);
        output.WriteLine($"valve {Facts(valve)}");
        output.WriteLine($"mine  {Facts(mine)}");
        output.WriteLine($"trees: {string.Join(", ", trees.Select(t => $"{t.Key} {KvTreeDiff.Typed(t.Value, "", 0, int.MaxValue).Count()} lines"))}");
        foreach (var line in report.Take(60))
            output.WriteLine(line);
        Assert.Empty(report);
    }

    /// <summary>
    /// The file built from the map (<see cref="Physics.WorldPhysics"/>) against
    /// Valve's: <c>WPBUILD=&lt;addon&gt;|&lt;map&gt;|&lt;compiled .vpk&gt;</c>,
    /// <c>WPBUILD_SHOW</c> caps the listed differences per block.
    /// </summary>
    [Fact]
    public void BuildsFromTheMap()
    {
        if (Environment.GetEnvironmentVariable("WPBUILD") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var show = int.TryParse(Environment.GetEnvironmentVariable("WPBUILD_SHOW"), out var n) ? n : 25;
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var game = Path.Combine(cs2, "game");
        using var models = new SettleBuildTests.PakModels(Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "csgo_addons", p[0]));
        var doc = DmxBinary.ReadFile(Path.Combine(cs2, "content", "csgo_addons", p[0], "maps", p[1] + ".vmap"));
        var notes = new List<string>();
        // WPBUILD_GPU=1: new-blending materials take their layers from the GPU sampler.
        using var gpu = Environment.GetEnvironmentVariable("WPBUILD_GPU") == "1"
            ? new Source2.Compiler.Gpu.GpuMaterialSampler(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"), models.Read)
            : null;
        var pieces = Physics.WorldCollision.Pieces(doc, name => Physics.WorldCollision.ReadMaterial(models.Material(name), models.CollisionProperty),
            notes, gpu == null ? null : gpu.For, models.Physics, models.SmartProp);
        var model = Physics.WorldPhysics.Build(pieces);
        var mine = WorldPhysicsAuthor.Container(Physics.WorldPhysicsTrees.Phys(model), Physics.WorldPhysicsTrees.Red2(model, models.SurfaceName), Physics.WorldPhysicsTrees.Data(p[1]));
        var valve = Read(p[2], $"maps/{p[1]}/world_physics.vmdl_c");
        output.WriteLine($"valve {Facts(valve)}");
        output.WriteLine($"mine  {Facts(mine)}");
        var ta = Trees(valve);
        var tb = Trees(mine);
        var total = 0;
        foreach (var (name, tree) in ta)
        {
            var diffs = tb.TryGetValue(name, out var other) ? KvTreeDiff.Diff(tree, other, 100000) : ["missing"];
            total += diffs.Count;
            output.WriteLine($"{name}: {diffs.Count} differences");
            foreach (var d in diffs.Take(show))
                output.WriteLine($"  {name}{d}");
        }
        foreach (var source in model.AttributeSources)
            output.WriteLine($"attribute source: {source}");
        foreach (var note in notes.Take(10))
            output.WriteLine($"note: {note}");
        Assert.Equal(0, total);
    }

    internal static byte[] Read(string vpk, string path)
    {
        using var package = new Package();
        package.Read(vpk);
        package.ReadEntry(package.FindEntry(path)!, out var bytes);
        return bytes;
    }

    /// <summary>Each KV3 block's decoded tree by block name.</summary>
    internal static Dictionary<string, KVObject> Trees(byte[] bytes)
    {
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        var trees = new Dictionary<string, KVObject>();
        foreach (var block in resource.Blocks)
        {
            KVObject? tree = block switch
            {
                KeyValuesOrNTRO k => k.Data,
                BinaryKV3 k => k.Data.Root,
                ResourceEditInfo2 r => r.Data?.Root,
                _ => null,
            };
            if (tree != null)
                trees[block.Type.ToString()] = tree;
        }
        return trees;
    }

    /// <summary>The container facts (version, block order, KV3 compression) and every block's tree.</summary>
    internal static List<string> Compare(byte[] valve, byte[] mine)
    {
        var report = new List<string>();
        var a = Facts(valve);
        var b = Facts(mine);
        if (a != b)
            report.Add($"container: {a} vs {b}");
        var ta = Trees(valve);
        var tb = Trees(mine);
        foreach (var (name, tree) in ta)
        {
            if (!tb.TryGetValue(name, out var other))
            {
                report.Add($"{name}: missing");
                continue;
            }
            report.AddRange(KvTreeDiff.Diff(tree, other).Select(l => $"{name}{l}"));
        }
        return report;
    }

    // Resource version, then each block's name, KV3 version and compression method.
    internal static string Facts(byte[] bytes)
    {
        var parts = new List<string> { $"v{BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6))}" };
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        for (var i = 0; i < count; i++)
        {
            var at = 16 + (i * 12);
            var offset = at + 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
            parts.Add($"{System.Text.Encoding.ASCII.GetString(bytes, at, 4)}:{Convert.ToHexString(bytes, offset, 4)}:m{BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 20))}:{Convert.ToHexString(bytes, offset + 4, 16)}");
        }
        return string.Join(" ", parts);
    }
}
