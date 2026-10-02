using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>INSTPROPS=&lt;vmap&gt;</c>): the entity classes and models a map
/// reaches through instances, counted.
/// </summary>
public class InstancePropsProbe(ITestOutputHelper output)
{
    [Fact]
    public void Count()
    {
        if (Environment.GetEnvironmentVariable("INSTPROPS") is not { } path)
            return;
        var doc = DmxBinary.ReadFile(path);
        output.WriteLine($"max node id {doc.Elements.Max(e => e.GetValue<int>("nodeID") ?? 0)}, nodes {doc.Elements.Count(e => e.GetValue<int>("nodeID") is not null)}, distinct ids {doc.Elements.Select(e => e.GetValue<int>("nodeID")).OfType<int>().Distinct().Count()}, instances {doc.OfType("CMapInstance").Count()}, locators {SmartProps.NodesCreatedOnLoad(doc, MapFixtures.SmartPropLocators)}");
        var reached = new HashSet<DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        void Reach(DmxBinary.Element e, bool targets)
        {
            if (!reached.Add(e)) return;
            foreach (var c in e.GetElements("children")) Reach(c, targets);
            if (targets && e.Get<DmxBinary.Element>("target") is { } t) Reach(t, targets);
        }
        var world = doc.OfType("CMapWorld").First();
        Reach(world, false);
        var plain = reached.Count;
        var plainWithId = reached.Count(e => e.GetValue<int>("nodeID") is not null);
        reached.Clear();
        Reach(world, true);
        output.WriteLine($"tree {plain} ({plainWithId} with ids), with targets {reached.Count} ({reached.Count(e => e.GetValue<int>("nodeID") is not null)} with ids); types {string.Join(", ", doc.Elements.Where(e => e.GetValue<int>("nodeID") is not null && !reached.Contains(e)).GroupBy(e => e.Type).Select(g => $"{g.Key} {g.Count()}"))}");
        output.WriteLine("tree types: " + string.Join(", ", reached.GroupBy(e => e.Type).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
        output.WriteLine($"hidden {MapEntities.HiddenNodes(doc).Count}");
        var (_, entities) = Maps.MapMeshes.ReadWithEntities(doc);
        foreach (var g in entities.Where(n => n.Through.Count > 0)
                     .Select(n => n.Element.Get<DmxBinary.Element>("entity_properties"))
                     .GroupBy(k => $"{k?.Get<string>("classname")} solid {k?.Get<string>("solid")} {k?.Get<string>("model")}"))
            output.WriteLine($"{g.Count()} {g.Key}");
    }
}

/// <summary>Exploration (<c>INSTEXPAND=&lt;vmap&gt;</c>): the instance copies the map's own expansion makes, id, template id and class.</summary>
public class InstanceExpandProbe(ITestOutputHelper output)
{
    [Fact]
    public void Copies()
    {
        if (Environment.GetEnvironmentVariable("INSTEXPAND") is not { } path)
            return;
        var doc = DmxBinary.ReadFile(path);
        var walked = MapEntities.From(doc);
        var roots = new List<(int Instance, int Root)>();
        var (copies, templates) = MapInstances.Expand(doc, walked, SmartProps.NodesCreatedOnLoad(doc, MapFixtures.SmartPropLocators),
            (node, id, through) =>
            {
                var inst = through[^1].GetValue<int>("nodeID") ?? -1;
                if (roots.Count == 0 || roots[^1].Instance != inst)
                    roots.Add((inst, id - 1));
            });
        foreach (var (inst, root) in roots)
            output.WriteLine($"block {inst} {root}");
        // Preorder slots over the world's children, as a collapse numbers a copied group.
        var slot = 0;
        void Slots(DmxBinary.Element g)
        {
            foreach (var c in g.GetElements("children"))
            {
                output.WriteLine($"slot {c.GetValue<int>("nodeID")} {slot++} {c.Type}");
                Slots(c);
            }
        }
        Slots(doc.OfType("CMapWorld").First());
        output.WriteLine($"slots total {slot}");
        output.WriteLine($"{copies.Count} copies, {templates.Count} templates");
        foreach (var c in copies.Take(400))
            output.WriteLine($"copy {c.NodeId} of {walked[c.Template].NodeId} {walked[c.Template].ClassName} at {c.Origin}");
    }
}
