using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>NOVISPROBE=&lt;vmap&gt;</c>): a node compiled without vis, as a
/// -world build does: the merged lists' entries (material, attributes, size,
/// bounds), the node's box grid and what CompileNode makes of them.
/// NOVISPROBE_MAT=&lt;material file name&gt; lists every entry of that material
/// with its streams (name/index/count/precise/type) and layout hash.
/// </summary>
public class NoVisClusterProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("NOVISPROBE") is not { Length: > 0 } vmap || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var doc = MapSource.Read(vmap);
        EntityLumpAuthor.Lighting? lighting = null;
        EntityLumpSet.Author(MapEntities.From(doc), MapFixtures.GameSchema(), Path.GetFileNameWithoutExtension(vmap),
                             MapEntities.FixupEntityNames(doc), doc, MapFixtures.SmartPropLocators, lighting: l => lighting = l);
        var world = WorldNodeBuild.WorldEntries(doc, content, shaders, NodeEntriesFromVmap.RendersAsWorld(game));
        var props = WorldNodeBuild.PropEntries(doc, content, shaders);
        var descriptors = NodeOverlays.FromWorld(doc);
        var (built, overlays) = WorldNodeBuild.BuildNode(world, props, descriptors);
        var envMaps = WorldNodeBuild.EnvMaps(built, lighting!.Volumes);
        var lists = WorldNodeBuild.MergerInputs(built, overlays, descriptors, envMaps, content, shaders, kinds: [.. MeshLists.Order.Select(o => o.Kind)])
            .ToDictionary(kv => kv.Key, kv => kv.Value.Select(m => new WorldNodeCompile.Entry(m.Material, m.Stride, m.Vertices, m.Indices, m.Facts)).ToList());

        if (Environment.GetEnvironmentVariable("NOVISPROBE_MAT") is { Length: > 0 } m)
        {
            foreach (var b in built.Where(b => Path.GetFileNameWithoutExtension(b.Source.Entry.Material) == m))
                output.WriteLine($"built node {b.Source.Header.NodeId} v {b.Vertices.Length / b.Source.Entry.Stride} streams {string.Join(",", b.Source.Entry.Streams.Select(x => $"{x.Name}/{x.First}/{x.Count}/{x.Type}"))}");
            if (built.FirstOrDefault(b => Path.GetFileNameWithoutExtension(b.Source.Entry.Material) == m) is { } one && content.Read(one.Source.Entry.Material + "_c") is { } bytes)
                output.WriteLine($"signature {string.Join(",", Source2.Compiler.MaterialAuthor.ExtractInputSignature(bytes).Select(x => x.Semantic))}");
        }
        var (min, max) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        foreach (var b in built)
            for (var k = 0; k + 2 < b.Vertices.Length; k += b.Source.Entry.Stride)
            {
                var p = new Vector3(b.Vertices[k], b.Vertices[k + 1], b.Vertices[k + 2]);
                (min, max) = (Vector3.Min(min, p), Vector3.Max(max, p));
            }
        var grid = RenderClusters.Grid(min, max, 1024f);
        output.WriteLine($"node {min} .. {max}: {grid.Count} grid boxes");
        foreach (var (kind, entries) in lists.Where(kv => kv.Value.Count > 0))
        {
            output.WriteLine($"{kind}: {entries.Count}");
            var only = Environment.GetEnvironmentVariable("NOVISPROBE_MAT");
            foreach (var e in only is { Length: > 0 } ? entries.Where(e => Path.GetFileNameWithoutExtension(e.Material) == only) : entries.Take(30))
            {
                var (lo, hi) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
                for (var k = 0; k + 2 < e.Vertices.Length; k += e.Stride)
                {
                    var p = new Vector3(e.Vertices[k], e.Vertices[k + 1], e.Vertices[k + 2]);
                    (lo, hi) = (Vector3.Min(lo, p), Vector3.Max(hi, p));
                }
                output.WriteLine($"  {Path.GetFileName(e.Material)} attr {e.Facts.Attributes:x} flags {e.Facts.ObjectFlags:x} stride {e.Stride} v {e.Vertices.Length / e.Stride} t {e.TriangleCount} node {e.Facts.NodeId} streams {string.Join(",", (e.Facts.MeshStreams ?? e.Facts.Streams).Select(s => $"{s.Name}/{s.Index}/{s.Count}/{s.Precise}/{s.Type}"))} hash {(int)WrbMeshEntry.LayoutHash(e.Facts.MeshStreams ?? e.Facts.Streams)} {lo}..{hi}");
            }
        }
        var layer = WorldNodeBuild.LayerEntries(built, overlays, descriptors, content, shaders);
        var result = WorldNodeCompile.Compile(lists, null, null, nodeBounds: (min, max), layer: layer);
        foreach (var o in result.Objects)
            output.WriteLine($"object {o.Name}: " + string.Join(" ", o.Meshes.Select(m => $"{Path.GetFileName(m.Material)}/v{m.Vertices.Length / m.Stride}/t{m.TriangleCount}/s{m.Stride}")));
        foreach (var a in result.Aggregates)
            output.WriteLine($"aggregate {a.Name}: " + string.Join(" ", a.Meshes.Select(m => $"v{m.Vertices.Length / m.Stride}/t{m.TriangleCount}")));
        if (Environment.GetEnvironmentVariable("NOVISPROBE_RCAPTURE") is { Length: > 0 } capture)
            CompareBoxes(capture, layer, result);
    }

    // NOVISPROBE_RCAPTURE=<capture_rclusters.py output>: Valve's one box-builder call
    // (centroids in, boxes out) against our render boxes and our layer's centroids
    // in BuildNode order (CompileNode's fallback when nothing else made boxes).
    private void CompareBoxes(string capture, List<WorldNodeCompile.Entry> layer, WorldNodeCompile.Result result)
    {
        var data = File.ReadAllBytes(capture);
        Vector3[] valveCentroids = [];
        List<(Vector3 Min, Vector3 Max)> valveBoxes = [];
        for (var at = 0; at < data.Length;)
        {
            var n = BitConverter.ToInt32(data, at);
            var head = System.Text.Json.JsonDocument.Parse(data.AsMemory(at + 4, n)).RootElement;
            at += 4 + n;
            var m = BitConverter.ToInt32(data, at);
            var f = new float[m / 4];
            Buffer.BlockCopy(data, at + 4, f, 0, m);
            at += 4 + m;
            if (head.GetProperty("ev").GetString() == "in")
                valveCentroids = [.. Enumerable.Range(0, f.Length / 3).Select(i => new Vector3(f[i * 3], f[i * 3 + 1], f[i * 3 + 2]))];
            else
                valveBoxes = [.. Enumerable.Range(0, f.Length / 6).Select(i => (new Vector3(f[i * 6], f[i * 6 + 1], f[i * 6 + 2]), new Vector3(f[i * 6 + 3], f[i * 6 + 4], f[i * 6 + 5])))];
        }
        output.WriteLine($"render boxes: ours {result.RenderBoxes.Count}, valve {valveBoxes.Count}");
        for (var i = 0; i < Math.Max(result.RenderBoxes.Count, valveBoxes.Count); i++)
            output.WriteLine($"  box {i}: ours {(i < result.RenderBoxes.Count ? $"{result.RenderBoxes[i].Min}..{result.RenderBoxes[i].Max}" : "-")} valve {(i < valveBoxes.Count ? $"{valveBoxes[i].Min}..{valveBoxes[i].Max}" : "-")}");
        var ours = new List<(Vector3 C, string From)>();
        foreach (var e in layer)
            {
                if (!RenderClusters.Contributes(e.Facts.Attributes))
                    continue;
                Vector3 P(int v) => new(e.Vertices[v * e.Stride], e.Vertices[(v * e.Stride) + 1], e.Vertices[(v * e.Stride) + 2]);
                for (var t = 0; t < e.TriangleCount; t++)
                    ours.Add((RenderClusters.Centroid(P(e.Indices[t * 3]), P(e.Indices[(t * 3) + 1]), P(e.Indices[(t * 3) + 2])), $"node {e.Facts.NodeId} {Path.GetFileName(e.Material)}"));
            }
        var valveSet = valveCentroids.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        var ourSet = ours.GroupBy(c => c.C).ToDictionary(g => g.Key, g => g.Count());
        output.WriteLine($"centroids: ours {ours.Count}, valve {valveCentroids.Length}");
        var missing = ours.Where(c => !valveSet.ContainsKey(c.C)).GroupBy(c => c.From).Select(g => $"{g.Key} x{g.Count()}").Take(30);
        output.WriteLine("ours not in valve's: " + string.Join("; ", missing));
        var extra = valveCentroids.Count(c => !ourSet.ContainsKey(c));
        output.WriteLine($"valve's not in ours: {extra}");
        var same = 0;
        while (same < Math.Min(ours.Count, valveCentroids.Length) && ours[same].C == valveCentroids[same])
            same++;
        output.WriteLine($"same order for the first {same}" + (same < ours.Count ? $"; ours then {ours[same].From}" : ""));
    }
}
