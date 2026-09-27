using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: a .vmap's painted world meshes, each .vmap vertex's texcoords
/// per corner as stored and after the builder's texcoord shift, to compare
/// with a captured blend split's mesh. <c>TEXPROBE=&lt;.vmap&gt;</c>.
/// </summary>
public class TexcoordProbe(ITestOutputHelper output)
{
    [Fact]
    public void Texcoords()
    {
        if (Environment.GetEnvironmentVariable("TEXPROBE") is not { Length: > 0 } path)
            return;
        var doc = DmxBinary.ReadFile(path);
        foreach (var mesh in Source2.Compiler.Maps.MapMeshes.Read(doc).Where(m => m.ParentType is "CMapWorld" or "CMapGroup"))
        {
            var data = mesh.Element!.Get<DmxBinary.Element>("meshData")!;
            var streams = data.Get<DmxBinary.Element>("faceVertexData")?.GetElements("streams").ToList() ?? [];
            if (!streams.Any(s => s.Name.Split(':')[0] == "VertexPaintBlendParams"))
                continue;
            output.WriteLine($"node {mesh.NodeId}: streams {string.Join(", ", streams.Select(s => s.Name))}");
            var texcoord = streams.First(s => s.Name.Split(':')[0] == "texcoord").Get<object?[]>("data")!.Select(x => (Vector2)x!).ToArray();
            var to = (data.Get<object?[]>("edgeVertexIndices") ?? []).Select(x => (int)x!).ToArray();
            var corner = (data.Get<object?[]>("edgeVertexDataIndices") ?? []).Select(x => (int)x!).ToArray();
            var positions = data.Get<DmxBinary.Element>("vertexData")!.GetElements("streams").First(s => s.Name.Split(':')[0] == "position").Get<object?[]>("data")!.Select(x => (Vector3)x!).ToArray();
            for (var h = 0; h < to.Length && h < 24; h++)
                output.WriteLine($"  half-edge {h}: vertex {to[h]} {positions[to[h]]} corner {corner[h]} uv {texcoord[corner[h]]}");
            foreach (var piece in Source2.Compiler.Maps.MapMeshCorners.Build(mesh.Element!))
            {
                var uv = piece.Streams.First(s => s.Name == "texcoord");
                output.WriteLine($"  shifted piece material {piece.Material}: first uvs {string.Join(" ", Enumerable.Range(0, Math.Min(6, piece.Vertices.Length / piece.Stride)).Select(k => $"({piece.Vertices[(k * piece.Stride) + uv.First]},{piece.Vertices[(k * piece.Stride) + uv.First + 1]})"))}");
            }
        }
    }
}
