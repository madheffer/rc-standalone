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
/// <see cref="WorldNodeCompile"/> from the .vmap alone (entries, BuildNode,
/// vis from our own trace scene, the mesh lists) against a -world -vis
/// package's node: <c>WNODE=&lt;vmap&gt;|&lt;compiled vpk&gt;</c>. The objects'
/// and aggregates' model names, then each model's triangles (positions as
/// a multiset) against ours.
/// </summary>
public class WorldNodeCompileTests(ITestOutputHelper output)
{
    /// <summary>The compile of a .vmap from scratch, and the content it read.</summary>
    internal sealed record Compiled(WorldNodeCompile.Result Result, GameContent Content) : IDisposable
    {
        public void Dispose() => Content.Dispose();
    }

    internal static Compiled? Compile(string vmap, Action<string>? log = null)
    {
        if (CS2Fixtures.StockPak() is not { } pak || MapFixtures.GameSchema() is not { } schema)
            return null;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var doc = MapSource.Read(vmap);
        EntityLumpAuthor.Lighting? lighting = null;
        EntityLumpSet.Author(MapEntities.From(doc), schema, Path.GetFileNameWithoutExtension(vmap),
                             MapEntities.FixupEntityNames(doc), doc, MapFixtures.SmartPropLocators, lighting: l => lighting = l);
        var world = WorldNodeBuild.WorldEntries(doc, content, shaders, NodeEntriesFromVmap.RendersAsWorld(game));
        var props = WorldNodeBuild.PropEntries(doc, content, shaders);
        var descriptors = NodeOverlays.FromWorld(doc);
        var (built, overlays) = WorldNodeBuild.BuildNode(world, props, descriptors);
        var envMaps = WorldNodeBuild.EnvMaps(built, lighting!.Volumes);
        var lists = WorldNodeBuild.MergerInputs(built, overlays, descriptors, envMaps, content, shaders, kinds: [.. MeshLists.Order.Select(o => o.Kind)])
            .ToDictionary(kv => kv.Key, kv => kv.Value.Select(m => new WorldNodeCompile.Entry(m.Material, m.Stride, m.Vertices, m.Indices, m.Facts)).ToList());
        foreach (var (kind, l) in lists.Where(kv => kv.Value.Count > 0))
            log?.Invoke($"list {kind}: {l.Count} entries, {l.Sum(e => e.TriangleCount)} triangles");

        // Vis from our own trace scene, as the compile traces it.
        var packages = new[] { "csgo", "core" }.Select(d => { var pk = new Package(); pk.Read(Path.Combine(game, d, "pak01_dir.vpk")); return pk; }).ToList();
        var visFlags = new MaterialVisFlags.Source([Path.Combine(game, "csgo_addons", addon)], packages);
        bool RendersAsWorld(string cls) => schema.IsSolidClass(cls) && schema.HasFlag(cls, "render_as_world_but_physics_as_entity");
        var plain = DmxBinary.ReadFile(vmap);
        // The vis result is cached beside the temp folder, keyed by the .vmap's path and time.
        var cache = Path.Combine(Path.GetTempPath(), "s2c_viscache",
                                 $"{Path.GetFileNameWithoutExtension(vmap)}_{File.GetLastWriteTimeUtc(vmap).Ticks:x}.bin");
        List<(Vector3 Min, Vector3 Max)>[] flat;
        float[][] mutual;
        if (ReadVis(cache) is { } cached)
            (flat, mutual) = cached;
        else
        {
            var rte = TraceScene.Environment(TraceScene.Triangles(MapMeshes.Read(plain), content.Material, mat => visFlags[mat], RendersAsWorld));
            var (vxvs, blocks) = VisBuild.RunWithBlocks(rte, VisConfig.FromMap(MapEntities.From(plain), schema));
            (flat, mutual) = (blocks, VisOutput.MutualVisibility(vxvs));
            WriteVis(cache, flat, mutual);
        }
        log?.Invoke($"vis: {flat.Length} clusters");
        var result = WorldNodeCompile.Compile(lists, flat, mutual);
        log?.Invoke($"ours: {result.Objects.Count} objects, {result.Aggregates.Count} aggregates, {result.RenderBoxes.Count} render boxes");
        return new Compiled(result, content);
    }

