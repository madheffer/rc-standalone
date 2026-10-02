using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>VMAPELEMENT=&lt;vmap&gt;|&lt;type&gt;[,type...]</c>): every element of
/// the given types with its attributes (values shortened), and the elements
/// that hold a reference to it with the attribute name.
/// </summary>
public class VmapElementProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("VMAPELEMENT") is not { } spec)
            return;
        var p = spec.Split('|');
        var types = p[1].Split(',').ToHashSet();
        var doc = DmxBinary.ReadFile(p[0]);
        var limit = int.TryParse(Environment.GetEnvironmentVariable("VMAPELEMENT_LIMIT"), out var l) ? l : 3;
        static string Short(object? v) => v switch
        {
            null => "null",
            DmxBinary.Element e => $"<{e.Type} {e.Name}>",
            object?[] a => $"[{a.Length}: {string.Join(", ", a.Take(6).Select(Short))}{(a.Length > 6 ? ", ..." : "")}]",
            _ => v.ToString() ?? "",
        };
        foreach (var type in types)
        {
            var hits = doc.Elements.Where(e => e.Type == type).ToList();
            output.WriteLine($"{type}: {hits.Count} elements");
            foreach (var e in hits.Take(limit))
            {
                output.WriteLine($"  element {e.Name}:");
                foreach (var (name, value) in e.Attributes)
                {
                    output.WriteLine($"    {name} = {Short(value)}");
                    // One level into owned elements (nodeData and the like).
                    if (value is DmxBinary.Element sub && name != "children")
                        foreach (var (n2, v2) in sub.Attributes)
                            output.WriteLine($"      .{n2} = {Short(v2)}");
                    if (name == "children" && value is object?[] kids)
                        foreach (var k in kids.OfType<DmxBinary.Element>())
                            output.WriteLine($"      child <{k.Type} {k.Name}> classname {k.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname")} model {k.Get<DmxBinary.Element>("entity_properties")?.Get<string>("model")} kids {string.Join(",", k.GetElements("children").Select(x => x.Type))}");
                }
                foreach (var holder in doc.Elements)
                    foreach (var (name, value) in holder.Attributes)
                        if (ReferenceEquals(value, e) || (value is object?[] arr && arr.Any(x => ReferenceEquals(x, e))))
                            output.WriteLine($"    held by <{holder.Type} {holder.Name}>.{name}");
            }
        }
    }
}
