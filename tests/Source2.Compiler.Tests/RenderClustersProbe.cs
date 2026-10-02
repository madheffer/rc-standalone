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
    /// <summary>
    /// <c>RCLUSTERS_ENTRIES=&lt;nodeentries capture&gt;|&lt;compiled vpk&gt;</c>: the boxes
    /// from every BuildNode:out entry's triangle centroids in entry order (as
    /// CompileNode collects the layer's entries), then each triangle of a
    /// <c>_c&lt;k&gt;</c> model assigned and compared with k.
    /// </summary>
    [Fact]
    public void FromBuildNodeOutput()
    {
        if (Environment.GetEnvironmentVariable("RCLUSTERS_ENTRIES") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var entries = NodeEntriesFromVmap.Read(p[0]).Where(c => c.Stage == "BuildNode:out").ToList();
        var filter = Environment.GetEnvironmentVariable("RCLUSTERS_EXCLUDE") is { } ex ? Convert.ToUInt64(ex, 16) : 0UL;
        var centroids = new List<Vector3>();
        foreach (var e in entries)
        {
            if ((BitConverter.ToUInt64(e.Raw, 0x1b0) & filter) != 0)
                continue;
            var at = e.Layout.First(x => x.Name.Equals("position", StringComparison.OrdinalIgnoreCase)).First;
            Vector3 P(int v) => new(e.Vertices[(v * e.Stride) + at], e.Vertices[(v * e.Stride) + at + 1], e.Vertices[(v * e.Stride) + at + 2]);
            for (var t = 0; t < e.Indices.Length / 3; t++)
                centroids.Add(RenderClusters.Centroid(P(e.Indices[t * 3]), P(e.Indices[(t * 3) + 1]), P(e.Indices[(t * 3) + 2])));
        }
        using var package = new Package();
        package.Read(p[1]);
        var clustered = new List<(string Name, List<(Vector3, Vector3, Vector3)> Triangles, int K)>();
        foreach (var entry in package.Entries.GetValueOrDefault("vmdl_c") ?? [])
        {
            var m = System.Text.RegularExpressions.Regex.Match(entry.FileName, @"^n\d+_lr\d+_c(\d+)_");
            if (!entry.GetFullPath().Contains("/worldnodes/", StringComparison.Ordinal) || !m.Success)
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
                    var pos = VBIB.GetVector3AttributeArray(vb, vb.InputLayoutFields.First(f => f.SemanticName is "POSITION" or "position"));
                    var ib = mesh.VBIB.IndexBuffers[dc.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                    var start = dc.GetInt32Property("m_nStartIndex");
                    var n = dc.GetInt32Property("m_nIndexCount");
                    var bas = dc.GetInt32Property("m_nBaseVertex");
                    int I(int at) => (ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, at * 2) : BitConverter.ToInt32(ib.Data, at * 4)) + bas;
                    for (var i = 0; i + 2 < n; i += 3)
                        tris.Add((pos[I(start + i)], pos[I(start + i + 1)], pos[I(start + i + 2)]));
                }
            clustered.Add((entry.FileName, tris, int.Parse(m.Groups[1].Value)));
        }
        output.WriteLine($"{centroids.Count} centroids from {entries.Count} entries; {clustered.Count} cluster models");
        foreach (var size in new[] { 1024f })
            foreach (var sizeSplit in new[] { false })
            {
                var boxes = RenderClusters.Build([.. centroids], 2048, size, sizeSplit);
                {
                    // Each entry's triangles in entry order, against the cluster of the shipped model holding them.
                    static string K(Vector3 a, Vector3 b, Vector3 c) => string.Join("|", new[] { a, b, c }.Select(v => $"{v.X:R},{v.Y:R},{v.Z:R}").Order());
                    var shippedK = new Dictionary<string, int>();
                    foreach (var (name, tris, k) in clustered)
                        foreach (var t in tris)
                            shippedK.TryAdd(K(t.Item1, t.Item2, t.Item3), k);
                    int ok = 0, all = 0;
                    var wrongBy = new SortedDictionary<string, int>();
                    foreach (var e in entries)
                    {
                        var at = e.Layout.First(x => x.Name.Equals("position", StringComparison.OrdinalIgnoreCase)).First;
                        Vector3 P(int v) => new(e.Vertices[(v * e.Stride) + at], e.Vertices[(v * e.Stride) + at + 1], e.Vertices[(v * e.Stride) + at + 2]);
                        var tri = Enumerable.Range(0, e.Indices.Length / 3).Select(t => (P(e.Indices[t * 3]), P(e.Indices[(t * 3) + 1]), P(e.Indices[(t * 3) + 2]))).ToList();
                        if (!tri.Any(t => shippedK.ContainsKey(K(t.Item1, t.Item2, t.Item3))) || (BitConverter.ToUInt64(e.Raw, 0x1b0) & 8) == 0)
                            continue;
                        var got = RenderClusters.Assign(boxes, tri.Select(t => RenderClusters.Centroid(t.Item1, t.Item2, t.Item3)));
                        for (var t = 0; t < tri.Count; t++)
                            if (shippedK.TryGetValue(K(tri[t].Item1, tri[t].Item2, tri[t].Item3), out var want))
                            {
                                all++;
                                if (got[t] == want)
                                    ok++;
                                else
                                    wrongBy[$"{want}->{got[t]}"] = wrongBy.GetValueOrDefault($"{want}->{got[t]}") + 1;
                            }
                    }
                    output.WriteLine($"  ENTRY ORDER size {size}: {ok}/{all} block-light entry triangles in the shipped cluster; wrong {string.Join(" ", wrongBy.Select(kv => $"{kv.Key}x{kv.Value}"))}");
                }
                int same = 0, total = 0, blSame = 0, blTotal = 0;
                foreach (var (name, tris, k) in clustered)
                {
                    var got = RenderClusters.Assign(boxes, tris.Select(t => RenderClusters.Centroid(t.Item1, t.Item2, t.Item3)));
                    var hit = got.Count(g => g == k);
                    same += hit;
                    total += got.Length;
                    if (name.Contains("_bl_", StringComparison.Ordinal) && hit != got.Length)
                        output.WriteLine($"    {name} k {k}: assigned {string.Join(" ", got.GroupBy(g => g).Select(g => $"{g.Key}x{g.Count()}"))}");
                    if (name.Contains("_bl_", StringComparison.Ordinal))
                    {
                        blSame += hit;
                        blTotal += got.Length;
                    }
                }
                for (var b = 0; b < boxes.Count; b++)
                    output.WriteLine($"    box {b}: {boxes[b].Min} .. {boxes[b].Max}");
                output.WriteLine($"  size {size} sizeSplit {sizeSplit}: {boxes.Count} boxes; {same}/{total} triangles in their model's cluster ({blSame}/{blTotal} block light)");
            }
    }

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
