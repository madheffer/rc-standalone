using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>SLIVER=&lt;addon&gt;|&lt;map&gt;|&lt;node&gt;|&lt;x,y,z&gt;</c>): the world
/// collision pieces of one node, and for the triangles with a corner at the
/// given world point, the .vmap face they come from: its corner count, its
/// corners in the mesh's own space and in world space, and how its triangles
/// were cut.
/// </summary>
public class MeshSliverProbe(ITestOutputHelper output)
{
    [Fact]
    public void Faces()
    {
        if (Environment.GetEnvironmentVariable("SLIVER") is not { } spec)
            return;
        var p = spec.Split('|');
        var node = int.Parse(p[2]);
        var at = p[3].Split(',').Select(float.Parse).ToArray();
        var target = new Vector3(at[0], at[1], at[2]);
        var doc = MapSource.Read(MapFixtures.VmapSource(p[0], p[1])!);
        foreach (var mesh in MapMeshes.Read(doc).Where(m => m.NodeId == node && m.Through.Count > 0))
        {
            var data = mesh.Element!.Get<DmxBinary.Element>("meshData")!;
            output.WriteLine($"mesh {mesh.NodeId}: instances {mesh.Instances.Length}, world {string.Join(" ", mesh.World.Select(v => v.ToString("R")))}");
            var faceEdges = (data.Get<object?[]>("faceEdgeIndices") ?? []).Select(x => (int)x!).ToArray();
            var next = (data.Get<object?[]>("edgeNextIndices") ?? []).Select(x => (int)x!).ToArray();
            var to = (data.Get<object?[]>("edgeVertexIndices") ?? []).Select(x => (int)x!).ToArray();
            var pos = (data.Get<DmxBinary.Element>("vertexData")?.GetElements("streams").First(s => s.Name.StartsWith("position", StringComparison.Ordinal)).Get<object?[]>("data") ?? []).Select(x => (Vector3)x!).ToArray();
            var sizes = faceEdges.Select(first => { var n = 0; var h = first; do { n++; h = next[h]; } while (h != first); return n; }).ToArray();
            output.WriteLine($"  faces by corner count: {string.Join(", ", sizes.GroupBy(x => x).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}"))}");
            for (var f = 0; f < faceEdges.Length; f++)
            {
                var loop = new List<int>();
                var h = faceEdges[f];
                do
                {
                    loop.Add(to[h]);
                    h = next[h];
                }
                while (h != faceEdges[f]);
                var world = loop.Select(v => MapMeshes.Transform(mesh.World, pos[v] * mesh.Scales)).ToArray();
                if (!world.Any(w => Vector3.Distance(w, target) < 0.01f))
                    continue;
                output.WriteLine($"  face {f}: {loop.Count} corners");
                for (var k = 0; k < loop.Count; k++)
                    output.WriteLine($"    {loop[k]}: local {pos[loop[k]]:R} world {world[k]:R}");
                var cut = PolygonTriangulator.Triangulate([.. world]);
                output.WriteLine($"    cut on world: {string.Join(" ", cut)}");
                var cutLocal = PolygonTriangulator.Triangulate([.. loop.Select(v => pos[v])]);
                output.WriteLine($"    cut on local: {string.Join(" ", cutLocal)}");
            }
        }
    }
}
