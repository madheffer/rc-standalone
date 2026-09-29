using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>WNGROUP=addon|map|id,id,...|out.json</c>): the named mesh
/// nodes' render pieces (position in the world, texcoord, normal, tangent),
/// each welded at 1/32, then concatenated in the order given and welded
/// again, written as JSON for trying the vertex optimisers against a node
/// model: <c>{ pieces: [...], merged: {vertices, indices}, welded: {...} }</c>.
/// </summary>
public class WorldNodeGroupProbe(ITestOutputHelper output)
{
    [Fact]
    public void Export()
    {
        if (Environment.GetEnvironmentVariable("WNGROUP") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        if (MapFixtures.VmapSource(p[0], p[1]) is not { } source)
            return;
        var document = DmxBinary.ReadFile(source);
        var meshes = MapMeshes.Read(document);
        var ids = p[2].Split(',').Select(int.Parse).ToArray();
        var pieces = new List<object>();
        var allV = new List<float>();
        var allI = new List<int>();
        var stride = 0;
        IReadOnlyList<Physics.MeshWeld.Stream>? layout = null;
        foreach (var id in ids)
        {
            var mesh = meshes.First(m => m.NodeId == id);
            var w = mesh.World;
            Vector3 Place(Vector3 v) => new(w[0] * v.X + w[1] * v.Y + w[2] * v.Z + w[3], w[4] * v.X + w[5] * v.Y + w[6] * v.Z + w[7],
                                            w[8] * v.X + w[9] * v.Y + w[10] * v.Z + w[11]);
            foreach (var piece in MapMeshCorners.Build(mesh.Element!, true, Place, withTangent: true))
            {
                var v = piece.Vertices.ToArray();
                for (var i = 0; i < v.Length; i += piece.Stride)
                {
                    var q = Place(new Vector3(v[i], v[i + 1], v[i + 2]));
                    (v[i], v[i + 1], v[i + 2]) = (q.X, q.Y, q.Z);
                }
                var (wv, wi) = Physics.MeshWeld.Weld(v, piece.Stride, piece.Indices, piece.Streams, 1f / 32f);
                pieces.Add(new { id, material = piece.Material, stride = piece.Stride, vertices = wv, indices = wi,
                                 layout = piece.Streams.Select(s => $"{s.Name}:{s.First}:{s.Count}") });
                stride = piece.Stride;
                layout = piece.Streams;
                var baseVertex = allV.Count / stride;
                allV.AddRange(wv);
                allI.AddRange(wi.Select(x => x + baseVertex));
            }
        }
        var (mv, mi) = Physics.MeshWeld.Weld([.. allV], stride, [.. allI], layout!, 1f / 32f);
        var json = JsonSerializer.Serialize(new
        {
            pieces,
            merged = new { vertices = allV, indices = allI },
            welded = new { vertices = mv, indices = mi },
            stride,
        });
        File.WriteAllText(p[3], json);
        output.WriteLine($"{pieces.Count} pieces, {allV.Count / stride} vertices merged, {mv.Length / stride} after the weld");
    }
}
