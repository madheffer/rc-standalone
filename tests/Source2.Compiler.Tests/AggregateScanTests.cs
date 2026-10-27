using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Which content maps have aggregates (ragdolls: a prop whose model has two
/// physics parts or a joint) in their settle world, for picking maps to check
/// the ragdoll settle on. AGGREGATE_SCAN=1 runs it over every .vmap under
/// content/csgo_addons; it lists each map's aggregates with node, class,
/// model and whether the settle makes them dynamic.
/// </summary>
public sealed class AggregateScanTests(ITestOutputHelper output)
{
    [Fact]
    public void ListMapsWithAggregates()
    {
        if (Environment.GetEnvironmentVariable("AGGREGATE_SCAN") != "1"
            || CS2Fixtures.StockPak() is not { } pak || MapFixtures.GameSchema() is not { } schema)
            return;
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, "..", ".."));
        foreach (var source in Directory.EnumerateFiles(Path.Combine(root, "content", "csgo_addons"), "*.vmap", SearchOption.AllDirectories))
        {
            var addon = Path.GetRelativePath(Path.Combine(root, "content", "csgo_addons"), source).Split(Path.DirectorySeparatorChar)[0];
            try
            {
                var document = DmxBinary.ReadFile(source);
                using var models = new SettleBuildTests.PakModels(pak, Path.Combine(root, "game", "csgo_addons", addon));
                var bodies = SettleWorld.Build(document, models, schema, SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators));
                var aggregates = bodies.Where(b => b.Aggregate != null && b.PartIndex == 0).ToList();
                if (aggregates.Count == 0)
                    continue;
                var settled = SettleWorld.Settled(document, bodies, models, schema).ToHashSet();
                output.WriteLine($"{addon}/{Path.GetFileNameWithoutExtension(source)}: {aggregates.Count} aggregate(s)");
                foreach (var b in aggregates)
                {
                    var props = b.Node?.Get<DmxBinary.Element>("entity_properties");
                    output.WriteLine($"  node {b.IdPath ?? b.NodeId.ToString()} {props?.Get<string>("classname")} {props?.Get<string>("model")} "
                                     + (settled.Contains(bodies.IndexOf(b)) ? "settled" : "static")
                                     + " part flags " + string.Join(",", b.Aggregate!.Parts.Select(p => "0x" + p.Flags.ToString("x"))));
                }
            }
            catch (Exception e)
            {
                output.WriteLine($"{addon}/{Path.GetFileNameWithoutExtension(source)}: {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
