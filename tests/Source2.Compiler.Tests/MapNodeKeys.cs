using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: every scalar key of the given map nodes and their parents,
/// side by side, for finding the setting that treats two nodes differently.
/// <c>NODEKEYS=&lt;vmap&gt;|&lt;node id&gt;,&lt;node id&gt;,...</c>.
/// </summary>
public class MapNodeKeys(ITestOutputHelper output)
{
    [Fact]
    public void KeysOfNodes()
    {
        if (Environment.GetEnvironmentVariable("NODEKEYS") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var ids = parts[1].Split(',').Select(x => int.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        var doc = DmxBinary.ReadFile(parts[0]);
        var parent = new Dictionary<DmxBinary.Element, DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        foreach (var e in doc.Elements)
            foreach (var c in e.GetElements("children"))
                parent.TryAdd(c, e);
        foreach (var e in doc.Elements.Where(e => e.GetValue<int>("nodeID") is int id && ids.Contains(id)))
        {
            output.WriteLine($"== node {e.GetValue<int>("nodeID")} {e.Type}");
            for (var p = e; p != null; p = parent.GetValueOrDefault(p))
            {
                var keys = p.Attributes.Where(a => a.Value is not DmxBinary.Element and not System.Collections.IList || a.Value is string)
                                       .Select(a => $"{a.Key}={a.Value}");
                output.WriteLine($"   {p.Type} #{p.GetValue<int>("nodeID")}: {string.Join(" ", keys)}");
                if (p.Get<DmxBinary.Element>("entity_properties") is { } ep)
                    output.WriteLine($"     entity: {string.Join(" ", ep.Attributes.Select(a => $"{a.Key}={a.Value}"))}");
            }
        }
    }
}
