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
            if ((BitConverter.ToUInt64(e.Raw, 0x1b0) & filter) != 0 || !RenderClusters.Contributes(BitConverter.ToUInt64(e.Raw, 0x1b0)))
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

    /// <summary>
    /// <c>RCLUSTERS_CAPTURE=&lt;capture_rclusters.py output&gt;</c> (optionally with
    /// <c>RCLUSTERS_ENTRIES</c> for the BuildNode capture): our boxes from
    /// Valve's own centroids against Valve's boxes, and Valve's centroids
    /// against those of every BuildNode:out entry.
    /// </summary>
    [Fact]
    public void AgainstCapturedBoxes()
    {
        if (Environment.GetEnvironmentVariable("RCLUSTERS_CAPTURE") is not { Length: > 0 } path)
            return;
        var data = File.ReadAllBytes(path);
        Vector3[]? centroids = null;
        List<(Vector3 Min, Vector3 Max)>? valve = null;
        for (var at = 0; at < data.Length;)
        {
            var n = BitConverter.ToInt32(data, at);
            var head = System.Text.Json.JsonDocument.Parse(data.AsMemory(at + 4, n)).RootElement;
            at += 4 + n;
            var m = BitConverter.ToInt32(data, at);
            var blob = data.AsSpan(at + 4, m).ToArray();
            at += 4 + m;
            var f = new float[blob.Length / 4];
            Buffer.BlockCopy(blob, 0, f, 0, blob.Length);
            if (head.GetProperty("ev").GetString() == "in")
                centroids = [.. Enumerable.Range(0, f.Length / 3).Select(i => new Vector3(f[i * 3], f[i * 3 + 1], f[i * 3 + 2]))];
            else
                valve = [.. Enumerable.Range(0, f.Length / 6).Select(i => (new Vector3(f[i * 6], f[i * 6 + 1], f[i * 6 + 2]), new Vector3(f[i * 6 + 3], f[i * 6 + 4], f[i * 6 + 5])))];
        }
        var ours = RenderClusters.Build([.. centroids!], 2048, 1024f, false);
        static string B((Vector3 Min, Vector3 Max) b) => $"{b.Min.X:R},{b.Min.Y:R},{b.Min.Z:R}..{b.Max.X:R},{b.Max.Y:R},{b.Max.Z:R}";
        // Build grows each box by 1/32 a side (exactly, at these magnitudes): the raw boxes for comparison.
        ours = [.. ours.Select(b => (b.Min + new Vector3(0.03125f), b.Max - new Vector3(0.03125f)))];
        var same = ours.Count == valve!.Count ? ours.Zip(valve).Count(z => B(z.First) == B(z.Second)) : -1;
        output.WriteLine($"{centroids!.Length} captured centroids; ours {ours.Count} boxes, valve {valve.Count}; {same} identical in order");
        for (var i = 0; i < Math.Min(4, valve.Count); i++)
            output.WriteLine($"  box {i}: ours {(i < ours.Count ? B(ours[i]) : "-")} valve {B(valve[i])}");
        if (Environment.GetEnvironmentVariable("RCLUSTERS_ENTRIES") is { Length: > 0 } spec)
        {
            // Which entries' triangles make Valve's centroid list, in order.
            var entries = NodeEntriesFromVmap.Read(spec.Split('|')[0]).Where(c => c.Stage == "BuildNode:out").ToList();
            var at = 0;
            var used = new List<int>();
            var skipped = new SortedDictionary<string, int>();
            for (var e = 0; e < entries.Count && at < centroids.Length; e++)
            {
                var c = entries[e];
                var pf = c.Layout.First(x => x.Name.Equals("position", StringComparison.OrdinalIgnoreCase)).First;
                Vector3 P(int v) => new(c.Vertices[(v * c.Stride) + pf], c.Vertices[(v * c.Stride) + pf + 1], c.Vertices[(v * c.Stride) + pf + 2]);
                var mine = Enumerable.Range(0, c.Indices.Length / 3).Select(t => RenderClusters.Centroid(P(c.Indices[t * 3]), P(c.Indices[t * 3 + 1]), P(c.Indices[t * 3 + 2]))).ToList();
                if (mine.Count > 0 && at + mine.Count <= centroids.Length && mine.SequenceEqual(centroids.Skip(at).Take(mine.Count)))
                {
                    used.Add(e);
                    at += mine.Count;
                }
                else
                {
                    var flags = BitConverter.ToUInt64(c.Raw, 0x1b0);
                    var key = $"flags {flags:x} objflags {BitConverter.ToUInt32(c.Raw, 0xbc):x}";
                    skipped[key] = skipped.GetValueOrDefault(key) + 1;
                }
            }
            output.WriteLine($"{used.Count} entries in order make {at} of {centroids.Length} centroids; skipped by flags: {string.Join("; ", skipped.Select(kv => $"{kv.Key} x{kv.Value}"))}");
            // The entries a mesh list takes (MeshLists.Assign on the captured fields) against those Valve's centroids came from.
            var listed = Enumerable.Range(0, entries.Count).Where(i =>
            {
                var r = entries[i].Raw;
                return MeshLists.Assign(new MeshLists.Input(BitConverter.ToUInt64(r, 0x1b0), BitConverter.ToUInt32(r, 0xbc), BitConverter.ToInt32(r, 0xac),
                    BitConverter.ToSingle(r, 0xb4), false, false)) != null;
            }).ToList();
            foreach (var i in used.Except(listed))
            {
                var r = entries[i].Raw;
                output.WriteLine($"  only valve: entry {i} {Path.GetFileName(entries[i].Material)} flags {BitConverter.ToUInt64(r, 0x1b0):x} objflags {BitConverter.ToUInt32(r, 0xbc):x} order {BitConverter.ToInt32(r, 0xac)} fade {BitConverter.ToSingle(r, 0xb4)}");
            }
            output.WriteLine($"LISTED {listed.Count} entries a list takes; same set as Valve's {listed.SequenceEqual(used)}; only listed {listed.Except(used).Count()}, only valve {used.Except(listed).Count()}");
            // Assignment on Valve's boxes (raw, and grown by 1/32): each block-light entry's triangles against the shipped cluster.
            using var package = new Package();
            package.Read(spec.Split('|')[1]);
            static string K(Vector3 a, Vector3 b, Vector3 c) => string.Join("|", new[] { a, b, c }.Select(v => $"{v.X:R},{v.Y:R},{v.Z:R}").Order());
            var shippedK = new Dictionary<string, int>();
            foreach (var entry in package.Entries.GetValueOrDefault("vmdl_c") ?? [])
            {
                var mm = System.Text.RegularExpressions.Regex.Match(entry.FileName, @"^n\d+_lr\d+_c(\d+)_");
                if (!entry.GetFullPath().Contains("/worldnodes/", StringComparison.Ordinal) || !mm.Success)
                    continue;
                package.ReadEntry(entry, out var bytes);
                using var resource = new Resource();
                resource.Read(new MemoryStream(bytes));
                var mesh = ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD().First().Mesh;
                foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                    foreach (var dc in so.GetArray("m_drawCalls"))
                    {
                        var vb = mesh.VBIB.VertexBuffers[dc.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
                        var pos = VBIB.GetVector3AttributeArray(vb, vb.InputLayoutFields.First(f => f.SemanticName is "POSITION" or "position"));
                        var ib = mesh.VBIB.IndexBuffers[dc.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                        var st = dc.GetInt32Property("m_nStartIndex");
                        var n = dc.GetInt32Property("m_nIndexCount");
                        var bas = dc.GetInt32Property("m_nBaseVertex");
                        int I(int x) => (ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, x * 2) : BitConverter.ToInt32(ib.Data, x * 4)) + bas;
                        for (var i = 0; i + 2 < n; i += 3)
                            shippedK.TryAdd(K(pos[I(st + i)], pos[I(st + i + 1)], pos[I(st + i + 2)]), int.Parse(mm.Groups[1].Value));
                    }
            }
            foreach (var grow in new[] { 0f, 0.03125f })
            {
                var boxes = valve.Select(b => (b.Min - new Vector3(grow), b.Max + new Vector3(grow))).ToList();
                int ok = 0, all = 0;
                var wrong = new SortedDictionary<string, int>();
                foreach (var c in entries.Where(c => (BitConverter.ToUInt64(c.Raw, 0x1b0) & 8) != 0))
                {
                    var pf = c.Layout.First(x => x.Name.Equals("position", StringComparison.OrdinalIgnoreCase)).First;
                    Vector3 P(int v) => new(c.Vertices[(v * c.Stride) + pf], c.Vertices[(v * c.Stride) + pf + 1], c.Vertices[(v * c.Stride) + pf + 2]);
                    var tri = Enumerable.Range(0, c.Indices.Length / 3).Select(t => (P(c.Indices[t * 3]), P(c.Indices[t * 3 + 1]), P(c.Indices[t * 3 + 2]))).ToList();
                    var got = RenderClusters.Assign(boxes, tri.Select(t => RenderClusters.Centroid(t.Item1, t.Item2, t.Item3)));
                    for (var t = 0; t < tri.Count; t++)
                        if (shippedK.TryGetValue(K(tri[t].Item1, tri[t].Item2, tri[t].Item3), out var want))
                        {
                            all++;
                            if (got[t] == want)
                                ok++;
                            else
                                wrong[$"{want}->{got[t]}"] = wrong.GetValueOrDefault($"{want}->{got[t]}") + 1;
                        }
                }
                output.WriteLine($"ASSIGN grow {grow}: {ok}/{all} block-light triangles; wrong {string.Join(" ", wrong.Select(kv => $"{kv.Key}x{kv.Value}"))}");
            }
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
