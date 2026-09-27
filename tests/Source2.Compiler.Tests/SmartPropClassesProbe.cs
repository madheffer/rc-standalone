using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: every smart prop class the addons' maps use, with how many
/// definitions and nodes use it. <c>SMARTPROPCLASSES=1</c>.
/// </summary>
public class SmartPropClassesProbe(ITestOutputHelper output)
{
    [Fact]
    public void Classes()
    {
        if (Environment.GetEnvironmentVariable("SMARTPROPCLASSES") != "1")
            return;
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var game = Path.Combine(cs2, "game");
        var uses = new Dictionary<string, (HashSet<string> Files, int Nodes)>();
        var failed = new HashSet<string>();
        foreach (var addon in Directory.GetDirectories(Path.Combine(cs2, "content", "csgo_addons")))
        {
            var maps = Path.Combine(addon, "maps");
            if (!Directory.Exists(maps))
                continue;
            using var models = new SettleBuildTests.PakModels(Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "csgo_addons", Path.GetFileName(addon)));
            foreach (var vmap in Directory.GetFiles(maps, "*.vmap"))
            {
                DmxBinary.Document doc;
                try
                {
                    doc = DmxBinary.ReadFile(vmap);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (var node in doc.OfType("CMapSmartProp"))
                {
                    var file = node.Get<string>("smartPropFilename") ?? "";
                    if (models.SmartProp(file) is not { } root)
                    {
                        failed.Add(file);
                        continue;
                    }
                    foreach (var cls in ClassesOf(root).Distinct())
                    {
                        var u = uses.GetValueOrDefault(cls, ([], 0));
                        u.Files.Add($"{Path.GetFileNameWithoutExtension(vmap)}:{file}");
                        uses[cls] = (u.Files, u.Nodes + 1);
                    }
                }
            }
        }
        foreach (var (cls, u) in uses.OrderBy(x => x.Key))
            output.WriteLine($"{cls}: {u.Nodes} nodes, {u.Files.Count} map/definition pairs, e.g. {string.Join(", ", u.Files.Take(3))}");
        foreach (var f in failed)
            output.WriteLine($"no definition: {f}");
    }

    private static IEnumerable<string> ClassesOf(KVObject value)
    {
        if (value.ValueType == KVValueType.Array)
        {
            foreach (var item in value.Values)
                foreach (var c in ClassesOf(item))
                    yield return c;
        }
        else if (value.ValueType == KVValueType.Collection)
        {
            foreach (var (key, child) in value.Children)
            {
                if (key == "_class" && child.ValueType == KVValueType.String)
                    yield return child.ToString() ?? "";
                foreach (var c in ClassesOf(child))
                    yield return c;
            }
        }
    }
}