    private static (List<(Vector3 Min, Vector3 Max)>[], float[][])? ReadVis(string path)
    {
        if (!File.Exists(path))
            return null;
        using var r = new BinaryReader(File.OpenRead(path));
        Vector3 V() => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var flat = new List<(Vector3, Vector3)>[r.ReadInt32()];
        for (var i = 0; i < flat.Length; i++)
        {
            flat[i] = [];
            for (var n = r.ReadInt32(); n > 0; n--)
                flat[i].Add((V(), V()));
        }
        var mutual = new float[r.ReadInt32()][];
        for (var i = 0; i < mutual.Length; i++)
        {
            mutual[i] = new float[r.ReadInt32()];
            for (var k = 0; k < mutual[i].Length; k++)
                mutual[i][k] = r.ReadSingle();
        }
        return (flat, mutual);
    }

    private static void WriteVis(string path, List<(Vector3 Min, Vector3 Max)>[] flat, float[][] mutual)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new BinaryWriter(File.Create(path));
        void V(Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        w.Write(flat.Length);
        foreach (var boxes in flat)
        {
            w.Write(boxes.Count);
            foreach (var (lo, hi) in boxes)
            {
                V(lo);
                V(hi);
            }
        }
        w.Write(mutual.Length);
        foreach (var row in mutual)
        {
            w.Write(row.Length);
            foreach (var x in row)
                w.Write(x);
        }
    }

    [Fact]
    public void AgainstPackage()
    {
        if (Environment.GetEnvironmentVariable("WNODE") is not { Length: > 0 } spec || spec.Split('|') is not [var vmap, var vpk])
            return;
        using var compiled = Compile(vmap, output.WriteLine);
        if (compiled == null)
            return;
        var result = compiled.Result;

        // Valve's node models: name -> triangles (positions, sorted per triangle).
        using var package = new Package();
        package.Read(vpk);
        var theirs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in package.Entries!.GetValueOrDefault("vmdl_c") ?? [])
        {
            if (!entry.DirectoryName.EndsWith("worldnodes", StringComparison.OrdinalIgnoreCase))
                continue;
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            var mesh = ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD().First().Mesh;
            var tris = new List<string>();
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
                        tris.Add(Key(pos[I(start + i)], pos[I(start + i + 1)], pos[I(start + i + 2)]));
                }
            theirs[Path.GetFileNameWithoutExtension(entry.FileName)] = tris;
        }
        var ours = result.Objects.Select(o => (o.Name, o.Meshes)).Concat(result.Aggregates.Select(a => (a.Name, a.Meshes)))
            .ToDictionary(x => x.Name, x => x.Meshes.SelectMany(Triangles).ToList(), StringComparer.OrdinalIgnoreCase);
        int sameName = 0, sameTris = 0;
        foreach (var name in theirs.Keys.Union(ours.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            var (t, o) = (theirs.GetValueOrDefault(name), ours.GetValueOrDefault(name));
            if (t != null && o != null)
            {
                sameName++;
                // Meshlet padding (a degenerate repeat) is Valve's alone.
                var tt = t.Where(k => k.Split('|').Distinct().Count() > 1).Order(StringComparer.Ordinal).ToList();
                var same = tt.SequenceEqual(o.Order(StringComparer.Ordinal));
                if (same)
                    sameTris++;
                output.WriteLine($"  {name}: Valve {tt.Count} triangles, ours {o.Count}{(same ? " same" : "")}");
            }
            else
                output.WriteLine($"  {name}: {(t != null ? $"only Valve's ({t.Count} triangles)" : $"only ours ({o!.Count} triangles)")}");
        }
        output.WriteLine($"models: Valve {theirs.Count}, ours {ours.Count}, names shared {sameName}, triangles equal {sameTris}");
    }

    private static string Key(Vector3 a, Vector3 b, Vector3 c)
        => string.Join("|", new[] { a, b, c }.Select(v => $"{v.X:R},{v.Y:R},{v.Z:R}").Order(StringComparer.Ordinal));

    private static IEnumerable<string> Triangles(WorldNodeCompile.Entry e)
    {
        var at = 0;
        foreach (var s in e.Facts.Streams)
        {
            if (string.Equals(s.Name, "position", StringComparison.OrdinalIgnoreCase))
                break;
            at += s.Count;
        }
        Vector3 P(int v) => new(e.Vertices[(v * e.Stride) + at], e.Vertices[(v * e.Stride) + at + 1], e.Vertices[(v * e.Stride) + at + 2]);
        for (var t = 0; t < e.TriangleCount; t++)
            yield return Key(P(e.Indices[t * 3]), P(e.Indices[(t * 3) + 1]), P(e.Indices[(t * 3) + 2]));
    }
}
