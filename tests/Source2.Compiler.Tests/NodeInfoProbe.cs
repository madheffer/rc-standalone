using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Scratch probe (NODEINFO=addon|map|id,id): for each map mesh with one of
/// those node ids, where it hangs (parent type and class, instances, prefabs,
/// hidden), its disableShadows, materials, face count and z range, and the
/// chain of DMX nodes from the world down to it.
/// </summary>
public class NodeInfoProbe(ITestOutputHelper output)
{
    [Fact]
    public void Describe()
    {
        if (Environment.GetEnvironmentVariable("NODEINFO") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        if (MapFixtures.VmapSource(p[0], p[1]) is not { } source)
            return;
        var ids = p[2].Split(',').Select(int.Parse).ToHashSet();
        var document = MapSource.Read(source);
        foreach (var node in document.Elements.Where(e => e.GetValue<int>("nodeID") is { } id && ids.Contains(id) && e.Type != "CMapMesh"))
        {
            output.WriteLine($"node {node.GetValue<int>("nodeID")}: {node.Type} {node.Get<string>("name")} "
                             + $"{node.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname")}");
            foreach (var n in Chain(document, node))
                output.WriteLine($"  < {n.Type} {n.GetValue<int>("nodeID")} {n.Get<string>("name")}");
        }
        foreach (var mesh in MapMeshes.Read(document).Where(m => ids.Contains(m.NodeId)))
        {
            var zs = mesh.Faces.SelectMany(f => f.Corners).Select(c => c.Z).DefaultIfEmpty().ToArray();
            output.WriteLine($"mesh {mesh.NodeId}: parent {mesh.ParentType} {mesh.ParentClass}, instances [{string.Join(",", mesh.Instances)}], "
                             + $"prefabs [{string.Join(",", mesh.Prefabs)}], hidden {mesh.Hidden}, disableShadows {mesh.Element?.GetValue<int>("disableShadows")}, "
                             + $"faces {mesh.Faces.Length}, z {zs.Min():F2}..{zs.Max():F2}");
            output.WriteLine($"  materials {string.Join(", ", mesh.Faces.Select(f => f.Material).Distinct())}");
            foreach (var key in new[] { "name", "visexclude", "renderwithdynamic", "bakelighting", "disablemerging", "fademindist", "excludefromlightmap" })
                if (mesh.Element?.Get<object>(key) is { } v)
                    output.WriteLine($"  {key} = {v}");
            var chain = Chain(document, mesh.Element!);
            foreach (var n in chain)
                output.WriteLine($"  < {n.Type} {n.GetValue<int>("nodeID")} {n.Get<string>("name")} "
                                 + $"{n.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname")}");
        }
    }

    // The nodes whose children lead to the element, nearest first.
    private static List<DmxBinary.Element> Chain(DmxBinary.Document document, DmxBinary.Element element)
    {
        var parent = new Dictionary<DmxBinary.Element, DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        foreach (var e in document.Elements)
            foreach (var c in e.GetElements("children"))
                parent.TryAdd(c, e);
        var chain = new List<DmxBinary.Element>();
        for (var at = element; parent.TryGetValue(at, out var up); at = up)
            chain.Add(up);
        return chain;
    }
}
