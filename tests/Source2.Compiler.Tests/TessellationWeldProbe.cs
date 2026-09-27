using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: a subdivided mesh's tessellated points of one material near
/// a world position, before the piece weld, in the order the piece meets them.
/// <c>TESSWELD=&lt;.vmap&gt;|&lt;node&gt;|&lt;material&gt;|x,y,z[;x,y,z...]</c>.
/// </summary>
public class TessellationWeldProbe(ITestOutputHelper output)
{
    [Fact]
    public void Near()
    {
        if (Environment.GetEnvironmentVariable("TESSWELD") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var doc = DmxBinary.ReadFile(p[0]);
        var mesh = MapMeshes.Read(doc).First(m => m.NodeId == int.Parse(p[1])).Element!;
        var material = int.Parse(p[2]);
        var data = mesh.Get<DmxBinary.Element>("meshData")!;
        var faceData = (data.Get<object?[]>("faceDataIndices") ?? []).Select(x => x is int i ? i : 0).ToArray();
        var faceMaterials = (data.Get<DmxBinary.Element>("faceData")?.GetElements("streams")
            .FirstOrDefault(st => st.Name.StartsWith("materialindex", StringComparison.Ordinal))?.Get<object?[]>("data") ?? [])
            .Select(x => x is int i ? i : 0).ToArray();
        var scales = mesh.GetValue<Vector3>("scales") ?? Vector3.One;
        var toWorld = MapMeshes.Local(mesh);
        var (cut, _) = MeshTessellation.TriangulateBuilder(data);
        var order = new List<(int Source, int Face, Vector3 World, Vector3 Local)>();
        var seen = new HashSet<int>();
        for (var t = 0; t < cut.Faces.Count; t++)
        {
            var f = cut.Faces[t];
            if ((faceMaterials.Length == 0 ? 0 : faceMaterials[faceData[f]]) != material)
                continue;
            for (var k = 0; k < 3; k++)
            {
                var v = cut.Indices[(t * 3) + k];
                if (seen.Add(v))
                    order.Add((v, f, MapMeshes.Transform(toWorld, cut.Positions[v] * scales), cut.Positions[v]));
            }
        }
        foreach (var target in p[3].Split(';'))
        {
            var c = target.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var at = new Vector3(c[0], c[1], c[2]);
            output.WriteLine($"near {at:R}:");
            for (var i = 0; i < order.Count; i++)
            {
                var w = order[i].World;
                if (MathF.Abs(w.X - at.X) <= 1f / 32f && MathF.Abs(w.Y - at.Y) <= 1f / 32f && MathF.Abs(w.Z - at.Z) <= 1f / 32f)
                    output.WriteLine($"  #{i} source {order[i].Source} face {order[i].Face} world ({w.X:R},{w.Y:R},{w.Z:R}) local ({order[i].Local.X:R},{order[i].Local.Y:R},{order[i].Local.Z:R})");
            }
        }
    }
}
