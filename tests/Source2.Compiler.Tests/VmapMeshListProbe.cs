using System.Text;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>VMAPMESHES=addon|map[|out file]</c>): every mesh node the
/// walk reaches, with its node attributes (meshData left out), its faces'
/// materials and counts, and its world placement.
/// </summary>
public class VmapMeshListProbe(ITestOutputHelper output)
{
    [Fact]
    public void List()
    {
        if (Environment.GetEnvironmentVariable("VMAPMESHES") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        if (MapFixtures.VmapSource(p[0], p[1]) is not { } source)
            return;
        var document = DmxBinary.ReadFile(source);
        var text = new StringBuilder();
        foreach (var mesh in MapMeshes.Read(document))
        {
            var e = mesh.Element!;
            text.AppendLine($"{e.Type} {e.Name} id {mesh.NodeId} parent {mesh.ParentType} {mesh.ParentClass} hidden {mesh.Hidden} faces {mesh.Faces.Length}: "
                            + string.Join(", ", mesh.Faces.GroupBy(f => f.Material).Select(g => $"{g.Key} x{g.Count()}")));
            var all = mesh.Faces.SelectMany(f => f.Corners).ToArray();
            if (all.Length > 0)
                text.AppendLine($"   bounds {System.Numerics.Vector3.Min(all.Aggregate(System.Numerics.Vector3.Min), all[0])} .. {all.Aggregate(System.Numerics.Vector3.Max)}; triangles {mesh.Faces.Sum(f => f.Corners.Length - 2)}");
            foreach (var (k, v) in e.Attributes)
            {
                if (k is "meshData" or "children")
                    continue;
                text.AppendLine($"   {k} = {Show(v)}");
            }
            var data = e.Get<DmxBinary.Element>("meshData");
            var names = (data?.Get<object?[]>("materials") ?? []).Select(x => x as string ?? "").ToArray();
            var faces = data?.Get<object?[]>("faceEdgeIndices")?.Length ?? 0;
            var verts = data?.Get<object?[]>("vertexEdgeIndices")?.Length ?? 0;
            text.AppendLine($"   faces {faces} vertices {verts} materials {string.Join(", ", names)}");
            text.AppendLine($"   world {string.Join(' ', mesh.World.Select(f => f.ToString("R")))}");
        }
        if (p.Length > 2)
            File.WriteAllText(p[2], text.ToString());
        else
            output.WriteLine(text.ToString());
    }

    /// <summary>
    /// <c>VMAPCORNERS=addon|map|nodeId[|out file]</c>: one mesh's faces corner
    /// by corner, raw streams (position through vertexDataIndices, then each
    /// faceVertexData stream), in face loop order.
    /// </summary>
    [Fact]
    public void Corners()
    {
        if (Environment.GetEnvironmentVariable("VMAPCORNERS") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        if (MapFixtures.VmapSource(p[0], p[1]) is not { } source)
            return;
        var document = DmxBinary.ReadFile(source);
        var mesh = MapMeshes.Read(document).First(m => m.NodeId == int.Parse(p[2]));
        var data = mesh.Element!.Get<DmxBinary.Element>("meshData")!;
        int[] Ints(string n) => (data.Get<object?[]>(n) ?? []).Select(x => x is int i ? i : -1).ToArray();
        var next = Ints("edgeNextIndices");
        var to = Ints("edgeVertexIndices");
        var first = Ints("faceEdgeIndices");
        var vertexData = Ints("vertexDataIndices");
        var cornerData = Ints("edgeVertexDataIndices");
        var positions = data.Get<DmxBinary.Element>("vertexData")!.GetElements("streams").First(s => s.Name.StartsWith("position", StringComparison.Ordinal)).Get<object?[]>("data")!;
        var streams = data.Get<DmxBinary.Element>("faceVertexData")!.GetElements("streams").ToList();
        var text = new StringBuilder();
        text.AppendLine($"world {string.Join(' ', mesh.World.Select(f => f.ToString("R")))}");
        text.AppendLine("streams: " + string.Join(", ", streams.Select(s => s.Name)));
        foreach (var fs in data.Get<DmxBinary.Element>("faceData")!.GetElements("streams"))
            text.AppendLine($"face stream {fs.Name}: {string.Join(" ", (fs.Get<object?[]>("data") ?? []).Select(Show))}");
        for (var f = 0; f < first.Length; f++)
        {
            text.AppendLine($"face {f}");
            var e = first[f];
            do
            {
                var line = $"  v{to[e]} pos {Show(positions[vertexData[to[e]]])}";
                foreach (var s in streams)
                    line += $" | {s.Name.Split(':')[0]} {Show((s.Get<object?[]>("data") ?? [])[cornerData[e]])}";
                text.AppendLine(line);
                e = next[e];
            } while (e != first[f]);
        }
        if (p.Length > 3)
            File.WriteAllText(p[3], text.ToString());
        else
            output.WriteLine(text.ToString());
    }

    /// <summary><c>VMAPENT=addon|map|classname</c>: each entity of the class, its origin, angles and keys.</summary>
    [Fact]
    public void Entities()
    {
        if (Environment.GetEnvironmentVariable("VMAPENT") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        if (MapFixtures.VmapSource(p[0], p[1]) is not { } source)
            return;
        var document = DmxBinary.ReadFile(source);
        foreach (var e in MapEntities.From(document).Where(x => x.ClassName.Equals(p[2], StringComparison.OrdinalIgnoreCase)))
        {
            output.WriteLine($"{e.ClassName}#{e.NodeId} origin {e.Origin} angles {e.Angles}");
            foreach (var kv in e.Keys)
                output.WriteLine($"   {kv.Key} = {kv.Value}");
        }
    }

    private static string Show(object? v) => v switch
    {
        null => "null",
        DmxBinary.Element el => $"<{el.Type} {el.Name}>",
        object?[] arr => $"[{string.Join(", ", arr.Take(8).Select(Show))}{(arr.Length > 8 ? $", ... {arr.Length}" : "")}]",
        System.Numerics.Vector2 v2 => $"{v2.X:R} {v2.Y:R}",
        System.Numerics.Vector3 v3 => $"{v3.X:R} {v3.Y:R} {v3.Z:R}",
        System.Numerics.Vector4 v4 => $"{v4.X:R} {v4.Y:R} {v4.Z:R} {v4.W:R}",
        _ => v.ToString() ?? "",
    };
}
