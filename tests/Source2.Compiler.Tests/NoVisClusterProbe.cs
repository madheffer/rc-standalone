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
        var result = WorldNodeCompile.Compile(lists, null, null, nodeBounds: (min, max));
        foreach (var o in result.Objects)
            output.WriteLine($"object {o.Name}: " + string.Join(" ", o.Meshes.Select(m => $"{Path.GetFileName(m.Material)}/v{m.Vertices.Length / m.Stride}/t{m.TriangleCount}/s{m.Stride}")));
        foreach (var a in result.Aggregates)
            output.WriteLine($"aggregate {a.Name}: " + string.Join(" ", a.Meshes.Select(m => $"v{m.Vertices.Length / m.Stride}/t{m.TriangleCount}")));
    }
}
