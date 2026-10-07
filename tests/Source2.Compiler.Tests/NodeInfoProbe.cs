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
        var addon = Path.Combine(Path.GetDirectoryName(CS2Fixtures.StockPak() ?? "")!, "..", "csgo_addons", p[0]);
        using var models = CS2Fixtures.StockPak() is { } pak ? new SettleBuildTests.PakModels(pak, addon) : null;
        foreach (var node in document.Elements.Where(e => e.GetValue<int>("nodeID") is { } id && ids.Contains(id) && e.Type != "CMapMesh"))
        {
            output.WriteLine($"node {node.GetValue<int>("nodeID")}: {node.Type} {node.Get<string>("name")} "
                             + $"{node.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname")} origin {node.GetValue<System.Numerics.Vector3>("origin")} "
                             + $"angles {node.GetValue<System.Numerics.Vector3>("angles")} scales {node.GetValue<System.Numerics.Vector3>("scales")}");
            foreach (var n in Chain(document, node))
                output.WriteLine($"  < {n.Type} {n.GetValue<int>("nodeID")} {n.Get<string>("name")}");
        }
        foreach (var mesh in MapMeshes.Read(document).Where(m => ids.Contains(m.NodeId)))
        {
            var zs = mesh.Faces.SelectMany(f => f.Corners).Select(c => c.Z).DefaultIfEmpty().ToArray();
            output.WriteLine($"mesh {mesh.NodeId}: parent {mesh.ParentType} {mesh.ParentClass}, instances [{string.Join(",", mesh.Instances)}], "
                             + $"prefabs [{string.Join(",", mesh.Prefabs)}], hidden {mesh.Hidden}, disableShadows {mesh.Element?.GetValue<int>("disableShadows")}, "
                             + $"faces {mesh.Faces.Length}, z {zs.Min():F2}..{zs.Max():F2}, origin {mesh.Origin} angles {mesh.Angles} scales {mesh.Scales}");
            output.WriteLine($"  materials {string.Join(", ", mesh.Faces.Select(f => f.Material).Distinct())}");
            if (models is not null)
                foreach (var m in mesh.Faces.Select(f => f.Material).Distinct())
                {
                    var info = models.Material(m);
                    output.WriteLine($"  flags {(info is null ? "unresolved" : TraceScene.MaterialFlags(info).ToString("x"))} shader {info?.Shader} {m}");
                    if (info is not null && Environment.GetEnvironmentVariable("NODEINFO_PARAMS") is not null)
                        output.WriteLine($"    ints {string.Join(" ", info.Ints.Select(kv => $"{kv.Key}={kv.Value}"))} | params {string.Join(" ", info.Params.Select(kv => $"{kv.Key}={kv.Value}"))}");
                }
            if (Environment.GetEnvironmentVariable("NODEINFO_FACES") is not null && mesh.Element?.Get<DmxBinary.Element>("meshData") is { } data)
            {
                // Each face's loop normal (Newell, local space) beside its ray-scene triangles' normals.
                var (corners, faces) = MeshTessellation.RayScene(data);
                var local = MeshTessellation.RayScene(data);
                var loops = mesh.Faces;
                for (var t = 0; t < faces.Count; t++)
                {
                    var a = corners[3 * t]; var b = corners[3 * t + 1]; var c = corners[3 * t + 2];
                    var n = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(b - a, c - a));
                    var f = loops[faces[t]].Corners;
                    var newell = System.Numerics.Vector3.Zero;
                    for (var k = 0; k < f.Length; k++)
                    {
                        var u = f[k]; var v = f[(k + 1) % f.Length];
                        newell += new System.Numerics.Vector3((u.Y - v.Y) * (u.Z + v.Z), (u.Z - v.Z) * (u.X + v.X), (u.X - v.X) * (u.Y + v.Y));
                    }
                    if (t < 40)
                        output.WriteLine($"  tri {t} face {faces[t]}: tri normal {n:F3} loop normal {System.Numerics.Vector3.Normalize(newell):F3} {f.Length} corners z {f.Average(x => x.Z):F1}");
                }
            }
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
