using System.Numerics;
using Source2.Compiler.Maps;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>RCLUSTERS=map[,map...]</c>): <see cref="RenderClusters"/>
/// fed the centroids of every triangle of a map's node models (Valve's own
/// output, world space), then each triangle of a <c>_c&lt;k&gt;</c> model
/// assigned and compared with k, under a few parameter choices.
/// </summary>
public class RenderClustersProbe(ITestOutputHelper output)
{
    [Fact]
    public void AgainstNodeModels()
    {
        if (Environment.GetEnvironmentVariable("RCLUSTERS") is not { Length: > 0 } maps)
            return;
        foreach (var map in maps.Split(','))
        {
            using var package = new Package();
            package.Read(Path.Combine(@"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe\maps", map + ".vpk"));
            var models = new List<(string Name, List<(Vector3, Vector3, Vector3)> Triangles)>();
            foreach (var entry in package.Entries.GetValueOrDefault("vmdl_c") ?? [])
            {
                if (!entry.GetFullPath().Contains("/worldnodes/", StringComparison.Ordinal))
                    continue;
                package.ReadEntry(entry, out var bytes);
                using var resource = new Resource();
                resource.Read(new MemoryStream(bytes));
                var mesh = ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD().First().Mesh;
                var tris = new List<(Vector3, Vector3, Vector3)>();
                foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                    foreach (var dc in so.GetArray("m_drawCalls"))
                    {
                        var vb = mesh.VBIB.VertexBuffers[dc.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
                        if (!vb.InputLayoutFields.Any(f => f.SemanticName is "POSITION" or "position"))
                            continue;
                        var pos = VBIB.GetVector3AttributeArray(vb, vb.InputLayoutFields.First(f => f.SemanticName is "POSITION" or "position"));
                        var ib = mesh.VBIB.IndexBuffers[dc.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                        var start = dc.GetInt32Property("m_nStartIndex");
                        var n = dc.GetInt32Property("m_nIndexCount");
                        int I(int at) => ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, at * 2) : BitConverter.ToInt32(ib.Data, at * 4);
                        for (var i = 0; i + 2 < n; i += 3)
                            tris.Add((pos[I(start + i)], pos[I(start + i + 1)], pos[I(start + i + 2)]));
                    }
                models.Add((Path.GetFileNameWithoutExtension(entry.GetFullPath()), tris));
            }
            var clustered = models.Select(m => (m.Name, m.Triangles, K: System.Text.RegularExpressions.Regex.Match(m.Name, @"^n\d+_lr\d+_c(\d+)_")))
                                  .Where(m => m.K.Success).Select(m => (m.Name, m.Triangles, K: int.Parse(m.K.Groups[1].Value))).ToList();
            output.WriteLine($"{map}: {models.Count} models, {models.Sum(m => m.Triangles.Count)} triangles, {clustered.Count} cluster models, highest index {(clustered.Count == 0 ? -1 : clustered.Max(c => c.K))}");
            foreach (var (label, pick) in new (string, Func<string, bool>)[]
                     {
                         ("all", _ => true),
                         ("no blocklight", n => !n.Contains("_bl_", StringComparison.Ordinal)),
                         ("no props", n => !n.Contains("_agg_prop_", StringComparison.Ordinal)),
                         ("no props, no blocklight", n => !n.Contains("_agg_prop_", StringComparison.Ordinal) && !n.Contains("_bl_", StringComparison.Ordinal)),
                     })
                foreach (var (minTris, size, sizeSplit) in new[] { (2048, 1024f, true), (2048, 1024f, false), (2048, 2048f, true) })
                {
                    var centroids = models.Where(m => pick(m.Name)).SelectMany(m => m.Triangles).Select(t => RenderClusters.Centroid(t.Item1, t.Item2, t.Item3)).ToArray();
                    var boxes = RenderClusters.Build(centroids, minTris, size, sizeSplit);
                    int same = 0, total = 0;
                    foreach (var (name, tris, k) in clustered)
                    {
                        var got = RenderClusters.Assign(boxes, tris.Select(t => RenderClusters.Centroid(t.Item1, t.Item2, t.Item3)));
                        same += got.Count(g => g == k);
                        total += got.Length;
                    }
                    output.WriteLine($"  {label}, min {minTris}, size {size}, sizeSplit {sizeSplit}: {boxes.Count} clusters, {same} of {total} cluster-model triangles assigned their model's index");
                }
        }
    }
}
